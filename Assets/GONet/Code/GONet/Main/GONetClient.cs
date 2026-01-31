/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 * 
 *
 * Authorized use is explicitly limited to the following:	
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on original source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 * -The ability to modify source code for local use only
 * -The ability to distribute products based on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on modified source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using NetcodeIO.NET;
using System;
using System.Collections.Generic;
using System.Net;
using GONet.Transport;
using GONetChannelId = System.Byte;

namespace GONet
{
    [Flags]
    public enum ClientTypeFlags : byte
    {
        None = 0,

        /// <summary>
        /// Standard client connecting to remote dedicated server.
        /// This is the most common client type for production games.
        /// </summary>
        Player_Standard = 1 << 0,

        /// <summary>
        /// Listen server (client-host): This client is also running the server in the same process.
        /// Industry-standard term for player-hosted multiplayer games.
        ///
        /// Use cases:
        /// - Player-hosted games (one player hosts, others connect)
        /// - Peer-to-peer topologies
        /// - Local testing (start server + client in same Unity instance)
        ///
        /// When this flag is set, GONet optimizes the host player's connection using:
        /// - GONetConnection_ClientHostLoopback (bypasses network stack)
        /// - Direct method calls instead of serialization
        /// - Zero network latency for host player
        ///
        /// Legacy name: ServerHost (deprecated, use ListenServer instead)
        /// </summary>
        ListenServer = 1 << 1,

        /// <summary>
        /// DEPRECATED: Use ListenServer instead (industry-standard terminology).
        /// This alias exists for backward compatibility only.
        /// </summary>
        [System.Obsolete("Use ListenServer instead (industry-standard term)", false)]
        ServerHost = ListenServer,

        /* this likely does not belong here,...but a thought nonetheless:
        Replay_Recorder =       1 << 2,
        */
    }

    public class GONetClient
    {
        private ClientTypeFlags _clientTypeFlags = ClientTypeFlags.Player_Standard;
        internal ClientTypeFlags ClientTypeFlags
        {
            get => _clientTypeFlags;

            set
            {
                var previous = _clientTypeFlags;
                _clientTypeFlags = value;
                if (value != previous)
                {
                    GONetMain.EventBus.Publish(new ClientTypeFlagsChangedEvent(GONetMain.Time.ElapsedTicks, GONetMain.MyAuthorityId, previous, value));
                }
            }
        }

        public bool IsConnectedToServer => ConnectionState == ClientState.Connected;

        /// <summary>
        /// True if this client is a standby mesh client (hot standby connection).
        /// Standby clients only process standby protocol messages (hello, keepalive, promote).
        /// They do NOT process regular game traffic until activated during failover.
        /// </summary>
        public bool IsStandbyMeshClient { get; internal set; } = false;

        /// <summary>
        /// Current state of this client connection to server.
        /// Subscribe to <see cref="ClientStateChangedEvent"/> via <see cref="GONetMain.EventBus"/>'s <see cref="GONetEventBus.Subscribe{T}(GONetEventBus.HandleEventDelegate{T}, GONetEventBus.EventFilterDelegate{T})"/>
        /// if you want notification each time this changes.
        /// </summary>
        public ClientState ConnectionState { get; private set; } = ClientState.Disconnected;

        /// <summary>
        /// Internal method to set ConnectionState during failover promotion.
        /// Used when a client becomes a host and needs to establish loopback connection.
        /// </summary>
        internal void SetConnectionStateForFailover(ClientState state)
        {
            var previous = ConnectionState;
            ConnectionState = state;
            GONetLog.Info($"[Failover] GONetClient.ConnectionState changed: {previous} -> {state}");
        }

        internal readonly Queue<GONetMain.NetworkData> incomingNetworkData_mustProcessAfterClientInitialized = new Queue<GONetMain.NetworkData>(100);

        /// <summary>
        /// Queue for messages that failed to process due to missing GONetId assignments.
        /// These will be retried after scene-defined object GONetIds are synchronized.
        /// </summary>
        internal readonly Queue<GONetMain.NetworkData> incomingNetworkData_waitingForGONetIds = new Queue<GONetMain.NetworkData>(100);

        /// <summary>
        /// Maximum size for the GONetId waiting queue to prevent unbounded growth.
        /// </summary>
        internal const int MAX_GONETID_QUEUE_SIZE = 1000;

        bool isInitializedWithServer;

        /// <summary>
        /// Indicates whether scene-defined object GONetIds have been assigned.
        /// Messages requiring GONetIds will be queued until this is true.
        /// </summary>
        internal bool areSceneDefinedObjectIdsReady = false;

