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
    /// Configuration settings for <see cref="IGONetTransport"/> implementations.
    ///
    /// <para>
    /// NOTE: Not all transports use all fields. Transport-specific fields are documented with their applicable transports.
    /// Transports should ignore fields they don't support.
    /// </para>
    /// </summary>
    [Serializable]
    public class GONetTransportConfig
    {
        #region Common Settings (All Transports)

        /// <summary>
        /// Update frequency in Hz (updates per second).
        /// Default: 60 Hz (16.6ms update interval).
        ///
        /// <para>
        /// TRANSPORT-SPECIFIC: Only used by transports with background threads (NetcodeIO).
        /// Most transports are event-driven and ignore this field.
        /// </para>
        ///
        /// <para>
        /// NetcodeIO: Controls socket polling rate (recommended: 60-128Hz).
        /// Higher values = lower packet receive latency, slightly more CPU.
        /// Default: 128Hz (competitive eSports server standard - CS:GO/CS2).
        /// </para>
        /// </summary>
        public int Tickrate = 128;

        /// <summary>
        /// Connection timeout in seconds.
        /// If no data received within this period, connection is considered timed out.
        /// Default: 10 seconds.
        /// </summary>
        public int TimeoutSeconds = 10;

        #endregion

        #region Reliability Layer Settings (Transports without Reliability capability)

        /// <summary>
        /// Maximum queued reliable messages before blocking/dropping.
        /// Only used if transport doesn't have <see cref="GONetTransportCapabilities.Reliability"/> flag.
        /// Default: 2000 messages.
        ///
        /// <para>
        /// If queue fills, behavior depends on implementation:
        /// - Block Send() until queue drains (may cause frame stutter)
        /// - Drop oldest messages (data loss)
        /// - Throw exception (fail fast)
        /// </para>
        /// </summary>
        public int MaxReliableQueueSize = 2000;

        /// <summary>
        /// Maximum packets processed per Update() call.
        /// Prevents frame stutter during packet bursts.
        /// Default: 20000 packets.
        ///
        /// <para>
        /// Only applicable to transports using ReliableNetcode wrapper.
        /// </para>
        /// </summary>
        public int MaxPacketsPerTick = 20000;

        /// <summary>
        /// Unreliable message drop threshold for congestion control (0.0-1.0).
        /// When reliable queue reaches this percentage, start dropping unreliable messages.
        /// Default: 0.9 (90% full).
        ///
        /// <para>
        /// Only applicable to transports using ReliableNetcode wrapper.
        /// </para>
        /// </summary>
        public float UnreliableDropThreshold = 0.90f;

        #endregion

        #region Security Settings (NetcodeIO Transport)

        /// <summary>
        /// Private encryption key for NetcodeIO transport (32 bytes).
        /// Must be kept secret and synchronized between client/server.
        ///
        /// <para>
        /// SECURITY WARNING: Never commit private keys to source control!
        /// Generate at runtime or load from secure storage.
        /// </para>
        ///
        /// <para>
        /// Only used by NetcodeIO transport. Ignored by other transports.
        /// </para>
        /// </summary>
        public byte[] PrivateKey;

        /// <summary>
        /// Protocol ID for NetcodeIO version matching (64-bit).
        /// Client and server must use same protocol ID to connect.
        ///
        /// <para>
        /// Use this to prevent clients from connecting to incompatible server versions.
        /// Change this value when making breaking protocol changes.
        /// </para>
        ///
        /// <para>
        /// Only used by NetcodeIO transport. Ignored by other transports.
        /// </para>
        /// </summary>
        public ulong ProtocolID;

        #endregion

        #region Advanced Settings

        /// <summary>
        /// Maximum packet size (MTU) in bytes.
        /// Messages larger than this require fragmentation (if transport supports it).
        /// Default: 1024 bytes (conservative value for most networks).
        ///
        /// <para>
        /// Typical MTU values:
        /// - Ethernet: 1500 bytes
        /// - IPv4 Internet: 576 bytes (safe minimum)
        /// - IPv6 Internet: 1280 bytes
        /// - Steam P2P: ~512 KB (very large)
        /// </para>
        /// </summary>
        public int MaxPacketSize = 1024;

        /// <summary>
        /// Enable IPv6 dual-stack sockets (IPv4 + IPv6 simultaneously).
        /// Default: true.
        ///
        /// <para>
        /// Only applicable to socket-based transports (NetcodeIO, custom UDP/TCP).
        /// Ignored by transports that don't use raw sockets (Steam, WebRTC).
        /// </para>
        /// </summary>
        public bool EnableIPv6 = true;

        /// <summary>
        /// Enable peer-to-peer endpoint lists for multi-path connections.
        /// Default: false.
        ///
        /// <para>
        /// When enabled, client can attempt connection via multiple endpoints (IPv4, IPv6, LAN, WAN).
        /// Only applicable to NetcodeIO transport with P2P support.
        /// </para>
        /// </summary>
        public bool EnableP2P = false;

        /// <summary>
        /// Peer-to-peer endpoint addresses for multi-endpoint connection attempts.
        /// Format: "ip:port" or "hostname:port".
        ///
        /// <para>
        /// Example: ["192.168.1.100:7777", "123.45.67.89:7777"]
        /// Client will try each endpoint until one succeeds.
        /// </para>
        ///
        /// <para>
        /// Only applicable to NetcodeIO transport with P2P support enabled.
        /// </para>
        /// </summary>
        public string[] P2PEndpoints;

        #endregion

        #region Factory Methods

        /// <summary>
        /// Create default configuration with sensible defaults for most games.
        /// </summary>
        public static GONetTransportConfig CreateDefault()
        {
            return new GONetTransportConfig();
        }

        /// <summary>
        /// Create configuration optimized for low-latency competitive games.
        /// Higher tickrate, tighter timeouts.
        /// </summary>
        public static GONetTransportConfig CreateLowLatency()
        {
            return new GONetTransportConfig
            {
                Tickrate = 120,
                TimeoutSeconds = 5,
                MaxReliableQueueSize = 1000,
                UnreliableDropThreshold = 0.80f
            };
        }

        /// <summary>
        /// Create configuration optimized for high player-count MMO-style games.
        /// Lower tickrate, larger queues, more lenient timeouts.
        /// </summary>
        public static GONetTransportConfig CreateHighCapacity()
        {
            return new GONetTransportConfig
            {
                Tickrate = 30,
                TimeoutSeconds = 20,
                MaxReliableQueueSize = 5000,
                MaxPacketsPerTick = 50000,
                UnreliableDropThreshold = 0.95f
            };
        }

        #endregion
    }
}
