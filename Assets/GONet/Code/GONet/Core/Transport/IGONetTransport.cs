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

using System;

namespace GONet.Transport
{
    /// <summary>
    /// Abstract transport layer for GONet networking.
    /// Implementations provide connection management, message delivery, and network statistics.
    ///
    /// <para>
    /// THREADING: All callbacks MAY be invoked from background threads.
    /// Implementations should be thread-safe or document threading requirements.
    /// GONet handles thread marshalling internally via <see cref="GONetThreading"/>.
    /// </para>
    ///
    /// <para>
    /// DESIGN PHILOSOPHY: This interface prioritizes:
    /// 1. Performance - Minimal overhead, zero allocations in hot path where possible
    /// 2. Simplicity - Clean API with &lt;20 core methods
    /// 3. Flexibility - Supports UDP, TCP, Steam P2P, WebRTC, custom transports
    /// 4. Backward Compatibility - Existing GONet code works unchanged
    /// </para>
    /// </summary>
    public interface IGONetTransport : IDisposable
    {
        #region Lifecycle

        /// <summary>
        /// Initialize transport with configuration.
        /// Called once before any other methods.
        /// </summary>
        /// <param name="config">Transport configuration settings</param>
        void Initialize(GONetTransportConfig config);

        /// <summary>
        /// Shutdown transport and release all resources.
        /// After shutdown, transport must be re-initialized before use.
        /// </summary>
        void Shutdown();

        #endregion

        #region Server-Side Operations

        /// <summary>
        /// Start server listening for incoming connections.
        /// </summary>
        /// <param name="port">Port to bind (0 = auto-assign). Ignored by transports that don't use ports (e.g., Steam P2P).</param>
        /// <param name="maxConnections">Maximum simultaneous client connections</param>
        void StartServer(int port, int maxConnections);

        /// <summary>
        /// Stop server and disconnect all clients gracefully.
        /// </summary>
        void StopServer();

        /// <summary>
        /// Disconnect specific client from server (server-side only).
        /// This allows server to kick individual clients without stopping the entire server.
        /// </summary>
        /// <param name="connection">Connection to disconnect</param>
        /// <param name="reason">Reason for disconnect (e.g., Kicked, ServerFull)</param>
        void DisconnectConnection(IGONetTransportConnection connection, GONetTransportDisconnectReason reason);

        /// <summary>
        /// Invoked when a client requests connection to this server (server-side only).
        ///
        /// <para>
        /// SECURITY: This event allows server to approve or reject incoming connections.
        /// Critical for P2P transports (Steam, EOS) to prevent unauthorized connections.
        /// If this event has no listeners, connections are auto-accepted (backward compatible).
        /// </para>
        ///
        /// <para>
        /// Return true to accept connection, false to reject.
        /// </para>
        ///
        /// <para>
        /// NOTE: May be called from background thread - use <see cref="GONetThreading.RunOnMainThread"/> if accessing Unity APIs.
        /// </para>
        /// </summary>
        event Func<IGONetTransportConnectionRequest, bool> OnServerConnectionRequested;

        /// <summary>
        /// Invoked when a client successfully connects to this server.
        /// NOTE: May be called from background thread - use <see cref="GONetThreading.RunOnMainThread"/> if accessing Unity APIs.
        /// </summary>
        event Action<IGONetTransportConnection> OnServerClientConnected;

        /// <summary>
        /// Invoked when a client disconnects from this server.
        /// NOTE: May be called from background thread - use <see cref="GONetThreading.RunOnMainThread"/> if accessing Unity APIs.
        /// </summary>
        event Action<IGONetTransportConnection, GONetTransportDisconnectReason> OnServerClientDisconnected;

        #endregion

        #region Client-Side Operations

        /// <summary>
        /// Connect to server as a client.
        /// </summary>
        /// <param name="address">Server address (IP address, Steam ID, EOS ID, etc. - format depends on transport)</param>
        /// <param name="port">Server port (ignored by portless transports like Steam P2P)</param>
        /// <param name="timeoutSeconds">Connection timeout in seconds</param>
        /// <param name="authData">Optional transport-specific authentication data (e.g., connect token for NetcodeIO, session ticket for Steam)</param>
        void ConnectClient(string address, int port, int timeoutSeconds, byte[] authData = null);

        /// <summary>
        /// Disconnect from server.
        /// </summary>
        void DisconnectClient();

        /// <summary>
        /// Invoked when this client successfully connects to server.
        /// NOTE: May be called from background thread - use <see cref="GONetThreading.RunOnMainThread"/> if accessing Unity APIs.
        /// </summary>
        event Action OnClientConnected;

        /// <summary>
        /// Invoked when this client disconnects from server.
        /// NOTE: May be called from background thread - use <see cref="GONetThreading.RunOnMainThread"/> if accessing Unity APIs.
        /// </summary>
        event Action<GONetTransportDisconnectReason> OnClientDisconnected;

