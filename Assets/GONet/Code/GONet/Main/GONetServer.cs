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

using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using GONet.Utils;
using NetcodeIO.NET;
using GONet.Transport;

using GONetChannelId = System.Byte;
using System.Net;

namespace GONet
{
    /// <summary>
    /// Server operating mode for hot standby support.
    /// </summary>
    public enum GONetServerMode : byte
    {
        /// <summary>
        /// Normal active host mode - processes all game traffic.
        /// </summary>
        ActiveHost = 0,

        /// <summary>
        /// Dormant mesh mode - accepts connections but only processes standby handshake + keepalives.
        /// Used for hot standby connections that can be instantly promoted to active during failover.
        /// </summary>
        DormantMesh = 1
    }

    public class GONetServer
    {
        public int MaxClientCount { get; private set; }

        /// <summary>
        /// Current server operating mode.
        /// DormantMesh servers only accept standby handshake + keepalive messages.
        /// </summary>
        public GONetServerMode Mode { get; private set; } = GONetServerMode.ActiveHost;

        /// <summary>
        /// The port this server is listening on (0 if not started or unknown).
        /// </summary>
        public int ListeningPort { get; private set; } = 0;

        /// <summary>
        /// Returns true if the server is running (checks both legacy and new transport paths).
        /// Legacy path: Checks if NetcodeIO server is running.
        /// New path: Checks if transport was started successfully (not just if transport exists).
        /// </summary>
        public bool IsRunning
        {
            get
            {
                if (useNewTransportPath)
                {
                    // New transport path: Check if StartServer() actually succeeded
                    // Transport can exist but StartServer() may have failed (e.g., SteamAPI not initialized)
                    return transport_new != null && serverStartedSuccessfully;
                }
                else
                {
                    // Legacy path: Check NetcodeIO server
                    return server != default && server.IsRunning;
                }
            }
        }

        Server server;

        // NEW: Transport abstraction (Phase 4 - Dual-path architecture)
        private IGONetTransport transport_new;
        private bool useNewTransportPath = false;
        private bool serverStartedSuccessfully = false; // Tracks whether StartServer() succeeded (new transport path only)

        /// <summary>
        /// Gets the transport instance used by this server.
        /// Used by failover integration to create loopback connections.
        /// </summary>
        internal IGONetTransport Transport => transport_new;

        public uint numConnections = 0;
        public readonly List<GONetRemoteClient> remoteClients;
        readonly Dictionary<ushort, GONetRemoteClient> remoteClientsByAuthorityId = new Dictionary<ushort, GONetRemoteClient>(10);
        readonly Dictionary<RemoteClient, GONetRemoteClient> remoteClientToGONetConnectionMap = new Dictionary<RemoteClient, GONetRemoteClient>(10);
        readonly Dictionary<IGONetTransportConnection, GONetRemoteClient> transportConnectionToGONetConnectionMap_new = new Dictionary<IGONetTransportConnection, GONetRemoteClient>(10);
        readonly ConcurrentQueue<RemoteClient> newlyConnectedClients = new ConcurrentQueue<RemoteClient>();
        readonly ConcurrentQueue<RemoteClient> newlyDisconnectedClients = new ConcurrentQueue<RemoteClient>();
        readonly ConcurrentQueue<IGONetTransportConnection> newlyConnectedClients_new = new ConcurrentQueue<IGONetTransportConnection>();
        readonly ConcurrentQueue<IGONetTransportConnection> newlyDisconnectedClients_new = new ConcurrentQueue<IGONetTransportConnection>();

        /// <summary>
        /// Tracks initialization message delivery to detect transport failures (Steamworks, etc).
        /// Key: client authorityId, Value: tracking data for that client's init messages
        /// See: .claude/STEAMWORKS_INIT_MESSAGE_DELIVERY_SOLUTION.md for architecture details
        /// </summary>
        internal readonly Dictionary<ushort, GONetInitMessageTracker> initMessageTrackersByAuthorityId = new Dictionary<ushort, GONetInitMessageTracker>(10);

        /// <summary>
        /// PHASE 2 FIX: Round-robin starting index for client processing fairness.
        /// Prevents "last client penalty" where clients processed later in list experience cumulative processing delay.
        /// Rotates each broadcast cycle to ensure all clients get "first" position equally over time.
        /// </summary>
        public int nextClientProcessingStartIndex = 0;

        /// <summary>
        /// Maximum degree of parallelism for buffer flushing operations.
        /// Calculated once at startup based on CPU core count to avoid stealing cores from Unity/Physics.
        /// Conservative strategy: Use half of available cores, capped based on total core count.
        /// </summary>
        private readonly int maxFlushParallelism;

        /// <summary>
        /// ParallelOptions instance reused for all Parallel.For calls to avoid allocation overhead.
        /// Configured once with maxFlushParallelism and reused indefinitely.
        /// </summary>
        private readonly ParallelOptions parallelFlushOptions;

        public delegate void ClientActionDelegate(GONetConnection_ServerToClient gonetConnection_ServerToClient);
        /// <summary>
        /// This *will* be called from main Unity thread.
        /// Also, consider subscribing to <see cref="RemoteClientStateChangedEvent"/>.
        /// </summary>
        public event ClientActionDelegate ClientConnected;

        /// <summary>
        /// This *will* be called from main Unity thread AFTER all internal stuff updated and cleaned up (i.e., data removed).
        /// Also, consider subscribing to <see cref="RemoteClientStateChangedEvent"/>.
        /// </summary>
        public event ClientActionDelegate ClientDisconnected;

