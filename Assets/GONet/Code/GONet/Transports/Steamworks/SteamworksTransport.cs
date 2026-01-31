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

using Steamworks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ReliableNetcode.Utils;
using GONet.Utils;

namespace GONet.Transport
{
    /// <summary>
    /// Transport implementation using Steamworks P2P networking via ISteamNetworkingSockets.
    ///
    /// <para>
    /// ARCHITECTURE:
    /// - Uses ISteamNetworkingSockets (modern Steam networking API, NOT legacy ISteamNetworking)
    /// - Supports Steam Datagram Relay (SDR) for NAT traversal via Valve's relay network
    /// - P2P connections via Steam IDs (CSteamID) - no IP addresses or port numbers needed
    /// - Automatic encryption, authentication, and congestion control via Steam networking layer
    /// </para>
    ///
    /// <para>
    /// CAPABILITIES:
    /// - P2P connections (peer-to-peer via Steam)
    /// - Encryption (transport-layer via Steam)
    /// - Authentication (Steam ID validation via SteamAPI)
    /// - Reliability (configurable per-message via nSendFlags)
    /// - Compression (built into Steam networking stack)
    /// - Fragmentation (automatic for messages up to k_cbMaxSteamNetworkingSocketsMessageSizeSend)
    /// </para>
    ///
    /// <para>
    /// REQUIREMENTS:
    /// - Steam client must be running
    /// - SteamAPI.Init() must succeed before using this transport
    /// - steam_appid.txt must contain GONet App ID 4168160
    /// </para>
    ///
    /// <para>
    /// THREAD SAFETY:
    /// - Steam callbacks may fire from any thread
    /// - All GONet events are marshalled to main thread via ConcurrentQueue
    /// - Update() MUST be called from Unity main thread each frame
    /// </para>
    /// </summary>
    public class SteamworksTransport : IGONetTransport
    {
        #region Fields

        // Steam networking handles
        private HSteamListenSocket listenSocketP2P = HSteamListenSocket.Invalid;  // For Steam P2P connections (ConnectP2P)
        private HSteamListenSocket listenSocketIP = HSteamListenSocket.Invalid;   // For Direct IP connections (ConnectByIPAddress)

        // P2P listen sockets are keyed by virtual port so we can accept new gameplay clients (virtual port 0)
        // while also accepting hot-standby mesh clients (virtual port 1), especially after failover promotion.
        private readonly object listenSocketLock = new object();
        private readonly Dictionary<int, HSteamListenSocket> p2pListenSocketsByVirtualPort = new Dictionary<int, HSteamListenSocket>();
        private HSteamNetPollGroup pollGroup = HSteamNetPollGroup.Invalid;
        private HSteamNetConnection clientConnection = HSteamNetConnection.Invalid;

        // Server connection tracking
        private readonly Dictionary<HSteamNetConnection, SteamworksConnection> serverConnections =
            new Dictionary<HSteamNetConnection, SteamworksConnection>();

        // Client connection
        private SteamworksConnection myClientConnection;

        // Configuration
        private GONetTransportConfig config;
        private int serverPort = -1; // Cached port for lock file management
        private int listenVirtualPort = 0; // Steam P2P virtual port used for CreateListenSocketP2P

        // State
        private bool isDisposed = false;
        private bool isServer = false;
        private bool isClient = false;
        private bool isConnected = false;
        private GONetTransportClientState clientState = GONetTransportClientState.Disconnected;

        // P2P listen socket recovery (SDR can be "Attempting" when StartServer is called)
        private int p2pListenSocketRetryCountdownUpdates = 0;
        private int p2pListenSocketRetryAttempts = 0;
        private bool hasLoggedP2PListenSocketRetry = false;
        private const int P2P_LISTEN_SOCKET_RETRY_INTERVAL_UPDATES = 30; // ~0.5s at 60fps
        private const int P2P_LISTEN_SOCKET_RETRY_MAX_ATTEMPTS = 120; // ~60s at 60fps

        /// <summary>
        /// Current client connection state (for UI display).
        /// </summary>
        public GONetTransportClientState ClientState => clientState;

        /// <summary>
        /// Steam P2P listen virtual port for this transport instance (0 = main server, 1 = dormant server).
        /// Must be set BEFORE calling <see cref="StartServer(int, int)"/>.
        /// </summary>
        public int ListenVirtualPort
        {
            get => listenVirtualPort;
            set => listenVirtualPort = value;
        }

