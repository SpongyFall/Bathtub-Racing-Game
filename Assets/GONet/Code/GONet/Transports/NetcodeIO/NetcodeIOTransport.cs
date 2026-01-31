/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 *
 * Authorized use is explicitly limited to the following:
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on original sources in binary form only (compiled code)
 * -The ability to modify source code for local use only
 * -The ability to distribute products based on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on modified sources in binary form only (compiled code)
 */

using NetcodeIO.NET;
using System;
using System.Collections.Generic;
using GONet.Utils;

namespace GONet.Transport
{
    /// <summary>
    /// Transport implementation using NetcodeIO.NET (UDP + encryption + connection management).
    /// This is the default transport for GONet, wrapping the existing NetcodeIO stack.
    ///
    /// <para>
    /// ARCHITECTURE:
    /// - NetcodeIO.NET: Connection management, encryption, UDP sockets
    /// - ReliableNetcode: Reliability layer (NOT exposed - handled by ReliabilityLayerAdapter)
    /// - This adapter bridges NetcodeIO to IGONetTransport interface
    /// </para>
    ///
    /// <para>
    /// CAPABILITIES:
    /// - IPv6 dual-stack sockets
    /// - Encryption (token-based authentication)
    /// - Authentication (connect token validation)
    /// - Does NOT provide: Reliability, Compression, Fragmentation (ReliableNetcode layer provides these)
    /// </para>
    /// </summary>
    public class NetcodeIOTransport : IGONetTransport
    {
        #region Fields

        // NetcodeIO instances
        private Server server;
        private Client client;

        // Server connection tracking
        private readonly Dictionary<RemoteClient, NetcodeIOConnection> serverConnections = new Dictionary<RemoteClient, NetcodeIOConnection>();

        // RACE CONDITION FIX (January 2026): Buffer messages that arrive before OnClientConnected callback
        // NetcodeIO can fire OnClientMessageReceived before OnClientConnected, causing messages to be dropped.
        // This buffer holds messages from unknown senders until their connected callback fires.
        private readonly Dictionary<RemoteClient, List<(byte[] payload, int size, long timestamp)>> pendingMessages =
            new Dictionary<RemoteClient, List<(byte[] payload, int size, long timestamp)>>();
        private readonly object pendingMessagesLock = new object();

        // Client connection
        private NetcodeIOConnection clientConnection;

        // Configuration
        private GONetTransportConfig config;
        private byte[] privateKey;
        private ulong protocolID;

        // State
        private bool isDisposed = false;
        private int serverPort = -1; // Cached port for lock file management

        #endregion

        #region Events

        public event Func<IGONetTransportConnectionRequest, bool> OnServerConnectionRequested;
        public event Action<IGONetTransportConnection> OnServerClientConnected;
        public event Action<IGONetTransportConnection, GONetTransportDisconnectReason> OnServerClientDisconnected;
        public event Action OnClientConnected;
        public event Action<GONetTransportDisconnectReason> OnClientDisconnected;
        public event Action<GONetTransportClientState> OnClientStateChanged;
        public event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte> OnMessageReceived;

        /// <summary>
        /// Invoked when message received with transport-level timestamp.
        /// For NetcodeIO, we don't have accurate transport-level timestamps like Steamworks,
        /// so we pass the current GONet time (processing time) as the receive timestamp.
        /// This event exists for interface compatibility with transports that do have accurate timestamps.
        /// </summary>
        public event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte, long> OnMessageReceivedWithTimestamp;

        #endregion

        #region Lifecycle

        public void Initialize(GONetTransportConfig config)
        {
            this.config = config ?? GONetTransportConfig.CreateDefault();
            this.privateKey = config.PrivateKey ?? GenerateRandomPrivateKey();
            this.protocolID = config.ProtocolID;
        }

        public void Shutdown()
        {
            StopServer();
            DisconnectClient();
        }

        public void Dispose()
        {
            if (!isDisposed)
            {
                Shutdown();
                isDisposed = true;
            }
        }

        #endregion

        #region Server-Side Operations

        public void StartServer(int port, int maxConnections)
        {
            if (server != null)
            {
                GONetLog.Warning("NetcodeIOTransport: Server already started");
                return;
            }

            try
            {
                server = new Server(
                    maxConnections,
                    port,
                    protocolID,
                    privateKey
                );

                // Subscribe to NetcodeIO events
                server.OnClientConnected += OnNetcodeClientConnected;
                server.OnClientDisconnected += OnNetcodeClientDisconnected;
                server.OnClientMessageReceived += OnNetcodeServerMessageReceived;

                // Start server with configured tickrate
                server.Start(config.Tickrate);

                // Cache port for lock file management
                serverPort = port;

                // Create lock file for auto-detection
                GONet.Utils.NetworkUtils.CreateServerLockFile(port);

                GONetLog.Info($"NetcodeIOTransport: Server started on port {port}, max clients: {maxConnections}");
            }
            catch (Exception ex)
            {
                GONetLog.Error($"NetcodeIOTransport: Failed to start server - {ex.Message}");
                throw;
            }
        }