        /// <summary>
        /// INTEGRATION GLUE: Adds a remote client during failover promotion.
        /// Used when a client self-promotes to host and needs to register its own loopback connection.
        /// </summary>
        internal void AddRemoteClientForFailover(GONetRemoteClient remoteClient)
        {
            remoteClients.Add(remoteClient);
            ++numConnections;

            ushort authorityId = remoteClient.ConnectionToClient.OwnerAuthorityId;
            remoteClientsByAuthorityId[authorityId] = remoteClient;

            GONetLog.Info($"[Failover] Added loopback remote client with authority {authorityId}, total connections: {numConnections}");
        }

        /// <summary>
        /// Create GONetServer instance.
        /// </summary>
        /// <param name="maxClientCount">Maximum simultaneous client connections</param>
        /// <param name="port">Server port (for NetcodeIO transport)</param>
        /// <param name="transport">Optional transport implementation (defaults to NetcodeIOTransport if null)</param>
        /// <param name="mode">Server operating mode (default: ActiveHost). Use DormantMesh for hot standby servers.</param>
        public GONetServer(int maxClientCount, int port, IGONetTransport transport = null, GONetServerMode mode = GONetServerMode.ActiveHost)
        {
            MaxClientCount = maxClientCount;
            Mode = mode;
            remoteClients = new List<GONetRemoteClient>(maxClientCount);

            // Calculate optimal parallelism based on CPU core count (once at startup, no runtime overhead)
            int coreCount = Environment.ProcessorCount;
            if (coreCount <= 4)
            {
                // Low-end CPU: Use 2 cores max, leave 2 for Unity main thread + OS
                maxFlushParallelism = 2;
            }
            else if (coreCount <= 8)
            {
                // Mid-range CPU: Leave 3 cores for Unity + Physics + OS
                maxFlushParallelism = coreCount - 3;
            }
            else
            {
                // High-end CPU: Use half cores for flushing, leave other half for game logic
                maxFlushParallelism = coreCount / 2;
            }

            // Create ParallelOptions once and reuse (avoids allocation on every Parallel.For call)
            parallelFlushOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxFlushParallelism
            };

            GONetLog.Info($"GONetServer buffer flush parallelism: {maxFlushParallelism} (CPU cores: {coreCount})");

            if (transport != null)
            {
                // NEW PATH: Use provided transport
                transport_new = transport;
                useNewTransportPath = true;

                // Subscribe to transport events
                transport_new.OnServerConnectionRequested += OnServerConnectionRequested_new;
                transport_new.OnServerClientConnected += OnServerClientConnected_new;
                transport_new.OnServerClientDisconnected += OnServerClientDisconnected_new;

                GONetLog.Info($"GONetServer initialized with transport: {transport.GetType().Name}");
            }
            else
            {
                // OLD PATH: Use NetcodeIO directly (backward compatible)
                server = new Server(maxClientCount, port, GONetMain.noIdeaWhatThisShouldBe_CopiedFromTheirUnitTest, GONetMain._privateKey);
                ListeningPort = port; // Store port for legacy path

                server.LogLevel = NetcodeLogLevel.Debug;

                server.OnClientConnected += OnClientConnected;
                server.OnClientDisconnected += OnClientDisconnected;
                server.OnClientMessageReceived += OnClientMessageReceived;
                server.TickBeginning += Server_TickBeginning_PossibleSeparateThread;

                if (NetworkUtils.IsLocalPortListening(port))
                {
                    GONetLog.Warning("Instantiated a server <instance> locally and the port is already occupied!!!  Calling the <instance>.Start() method will likely fail and return false.");
                }
            }
        }

        /// <summary>
        /// NOTE: <paramref name="initiatingClientConnectionUID"/> correlates to <see cref="RemoteClientStateChangedEvent.InitiatingClientConnectionUID"/> and <see cref="ClientStateChangedEvent.InitiatingClientConnectionUID"/>.
        /// </summary>
        public bool TryGetClientByConnectionUID(ulong initiatingClientConnectionUID, out GONetRemoteClient remoteClient)
        {
            if (initiatingClientConnectionUID != 0)
            {
                int count = remoteClients.Count;
                for (int i = 0; i < count; ++i)
                {
                    var client = remoteClients[i];
                    if (client != null && client.ConnectionToClient != null && client.ConnectionToClient.InitiatingClientConnectionUID == initiatingClientConnectionUID)
                    {
                        remoteClient = client;
                        return true;
                    }
                }
            }

            remoteClient = null;
            return false;
        }

        /// <summary>
        /// Disconnects a client identified by <see cref="GONetConnection.InitiatingClientConnectionUID"/>.
        /// Primarily used by hot standby promotion logic to disconnect stale standby links that must not be treated as gameplay clients.
        /// </summary>
        internal bool TryDisconnectClientByConnectionUID(ulong initiatingClientConnectionUID, GONetTransportDisconnectReason reason, string logContext = null)
        {
            if (initiatingClientConnectionUID == 0)
            {
                return false;
            }

            if (!TryGetClientByConnectionUID(initiatingClientConnectionUID, out GONetRemoteClient remoteClient) || remoteClient == null)
            {
                return false;
            }

            // Never disconnect the host loopback via this helper.
            if (remoteClient.ConnectionToClient is GONetConnection_ClientHostLoopback)
            {
                return false;
            }

            try
            {
                if (useNewTransportPath)
                {
                    if (transport_new == null)
                    {
                        return false;
                    }

                    IGONetTransportConnection transportConnection = null;
                    foreach (var kvp in transportConnectionToGONetConnectionMap_new)
                    {
                        if (ReferenceEquals(kvp.Value, remoteClient))
                        {
                            transportConnection = kvp.Key;
                            break;
                        }
                    }

                    if (transportConnection == null)
                    {
                        return false;
                    }

                    transport_new.DisconnectConnection(transportConnection, reason);
                }
                else
                {
                    if (server == null || remoteClient.RemoteClient == null)
                    {
                        return false;
                    }

                    server.Disconnect(remoteClient.RemoteClient);
                }

                string contextSuffix = string.IsNullOrWhiteSpace(logContext) ? string.Empty : $" ({logContext})";
                GONetLog.Info($"[GONetServer] Disconnected client UID={initiatingClientConnectionUID} AuthId={remoteClient.ConnectionToClient.OwnerAuthorityId}{contextSuffix}");
                return true;
            }
            catch (Exception ex)
            {
                string contextSuffix = string.IsNullOrWhiteSpace(logContext) ? string.Empty : $" ({logContext})";
                GONetLog.Warning($"[GONetServer] Failed to disconnect client UID={initiatingClientConnectionUID}{contextSuffix}: {ex.Message}");
                return false;
            }
        }

