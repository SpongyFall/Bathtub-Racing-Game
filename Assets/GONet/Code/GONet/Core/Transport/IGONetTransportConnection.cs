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

namespace GONet.Transport
{
    /// <summary>
    /// Represents a connection to a remote endpoint (client or server).
    /// Provides connection-specific metadata and statistics.
    /// </summary>
    public interface IGONetTransportConnection
    {
        /// <summary>
        /// Unique connection identifier assigned by transport layer.
        /// Guaranteed to be unique within a single server session.
        /// </summary>
        ulong ConnectionUID { get; }

        /// <summary>
        /// GONet authority ID for this connection.
        /// <para>
        /// For server connections: Client's assigned authority ID (1-1022).
        /// </para>
        /// <para>
        /// For client connection to server: <see cref="GONetMain.OwnerAuthorityId_Server"/> (1023).
        /// </para>
        /// </summary>
        ushort AuthorityId { get; set; }

        /// <summary>
        /// Remote endpoint address (IP:port, Steam ID, etc.).
        /// Format depends on transport implementation.
        /// May be null for transports that don't expose addresses (e.g., opaque matchmaking services).
        /// </summary>
        string RemoteAddress { get; }

        /// <summary>
        /// True if connection is currently active.
        /// False if disconnected, timed out, or not yet established.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Round-trip time for this specific connection in milliseconds (0 if unavailable).
        /// Updated continuously during connection lifetime.
        /// </summary>
        float RTTMilliseconds { get; }

        /// <summary>
        /// Packet loss percentage for this connection (0.0-1.0).
        /// 0 if unavailable or not yet measured.
        /// </summary>
        float PacketLoss { get; }

        /// <summary>
        /// Number of bytes queued for send on this connection.
        /// Used for congestion detection and flow control.
        /// High values (>100KB) indicate network congestion or slow receiver.
        /// Returns 0 if transport doesn't expose this metric.
        /// </summary>
        uint BytesQueuedForSend { get; }

        /// <summary>
        /// True if this connection is using relay/TURN server for NAT traversal.
        /// False if direct P2P connection or not applicable.
        ///
        /// <para>
        /// Relay connections have higher latency (~50-100ms additional RTT) but work through restrictive firewalls.
        /// Useful for diagnostics and adaptive quality settings.
        /// </para>
        /// </summary>
        bool IsUsingRelay { get; }

        /// <summary>
        /// Access native transport-specific connection object for advanced use cases.
        ///
        /// <para>
        /// Example usage:
        /// <code>
        /// if (connection.GetNativeConnection&lt;NetcodeIO.NET.RemoteClient&gt;() is RemoteClient remoteClient)
        /// {
        ///     // Access NetcodeIO-specific properties
        ///     var endpoint = remoteClient.RemoteEndpoint;
        /// }
        /// </code>
        /// </para>
        /// </summary>
        /// <typeparam name="T">Native connection type (e.g., NetcodeIO.NET.RemoteClient, Steamworks.CSteamID)</typeparam>
        /// <returns>Native connection object, or null if type doesn't match</returns>
        T GetNativeConnection<T>() where T : class;
    }
}