        public void StopServer()
        {
            if (server != null)
            {
                server.Stop();
                server = null;
                serverConnections.Clear();

                // Remove lock file
                if (serverPort > 0)
                {
                    GONet.Utils.NetworkUtils.RemoveServerLockFile(serverPort);
                    serverPort = -1;
                }

                GONetLog.Info("NetcodeIOTransport: Server stopped");
            }
        }

        public void DisconnectConnection(IGONetTransportConnection connection, GONetTransportDisconnectReason reason)
        {
            if (connection is NetcodeIOConnection netcodeConnection)
            {
                // NetcodeIO doesn't provide per-client disconnect, so we remove from tracking
                // The client will timeout and disconnect naturally
                if (serverConnections.ContainsKey(netcodeConnection.RemoteClient))
                {
                    serverConnections.Remove(netcodeConnection.RemoteClient);
                    OnServerClientDisconnected?.Invoke(connection, reason);
                }
            }
        }

        private void OnNetcodeClientConnected(RemoteClient remoteClient)
        {
            // Create connection request for approval
            var request = new NetcodeIOConnectionRequest(remoteClient);

            // Check if server wants to approve connection
            bool approved = true;
            if (OnServerConnectionRequested != null)
            {
                approved = OnServerConnectionRequested.Invoke(request);
            }

            if (approved && !request.WasRejected)
            {
                // Connection approved - create wrapper and track
                NetcodeIOConnection connection = new NetcodeIOConnection(remoteClient);
                serverConnections[remoteClient] = connection;

                OnServerClientConnected?.Invoke(connection);

                GONetLog.Info($"NetcodeIOTransport: Client connected - ClientID: {remoteClient.ClientID}");

                // RACE CONDITION FIX (January 2026): Process any buffered messages that arrived before this callback
                ProcessPendingMessages(remoteClient, connection);
            }
            else
            {
                // Connection rejected - disconnect client
                // NetcodeIO doesn't have explicit reject, so just don't track the connection
                // Client will timeout
                GONetLog.Info($"NetcodeIOTransport: Client connection rejected - ClientID: {remoteClient.ClientID}");
            }
        }

        private void OnNetcodeClientDisconnected(RemoteClient remoteClient)
        {
            if (serverConnections.TryGetValue(remoteClient, out NetcodeIOConnection connection))
            {
                serverConnections.Remove(remoteClient);
                OnServerClientDisconnected?.Invoke(connection, GONetTransportDisconnectReason.UserInitiated);

                GONetLog.Info($"NetcodeIOTransport: Client disconnected - ClientID: {remoteClient.ClientID}");
            }

            // RACE CONDITION FIX (January 2026): Clean up any buffered messages for disconnected client
            lock (pendingMessagesLock)
            {
                if (pendingMessages.Remove(remoteClient))
                {
                    GONetLog.Debug($"[NetcodeIO-BUFFER] Cleaned up buffered messages for disconnected ClientID {remoteClient.ClientID}");
                }
            }
        }