        private void Server_TickBeginning_PossibleSeparateThread()
        {
            // Parallel buffer flushing: Use thread pool with bounded parallelism to avoid CPU starvation
            // MaxDegreeOfParallelism calculated once at startup based on core count
            Parallel.For(0, (int)numConnections, parallelFlushOptions, iConnection =>
            {
                GONetConnection_ServerToClient gONetConnection_ServerToClient = remoteClients[iConnection].ConnectionToClient;
                gONetConnection_ServerToClient.ProcessSendBuffer_IfAppropriate();

                /* TELEMETRY DISABLED - Uncomment for performance analysis
                long clientFlushStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                gONetConnection_ServerToClient.ProcessSendBuffer_IfAppropriate();
                long clientFlushEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                double flushMicroseconds = (clientFlushEndTicks - clientFlushStartTicks) * 1000000.0 / System.Diagnostics.Stopwatch.Frequency;
                double cumulativeMicroseconds = (clientFlushEndTicks - tickStartTicks) * 1000000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (flushMicroseconds > 100)
                {
                    GONetLog.Debug($"[FLUSH-TELEMETRY] OLD-PATH-PARALLEL Client#{iConnection} AuthId:{gONetConnection_ServerToClient.OwnerAuthorityId} | FlushTime:{flushMicroseconds:F1}μs WallClock:{cumulativeMicroseconds:F1}μs RTT:{gONetConnection_ServerToClient.RTTMilliseconds:F1}ms Loss:{gONetConnection_ServerToClient.PacketLoss:F3}");
                }
                */
            });

            /* TELEMETRY DISABLED - Uncomment for performance analysis
            long tickEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            double totalFlushMicroseconds = (tickEndTicks - tickStartTicks) * 1000000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (totalFlushMicroseconds > 500)
            {
                GONetLog.Warning($"[FLUSH-TELEMETRY] OLD-PATH-PARALLEL TOTAL flush time: {totalFlushMicroseconds:F1}μs for {numConnections} clients (avg {totalFlushMicroseconds / numConnections:F1}μs/client, parallelism: {maxFlushParallelism})");
            }
            */
        }

        /// <summary>
        /// If the server can start, it will and return true......returns false otherwise.
        /// </summary>
        /// <param name="port">Port to bind server (only used with new transport path)</param>
        public bool Start(int port = 0)
        {
            try
            {
                // Only set isServerOverride for ActiveHost servers, NOT for DormantMesh (hot standby) servers
                if (Mode == GONetServerMode.ActiveHost)
                {
                    GONetMain.isServerOverride = true; // wherever the server is running is automatically considered "the" server
                }

                if (useNewTransportPath)
                {
                    // NEW PATH: Start transport
                    if (port <= 0)
                    {
                        GONetLog.Error("Port must be specified when using new transport path");
                        serverStartedSuccessfully = false;
                        return false;
                    }

                    transport_new.StartServer(port, MaxClientCount);
                    serverStartedSuccessfully = true; // Mark success
                    ListeningPort = port;
                    GONetLog.Info($"GONetServer started on port {port} with transport: {transport_new.GetType().Name}");
                }
                else
                {
                    // OLD PATH: Start NetcodeIO server
                    server.Start(128); // 128 tick = competitive eSports standard (CS:GO/CS2)
                }
            }
            catch (Exception e)
            { // one main reason why here is if a server is already started on this machine on port....so this process will turn into a client
                GONetLog.Error($"Attempting to start server failed due to exception of type: {e.GetType().Name} with message: {e.Message}");
                serverStartedSuccessfully = false; // Mark failure
                return false;
            }

            return true;
        }

