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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GONet.DistributedHost
{
    /// <summary>
    /// Represents a node's identity in a distributed host topology.
    /// Contains both persistent (cross-session) and session-specific identifiers.
    ///
    /// Size: 24 bytes (unmanaged struct for efficient serialization)
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GONetNodeIdentity : IEquatable<GONetNodeIdentity>
    {
        /// <summary>
        /// Globally unique identifier persisted locally (PlayerPrefs or file).
        /// Survives across sessions. Generated once per installation.
        /// </summary>
        public ulong PersistentId;

        /// <summary>
        /// Current session's MyAuthorityId from GONetMain.
        /// Assigned by server on connection, valid only for this session.
        /// </summary>
        public ushort SessionAuthorityId;

        /// <summary>
        /// Monotonic timestamp when this node joined the session.
        /// Used for deterministic tiebreakers. Uses GONetMain.Time.ElapsedTicks, NOT wall-clock.
        /// </summary>
        public long JoinedAtTicks;

        /// <summary>
        /// Capability flags indicating what this node can do.
        /// </summary>
        public GONetNodeCapabilities Capabilities;

        /// <summary>
        /// Bitfield indicating which metrics are known vs unknown/invalid.
        /// Prevents assuming 0 for unknown values (which would unfairly tank scores).
        /// </summary>
        public byte MetricsValidityFlags;

        // Padding to align to 24 bytes total (8 + 2 + 8 + 4 + 1 = 23, +1 padding = 24)
        private byte _padding;

        /// <summary>
        /// Creates a new node identity with the specified values.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public GONetNodeIdentity(ulong persistentId, ushort sessionAuthorityId, long joinedAtTicks, GONetNodeCapabilities capabilities = GONetNodeCapabilities.CanHost)
        {
            PersistentId = persistentId;
            SessionAuthorityId = sessionAuthorityId;
            JoinedAtTicks = joinedAtTicks;
            Capabilities = capabilities;
            MetricsValidityFlags = 0;
            _padding = 0;
        }

        /// <summary>
        /// Returns true if this node has the CanHost capability and is not disqualified.
        /// </summary>
        public bool CanBecomeHost =>
            (Capabilities & GONetNodeCapabilities.CanHost) != 0 &&
            (Capabilities & GONetNodeCapabilities.RequiresRelay) == 0;

        /// <summary>
        /// Returns true if this node is a dedicated server (pinned host).
        /// </summary>
        public bool IsDedicatedServer => (Capabilities & GONetNodeCapabilities.DedicatedServer) != 0;

        public bool Equals(GONetNodeIdentity other)
        {
            return PersistentId == other.PersistentId && SessionAuthorityId == other.SessionAuthorityId;
        }

        public override bool Equals(object obj)
        {
            return obj is GONetNodeIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(PersistentId, SessionAuthorityId);
        }

        public static bool operator ==(GONetNodeIdentity left, GONetNodeIdentity right) => left.Equals(right);
        public static bool operator !=(GONetNodeIdentity left, GONetNodeIdentity right) => !left.Equals(right);

        public override string ToString()
        {
            return $"NodeId(P:{PersistentId:X8}, S:{SessionAuthorityId}, J:{JoinedAtTicks}, C:{Capabilities})";
        }
    }

    /// <summary>
    /// Capability flags for distributed host nodes.
    /// Determines what roles and features a node can support.
    /// </summary>
    [Flags]
    public enum GONetNodeCapabilities : uint
    {
        /// <summary>
        /// No special capabilities.
        /// </summary>
        None = 0,

        /// <summary>
        /// Node is capable of becoming the host.
        /// Cleared automatically if behind symmetric NAT or requires relay to reach majority of peers.
        /// </summary>
        CanHost = 1 << 0,

        /// <summary>
        /// Node is a dedicated server (pinned host).
        /// When set, this node is the eternal host and election is completely disabled.
        /// </summary>
        DedicatedServer = 1 << 1,

        /// <summary>
        /// Node has verified good uplink bandwidth (>= 1 Mbps upload).
        /// Gives slight scoring bonus for host selection.
        /// </summary>
        HasGoodUplink = 1 << 2,

        /// <summary>
        /// Node requires relay to communicate with most peers.
        /// Set when direct P2P fails to majority of mesh.
        /// Nodes with this flag are disqualified from hosting.
        /// </summary>
        RequiresRelay = 1 << 3,

        /// <summary>
        /// Node is running in headless/server mode (no display).
        /// Typically indicates a dedicated server or bot.
        /// </summary>
        IsHeadless = 1 << 4,

        /// <summary>
        /// Node is running on mobile device (iOS/Android).
        /// Used for battery-aware scoring adjustments.
        /// </summary>
        IsMobile = 1 << 5,

        /// <summary>
        /// Node's NAT type has been verified as Open/Full Cone.
        /// Most compatible for P2P connections.
        /// </summary>
        NATOpen = 1 << 6,

        /// <summary>
        /// Node's NAT type has been verified as Symmetric.
        /// Least compatible for P2P, may require relay.
        /// </summary>
        NATSymmetric = 1 << 7,
    }

    /// <summary>
    /// Wrapper type for PersistentId to prevent accidental mixing with other ulong values.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct PersistentNodeId : IEquatable<PersistentNodeId>
    {
        public readonly ulong Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PersistentNodeId(ulong value) => Value = value;

        public bool Equals(PersistentNodeId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is PersistentNodeId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"PersistentId({Value:X16})";

        public static bool operator ==(PersistentNodeId left, PersistentNodeId right) => left.Value == right.Value;
        public static bool operator !=(PersistentNodeId left, PersistentNodeId right) => left.Value != right.Value;
        public static implicit operator ulong(PersistentNodeId id) => id.Value;
        public static explicit operator PersistentNodeId(ulong value) => new PersistentNodeId(value);
    }

    /// <summary>
    /// Wrapper type for SessionAuthorityId to prevent accidental mixing with other ushort values.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct SessionNodeId : IEquatable<SessionNodeId>
    {
        public readonly ushort Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SessionNodeId(ushort value) => Value = value;

        public bool Equals(SessionNodeId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is SessionNodeId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"SessionId({Value})";

        public static bool operator ==(SessionNodeId left, SessionNodeId right) => left.Value == right.Value;
        public static bool operator !=(SessionNodeId left, SessionNodeId right) => left.Value != right.Value;
        public static implicit operator ushort(SessionNodeId id) => id.Value;
        public static explicit operator SessionNodeId(ushort value) => new SessionNodeId(value);
    }

    /// <summary>
    /// Flags indicating which metric categories have valid/known values.
    /// Used to prevent scoring penalties for unknown values.
    /// </summary>
    [Flags]
    public enum MetricsValidityFlags : byte
    {
        None = 0,
        RTTValid = 1 << 0,
        JitterValid = 1 << 1,
        PacketLossValid = 1 << 2,
        BandwidthValid = 1 << 3,
        CPUHeadroomValid = 1 << 4,
        BatteryValid = 1 << 5,
        NATTypeValid = 1 << 6,
        AllValid = 0x7F
    }

    /// <summary>
    /// Manages persistent node ID generation and storage.
    /// </summary>
    public static class GONetNodeIdentityManager
    {
        private static ulong? cachedPersistentId;

        /// <summary>
        /// Gets or generates a persistent node ID for this process instance.
        /// Each process gets a unique ID via GUID generation, ensuring uniqueness even when
        /// multiple builds run on the same machine (which share PlayerPrefs).
        /// </summary>
        public static ulong GetOrCreatePersistentId()
        {
            if (cachedPersistentId.HasValue)
            {
                return cachedPersistentId.Value;
            }

            // Generate a unique ID per process using GONet's GUID utility.
            // This ensures uniqueness across all processes, even on the same machine.
            ulong newId = (ulong)GONet.Utils.GUID.Generate().AsInt64();

            cachedPersistentId = newId;
            GONetLog.Info($"[DistributedHost] Process PersistentNodeId: {newId:X16}");

            return newId;
        }

        /// <summary>
        /// Creates a node identity for the local machine with the given session authority ID.
        /// </summary>
        /// <param name="sessionAuthorityId">The authority ID assigned for this session</param>
        /// <param name="joinedAtTicks">Monotonic timestamp when joined (from GONetMain.Time.ElapsedTicks)</param>
        /// <returns>A fully populated node identity</returns>
        public static GONetNodeIdentity CreateLocalIdentity(ushort sessionAuthorityId, long joinedAtTicks)
        {
            var identity = new GONetNodeIdentity(
                GetOrCreatePersistentId(),
                sessionAuthorityId,
                joinedAtTicks,
                DetectCapabilities()
            );

            return identity;
        }

        /// <summary>
        /// Detects the capabilities of the local machine.
        /// </summary>
        private static GONetNodeCapabilities DetectCapabilities()
        {
            GONetNodeCapabilities caps = GONetNodeCapabilities.CanHost; // Assume can host by default

            // Check if headless (no graphics)
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                caps |= GONetNodeCapabilities.IsHeadless;
            }

            // Check if mobile
            if (Application.platform == RuntimePlatform.IPhonePlayer ||
                Application.platform == RuntimePlatform.Android)
            {
                caps |= GONetNodeCapabilities.IsMobile;
            }

            // NAT type detection is deferred until we can do STUN-like checks
            // RequiresRelay is determined after connection to other peers

            return caps;
        }

        /// <summary>
        /// Clears the cached persistent ID (for testing purposes only).
        /// </summary>
        internal static void ClearCache()
        {
            cachedPersistentId = null;
        }
    }

    /// <summary>
    /// Network connection endpoint information for a node.
    /// Used for failover reconnection - when a vice host becomes the new host,
    /// other clients need to know how to connect to it.
    ///
    /// This struct is transport-agnostic and contains multiple possible connection methods.
    /// Clients try them in order of preference based on their network situation.
    /// Supports both IPv4 and IPv6 for maximum compatibility.
    /// </summary>
    [MemoryPack.MemoryPackable]
    public partial struct GONetConnectionEndpoint
    {
        /// <summary>
        /// IPv4 address as a 32-bit integer (network byte order).
        /// 0 if not available or not applicable.
        /// For LAN/loopback scenarios, this is the local IP (e.g., 192.168.x.x or 127.0.0.1).
        /// </summary>
        public uint IPv4Address;

        /// <summary>
        /// IPv6 address as two 64-bit integers (high and low parts).
        /// Both 0 if not available or not applicable.
        /// Stored as: [High: bytes 0-7] [Low: bytes 8-15]
        /// </summary>
        public ulong IPv6AddressHigh;
        public ulong IPv6AddressLow;

        /// <summary>
        /// Port number this node can accept connections on.
        /// 0 if not listening or unknown.
        /// </summary>
        public ushort Port;

        /// <summary>
        /// Transport-specific connection token/identifier.
        /// Examples:
        /// - Steamworks: SteamID (64-bit)
        /// - Unity Relay: Allocation ID or join code hash
        /// - Custom: Whatever the transport needs
        /// </summary>
        public ulong TransportSpecificId;

        /// <summary>
        /// Flags indicating what connection methods are available.
        /// </summary>
        public ConnectionEndpointFlags Flags;

        /// <summary>
        /// Creates an endpoint for LAN/loopback connections.
        /// </summary>
        public static GONetConnectionEndpoint CreateLAN(uint ipv4, ushort port)
        {
            var flags = ConnectionEndpointFlags.HasIPv4 | ConnectionEndpointFlags.CanAcceptConnections;

            // Detect if loopback or LAN
            if (GONet.Utils.NetworkUtils.IsLoopbackIPv4(ipv4))
            {
                flags |= ConnectionEndpointFlags.IsLoopback;
            }
            else if (GONet.Utils.NetworkUtils.IsPrivateIPv4(ipv4))
            {
                flags |= ConnectionEndpointFlags.IsLAN;
            }

            return new GONetConnectionEndpoint
            {
                IPv4Address = ipv4,
                Port = port,
                TransportSpecificId = 0,
                Flags = flags
            };
        }

        /// <summary>
        /// Creates a local endpoint using auto-detected LAN IP and the specified port.
        /// This is the typical way to create an endpoint for the local node's dormant server.
        /// </summary>
        public static GONetConnectionEndpoint CreateLocalLAN(ushort port)
        {
            uint localIP = GONet.Utils.NetworkUtils.GetLocalIPv4ForLAN();
            return CreateLAN(localIP, port);
        }

        /// <summary>
        /// Creates an endpoint with transport-specific ID (e.g., SteamID).
        /// </summary>
        public static GONetConnectionEndpoint CreateTransportSpecific(ulong transportId)
        {
            return new GONetConnectionEndpoint
            {
                IPv4Address = 0,
                Port = 0,
                TransportSpecificId = transportId,
                Flags = ConnectionEndpointFlags.HasTransportId | ConnectionEndpointFlags.CanAcceptConnections
            };
        }

        /// <summary>
        /// Creates an endpoint with both LAN and transport-specific info.
        /// </summary>
        public static GONetConnectionEndpoint CreateFull(uint ipv4, ushort port, ulong transportId)
        {
            return new GONetConnectionEndpoint
            {
                IPv4Address = ipv4,
                Port = port,
                TransportSpecificId = transportId,
                Flags = ConnectionEndpointFlags.HasIPv4 | ConnectionEndpointFlags.HasTransportId | ConnectionEndpointFlags.CanAcceptConnections
            };
        }

        /// <summary>
        /// Returns true if this endpoint has valid IPv4 connection info.
        /// </summary>
        public bool HasIPv4 => (Flags & ConnectionEndpointFlags.HasIPv4) != 0 && IPv4Address != 0;

        /// <summary>
        /// Returns true if this endpoint has valid IPv6 connection info.
        /// </summary>
        public bool HasIPv6 => (Flags & ConnectionEndpointFlags.HasIPv6) != 0 && (IPv6AddressHigh != 0 || IPv6AddressLow != 0);

        /// <summary>
        /// Returns true if this endpoint has any IP address (v4 or v6).
        /// </summary>
        public bool HasIP => HasIPv4 || HasIPv6;

        /// <summary>
        /// Returns true if this endpoint has a transport-specific ID.
        /// </summary>
        public bool HasTransportId => (Flags & ConnectionEndpointFlags.HasTransportId) != 0 && TransportSpecificId != 0;

        /// <summary>
        /// Returns true if this node can accept incoming connections.
        /// </summary>
        public bool CanAcceptConnections => (Flags & ConnectionEndpointFlags.CanAcceptConnections) != 0;

        /// <summary>
        /// Gets the IPv4 address as a dotted string (e.g., "192.168.1.100").
        /// </summary>
        public string IPv4String
        {
            get
            {
                if (IPv4Address == 0) return "0.0.0.0";
                return $"{(IPv4Address >> 24) & 0xFF}.{(IPv4Address >> 16) & 0xFF}.{(IPv4Address >> 8) & 0xFF}.{IPv4Address & 0xFF}";
            }
        }

        /// <summary>
        /// Gets the IPv6 address as a colon-separated string.
        /// </summary>
        public string IPv6String
        {
            get
            {
                if (IPv6AddressHigh == 0 && IPv6AddressLow == 0) return "::";

                // Convert to bytes then to IPAddress for proper formatting
                byte[] bytes = new byte[16];
                for (int i = 0; i < 8; i++)
                {
                    bytes[i] = (byte)((IPv6AddressHigh >> (56 - i * 8)) & 0xFF);
                    bytes[i + 8] = (byte)((IPv6AddressLow >> (56 - i * 8)) & 0xFF);
                }
                return new System.Net.IPAddress(bytes).ToString();
            }
        }

        /// <summary>
        /// Sets the IPv6 address from an IPAddress object.
        /// </summary>
        public void SetIPv6(System.Net.IPAddress ipv6)
        {
            if (ipv6 == null || ipv6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                IPv6AddressHigh = 0;
                IPv6AddressLow = 0;
                return;
            }

            byte[] bytes = ipv6.GetAddressBytes();
            IPv6AddressHigh = 0;
            IPv6AddressLow = 0;

            for (int i = 0; i < 8; i++)
            {
                IPv6AddressHigh |= (ulong)bytes[i] << (56 - i * 8);
                IPv6AddressLow |= (ulong)bytes[i + 8] << (56 - i * 8);
            }

            Flags |= ConnectionEndpointFlags.HasIPv6;
        }

        /// <summary>
        /// Gets the IPv6 address as an IPAddress object.
        /// </summary>
        public System.Net.IPAddress GetIPv6Address()
        {
            if (!HasIPv6) return System.Net.IPAddress.IPv6None;

            byte[] bytes = new byte[16];
            for (int i = 0; i < 8; i++)
            {
                bytes[i] = (byte)((IPv6AddressHigh >> (56 - i * 8)) & 0xFF);
                bytes[i + 8] = (byte)((IPv6AddressLow >> (56 - i * 8)) & 0xFF);
            }
            return new System.Net.IPAddress(bytes);
        }

        /// <summary>
        /// Gets the IPv4 address as an IPAddress object.
        /// </summary>
        public System.Net.IPAddress GetIPv4Address()
        {
            if (!HasIPv4) return System.Net.IPAddress.None;

            byte[] bytes = new byte[4];
            bytes[0] = (byte)((IPv4Address >> 24) & 0xFF);
            bytes[1] = (byte)((IPv4Address >> 16) & 0xFF);
            bytes[2] = (byte)((IPv4Address >> 8) & 0xFF);
            bytes[3] = (byte)(IPv4Address & 0xFF);
            return new System.Net.IPAddress(bytes);
        }

        /// <summary>
        /// Parses an IPv4 string to a 32-bit integer.
        /// </summary>
        public static uint ParseIPv4(string ipString)
        {
            if (string.IsNullOrEmpty(ipString)) return 0;

            var parts = ipString.Split('.');
            if (parts.Length != 4) return 0;

            if (byte.TryParse(parts[0], out byte a) &&
                byte.TryParse(parts[1], out byte b) &&
                byte.TryParse(parts[2], out byte c) &&
                byte.TryParse(parts[3], out byte d))
            {
                return ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
            }
            return 0;
        }

        /// <summary>
        /// Creates an endpoint from an IPAddress (v4 or v6) and port.
        /// </summary>
        public static GONetConnectionEndpoint CreateFromIPAddress(System.Net.IPAddress address, ushort port)
        {
            var endpoint = new GONetConnectionEndpoint { Port = port };

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] bytes = address.GetAddressBytes();
                endpoint.IPv4Address = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                endpoint.Flags = ConnectionEndpointFlags.HasIPv4 | ConnectionEndpointFlags.CanAcceptConnections;

                if (System.Net.IPAddress.IsLoopback(address))
                    endpoint.Flags |= ConnectionEndpointFlags.IsLoopback;
                else if (GONet.Utils.NetworkUtils.IsPrivateIPv4(endpoint.IPv4Address))
                    endpoint.Flags |= ConnectionEndpointFlags.IsLAN;
            }
            else if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                endpoint.SetIPv6(address);
                endpoint.Flags = ConnectionEndpointFlags.HasIPv6 | ConnectionEndpointFlags.CanAcceptConnections;

                if (System.Net.IPAddress.IsLoopback(address))
                    endpoint.Flags |= ConnectionEndpointFlags.IsLoopback;
                else if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
                    endpoint.Flags |= ConnectionEndpointFlags.IsLAN;
            }

            return endpoint;
        }

        /// <summary>
        /// Creates a dual-stack endpoint with both IPv4 and IPv6.
        /// </summary>
        public static GONetConnectionEndpoint CreateDualStack(System.Net.IPAddress ipv4, System.Net.IPAddress ipv6, ushort port)
        {
            var endpoint = CreateFromIPAddress(ipv4, port);
            if (ipv6 != null && ipv6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                endpoint.SetIPv6(ipv6);
            }
            return endpoint;
        }

        public override string ToString()
        {
            var parts = new System.Collections.Generic.List<string>();

            if (HasIPv4)
                parts.Add($"v4:{IPv4String}:{Port}");
            if (HasIPv6)
                parts.Add($"v6:[{IPv6String}]:{Port}");
            if (HasTransportId)
                parts.Add($"TID:{TransportSpecificId:X}");

            if (parts.Count == 0)
                return "Endpoint(none)";

            return $"Endpoint({string.Join(", ", parts)})";
        }
    }

    /// <summary>
    /// Flags indicating what connection methods are available for a node.
    /// </summary>
    [Flags]
    public enum ConnectionEndpointFlags : byte
    {
        None = 0,

        /// <summary>
        /// IPv4 address is valid and usable.
        /// </summary>
        HasIPv4 = 1 << 0,

        /// <summary>
        /// IPv6 address is valid and usable (future).
        /// </summary>
        HasIPv6 = 1 << 1,

        /// <summary>
        /// Transport-specific ID is valid (SteamID, Relay allocation, etc.).
        /// </summary>
        HasTransportId = 1 << 2,

        /// <summary>
        /// This node can accept incoming connections (has open port/NAT).
        /// If not set, this node can only initiate outgoing connections.
        /// </summary>
        CanAcceptConnections = 1 << 3,

        /// <summary>
        /// Connection requires going through a relay (Unity Relay, Steam Relay, etc.).
        /// </summary>
        RequiresRelay = 1 << 4,

        /// <summary>
        /// This is a loopback address (127.0.0.1) - only valid for same-machine connections.
        /// </summary>
        IsLoopback = 1 << 5,

        /// <summary>
        /// This is a LAN address (192.168.x.x, 10.x.x.x, 172.16-31.x.x).
        /// </summary>
        IsLAN = 1 << 6,
    }
}