        private void OnNetcodeServerMessageReceived(RemoteClient sender, byte[] payload, int payloadSize)
        {
            if (serverConnections.TryGetValue(sender, out NetcodeIOConnection connection))
            {
                // NetcodeIO doesn't provide transport-level timestamps like Steamworks,
                // so we use processing time as the receive timestamp for interface compatibility.
                // CRITICAL (December 2025): Must use ELAPSED time domain, NOT absolute UTC ticks!
                // GONet.Network.cs expects timestamps in SecretaryOfTemporalAffairs elapsed domain.
                long processingTimeTicks = GONetMain.SecretaryOfTemporalAffairs.GetRawElapsedTicksStatic();

                // CRITICAL ORDER (December 2025): Fire OnMessageReceivedWithTimestamp FIRST so subscribers
                // can capture the accurate transport-level timestamp BEFORE OnMessageReceived processes the message.
                // GONetConnection stores _pendingTransportReceiveTicks from this event, then uses it in OnMessageReceived.
                OnMessageReceivedWithTimestamp?.Invoke(payload, payloadSize, GONetTransportQoS.Reliable, connection, 0, processingTimeTicks);

                // Then fire standard event (for backward compatibility with code that doesn't need timestamps)
                OnMessageReceived?.Invoke(payload, payloadSize, GONetTransportQoS.Reliable, connection, 0);
            }
            else
            {
                // RACE CONDITION FIX (January 2026): Buffer messages that arrive before OnClientConnected
                // NetcodeIO can fire OnClientMessageReceived before OnClientConnected, causing a race condition
                // where messages are dropped because the sender isn't in serverConnections yet.
                // Solution: Buffer these messages and process them when OnClientConnected fires.
                if (sender != null && sender.Connected)
                {
                    // Capture timestamp now (before buffering) to preserve accurate timing
                    long timestamp = GONetMain.SecretaryOfTemporalAffairs.GetRawElapsedTicksStatic();

                    // Make a copy of the payload since the buffer may be reused
                    byte[] payloadCopy = new byte[payloadSize];
                    Buffer.BlockCopy(payload, 0, payloadCopy, 0, payloadSize);

                    lock (pendingMessagesLock)
                    {
                        if (!pendingMessages.TryGetValue(sender, out var messageList))
                        {
                            messageList = new List<(byte[] payload, int size, long timestamp)>();
                            pendingMessages[sender] = messageList;
                        }
                        messageList.Add((payloadCopy, payloadSize, timestamp));
                    }

                    GONetLog.Info($"[NetcodeIO-BUFFER] Buffered {payloadSize} bytes from ClientID {sender.ClientID} - awaiting OnClientConnected callback");
                }
                else
                {
                    // Sender is null or not connected - this is unexpected, log and drop
                    GONetLog.Warning($"[NetcodeIO-RECV-DROP] Server received {payloadSize} bytes from invalid sender - ClientID: {sender?.ClientID ?? 0}, " +
                        $"Connected: {sender?.Connected ?? false}, serverConnections.Count: {serverConnections.Count}. " +
                        $"Message DROPPED - sender is null or not connected!");
                }
            }
        }

        /// <summary>
        /// RACE CONDITION FIX (January 2026): Process messages that were buffered before OnClientConnected fired.
        /// </summary>
        private void ProcessPendingMessages(RemoteClient remoteClient, NetcodeIOConnection connection)
        {
            List<(byte[] payload, int size, long timestamp)> messagesToProcess = null;

            lock (pendingMessagesLock)
            {
                if (pendingMessages.TryGetValue(remoteClient, out var messageList))
                {
                    messagesToProcess = messageList;
                    pendingMessages.Remove(remoteClient);
                }
            }

            if (messagesToProcess != null && messagesToProcess.Count > 0)
            {
                GONetLog.Info($"[NetcodeIO-BUFFER] Processing {messagesToProcess.Count} buffered messages for ClientID {remoteClient.ClientID}");

                foreach (var (payload, size, timestamp) in messagesToProcess)
                {
                    // Fire both events with the original captured timestamp
                    OnMessageReceivedWithTimestamp?.Invoke(payload, size, GONetTransportQoS.Reliable, connection, 0, timestamp);
                    OnMessageReceived?.Invoke(payload, size, GONetTransportQoS.Reliable, connection, 0);
                }
            }
        }

        #endregion

        #region Client-Side Operations

        public void ConnectClient(string address, int port, int timeoutSeconds, byte[] authData = null)
        {
            if (client != null)
            {
                GONetLog.Warning("NetcodeIOTransport: Client already exists, disconnecting first");
                DisconnectClient();
            }

            try
            {
                client = new Client();
                client.OnStateChanged += OnNetcodeClientStateChanged;
                client.OnMessageReceived += OnNetcodeClientMessageReceived;

                // Generate connect token if not provided
                if (authData == null)
                {
                    // Generate unique clientID using GUID (same approach as legacy GONetConnections)
                    // Each process/client gets a unique random ID to prevent collisions
                    ulong clientID = (ulong)Utils.GUID.Generate().AsInt64();

                    TokenFactory factory = new TokenFactory(protocolID, privateKey);

                    // Build endpoint list (supports IPv4/IPv6 dual-stack)
                    List<System.Net.IPEndPoint> endpoints = GONet.Utils.NetworkUtils.BuildDualStackEndpointList(address, port);

                    authData = factory.GenerateConnectToken(
                        endpoints.ToArray(),
                        120,  // expirySeconds - CONNECTION_TOKEN_TIMEOUT_SECONDS
                        timeoutSeconds,  // serverTimeout
                        0UL,  // sequence - unused for client, set to 0
                        clientID,  // clientID - unique per connection (GUID-based to prevent collisions across processes)
                        new byte[256]  // userData
                    );
                }

                client.Connect(authData);

                GONetLog.Info($"NetcodeIOTransport: Connecting to {address}:{port}...");
            }
            catch (Exception ex)
            {
                GONetLog.Error($"NetcodeIOTransport: Failed to connect - {ex.Message}");
                throw;
            }
        }