        /// <summary>
        /// Call this every frame (from the main Unity thread!) in order to process all network traffic in a timely manner.
        /// </summary>
        public void Update()
        {
            // Cache frame count for thread-safe access from background send threads
            _cachedFrameCount = UnityEngine.Time.frameCount;

            // Update all connections
            for (int iConnection = 0; iConnection < numConnections; ++iConnection)
            {
                GONetConnection_ServerToClient gONetConnection_ServerToClient = remoteClients[iConnection].ConnectionToClient;
                gONetConnection_ServerToClient.Update(); // have to do this in order for anything to really be processed, in or out.
            }

            // Update transport (if using new path)
            if (useNewTransportPath && transport_new != null)
            {
                transport_new.Update();

                // CRITICAL: Flush send buffers every frame (same as old path's background tick callback)
                // Without this, messages queue indefinitely causing choppy physics sync
                // Parallel buffer flushing: Use thread pool with bounded parallelism to avoid CPU starvation
                Parallel.For(0, (int)numConnections, parallelFlushOptions, iConnection =>
                {
                    GONetConnection_ServerToClient gONetConnection_ServerToClient = remoteClients[iConnection].ConnectionToClient;
                    gONetConnection_ServerToClient.ProcessSendBuffer_IfAppropriate();

                    /* TELEMETRY DISABLED - Uncomment for performance analysis
                    long clientFlushStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    gONetConnection_ServerToClient.ProcessSendBuffer_IfAppropriate();
                    long clientFlushEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    double flushMicroseconds = (clientFlushEndTicks - clientFlushStartTicks) * 1000000.0 / System.Diagnostics.Stopwatch.Frequency;
                    double wallClockMicroseconds = (clientFlushEndTicks - tickStartTicks) * 1000000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (flushMicroseconds > 100)
                    {
                        GONetLog.Debug($"[FLUSH-TELEMETRY] NEW-PATH-PARALLEL Client#{iConnection} AuthId:{gONetConnection_ServerToClient.OwnerAuthorityId} | FlushTime:{flushMicroseconds:F1}μs WallClock:{wallClockMicroseconds:F1}μs RTT:{gONetConnection_ServerToClient.RTTMilliseconds:F1}ms Loss:{gONetConnection_ServerToClient.PacketLoss:F3}");
                    }
                    */
                });

                /* TELEMETRY DISABLED - Uncomment for performance analysis
                long tickEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                double totalFlushMicroseconds = (tickEndTicks - tickStartTicks) * 1000000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (totalFlushMicroseconds > 500)
                {
                    GONetLog.Warning($"[FLUSH-TELEMETRY] NEW-PATH-PARALLEL TOTAL flush time: {totalFlushMicroseconds:F1}μs for {numConnections} clients (avg {totalFlushMicroseconds / numConnections:F1}μs/client, parallelism: {maxFlushParallelism})");
                }
                */
            }

            ProcessClientsNewlyConnectedDisconnected_MainUnityThread();

            // CRITICAL FIX (January 2026): Check for clients stuck loading scene due to lost SceneLoadCompleteEvent
            // Under extreme network conditions (poor WiFi + CPU throttle), SceneLoadCompleteEvent can be lost
            // despite reliable channel retries. This proactively sends GONetIds after a timeout.
            if (Mode == GONetServerMode.ActiveHost)
            {
                GONetMain.Server_CheckClientLoadingTimeouts();
            }
        }

        // STEAMWORKS SYNC DIAGNOSTIC (Dec 2025)
        private static int _syncDiag_sendCallCount = 0;
        private static int _syncDiag_lastLogFrame = 0;
        private static int _cachedFrameCount = 0; // Cached on main thread for use in background threads
        private static int _syncDiag_bytesSentSinceLastLog = 0;
        private static int _syncDiag_clientsSentToSinceLastLog = 0;

        public void SendBytesToAllClients(byte[] bytes, int bytesUsedCount, GONetChannelId channelId)
        {
            _syncDiag_sendCallCount++;
            int clientsSentTo = 0;
            int clientsSkippedLoopback = 0;
            int clientsSkippedServerAuth = 0;

            for (int iConnection = 0; iConnection < numConnections; ++iConnection)
            {
                GONetConnection_ServerToClient gONetConnection_ServerToClient = remoteClients[iConnection].ConnectionToClient;
                // HOST MODE FIX: Skip loopback connection - host already has all data locally
                if (gONetConnection_ServerToClient is GONetConnection_ClientHostLoopback)
                {
                    clientsSkippedLoopback++;
                    continue;
                }
                // HOT STANDBY HARDENING: Never send gameplay traffic to a non-loopback connection that identifies as server authority.
                // This indicates a stale standby mesh link that must not be treated as a gameplay client after promotion.
                if (gONetConnection_ServerToClient.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Server)
                {
                    clientsSkippedServerAuth++;
                    continue;
                }
                // SLOT RESERVATION (December 2025): Use channel-based priority
                var priority = GONetChannel.GetMessagePriority(channelId);
                gONetConnection_ServerToClient.SendMessageOverChannel(bytes, bytesUsedCount, channelId, priority);
                clientsSentTo++;
            }

            _syncDiag_bytesSentSinceLastLog += bytesUsedCount * clientsSentTo;
            _syncDiag_clientsSentToSinceLastLog += clientsSentTo;

            // Log every 120 frames (~2 seconds at 60fps) when diagnostics enabled
            // Use cached frame count since this may be called from background thread
            int currentFrame = _cachedFrameCount;
            if (GONetConfig.LogSyncDiagnostics && currentFrame - _syncDiag_lastLogFrame >= 120)
            {
                // Build authority list for diagnostic
                System.Text.StringBuilder authInfo = new System.Text.StringBuilder();
                for (int i = 0; i < numConnections; i++)
                {
                    var conn = remoteClients[i].ConnectionToClient;
                    bool isLoopback = conn is GONetConnection_ClientHostLoopback;
                    bool isServerAuth = conn.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Server;
                    if (authInfo.Length > 0) authInfo.Append(", ");
                    authInfo.Append($"{conn.OwnerAuthorityId}");
                    if (isLoopback) authInfo.Append("(LB)");
                    if (isServerAuth) authInfo.Append("(SA)");
                }
                GONetLog.Debug($"[SYNC-DIAG-TX] SendBytesToAllClients: numConn={numConnections}, sentTo={clientsSentTo}, skipLB={clientsSkippedLoopback}, skipSA={clientsSkippedServerAuth}, ch={channelId} | Authorities: [{authInfo}]");
                _syncDiag_lastLogFrame = currentFrame;
                _syncDiag_bytesSentSinceLastLog = 0;
                _syncDiag_clientsSentToSinceLastLog = 0;
            }
        }