        /// <summary>
        /// Invoked when this client's connection state changes.
        /// NOTE: May be called from background thread - use <see cref="GONetThreading.RunOnMainThread"/> if accessing Unity APIs.
        /// </summary>
        event Action<GONetTransportClientState> OnClientStateChanged;

        #endregion

        #region Data Transmission

        /// <summary>
        /// Send message to remote endpoint(s).
        ///
        /// <para>
        /// PERFORMANCE NOTE: Transport may retain reference to <paramref name="data"/> array until next <see cref="Update"/> call.
        /// Do NOT modify array contents after calling Send. Use array pooling where possible.
        /// </para>
        /// </summary>
        /// <param name="data">Message bytes</param>
        /// <param name="length">Message length in bytes</param>
        /// <param name="qos">Quality of service (reliable/unreliable delivery)</param>
        /// <param name="target">
        /// Target connection for server-side sends (specific client).
        /// Null for client→server sends, or server→all-clients broadcast.
        /// </param>
        /// <param name="channel">
        /// Logical channel ID (0-255) for multiplexing different message types.
        /// Allows separation of concerns (e.g., channel 0 = RPCs, channel 1 = position updates, channel 2 = voice).
        /// Default: 0 (main channel).
        /// </param>
        void Send(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection target = null, byte channel = 0);

        /// <summary>
        /// Broadcast message to all connected clients (server-side only).
        /// This is an optimization over manually looping through connections.
        ///
        /// <para>
        /// PERFORMANCE NOTE: Implementations may batch sends or use multicast optimizations.
        /// Fallback implementation loops through all connections calling Send().
        /// </para>
        /// </summary>
        /// <param name="data">Message bytes</param>
        /// <param name="length">Message length in bytes</param>
        /// <param name="qos">Quality of service (reliable/unreliable delivery)</param>
        /// <param name="excludeConnection">Optional connection to exclude from broadcast (e.g., original sender)</param>
        /// <param name="channel">Logical channel ID (0-255). Default: 0.</param>
        void Broadcast(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection excludeConnection = null, byte channel = 0);

        /// <summary>
        /// Poll for incoming messages and update internal transport state.
        /// MUST be called from Unity main thread each frame (typically in GONetServer/GONetClient.Update()).
        ///
        /// <para>
        /// THREADING: If transport uses background threads for I/O, this method queues received messages
        /// for delivery on main thread. If transport is single-threaded, this method performs actual I/O.
        /// </para>
        /// </summary>
        void Update();

        /// <summary>
        /// Check if a server instance is already running on the local machine for this transport.
        ///
        /// <para>
        /// USAGE: Auto-detection logic uses this to determine if current instance should start as server or client.
        /// - Returns true → Another instance is already running as server → Start as client
        /// - Returns false → No server detected → Start as server
        /// </para>
        ///
        /// <para>
        /// IMPLEMENTATION NOTES:
        /// - NetcodeIO: Attempts to bind UDP socket to port (fallback for Steam detection)
        /// - Steamworks: Checks lock file (Steam sockets invisible to OS-level port checks)
        /// - Custom transports: Implement appropriate detection mechanism (lock file, registry, shared memory, etc.)
        /// </para>
        ///
        /// <para>
        /// THREADING: Safe to call from any thread.
        /// </para>
        /// </summary>
        /// <param name="port">Port to check (may be ignored by transports that don't use ports)</param>
        /// <returns>True if server detected on local machine, false otherwise</returns>
        bool IsServerRunningLocally(int port);

        /// <summary>
        /// Invoked when message received from remote endpoint.
        ///
        /// <para>
        /// IMPORTANT: <paramref name="data"/> array is only valid until callback returns.
        /// If you need to retain message data, copy it to a separate buffer.
        /// </para>
        ///
        /// <para>
        /// NOTE: May be called from background thread - use <see cref="GONetThreading.RunOnMainThread"/> if accessing Unity APIs.
        /// </para>
        /// </summary>
        /// <param name="data">Message bytes (valid only until callback returns)</param>
        /// <param name="length">Message length in bytes</param>
        /// <param name="qos">Quality of service this message was sent with</param>
        /// <param name="source">Source connection (server-side: specific client, client-side: null)</param>
        /// <param name="channel">Logical channel ID (0-255) this message was received on</param>
        event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte> OnMessageReceived;