        /// <summary>
        /// Tracks initialization message channels received (for acknowledgment to server).
        /// Key: channel ID, Value: count of messages received on that channel
        /// See: .claude/STEAMWORKS_INIT_MESSAGE_DELIVERY_SOLUTION.md
        /// </summary>
        internal readonly Dictionary<GONetChannelId, int> receivedInitMessageChannels = new Dictionary<GONetChannelId, int>();

        /// <summary>
        /// Flag to stop tracking init messages after acknowledgment is sent.
        /// Set to true after Client_SendInitializationAcknowledgment() completes.
        /// Prevents continual tracking of TimeSync_Unreliable messages (channel 0) throughout session.
        /// </summary>
        internal bool hasAcknowledgedInitMessages = false;

        /// <summary>
        /// Init tracking marker received on ClientInitialization_EventSingles_Reliable.
        /// Used to delay init acknowledgment until init event singles are complete.
        /// </summary>
        internal bool hasReceivedInitTrackingMarker_EventSingles = false;

        /// <summary>
        /// Init tracking marker received on ClientInitialization_CustomSerialization_Reliable.
        /// Used to delay init acknowledgment until init custom serialization is complete.
        /// </summary>
        internal bool hasReceivedInitTrackingMarker_CustomSerialization = false;

        public bool IsInitializedWithServer
        {
            get => IsConnectedToServer && isInitializedWithServer;
            internal set
            {
                bool before = IsInitializedWithServer;

                GONetLog.Info($"[INIT] GONetClient.IsInitializedWithServer setter called - value: {value}, before: {before}, IsConnectedToServer: {IsConnectedToServer}, ConnectionState: {ConnectionState}");

                isInitializedWithServer = value;

                bool after = IsInitializedWithServer;
                GONetLog.Info($"[INIT] After setting isInitializedWithServer field - IsInitializedWithServer property: {after}, will fire event: {!before && after}");

                if (!before && IsInitializedWithServer)
                {
                    GONetLog.Info($"[INIT] Firing InitializedWithServer event");
                    InitializedWithServer?.Invoke(this);
                }
            }
        }

        public delegate void ClientDelegate(GONetClient client);
        public event ClientDelegate InitializedWithServer;

        /// <summary>
        /// This *will* be called from main Unity thread.
        /// Also, consider subscribing to <see cref="ClientStateChangedEvent"/> to be informed of changes 
        /// that go beyond just connect/disconnect (e.g., all other values of <see cref="ClientState"/>).
        /// </summary>
        public event ClientDelegate ClientConnected;

        /// <summary>
        /// This *will* be called from main Unity thread.
        /// Also, consider subscribing to <see cref="ClientStateChangedEvent"/> to be informed of changes 
        /// that go beyond just connect/disconnect (e.g., all other values of <see cref="ClientState"/>).
        /// </summary>
        public event ClientDelegate ClientDisconnected;

        /// <summary>
        /// This auto-assigned UID is used to correlate this client's connection to the server both on client side and server side.
        /// See <see cref="ClientStateChangedEvent.InitiatingClientConnectionUID"/> and <see cref="RemoteClientStateChangedEvent.InitiatingClientConnectionUID"/>.
        /// IMPORTANT: This value changes inside the call to <see cref="ConnectToServer(string, int, int)"/> and <see cref="ConnectToServer(string, int, int, int)"/>, which means you should always access this property instead of storing the value off elsewhere.
        /// </summary>
        public ulong InitiatingClientConnectionUID => connectionToServer.InitiatingClientConnectionUID;

        internal readonly GONetConnection_ClientToServer connectionToServer;

        /// <summary>
        /// Exposes the connection to the server for reliability layer operations.
        /// Used during hot standby failover to reset sequence numbers.
        /// </summary>
        public GONetConnection_ClientToServer Connection => connectionToServer;

        private readonly Client client;

        // NEW: Transport abstraction (Phase 5 - Dual-path architecture)
        private IGONetTransport transport_new;
        private bool useNewTransportPath = false;

        /// <summary>
        /// Current transport implementation (null if using legacy NetcodeIO path).
        /// </summary>
        public IGONetTransport Transport => transport_new;