        public void SendBytesToClient(GONetRemoteClient remoteClient, byte[] bytes, int bytesUsedCount, GONetChannelId channelId)
        {
            GONetConnection_ServerToClient gONetConnection_ServerToClient = remoteClient.ConnectionToClient;
            // HOST MODE FIX: Skip loopback connection - host already has all data locally
            if (gONetConnection_ServerToClient is GONetConnection_ClientHostLoopback)
            {
                return;
            }
            if (gONetConnection_ServerToClient.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Server)
            {
                return;
            }
            // SLOT RESERVATION (December 2025): Use channel-based priority
            var priority = GONetChannel.GetMessagePriority(channelId);
            gONetConnection_ServerToClient.SendMessageOverChannel(bytes, bytesUsedCount, channelId, priority);
        }

        public void ForEachClient(Action<GONetConnection_ServerToClient> doThis)
        {
            for (int iConnection = 0; iConnection < numConnections; ++iConnection)
            {
                GONetConnection_ServerToClient gONetConnection_ServerToClient = remoteClients[iConnection].ConnectionToClient;
                doThis(gONetConnection_ServerToClient);
            }
        }

        /// <summary>
        /// NOTE: This will likely be called on a thread other than the main Unity thread
        /// </summary>
        private void OnClientMessageReceived(RemoteClient sender, byte[] payload, int payloadSize)
        {
            GONetRemoteClient remoteClient;
            if (remoteClientToGONetConnectionMap.TryGetValue(sender, out remoteClient))
            {
                remoteClient.ConnectionToClient.ReceivePacket(payload, payloadSize);
            }
            else // we will ASSume the first time through is the connection message and OnClientConnected will not have been called yet and we will just go straight to process
            {
                // nothing to do i suppose
            }
        }

        /// <summary>
        /// NOTE: This will likely be called on a thread other than the main Unity thread
        /// </summary>
        private void OnClientDisconnected(RemoteClient client)
        {
            const string DIS = "client *DIS*connected";
            GONetLog.Debug(DIS);

            newlyDisconnectedClients.Enqueue(client);
        }

        /// <summary>
        /// NOTE: This will likely be called on a thread other than the main Unity thread
        /// </summary>
        private void OnClientConnected(RemoteClient client)
        {
            const string CON = "client connected";
            GONetLog.Debug(CON);

            newlyConnectedClients.Enqueue(client);
        }

        /// <summary>
        /// So, this bubbles it up and fires a GONet event for them (i.e., <see cref="RemoteClientStateChangedEvent"/>).
        /// </summary>
        private void OnClientConnectionStateChanged_BubbleEventUp(RemoteClient client, bool justConnected)
        {
            ClientState previous = justConnected ? ClientState.Disconnected: ClientState.Connected;
            ClientState newState = justConnected ? ClientState.Connected : ClientState.Disconnected;

            ulong clientID = 0;
            if (remoteClientToGONetConnectionMap.ContainsKey(client) && remoteClientToGONetConnectionMap[client].ConnectionToClient != null)
            {
                clientID = remoteClientToGONetConnectionMap[client].ConnectionToClient.InitiatingClientConnectionUID;
            }
            else
            {
                const string WHY = "Client connection state changed, but not able to look up the related client information!!!  Check into this.";
                GONetLog.Error(WHY);
            }

            const string CLIENT = "Client connection state changed to: ";
            const string AUTH = ".  My client guid: ";
            GONetLog.Debug(string.Concat(CLIENT, Enum.GetName(typeof(ClientState), newState), AUTH, clientID));

            GONetMain.EventBus.PublishASAP(new RemoteClientStateChangedEvent(GONetMain.Time.ElapsedTicks, clientID, previous, newState));
        }

        private void ProcessClientsNewlyConnectedDisconnected_MainUnityThread()
        {
            if (useNewTransportPath)
            {
                // NEW PATH: Process IGONetTransportConnection events
                int count = newlyConnectedClients_new.Count;
                for (int i = 0; i < count && !newlyConnectedClients_new.IsEmpty; ++i)
                {
                    IGONetTransportConnection connection;
                    if (newlyConnectedClients_new.TryDequeue(out connection))
                    {
                        ProcessClientNewlyConnected_MainUnityThread_new(connection);
                    }
                }

                count = newlyDisconnectedClients_new.Count;
                for (int i = 0; i < count && !newlyDisconnectedClients_new.IsEmpty; ++i)
                {
                    IGONetTransportConnection connection;
                    if (newlyDisconnectedClients_new.TryDequeue(out connection))
                    {
                        ProcessClientNewlyDisconnected_MainUnityThread_new(connection);
                    }
                }
            }
            else
            {
                // OLD PATH: Process NetcodeIO RemoteClient events
                int count = newlyConnectedClients.Count;
                for (int i = 0; i < count && !newlyConnectedClients.IsEmpty; ++i)
                {
                    RemoteClient client;
                    if (newlyConnectedClients.TryDequeue(out client))
                    {
                        ProcessClientNewlyConnected_MainUnityThread(client);
                    } // else TODO warn
                }

                count = newlyDisconnectedClients.Count;
                for (int i = 0; i < count && !newlyDisconnectedClients.IsEmpty; ++i)
                {
                    RemoteClient client;
                    if (newlyDisconnectedClients.TryDequeue(out client))
                    {
                        ProcessClientNewlyDisconnected_MainUnityThread(client);
                    } // else TODO warn
                }
            }
        }

