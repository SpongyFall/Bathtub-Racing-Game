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
    /// Quality of Service (QoS) types for message delivery.
    /// </summary>
    public enum GONetTransportQoS : byte
    {
        /// <summary>
        /// Unreliable, unordered delivery (UDP-like).
        /// Messages may be lost or arrive out-of-order.
        /// Lowest latency, best for frequently-updated state (position, rotation).
        /// </summary>
        Unreliable = 0,

        /// <summary>
        /// Reliable, ordered delivery (TCP-like).
        /// Messages are guaranteed to arrive in order, with retransmission on loss.
        /// Higher latency, best for critical events (RPCs, spawn/despawn).
        /// </summary>
        Reliable = 1
    }

    /// <summary>
    /// Client connection states.
    /// </summary>
    public enum GONetTransportClientState : byte
    {
        /// <summary>
        /// Client is disconnected from server.
        /// </summary>
        Disconnected = 0,

        /// <summary>
        /// Client is attempting to connect to server.
        /// </summary>
        Connecting = 1,

        /// <summary>
        /// Client is successfully connected to server.
        /// </summary>
        Connected = 2,

        /// <summary>
        /// Client is disconnecting from server.
        /// </summary>
        Disconnecting = 3,

        /// <summary>
        /// Connection timed out (no response from server within timeout period).
        /// </summary>
        TimedOut = 4,

        /// <summary>
        /// Connection refused by server (server full, authentication failed, etc.).
        /// </summary>
        Refused = 5,

        /// <summary>
        /// Connection failed due to transport error.
        /// </summary>
        Error = 6
    }

    /// <summary>
    /// Disconnect reason codes.
    /// </summary>
    public enum GONetTransportDisconnectReason : byte
    {
        /// <summary>
        /// User initiated disconnect (client called Disconnect() or server called StopServer()).
        /// </summary>
        UserInitiated = 0,

        /// <summary>
        /// Connection timed out (no heartbeat/data received within timeout threshold).
        /// </summary>
        Timeout = 1,

        /// <summary>
        /// Server shutdown (server called StopServer()).
        /// </summary>
        ServerShutdown = 2,

        /// <summary>
        /// Server is full (maximum client count reached).
        /// </summary>
        ServerFull = 3,

        /// <summary>
        /// Transport-level error (socket error, encryption failure, protocol error).
        /// </summary>
        TransportError = 4,

        /// <summary>
        /// Authentication failed (invalid token, expired credentials, etc.).
        /// </summary>
        AuthenticationFailed = 5,

        /// <summary>
        /// Client was kicked by server.
        /// </summary>
        Kicked = 6
    }

    /// <summary>
    /// Transport capability flags.
    /// GONet uses these to determine whether to apply additional layers (reliability, compression, etc.).
    /// </summary>
    [Flags]
    public enum GONetTransportCapabilities
    {
        /// <summary>
        /// No special capabilities (basic UDP-like transport).
        /// </summary>
        None = 0,

        /// <summary>
        /// Transport supports IPv6 addressing (in addition to IPv4).
        /// </summary>
        IPv6 = 1 << 0,

        /// <summary>
        /// Transport supports peer-to-peer connections (no dedicated server required).
        /// Example: Steam P2P, WebRTC.
        /// </summary>
        P2P = 1 << 1,

        /// <summary>
        /// Transport provides encryption at transport layer.
        /// GONet will skip application-level encryption if this flag is set.
        /// </summary>
        Encryption = 1 << 2,

        /// <summary>
        /// Transport provides compression at transport layer.
        /// GONet will skip application-level compression if this flag is set.
        /// </summary>
        Compression = 1 << 3,

        /// <summary>
        /// Transport supports messages larger than MTU (automatic fragmentation/reassembly).
        /// If not set, GONet must manually fragment large messages.
        /// </summary>
        Fragmentation = 1 << 4,

        /// <summary>
        /// Transport provides built-in reliability layer (handles retransmission, ACKs, sequencing).
        /// If set, GONet will NOT wrap transport with ReliableNetcode layer.
        /// Example: Steam P2P, TCP-based transports.
        /// </summary>
        Reliability = 1 << 5,

        /// <summary>
        /// Transport provides connection authentication/authorization.
        /// Example: Steam authentication, Epic Online Services authentication.
        /// </summary>
        Authentication = 1 << 6,

        /// <summary>
        /// Transport supports multiple simultaneous listen sockets/servers.
        /// Required for hot standby where each node runs both an active and dormant server.
        /// Example: Most UDP-based transports, Steam Sockets.
        /// </summary>
        MultipleListenSockets = 1 << 7,

        /// <summary>
        /// Transport supports virtual ports (logical multiplexing over single connection).
        /// Enables dormant server on virtual port 1 while main server uses virtual port 0.
        /// Example: Steam P2P uses virtual ports for multiple services over single connection.
        /// </summary>
        VirtualPorts = 1 << 8
    }
}