        /// <summary>
        /// Create GONetClient instance.
        /// </summary>
        /// <param name="transport">Optional transport implementation (defaults to NetcodeIO if null)</param>
        /// <param name="isStandbyMeshClient">True if this is a standby mesh client. Standby mesh clients have their own
        /// separate transport and MUST subscribe to receive messages (unlike HOST's main client which uses loopback).</param>
        public GONetClient(IGONetTransport transport = null, bool isStandbyMeshClient = false)
        {
            IsStandbyMeshClient = isStandbyMeshClient; // Set early so it's available during construction
            int maxQueueSize = GONetGlobal.Instance != null ? GONetGlobal.Instance.maxReliableMessageQueueSize : 2000;

            if (transport != null)
            {
                // NEW PATH: Use provided transport
                transport_new = transport;
                useNewTransportPath = true;

                // Create connection using new constructor
                // Pass isStandbyMeshClient so the connection knows whether to subscribe to transport messages
                connectionToServer = new GONetConnection_ClientToServer(transport_new, maxQueueSize, isStandbyMeshClient);

                // Subscribe to transport events
                transport_new.OnClientConnected += OnClientConnected_new;
                transport_new.OnClientDisconnected += OnClientDisconnected_new;
                transport_new.OnClientStateChanged += OnClientStateChanged_new;

                GONetLog.Info($"GONetClient initialized with transport: {transport.GetType().Name}");
            }
            else
            {
                // OLD PATH: Use NetcodeIO directly (backward compatible)
                this.client = new();
                connectionToServer = new GONetConnection_ClientToServer(client, maxQueueSize);

                client.OnStateChanged += OnStateChanged_BubbleEventUp;
                client.TickBeginning += Client_TickBeginning_PossibleSeparateThread;
            }

            // Common subscription (both paths)
            // Since the OnStateChanged_BubbleEventUp can occur on non-main Unity thread, this
            // provides a way to process (i.e., invoke public event) on the main thread since
            // GONet event bus subscriptions are processed on the main thread.
            GONetMain.EventBus.Subscribe<ClientStateChangedEvent>(OnStateChanged_BubbleEventUp_MainThread);
        }

        public void AddP2pEndPoint(IPEndPoint p2pEndPoint)
        {
            client.AddP2pEndPoint(p2pEndPoint);

            // TODO consolidate this!!
            GONetGlobal.ServerP2pEndPoint = p2pEndPoint;
        }

        private void Client_TickBeginning_PossibleSeparateThread()
        {
            connectionToServer.ProcessSendBuffer_IfAppropriate();
        }

        /// <summary>
        /// NOTE: Consider subscribing to <see cref="ClientStateChangedEvent"/> (via <see cref="GONetEventBus.Subscribe{T}(GONetEventBus.HandleEventDelegate{T}, GONetEventBus.EventFilterDelegate{T})"/>) prior to calling this so you can react to any changes to the state.
        ///       If you do subscribe, ensure the subscription filter/predicate compares its <see cref="ClientStateChangedEvent.InitiatingClientConnectionUID"/> to <see cref="InitiatingClientConnectionUID"/>.
        ///       See <see cref="GONetSampleSpawner.OnClientStateChanged_LogIt(GONetEventEnvelope{ClientStateChangedEvent})"/> for example.
        /// </summary>
        /// <param name="serverIP"></param>
        /// <param name="serverPort"></param>
        /// <param name="timeoutSeconds">
        /// This value serves two purposes:
        /// 1) Prior to connection being established, this represents how many seconds the client will attempt to connect to the server before giving up and considering the connected timed out (i.e., <see cref="ClientState.ConnectionRequestTimedOut"/>).  NOTE: During this time period, the connection will be attempted 10 times per second.
        /// 2) After connection is established, this represents how many seconds have to transpire with no communication for this connection to be considered timed out...then will be auto-disconnected by the server.
        /// </param>
        public void ConnectToServer(string serverIP, int serverPort, int timeoutSeconds)
        {
            // Auto-enable process ID prefix for log files when connecting to local server
            // This prevents log file corruption when server and client run on the same machine
            if (GONet.Utils.NetworkUtils.IsLoopbackAddress(serverIP) && !GONetLog.UseProcessIdPrefix)
            {
                GONetLog.UseProcessIdPrefix = true;
                GONetLog.Info($"[GONetClient] Auto-enabled UseProcessIdPrefix for local server connection ({serverIP})");
            }

            if (useNewTransportPath && transport_new != null)
            {
                // NEW PATH: Connect via IGONetTransport
                transport_new.ConnectClient(serverIP, serverPort, timeoutSeconds);
            }
            else
            {
                // OLD PATH: Connect via NetcodeIO
                connectionToServer.Connect(serverIP, serverPort, timeoutSeconds);
            }
        }