        /// <summary>
        /// OLD PATH: Process newly connected client (main Unity thread)
        /// PHASE II: Detects host player and creates loopback connection for optimization
        /// NOTE: Loopback only fully supported in NEW PATH (pluggable transport)
        /// OLD PATH uses normal connection but could be extended if needed
        /// </summary>
        private void ProcessClientNewlyConnected_MainUnityThread(RemoteClient client)
        {
            if (numConnections < MaxClientCount)
            {
                int maxQueueSize = GONetGlobal.Instance != null ? GONetGlobal.Instance.maxReliableMessageQueueSize : 2000;

                // NOTE: For OLD PATH (legacy NetcodeIO), we use standard connection
                // Loopback optimization primarily targets NEW PATH (pluggable transport)
                // Could add loopback here too if needed, but NEW PATH is recommended
                GONetConnection_ServerToClient gonetConnection_ServerToClient = new GONetConnection_ServerToClient(client, maxQueueSize);
                GONetRemoteClient remoteClient = new GONetRemoteClient(client, gonetConnection_ServerToClient);

                // LATE-JOINER SYNC SUPPRESSION: Mark new client as loading the server's current active scene
                // This suppresses unreliable sync messages until client sends SceneLoadCompleteEvent
                // Without this, sync bundles flood the client BEFORE they've loaded the scene and received GONetId mappings
                if (Mode == GONetServerMode.ActiveHost)
                {
                    string activeSceneName = GONetMain.SceneManager?.ActiveSceneName;
                    if (!string.IsNullOrEmpty(activeSceneName))
                    {
                        remoteClient.IsCurrentlyLoadingScene = true;
                        remoteClient.CurrentlyLoadingSceneName = activeSceneName;
                        remoteClient.LoadingStartedTime = GONetMain.Time.ElapsedSeconds;
                        remoteClient.HasSentLoadingTimeoutFallback = false;
                        GONetLog.Info($"[LATE-JOINER-INIT] Marked new client as loading scene '{activeSceneName}' (unreliable sync suppressed until SceneLoadCompleteEvent)");
                    }
                }

                remoteClients.Add(remoteClient);
                ++numConnections;
                remoteClientToGONetConnectionMap[client] = remoteClient;

                ClientConnected?.Invoke(gonetConnection_ServerToClient);
            } // else TODO warn
        }

        private void ProcessClientNewlyDisconnected_MainUnityThread(RemoteClient client)
        {
            if (remoteClientToGONetConnectionMap.TryGetValue(client, out GONetRemoteClient gonetRemoteClient) &&
                remoteClients.Remove(gonetRemoteClient))
            {
                ushort authorityId = gonetRemoteClient.ConnectionToClient.OwnerAuthorityId;
                if (remoteClientsByAuthorityId.TryGetValue(authorityId, out GONetRemoteClient mappedClient) &&
                    ReferenceEquals(mappedClient, gonetRemoteClient))
                {
                    remoteClientsByAuthorityId.Remove(authorityId);
                }

                // Clean up init message tracker
                RemoveInitMessageTracker(authorityId);

                --numConnections;
            } // else TODO warn

            remoteClientToGONetConnectionMap.Remove(client);
            ClientDisconnected?.Invoke(gonetRemoteClient?.ConnectionToClient);
        }

        public void Stop()
        {
            if (useNewTransportPath && transport_new != null)
            {
                // NEW PATH: Stop transport
                transport_new.StopServer();
            }
            else if (server != null)
            {
                // OLD PATH: Stop NetcodeIO server
                server.Stop();
            }

            GONetMain.isServerOverride = false; // wherever the server is running is automatically considered "the" server
        }

        /// <summary>
        /// Finds a connection by its ConnectionUID.
        /// Used by hot standby system to send messages to specific standby clients.
        /// </summary>
        public GONetConnection_ServerToClient GetConnectionByUID(ulong connectionUID)
        {
            foreach (var remoteClient in remoteClients)
            {
                if (remoteClient.ConnectionToClient?.InitiatingClientConnectionUID == connectionUID)
                {
                    return remoteClient.ConnectionToClient;
                }
            }
            return null;
        }

        /// <summary>
        /// Promotes a DormantMesh server to ActiveHost mode.
        /// Called during failover when this node becomes the new host.
        /// Existing standby connections become active game connections.
        /// </summary>
        /// <returns>True if promotion succeeded, false if server was already active or not running.</returns>
        public bool PromoteToActive()
        {
            if (!IsRunning)
            {
                GONetLog.Error("[GONetServer] Cannot promote - server is not running");
                return false;
            }

            if (Mode == GONetServerMode.ActiveHost)
            {
                GONetLog.Warning("[GONetServer] Server is already in ActiveHost mode");
                return false;
            }

            Mode = GONetServerMode.ActiveHost;
            GONetMain.isServerOverride = true;

            // Update our authority ID to server authority so IsMine checks work correctly
            GONetMain.PromoteToServerAuthority();

            GONetLog.Info($"[GONetServer] Promoted from DormantMesh to ActiveHost with {numConnections} existing connections");
            return true;
        }

        /// <summary>
        /// Returns true if this server should process game traffic.
        /// DormantMesh servers reject non-standby messages.
        /// </summary>
        public bool ShouldProcessGameTraffic => Mode == GONetServerMode.ActiveHost;

        public GONetRemoteClient GetRemoteClientByConnection(GONetConnection_ServerToClient connectionToClient)
        {
            return remoteClients.FirstOrDefault(x => x.ConnectionToClient == connectionToClient);
        }