        public void DisconnectClient()
        {
            if (client != null)
            {
                client.Disconnect();
                client = null;
                clientConnection = null;

                GONetLog.Info("NetcodeIOTransport: Client disconnected");
            }
        }

        private void OnNetcodeClientStateChanged(ClientState newState)
        {
            GONetTransportClientState transportState = MapNetcodeClientState(newState);
            OnClientStateChanged?.Invoke(transportState);

            if (newState == ClientState.Connected)
            {
                // Create client connection wrapper
                clientConnection = new NetcodeIOConnection(null);  // Client doesn't have RemoteClient
                OnClientConnected?.Invoke();

                GONetLog.Info("NetcodeIOTransport: Client connected successfully");
            }
            else if (newState == ClientState.Disconnected || newState == ClientState.ConnectionTimedOut)
            {
                GONetTransportDisconnectReason reason = newState == ClientState.ConnectionTimedOut
                    ? GONetTransportDisconnectReason.Timeout
                    : GONetTransportDisconnectReason.UserInitiated;

                OnClientDisconnected?.Invoke(reason);

                GONetLog.Info($"NetcodeIOTransport: Client disconnected - State: {newState}");
            }
        }

        private void OnNetcodeClientMessageReceived(byte[] payload, int payloadSize)
        {
            // NetcodeIO doesn't provide transport-level timestamps like Steamworks,
            // so we use processing time as the receive timestamp for interface compatibility.
            // CRITICAL (December 2025): Must use ELAPSED time domain, NOT absolute UTC ticks!
            // GONet.Network.cs expects timestamps in SecretaryOfTemporalAffairs elapsed domain.
            long processingTimeTicks = GONetMain.SecretaryOfTemporalAffairs.GetRawElapsedTicksStatic();

            // CRITICAL ORDER (December 2025): Fire OnMessageReceivedWithTimestamp FIRST so subscribers
            // can capture the accurate transport-level timestamp BEFORE OnMessageReceived processes the message.
            // GONetConnection stores _pendingTransportReceiveTicks from this event, then uses it in OnMessageReceived.
            OnMessageReceivedWithTimestamp?.Invoke(payload, payloadSize, GONetTransportQoS.Reliable, null, 0, processingTimeTicks);

            // Then fire standard event (for backward compatibility with code that doesn't need timestamps)
            OnMessageReceived?.Invoke(payload, payloadSize, GONetTransportQoS.Reliable, null, 0);
        }

        #endregion

        #region Data Transmission

        public void Send(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection target = null, byte channel = 0)
        {
            // NOTE: NetcodeIO doesn't expose QoS or channels - that's handled by ReliableNetcode layer
            // This method is called by ReliableNetcode's TransmitCallback after adding reliability headers

            if (IsServer)
            {
                if (target == null)
                {
                    // Broadcast to all clients
                    foreach (var connection in serverConnections.Values)
                    {
                        if (connection?.RemoteClient == null || !connection.RemoteClient.Connected)
                            continue;

                        connection.RemoteClient.SendPayload(data, length);
                    }
                }
                else
                {
                    // Send to specific client
                    if (target is NetcodeIOConnection netcodeConnection)
                    {
                        if (netcodeConnection.RemoteClient == null || !netcodeConnection.RemoteClient.Connected)
                            return;

                        netcodeConnection.RemoteClient.SendPayload(data, length);
                    }
                }
            }
            else if (IsClient && client != null)
            {
                client.Send(data, length);
            }
        }

        public void Broadcast(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection excludeConnection = null, byte channel = 0)
        {
            if (!IsServer)
            {
                GONetLog.Warning("NetcodeIOTransport: Broadcast called on client (ignored)");
                return;
            }

            foreach (var connection in serverConnections.Values)
            {
                if (connection != excludeConnection)
                {
                    if (connection?.RemoteClient == null || !connection.RemoteClient.Connected)
                        continue;

                    connection.RemoteClient.SendPayload(data, length);
                }
            }
        }