        public void SendBytesToServer(byte[] bytes, int bytesUsedCount, GONetChannelId channelId)
        {
            // SLOT RESERVATION (December 2025): Use channel-based priority
            var priority = GONetChannel.GetMessagePriority(channelId);
            connectionToServer.SendMessageOverChannel(bytes, bytesUsedCount, channelId, priority);
        }

        /// <summary>
        /// Call this every frame (from the main Unity thread!) in order to process all network traffic in a timely manner.
        /// </summary>
        public void Update()
        {
            // Update transport FIRST (if using new path) - this processes callbacks that may change ConnectionState
            if (useNewTransportPath && transport_new != null)
            {
                transport_new.Update();
            }

            // Now check connection state (may have been updated by transport callbacks above)
            if (ConnectionState == ClientState.Connected)
            {
                connectionToServer.Update();

                // CRITICAL: Flush send buffer every frame (same as old path's background tick callback)
                // Without this, messages queue indefinitely causing choppy physics sync
                if (useNewTransportPath && transport_new != null)
                {
                    connectionToServer.ProcessSendBuffer_IfAppropriate();
                }
            }
            else if (useNewTransportPath && GONetConfig.LogClientConnectionDiagnostics)
            {
                // DIAGNOSTIC (January 2026): Log when Update is skipped due to ConnectionState
                // This helps debug cases where reliable messages are queued but never transmitted
                GONetLog.Debug($"[CLIENT-UPDATE-SKIP] ConnectionState={ConnectionState}, ReliableEndpoint.Update() NOT called - queued messages won't transmit!");
            }
        }

        public void Disconnect()
        {
            if (useNewTransportPath && transport_new != null)
            {
                // NEW PATH: Disconnect via IGONetTransport
                transport_new.DisconnectClient();
            }
            else
            {
                // OLD PATH: Disconnect via GONetConnection
                connectionToServer.Disconnect();
            }
        }

        /// <summary>
        /// Since the <see cref="client"/> is private, the event it publishes for state change is not visible to GONet users.
        /// So, this bubbles it up and fires a GONet event for them (i.e., <see cref="ClientStateChangedEvent"/>).
        /// </summary>
        private void OnStateChanged_BubbleEventUp(ClientState state)
        {
            var previous = ConnectionState;
            ConnectionState = state;

            const string CLIENT = "Client state changed to: ";
            const string AUTH = ".  My client guid: ";
            GONetLog.Debug(string.Concat(CLIENT, Enum.GetName(typeof(ClientState), state), AUTH, connectionToServer.InitiatingClientConnectionUID));

            // NOTE: The following will cause OnStateChanged_BubbleEventUp_MainThread to be called:
            if (previous != state)
            {
                GONetMain.EventBus.PublishASAP(new ClientStateChangedEvent(GONetMain.Time.ElapsedTicks, connectionToServer.InitiatingClientConnectionUID, previous, state));
            }
        }

        private void OnStateChanged_BubbleEventUp_MainThread(GONetEventEnvelope<ClientStateChangedEvent> eventEnvelope)
        {
            // CRITICAL FIX: Only fire events for THIS client's state changes, not all clients!
            // Without this check, when any GONetClient disconnects, ALL clients would fire their
            // disconnect events, causing cascade disconnects in hot standby mesh connections.
            if (eventEnvelope.Event.InitiatingClientConnectionUID != connectionToServer.InitiatingClientConnectionUID)
            {
                return; // Event is for a different client, ignore it
            }

            switch (eventEnvelope.Event.StateNow)
            {
                case ClientState.Connected:
                    ClientConnected?.Invoke(this);
                    break;
                case ClientState.Disconnected:
                    ClientDisconnected?.Invoke(this);
                    break;
            }
        }

        #region NEW TRANSPORT PATH - Event Handlers

        /// <summary>
        /// NEW PATH: Client connected handler
        /// </summary>
        private void OnClientConnected_new()
        {
            const string CON = "Client connected (new transport path)";
            GONetLog.Debug(CON);

            // Update connection state
            var previous = ConnectionState;
            ConnectionState = ClientState.Connected;

            // Publish state change event
            if (previous != ClientState.Connected)
            {
                GONetMain.EventBus.PublishASAP(new ClientStateChangedEvent(
                    GONetMain.Time.ElapsedTicks,
                    connectionToServer.InitiatingClientConnectionUID,
                    previous,
                    ClientState.Connected));
            }
        }