        public bool TryGetRemoteClientByAuthorityId(ushort authorityId, out GONetRemoteClient gonetRemoteClient)
        {
            return remoteClientsByAuthorityId.TryGetValue(authorityId, out gonetRemoteClient);
        }

        public GONetRemoteClient GetRemoteClientByAuthorityId(ushort authorityId)
        {
            return remoteClientsByAuthorityId[authorityId];
        }

        /// <summary>
        /// FAILOVER FIX: Registers a remote client in the authority ID lookup dictionary.
        /// Called during failover when dormant server connections need to be made discoverable.
        ///
        /// Background: When the dormant server is promoted, its connections have OwnerAuthorityId
        /// set via PopulateDormantServerConnectionAuthorities(), but the remoteClientsByAuthorityId
        /// dictionary is NOT updated (because OnConnectionToClientAuthorityIdAssigned never fires
        /// for dormant connections). This causes KeyNotFoundException in sync data sending.
        /// </summary>
        public void RegisterRemoteClientByAuthorityId(ushort authorityId, GONetRemoteClient remoteClient)
        {
            remoteClientsByAuthorityId[authorityId] = remoteClient;
            GONetLog.Debug($"[GONetServer] Registered remote client for authority {authorityId} in lookup dictionary");

            // Failover clients already completed initial handshake; disable init tracking to avoid stale trackers.
            remoteClient.IsInitMessageTrackingActive = false;

            // CRITICAL FAILOVER FIX: These clients are NOT loading a scene - they're already in the game
            // and just switched to a new host via hot standby. Without this, sync data gets dropped with
            // "[SCENE-LOADING-DROP] Dropping unreliable message for client X (loading scene 'GONetSample')"
            // because the dormant server's remoteClients may have IsCurrentlyLoadingScene=true from
            // their initial connection setup.
            remoteClient.IsCurrentlyLoadingScene = false;
            remoteClient.CurrentlyLoadingSceneName = null;
        }

        public void AddP2pEndPoint(IPEndPoint p2pEndPoint)
        {
            server.AddP2pEndPoint(p2pEndPoint);

            // TODO consolidate this!!
            GONetGlobal.ServerP2pEndPoint = p2pEndPoint;
        }

        internal void OnConnectionToClientAuthorityIdAssigned(GONetConnection_ServerToClient connectionToClient, ushort ownerAuthorityId)
        {
            GONetRemoteClient remoteClient = GetRemoteClientByConnection(connectionToClient);
            remoteClientsByAuthorityId[connectionToClient.OwnerAuthorityId] = remoteClient;
            if (remoteClient != null)
            {
                remoteClient.IsInitMessageTrackingActive = true;
            }

            // DEBUG: Set connection ID for ACK instrumentation logging
            connectionToClient.ConnectionId = $"S->C{ownerAuthorityId}";

            // Initialize init message tracker for this client
            initMessageTrackersByAuthorityId[ownerAuthorityId] = new GONetInitMessageTracker(ownerAuthorityId);
#if GONet_INIT_TRACE
            GONetLog.Debug($"[InitMsgTracker] Created tracker for new client with AuthorityId {ownerAuthorityId}");
#endif
        }

        /// <summary>
        /// Get or create init message tracker for a specific client.
        /// Used by GONetMain when sending initialization messages.
        /// </summary>
        internal GONetInitMessageTracker GetOrCreateInitMessageTracker(ushort authorityId)
        {
            if (!initMessageTrackersByAuthorityId.TryGetValue(authorityId, out GONetInitMessageTracker tracker))
            {
                tracker = new GONetInitMessageTracker(authorityId);
                initMessageTrackersByAuthorityId[authorityId] = tracker;
                GONetLog.Warning($"[InitMsgTracker] Late-created tracker for AuthorityId {authorityId} (should have been created during authority assignment)");
            }
            return tracker;
        }

        /// <summary>
        /// Clean up init message tracker when client disconnects.
        /// </summary>
        internal void RemoveInitMessageTracker(ushort authorityId)
        {
            if (initMessageTrackersByAuthorityId.TryGetValue(authorityId, out GONetInitMessageTracker tracker))
            {
                tracker.Dispose();
                initMessageTrackersByAuthorityId.Remove(authorityId);
#if GONet_INIT_TRACE
                GONetLog.Debug($"[InitMsgTracker] Removed and disposed tracker for disconnected client (AuthorityId {authorityId})");
#endif
            }
        }

        #region NEW TRANSPORT PATH - Event Handlers

        /// <summary>
        /// NEW PATH: Connection approval handler (Enhancement 1)
        /// NOTE: This may be called from background thread
        /// </summary>
        private bool OnServerConnectionRequested_new(IGONetTransportConnectionRequest request)
        {
            // Check if server has capacity for another client
            if (numConnections >= MaxClientCount)
            {
                GONetLog.Warning($"Server full ({numConnections}/{MaxClientCount}) - rejecting connection from {request.RemoteAddress}");
                request.Reject(GONetTransportDisconnectReason.ServerFull);
                return false; // Reject at transport layer BEFORE accepting
            }

            GONetLog.Debug($"Connection request from {request.RemoteAddress} - approved ({numConnections}/{MaxClientCount})");
            return true;
        }

        /// <summary>
        /// NEW PATH: Client connected handler
        /// NOTE: This will likely be called on a thread other than the main Unity thread
        /// </summary>
        private void OnServerClientConnected_new(IGONetTransportConnection connection)
        {
            const string CON = "client connected (new transport path)";
            GONetLog.Debug(CON);

            newlyConnectedClients_new.Enqueue(connection);
        }