        /// <summary>
        /// Invoked when message received with transport-level timestamp (if available).
        ///
        /// <para><b>HIGH-LOAD OPTIMIZATION (December 2025):</b></para>
        /// <para>
        /// This event provides the ACCURATE transport-level receive timestamp, which is when
        /// the transport layer (e.g., Steam's internal networking) actually received the packet -
        /// NOT when our code processed it. During high-load scenarios, there can be 50-500ms
        /// between when the transport receives a packet and when we process it.
        /// </para>
        ///
        /// <para>
        /// Using this timestamp for RTT calculations gives accurate network latency that
        /// doesn't include frame processing delay.
        /// </para>
        ///
        /// <para>
        /// IMPORTANT: <paramref name="data"/> array is only valid until callback returns.
        /// If you need to retain message data, copy it to a separate buffer.
        /// </para>
        ///
        /// <para>
        /// NOTE: This event may not be fired by all transports. Transports that don't support
        /// accurate receive timestamps (or where the timestamp equals processing time) should
        /// only fire <see cref="OnMessageReceived"/>.
        /// </para>
        /// </summary>
        /// <param name="data">Message bytes (valid only until callback returns)</param>
        /// <param name="length">Message length in bytes</param>
        /// <param name="qos">Quality of service this message was sent with</param>
        /// <param name="source">Source connection (server-side: specific client, client-side: null)</param>
        /// <param name="channel">Logical channel ID (0-255) this message was received on</param>
        /// <param name="transportReceiveTicks">
        /// Transport-level receive timestamp in GONet ticks (100-nanosecond units).
        /// For Steamworks: Converted from <c>SteamNetworkingMessage_t.m_usecTimeReceived</c>.
        /// For other transports: May equal processing time if accurate timestamps not available.
        /// </param>
        event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte, long> OnMessageReceivedWithTimestamp;

        #endregion

        #region Network Statistics

        /// <summary>
        /// Round-trip time in milliseconds (0 if unavailable or not yet measured).
        /// For server, this is typically the average RTT across all clients.
        /// </summary>
        float RTTMilliseconds { get; }

        /// <summary>
        /// Packet loss percentage 0.0-1.0 (0 if unavailable or not yet measured).
        /// For server, this is typically the average packet loss across all clients.
        /// </summary>
        float PacketLoss { get; }

        /// <summary>
        /// Outgoing bandwidth in kilobytes per second.
        /// </summary>
        float SentBandwidthKBPS { get; }

        /// <summary>
        /// Incoming bandwidth in kilobytes per second.
        /// </summary>
        float ReceivedBandwidthKBPS { get; }

        #endregion

        #region Connection State

        /// <summary>
        /// True if transport is currently running as server.
        /// </summary>
        bool IsServer { get; }

        /// <summary>
        /// True if transport is currently running as client.
        /// </summary>
        bool IsClient { get; }

        /// <summary>
        /// True if client is connected to server (client-side only).
        /// Always false for server-side.
        /// </summary>
        bool IsConnected { get; }

        #endregion

        #region Capabilities

        /// <summary>
        /// Transport capabilities (features supported).
        /// GONet uses this to determine whether to apply additional layers (reliability, compression, etc.).
        /// </summary>
        GONetTransportCapabilities Capabilities { get; }

        /// <summary>
        /// Query the maximum message size this transport can reliably deliver without application-level chunking.
        ///
        /// <para>
        /// GONet uses this to determine when to apply manual chunking. If a message exceeds this limit,
        /// GONet will split it into smaller chunks with reassembly headers before sending.
        /// </para>
        ///
        /// <para>
        /// IMPLEMENTATION GUIDANCE:
        /// - Return the practical limit your transport can handle end-to-end (including any wrapper layers)
        /// - Account for ReliableNetcode if you use it (~16KB limit due to 16 fragments × 1KB)
        /// - Account for Steam P2P limits if applicable (~512KB for Steam networking, ~1MB for newer API)
        /// - Return -1 if your transport has no practical limit (GONet will use reasonable defaults)
        /// - QoS-specific: Unreliable often has lower limits (MTU ~1200) vs reliable (can be much larger)
        /// </para>
        ///
        /// <para>
        /// EXAMPLES:
        /// - NetcodeIO + ReliableNetcode: return 16 * 1024 (16KB - ReliableNetcode's fragment limit)
        /// - Steamworks (using ReliableNetcode wrapper): return 16 * 1024 (same - wrapper dictates limit)
        /// - Raw UDP unreliable: return 1200 (safe MTU minus headers)
        /// - TCP or native Steam reliable: return 512 * 1024 or higher (platform-dependent)
        /// - WebRTC data channel: return -1 (effectively unlimited, browser handles fragmentation)
        /// </para>
        /// </summary>
        /// <param name="qos">Quality of service (Reliable or Unreliable) - limits may differ</param>
        /// <returns>
        /// Maximum message size in bytes, or -1 if unlimited/unknown.
        /// GONet treats -1 as "transport handles large messages" and applies conservative defaults.
        /// </returns>
        int GetMaxMessageSize(GONetTransportQoS qos);

        #endregion
    }
}