        /// <summary>
        /// NEW PATH: Client disconnected handler
        /// </summary>
        private void OnClientDisconnected_new(GONetTransportDisconnectReason reason)
        {
            const string DIS = "Client disconnected (new transport path)";
            GONetLog.Debug($"{DIS} - Reason: {reason}");

            // Update connection state
            var previous = ConnectionState;
            ConnectionState = ClientState.Disconnected;

            // Publish state change event
            if (previous != ClientState.Disconnected)
            {
                GONetMain.EventBus.PublishASAP(new ClientStateChangedEvent(
                    GONetMain.Time.ElapsedTicks,
                    connectionToServer.InitiatingClientConnectionUID,
                    previous,
                    ClientState.Disconnected));
            }
        }

        /// <summary>
        /// NEW PATH: Client state changed handler
        /// </summary>
        private void OnClientStateChanged_new(GONetTransportClientState transportState)
        {
            // Map transport state to NetcodeIO ClientState
            ClientState newState = MapTransportClientState(transportState);

            var previous = ConnectionState;
            ConnectionState = newState;

            const string CLIENT = "Client state changed to: ";
            const string AUTH = ".  My client UID: ";
            GONetLog.Debug(string.Concat(CLIENT, Enum.GetName(typeof(ClientState), newState), AUTH, connectionToServer.InitiatingClientConnectionUID));

            // Publish state change event
            if (previous != newState)
            {
                GONetMain.EventBus.PublishASAP(new ClientStateChangedEvent(
                    GONetMain.Time.ElapsedTicks,
                    connectionToServer.InitiatingClientConnectionUID,
                    previous,
                    newState));
            }
        }

        /// <summary>
        /// Map IGONetTransport state to NetcodeIO ClientState
        /// </summary>
        private ClientState MapTransportClientState(GONetTransportClientState transportState)
        {
            switch (transportState)
            {
                case GONetTransportClientState.Disconnected:
                    return ClientState.Disconnected;
                case GONetTransportClientState.Connecting:
                    return ClientState.SendingConnectionRequest;
                case GONetTransportClientState.Connected:
                    return ClientState.Connected;
                case GONetTransportClientState.TimedOut:
                    return ClientState.ConnectionTimedOut;
                case GONetTransportClientState.Refused:
                    return ClientState.ConnectionDenied;
                case GONetTransportClientState.Error:
                default:
                    return ClientState.Disconnected;
            }
        }

        #endregion
    }

    public class GONetRemoteClient
    {
        public RemoteClient RemoteClient { get; private set; }

        public GONetConnection_ServerToClient ConnectionToClient { get; private set; }

        bool isInitializedWithServer;
        public bool IsInitializedWithServer
        {
            get => isInitializedWithServer;
            internal set
            {
                bool before = isInitializedWithServer;
                isInitializedWithServer = value;
                if (before != value && value)
                {
                    InitializedWithServer?.Invoke(this);
                }
            }
        }

        public delegate void InitializedWithServerDelegate(GONetRemoteClient remoteClient);
        public event InitializedWithServerDelegate InitializedWithServer;

        /// <summary>
        /// True while init message tracking is active for this client.
        /// When false, init-channel sends are treated as normal traffic (no tracking, no sentinels).
        /// </summary>
        public bool IsInitMessageTrackingActive { get; internal set; } = true;

        /// <summary>
        /// Tracks whether this client is currently loading a scene.
        /// Set to true when server publishes SceneLoadEvent, set to false when client sends SceneLoadCompleteEvent.
        /// Used to suppress sync messages during scene loading (client can't process them without GONetIds).
        /// </summary>
        public bool IsCurrentlyLoadingScene { get; internal set; } = false;

        /// <summary>
        /// Name of the scene this client is currently loading (null if not loading).
        /// </summary>
        public string CurrentlyLoadingSceneName { get; internal set; } = null;

        /// <summary>
        /// Timestamp (Time.ElapsedSeconds) when the client started loading the current scene.
        /// Used for server-side timeout detection when SceneLoadCompleteEvent is lost.
        /// </summary>
        public double LoadingStartedTime { get; internal set; } = 0;

        /// <summary>
        /// Whether the server has already sent a fallback GONetId sync for this client due to timeout.
        /// Prevents repeated fallback sends while client is still marked as loading.
        /// </summary>
        public bool HasSentLoadingTimeoutFallback { get; internal set; } = false;

        public GONetRemoteClient(RemoteClient remoteClient, GONetConnection_ServerToClient connectionToClient)
        {
            RemoteClient = remoteClient;
            ConnectionToClient = connectionToClient;
        }
    }
}