        /// <summary>
        /// NEW PATH: Client disconnected handler
        /// NOTE: This will likely be called on a thread other than the main Unity thread
        /// </summary>
        private void OnServerClientDisconnected_new(IGONetTransportConnection connection, GONetTransportDisconnectReason reason)
        {
            const string DIS = "client *DIS*connected (new transport path)";
            GONetLog.Debug($"{DIS} - Reason: {reason}");

            newlyDisconnectedClients_new.Enqueue(connection);
        }

        /// <summary>
        /// NEW PATH: Process newly connected client (main Unity thread)
        /// PHASE II: Detects host player and creates loopback connection for optimization
        /// </summary>
        private void ProcessClientNewlyConnected_MainUnityThread_new(IGONetTransportConnection connection)
        {
            if (numConnections < MaxClientCount)
            {
                int maxQueueSize = GONetGlobal.Instance != null ? GONetGlobal.Instance.maxReliableMessageQueueSize : 2000;

                // PHASE II: Detect if this is the host player (local client)
                // Check if GONetClient exists and has ListenServer flag
                bool isHostPlayer = GONetMain.GONetClient != null &&
                                    GONetMain.GONetClient.ClientTypeFlags.HasFlag(ClientTypeFlags.ListenServer) &&
                                    numConnections == 0; // First client connecting must be host

                GONetConnection_ServerToClient gonetConnection_ServerToClient;

                if (isHostPlayer)
                {
                    // PHASE II OPTIMIZATION: Create loopback connection for host player
                    GONetLog.Info("[GONetServer] Detected host player connection - creating GONetConnection_ClientHostLoopback for optimization");
                    gonetConnection_ServerToClient = new GONetConnection_ClientHostLoopback(
                        transport_new,
                        connection,
                        GONetMain.GONetClient,
                        maxQueueSize);
                }
                else
                {
                    // Standard remote client connection
                    gonetConnection_ServerToClient = new GONetConnection_ServerToClient(transport_new, connection, maxQueueSize);
                }

                GONetRemoteClient remoteClient = new GONetRemoteClient(null, gonetConnection_ServerToClient); // RemoteClient is null for new path

                // LATE-JOINER SYNC SUPPRESSION: Mark new client as loading the server's current active scene
                // This suppresses unreliable sync messages until client sends SceneLoadCompleteEvent
                // Without this, sync bundles flood the client BEFORE they've loaded the scene and received GONetId mappings
                if (Mode == GONetServerMode.ActiveHost)
                {
                    string activeSceneName = GONetMain.SceneManager?.ActiveSceneName;
                    if (!string.IsNullOrEmpty(activeSceneName))
                    {
                        remoteClient.IsCurrentlyLoadingScene = true;
                        remoteClient.CurrentlyLoadingSceneName = activeSceneName;
                        remoteClient.LoadingStartedTime = GONetMain.Time.ElapsedSeconds;
                        remoteClient.HasSentLoadingTimeoutFallback = false;
                        GONetLog.Info($"[LATE-JOINER-INIT] Marked new client as loading scene '{activeSceneName}' (unreliable sync suppressed until SceneLoadCompleteEvent)");
                    }
                }

                remoteClients.Add(remoteClient);
                ++numConnections;
                transportConnectionToGONetConnectionMap_new[connection] = remoteClient;

                ClientConnected?.Invoke(gonetConnection_ServerToClient);

                // Bubble up event
                OnClientConnectionStateChanged_BubbleEventUp_new(connection, true);
            }
        }

        /// <summary>
        /// NEW PATH: Process newly disconnected client (main Unity thread)
        /// </summary>
        private void ProcessClientNewlyDisconnected_MainUnityThread_new(IGONetTransportConnection connection)
        {
            if (transportConnectionToGONetConnectionMap_new.TryGetValue(connection, out GONetRemoteClient gonetRemoteClient) &&
                remoteClients.Remove(gonetRemoteClient))
            {
                ushort authorityId = gonetRemoteClient.ConnectionToClient.OwnerAuthorityId;
                if (remoteClientsByAuthorityId.TryGetValue(authorityId, out GONetRemoteClient mappedClient) &&
                    ReferenceEquals(mappedClient, gonetRemoteClient))
                {
                    remoteClientsByAuthorityId.Remove(authorityId);
                }

                // Clean up init message tracker
                RemoveInitMessageTracker(authorityId);

                --numConnections;
            }

            transportConnectionToGONetConnectionMap_new.Remove(connection);
            ClientDisconnected?.Invoke(gonetRemoteClient?.ConnectionToClient);

            // Bubble up event
            OnClientConnectionStateChanged_BubbleEventUp_new(connection, false);
        }

        /// <summary>
        /// NEW PATH: Bubble up connection state change event
        /// </summary>
        private void OnClientConnectionStateChanged_BubbleEventUp_new(IGONetTransportConnection connection, bool justConnected)
        {
            ClientState previous = justConnected ? ClientState.Disconnected : ClientState.Connected;
            ClientState newState = justConnected ? ClientState.Connected : ClientState.Disconnected;

            ulong clientID = connection?.ConnectionUID ?? 0;

            const string CLIENT = "Client connection state changed to: ";
            const string AUTH = ".  My client UID: ";
            GONetLog.Debug(string.Concat(CLIENT, Enum.GetName(typeof(ClientState), newState), AUTH, clientID));

            GONetMain.EventBus.PublishASAP(new RemoteClientStateChangedEvent(GONetMain.Time.ElapsedTicks, clientID, previous, newState));
        }

        #endregion
    }
}