        public void Update()
        {
            // NetcodeIO uses background thread for Tick() - no update needed
            // But we could pump state changes here if needed

            // Keep lock file fresh if we're running as server
            if (server != null && serverPort > 0)
            {
                GONet.Utils.NetworkUtils.UpdateServerLockFile(serverPort);
            }
        }

        public bool IsServerRunningLocally(int port)
        {
            // Use lock file mechanism (same as Steamworks)
            // This handles both NetcodeIO and Steamworks transports uniformly
            return GONet.Utils.NetworkUtils.IsLocalPortListening(port);
        }

        #endregion

        #region Network Statistics

        public float RTTMilliseconds => 0;  // NetcodeIO doesn't expose RTT (ReliableNetcode does at connection level)
        public float PacketLoss => 0;
        public float SentBandwidthKBPS => 0;
        public float ReceivedBandwidthKBPS => 0;

        #endregion

        #region Connection State

        public bool IsServer => server != null;
        public bool IsClient => client != null;
        public bool IsConnected => client?.State == ClientState.Connected;

        #endregion

        #region Capabilities

        public GONetTransportCapabilities Capabilities =>
            GONetTransportCapabilities.IPv6 |
            GONetTransportCapabilities.Encryption |
            GONetTransportCapabilities.Authentication;
        // NOTE: Does NOT include Reliability flag - ReliableNetcode layer provides that

        public int GetMaxMessageSize(GONetTransportQoS qos)
        {
            // NetcodeIO is wrapped by ReliableNetcode layer, which fragments messages into 16 chunks × 1KB
            // This gives us a practical limit of ~16KB for both reliable and unreliable
            // (Unreliable still goes through ReliableNetcode's unreliable channel with same fragmentation)
            return 16 * 1024;
        }

        #endregion

        #region Helpers

        private GONetTransportClientState MapNetcodeClientState(ClientState netcodeState)
        {
            switch (netcodeState)
            {
                case ClientState.Connected:
                    return GONetTransportClientState.Connected;
                case ClientState.SendingConnectionRequest:
                    return GONetTransportClientState.Connecting;
                case ClientState.Disconnected:
                    return GONetTransportClientState.Disconnected;
                case ClientState.ConnectionTimedOut:
                case ClientState.ConnectionRequestTimedOut:
                    return GONetTransportClientState.TimedOut;
                case ClientState.ConnectionDenied:
                    return GONetTransportClientState.Refused;
                default:
                    return GONetTransportClientState.Error;
            }
        }

        private byte[] GenerateRandomPrivateKey()
        {
            byte[] key = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Create().GetBytes(key);
            return key;
        }

        #endregion
    }

    #region NetcodeIOConnection

    /// <summary>
    /// Connection wrapper for NetcodeIO RemoteClient.
    /// </summary>
    internal class NetcodeIOConnection : IGONetTransportConnection
    {
        internal RemoteClient RemoteClient;

        public NetcodeIOConnection(RemoteClient remoteClient)
        {
            RemoteClient = remoteClient;
        }

        public ulong ConnectionUID => RemoteClient?.ClientID ?? 0;
        public ushort AuthorityId { get; set; }
        public string RemoteAddress => RemoteClient?.RemoteEndpoint?.ToString();
        public bool IsConnected => RemoteClient?.Connected ?? false;
        public float RTTMilliseconds => 0;  // NetcodeIO doesn't track per-connection RTT (ReliableNetcode does)
        public float PacketLoss => 0;
        public uint BytesQueuedForSend => 0;
        public bool IsUsingRelay => false;

        public T GetNativeConnection<T>() where T : class
        {
            return RemoteClient as T;
        }
    }

    #endregion

    #region NetcodeIOConnectionRequest

    /// <summary>
    /// Connection request wrapper for NetcodeIO connection approval.
    /// </summary>
    internal class NetcodeIOConnectionRequest : IGONetTransportConnectionRequest
    {
        private readonly RemoteClient remoteClient;
        internal bool WasRejected { get; private set; }

        public NetcodeIOConnectionRequest(RemoteClient remoteClient)
        {
            this.remoteClient = remoteClient;
        }

        public string RemoteAddress => remoteClient.RemoteEndpoint?.ToString();
        public byte[] RequestData => null;  // NetcodeIO doesn't expose raw request data

        public void Accept()
        {
            // NetcodeIO auto-accepts in OnClientConnected callback
            // This is a no-op for tracking purposes
        }

        public void Reject(GONetTransportDisconnectReason reason)
        {
            WasRejected = true;
            // NetcodeIO doesn't have explicit reject - connection will timeout
        }

        public T GetNativeRequest<T>() where T : class
        {
            return remoteClient as T;
        }
    }

    #endregion
}