        public ulong LocalSteamId
        {
            get
            {
                try
                {
                    return SteamUser.GetSteamID().m_SteamID;
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Ensure this server transport is listening for Steam P2P connections on the specified virtual port.
        /// This is used during hot-standby failover promotion: the promoted dormant server may already be
        /// listening on virtual port 1 (standby mesh) and needs to ALSO listen on virtual port 0 (gameplay)
        /// for new joiners.
        /// </summary>
        public bool EnsureP2PListenSocket(int virtualPort)
        {
            if (virtualPort < 0)
            {
                GONetLog.Warning($"[SteamworksTransport] EnsureP2PListenSocket ignored for invalid virtualPort={virtualPort}");
                return false;
            }

            lock (listenSocketLock)
            {
                if (p2pListenSocketsByVirtualPort.TryGetValue(virtualPort, out HSteamListenSocket existing) &&
                    existing != HSteamListenSocket.Invalid)
                {
                    return true;
                }
            }

            if (!isServer)
            {
                GONetLog.Warning($"[SteamworksTransport] Cannot create P2P listen socket (virtualPort={virtualPort}) - server is not running");
                return false;
            }

            HSteamListenSocket socket = SteamNetworkingSockets.CreateListenSocketP2P(virtualPort, 0, null);
            if (socket == HSteamListenSocket.Invalid)
            {
                GONetLog.Warning($"[SteamworksTransport] CreateListenSocketP2P failed (virtualPort={virtualPort})");
                return false;
            }

            lock (listenSocketLock)
            {
                p2pListenSocketsByVirtualPort[virtualPort] = socket;

                // Preserve legacy field behavior: keep at least one valid P2P listen socket handle.
                if (listenSocketP2P == HSteamListenSocket.Invalid)
                {
                    listenSocketP2P = socket;
                }
            }

            GONetLog.Info($"[SteamworksTransport] Created P2P listen socket (virtualPort={virtualPort})");
            return true;
        }

        // Thread-safe event queue (Steam callbacks → GONet events)
        private readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();

        // Steam callbacks
        private Callback<SteamNetConnectionStatusChangedCallback_t> connectionStatusCallback;

        // Connection ID counter (for IGONetTransportConnection.ConnectionUID)
        private ulong nextConnectionUID = 1;

        // Message buffer pool (zero-allocation optimization)
        // Note: Message buffer pooling removed - Steam handles message lifecycle internally
        

        // Statistics tracking
        private float rttMilliseconds = 0f;
        private float packetLoss = 0f;
        private float sentBandwidthKBPS = 0f;
        private float receivedBandwidthKBPS = 0f;

        // REMOVED: 15-second warmup queueing (2025-11-07)
        // Queueing was causing message bursts that flooded Steam's send buffer.
        // With the subscription fix in place (GONetConnections.cs:163), messages should flow normally.

        // IL2CPP-compatible static callback for Steam debug output
        // IMPORTANT: Must be static method, NOT lambda/instance method, for IL2CPP marshaling
        // Note: Signature must match FSteamNetworkingSocketsDebugOutput delegate (StringBuilder, not string)
        [AOT.MonoPInvokeCallback(typeof(FSteamNetworkingSocketsDebugOutput))]
        private static void SteamDebugOutputCallback(ESteamNetworkingSocketsDebugOutputType type, System.Text.StringBuilder msg)
        {
            // Only log important messages (warnings, errors, bugs) - filter out verbose debug spam
            if (type == ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Warning ||
                type == ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Error ||
                type == ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Bug)
            {
                string msgStr = msg?.ToString() ?? "";

                // Additional filtering: Skip common verbose messages even if marked as warnings
                if (msgStr.Contains("Send Nagle") ||
                    msgStr.Contains("QueueTime") ||
                    msgStr.Contains("SendRate"))
                {
                    return; // Skip verbose packet-level spam
                }

                // Log important messages with appropriate level
                if (type == ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Error ||
                    type == ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Bug)
                {
                    GONetLog.Error($"[Steam] {msgStr}");
                }
                else
                {
                    //GONetLog.Warning($"[Steam] {msgStr}"); // COMMENTED - spam (~228 logs/run)
                }
            }
            // Silently ignore Debug/Verbose/Msg/Everything types
        }

        #endregion

        #region Steam Network Configuration

        /// <summary>
        /// Creates optimized SteamNetworkingConfigValue_t array for connection/listen sockets.
        /// Based on community research and Steam documentation for high-frequency real-time multiplayer.
        ///
        /// Key optimizations:
        /// - Larger send/receive buffers (prevents overflow during bursts)
        /// - Nagle algorithm disabled (immediate transmission, no message coalescing)
        /// - Tuned for 60-125 msg/sec gameplay traffic
        /// </summary>
        private SteamNetworkingConfigValue_t[] CreateOptimizedConnectionConfig()
        {
            var config = new List<SteamNetworkingConfigValue_t>();

            // 1. Increase send buffer to 4 MB (from 512 KB default)
            // Prevents buffer overflow when sending bursts of messages
            // Essential for server sending to multiple clients simultaneously
            config.Add(new SteamNetworkingConfigValue_t
            {
                m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                m_val = new SteamNetworkingConfigValue_t.OptionValue { m_int32 = 4 * 1024 * 1024 } // 4 MB
            });

            // 2. Increase receive buffer to 8 MB (from ~4 MB default)
            // Prevents receive queue overflow if ReceiveMessagesOnConnection can't drain fast enough
            // Larger than send buffer because server receives from multiple clients
            config.Add(new SteamNetworkingConfigValue_t
            {
                m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferSize,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                m_val = new SteamNetworkingConfigValue_t.OptionValue { m_int32 = 8 * 1024 * 1024 } // 8 MB
            });

            // 3. Disable Nagle algorithm entirely (set NagleTime to 0)
            // Default is 5000 microseconds (5 ms), which buffers small messages
            // For real-time games, we want immediate transmission (use NoNagle flag + this config)
            config.Add(new SteamNetworkingConfigValue_t
            {
                m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                m_val = new SteamNetworkingConfigValue_t.OptionValue { m_int32 = 0 } // 0 microseconds (disabled)
            });

            // 4. CRITICAL: Allow connections without authentication for local IP-based development
            // When using DirectIP (127.0.0.1), certificate validation can fail if Steam isn't fully initialized
            // or if testing with the same Steam account on multiple processes
            // This is SAFE for local development - still uses encryption, just skips cert validation
            config.Add(new SteamNetworkingConfigValue_t
            {
                m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_IP_AllowWithoutAuth,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                m_val = new SteamNetworkingConfigValue_t.OptionValue { m_int32 = 2 } // 2 = allow all connections without cert (for local dev)
            });

            return config.ToArray();
        }

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
        /// For Steamworks, this provides the ACCURATE receive timestamp from
        /// <c>SteamNetworkingMessage_t.m_usecTimeReceived</c>, which is when Steam's
        /// networking layer actually received the packet (NOT when we processed it).
        ///
        /// <para>
        /// This timestamp is critical for accurate RTT calculations during high-load
        /// scenarios where there can be 50-500ms between Steam receiving a packet
        /// and our code processing it.
        /// </para>
        /// </summary>
        public event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte, long> OnMessageReceivedWithTimestamp;

        #endregion

        #region Lifecycle

        public SteamworksTransport()
        {
            // No initialization needed - Steam handles message lifecycle
        }

        public void Initialize(GONetTransportConfig config)
        {
            this.config = config ?? GONetTransportConfig.CreateDefault();

            // Verify Steam API is initialized
            // Check if SteamAPI.Init() was called (typically by GONetSteamManager component)
            if (!GONetSteamManager.IsInitialized)
            {
                throw new InvalidOperationException(
                    "SteamworksTransport: Steamworks is not initialized.\n\n" +
                    "SOLUTION:\n" +
                    "1. Add GONetSteamManager component to GONetGlobal GameObject (it calls SteamAPI.Init() automatically)\n" +
                    "2. Ensure Steam client is running\n" +
                    "3. Verify steam_appid.txt exists in project root with GONet App ID 4168160\n\n" +
                    "GONetSteamManager handles SteamAPI.Init() and SteamAPI.RunCallbacks() lifecycle."
                );
            }

            // Register Steam callbacks
            connectionStatusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnSteamNetConnectionStatusChanged);

            // Try to check Steam Datagram Relay availability (may fail if Steam not fully initialized)
            string sdrStatus = "UNKNOWN";
            try
            {
                sdrStatus = SteamNetworkingUtils.GetRelayNetworkStatus(out _) == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current ? "YES" : "NO";
            }
            catch
            {
                // Steam not fully initialized - ignore
            }
            GONetLog.Info($"SteamworksTransport: Initialized (Steam Datagram Relay available: {sdrStatus})");
        }

        public void Shutdown()
        {
            StopServer();
            DisconnectClient();

            // Unregister callbacks
            connectionStatusCallback?.Dispose();
            connectionStatusCallback = null;
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

        private bool IsOurListenSocket(HSteamListenSocket socket)
        {
            if (socket == HSteamListenSocket.Invalid)
            {
                return false;
            }

            if (socket == listenSocketIP || socket == listenSocketP2P)
            {
                return true;
            }

            lock (listenSocketLock)
            {
                foreach (var kvp in p2pListenSocketsByVirtualPort)
                {
                    if (kvp.Value == socket)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void CloseAllP2PListenSockets()
        {
            List<HSteamListenSocket> socketsToClose = null;

            lock (listenSocketLock)
            {
                if (p2pListenSocketsByVirtualPort.Count > 0)
                {
                    socketsToClose = new List<HSteamListenSocket>(p2pListenSocketsByVirtualPort.Values);
                    p2pListenSocketsByVirtualPort.Clear();
                }

                listenSocketP2P = HSteamListenSocket.Invalid;
            }

            if (socketsToClose == null)
            {
                return;
            }

            foreach (HSteamListenSocket socket in socketsToClose)
            {
                if (socket != HSteamListenSocket.Invalid)
                {
                    SteamNetworkingSockets.CloseListenSocket(socket);
                }
            }
        }

        #region Server-Side Operations

        public void StartServer(int port, int maxConnections)
        {
            if (isServer)
            {
                GONetLog.Warning("SteamworksTransport: Server already started");
                return;
            }

            try
            {
                // Reset retry state for this run
                p2pListenSocketRetryCountdownUpdates = 0;
                p2pListenSocketRetryAttempts = 0;
                hasLoggedP2PListenSocketRetry = false;

                // DIAGNOSTIC: Log Steam networking state
                GONetLog.Info($"[SteamworksTransport] Attempting to start P2P server...");
                GONetLog.Info($"[SteamworksTransport] Steam User: {SteamUser.GetSteamID()}");
                GONetLog.Info($"[SteamworksTransport] App ID: {SteamUtils.GetAppID()}");

                // Enable Steam networking debug output (warnings and errors only - filters out verbose spam)
                // IMPORTANT: Use static method (not lambda) for IL2CPP compatibility
                // Use k_ESteamNetworkingSocketsDebugOutputType_Msg instead of Everything to reduce spam
                SteamNetworkingUtils.SetDebugOutputFunction(
                    ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Msg,
                    SteamDebugOutputCallback);

                // NOTE: SDR wait is now handled in GONetConnectionWizard.WaitForSteamSDRReady()
                // This prevents blocking the Unity main thread and allows UI animation during startup
                // If you're starting server programmatically (not via wizard), you should wait for SDR yourself

                // Log current SDR state for debugging
                SteamRelayNetworkStatus_t status;
                ESteamNetworkingAvailability sdrAvailability = SteamNetworkingUtils.GetRelayNetworkStatus(out status);
                GONetLog.Info($"[SteamworksTransport] Current SDR Availability: {sdrAvailability}");
                GONetLog.Info($"[SteamworksTransport] SDR Status Details: Avail={status.m_eAvail}, NetworkConfig={status.m_eAvailNetworkConfig}, AnyRelay={status.m_eAvailAnyRelay}");

                if (sdrAvailability != ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current)
                {
                    GONetLog.Warning($"[SteamworksTransport] SDR is not ready yet (status: {sdrAvailability}). P2P socket may fail and fall back to IP.");
                }

                // Create BOTH socket types to accept both P2P and Direct IP connections
                // 1. P2P socket (for Steam ID-based connections via ConnectP2P)
                // Parameters: (virtualPort, numOptions, optionsArray)
                listenSocketP2P = SteamNetworkingSockets.CreateListenSocketP2P(listenVirtualPort, 0, null);

                if (listenSocketP2P != HSteamListenSocket.Invalid)
                {
                    lock (listenSocketLock)
                    {
                        p2pListenSocketsByVirtualPort[listenVirtualPort] = listenSocketP2P;
                    }
                    GONetLog.Info($"[SteamworksTransport] Successfully created P2P listen socket (virtualPort={listenVirtualPort}, for Steam ID connections)");
                }
                else
                {
                    GONetLog.Warning($"[SteamworksTransport] CreateListenSocketP2P failed (virtualPort={listenVirtualPort}) - Steam P2P connections will NOT work");
                }

                // 2. IP socket (for Direct IP connections via ConnectByIPAddress)
                // This is SEPARATE from P2P and required for Direct IP connections
                // IMPORTANT: If a specific port is requested, do NOT fall back to other ports.
                // Hot standby and connection presets assume deterministic binding.
                int[] portsToTry = port > 0
                    ? new[] { port }
                    : new[] { 27015, 7777, 8888 }; // common game server ports
                bool ipSocketCreated = false;

                foreach (int tryPort in portsToTry)
                {
                    GONetLog.Info($"[SteamworksTransport] Attempting CreateListenSocketIP on port {tryPort}...");

                    // Create IP-based socket for Direct IP connections
                    // This still uses Steam networking stack (encryption, reliability) but binds to IP:port
                    var address = new SteamNetworkingIPAddr();
                    address.Clear();
                    address.SetIPv4(0, (ushort)tryPort); // 0.0.0.0:port (bind to all interfaces)

                    // Apply optimized configuration (larger buffers, Nagle disabled)
                    var connectionConfig = CreateOptimizedConnectionConfig();
                    listenSocketIP = SteamNetworkingSockets.CreateListenSocketIP(ref address, connectionConfig.Length, connectionConfig);

                    if (listenSocketIP != HSteamListenSocket.Invalid)
                    {
                        GONetLog.Info($"[SteamworksTransport] Successfully created IP listen socket on port {tryPort} (for Direct IP connections)");
                        ipSocketCreated = true;
                        break;
                    }
                    else
                    {
                        GONetLog.Warning($"[SteamworksTransport] CreateListenSocketIP failed on port {tryPort}, trying next port...");
                    }
                }

                if (!ipSocketCreated)
                {
                    GONetLog.Warning($"[SteamworksTransport] CreateListenSocketIP failed on all ports - Direct IP connections will NOT work");
                }

                // Check if at least ONE socket type succeeded
                if (listenSocketP2P == HSteamListenSocket.Invalid && listenSocketIP == HSteamListenSocket.Invalid)
                {
                    string portsTried = string.Join(", ", portsToTry);
                    throw new Exception(
                        $"Failed to create any listen socket - BOTH P2P and IP sockets failed.\n\n" +
                        "TRIED:\n" +
                        "1. CreateListenSocketP2P (failed - possibly App ID restrictions or SDR not ready)\n" +
                        $"2. CreateListenSocketIP on ports: {portsTried} (all failed)\n\n" +
                        "POSSIBLE CAUSES:\n" +
                        "- All tested ports already in use or blocked by firewall\n" +
                        "- Steam App ID may have network restrictions\n" +
                        "- SDR not initialized (required for P2P)\n\n" +
                        $"SDR Status: {sdrAvailability}\n" +
                        $"Steam ID: {SteamUser.GetSteamID()}\n" +
                        $"App ID: {SteamUtils.GetAppID()}\n\n" +
                        "NEXT STEPS:\n" +
                        "- Check firewall settings / port availability\n" +
                        "- Wait for SDR to initialize before starting server\n" +
                        "- Try running Unity Editor as Administrator"
                    );
                }

                // Log final socket status
                string socketStatus = "";
                if (listenSocketP2P != HSteamListenSocket.Invalid && listenSocketIP != HSteamListenSocket.Invalid)
                {
                    socketStatus = "BOTH Steam P2P and Direct IP connections accepted";
                }
                else if (listenSocketP2P != HSteamListenSocket.Invalid)
                {
                    socketStatus = "ONLY Steam P2P connections accepted (Direct IP failed)";
                }
                else if (listenSocketIP != HSteamListenSocket.Invalid)
                {
                    socketStatus = "ONLY Direct IP connections accepted (P2P failed)";
                }
                GONetLog.Info($"[SteamworksTransport] Server socket status: {socketStatus}");

                // Create poll group for efficient message polling
                pollGroup = SteamNetworkingSockets.CreatePollGroup();

                if (pollGroup == HSteamNetPollGroup.Invalid)
                {
                    // Clean up both sockets if poll group creation fails
                    CloseAllP2PListenSockets();
                    if (listenSocketIP != HSteamListenSocket.Invalid)
                    {
                        SteamNetworkingSockets.CloseListenSocket(listenSocketIP);
                        listenSocketIP = HSteamListenSocket.Invalid;
                    }
                    throw new Exception("Failed to create poll group");
                }

                isServer = true;
                serverPort = port; // Cache for lock file management

                // Create lock file for auto-detection
                GONet.Utils.NetworkUtils.CreateServerLockFile(port);

                GONetLog.Info($"SteamworksTransport: Server started (Steam ID: {SteamUser.GetSteamID()}, max clients: {maxConnections})");
            }
            catch (Exception ex)
            {
                GONetLog.Error($"SteamworksTransport: Failed to start server - {ex.Message}");
                throw;
            }
        }

        public void StopServer()
        {
            if (!isServer)
                return;

            // Close all client connections
            var connectionsToClose = new List<HSteamNetConnection>(serverConnections.Keys);
            foreach (var conn in connectionsToClose)
            {
                SteamNetworkingSockets.CloseConnection(conn, (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic, "Server shutdown", false);
            }
            serverConnections.Clear();

            // Close poll group
            if (pollGroup != HSteamNetPollGroup.Invalid)
            {
                SteamNetworkingSockets.DestroyPollGroup(pollGroup);
                pollGroup = HSteamNetPollGroup.Invalid;
            }

            CloseAllP2PListenSockets();

            if (listenSocketIP != HSteamListenSocket.Invalid)
            {
                SteamNetworkingSockets.CloseListenSocket(listenSocketIP);
                listenSocketIP = HSteamListenSocket.Invalid;
            }

            isServer = false;

            // Reset retry state so a subsequent StartServer has a clean slate
            p2pListenSocketRetryCountdownUpdates = 0;
            p2pListenSocketRetryAttempts = 0;
            hasLoggedP2PListenSocketRetry = false;

            // Remove lock file
            if (serverPort > 0)
            {
                GONet.Utils.NetworkUtils.RemoveServerLockFile(serverPort);
                serverPort = -1;
            }

            GONetLog.Info("SteamworksTransport: Server stopped");
        }

        public void DisconnectConnection(IGONetTransportConnection connection, GONetTransportDisconnectReason reason)
        {
            if (connection is SteamworksConnection steamConnection)
            {
                HSteamNetConnection handle = steamConnection.ConnectionHandle;

                if (serverConnections.ContainsKey(handle))
                {
                    SteamNetworkingSockets.CloseConnection(handle, (int)MapDisconnectReasonToSteamEnd(reason), reason.ToString(), false);
                    serverConnections.Remove(handle);

                    // Fire disconnect event
                    EnqueueMainThreadAction(() => OnServerClientDisconnected?.Invoke(connection, reason));
                }
            }
        }

        #endregion

        #region Client-Side Operations

        public void ConnectClient(string address, int port, int timeoutSeconds, byte[] authData = null)
        {
            if (isClient)
            {
                GONetLog.Warning("SteamworksTransport: Client already connecting or connected");
                return;
            }

            try
            {
                // Determine if address is Steam ID (numeric) or IP address (contains dots/colons)
                bool isIPAddress = address.Contains(".") || address.Contains(":");

                if (isIPAddress)
                {
                    // IP-based connection (for local development or IP-based servers)
                    GONetLog.Info($"[SteamworksTransport] Connecting to IP address: {address}:{port}");

                    var serverAddress = new SteamNetworkingIPAddr();
                    serverAddress.Clear();

                    // Parse IP address
                    if (System.Net.IPAddress.TryParse(address, out var ipAddr))
                    {
                        if (ipAddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            // IPv4
                            byte[] bytes = ipAddr.GetAddressBytes();
                            uint ipv4 = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
                            serverAddress.SetIPv4(ipv4, (ushort)port);
                        }
                        else
                        {
                            throw new ArgumentException($"IPv6 not yet supported in SteamworksTransport: {address}");
                        }
                    }
                    else
                    {
                        throw new ArgumentException($"Invalid IP address format: {address}");
                    }

                    // Connect via IP with optimized configuration (larger buffers, Nagle disabled)
                    var connectionConfig = CreateOptimizedConnectionConfig();
                    clientConnection = SteamNetworkingSockets.ConnectByIPAddress(ref serverAddress, connectionConfig.Length, connectionConfig);

                    if (clientConnection == HSteamNetConnection.Invalid)
                    {
                        throw new Exception("Failed to connect (ConnectByIPAddress returned Invalid)");
                    }

                    isClient = true;
                    SetClientState(GONetTransportClientState.Connecting);

                    GONetLog.Info($"SteamworksTransport: Connecting to server (IP: {address}:{port})");
                }
                else
                {
                    // Steam ID-based P2P connection
                    if (!ulong.TryParse(address, out ulong steamIdRaw))
                    {
                        throw new ArgumentException($"Invalid Steam ID: {address}. Expected 64-bit unsigned integer or IP address.");
                    }

                    CSteamID targetSteamID = new CSteamID(steamIdRaw);

                    // Create identity for target server
                    SteamNetworkingIdentity serverIdentity = new SteamNetworkingIdentity();
                    serverIdentity.SetSteamID(targetSteamID);

                    // Connect via P2P
                    // Backward compatibility: historically, GONet passed an OS port here (e.g., 7777) even for P2P.
                    // For hot-standby mesh we need virtual port 1; for normal gameplay connections we use virtual port 0.
                    int virtualPort = (port == 0 || port == 1) ? port : 0;
                    clientConnection = SteamNetworkingSockets.ConnectP2P(ref serverIdentity, virtualPort, 0, null);

                    if (clientConnection == HSteamNetConnection.Invalid)
                    {
                        throw new Exception("Failed to connect (ConnectP2P returned Invalid)");
                    }

                    isClient = true;
                    SetClientState(GONetTransportClientState.Connecting);

                    GONetLog.Info($"SteamworksTransport: Connecting to server (Steam ID: {targetSteamID}, virtualPort={virtualPort})");
                }
            }
            catch (Exception ex)
            {
                GONetLog.Error($"SteamworksTransport: Failed to connect - {ex.Message}");
                SetClientState(GONetTransportClientState.Error);
                throw;
            }
        }

        public void DisconnectClient()
        {
            if (!isClient)
                return;

            if (clientConnection != HSteamNetConnection.Invalid)
            {
                // Clean up queuing state
                SteamNetworkingSockets.CloseConnection(clientConnection, (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic, "Client disconnect", false);
                clientConnection = HSteamNetConnection.Invalid;
            }

            isClient = false;
            isConnected = false;
            myClientConnection = null;
            SetClientState(GONetTransportClientState.Disconnected);

            GONetLog.Info("SteamworksTransport: Client disconnected");
        }

        #endregion

        #region Data Transmission

        /// <summary>
        /// Best-effort extraction of the inner GONet channel + message type from a ReliableNetcode packet.
        /// This is used for diagnostics only (mesh handshake debugging) and should not be used for gameplay logic.
        /// </summary>
        private static bool TryExtractDistributedHostMessageTypeFromReliableNetcodePacket(byte[] packet, int packetLength, out byte gonetChannelId, out byte messageType)
        {
            gonetChannelId = 0;
            messageType = 0;

            if (packet == null || packetLength < 8 || packetLength > packet.Length)
            {
                return false;
            }

            // ReliableNetcode packet header always begins with [prefixByte][channelId]. ChannelId is 0 (Reliable) or 1 (Unreliable).
            byte reliableNetcodeChannelId = packet[1];
            if (reliableNetcodeChannelId != 0 && reliableNetcodeChannelId != 1)
            {
                return false;
            }

            try
            {
                int headerBytes = PacketIO.ReadPacketHeader(packet, 0, packetLength, out _, out _, out _, out _, out _);
                int offset = headerBytes;

                // ReliableMessageChannel packet payload begins with: [messageSeq(ushort)][messageLen(varushort)][messageBytes...]
                // We only need to look at the first message for handshake diagnostics.
                if (offset + 3 > packetLength)
                {
                    return false;
                }

                offset += 2; // messageSeq

                byte b1 = packet[offset++];
                ushort messageLength = (ushort)(b1 & 0x7F);
                if ((b1 & 0x80) != 0)
                {
                    if (offset >= packetLength)
                    {
                        return false;
                    }
                    messageLength |= (ushort)(packet[offset++] << 7);
                }

                // Inner message bytes are: [gonetChannelId(1)][bodySize(4)][payload...]
                if (messageLength < 6 || offset + messageLength > packetLength)
                {
                    return false;
                }

                gonetChannelId = packet[offset];
                if (gonetChannelId != GONetChannel.DistributedHost_Reliable.Id &&
                    gonetChannelId != GONetChannel.DistributedHost_Unreliable.Id)
                {
                    return false;
                }

                messageType = packet[offset + 5];
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Send(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection target = null, byte channel = 0)
        {
            // NOTE: Channel parameter exists for interface compatibility, but is NOT used here
            // GONetConnections already encodes the channel ID in the payload header when transport has reliability
            // (see GONetConnections.cs lines 238-241: prepends channelId + size before calling transport.Send())
            // Steamworks just sends the payload as-is, GONetConnections will extract the channel on receive

            // MESH HANDSHAKE DIAGNOSTIC: In Steamworks mode we wrap reliability via ReliableNetcode, so transport-level
            // packets are ReliableNetcode datagrams. Extract the inner DistributedHost message type for logging.
            if (isServer && TryExtractDistributedHostMessageTypeFromReliableNetcodePacket(data, length, out byte gonetChannelId, out byte msgType) &&
                msgType >= 30 && msgType <= 40)
            {
                ulong targetUID = (target as SteamworksConnection)?.ConnectionUID ?? 0;
                HSteamNetConnection targetHandle = (target as SteamworksConnection)?.ConnectionHandle ?? HSteamNetConnection.Invalid;
                GONetLog.Warning($"[MESH-TX-DIAG] Server sending standby msg: type={msgType}, gonetChannel={gonetChannelId}, length={length}, " +
                    $"targetUID={targetUID}, targetHandle={targetHandle.m_HSteamNetConnection}, qos={qos}");
            }

            int nSendFlags = MapQoSToSteamSendFlags(qos);

            if (isServer)
            {
                // Server → Client send
                if (target is SteamworksConnection steamTarget)
                {
                    SendToConnection(steamTarget.ConnectionHandle, data, length, nSendFlags);
                }
                else if (target == null && isClient && isConnected)
                {
                    // HOST MODE: Internal client sending to local server with target=null.
                    // The transport is both server and client - route via client connection handle.
                    SendToConnection(clientConnection, data, length, nSendFlags);
                }
                else
                {
                    //GONetLog.Warning("SteamworksTransport.Send: Invalid target connection (server-side)");
                }
            }
            else if (isClient && isConnected)
            {
                // Client → Server send
                SendToConnection(clientConnection, data, length, nSendFlags);
            }
            else
            {
                //GONetLog.Warning("SteamworksTransport.Send: Not connected");
            }
        }

        public void Broadcast(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection excludeConnection = null, byte channel = 0)
        {
            if (!isServer)
            {
                GONetLog.Warning("SteamworksTransport.Broadcast: Only server can broadcast");
                return;
            }

            // NOTE: Channel parameter exists for interface compatibility, but is NOT used here
            // GONetConnections already encodes the channel ID in the payload header when transport has reliability
            // Steamworks just broadcasts the payload as-is

            int nSendFlags = MapQoSToSteamSendFlags(qos);

            HSteamNetConnection excludeHandle = HSteamNetConnection.Invalid;
            if (excludeConnection is SteamworksConnection steamExclude)
            {
                excludeHandle = steamExclude.ConnectionHandle;
            }

            foreach (var kvp in serverConnections)
            {
                if (kvp.Key != excludeHandle)
                {
                    SendToConnection(kvp.Key, data, length, nSendFlags);
                }
            }
        }

        public void Update()
        {
            // Process main thread action queue (Steam callbacks → GONet events)
            while (mainThreadActions.TryDequeue(out Action action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    GONetLog.Error($"SteamworksTransport.Update: Exception in main thread action - {ex}");
                }
            }

            // Poll for incoming messages
            // IMPORTANT: A single SteamworksTransport instance may be used as BOTH server and client (HOST mode or
            // when virtual-port multiplexing is used). In that case we must poll BOTH sides; using else-if would
            // starve the client receive path and break hot-standby mesh handshakes.
            if (isServer)
            {
                ReceiveMessagesOnPollGroup();
            }
            if (isClient && clientConnection != HSteamNetConnection.Invalid)
            {
                ReceiveMessagesOnConnection(clientConnection);
            }

            TryRecoverP2PListenSocketIfNeeded();

            // Update network statistics
            UpdateNetworkStats();

            // Keep lock file fresh if we're running as server
            if (isServer && serverPort > 0)
            {
                GONet.Utils.NetworkUtils.UpdateServerLockFile(serverPort);
            }
        }

        private void TryRecoverP2PListenSocketIfNeeded()
        {
            if (!isServer)
            {
                return;
            }

            if (listenVirtualPort < 0)
            {
                return;
            }

            if (listenSocketP2P != HSteamListenSocket.Invalid)
            {
                return;
            }

            if (p2pListenSocketRetryAttempts >= P2P_LISTEN_SOCKET_RETRY_MAX_ATTEMPTS)
            {
                return;
            }

            if (p2pListenSocketRetryCountdownUpdates > 0)
            {
                p2pListenSocketRetryCountdownUpdates--;
                return;
            }

            p2pListenSocketRetryCountdownUpdates = P2P_LISTEN_SOCKET_RETRY_INTERVAL_UPDATES;
            p2pListenSocketRetryAttempts++;

            ESteamNetworkingAvailability availability;
            try
            {
                availability = SteamNetworkingUtils.GetRelayNetworkStatus(out _);
            }
            catch
            {
                return;
            }

            if (availability != ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current)
            {
                if (!hasLoggedP2PListenSocketRetry)
                {
                    hasLoggedP2PListenSocketRetry = true;
                    GONetLog.Info($"[SteamworksTransport] SDR not ready yet ({availability}); will retry CreateListenSocketP2P in background");
                }
                return;
            }

            if (EnsureP2PListenSocket(listenVirtualPort))
            {
                GONetLog.Info($"[SteamworksTransport] SDR ready - recovered P2P listen socket (virtualPort={listenVirtualPort})");
            }
            else
            {
                // If SDR is ready and CreateListenSocketP2P still fails, repeated retries usually just spam logs.
                // Treat as non-recoverable for this run (Direct IP can still work).
                p2pListenSocketRetryAttempts = P2P_LISTEN_SOCKET_RETRY_MAX_ATTEMPTS;
            }
        }

        public bool IsServerRunningLocally(int port)
        {
            // Use lock file mechanism (works for both Steamworks and NetcodeIO)
            return GONet.Utils.NetworkUtils.IsLocalPortListening(port);
        }

        #endregion

        #region Steam Callbacks

        private void OnSteamNetConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            // MULTI-INSTANCE SAFE: When multiple SteamworksTransport instances exist (e.g., main server +
            // dormant server for hot standby), Steam fires callbacks to ALL registered handlers.
            // We must filter to only process callbacks for OUR connections.
            //
            // Connection ownership is determined by:
            // - SERVER-side: m_hListenSocket matches our listen socket(s)
            // - CLIENT-side: m_hConn matches our clientConnection handle
            // - TRACKED: m_hConn exists in our serverConnections dictionary
            HSteamListenSocket callbackListenSocket = callback.m_info.m_hListenSocket;
            HSteamNetConnection callbackConn = callback.m_hConn;

            // Check if this is a server-side callback (has a listen socket)
            if (callbackListenSocket != HSteamListenSocket.Invalid)
            {
                bool isOurListenSocket = IsOurListenSocket(callbackListenSocket);
                if (!isOurListenSocket)
                {
                    // This callback is for a different SteamworksTransport instance's listen socket - ignore
                    return;
                }
                // It's our listen socket - process as server event
                HandleServerConnectionStatusChanged(callback);
                return;
            }

            // No listen socket = client-side callback OR disconnect for a tracked connection
            // Check if it's our outgoing client connection
            if (isClient && clientConnection != HSteamNetConnection.Invalid && callbackConn == clientConnection)
            {
                HandleClientConnectionStatusChanged(callback);
                return;
            }

            // Check if it's a connection we're tracking (e.g., disconnect for a server connection we accepted)
            if (isServer && serverConnections.ContainsKey(callbackConn))
            {
                HandleServerConnectionStatusChanged(callback);
                return;
            }

            // Not our connection - ignore
            // This happens when another transport instance's client connection fires a callback
        }

        private void HandleServerConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            ESteamNetworkingConnectionState oldState = callback.m_eOldState;
            ESteamNetworkingConnectionState newState = callback.m_info.m_eState;
            HSteamNetConnection conn = callback.m_hConn;

            switch (newState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    // New client attempting to connect - approve or reject
                    HandleServerConnectionRequest(callback);
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    // Client successfully connected
                    HandleServerClientConnected(callback);
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    // Client disconnected
                    HandleServerClientDisconnected(callback);
                    break;
            }
        }

        private void HandleServerConnectionRequest(SteamNetConnectionStatusChangedCallback_t callback)
        {
            HSteamNetConnection conn = callback.m_hConn;
            CSteamID remoteSteamID = callback.m_info.m_identityRemote.GetSteamID();

            // Create connection request for approval
            var request = new SteamworksConnectionRequest(conn, remoteSteamID, this);

            // Fire approval event
            bool approved = true;
            if (OnServerConnectionRequested != null)
            {
                approved = OnServerConnectionRequested.Invoke(request);
            }

            if (approved && !request.WasRejected)
            {
                // CRITICAL FIX: Don't accept until identity is resolved
                // Steam fires Connecting callbacks before the remote Steam ID is resolved (shows as 0).
                // If we call AcceptConnection() now, it fails with k_EResultInvalidParam.
                // If we then close the connection, the subsequent callback (with resolved ID) fails
                // with k_EResultInvalidState because we already closed it.
                // Fix: Return early and wait for Steam's next callback when identity is available.
                if (!remoteSteamID.IsValid() || remoteSteamID.m_SteamID == 0)
                {
                    GONetLog.Debug($"[SteamworksTransport] Deferring connection acceptance - Steam ID not yet resolved (conn={conn})");
                    return; // Don't close, just wait for next callback with resolved identity
                }

                // Accept connection
                EResult acceptResult = SteamNetworkingSockets.AcceptConnection(conn);
                if (acceptResult != EResult.k_EResultOK)
                {
                    GONetLog.Warning($"SteamworksTransport: Failed to accept connection from {remoteSteamID} (result: {acceptResult})");
                    SteamNetworkingSockets.CloseConnection(conn, (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic, "Accept failed", false);
                }
                else
                {
                    // Add to poll group for efficient polling
                    if (!SteamNetworkingSockets.SetConnectionPollGroup(conn, pollGroup))
                    {
                        GONetLog.Warning($"SteamworksTransport: Failed to add connection {remoteSteamID} to poll group");
                    }
                }
            }
            else
            {
                // Reject connection
                GONetTransportDisconnectReason rejectReason = request.RejectionReason ?? GONetTransportDisconnectReason.AuthenticationFailed;
                SteamNetworkingSockets.CloseConnection(conn, (int)MapDisconnectReasonToSteamEnd(rejectReason), rejectReason.ToString(), false);
                GONetLog.Info($"SteamworksTransport: Connection request from {remoteSteamID} rejected (reason: {rejectReason})");
            }
        }

        /// <summary>
        /// Waits for Steamworks connection to stabilize before proceeding.
        /// CRITICAL: Steamworks reports Connected state before internal send buffers are ready.
        /// Empirical evidence shows messages sent 3-6ms after Connected are DROPPED (even with k_nSteamNetworkingSend_Reliable).
        /// TEST RESULT (2025-11-06): 100ms delay was NOT sufficient - 3 out of 4 messages still dropped at 110ms delay!
        ///
        /// NOTE: GetConnectionRealTimeStatus doesn't help - it returns invalid metrics (ping=0, quality=-1) for ~5 seconds.
        /// </summary>
        /// <param name="conn">Connection handle (for logging/future use)</param>
        /// <param name="onReady">Callback to invoke when connection is stabilized</param>
        private void WaitForConnectionReady(HSteamNetConnection conn, System.Action onReady)
        {
            // Small safety margin for Steam connection stabilization
            // NOTE: This delay should be eliminated entirely by using CreateSocketPair for localhost testing
            const int STABILIZATION_DELAY_MS = 500;

            //GONetLog.Info($"[SteamworksTransport] Connection established for {conn}, waiting {STABILIZATION_DELAY_MS}ms for stabilization..."); // COMMENTED - spammy log (log cleanup)

            // CRITICAL FIX (2025-11-07): Task.Delay().ContinueWith() doesn't work in Unity - no SynchronizationContext!
            // Use background thread + main thread marshalling instead
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                System.Threading.Thread.Sleep(STABILIZATION_DELAY_MS);

                // Marshal callback to main thread
                EnqueueMainThreadAction(() =>
                {
                    //GONetLog.Info($"[SteamworksTransport] Connection {conn} stabilized after {STABILIZATION_DELAY_MS}ms delay - ready for traffic"); // COMMENTED - spammy log (log cleanup)
                    onReady?.Invoke();
                });
            });
        }

        private void HandleServerClientConnected(SteamNetConnectionStatusChangedCallback_t callback)
        {
            HSteamNetConnection conn = callback.m_hConn;
            CSteamID remoteSteamID = callback.m_info.m_identityRemote.GetSteamID();

            // Extract remote IP address for Direct IP connections (used by hot standby for reciprocal connections)
            string remoteIP = null;
            if (!callback.m_info.m_addrRemote.IsIPv6AllZeros())
            {
                // m_addrRemote contains the actual IP:port for Direct IP connections
                remoteIP = SteamNetworkingIPAddrToString(callback.m_info.m_addrRemote);
                GONetLog.Debug($"[SteamworksTransport] Direct IP connection detected, remote address: {remoteIP}");
            }

            // Create connection wrapper with IP address for Direct IP, or just Steam ID for P2P
            SteamworksConnection connection = new SteamworksConnection(conn, remoteSteamID, nextConnectionUID++, remoteIP);
            serverConnections[conn] = connection;

            // MESH HANDSHAKE DIAGNOSTIC: Log the connection handle for later correlation
            GONetLog.Warning($"[MESH-DIAG-STORE] Stored connection in serverConnections: handle={conn.m_HSteamNetConnection}, " +
                $"UID={connection.ConnectionUID}, RemoteAddr={connection.RemoteAddress}, connCount={serverConnections.Count}");

            GONetLog.Info($"SteamworksTransport: Client connected (callback received) - Steam ID: {remoteSteamID}, RemoteAddress: {connection.RemoteAddress}, Connection UID: {connection.ConnectionUID}");

            // CRITICAL FIX (2025-11-07): Fire OnServerClientConnected IMMEDIATELY so GONetConnection can:
            // 1. Subscribe to OnMessageReceived (catch incoming messages)
            // 2. Set up ReliableNetcode wrapper (both send and receive paths)
            //
            // The 500ms "stabilization delay" was preventing subscription setup, causing messages to arrive
            // with 0 subscribers and get dropped. GONet's ReliableNetcode layer handles retransmits,
            // so we don't need transport-level delays.
            GONetLog.Info($"SteamworksTransport: Firing OnServerClientConnected event immediately for Steam ID: {remoteSteamID}");
            OnServerClientConnected?.Invoke(connection);
        }

        private void HandleServerClientDisconnected(SteamNetConnectionStatusChangedCallback_t callback)
        {
            HSteamNetConnection conn = callback.m_hConn;

            if (serverConnections.TryGetValue(conn, out SteamworksConnection connection))
            {
                serverConnections.Remove(conn);

                GONetTransportDisconnectReason reason = MapSteamEndToDisconnectReason(callback.m_info.m_eEndReason);

                // Fire event on main thread
                EnqueueMainThreadAction(() => OnServerClientDisconnected?.Invoke(connection, reason));

                // Log detailed disconnect info including Steam's reason code and description
                ESteamNetConnectionEnd steamEndReason = (ESteamNetConnectionEnd)callback.m_info.m_eEndReason;
                string description = callback.m_info.m_szEndDebug;
                GONetLog.Info($"SteamworksTransport: Client disconnected - Steam ID: {connection.SteamID}, Reason: {reason}, Steam End Reason: {steamEndReason} ({callback.m_info.m_eEndReason}), Description: {description}");
            }

            // Clean up connection
            SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
        }

        private void HandleClientConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            ESteamNetworkingConnectionState newState = callback.m_info.m_eState;

            switch (newState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    // Successfully connected to server
                    isConnected = true;
                    CSteamID remoteSteamID = callback.m_info.m_identityRemote.GetSteamID();

                    // Extract remote IP address for Direct IP connections
                    string remoteIP = null;
                    if (!callback.m_info.m_addrRemote.IsIPv6AllZeros())
                    {
                        remoteIP = SteamNetworkingIPAddrToString(callback.m_info.m_addrRemote);
                    }

                    myClientConnection = new SteamworksConnection(clientConnection, remoteSteamID, nextConnectionUID++, remoteIP);
                    SetClientState(GONetTransportClientState.Connected);

                    GONetLog.Info($"SteamworksTransport: Connected to server (callback received) - Steam ID: {myClientConnection.SteamID}, RemoteAddress: {myClientConnection.RemoteAddress}");

                    // CRITICAL FIX (2025-11-07): Fire OnClientConnected IMMEDIATELY so GONetConnection can:
                    // 1. Subscribe to OnMessageReceived (catch incoming messages)
                    // 2. Set up ReliableNetcode wrapper (both send and receive paths)
                    //
                    // The 500ms "stabilization delay" was preventing subscription setup, causing init messages
                    // from server to arrive with 0 subscribers and get dropped. GONet's ReliableNetcode layer
                    // handles retransmits, so we don't need transport-level delays.
                    GONetLog.Info($"SteamworksTransport: Firing OnClientConnected event immediately");
                    OnClientConnected?.Invoke();
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    // Disconnected from server
                    isConnected = false;
                    GONetTransportDisconnectReason reason = MapSteamEndToDisconnectReason(callback.m_info.m_eEndReason);
                    SetClientState(GONetTransportClientState.Disconnected);

                    EnqueueMainThreadAction(() => OnClientDisconnected?.Invoke(reason));

                    // Log detailed disconnect info including Steam's reason code and description
                    ESteamNetConnectionEnd steamEndReason = (ESteamNetConnectionEnd)callback.m_info.m_eEndReason;
                    string description = callback.m_info.m_szEndDebug;
                    GONetLog.Info($"SteamworksTransport: Disconnected from server - Reason: {reason}, Steam End Reason: {steamEndReason} ({callback.m_info.m_eEndReason}), Description: {description}");

                    // Clean up connection
                    SteamNetworkingSockets.CloseConnection(clientConnection, 0, null, false);
                    clientConnection = HSteamNetConnection.Invalid;
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    SetClientState(GONetTransportClientState.Connecting);
                    break;
            }
        }

        #endregion

        #region Message Handling

        private void SendToConnection(HSteamNetConnection conn, byte[] data, int length, int nSendFlags)
        {
            // Pin the byte array and get IntPtr for Steam API
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr pData = handle.AddrOfPinnedObject();
                EResult result = SteamNetworkingSockets.SendMessageToConnection(conn, pData, (uint)length, nSendFlags, out long messageNumber);

                // Only log errors, not successful sends (too verbose at 60+ msg/sec)
                if (result != EResult.k_EResultOK)
                {
                    if (result == EResult.k_EResultNoConnection)
                    {
                        //GONetLog.Warning($"[SteamworksTransport] TX ✗ No connection (result: {result}, conn={conn})");
                    }
                    else
                    {
                        //GONetLog.Warning($"[SteamworksTransport] TX ✗ Steam rejected: {length} bytes, result={result}, msgNum={messageNumber}");
                    }
                }
                else
                {
                    // Handshake hardening: during connection warmup some platforms can drop or delay the first few packets.
                    // Force an immediate flush for the HotStandby handshake messages only (very low frequency).
                    if (TryExtractDistributedHostMessageTypeFromReliableNetcodePacket(data, length, out _, out byte msgType) &&
                        (msgType == 30 || msgType == 31))
                    {
                        try { SteamNetworkingSockets.FlushMessagesOnConnection(conn); } catch { }
                    }
                }
                // FLUSH REMOVED: Research shows FlushMessagesOnConnection after EVERY send adds high CPU overhead
                // at high send rates (60-125 msg/sec). Instead, we:
                // - Use NoNagle flag on reliable sends for immediate transmission
                // - Set NagleTime=0 in connection config to disable buffering globally
                // - Rely on Steam's internal batching for efficiency
                // Flush should only be used for critical timing (e.g., before long delays)
            }
            finally
            {
                handle.Free();
            }
        }

        private void ReceiveMessagesOnPollGroup()
        {
            // CRITICAL FIX: Drain receive queue in tight loop until empty
            // Same fix as ReceiveMessagesOnConnection - prevents queue overflow

            IntPtr[] messages = new IntPtr[128]; // Larger batch size for efficiency
            int totalReceived = 0;
            int batches = 0;

            // TIGHT LOOP: Keep polling until queue is empty
            while (true)
            {
                int numMessages = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, messages, 128);

                if (numMessages <= 0)
                    break; // Queue empty, done

                batches++;
                totalReceived += numMessages;

                // Process this batch
                for (int i = 0; i < numMessages; i++)
                {
                    if (messages[i] == IntPtr.Zero)
                    {
                        GONetLog.Warning($"[SteamworksTransport] ReceiveMessagesOnPollGroup: Message {i}/{numMessages} has IntPtr.Zero! Steamworks API contract violation!");
                    }
                    else
                    {
                        ProcessReceivedMessage(messages[i], i, numMessages);
                    }
                }

                // Safety: Prevent infinite loop
                if (totalReceived > 10000)
                {
                    GONetLog.Warning($"[SteamworksTransport] Received {totalReceived} messages in one Update() - possible Steam API issue or DoS attack. Breaking receive loop.");
                    break;
                }
            }

            // Log receive statistics (only if messages received and queue was backed up)
            if (totalReceived > 0 && batches > 1)
            {
                GONetLog.Info($"[SteamworksTransport] Drained {totalReceived} messages in {batches} batches (queue was backed up)");
            }
        }

        private void ReceiveMessagesOnConnection(HSteamNetConnection conn)
        {
            // CRITICAL FIX: Drain receive queue in tight loop until empty
            // Research shows this is essential for high-frequency traffic (60-125 msg/sec)
            // Previous implementation only polled once per frame with 32 message limit
            // → Queue overflow at high rates → packet drops
            //
            // New implementation:
            // - Larger batch size (128 messages for efficiency)
            // - Loop until queue empty (prevents overflow)
            // - Safety limit (10,000 messages per Update to prevent infinite loop)

            IntPtr[] messages = new IntPtr[128]; // Larger batch size for efficiency
            int totalReceived = 0;
            int batches = 0;

            // TIGHT LOOP: Keep polling until queue is empty
            while (true)
            {
                int numMessages = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, messages, 128);

                if (numMessages <= 0)
                    break; // Queue empty, done

                batches++;
                totalReceived += numMessages;

                // Process this batch
                for (int i = 0; i < numMessages; i++)
                {
                    if (messages[i] == IntPtr.Zero)
                    {
                        GONetLog.Warning($"[SteamworksTransport] ReceiveMessagesOnConnection: Message {i}/{numMessages} has IntPtr.Zero! Steamworks API contract violation!");
                    }
                    else
                    {
                        ProcessReceivedMessage(messages[i], i, numMessages);
                    }
                }

                // Safety: Prevent infinite loop if Steam API misbehaves or under attack
                if (totalReceived > 10000)
                {
                    GONetLog.Warning($"[SteamworksTransport] Received {totalReceived} messages in one Update() - possible Steam API issue or DoS attack. Breaking receive loop.");
                    break;
                }
            }

            // Log receive statistics (only if queue was backed up)
            // COMMENTED (log cleanup) - fires frequently when receiving multiple batches
            /*if (totalReceived > 0 && batches > 1)
            {
                GONetLog.Info($"[SteamworksTransport] Drained {totalReceived} messages in {batches} batches (queue was backed up)");
            }*/
        }

        // STEAMWORKS SYNC DIAGNOSTIC (Dec 2025): Track received message statistics
        private static int _syncDiag_rxMsgCount = 0;
        private static int _syncDiag_rxBytesTotal = 0;
        private static int _syncDiag_rxLastLogFrame = 0;

        private void ProcessReceivedMessage(IntPtr pMessage, int messageIndex, int totalMessages)
        {
            if (pMessage == IntPtr.Zero)
            {
                GONetLog.Error($"[SteamworksTransport] ProcessReceivedMessage: Skipping message {messageIndex}/{totalMessages} because IntPtr is Zero!");
                return;
            }

            // Get message data
            SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(pMessage);

            int dataSize = message.m_cbSize;
            
            // SYNC DIAGNOSTIC: Track message reception
            _syncDiag_rxMsgCount++;
            _syncDiag_rxBytesTotal += dataSize;
            int currentFrame = UnityEngine.Time.frameCount;
            if (currentFrame - _syncDiag_rxLastLogFrame >= 120) // Log every ~2 seconds
            {
                //GONetLog.Info($"[SYNC-DIAG-RX] SteamworksTransport received: msgs={_syncDiag_rxMsgCount}, totalBytes={_syncDiag_rxBytesTotal}, thisMsg: size={dataSize}, isServer={isServer}");
                _syncDiag_rxLastLogFrame = currentFrame;
                _syncDiag_rxMsgCount = 0;
                _syncDiag_rxBytesTotal = 0;
            }
            byte[] data = new byte[dataSize];

            // RELIABILITY TEST - detect test messages (16 bytes with signature)
            if (dataSize == 16)
            {
                byte type = Marshal.ReadByte(message.m_pData, 0);
                byte sig1 = Marshal.ReadByte(message.m_pData, 1);
                byte sig2 = Marshal.ReadByte(message.m_pData, 2);

                // TEST CODE REMOVED
            }

            Marshal.Copy(message.m_pData, data, 0, dataSize);

            HSteamNetConnection sourceConn = message.m_conn;

            // Determine QoS from message flags (unreliable or reliable)
            // NOTE: Steam doesn't expose flags in received messages, so we default to Reliable for safety
            GONetTransportQoS qos = GONetTransportQoS.Reliable;

            // Determine source connection
            IGONetTransportConnection sourceConnection = null;
            if (isServer && serverConnections.TryGetValue(sourceConn, out SteamworksConnection serverConn))
            {
                sourceConnection = serverConn;
            }

            // MESH HANDSHAKE DIAGNOSTIC: Log when receiving standby messages
            // SteamworksTransport packets are ReliableNetcode datagrams (because Steam reliability is disabled).
            // Extract the inner DistributedHost message type for diagnostics.
            bool isPotentiallyStandbyMessage = TryExtractDistributedHostMessageTypeFromReliableNetcodePacket(data, dataSize, out byte channelId, out byte msgType) &&
                                               msgType >= 30 && msgType <= 40;

            // MESH ACK DIAGNOSTIC: Log when CLIENT receives ANY standby message (mesh client ACK receive path)
            if (!isServer && isClient && isPotentiallyStandbyMessage)
            {
                GONetLog.Warning($"[MESH-RX-DIAG] Client received standby msg: channel={channelId}, type={msgType}, size={dataSize}, " +
                    $"fromHandle={sourceConn.m_HSteamNetConnection}, isConnected={isConnected}, clientHandle={clientConnection.m_HSteamNetConnection}");
            }

            if (isServer && isPotentiallyStandbyMessage)
            {
                bool lookupSuccess = sourceConnection != null;
                int connCount = serverConnections.Count;
//                GONetLog.Warning($"[MESH-DIAG-RX] Server received standby msg: channel={channelId}, type={msgType}, size={dataSize}, " +
//                    $"sourceConnLookup={lookupSuccess}, serverConnCount={connCount}, " +
//                    $"sourceConnHandle={(sourceConn != HSteamNetConnection.Invalid ? sourceConn.m_HSteamNetConnection.ToString() : "INVALID")}, " +
//                    $"subscriberCount={(OnMessageReceived != null ? OnMessageReceived.GetInvocationList().Length : 0)}");
            }

            // NOTE: Channel ID is NOT extracted here - it's embedded in the payload by GONetConnections
            // When transport has reliability, GONetConnections prepends [channelId][size] header to payload
            // (see GONetConnections.cs lines 238-241 for encoding)
            // GONetConnections.OnTransportMessageReceived() will extract the channel from the payload header
            // We pass channel=0 here because the actual channel is in the payload, not the transport parameter
            byte channel = 0;

            // HIGH-LOAD OPTIMIZATION (December 2025): Extract Steam's internal receive timestamp
            // This is when Steam's networking layer ACTUALLY received the packet, NOT when we processed it.
            // During high-load scenarios (scene loads, heavy instantiation), there can be 50-500ms
            // between when Steam receives a packet and when we process it.
            // Using this timestamp for RTT calculations gives accurate network latency.
            long steamReceiveUsec = (long)message.m_usecTimeReceived;
            long transportReceiveTicks = SteamTimeSync.ConvertSteamTimeToGONetTicks(steamReceiveUsec);

            // Fire event on main thread (no per-message logging - too verbose at 60+ msg/sec)
            // CRITICAL ORDER (December 2025): Fire OnMessageReceivedWithTimestamp FIRST so subscribers
            // can capture the accurate transport-level timestamp BEFORE OnMessageReceived processes the message.
            // This enables accurate RTT calculations during high-load scenarios.
            EnqueueMainThreadAction(() =>
            {
                // Fire timestamped event FIRST for subscribers that need accurate arrival time (e.g., time sync)
                OnMessageReceivedWithTimestamp?.Invoke(data, dataSize, qos, sourceConnection, channel, transportReceiveTicks);

                // Then fire standard event (for backward compatibility with code that doesn't need timestamps)
                OnMessageReceived?.Invoke(data, dataSize, qos, sourceConnection, channel);
            });

            // Release message (must use static method with original IntPtr to free native memory)
            SteamNetworkingMessage_t.Release(pMessage);
        }

        #endregion

        #region Network Statistics

        private void UpdateNetworkStats()
        {
            if (isServer)
            {
                // Average stats across all clients
                float totalRTT = 0f;
                float totalLoss = 0f;
                int connCount = serverConnections.Count;

                foreach (var kvp in serverConnections)
                {
                    UpdateConnectionStats(kvp.Key, kvp.Value);
                    totalRTT += kvp.Value.RTTMilliseconds;
                    totalLoss += kvp.Value.PacketLoss;
                }

                if (connCount > 0)
                {
                    rttMilliseconds = totalRTT / connCount;
                    packetLoss = totalLoss / connCount;
                }
            }
            else if (isClient && isConnected && myClientConnection != null)
            {
                UpdateConnectionStats(clientConnection, myClientConnection);
                rttMilliseconds = myClientConnection.RTTMilliseconds;
                packetLoss = myClientConnection.PacketLoss;
            }

            // TODO: Calculate sent/received bandwidth from Steam networking stats
            // This requires tracking bytes sent/received over time windows
        }

        private void UpdateConnectionStats(HSteamNetConnection conn, SteamworksConnection connection)
        {
            // Get detailed connection info from Steam
            SteamNetConnectionRealTimeStatus_t status = new SteamNetConnectionRealTimeStatus_t();
            SteamNetConnectionRealTimeLaneStatus_t laneStatus = new SteamNetConnectionRealTimeLaneStatus_t();

            if (SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref laneStatus) == EResult.k_EResultOK)
            {
                connection.UpdateStats(status.m_nPing, status.m_flOutPacketsPerSec, status.m_flInPacketsPerSec);
            }
        }

        public float RTTMilliseconds => rttMilliseconds;

        public float PacketLoss => packetLoss;

        public float SentBandwidthKBPS => sentBandwidthKBPS;

        public float ReceivedBandwidthKBPS => receivedBandwidthKBPS;

        #endregion

        #region Connection State

        public bool IsServer => isServer;

        public bool IsClient => isClient;

        public bool IsConnected => isConnected;

        #endregion

        #region Capabilities

        public GONetTransportCapabilities Capabilities =>
            GONetTransportCapabilities.P2P |
            GONetTransportCapabilities.Encryption |
            GONetTransportCapabilities.Authentication |
            GONetTransportCapabilities.MultipleListenSockets |
            GONetTransportCapabilities.VirtualPorts |
            // REMOVED: GONetTransportCapabilities.Reliability
            // Steam's reliable channel is broken (0-2% delivery), so we removed it.
            // GONet will now wrap us with ReliabilityLayerAdapter to provide reliability via ReliableNetcode.
            GONetTransportCapabilities.Compression |
            GONetTransportCapabilities.Fragmentation;

        public int GetMaxMessageSize(GONetTransportQoS qos)
        {
            // Even though Steam has Fragmentation capability and could theoretically handle larger messages,
            // we wrap Steamworks with ReliableNetcode for reliability (Steam's reliability is broken).
            // Therefore, we're limited by ReliableNetcode's 16KB fragmentation limit.
            //
            // NOTE: If we ever switch to using Steam's native reliability, we could increase this to ~512KB
            // (Steam P2P max message size), but for now we're constrained by the ReliableNetcode wrapper.
            return 16 * 1024;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Converts a SteamNetworkingIPAddr to a human-readable "ip:port" string.
        /// Steam's ToString() on this struct just returns the type name, not the actual address.
        /// </summary>
        private static string SteamNetworkingIPAddrToString(SteamNetworkingIPAddr addr)
        {
            uint ipv4 = addr.GetIPv4();
            if (ipv4 == 0)
            {
                // IPv6 or invalid - fall back to noting this
                return null;
            }

            // Convert uint to dotted-quad format (network byte order)
            byte b1 = (byte)((ipv4 >> 24) & 0xFF);
            byte b2 = (byte)((ipv4 >> 16) & 0xFF);
            byte b3 = (byte)((ipv4 >> 8) & 0xFF);
            byte b4 = (byte)(ipv4 & 0xFF);

            return $"{b1}.{b2}.{b3}.{b4}:{addr.m_port}";
        }

        private void SetClientState(GONetTransportClientState newState)
        {
            if (clientState != newState)
            {
                clientState = newState;
                EnqueueMainThreadAction(() => OnClientStateChanged?.Invoke(newState));
            }
        }

        private void EnqueueMainThreadAction(Action action)
        {
            mainThreadActions.Enqueue(action);
        }

        private int MapQoSToSteamSendFlags(GONetTransportQoS qos)
        {
            // DESIGN DECISION: Always use Steam unreliable channel
            //
            // Testing showed ISteamNetworkingSockets reliable channel is broken (0-2% delivery)
            // across all configurations. Unreliable achieves 99.9% delivery at 100 msg/sec.
            //
            // GONet's ReliableNetcode layer handles retransmits for reliable channels on top
            // of the unreliable base, giving us working reliability without debugging Steam bugs.
            //
            // This gives us the best of both worlds:
            // - 99.9% base delivery from Steam unreliable (proven in testing)
            // - 100% reliability via GONet's ACK/retransmit for channels that need it

            int flags = Constants.k_nSteamNetworkingSend_Unreliable | Constants.k_nSteamNetworkingSend_NoDelay;

            return flags;
        }

        private int MapDisconnectReasonToSteamEnd(GONetTransportDisconnectReason reason)
        {
            switch (reason)
            {
                case GONetTransportDisconnectReason.UserInitiated:
                    return (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic;
                case GONetTransportDisconnectReason.Timeout:
                    return (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic;
                case GONetTransportDisconnectReason.ServerShutdown:
                    return (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic;
                case GONetTransportDisconnectReason.ServerFull:
                    return (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Max;
                case GONetTransportDisconnectReason.TransportError:
                    return (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Misc_InternalError;
                case GONetTransportDisconnectReason.AuthenticationFailed:
                    return (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Misc_Generic;
                case GONetTransportDisconnectReason.Kicked:
                    return (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic;
                default:
                    return (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic;
            }
        }

        #endregion

        // TEST CODE REMOVED - All reliability test methods removed

        #region Message Queue Flushing

        /// <summary>
        /// Flushes queued messages after warmup period expires.
        /// Called from Update() every frame.
        /// </summary>
        // REMOVED: FlushQueuedMessages() - no longer needed without warmup queueing

        #endregion

        #region Helper Methods

        private GONetTransportDisconnectReason MapSteamEndToDisconnectReason(int endReason)
        {
            switch ((ESteamNetConnectionEnd)endReason)
            {
                case ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic:
                    return GONetTransportDisconnectReason.UserInitiated;
                case ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Max:
                    return GONetTransportDisconnectReason.ServerFull;
                case ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Misc_Timeout:
                    return GONetTransportDisconnectReason.Timeout;
                case ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Misc_InternalError:
                    return GONetTransportDisconnectReason.TransportError;
                default:
                    return GONetTransportDisconnectReason.TransportError;
            }
        }

        #endregion
    }

    #region SteamworksConnection

    /// <summary>
    /// Represents a connection to a remote Steam client.
    /// Implements <see cref="IGONetTransportConnection"/> for GONet integration.
    /// </summary>
    public class SteamworksConnection : IGONetTransportConnection
    {
        private readonly HSteamNetConnection connectionHandle;
        private readonly CSteamID steamID;
        private readonly ulong connectionUID;
        private readonly string remoteIPAddress; // For Direct IP connections, stores the actual IP:port

        private float rttMilliseconds;
        private float packetLoss;
        private float outPacketsPerSec;
        private float inPacketsPerSec;

        public HSteamNetConnection ConnectionHandle => connectionHandle;
        public CSteamID SteamID => steamID;

        public SteamworksConnection(HSteamNetConnection handle, CSteamID steamID, ulong uid, string remoteIP = null)
        {
            this.connectionHandle = handle;
            this.steamID = steamID;
            this.connectionUID = uid;
            this.remoteIPAddress = remoteIP;
        }

        public void UpdateStats(int pingMs, float outPPS, float inPPS)
        {
            this.rttMilliseconds = pingMs;
            this.outPacketsPerSec = outPPS;
            this.inPacketsPerSec = inPPS;

            // Calculate packet loss (approximation - Steam doesn't directly expose this)
            // We'll leave this as 0 for now; full implementation would track sent vs ack'd packets
            this.packetLoss = 0f;
        }

        #region IGONetTransportConnection Implementation

        public ulong ConnectionUID => connectionUID;

        public ushort AuthorityId { get; set; }

        // For Direct IP connections, return the actual IP address so hot standby can connect correctly.
        // For P2P connections, fall back to Steam ID.
        public string RemoteAddress => !string.IsNullOrEmpty(remoteIPAddress) ? remoteIPAddress : steamID.ToString();

        public bool IsConnected => connectionHandle != HSteamNetConnection.Invalid;

        public float RTTMilliseconds => rttMilliseconds;

        public float PacketLoss => packetLoss;

        public uint BytesQueuedForSend
        {
            get
            {
                // Get detailed connection info from Steam
                SteamNetConnectionRealTimeStatus_t status = new SteamNetConnectionRealTimeStatus_t();
                SteamNetConnectionRealTimeLaneStatus_t laneStatus = new SteamNetConnectionRealTimeLaneStatus_t();

                if (SteamNetworkingSockets.GetConnectionRealTimeStatus(connectionHandle, ref status, 0, ref laneStatus) == EResult.k_EResultOK)
                {
                    return (uint)status.m_cbPendingReliable;
                }
                return 0;
            }
        }

        public bool IsUsingRelay
        {
            get
            {
                // Check if using Steam Datagram Relay (SDR)
                SteamNetConnectionInfo_t info;
                if (SteamNetworkingSockets.GetConnectionInfo(connectionHandle, out info))
                {
                    // If IP is 0, we're using relay
                    return info.m_addrRemote.GetIPv4() == 0;
                }
                return false;
            }
        }

        public T GetNativeConnection<T>() where T : class
        {
            if (typeof(T) == typeof(CSteamID))
            {
                return (T)(object)steamID;
            }
            else if (typeof(T) == typeof(HSteamNetConnection))
            {
                return (T)(object)connectionHandle;
            }
            return null;
        }

        #endregion
    }

    #endregion

    #region SteamworksConnectionRequest

    /// <summary>
    /// Represents an incoming connection request from a Steam client.
    /// Implements <see cref="IGONetTransportConnectionRequest"/> for GONet integration.
    /// </summary>
    public class SteamworksConnectionRequest : IGONetTransportConnectionRequest
    {
        private readonly HSteamNetConnection connectionHandle;
        private readonly CSteamID remoteSteamID;
        private readonly SteamworksTransport transport;

        private bool wasRejected = false;
        private GONetTransportDisconnectReason? rejectionReason;

        public bool WasRejected => wasRejected;
        public GONetTransportDisconnectReason? RejectionReason => rejectionReason;

        public SteamworksConnectionRequest(HSteamNetConnection handle, CSteamID steamID, SteamworksTransport transport)
        {
            this.connectionHandle = handle;
            this.remoteSteamID = steamID;
            this.transport = transport;
        }

        #region IGONetTransportConnectionRequest Implementation

        public string RemoteAddress => remoteSteamID.ToString();

        public byte[] RequestData => null; // Steam doesn't provide request data in this callback

        public void Accept()
        {
            // Accept is handled externally in HandleServerConnectionRequest
            // This method is a no-op for Steam (approval is implicit via OnServerConnectionRequested return value)
        }

        public void Reject(GONetTransportDisconnectReason reason)
        {
            wasRejected = true;
            rejectionReason = reason;
        }

        public T GetNativeRequest<T>() where T : class
        {
            if (typeof(T) == typeof(CSteamID))
            {
                return (T)(object)remoteSteamID;
            }
            else if (typeof(T) == typeof(HSteamNetConnection))
            {
                return (T)(object)connectionHandle;
            }
            return null;
        }

        #endregion
    }

    #endregion
}
