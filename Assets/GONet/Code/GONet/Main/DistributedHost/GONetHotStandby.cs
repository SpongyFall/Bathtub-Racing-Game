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
 * -The ability to commercialize products built on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using System;
using System.Collections.Generic;
using System.Linq;
using GONet.Transport;
using GONet.Utils;
using MemoryPack;
using NetcodeIO.NET;

namespace GONet.DistributedHost
{
    #region Message Types

    /// <summary>
    /// Handshake message sent immediately after connecting to a peer's dormant server.
    /// Identifies who we are so the dormant server can populate its authority map.
    /// </summary>
    [MemoryPackable]
    public partial struct StandbyHelloMessage
    {
        /// <summary>Our session authority ID.</summary>
        public ushort AuthorityId;

        /// <summary>Session GUID for validation.</summary>
        public long SessionGUID;

        /// <summary>
        /// Secret token for validation (hash of session + authority).
        /// Prevents malicious actors from spoofing authority IDs.
        /// </summary>
        public uint SecretToken;

        /// <summary>The port our dormant server is listening on (for reciprocal connection).</summary>
        public ushort DormantPort;

        /// <summary>Virtual port if using a transport that supports them (e.g., Steam).</summary>
        public int VirtualPort;

        public static uint ComputeSecretToken(long sessionGUID, ushort authorityId)
        {
            // Simple hash combining session and authority for validation
            unchecked
            {
                uint hash = 2166136261;
                hash = (hash ^ (uint)(sessionGUID & 0xFFFFFFFF)) * 16777619;
                hash = (hash ^ (uint)(sessionGUID >> 32)) * 16777619;
                hash = (hash ^ authorityId) * 16777619;
                return hash;
            }
        }
    }

    /// <summary>
    /// Response to StandbyHello - confirms the connection was accepted.
    /// </summary>
    [MemoryPackable]
    public partial struct StandbyHelloAckMessage
    {
        /// <summary>Authority ID of the dormant server owner.</summary>
        public ushort ServerAuthorityId;

        /// <summary>True if handshake was accepted.</summary>
        public bool Accepted;
    }

    /// <summary>
    /// Lightweight keepalive message for standby connections.
    /// Sent periodically to maintain the warm connection.
    /// </summary>
    [MemoryPackable]
    public partial struct StandbyKeepaliveMessage
    {
        /// <summary>Sender's authority ID.</summary>
        public ushort AuthorityId;

        /// <summary>Sequence number for detecting packet loss.</summary>
        public uint Sequence;

        /// <summary>
        /// Sender's local timestamp when this keepalive was sent (ticks).
        /// Used for RTT measurement: receiver echoes this back in their next keepalive.
        /// </summary>
        public long SentTimestampTicks;

        /// <summary>
        /// Echoed timestamp from the peer's last keepalive.
        /// When receiver sees their own timestamp echoed back, they can calculate RTT.
        /// Value of 0 means no echo (old version or first keepalive).
        /// </summary>
        public long EchoTimestampTicks;
    }

    /// <summary>
    /// Message sent by new host to all connected clients to signal promotion.
    /// Clients receiving this switch their standby connection to active.
    /// </summary>
    [MemoryPackable]
    public partial struct SessionPromoteMessage
    {
        /// <summary>New host epoch (must be higher than current to be accepted).</summary>
        public uint HostEpoch;

        /// <summary>Session GUID for validation.</summary>
        public long SessionGUID;

        /// <summary>New host's authority ID.</summary>
        public ushort HostAuthorityId;

        /// <summary>Current game tick for time synchronization.</summary>
        public long CurrentTick;

        /// <summary>
        /// GONetIds that were destroyed during host promotion and must be despawned on clients.
        /// This is delivered via the DistributedHost channel so clients can apply it BEFORE the
        /// ReliableNetcode reset suppresses reliable traffic during failover switchover.
        /// </summary>
        public uint[] DeferredDespawnGONetIds;
    }

    /// <summary>
    /// Fast mesh heartbeat for application-level liveness detection.
    /// Sent unreliably at high frequency (10Hz) over the hot standby mesh.
    /// This provides redundant failover detection in case main server heartbeats are delayed.
    /// </summary>
    [MemoryPackable]
    public partial struct MeshHeartbeatMessage
    {
        /// <summary>Sender's authority ID (the host sending the heartbeat).</summary>
        public ushort HostAuthorityId;

        /// <summary>Current host epoch for validation.</summary>
        public uint HostEpoch;
    }

    /// <summary>
    /// Unreliable handshake to coordinate a full ReliableNetcode reset across a connection.
    /// Needed after host failover to prevent reliable sequence/ACK state from stalling on one side.
    /// </summary>
    [MemoryPackable]
    public partial struct ReliabilityResetRequestMessage
    {
        public uint HostEpoch;
        public uint ReliableSessionId;
    }

    /// <summary>
    /// Server->client commit message indicating the server has reset its reliability state for this connection.
    /// Client should reset upon receipt and respond with <see cref="ReliabilityResetCompleteMessage"/>.
    /// </summary>
    [MemoryPackable]
    public partial struct ReliabilityResetCommitMessage
    {
        public uint HostEpoch;
        public uint ReliableSessionId;
    }

    /// <summary>
    /// Client->server completion message indicating client reset is finished and reliable traffic may resume.
    /// </summary>
    [MemoryPackable]
    public partial struct ReliabilityResetCompleteMessage
    {
        public uint HostEpoch;
        public uint ReliableSessionId;
    }

    /// <summary>
    /// Single entry in the mesh topology sync message.
    /// Contains all information needed to establish a standby connection to a peer.
    /// </summary>
    [MemoryPackable]
    public partial struct MeshTopologyEntry
    {
        /// <summary>Peer's authority ID in the session.</summary>
        public ushort AuthorityId;

        /// <summary>Peer's persistent ID for correlation.</summary>
        public ulong PersistentId;

        /// <summary>Address of peer's dormant server (IP or transport-specific ID, e.g., SteamID).</summary>
        public string DormantServerAddress;

        /// <summary>Port of peer's dormant server (OS port for IP transports, virtual port for Steam P2P).</summary>
        public int DormantServerPort;
    }

    /// <summary>
    /// Message containing full mesh topology snapshot.
    /// Sent by host to new clients and broadcast after failover to ensure
    /// all nodes have complete mesh knowledge for deterministic tiebreaker.
    /// </summary>
    [MemoryPackable]
    public partial class MeshTopologySyncMessage
    {
        /// <summary>List of all known peer endpoints.</summary>
        public List<MeshTopologyEntry> Peers;

        /// <summary>Host epoch for validation.</summary>
        public int HostEpoch;
    }

    #endregion

    /// <summary>
    /// State of a standby connection to a peer's dormant server.
    /// </summary>
    public enum StandbyConnectionState
    {
        /// <summary>Connection not yet attempted.</summary>
        NotStarted,

        /// <summary>Connection attempt in progress.</summary>
        Connecting,

        /// <summary>TCP/UDP connected, waiting for handshake.</summary>
        AwaitingHandshake,

        /// <summary>Handshake complete, sending keepalives.</summary>
        Connected,

        /// <summary>Connection failed, will retry.</summary>
        Failed,

        /// <summary>Connection closed (peer left or shutdown).</summary>
        Closed,

        /// <summary>This connection is now the active connection (failover occurred).</summary>
        Active
    }

    /// <summary>
    /// Represents a standby connection to a peer's dormant server.
    /// These connections are kept warm and ready for instant failover.
    /// </summary>
    public class StandbyConnection
    {
        /// <summary>Authority ID of the peer we're connected to. Can be updated during re-keying after promotion.</summary>
        public ushort PeerAuthorityId { get; internal set; }

        /// <summary>The endpoint of the peer's dormant server.</summary>
        public GONetConnectionEndpoint PeerEndpoint { get; set; }

        /// <summary>Current state of this standby connection.</summary>
        public StandbyConnectionState State { get; internal set; } = StandbyConnectionState.NotStarted;

        /// <summary>The underlying GONet client connection (null if not connected).</summary>
        public GONetClient Client { get; internal set; }

        /// <summary>Time of last successful keepalive received.</summary>
        public float LastKeepaliveTime { get; internal set; }

        /// <summary>Sequence number from last keepalive received (server -&gt; client).</summary>
        public uint LastKeepaliveSequenceReceived { get; internal set; } = uint.MaxValue;

        /// <summary>Time when <see cref="LastKeepaliveSequenceReceived"/> last advanced.</summary>
        public float LastKeepaliveSequenceAdvancedTime { get; internal set; }

        /// <summary>Time of last watchdog-driven reliability reset attempt.</summary>
        public float LastWatchdogReliabilityResetTime { get; internal set; }

        /// <summary>Watchdog reliability reset attempts since last keepalive sequence progress.</summary>
        public int WatchdogReliabilityResetAttemptCount { get; internal set; }

        /// <summary>Number of consecutive connection failures.</summary>
        public int FailureCount { get; internal set; }

        /// <summary>Time of last connection attempt.</summary>
        public float LastConnectionAttemptTime { get; internal set; }

        /// <summary>Time the connection was established (for handshake timeout).</summary>
        public float ConnectedAtTime { get; internal set; }

        /// <summary>Time the last StandbyHello was sent (for resend pacing).</summary>
        public float LastHelloSentTime { get; internal set; }

        /// <summary>
        /// Whether we need to refresh the StandbyHello with a new authority ID.
        /// </summary>
        public bool PendingAuthorityRefresh { get; internal set; }

        /// <summary>Keepalive sequence number for outgoing messages.</summary>
        public uint KeepaliveSequence { get; internal set; }

        /// <summary>
        /// Transport instance for this mesh client connection (when using pluggable transport).
        /// Each mesh client has its own transport to avoid cross-delivery issues.
        /// </summary>
        public IGONetTransport MeshClientTransport { get; internal set; }

        /// <summary>
        /// Last timestamp we sent in a keepalive to this peer (for RTT calculation).
        /// </summary>
        public long LastSentTimestampTicks { get; internal set; }

        /// <summary>
        /// Last timestamp received from peer (to echo back in our next keepalive).
        /// </summary>
        public long PeerTimestampToEcho { get; internal set; }

        /// <summary>
        /// RTT measured via keepalive echo (milliseconds).
        /// This is our own measurement, independent of transport.
        /// </summary>
        public ushort KeepaliveRTT_Ms { get; internal set; }

        /// <summary>
        /// Gets the measured RTT to this peer in milliseconds.
        /// Prefers our own keepalive RTT measurement, falls back to transport if unavailable.
        /// </summary>
        public ushort MeasuredRTT_Ms
        {
            get
            {
                // Prefer our own keepalive-based RTT measurement
                if (KeepaliveRTT_Ms > 0)
                    return KeepaliveRTT_Ms;

                // Fall back to transport RTT if available
                if (Client?.connectionToServer == null)
                    return 0;

                float rttSeconds = Client.connectionToServer.RTT_RecentAverage;
                if (rttSeconds <= 0)
                    return 0;

                float rttMs = rttSeconds * 1000f;
                return (ushort)Math.Min(rttMs, ushort.MaxValue - 1);
            }
        }

        public StandbyConnection(ushort peerAuthorityId, GONetConnectionEndpoint endpoint)
        {
            PeerAuthorityId = peerAuthorityId;
            PeerEndpoint = endpoint;
        }
    }

    /// <summary>
    /// Tracks a pending connection to this node's dormant server.
    /// Used to enforce handshake timeout.
    /// </summary>
    internal class PendingDormantConnection
    {
        public ulong ConnectionUID;
        public float ConnectedAtTime;
        public bool HandshakeReceived;
    }

    /// <summary>
    /// Manages hot standby connections for host migration.
    ///
    /// Each node runs a dormant GONet server that accepts connections but doesn't process game traffic.
    /// All nodes maintain idle connections to all other nodes' dormant servers.
    /// On failover, traffic is instantly switched to the new host's already-established connection.
    ///
    /// Key features:
    /// - Transport-aware: Uses virtual ports for Steam, OS ports for others
    /// - Handshake protocol: StandbyHello identifies connections before failover
    /// - Authority map: Maps ConnectionID to AuthorityID for instant routing after promotion
    /// </summary>
    public class GONetHotStandbyManager
    {
        #region Constants

        /// <summary>Interval between keepalive messages (seconds).</summary>
        public const float KEEPALIVE_INTERVAL_SECONDS = 5.0f;

        /// <summary>Maximum time to wait before considering a standby connection dead (seconds).</summary>
        public const float KEEPALIVE_TIMEOUT_SECONDS = 15.0f;

        /// <summary>Delay between connection attempts to different peers (seconds).</summary>
        public const float CONNECTION_STAGGER_DELAY_SECONDS = 0.5f;

        /// <summary>Base delay before retrying a failed connection (seconds).</summary>
        public const float BASE_RETRY_DELAY_SECONDS = 2.0f;

        /// <summary>Maximum delay between retry attempts (seconds).</summary>
        public const float MAX_RETRY_DELAY_SECONDS = 30.0f;

        /// <summary>
        /// Maximum number of consecutive connection failures before abandoning a peer.
        /// After this many failures, the peer is removed from the mesh topology and
        /// no further connection attempts are made. This prevents infinite retry storms
        /// when all peers have disconnected.
        /// </summary>
        public const int MAX_CONSECUTIVE_FAILURES = 5;

        /// <summary>Maximum ports to try when finding available dormant server port.</summary>
        public const int MAX_PORT_ATTEMPTS = 100;

        /// <summary>Timeout for handshake after connection (seconds).</summary>
        public const float HANDSHAKE_TIMEOUT_SECONDS = 5.0f;

        /// <summary>
        /// While awaiting StandbyHelloAck, periodically resend StandbyHello.
        /// This hardens mesh establishment against early-packet drops on some transports (e.g., Steamworks Direct IP).
        /// </summary>
        private const float STANDBY_HELLO_RESEND_INTERVAL_SECONDS = 0.5f;

        /// <summary>
        /// Timeout (seconds) for outbound standby mesh clients to establish a transport connection to a peer.
        /// </summary>
        private const int STANDBY_MESH_CLIENT_CONNECT_TIMEOUT_SECONDS = 10;

        /// <summary>
        /// Additional grace (seconds) before treating a connection as stuck even if transport state doesn't flip.
        /// </summary>
        private const float STANDBY_MESH_CLIENT_CONNECT_TIMEOUT_GRACE_SECONDS = 2.0f;

        /// <summary>Virtual port for dormant server (used by Steam and similar transports).</summary>
        public const int DORMANT_VIRTUAL_PORT = 1;

        /// <summary>Virtual port for main server (used by Steam).</summary>
        public const int MAIN_VIRTUAL_PORT = 0;

	        // Message type IDs for hot standby protocol
	        internal const byte MSG_TYPE_STANDBY_HELLO = 30;
	        internal const byte MSG_TYPE_STANDBY_HELLO_ACK = 31;
	        internal const byte MSG_TYPE_STANDBY_KEEPALIVE = 32;
	        internal const byte MSG_TYPE_SESSION_PROMOTE = 33;
	        internal const byte MSG_TYPE_MESH_HEARTBEAT = 34;
	        internal const byte MSG_TYPE_RELIABILITY_RESET_REQUEST = 36;
	        internal const byte MSG_TYPE_RELIABILITY_RESET_COMMIT = 37;
	        internal const byte MSG_TYPE_RELIABILITY_RESET_COMPLETE = 38;

        /// <summary>
        /// Fast mesh heartbeat interval - sent unreliably at high frequency for failover detection.
        /// This is separate from transport-level keepalives and provides application-level liveness.
        /// </summary>
        public const float MESH_HEARTBEAT_INTERVAL_SECONDS = 0.1f; // 10Hz

        /// <summary>
        /// Minimum interval between host-driven mesh topology broadcasts.
        /// Prevents bursty join/leave activity from spamming full snapshots every frame.
        /// </summary>
        private const float MESH_TOPOLOGY_BROADCAST_MIN_INTERVAL_SECONDS = 0.25f;

        /// <summary>How often the mesh watchdog evaluates standby connections.</summary>
        private const float MESH_WATCHDOG_INTERVAL_SECONDS = 1.0f;

        /// <summary>
        /// If keepalive sequencing makes no progress for this long, attempt a coordinated reliability reset.
        /// Uses keepalive SEQUENCE advance (not just receipt time) so endless retransmits don't look healthy.
        /// </summary>
        private const float MESH_WATCHDOG_STALE_SECONDS = KEEPALIVE_INTERVAL_SECONDS * 2.0f; // 10s

        /// <summary>Minimum time between watchdog-driven reliability reset attempts per connection.</summary>
        private const float MESH_WATCHDOG_RESET_COOLDOWN_SECONDS = 5.0f;

        /// <summary>Escalation threshold: after this many watchdog resets without progress, force a reconnect.</summary>
        private const int MESH_WATCHDOG_MAX_RESETS_BEFORE_RECONNECT = 2;

        #endregion

        #region Fields

        private bool isInitialized = false;
        private bool isHost = false;

        /// <summary>Last processed mesh topology epoch - used for reset on failover boundary.</summary>
        private int lastMeshTopologyEpoch = int.MinValue;

        /// <summary>The dormant GONet server running on this node.</summary>
        private GONetServer dormantServer;

        /// <summary>Port the dormant server is listening on (OS port for non-virtual-port transports).</summary>
        private ushort dormantServerPort;

        /// <summary>Virtual port the dormant server is listening on (for transports that support it).</summary>
        private int dormantVirtualPort = -1;

        /// <summary>True if using virtual ports (Steam) vs OS ports (NetcodeIO).</summary>
        private bool usesVirtualPorts = false;

        /// <summary>Starting port used to (re)start the dormant server.</summary>
        private ushort dormantServerStartingPort;

        /// <summary>
        /// Transport provided during <see cref="Initialize"/> (used for virtual-port transports that share a transport instance).
        /// </summary>
        private IGONetTransport transportProvidedAtInitialize;

        /// <summary>
        /// Separate transport instance for the dormant server.
        /// CRITICAL: The dormant server must have its OWN transport to avoid cross-delivery
        /// of packets between main server and dormant server connections.
        /// </summary>
        private IGONetTransport dormantTransport;

        /// <summary>Standby connections to all known peers (outgoing connections we initiated).</summary>
        private readonly Dictionary<ushort, StandbyConnection> standbyConnections = new Dictionary<ushort, StandbyConnection>(128);
        // Tracks the host's original authority after re-keying to server authority so we can keep a dormant-server standby link.
        private ushort serverDormantShadowAuthorityId;

        /// <summary>
        /// Authority map for dormant server (incoming connections from peers).
        /// Maps ConnectionUID to AuthorityID - critical for routing after promotion.
        /// </summary>
        private readonly Dictionary<ulong, ushort> authorityMapByConnectionUID = new Dictionary<ulong, ushort>(128);

        /// <summary>Reverse lookup: AuthorityId to ConnectionUID.</summary>
        private readonly Dictionary<ushort, ulong> connectionUIDByAuthorityId = new Dictionary<ushort, ulong>(128);

        /// <summary>
        /// Tracks endpoint info for peers who connected TO our dormant server (incoming connections).
        /// This is separate from standbyConnections (outgoing) - we may know a peer exists via incoming
        /// connection before we've established an outgoing standby connection to them.
        /// Used by GetAllKnownPeerEndpoints() to include ALL known peers in mesh topology sync.
        /// </summary>
        private readonly Dictionary<ushort, GONetConnectionEndpoint> incomingPeerEndpoints = new Dictionary<ushort, GONetConnectionEndpoint>(128);

        /// <summary>Pending connections awaiting handshake (for timeout enforcement).</summary>
        private readonly Dictionary<ulong, PendingDormantConnection> pendingConnections = new Dictionary<ulong, PendingDormantConnection>(128);

        /// <summary>Lock for thread-safe access.</summary>
        private readonly object connectionLock = new object();

        /// <summary>Time of last connection establishment attempt.</summary>
        private float lastConnectionAttemptTime;

        /// <summary>Queue of peers waiting for connection establishment.</summary>
        private readonly Queue<ushort> connectionQueue = new Queue<ushort>();

        /// <summary>
        /// Tracks which peers are currently enqueued in <see cref="connectionQueue"/>.
        /// Prevents duplicate queue entries and avoids O(n) scans of <see cref="Queue{T}"/>.
        /// </summary>
        private readonly HashSet<ushort> connectionQueueSet = new HashSet<ushort>();

        /// <summary>Time of last keepalive sent.</summary>
        private float lastKeepaliveSentTime;

        /// <summary>Time of last mesh heartbeat sent (host only, for fast failover detection).</summary>
        private float lastMeshHeartbeatSentTime;

        /// <summary>True when host should broadcast a mesh topology snapshot on next eligible Update tick.</summary>
        private bool pendingMeshTopologyBroadcast;

        /// <summary>Time of last topology broadcast (host only).</summary>
        private float lastMeshTopologyBroadcastTime;

        /// <summary>Time of last mesh watchdog evaluation.</summary>
        private float lastMeshWatchdogTime;

        /// <summary>
        /// Previous elapsed time for detecting significant time jumps (e.g., time sync reset).
        /// When a jump > TIME_JUMP_THRESHOLD_SECONDS is detected, all keepalive timestamps
        /// must be refreshed to prevent false timeout detection.
        /// </summary>
        private float previousUpdateElapsedTime;
        private const float TIME_JUMP_THRESHOLD_SECONDS = 5.0f;

        /// <summary>
        /// Returns the current session GUID. Uses GONetMain.SessionGUID directly rather than caching
        /// because for clients, the SessionGUID may not be set until after initialization completes.
        /// This was causing the first client to connect to have sessionGUID=0 (unset) leading to
        /// Session GUID mismatch errors and 0/0 mesh status.
        /// </summary>
        private long sessionGUID => GONetMain.SessionGUID;

        /// <summary>Reusable buffer for serialization.</summary>
        private byte[] sendBuffer = new byte[128];

        /// <summary>
        /// Pending SessionPromote message to retry sending to peers who weren't ready.
        /// When not null, we retry sending on each Update until all peers receive it or timeout.
        /// </summary>
        private SessionPromoteMessage? pendingSessionPromote;

        /// <summary>Time when we started trying to send SessionPromote retries.</summary>
        private float sessionPromoteRetryStartTime;

        /// <summary>Maximum time to retry sending SessionPromote (seconds).</summary>
        private const float SESSION_PROMOTE_RETRY_TIMEOUT = 5.0f;

        /// <summary>Interval between SessionPromote retry attempts.</summary>
        private const float SESSION_PROMOTE_RETRY_INTERVAL = 0.5f;

	        /// <summary>Time of last SessionPromote retry attempt.</summary>
	        private float lastSessionPromoteRetryTime;

	        /// <summary>Peers that have NOT yet received SessionPromote.</summary>
	        private readonly HashSet<ushort> peersAwaitingSessionPromote = new HashSet<ushort>();

	        // FAILOVER RELIABILITY RESET (December 2025):
	        // Coordinates a full ReliableNetcode reset over the existing standby connection to prevent
	        // post-failover reliable sequence/ACK stalls (e.g., endless DUP receives / retransmits).
		        private const float RELIABILITY_RESET_RETRY_INTERVAL_SECONDS = 0.25f;
		        private const float RELIABILITY_RESET_TIMEOUT_SECONDS = 3.0f;

		        private static uint GenerateReliableSessionId()
		        {
		            uint id = unchecked((uint)GUID.Generate().AsInt64());
		            return id == 0 ? 1u : id;
		        }

		        // Client-side state (any node initiating a coordinated reset on an outbound client connection).
		        private sealed class PendingReliabilityResetClientState
		        {
		            public uint HostEpoch;
		            public uint ReliableSessionId;
		            public float StartTime;
		            public float LastRequestSentTime;
		            public int RequestSendCount;
		            public GONetClient Client;
		            public GONetConnection Connection;
		            public ushort? StandbyHelloPeerAuthorityId;
		        }

		        private readonly Dictionary<ulong, PendingReliabilityResetClientState> pendingReliabilityResetsByClientConnectionUID =
		            new Dictionary<ulong, PendingReliabilityResetClientState>(128);

		        private readonly Dictionary<ulong, uint> lastCompletedReliabilityResetEpochByClientConnectionUID =
		            new Dictionary<ulong, uint>(128);

		        private readonly Dictionary<ulong, uint> lastCompletedReliabilityResetSessionIdByClientConnectionUID =
		            new Dictionary<ulong, uint>(128);

                private static ulong GetClientConnectionUID(GONetClient client)
                {
                    return client?.connectionToServer?.InitiatingClientConnectionUID
                           ?? client?.Connection?.InitiatingClientConnectionUID
                           ?? 0;
                }

                private void CleanupClientReliabilityResetTracking_NoLock(ulong connectionUid)
                {
                    if (connectionUid == 0) return;
                    pendingReliabilityResetsByClientConnectionUID.Remove(connectionUid);
                    lastCompletedReliabilityResetEpochByClientConnectionUID.Remove(connectionUid);
                    lastCompletedReliabilityResetSessionIdByClientConnectionUID.Remove(connectionUid);
                }

                private void CleanupClientReliabilityResetTracking_NoLock(GONetClient client)
                {
                    CleanupClientReliabilityResetTracking_NoLock(GetClientConnectionUID(client));
                }

                private void CleanupServerReliabilityResetTracking_NoLock(ulong connectionUid)
                {
                    if (connectionUid == 0) return;
                    pendingReliabilityResetsByConnectionUID.Remove(connectionUid);
                }

	        // Server-side state (this node as host responding to clients).
	        private sealed class PendingReliabilityResetServerState
	        {
	            public uint HostEpoch;
	            public uint ReliableSessionId;
	            public float StartTime;
	            public float LastCommitSentTime;
	            public int CommitSendCount;
	        }

	        private readonly Dictionary<ulong, PendingReliabilityResetServerState> pendingReliabilityResetsByConnectionUID =
	            new Dictionary<ulong, PendingReliabilityResetServerState>(128);

	        #endregion

        #region Singleton

        private static GONetHotStandbyManager instance;
        public static GONetHotStandbyManager Instance => instance ??= new GONetHotStandbyManager();
        private GONetHotStandbyManager() { }

        #endregion

        #region Properties

        /// <summary>Whether the hot standby system is initialized and running.</summary>
        public bool IsInitialized => isInitialized;

        /// <summary>Port the dormant server is listening on (0 if not started).</summary>
        public ushort DormantServerPort => dormantServerPort;

        /// <summary>Virtual port for dormant server (-1 if not using virtual ports).</summary>
        public int DormantVirtualPort => dormantVirtualPort;

        /// <summary>True if transport uses virtual ports.</summary>
        public bool UsesVirtualPorts => usesVirtualPorts;

        /// <summary>The dormant server instance (null if not started).</summary>
        public GONetServer DormantServer => dormantServer;

        /// <summary>Number of active standby connections (outgoing).</summary>
        public int ActiveStandbyConnectionCount
        {
            get
            {
                lock (connectionLock)
                {
                    // First, check if we have any Active state connections (post-failover)
                    bool hasActiveNonServerConnection = false;
                    bool hasActiveServerConnection = false;
                    foreach (var conn in standbyConnections.Values)
                    {
                        if (conn.State == StandbyConnectionState.Active)
                        {
                            if (conn.PeerAuthorityId == GONetMain.OwnerAuthorityId_Server)
                            {
                                hasActiveServerConnection = true;
                            }
                            else
                            {
                                hasActiveNonServerConnection = true;
                            }
                        }
                    }

                    int count = 0;
                    foreach (var kvp in standbyConnections)
                    {
                        // After failover, we may have both [oldPeerId:Active] and [1023:Connected]
                        // pointing to the same physical host. Skip 1023 to avoid double-counting.
                        // BUT in the initial scenario (no failover yet), 1023 may be our only peer.
                        if (kvp.Key == GONetMain.OwnerAuthorityId_Server && hasActiveNonServerConnection)
                            continue;
                        if (serverDormantShadowAuthorityId != 0 &&
                            kvp.Key == serverDormantShadowAuthorityId &&
                            hasActiveServerConnection)
                        {
                            continue;
                        }

                        var conn = kvp.Value;
                        // Count connections that are actually alive:
                        // - Connected state: Pre-failover standby connection
                        // - Active state: Post-failover promoted connection (main traffic)
                        // Failed/Closed/Connecting states are never counted.
                        bool isLiveState = conn.State == StandbyConnectionState.Connected ||
                                          conn.State == StandbyConnectionState.Active;
                        bool isActuallyConnected = conn.Client != null && conn.Client.IsConnectedToServer;

                        if (isLiveState && isActuallyConnected)
                            count++;
                    }
                    return count;
                }
            }
        }

        /// <summary>Number of peers connected to our dormant server (incoming).</summary>
        public int DormantServerConnectionCount
        {
            get
            {
                lock (connectionLock)
                {
                    return authorityMapByConnectionUID.Count;
                }
            }
        }

        #endregion

        #region Lifecycle

        /// <summary>
        /// Initializes the hot standby system.
        /// Starts a dormant GONet server and begins establishing connections to known peers.
        /// </summary>
        /// <param name="startingPort">Starting port to try for the dormant server.</param>
        /// <param name="asHost">Whether this node is currently the host.</param>
        /// <param name="transport">Transport to use (determines virtual port vs OS port binding).</param>
        public void Initialize(ushort startingPort, bool asHost, IGONetTransport transport = null)
        {
            if (isInitialized) return;

            isHost = asHost;
            serverDormantShadowAuthorityId = 0;
            dormantServerStartingPort = startingPort;
            transportProvidedAtInitialize = transport;
            // Note: sessionGUID is now a property that reads GONetMain.SessionGUID directly.
            // This ensures we always use the correct value even if SessionGUID wasn't set yet during Initialize().
            GONetLog.Info($"[HotStandby-DEBUG] Initialize: sessionGUID={sessionGUID} (read from GONetMain.SessionGUID)");
            lastConnectionAttemptTime = (float)GONetMain.Time.ElapsedSeconds;
            lastKeepaliveSentTime = (float)GONetMain.Time.ElapsedSeconds;

            // Determine transport capabilities
            if (transport != null)
            {
                usesVirtualPorts = (transport.Capabilities & GONetTransportCapabilities.VirtualPorts) != 0;
            }
            else if (GONetGlobal.Instance != null && GONetGlobal.Instance.usePluggableTransport)
            {
                // CRITICAL FIX (Dec 2025): When transport is null, check GONetGlobal.transportType
                // to determine if we should use virtual ports. Previously assumed NetcodeIO always,
                // but Steamworks requires virtual ports and cannot connect to a NetcodeIO server!
                usesVirtualPorts = GONetGlobal.Instance.transportType == GONetTransportType.Steamworks;
                GONetLog.Info($"[HotStandby-DEBUG] Transport null, inferred usesVirtualPorts={usesVirtualPorts} from transportType={GONetGlobal.Instance.transportType}");
            }

            // Steamworks supports virtual ports for P2P (SteamID) standby mesh, but we also support Steamworks Direct IP
            // for local testing. When the client connected to the host via an IP address, keep the standby mesh in OS-port
            // mode so peers advertise/connect using IP:port instead of requiring SteamID + virtualPort.
            if (!asHost && usesVirtualPorts)
            {
                bool isSteamworks =
                    transport is SteamworksTransport ||
                    (GONetGlobal.Instance != null &&
                     GONetGlobal.Instance.usePluggableTransport &&
                     GONetGlobal.Instance.transportType == GONetTransportType.Steamworks);

                if (isSteamworks)
                {
                    string serverAddress = GONetGlobal.ServerIPAddress_Actual;
                    if (!string.IsNullOrWhiteSpace(serverAddress) && (serverAddress.Contains(".") || serverAddress.Contains(":")))
                    {
                        usesVirtualPorts = false;
                        GONetLog.Info($"[HotStandby] Steamworks Direct-IP session detected (serverAddress={serverAddress}) - using OS ports for standby mesh");
                    }
                }
            }

            // Start dormant server
            if (!StartDormantServer(startingPort, transport))
            {
                GONetLog.Error($"[HotStandby] Failed to start dormant server after {MAX_PORT_ATTEMPTS} attempts starting from port {startingPort}");
                // Continue anyway - we can still connect to others' dormant servers
            }

            // Subscribe to failover events for traffic switchover
            // Use the new event that includes the original authority ID for standby connection lookup
            GONetHostFailoverManager.Instance.OnNewHostDetectedWithOriginalId += OnFailover_NewHostDetectedWithOriginalId;
            GONetHostFailoverManager.Instance.OnSelfPromotedToHost += OnFailover_SelfPromoted;

            // CRITICAL FIX: Update the gossip endpoint to use the ACTUAL hot standby port.
            // Previously, the gossip endpoint used the connectivity probe port, and peers
            // calculated probe_port + 1 to find the hot standby. But due to port scanning,
            // hot_standby_port may not equal probe_port + 1. By setting the endpoint to the
            // actual hot standby port, peers can connect directly without calculation.
            UpdateGossipEndpointForHotStandby(transport, "Initialize");

            isInitialized = true;
            GONetLog.Info($"[HotStandby] Initialized (isHost={asHost}, port={dormantServerPort}, virtualPort={dormantVirtualPort}, usesVirtualPorts={usesVirtualPorts})");
        }

        private static bool TryGetLocalTransportSpecificId(IGONetTransport transport, out ulong transportId)
        {
            transportId = 0;

            if (transport is SteamworksTransport steamworksTransport)
            {
                transportId = steamworksTransport.LocalSteamId;
                return transportId != 0;
            }

            return false;
        }

        private void UpdateGossipEndpointForHotStandby(IGONetTransport transport, string context)
        {
            if (GONetGossipManager.Instance == null)
            {
                return;
            }

            if (usesVirtualPorts)
            {
                if (dormantVirtualPort <= 0)
                {
                    // Dormant server isn't running (or failed to start) - don't advertise a bogus endpoint.
                    return;
                }

                if (!TryGetLocalTransportSpecificId(transport, out ulong transportId))
                {
                    GONetLog.Warning($"[HotStandby] Cannot update gossip endpoint ({context}) for virtual ports - missing local transport ID");
                    return;
                }

                ushort virtualPort = (ushort)dormantVirtualPort;
                var endpoint = GONet.Utils.NetworkUtils.CreateLocalDualStackEndpoint(virtualPort);
                endpoint.TransportSpecificId = transportId;
                endpoint.Flags |= ConnectionEndpointFlags.HasTransportId;

                GONetGossipManager.Instance.SetLocalEndpoint(endpoint);
                GONetLog.Info($"[HotStandby] Updated gossip endpoint ({context}) to SteamID {transportId} virtualPort {virtualPort}");
                return;
            }

            if (dormantServerPort > 0)
            {
                var endpoint = GONet.Utils.NetworkUtils.CreateLocalDualStackEndpoint(dormantServerPort);
                GONetGossipManager.Instance.SetLocalEndpoint(endpoint);
                GONetLog.Info($"[HotStandby] Updated gossip endpoint ({context}) to hot standby port {dormantServerPort}");
            }
        }

        /// <summary>
        /// Called when failover completes and a NEW host is detected (someone else promoted).
        /// Triggers traffic switchover to the pre-established standby connection.
        /// </summary>
        /// <param name="newHostAuthorityId">The new host's authority ID (1023 after promotion)</param>
        /// <param name="originalAuthorityId">The peer's ORIGINAL authority ID before they promoted - use this for standby lookup</param>
        private void OnFailover_NewHostDetectedWithOriginalId(ushort newHostAuthorityId, ushort originalAuthorityId)
        {
            if (isHost)
            {
                GONetLog.Warning($"[HotStandby] Received new host notification but I am the host? Ignoring.");
                return;
            }

            GONetLog.Info($"[HotStandby] Failover detected - new host is {newHostAuthorityId} (original peer authority: {originalAuthorityId}), initiating traffic switchover");

            // CRITICAL: Use the ORIGINAL authority ID to look up the standby connection
            // Standby connections are keyed by the peer's original authority ID, not their post-promotion server ID (1023)
	            if (!TryActivateStandbyConnection(originalAuthorityId, GONetMain.HostEpoch))
	            {
	                GONetLog.Error($"[HotStandby] CRITICAL: Failed to activate standby connection to new host (original authority {originalAuthorityId})! Client will be disconnected.");
	            }
	        }

        /// <summary>
        /// Called when this node self-promotes to host.
        /// Promotes the dormant server to active and notifies all standby clients.
        /// </summary>
        private void OnFailover_SelfPromoted()
        {
            GONetLog.Info($"[HotStandby] Self-promoted to host, activating dormant server");
            OnBecameHost();
        }

        /// <summary>
        /// Called when this node steps down from host due to split-brain resolution.
        /// Restarts the dormant server in <see cref="GONetServerMode.DormantMesh"/> so the peer can rejoin the mesh as a client.
        /// </summary>
        public void OnDemotedFromHost()
        {
            if (!isInitialized) return;
            if (!isHost) return;

            isHost = false;
            serverDormantShadowAuthorityId = 0;
            pendingMeshTopologyBroadcast = false;

            GONetLog.Warning("[HotStandby] Demoted from host - restarting dormant server in DormantMesh mode");

            // Re-evaluate transport mode now that we're a client; Steamworks Direct-IP should use OS ports.
            if (usesVirtualPorts)
            {
                bool isSteamworks =
                    transportProvidedAtInitialize is SteamworksTransport ||
                    (GONetGlobal.Instance != null &&
                     GONetGlobal.Instance.usePluggableTransport &&
                     GONetGlobal.Instance.transportType == GONetTransportType.Steamworks);

                if (isSteamworks)
                {
                    string serverAddress = GONetGlobal.ServerIPAddress_Actual;
                    if (!string.IsNullOrWhiteSpace(serverAddress) && (serverAddress.Contains(".") || serverAddress.Contains(":")))
                    {
                        usesVirtualPorts = false;
                        GONetLog.Info($"[HotStandby] Steamworks Direct-IP session detected (serverAddress={serverAddress}) - using OS ports for standby mesh");
                    }
                }
            }

            // Stop the previously-promoted dormant server (it was ActiveHost while we were host).
            if (dormantServer != null)
            {
                try { dormantServer.Stop(); } catch { }
                dormantServer = null;
            }

            // Dispose dormant transport (OS-port transports only).
            if (dormantTransport != null)
            {
                try { dormantTransport.Dispose(); } catch { }
                dormantTransport = null;
            }

            lock (connectionLock)
            {
                authorityMapByConnectionUID.Clear();
                connectionUIDByAuthorityId.Clear();
                incomingPeerEndpoints.Clear();
                pendingConnections.Clear();
            }

            ushort restartPort = dormantServerStartingPort != 0
                ? dormantServerStartingPort
                : Math.Max((ushort)1, dormantServerPort);
	            if (!StartDormantServer(restartPort, transportProvidedAtInitialize))
	            {
	                GONetLog.Error($"[HotStandby] Failed to restart dormant server after demotion (startingPort={restartPort})");
	                return;
	            }

	            // Keep gossip endpoint aligned with actual dormant server binding (OS port or Steam virtual port).
	            UpdateGossipEndpointForHotStandby(transportProvidedAtInitialize, "DemotedFromHost");
	        }

        /// <summary>
        /// Shuts down the hot standby system.
        /// </summary>
        public void Shutdown()
        {
            if (!isInitialized) return;

            // Unsubscribe from failover events
            GONetHostFailoverManager.Instance.OnNewHostDetectedWithOriginalId -= OnFailover_NewHostDetectedWithOriginalId;
            GONetHostFailoverManager.Instance.OnSelfPromotedToHost -= OnFailover_SelfPromoted;
            serverDormantShadowAuthorityId = 0;

            // Close all standby connections
            lock (connectionLock)
            {
                foreach (var conn in standbyConnections.Values)
                {
                    if (conn.Client != null)
                    {
                        CleanupClientReliabilityResetTracking_NoLock(conn.Client);
                        try { conn.Client.Disconnect(); }
                        catch { }
                    }
                    // Dispose mesh client transport if present
                    if (conn.MeshClientTransport != null)
                    {
                        try { conn.MeshClientTransport.Dispose(); }
                        catch { }
                    }
                    conn.State = StandbyConnectionState.Closed;
                }
                standbyConnections.Clear();
                authorityMapByConnectionUID.Clear();
                connectionUIDByAuthorityId.Clear();
                incomingPeerEndpoints.Clear();
                pendingConnections.Clear();
                pendingReliabilityResetsByClientConnectionUID.Clear();
                lastCompletedReliabilityResetEpochByClientConnectionUID.Clear();
                lastCompletedReliabilityResetSessionIdByClientConnectionUID.Clear();
                pendingReliabilityResetsByConnectionUID.Clear();
                dormantClientLastKeepalive.Clear();
                dormantClientLastKeepaliveSequenceReceived.Clear();
                dormantClientLastKeepaliveSequenceAdvancedTime.Clear();
                dormantClientWatchdogLastResetAttemptTime.Clear();
                dormantClientWatchdogResetAttemptCount.Clear();
                dormantClientLastSentTimestamp.Clear();
                dormantClientTimestampToEcho.Clear();
                dormantClientRTT.Clear();
                dormantServerKeepaliveSequence.Clear();
            }

            // Stop dormant server
            if (dormantServer != null)
            {
                try { dormantServer.Stop(); }
                catch { }
                dormantServer = null;
            }

            // Dispose dormant transport (only for OS-port transports that created one)
            if (dormantTransport != null)
            {
                try { dormantTransport.Dispose(); }
                catch { }
                dormantTransport = null;
            }

            lock (connectionLock)
            {
                ClearConnectionQueue_NoLock();
            }
            dormantServerPort = 0;
            dormantVirtualPort = -1;
            isInitialized = false;

            GONetLog.Info("[HotStandby] Shut down");
        }

        /// <summary>
        /// FAILOVER FIX: Populates the dormant server's connection OwnerAuthorityIds using the authority map.
        ///
        /// During normal operation, GONetServer assigns OwnerAuthorityId when clients connect through the
        /// standard flow. But dormant server connections bypass this - they get OwnerAuthorityId=0.
        /// The authority map (populated via StandbyHello handshakes) knows the correct authority for each
        /// connection UID. This method transfers that knowledge to the actual connection objects.
        ///
        /// Must be called BEFORE SetPromotedServer() marks clients as initialized, otherwise sync data
        /// will be sent to connections with OwnerAuthorityId=0 (which won't route correctly).
        /// </summary>
        public void PopulateDormantServerConnectionAuthorities()
        {
            if (dormantServer == null)
            {
                GONetLog.Warning("[HotStandby] PopulateDormantServerConnectionAuthorities called but dormantServer is null");
                return;
            }

            int updatedCount = 0;
            int disconnectedServerAuthorityCount = 0;
            List<ulong> serverAuthorityConnectionUIDsToDisconnect = null;
            ushort serverAuthorityId = GONetMain.OwnerAuthorityId_Server;
            ushort outgoingAuthorityId = 0;
            bool hasOutgoingAuthority =
                GONetHostHandoffManager.Instance != null &&
                GONetHostHandoffManager.Instance.TryGetPendingOutgoingHostAuthorityId(out outgoingAuthorityId);
            lock (connectionLock)
            {
                for (int i = 0; i < dormantServer.numConnections; i++)
                {
                    GONetRemoteClient remoteClient = dormantServer.remoteClients[i];
                    if (remoteClient == null || remoteClient.ConnectionToClient == null) continue;

                    ulong connectionUID = remoteClient.ConnectionToClient.InitiatingClientConnectionUID;
                    if (authorityMapByConnectionUID.TryGetValue(connectionUID, out ushort authorityId))
                    {
                        // CRITICAL (Dec 2025): A dormant mesh server can have an inbound standby connection from the CURRENT host,
                        // which will identify as server authority (1023). When this node self-promotes, that connection becomes
                        // a stale "previous host" link and must NEVER be treated as a gameplay client.
                        if (authorityId == serverAuthorityId)
                        {
                            if (hasOutgoingAuthority && outgoingAuthorityId != 0)
                            {
                                authorityMapByConnectionUID[connectionUID] = outgoingAuthorityId;
                                connectionUIDByAuthorityId.Remove(serverAuthorityId);
                                if (connectionUIDByAuthorityId.TryGetValue(outgoingAuthorityId, out ulong existingUid) &&
                                    existingUid != connectionUID)
                                {
                                    connectionUIDByAuthorityId.Remove(outgoingAuthorityId);
                                }
                                connectionUIDByAuthorityId[outgoingAuthorityId] = connectionUID;

                                remoteClient.ConnectionToClient.OwnerAuthorityId = outgoingAuthorityId;
                                dormantServer.RegisterRemoteClientByAuthorityId(outgoingAuthorityId, remoteClient);
                                updatedCount++;

                                GONetLog.Warning($"[HotStandby] Remapped inbound standby connection from server authority {serverAuthorityId} " +
                                                 $"to outgoing host authority {outgoingAuthorityId} during handoff");
                                continue;
                            }

                            // Ensure the connection has a stable OwnerAuthorityId for any interim bookkeeping until disconnect processes.
                            remoteClient.ConnectionToClient.OwnerAuthorityId = authorityId;
                            dormantServer.RegisterRemoteClientByAuthorityId(authorityId, remoteClient);

                            // Remove from hot standby inbound tracking so mesh UI/topology and promotion messaging don't treat it as a peer.
                            authorityMapByConnectionUID.Remove(connectionUID);
                            connectionUIDByAuthorityId.Remove(authorityId);
                            incomingPeerEndpoints.Remove(authorityId);
                            pendingConnections.Remove(connectionUID);

                            if (serverAuthorityConnectionUIDsToDisconnect == null)
                            {
                                serverAuthorityConnectionUIDsToDisconnect = new List<ulong>(1);
                            }
                            serverAuthorityConnectionUIDsToDisconnect.Add(connectionUID);
                            disconnectedServerAuthorityCount++;

                            GONetLog.Warning($"[HotStandby] Detected inbound standby link identified as server authority {serverAuthorityId} (UID {connectionUID}). Disconnecting - not a gameplay client.");
                            continue;
                        }

                        if (remoteClient.ConnectionToClient.OwnerAuthorityId != authorityId)
                        {
                            remoteClient.ConnectionToClient.OwnerAuthorityId = authorityId;
                            updatedCount++;
                            GONetLog.Info($"[HotStandby] Set connection UID {connectionUID} OwnerAuthorityId to {authorityId}");
                        }

                        // CRITICAL FIX: Also register the remote client in the authority ID lookup dictionary.
                        // Without this, GetRemoteClientByAuthorityId() throws KeyNotFoundException during sync sending,
                        // because OnConnectionToClientAuthorityIdAssigned() never fires for dormant server connections.
                        dormantServer.RegisterRemoteClientByAuthorityId(authorityId, remoteClient);
                    }
                    else
                    {
                        GONetLog.Warning($"[HotStandby] Connection UID {connectionUID} not found in authority map - will have OwnerAuthorityId=0");
                    }
                }
            }

            if (serverAuthorityConnectionUIDsToDisconnect != null)
            {
                foreach (ulong uid in serverAuthorityConnectionUIDsToDisconnect)
                {
                    dormantServer.TryDisconnectClientByConnectionUID(
                        uid,
                        GONetTransportDisconnectReason.Kicked,
                        "stale inbound host standby link (AuthId=1023)");
                }
            }

            GONetLog.Info($"[HotStandby] PopulateDormantServerConnectionAuthorities: Updated {updatedCount} connection authority IDs");
            if (disconnectedServerAuthorityCount > 0)
            {
                GONetLog.Warning($"[HotStandby] Disconnected {disconnectedServerAuthorityCount} stale inbound server-authority standby link(s) during promotion");
            }
        }

        /// <summary>
        /// Collects RTT measurements to all connected standby peers.
        /// Used for host scoring to determine network centrality.
        /// </summary>
        /// <returns>Dictionary mapping peer authority ID to RTT in milliseconds</returns>
        public Dictionary<ushort, ushort> CollectPeerRTTs()
        {
            var result = new Dictionary<ushort, ushort>();

            lock (connectionLock)
            {
                foreach (var kvp in standbyConnections)
                {
                    ushort peerAuthorityId = kvp.Key;
                    StandbyConnection conn = kvp.Value;

                    // Only include connected peers with valid RTT
                    if (conn.State == StandbyConnectionState.Connected && conn.MeasuredRTT_Ms > 0)
                    {
                        result[peerAuthorityId] = conn.MeasuredRTT_Ms;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the RTT to a specific peer in milliseconds.
        /// Returns 0 if the peer is not connected or RTT is not available.
        /// </summary>
        public ushort GetPeerRTT(ushort peerAuthorityId)
        {
            lock (connectionLock)
            {
                if (standbyConnections.TryGetValue(peerAuthorityId, out var conn) &&
                    conn.State == StandbyConnectionState.Connected)
                {
                    return conn.MeasuredRTT_Ms;
                }
            }
            return 0;
        }

        #endregion

        #region Dormant Server

        /// <summary>
        /// Starts the dormant GONet server with transport-aware binding.
        /// </summary>
	        private bool StartDormantServer(ushort startingPort, IGONetTransport transport)
	        {
	            // Always start the dormant server on an OS port (port scanning handles conflicts).
	            // For transports that support virtual ports (Steam), we additionally configure the
	            // dormant transport instance to listen on virtual port 1 for P2P mesh connections.
	            int desiredVirtualPort = usesVirtualPorts ? DORMANT_VIRTUAL_PORT : -1;
	            bool started = StartDormantServerOSPort(startingPort, transport);
	            dormantVirtualPort = started ? desiredVirtualPort : -1;
	            return started;
	        }

        /// <summary>
        /// Starts dormant server using virtual port (for transports like Steam).
        /// NOTE: For virtual port transports, we intentionally SHARE the transport with the main server.
        /// Virtual port transports (Steam, etc.) handle routing at the virtual port level internally,
        /// so cross-delivery is not possible - each virtual port has its own receive callback.
        /// This is different from OS-port transports (NetcodeIO) where we MUST use separate transports.
        /// </summary>
        private bool StartDormantServerVirtualPort(IGONetTransport transport)
        {
            try
            {
                // CRITICAL: Virtual port transports (Steamworks) REQUIRE a shared transport instance.
                // We cannot create a separate transport because virtual ports are multiplexed on the
                // same physical connection. If transport is null, we cannot start the dormant server.
                if (transport == null)
                {
                    GONetLog.Error($"[HotStandby] Cannot start dormant server with virtual ports - transport instance is required but was null. " +
                                   $"The distributed host feature requires the main transport to be passed to Initialize() for Steamworks. " +
                                   $"Hot standby will be disabled for this session.");
                    return false;
                }

                // For virtual ports, we use the same physical address but different virtual port
                // The transport handles the multiplexing internally, so sharing transport is safe
                dormantVirtualPort = DORMANT_VIRTUAL_PORT;
                dormantServerPort = (ushort)GONetGlobal.ServerPort_Actual; // Same OS port

                // NOTE: We share the transport for virtual ports because:
                // 1. Virtual port transports can't have multiple instances on the same physical port
                // 2. The transport handles routing by virtual port internally, preventing cross-delivery
                dormantServer = new GONetServer(
                    maxClientCount: 32,
                    port: dormantServerPort,
                    transport: transport,
                    mode: GONetServerMode.DormantMesh
                );

                // Subscribe to connection events
                dormantServer.ClientConnected += OnDormantServerClientConnected;
                dormantServer.ClientDisconnected += OnDormantServerClientDisconnected;

                // TODO: Configure transport to use virtual port 1
                // This requires transport-specific API that may vary

                if (!dormantServer.Start(dormantServerPort))
                {
                    dormantServer = null;
                    return false;
                }

                GONetLog.Info($"[HotStandby] Dormant server started on virtual port {dormantVirtualPort}");
                return true;
            }
            catch (Exception ex)
            {
                GONetLog.Error($"[HotStandby] Failed to start dormant server on virtual port: {ex.Message}");
                dormantServer = null;
                return false;
            }
        }

        /// <summary>
        /// Starts dormant server using OS port scanning (for NetcodeIO and similar).
        /// CRITICAL FIX (Dec 2025): Creates a SEPARATE transport instance for the dormant server
        /// to prevent cross-delivery of packets between main server and dormant server connections.
        /// </summary>
	        private bool StartDormantServerOSPort(ushort startingPort, IGONetTransport mainTransport)
	        {
            // CRITICAL FIX (Dec 2025): Create a SEPARATE transport instance for the dormant server.
            // Sharing the same transport causes all connections (main + dormant) to subscribe
            // to the same OnMessageReceived event, leading to:
            // 1. Packets being delivered to wrong connections
            // 2. False ACKs from dormant mesh connections
            // 3. Reliable message deadlock (clients think server ACKed but it didn't)
            //
            // Create a separate transport for the dormant server if pluggable transport is enabled.
            // Each transport implementation is responsible for being multi-instance safe:
            // - NetcodeIO: Independent instances with separate message handlers
            // - Steamworks: Filters callbacks by listen socket ownership (m_hListenSocket check)
            bool shouldCreateSeparateTransport = GONetGlobal.Instance != null && GONetGlobal.Instance.usePluggableTransport;

	            if (shouldCreateSeparateTransport)
	            {
	                // Use factory to create correct transport type with standard GONet credentials
	                dormantTransport = GONetTransportFactory.CreateAndInitialize();
	                if (usesVirtualPorts && dormantTransport is SteamworksTransport steamworksTransport)
	                {
	                    steamworksTransport.ListenVirtualPort = DORMANT_VIRTUAL_PORT;
	                }
	                GONetLog.Info($"[HotStandby] Created separate {dormantTransport.GetType().Name} instance for dormant server");
	            }
            // else: old path (non-pluggable transport) - no separate transport needed

            // Use the dormant transport if we created one, otherwise null (old path has its own handling)
            var transportForDormant = dormantTransport;

            for (int attempt = 0; attempt < MAX_PORT_ATTEMPTS; attempt++)
            {
                ushort portToTry = (ushort)(startingPort + attempt);

                // Check for overflow
                if (portToTry < startingPort && attempt > 0)
                {
                    GONetLog.Warning($"[HotStandby] Port overflow, stopping search at port {portToTry}");
                    break;
                }

                if (TryStartServerOnPort(portToTry, transportForDormant))
                {
                    dormantServerPort = portToTry;
                    return true;
                }
            }

            // Clean up dormant transport on failure
            if (dormantTransport != null)
            {
                dormantTransport.Dispose();
                dormantTransport = null;
            }

            return false;
        }

        /// <summary>
        /// Attempts to start the dormant server on a specific port.
        /// </summary>
        private bool TryStartServerOnPort(ushort port, IGONetTransport transport)
        {
            try
            {
                // Check if port is already in use
                if (NetworkUtils.IsLocalPortListening(port))
                {
                    return false;
                }

                dormantServer = new GONetServer(
                    maxClientCount: 32,
                    port: port,
                    transport: transport,
                    mode: GONetServerMode.DormantMesh
                );

                // Subscribe to connection events
                dormantServer.ClientConnected += OnDormantServerClientConnected;
                dormantServer.ClientDisconnected += OnDormantServerClientDisconnected;

                if (!dormantServer.Start(port))
                {
                    dormantServer = null;
                    return false;
                }

                GONetLog.Info($"[HotStandby] Dormant server started on OS port {port}");
                return true;
            }
            catch (Exception ex)
            {
                GONetLog.Debug($"[HotStandby] Failed to bind port {port}: {ex.Message}");
                dormantServer = null;
                return false;
            }
        }

        private void OnDormantServerClientConnected(GONetConnection_ServerToClient connection)
        {
            float now = (float)GONetMain.Time.ElapsedSeconds;

            lock (connectionLock)
            {
                // Add to pending connections, start handshake timeout
                var pending = new PendingDormantConnection
                {
                    ConnectionUID = connection.InitiatingClientConnectionUID,
                    ConnectedAtTime = now,
                    HandshakeReceived = false
                };
                pendingConnections[connection.InitiatingClientConnectionUID] = pending;
            }

            // DIAGNOSTIC: Set temporary ConnectionId until we know authority (helps debug cross-delivery)
            connection.ConnectionId = $"Dormant-UID{connection.InitiatingClientConnectionUID}";

            GONetLog.Info($"[HotStandby] Dormant server: client connected (UID: {connection.InitiatingClientConnectionUID}), awaiting handshake");
        }

        private void OnDormantServerClientDisconnected(GONetConnection_ServerToClient connection)
        {
            lock (connectionLock)
            {
                ulong uid = connection.InitiatingClientConnectionUID;

                // Remove from authority map
                if (authorityMapByConnectionUID.TryGetValue(uid, out ushort authorityId))
                {
                    authorityMapByConnectionUID.Remove(uid);
                    connectionUIDByAuthorityId.Remove(authorityId);
                    incomingPeerEndpoints.Remove(authorityId);
                    GONetLog.Info($"[HotStandby] Dormant server: authority {authorityId} disconnected");
                }

                // Remove from pending
                pendingConnections.Remove(uid);

                // Remove keepalive / watchdog / RTT tracking
                dormantClientLastKeepalive.Remove(uid);
                dormantClientLastKeepaliveSequenceReceived.Remove(uid);
                dormantClientLastKeepaliveSequenceAdvancedTime.Remove(uid);
                dormantServerKeepaliveSequence.Remove(uid);
                dormantClientWatchdogLastResetAttemptTime.Remove(uid);
                dormantClientWatchdogResetAttemptCount.Remove(uid);
                dormantClientLastSentTimestamp.Remove(uid);
                dormantClientTimestampToEcho.Remove(uid);
                dormantClientRTT.Remove(uid);

                CleanupServerReliabilityResetTracking_NoLock(uid);
            }
        }

        #endregion

        #region Handshake Protocol

        /// <summary>
        /// Handles incoming StandbyHello message on dormant server.
        /// Validates the sender and populates the authority map.
        /// </summary>
        public void HandleStandbyHello(StandbyHelloMessage hello, GONetConnection_ServerToClient connection)
        {
            bool shouldBroadcastTopology = false;
            ushort newPeerAuthorityId = 0;

            lock (connectionLock)
            {
                ulong uid = connection.InitiatingClientConnectionUID;

                // Validate the secret token
                uint expectedToken = StandbyHelloMessage.ComputeSecretToken(hello.SessionGUID, hello.AuthorityId);
                if (hello.SecretToken != expectedToken)
                {
                    GONetLog.Warning($"[HotStandby] Invalid secret token from authority {hello.AuthorityId}");
                    // Don't respond - let timeout kick in
                    return;
                }

                // Validate session GUID matches
                if (hello.SessionGUID != sessionGUID)
                {
                    GONetLog.Warning($"[HotStandby] Session GUID mismatch from authority {hello.AuthorityId}: received={hello.SessionGUID}, expected={sessionGUID}, GONetMain.SessionGUID={GONetMain.SessionGUID}");
                    return;
                }

                // The secret token validated above already proves the sender:
                // 1. Knows our session GUID (cryptographically tied to this session)
                // 2. Computed the token with their claimed authority ID
                //
                // Additional checks below are defense-in-depth but NOT required for security.
                // They provide logging context and may catch edge cases.
                bool isKnownViaGossip = GONetGossipManager.Instance.IsNodeKnown(hello.AuthorityId);
                bool isServerAuthority = hello.AuthorityId == GONetMain.OwnerAuthorityId_Server;
                bool isConnectedToOurMainServer = GONetMain.IsServer &&
                    GONetMain.gonetServer != null &&
                    GONetMain.gonetServer.TryGetRemoteClientByAuthorityId(hello.AuthorityId, out _);

                // Log the validation path for debugging
                if (!isKnownViaGossip && !isServerAuthority && !isConnectedToOurMainServer)
                {
                    // Secret token validated, so we accept even without other checks.
                    // This handles timing races where standby hello arrives before:
                    // - Gossip aggregate propagates (late-joiner case)
                    // - Main server registers the client (timing race)
                    GONetLog.Info($"[HotStandby] Accepting authority {hello.AuthorityId} via valid secret token " +
                        $"(not yet in gossip or main server registry - normal for late-joiners/timing races)");
                }
                else if (!isKnownViaGossip)
                {
                    string reason = isServerAuthority ? "server authority" : "connected to main server";
                    GONetLog.Info($"[HotStandby] Accepting authority {hello.AuthorityId} not yet in gossip - {reason}");
                }

                // Mark handshake received
                if (pendingConnections.TryGetValue(uid, out var pending))
                {
                    pending.HandshakeReceived = true;
                }

                ushort reportedAuthorityId = hello.AuthorityId;
                ushort mappedAuthorityId = reportedAuthorityId;
                if (reportedAuthorityId == GONetMain.OwnerAuthorityId_Server &&
                    GONetHostHandoffManager.Instance.TryGetPendingOutgoingHostAuthorityId(out ushort pendingOutgoingAuthorityIdForRemap) &&
                    pendingOutgoingAuthorityIdForRemap != 0)
                {
                    mappedAuthorityId = pendingOutgoingAuthorityIdForRemap;
                    GONetLog.Warning($"[HotStandby] Remapping StandbyHello from server authority {reportedAuthorityId} to outgoing host authority {mappedAuthorityId} (lossless handoff pending)");
                }

                // Add to authority map (handle authority changes on same connection)
                if (authorityMapByConnectionUID.TryGetValue(uid, out ushort previousAuthorityId) &&
                    previousAuthorityId != mappedAuthorityId)
                {
                    connectionUIDByAuthorityId.Remove(previousAuthorityId);
                    GONetLog.Warning($"[HotStandby] Authority remap for connection UID {uid}: {previousAuthorityId} -> {mappedAuthorityId}");
                }

                if (connectionUIDByAuthorityId.TryGetValue(mappedAuthorityId, out ulong existingUid) && existingUid != uid)
                {
                    connectionUIDByAuthorityId.Remove(mappedAuthorityId);
                }

                authorityMapByConnectionUID[uid] = mappedAuthorityId;
                connectionUIDByAuthorityId[mappedAuthorityId] = uid;
                connection.OwnerAuthorityId = mappedAuthorityId;

                // DIAGNOSTIC: Update ConnectionId now that we know authority
                connection.ConnectionId = $"Dormant-S->C{mappedAuthorityId}";

                // Initialize keepalive tracking for this client
                float now = (float)GONetMain.Time.ElapsedSeconds;
                dormantClientLastKeepalive[uid] = now;
                dormantClientLastKeepaliveSequenceReceived[uid] = uint.MaxValue;
                dormantClientLastKeepaliveSequenceAdvancedTime[uid] = now;
                dormantClientWatchdogLastResetAttemptTime.Remove(uid);
                dormantClientWatchdogResetAttemptCount.Remove(uid);

                if (mappedAuthorityId != reportedAuthorityId)
                {
                    GONetLog.Info($"[HotStandby] Authority map updated: UID {uid} -> Authority {mappedAuthorityId} (reported {reportedAuthorityId})");
                }
                else
                {
                    GONetLog.Info($"[HotStandby] Authority map updated: UID {uid} -> Authority {mappedAuthorityId}");
                }

                // Send ack
                var ack = new StandbyHelloAckMessage
                {
                    ServerAuthorityId = GONetMain.MyAuthorityId,
                    Accepted = true
                };
                SendStandbyMessage(MSG_TYPE_STANDBY_HELLO_ACK, ack, connection);

                // CRITICAL FIX (Dec 2025): Initiate reciprocal connection back to the client's dormant server.
                // Without this, the mesh is asymmetric - client connects to us but we don't connect back.
                // This is especially important for late-joiners who aren't discovered via gossip.
                bool alreadyHasConnection = standbyConnections.ContainsKey(hello.AuthorityId);
                GONetLog.Debug($"[HotStandby] Reciprocal check: DormantPort={hello.DormantPort}, alreadyHasConnection={alreadyHasConnection}");

                // CRITICAL FIX: Conditional reciprocal connection to server authority (1023).
                // - In INITIAL setup: Clients SHOULD connect to the server's dormant server for failover readiness.
                // - AFTER FAILOVER: The promoted peer is already in standbyConnections under their original authority ID.
                //   We should NOT add a duplicate "peer 1023" entry.
                //
                // Detection: If we have ANY Active state connection, we've been through SessionPromote (failover).
                // Note: isServerAuthority was already computed above (line 924)
                bool isPostFailover = false;
                foreach (var conn in standbyConnections.Values)
                {
                    if (conn.State == StandbyConnectionState.Active)
                    {
                        isPostFailover = true;
                        break;
                    }
                }

                // Check if this is an endpoint UPDATE for an existing peer (e.g., after promotion, new dormant server port)
                bool remoteLooksLikeTransportId = false;
                string remoteAddressRaw = connection.RemoteAddressString;
                if (!string.IsNullOrEmpty(remoteAddressRaw))
                {
                    remoteLooksLikeTransportId = ulong.TryParse(remoteAddressRaw, out _);
                }

                ushort updatedPort = (remoteLooksLikeTransportId && hello.VirtualPort > 0)
                    ? (ushort)hello.VirtualPort
                    : hello.DormantPort;

                bool needsEndpointUpdate = false;
                StandbyConnection existingConn = null;
                if (alreadyHasConnection)
                {
                    existingConn = standbyConnections[hello.AuthorityId];
                    if (updatedPort > 0 && existingConn.PeerEndpoint.Port != updatedPort)
                    {
                        string portKind = (remoteLooksLikeTransportId && hello.VirtualPort > 0) ? "virtual port" : "dormant port";
                        GONetLog.Info($"[HotStandby] Peer {hello.AuthorityId} has updated dormant {portKind}: {existingConn.PeerEndpoint.Port} -> {updatedPort}");
                        needsEndpointUpdate = true;
                    }
                }

                bool needsShadowConnection = false;
                if (isServerAuthority && isPostFailover && existingConn != null && existingConn.State == StandbyConnectionState.Active)
                {
                    if (serverDormantShadowAuthorityId == 0)
                    {
                        needsShadowConnection = true;
                    }
                    else if (standbyConnections.TryGetValue(serverDormantShadowAuthorityId, out var shadowConn))
                    {
                        bool shadowPortMismatch = updatedPort > 0 && shadowConn.PeerEndpoint.Port != updatedPort;
                        bool shadowDisconnected = shadowConn.State == StandbyConnectionState.Closed ||
                            shadowConn.State == StandbyConnectionState.Failed;
                        if (shadowPortMismatch || shadowDisconnected)
                        {
                            needsShadowConnection = true;
                        }
                    }
                    else
                    {
                        needsShadowConnection = true;
                    }
                }

                // CRITICAL FIX (Dec 2025): Only skip if we ALREADY HAVE a connection to authority 1023 AND no port update needed.
                // After cascading failover, the host starts a NEW dormant server on a different port.
                // Clients need to connect to this new dormant server for:
                // 1. Host's DormantServerConnectionCount to be accurate
                // 2. Future cascading failover (authorityMapByConnectionUID mapping)
                // Without this fix, clients never connect to host's new dormant → "1 of 0 peers" on host
                if (isServerAuthority && isPostFailover && alreadyHasConnection && !needsEndpointUpdate && !needsShadowConnection)
                {
                    GONetLog.Debug($"[HotStandby] Skipping reciprocal connection to server authority {hello.AuthorityId} - post-failover, already have matching connection");
                }
	                else if ((hello.DormantPort > 0 || hello.VirtualPort > 0) && (!alreadyHasConnection || needsEndpointUpdate || needsShadowConnection))
	                {
                    // Get the client's IP address from the connection (works with both old and new transport paths)
                    string remoteAddressWithPort = connection.RemoteAddressString;
                    GONetLog.Debug($"[HotStandby] RemoteAddressString: {remoteAddressWithPort ?? "NULL"}");

                    string remoteAddress = null;
                    if (!string.IsNullOrEmpty(remoteAddressWithPort))
                    {
                        // Parse IP from various formats:
                        // IPv4: "192.168.1.84:port"
                        // IPv6: "[::ffff:192.168.1.84]:port" or "[fe80::1]:port"
                        string addressPart = remoteAddressWithPort;

                        // Remove port if present (handle both IPv4 and bracketed IPv6 formats)
                        if (addressPart.StartsWith("["))
                        {
                            // IPv6 bracketed format: [address]:port
                            int bracketEnd = addressPart.IndexOf(']');
                            if (bracketEnd > 0)
                            {
                                addressPart = addressPart.Substring(1, bracketEnd - 1); // Remove [ and ]
                            }
                        }
                        else
                        {
                            // IPv4 format: address:port
                            int colonIndex = addressPart.LastIndexOf(':');
                            if (colonIndex > 0)
                            {
                                addressPart = addressPart.Substring(0, colonIndex);
                            }
                        }

                        // Handle IPv6-mapped IPv4 addresses (::ffff:192.168.1.x)
                        if (addressPart.StartsWith("::ffff:"))
                        {
                            remoteAddress = addressPart.Substring(7);
                        }
                        else if (addressPart.Contains(":"))
                        {
                            // Pure IPv6 - not supported for now, log and skip
                            GONetLog.Warning($"[HotStandby] Pure IPv6 address not supported for reciprocal connection: {addressPart}");
                        }
                        else
                        {
                            // Plain IPv4
                            remoteAddress = addressPart;
                        }
                    }
                    else
                    {
                        GONetLog.Warning($"[HotStandby] RemoteAddressString is null or empty");
                    }

	                    if (!string.IsNullOrEmpty(remoteAddress))
	                    {
	                        GONetLog.Debug($"[HotStandby] Parsed address: '{remoteAddress}' from '{remoteAddressWithPort}'");
	                        GONetConnectionEndpoint reciprocalEndpoint;
	                        string connectAddress;
	                        ushort connectPort;

	                        // For Steam P2P, RemoteAddressString is the SteamID; for Direct IP it's an IP:port string.
	                        if (ulong.TryParse(remoteAddress, out ulong remoteTransportId) && remoteTransportId != 0)
	                        {
	                            connectAddress = remoteTransportId.ToString();
	                            connectPort = hello.VirtualPort > 0 ? (ushort)hello.VirtualPort : (ushort)DORMANT_VIRTUAL_PORT;
	                            reciprocalEndpoint = GONetConnectionEndpoint.CreateTransportSpecific(remoteTransportId);
	                            reciprocalEndpoint.Port = connectPort;
	                        }
	                        else
	                        {
	                            connectAddress = remoteAddress;
	                            connectPort = hello.DormantPort;
	                            uint ipv4 = GONetConnectionEndpoint.ParseIPv4(remoteAddress);
	                            reciprocalEndpoint = GONetConnectionEndpoint.CreateLAN(ipv4, connectPort);
	                        }

	                        string updateTag = string.Empty;
	                        if (needsEndpointUpdate)
	                        {
	                            updateTag = " [ENDPOINT UPDATE]";
	                        }
	                        if (needsShadowConnection)
	                        {
	                            updateTag += " [SHADOW REFRESH]";
	                        }
	                        GONetLog.Info($"[HotStandby] Initiating reciprocal connection to authority {hello.AuthorityId} " +
	                            $"at {connectAddress}:{connectPort} (from StandbyHello){updateTag}");

                        // CRITICAL FIX (Dec 2025): Track incoming peer endpoint for mesh topology sync.
                        // This ensures GetAllKnownPeerEndpoints() includes peers who connected TO us,
                        // even before we complete the reciprocal outgoing connection to them.
                        incomingPeerEndpoints[hello.AuthorityId] = reciprocalEndpoint;

                        if (existingConn != null || standbyConnections.TryGetValue(hello.AuthorityId, out existingConn))
                        {
                            if (isServerAuthority &&
                                existingConn.State == StandbyConnectionState.Active &&
                                serverDormantShadowAuthorityId != 0 &&
                                serverDormantShadowAuthorityId != GONetMain.OwnerAuthorityId_Server)
                            {
                                // Keep main traffic on the Active server connection; update/create a shadow entry for the host's dormant server.
                                if (standbyConnections.TryGetValue(serverDormantShadowAuthorityId, out var shadowConn))
                                {
                                    bool shadowEndpointChanged =
                                        shadowConn.PeerEndpoint.Port != reciprocalEndpoint.Port ||
                                        shadowConn.PeerEndpoint.IPv4Address != reciprocalEndpoint.IPv4Address ||
                                        shadowConn.PeerEndpoint.IPv6AddressHigh != reciprocalEndpoint.IPv6AddressHigh ||
                                        shadowConn.PeerEndpoint.IPv6AddressLow != reciprocalEndpoint.IPv6AddressLow ||
                                        shadowConn.PeerEndpoint.TransportSpecificId != reciprocalEndpoint.TransportSpecificId;

                                    shadowConn.PeerEndpoint = reciprocalEndpoint;

                                    if (shadowEndpointChanged)
                                    {
                                        if (shadowConn.State == StandbyConnectionState.Active)
                                        {
                                            GONetLog.Info($"[HotStandby] Updated endpoint metadata for host dormant shadow {serverDormantShadowAuthorityId} (port={hello.DormantPort}) - not restarting");
                                        }
                                        else
                                        {
                                            GONetLog.Info($"[HotStandby] Restarting host dormant shadow connection {serverDormantShadowAuthorityId} due to endpoint update");

                                            if (shadowConn.Client != null)
                                            {
                                                var oldClient = shadowConn.Client;
                                                CleanupClientReliabilityResetTracking_NoLock(oldClient);
                                                shadowConn.Client = null;
                                                try { oldClient.Disconnect(); } catch { }
                                            }
                                            if (shadowConn.MeshClientTransport != null)
                                            {
                                                try { shadowConn.MeshClientTransport.Dispose(); } catch { }
                                                shadowConn.MeshClientTransport = null;
                                            }

                                            shadowConn.State = StandbyConnectionState.NotStarted;
                                            shadowConn.FailureCount = 0;
                                            shadowConn.WatchdogReliabilityResetAttemptCount = 0;
                                            EnqueueConnectionAttempt_NoLock(serverDormantShadowAuthorityId);
                                        }
                                    }
                                }
                                else
                                {
                                    var shadowConnNew = new StandbyConnection(serverDormantShadowAuthorityId, reciprocalEndpoint);
                                    standbyConnections[serverDormantShadowAuthorityId] = shadowConnNew;
                                    EnqueueConnectionAttempt_NoLock(serverDormantShadowAuthorityId);
                                    GONetLog.Info($"[HotStandby] Created host dormant shadow connection {serverDormantShadowAuthorityId} at port {hello.DormantPort}");
                                }
                            }
                            else
                            {
                                existingConn.PeerEndpoint = reciprocalEndpoint;

                                if (needsEndpointUpdate || needsShadowConnection)
                                {
                                    // Never disrupt Active game traffic; only restart standby mesh clients.
                                    if (existingConn.State == StandbyConnectionState.Active)
                                    {
                                        // CRITICAL FIX (Dec 2025): When we have an Active game connection to the server
                                        // and receive a StandbyHello with a NEW dormant port, we need to create a
                                        // SEPARATE standby connection to the server's dormant server. The Active
                                        // connection is for game traffic; the new connection is for standby mesh health.
                                        bool isFromServer = hello.AuthorityId == GONetMain.OwnerAuthorityId_Server;
                                        bool hasClient = existingConn.Client != null;
                                        bool clientConnected = hasClient && existingConn.Client.IsConnectedToServer;
                                        GONetLog.Debug($"[HotStandby] Server dormant shadow check: isFromServer={isFromServer}, hasClient={hasClient}, clientConnected={clientConnected}, hello.Auth={hello.AuthorityId}, serverAuth={GONetMain.OwnerAuthorityId_Server}");

                                        // If we're a client and this StandbyHello is from the server, create a shadow
                                        // connection to the server's dormant server. The Active connection is for game traffic.
                                        if (isFromServer && !GONetMain.IsServer)
                                        {
                                            // Check if we already have a dormant shadow connection
                                            if (serverDormantShadowAuthorityId == 0)
                                            {
                                                // Use a synthetic authority ID for the server's dormant connection
                                                // (server authority - 1, which won't conflict with real authorities)
                                                serverDormantShadowAuthorityId = (ushort)(GONetMain.OwnerAuthorityId_Server - 1);
                                            }

                                            if (!standbyConnections.ContainsKey(serverDormantShadowAuthorityId) ||
                                                standbyConnections[serverDormantShadowAuthorityId].State == StandbyConnectionState.Closed ||
                                                standbyConnections[serverDormantShadowAuthorityId].State == StandbyConnectionState.Failed)
                                            {
                                                var dormantConn = new StandbyConnection(serverDormantShadowAuthorityId, reciprocalEndpoint);
                                                standbyConnections[serverDormantShadowAuthorityId] = dormantConn;
                                                EnqueueConnectionAttempt_NoLock(serverDormantShadowAuthorityId);
                                                GONetLog.Info($"[HotStandby] Created server dormant shadow connection to port {hello.DormantPort} (shadow auth={serverDormantShadowAuthorityId}) while Active game connection exists");
                                            }
                                            else
                                            {
                                                // Update existing shadow connection endpoint
                                                var shadowConn = standbyConnections[serverDormantShadowAuthorityId];
                                                shadowConn.PeerEndpoint = reciprocalEndpoint;
                                                if (shadowConn.State != StandbyConnectionState.Connected && 
                                                    shadowConn.State != StandbyConnectionState.Active)
                                                {
                                                    shadowConn.State = StandbyConnectionState.NotStarted;
                                                    shadowConn.FailureCount = 0;
                                                    EnqueueConnectionAttempt_NoLock(serverDormantShadowAuthorityId);
                                                    GONetLog.Info($"[HotStandby] Restarting server dormant shadow connection to port {hello.DormantPort}");
                                                }
                                            }
                                        }
                                        else
                                        {
                                            GONetLog.Info($"[HotStandby] Updated endpoint metadata for Active connection to peer {hello.AuthorityId} (port={hello.DormantPort}) - not restarting");
                                        }
                                    }
                                    else
                                    {
                                        GONetLog.Info($"[HotStandby] Restarting standby connection to peer {hello.AuthorityId} due to endpoint update");

                                        if (existingConn.Client != null)
                                        {
                                            var oldClient = existingConn.Client;
                                            CleanupClientReliabilityResetTracking_NoLock(oldClient);
                                            existingConn.Client = null;
                                            try { oldClient.Disconnect(); } catch { }
                                        }
                                        if (existingConn.MeshClientTransport != null)
                                        {
                                            try { existingConn.MeshClientTransport.Dispose(); } catch { }
                                            existingConn.MeshClientTransport = null;
                                        }

                                        existingConn.State = StandbyConnectionState.NotStarted;
                                        existingConn.FailureCount = 0;
                                        existingConn.WatchdogReliabilityResetAttemptCount = 0;
                                        EnqueueConnectionAttempt_NoLock(hello.AuthorityId);
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Create standby connection entry and queue it (we're already in the lock)
                            var conn = new StandbyConnection(hello.AuthorityId, reciprocalEndpoint);
                            standbyConnections[hello.AuthorityId] = conn;
                            EnqueueConnectionAttempt_NoLock(hello.AuthorityId);
                        }

                        // CRITICAL FIX (Dec 2025): Flag that we should broadcast topology after lock releases.
                        // We just learned about a new peer's dormant server - broadcast to ALL clients so they
                        // can establish mesh connections. This fixes the issue where existing clients never
                        // learned about newly joined peers (snapshot was sent before StandbyHello arrived).
                        shouldBroadcastTopology = true;
                        newPeerAuthorityId = hello.AuthorityId;
                    }
                    else
                    {
                        GONetLog.Warning($"[HotStandby] Could not get remote address for reciprocal connection to authority {hello.AuthorityId}");
                    }
                }
            }

            ushort reconnectAuthorityId = hello.AuthorityId;
            if (reconnectAuthorityId == GONetMain.OwnerAuthorityId_Server &&
                GONetHostHandoffManager.Instance.TryGetPendingOutgoingHostAuthorityId(out ushort pendingOutgoingAuthorityId))
            {
                reconnectAuthorityId = pendingOutgoingAuthorityId;
                GONetLog.Info($"[HotStandby] Remapped outgoing host reconnect from server authority to {pendingOutgoingAuthorityId} (lossless handoff pending)");
            }

            GONetHostHandoffManager.Instance.NotifyOutgoingHostReconnected(reconnectAuthorityId);

            // CRITICAL FIX (Dec 2025): Broadcast mesh topology AFTER the lock releases.
            // This ensures all clients learn about the new peer's dormant server info.
            // Must be outside lock to avoid potential deadlocks with GONetMain.gonetServer.
            if (shouldBroadcastTopology && GONetMain.IsServer)
            {
                GONetLog.Info($"[HotStandby] Broadcasting mesh topology after learning new peer {newPeerAuthorityId}'s dormant server info");
                GONetGossipIntegration.SendMeshTopologyToClient(newPeerAuthorityId);
            }
        }

        /// <summary>
        /// Requests a StandbyHello refresh for an existing peer connection (authority changes after demotion).
        /// </summary>
        public bool RequestStandbyAuthorityRefresh(ushort peerAuthorityId, string reason)
        {
            lock (connectionLock)
            {
                if (!standbyConnections.TryGetValue(peerAuthorityId, out var conn))
                {
                    if (peerAuthorityId != GONetMain.OwnerAuthorityId_Server &&
                        serverDormantShadowAuthorityId != 0 &&
                        peerAuthorityId == serverDormantShadowAuthorityId &&
                        standbyConnections.TryGetValue(GONetMain.OwnerAuthorityId_Server, out var rekeyedConn))
                    {
                        conn = rekeyedConn;
                        GONetLog.Info($"[HotStandby] Authority refresh using re-keyed server connection for peer {peerAuthorityId} ({reason})");
                    }
                    else
                    {
                        GONetLog.Warning($"[HotStandby] Authority refresh skipped - no standby connection for peer {peerAuthorityId}");
                        return false;
                    }
                }

                conn.PendingAuthorityRefresh = true;
                return TrySendStandbyHelloRefresh_NoLock(conn, peerAuthorityId, reason);
            }
        }

        private bool TrySendStandbyHelloRefresh_NoLock(StandbyConnection conn, ushort peerAuthorityId, string reason)
        {
            if (conn == null || conn.Client == null || !conn.Client.IsConnectedToServer)
            {
                return false;
            }

            float now = (float)GONetMain.Time.ElapsedSeconds;
            var hello = new StandbyHelloMessage
            {
                AuthorityId = GONetMain.MyAuthorityId,
                SessionGUID = sessionGUID,
                SecretToken = StandbyHelloMessage.ComputeSecretToken(sessionGUID, GONetMain.MyAuthorityId),
                DormantPort = dormantServerPort,
                VirtualPort = dormantVirtualPort
            };

            try
            {
                SendStandbyMessageToServer(MSG_TYPE_STANDBY_HELLO, hello, conn.Client);
                conn.LastHelloSentTime = now;
                GONetLog.Info($"[HotStandby] Sent StandbyHello refresh to peer {peerAuthorityId} ({reason})");
                return true;
            }
            catch (Exception ex)
            {
                GONetLog.Warning($"[HotStandby] Failed StandbyHello refresh to peer {peerAuthorityId} ({reason}): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Handles incoming StandbyHelloAck on client side.
        /// </summary>
        public void HandleStandbyHelloAck(StandbyHelloAckMessage ack, ushort fromAuthorityId)
        {
            GONetLog.Warning($"[HotStandby-ACK-RECV] HandleStandbyHelloAck called: fromAuthorityId={fromAuthorityId}, Accepted={ack.Accepted}");

            bool shouldActivateVoluntary = false;
            bool shouldBroadcastTopology = false;
            uint targetEpoch = 0;
            var failoverManager = GONetHostFailoverManager.Instance;
            lock (connectionLock)
            {
                if (standbyConnections.TryGetValue(fromAuthorityId, out var conn))
                {
                    GONetLog.Warning($"[HotStandby-ACK-RECV] Found connection for peer {fromAuthorityId}: State={conn.State}");
                    if (conn.PendingAuthorityRefresh && ack.Accepted)
                    {
                        conn.PendingAuthorityRefresh = false;
                        GONetLog.Info($"[HotStandby] Authority refresh acknowledged by peer {fromAuthorityId}");
                    }

                    if (conn.State == StandbyConnectionState.AwaitingHandshake)
                    {
                        if (!ack.Accepted)
                        {
                            GONetLog.Warning($"[HotStandby-ACK-RECV] StandbyHello rejected by peer {fromAuthorityId} - will retry");
                            conn.State = StandbyConnectionState.Failed;
                            conn.FailureCount++;
                            conn.LastConnectionAttemptTime = (float)GONetMain.Time.ElapsedSeconds;
                            EnqueueConnectionAttempt_NoLock(fromAuthorityId);
                            return;
                        }

                        var oldState = conn.State;
                        conn.State = StandbyConnectionState.Connected;
                        conn.LastKeepaliveTime = (float)GONetMain.Time.ElapsedSeconds;
                        conn.LastKeepaliveSequenceReceived = uint.MaxValue;
                        conn.LastKeepaliveSequenceAdvancedTime = conn.LastKeepaliveTime;
                        conn.LastWatchdogReliabilityResetTime = 0f;
                        conn.WatchdogReliabilityResetAttemptCount = 0;
                        conn.FailureCount = 0;
                        GONetLog.Warning($"[MESH-CONN] STATE CHANGE: peer={fromAuthorityId} {oldState}->{conn.State} myAuth={GONetMain.MyAuthorityId}");

                        // CRITICAL FIX (Dec 2025): Broadcast mesh topology when outgoing connection becomes Connected.
                        // This ensures all clients learn about the peer's endpoint info after handoff.
                        // Without this, mesh UI shows incorrect peer counts ("0 of 1" / "1 of 0").
                        if (GONetMain.IsServer)
                        {
                            shouldBroadcastTopology = true;
                        }

                        if (failoverManager != null &&
                            failoverManager.DidVoluntarilyDemote &&
                            fromAuthorityId == failoverManager.VoluntaryHandoffTargetAuthorityId)
                        {
                            shouldActivateVoluntary = true;
                            targetEpoch = failoverManager.VoluntaryHandoffTargetEpoch;
                        }
                    }
                }
            }

            // CRITICAL FIX (Dec 2025): Broadcast mesh topology AFTER lock releases.
            // This updates all clients with the newly connected peer's endpoint info.
            if (shouldBroadcastTopology)
            {
                GONetLog.Info($"[HotStandby] Broadcasting mesh topology after outgoing connection to peer {fromAuthorityId} became Connected");
                GONetGossipIntegration.SendMeshTopologyToClient(fromAuthorityId);
            }

            if (shouldActivateVoluntary)
            {
                if (TryActivateStandbyConnection(fromAuthorityId, targetEpoch))
                {
                    GONetLog.Info($"[HotStandby] Voluntary demotion reconnect: activated standby connection to {fromAuthorityId} (epoch {targetEpoch})");
                }
                else
                {
                    GONetLog.Warning($"[HotStandby] Voluntary demotion reconnect: standby connection to {fromAuthorityId} not ready for activation");
                }
            }

            // CRITICAL FIX (Dec 2025): Notify handoff manager when outgoing host reconnects via OUTBOUND connection.
            // In voluntary handoff, the promoted host initiates an outbound connection TO the demoted host.
            // The demoted host responds with StandbyHelloAck (not StandbyHello), so the notification in
            // HandleStandbyHello never fires. Without this, lossless cleanup times out after 30s and
            // destroys objects that should be preserved.
            if (GONetHostHandoffManager.Instance.TryGetPendingOutgoingHostAuthorityId(out ushort pendingOutgoingAuthorityId) &&
                fromAuthorityId == pendingOutgoingAuthorityId)
            {
                GONetLog.Info($"[HotStandby] Outgoing host {fromAuthorityId} reconnected via outbound StandbyHelloAck (lossless handoff preserved)");
                GONetHostHandoffManager.Instance.NotifyOutgoingHostReconnected(fromAuthorityId);

                // CRITICAL FIX (Dec 2025): Send updated dormant port to reconnected peer.
                // After promotion, the new server starts a dormant server on a NEW port. The peer only knows
                // our old port from the initial StandbyHello. Without this update, the peer can't connect
                // to our dormant server, causing mesh UI to show "1/0" instead of "1/1".
                if (GONetMain.IsServer && dormantServerPort > 0)
                {
                    RequestStandbyAuthorityRefresh(fromAuthorityId, "post-promotion-dormant-port-update");
                }
            }
        }

        /// <summary>
        /// Handles incoming keepalive message (on client side - from dormant server we connected to).
        /// </summary>
        public void HandleStandbyKeepalive(StandbyKeepaliveMessage keepalive, ushort fromAuthorityId)
        {
            float now = (float)GONetMain.Time.ElapsedSeconds;
            // CRITICAL: Use wall-clock time for RTT calculation, not GONet session time.
            // GONetMain.Time.ElapsedTicks can have discontinuities during failover (time reset),
            // causing wildly inaccurate RTT values (1-4 seconds instead of 50-100ms).
            // HighResolutionTimeUtils.UtcNowTicks is monotonic and never resets.
            long nowTicks = HighResolutionTimeUtils.UtcNowTicks;
            lock (connectionLock)
            {
                if (standbyConnections.TryGetValue(fromAuthorityId, out var conn))
                {
                    conn.LastKeepaliveTime = now;
                    if (conn.LastKeepaliveSequenceReceived != keepalive.Sequence)
                    {
                        conn.LastKeepaliveSequenceReceived = keepalive.Sequence;
                        conn.LastKeepaliveSequenceAdvancedTime = now;
                        conn.WatchdogReliabilityResetAttemptCount = 0;
                    }

                    // Store peer's timestamp for echoing back in our next keepalive
                    if (keepalive.SentTimestampTicks > 0)
                    {
                        conn.PeerTimestampToEcho = keepalive.SentTimestampTicks;
                    }

                    // Calculate RTT if peer echoed our timestamp back
                    if (keepalive.EchoTimestampTicks > 0 && conn.LastSentTimestampTicks > 0)
                    {
                        // Peer echoed our timestamp - calculate RTT
                        long rttTicks = nowTicks - keepalive.EchoTimestampTicks;
                        if (rttTicks > 0 && rttTicks < TimeSpan.TicksPerSecond * 10) // Sanity check: < 10s
                        {
                            float rttMs = (float)rttTicks / TimeSpan.TicksPerMillisecond;
                            conn.KeepaliveRTT_Ms = (ushort)Math.Min(rttMs, ushort.MaxValue - 1);
                        }
                    }

                    // COMMENTED (log cleanup) - fires every 5 seconds per peer, spammy
                    //GONetLog.Debug($"[HotStandby] Standby client received keepalive seq={keepalive.Sequence} from authority {fromAuthorityId} (measuredRTT={conn.KeepaliveRTT_Ms}ms)");
                }
                else
                {
                    // COMMENTED (log cleanup) - can fire on stale keepalives
                    //GONetLog.Debug($"[HotStandby] Standby client received keepalive from unknown authority {fromAuthorityId}");
                }
            }
        }

        /// <summary>
        /// Handles incoming keepalive message on dormant server (from standby clients connected to us).
        /// </summary>
        public void HandleStandbyKeepaliveOnServer(StandbyKeepaliveMessage keepalive, GONetConnection_ServerToClient connection)
        {
            float now = (float)GONetMain.Time.ElapsedSeconds;
            // CRITICAL: Use wall-clock time for RTT calculation, not GONet session time.
            // GONetMain.Time.ElapsedTicks can have discontinuities during failover (time reset),
            // causing wildly inaccurate RTT values (1-4 seconds instead of 50-100ms).
            // HighResolutionTimeUtils.UtcNowTicks is monotonic and never resets.
            long nowTicks = HighResolutionTimeUtils.UtcNowTicks;
            lock (connectionLock)
            {
                ulong uid = connection.InitiatingClientConnectionUID;
                bool isNewEntry = !dormantClientLastKeepalive.ContainsKey(uid);
                dormantClientLastKeepalive[uid] = now;

                if (!dormantClientLastKeepaliveSequenceReceived.TryGetValue(uid, out uint lastSeq) || lastSeq != keepalive.Sequence)
                {
                    dormantClientLastKeepaliveSequenceReceived[uid] = keepalive.Sequence;
                    dormantClientLastKeepaliveSequenceAdvancedTime[uid] = now;
                    dormantClientWatchdogResetAttemptCount.Remove(uid);
                }

                // Store peer's timestamp for echoing back
                if (keepalive.SentTimestampTicks > 0)
                {
                    dormantClientTimestampToEcho[uid] = keepalive.SentTimestampTicks;
                }

                // Calculate RTT if peer echoed our timestamp back
                ushort measuredRtt = 0;
                if (keepalive.EchoTimestampTicks > 0 && dormantClientLastSentTimestamp.TryGetValue(uid, out long lastSent) && lastSent > 0)
                {
                    long rttTicks = nowTicks - keepalive.EchoTimestampTicks;
                    if (rttTicks > 0 && rttTicks < TimeSpan.TicksPerSecond * 10) // Sanity check: < 10s
                    {
                        float rttMs = (float)rttTicks / TimeSpan.TicksPerMillisecond;
                        measuredRtt = (ushort)Math.Min(rttMs, ushort.MaxValue - 1);
                        dormantClientRTT[uid] = measuredRtt;
                    }
                }
                else
                {
                    dormantClientRTT.TryGetValue(uid, out measuredRtt);
                }

                // COMMENTED (log cleanup) - fires every 5 seconds per client, spammy
                //GONetLog.Debug($"[HotStandby] Dormant server received keepalive from authority {keepalive.AuthorityId} (UID: {uid}, rtt={measuredRtt}ms){(isNewEntry ? " - new entry" : "")}");
            }
        }

	        /// <summary>
	        /// Handles incoming mesh heartbeat from the host (received via dormant server connection).
	        /// This provides redundant failover detection in addition to main server heartbeats.
	        /// </summary>
	        public void HandleMeshHeartbeat(MeshHeartbeatMessage heartbeat)
	        {
	            // Only non-hosts should process mesh heartbeats
	            if (isHost) return;

	            // Validate epoch - ignore stale heartbeats from old hosts
	            if (heartbeat.HostEpoch < GONetMain.HostEpoch)
	            {
	                GONetLog.Debug($"[HotStandby] Ignoring stale mesh heartbeat from epoch {heartbeat.HostEpoch} (current: {GONetMain.HostEpoch})");
	                return;
	            }

	            // Update the failover manager's heartbeat tracking
	            // This is the same as receiving a main server heartbeat - it proves the host is alive
	            GONetHostFailoverManager.Instance.OnMeshHeartbeatReceived(heartbeat.HostAuthorityId, heartbeat.HostEpoch);
	        }

	        #region Failover Reliability Reset (Unreliable Handshake)

	        /// <summary>
	        /// Host-side: client requests a coordinated reliability reset on this connection for the given epoch.
	        /// </summary>
			        public void HandleReliabilityResetRequest(ReliabilityResetRequestMessage request, GONetConnection_ServerToClient connection)
			        {
			            if (!isInitialized) return;
			            if (connection == null) return;

			            ulong connUid = connection.InitiatingClientConnectionUID;
			            bool isDormantServerConnection = dormantServer != null && ReferenceEquals(dormantServer.GetConnectionByUID(connUid), connection);
			            bool isMainServerConnection = GONetMain.gonetServer != null && ReferenceEquals(GONetMain.gonetServer.GetConnectionByUID(connUid), connection);

			            // Safety: only process reset requests on known server connections (dormant mesh or the active host server).
			            if (!isDormantServerConnection && !isMainServerConnection)
			            {
			                return;
			            }

			            // Only the host should allow reliability resets on the active game server connection.
			            if (isMainServerConnection && !isHost)
			            {
			                return;
			            }

			            if (request.HostEpoch == 0 || request.HostEpoch < GONetMain.HostEpoch)
			            {
			                GONetLog.Warning($"[HotStandby] Ignoring stale ReliabilityResetRequest for epoch {request.HostEpoch} (current={GONetMain.HostEpoch}) connUID={connUid}");
			                return;
			            }

				            float now = (float)GONetMain.Time.ElapsedSeconds;
				            ulong uid = connUid;
				            uint requestedSessionId = request.ReliableSessionId != 0 ? request.ReliableSessionId : GenerateReliableSessionId();

				            lock (connectionLock)
				            {
				                // If already pending for this connection, just re-send commit (client likely missed it).
				                if (pendingReliabilityResetsByConnectionUID.TryGetValue(uid, out var existing) && existing.HostEpoch == request.HostEpoch)
				                {
				                    if (existing.ReliableSessionId != requestedSessionId)
				                    {
				                        existing.ReliableSessionId = requestedSessionId;
				                        existing.StartTime = now;
				                        existing.LastCommitSentTime = 0f;
				                        existing.CommitSendCount = 0;

				                        // Suppress reliable traffic to avoid buffering messages from a different reliability session.
				                        connection.SuppressReliableTraffic = true;

				                        // Reset server-side reliability state for this connection with the requested session id.
				                        connection.ResetReliabilityLayer(requestedSessionId);
				                    }

				                    if (now - existing.LastCommitSentTime >= RELIABILITY_RESET_RETRY_INTERVAL_SECONDS)
				                    {
				                        var commit = new ReliabilityResetCommitMessage { HostEpoch = request.HostEpoch, ReliableSessionId = existing.ReliableSessionId };
				                        SendStandbyMessageUnreliable(MSG_TYPE_RELIABILITY_RESET_COMMIT, commit, connection);
				                        existing.LastCommitSentTime = now;
				                        existing.CommitSendCount++;
				                    }
				                    return;
				                }

				                // Suppress reliable traffic to avoid buffering messages from a different reliability session.
				                connection.SuppressReliableTraffic = true;

				                // Reset server-side reliability state for this connection.
				                connection.ResetReliabilityLayer(requestedSessionId);

				                // Send commit over UNRELIABLE (reliable stream may be reset/misaligned during failover).
				                var commitMsg = new ReliabilityResetCommitMessage { HostEpoch = request.HostEpoch, ReliableSessionId = requestedSessionId };
				                SendStandbyMessageUnreliable(MSG_TYPE_RELIABILITY_RESET_COMMIT, commitMsg, connection);

				                pendingReliabilityResetsByConnectionUID[uid] = new PendingReliabilityResetServerState
				                {
				                    HostEpoch = request.HostEpoch,
				                    ReliableSessionId = requestedSessionId,
				                    StartTime = now,
				                    LastCommitSentTime = now,
				                    CommitSendCount = 1
				                };

				                GONetLog.Warning($"[HotStandby] Reliability reset commit sent to connUID={uid} epoch={request.HostEpoch} sessionId={requestedSessionId} (server reset complete)");
				            }
				        }

	        /// <summary>
	        /// Client-side: host indicates it has reset; client should reset and respond with complete.
	        /// </summary>
		        public void HandleReliabilityResetCommit(ReliabilityResetCommitMessage commit, GONetConnection relatedConnection)
		        {
		            if (!isInitialized) return;
		            if (relatedConnection == null) return;

		            if (commit.HostEpoch < GONetMain.HostEpoch)
		            {
	                GONetLog.Warning($"[HotStandby] Ignoring stale ReliabilityResetCommit epoch {commit.HostEpoch} (current={GONetMain.HostEpoch})");
		                return;
		            }

		            lock (connectionLock)
		            {
		                ulong uid = relatedConnection.InitiatingClientConnectionUID;
		                if (uid == 0)
		                {
		                    GONetLog.Warning($"[HotStandby] Ignoring ReliabilityResetCommit with connectionUID=0 epoch={commit.HostEpoch} (cannot correlate)");
		                    return;
		                }

		                if (!TryResolveClientForConnection_NoLock(relatedConnection, uid, out var clientToServer))
		                {
		                    GONetLog.Warning($"[HotStandby] Cannot resolve client for ReliabilityResetCommit connUID={uid} epoch={commit.HostEpoch} - cannot send COMPLETE");
		                    return;
		                }

			                // Idempotency per-connection: if we already completed this epoch, re-send COMPLETE (server may be retrying COMMIT).
			                if (lastCompletedReliabilityResetEpochByClientConnectionUID.TryGetValue(uid, out uint completedEpoch))
			                {
			                    if (commit.HostEpoch < completedEpoch)
			                    {
			                        return;
			                    }

			                    if (commit.HostEpoch == completedEpoch)
			                    {
			                        uint completedSessionId = 0;
			                        lastCompletedReliabilityResetSessionIdByClientConnectionUID.TryGetValue(uid, out completedSessionId);

			                        if (completedSessionId != 0 && commit.ReliableSessionId == completedSessionId)
			                        {
			                            var completeAgain = new ReliabilityResetCompleteMessage { HostEpoch = commit.HostEpoch, ReliableSessionId = commit.ReliableSessionId };
			                            SendStandbyMessageToServerUnreliable(MSG_TYPE_RELIABILITY_RESET_COMPLETE, completeAgain, clientToServer);
			                            return;
			                        }
			                    }
			                }

		                ushort? standbyPeerToHello = null;
		                bool hadPendingReset = pendingReliabilityResetsByClientConnectionUID.TryGetValue(uid, out var pendingClientState);
		                if (hadPendingReset)
		                {
		                    standbyPeerToHello = pendingClientState.StandbyHelloPeerAuthorityId;
		                    pendingReliabilityResetsByClientConnectionUID.Remove(uid);
		                }

		                // DECEMBER 2025 FIX: Only reset if we had a pending reset for this connection.
		                // New clients that connected AFTER failover don't have pending resets and shouldn't
		                // have their send queue cleared - their GONetLocal spawn is legitimate fresh data.
		                // See: client6-spawn-lost-reliability-reset-bug.md for full root cause analysis.
		                if (!hadPendingReset)
		                {
		                    // This is a new client connection with no stale state to reset.
		                    // Send COMPLETE to satisfy the server protocol without actually resetting.
		                    var skipResetCompleteMsg = new ReliabilityResetCompleteMessage { HostEpoch = commit.HostEpoch, ReliableSessionId = commit.ReliableSessionId };
		                    SendStandbyMessageToServerUnreliable(MSG_TYPE_RELIABILITY_RESET_COMPLETE, skipResetCompleteMsg, clientToServer);

		                    lastCompletedReliabilityResetEpochByClientConnectionUID[uid] = commit.HostEpoch;
		                    lastCompletedReliabilityResetSessionIdByClientConnectionUID[uid] = commit.ReliableSessionId;

		                    GONetLog.Warning($"[HotStandby] Skipped reliability reset for new client connUID={uid} epoch={commit.HostEpoch} - no pending reset (sent COMPLETE without resetting)");
		                    return;
		                }

			                // Suppress reliable traffic during reset to prevent sequence deadlocks.
			                relatedConnection.SuppressReliableTraffic = true;

			                // Reset client-side reliability state for this connection.
			                relatedConnection.ResetReliabilityLayer(commit.ReliableSessionId);

			                // Notify server that client reset is complete (unreliable).
			                var completeMsg = new ReliabilityResetCompleteMessage { HostEpoch = commit.HostEpoch, ReliableSessionId = commit.ReliableSessionId };
			                SendStandbyMessageToServerUnreliable(MSG_TYPE_RELIABILITY_RESET_COMPLETE, completeMsg, clientToServer);

			                lastCompletedReliabilityResetEpochByClientConnectionUID[uid] = commit.HostEpoch;
			                lastCompletedReliabilityResetSessionIdByClientConnectionUID[uid] = commit.ReliableSessionId;

			                // Resume reliable traffic locally; server resumes after processing Complete.
			                relatedConnection.SuppressReliableTraffic = false;

		                if (standbyPeerToHello.HasValue)
		                {
		                    TrySendStandbyHelloAfterReliabilityReset_NoLock(standbyPeerToHello.Value, clientToServer);
		                }

				                GONetLog.Warning($"[HotStandby] Reliability reset complete (client) connUID={uid} epoch={commit.HostEpoch} sessionId={commit.ReliableSessionId} - sent COMPLETE");
			            }
			        }

		        private bool TryResolveClientForConnection_NoLock(GONetConnection relatedConnection, ulong connectionUid, out GONetClient client)
		        {
		            client = null;

		            if (relatedConnection == null || connectionUid == 0)
		            {
		                return false;
		            }

		            if (pendingReliabilityResetsByClientConnectionUID.TryGetValue(connectionUid, out var pending) &&
		                pending.Client != null &&
		                pending.Client.Connection == relatedConnection)
		            {
		                client = pending.Client;
		                return true;
		            }

		            if (GONetMain.GONetClient != null && GONetMain.GONetClient.Connection == relatedConnection)
		            {
		                client = GONetMain.GONetClient;
		                return true;
		            }

		            foreach (var standby in standbyConnections.Values)
		            {
		                if (standby.Client != null && standby.Client.Connection == relatedConnection)
		                {
		                    client = standby.Client;
		                    return true;
		                }
		            }

		            return false;
		        }

		        private void TrySendStandbyHelloAfterReliabilityReset_NoLock(ushort peerAuthorityId, GONetClient client)
		        {
		            if (client == null) return;

		            if (!standbyConnections.TryGetValue(peerAuthorityId, out var conn))
		            {
		                return;
		            }

		            // Ignore callbacks from old/replaced client instances
		            if (conn.Client != client)
		            {
		                return;
		            }

            bool needsAuthorityRefresh = conn.PendingAuthorityRefresh;
            if (conn.State == StandbyConnectionState.Connected || conn.State == StandbyConnectionState.Active)
            {
                if (needsAuthorityRefresh)
                {
                    TrySendStandbyHelloRefresh_NoLock(conn, peerAuthorityId, "authority-refresh");
                }
                return;
            }

			            float now = (float)GONetMain.Time.ElapsedSeconds;
			            conn.State = StandbyConnectionState.AwaitingHandshake;
			            conn.ConnectedAtTime = now;
			            conn.LastHelloSentTime = now;

			            var hello = new StandbyHelloMessage
			            {
		                AuthorityId = GONetMain.MyAuthorityId,
		                SessionGUID = sessionGUID,
		                SecretToken = StandbyHelloMessage.ComputeSecretToken(sessionGUID, GONetMain.MyAuthorityId),
		                DormantPort = dormantServerPort,
		                VirtualPort = dormantVirtualPort
		            };

		            try
		            {
		                SendStandbyMessageToServer(MSG_TYPE_STANDBY_HELLO, hello, client);
		                GONetLog.Info($"[HotStandby] Connected to peer {peerAuthorityId}, sent handshake (after reliability reset)");
		            }
		            catch (Exception ex)
		            {
		                GONetLog.Warning($"[HotStandby] Failed to send handshake to {peerAuthorityId} (after reliability reset): {ex.Message}");
		                conn.State = StandbyConnectionState.Failed;
		                conn.FailureCount++;
		            }
		        }

	        /// <summary>
	        /// Host-side: client indicates it has reset; server may resume reliable traffic.
	        /// </summary>
			        public void HandleReliabilityResetComplete(ReliabilityResetCompleteMessage complete, GONetConnection_ServerToClient connection)
			        {
			            if (!isInitialized) return;
			            if (connection == null) return;

			            ulong connUid = connection.InitiatingClientConnectionUID;
			            bool isDormantServerConnection = dormantServer != null && ReferenceEquals(dormantServer.GetConnectionByUID(connUid), connection);
			            bool isMainServerConnection = GONetMain.gonetServer != null && ReferenceEquals(GONetMain.gonetServer.GetConnectionByUID(connUid), connection);

			            // Safety: only process completion on known server connections (dormant mesh or the active host server).
			            if (!isDormantServerConnection && !isMainServerConnection)
			            {
			                return;
			            }

			            // Only the host should allow reliability resets on the active game server connection.
			            if (isMainServerConnection && !isHost)
			            {
			                return;
			            }

				            lock (connectionLock)
				            {
				                ulong uid = connection.InitiatingClientConnectionUID;
				                if (!pendingReliabilityResetsByConnectionUID.TryGetValue(uid, out var pending) ||
				                    pending.HostEpoch != complete.HostEpoch ||
				                    pending.ReliableSessionId != complete.ReliableSessionId)
				                {
				                    return;
				                }

				                pendingReliabilityResetsByConnectionUID.Remove(uid);

				                // Resume reliable traffic now that both sides have reset.
				                connection.SuppressReliableTraffic = false;

				                GONetLog.Warning($"[HotStandby] Reliability reset complete (server) connUID={uid} epoch={complete.HostEpoch} sessionId={complete.ReliableSessionId} - reliable traffic resumed");
				            }
				        }

	        #endregion

        /// <summary>
        /// Tracks last keepalive time for clients connected to our dormant server.
        /// Key = ConnectionUID, Value = last keepalive time.
        /// </summary>
        private readonly Dictionary<ulong, float> dormantClientLastKeepalive = new Dictionary<ulong, float>(128);

        /// <summary>Tracks last keepalive SEQUENCE received per dormant client (client -&gt; server).</summary>
        private readonly Dictionary<ulong, uint> dormantClientLastKeepaliveSequenceReceived = new Dictionary<ulong, uint>(128);

        /// <summary>Tracks the last time the keepalive sequence advanced per dormant client.</summary>
        private readonly Dictionary<ulong, float> dormantClientLastKeepaliveSequenceAdvancedTime = new Dictionary<ulong, float>(128);

        /// <summary>Rate limiting / escalation tracking for dormant-client watchdog resets.</summary>
        private readonly Dictionary<ulong, float> dormantClientWatchdogLastResetAttemptTime = new Dictionary<ulong, float>(128);

        /// <summary>Watchdog reset attempts since last keepalive sequence progress (server side).</summary>
        private readonly Dictionary<ulong, int> dormantClientWatchdogResetAttemptCount = new Dictionary<ulong, int>(128);

        /// <summary>Last timestamp we sent in keepalive to each dormant client (for RTT calculation).</summary>
        private readonly Dictionary<ulong, long> dormantClientLastSentTimestamp = new Dictionary<ulong, long>(128);

        /// <summary>Timestamp to echo back from each dormant client.</summary>
        private readonly Dictionary<ulong, long> dormantClientTimestampToEcho = new Dictionary<ulong, long>(128);

        /// <summary>Measured RTT to each dormant client (milliseconds).</summary>
        private readonly Dictionary<ulong, ushort> dormantClientRTT = new Dictionary<ulong, ushort>(128);

        #endregion

        #region Update Loop

        /// <summary>
        /// Called from GONetMain.Update() to manage standby connections.
        /// </summary>
	        public void Update(float elapsedSeconds)
	        {
	            if (!isInitialized) return;
	            if (!GONetGlobal.Instance.enableDistributedHostAuthority) return;

            // CRITICAL FIX (January 2026): Detect significant time jumps caused by time sync.
            // When a new client connects to a server (especially a promoted host), its ElapsedSeconds
            // can jump by 90+ seconds to align with the server's time domain. This is expected behavior
            // for time sync, but it would cause all keepalive timestamps (recorded pre-sync) to appear
            // "stale" and trigger immediate timeout. Refresh all timestamps when such a jump is detected.
            float timeDelta = elapsedSeconds - previousUpdateElapsedTime;
            if (previousUpdateElapsedTime > 0 && timeDelta > TIME_JUMP_THRESHOLD_SECONDS)
            {
                GONetLog.Warning($"[HotStandby] Time sync jump detected: {previousUpdateElapsedTime:F2}s -> {elapsedSeconds:F2}s (delta={timeDelta:F2}s). Refreshing all keepalive timestamps to prevent false timeout.");
                RefreshAllKeepaliveTimestamps(elapsedSeconds);
            }
            previousUpdateElapsedTime = elapsedSeconds;

            // Update dormant server
            dormantServer?.Update();

            // Check for handshake timeouts
            CheckHandshakeTimeouts(elapsedSeconds);

            // Update standby connections
            UpdateStandbyConnections(elapsedSeconds);

            // Self-healing: ensure any preserved/pending connections are queued for attempts.
            EnsureConnectionQueueIsPopulated(elapsedSeconds);

            // Process connection queue (staggered)
            ProcessConnectionQueue(elapsedSeconds);

            // Send keepalives
            SendKeepalives(elapsedSeconds);

            // Dormant server: send keepalives to connected standby clients
            SendKeepalivesToDormantClients(elapsedSeconds);

            // Host: send fast mesh heartbeats for redundant failover detection
            SendMeshHeartbeats(elapsedSeconds);

            // Dormant server: check for stale standby clients
            CheckDormantClientTimeouts(elapsedSeconds);

            // Mesh watchdog: self-heal stalled reliable channels (mesh only) without requiring a full reconnect.
            ProcessMeshWatchdog(elapsedSeconds);

            // Process SessionPromote retries if we're the host and have pending retries
            if (isHost && pendingSessionPromote.HasValue)
            {
                ProcessSessionPromoteRetries(elapsedSeconds);
            }

	            // Host-driven topology convergence: broadcast a snapshot when mesh membership/endpoint info changes.
	            if (isHost && pendingMeshTopologyBroadcast &&
	                elapsedSeconds - lastMeshTopologyBroadcastTime >= MESH_TOPOLOGY_BROADCAST_MIN_INTERVAL_SECONDS)
	            {
	                pendingMeshTopologyBroadcast = false;
	                lastMeshTopologyBroadcastTime = elapsedSeconds;
	                GONetGossipIntegration.BroadcastMeshTopologyToAllClients();
	            }

		            // Failover reliability reset handshake (client + host) to prevent reliable sequence stalls after host switch.
		            ProcessReliabilityResetClient(elapsedSeconds);
		            ProcessReliabilityResetServer(elapsedSeconds);
		        }

        /// <summary>
        /// Refreshes all keepalive-related timestamps to the current elapsed time.
        /// Called when a significant time jump is detected (e.g., time sync alignment).
        /// This prevents false timeout detection when the time domain changes.
        /// </summary>
        private void RefreshAllKeepaliveTimestamps(float currentTime)
        {
            // Refresh instance-level timing fields
            lastKeepaliveSentTime = currentTime;
            lastMeshHeartbeatSentTime = currentTime;
            lastMeshTopologyBroadcastTime = currentTime;
            lastMeshWatchdogTime = currentTime;
            lastStandbyLogTime = currentTime;
            lastMeshStateLogTime = currentTime;

            // Refresh all standby connection timestamps
            lock (connectionLock)
            {
                foreach (var conn in standbyConnections.Values)
                {
                    if (conn.State == StandbyConnectionState.Connected ||
                        conn.State == StandbyConnectionState.Active ||
                        conn.State == StandbyConnectionState.AwaitingHandshake)
                    {
                        conn.LastKeepaliveTime = currentTime;
                        conn.ConnectedAtTime = currentTime;
                        conn.LastKeepaliveSequenceAdvancedTime = currentTime;
                    }
                    // Also refresh attempt time for failed/pending connections to prevent immediate retry spam
                    conn.LastConnectionAttemptTime = currentTime;
                }

                // Refresh dormant server client keepalive timestamps
                var keysToUpdate = new List<ulong>(dormantClientLastKeepalive.Keys);
                foreach (var uid in keysToUpdate)
                {
                    dormantClientLastKeepalive[uid] = currentTime;
                    if (dormantClientLastKeepaliveSequenceAdvancedTime.ContainsKey(uid))
                    {
                        dormantClientLastKeepaliveSequenceAdvancedTime[uid] = currentTime;
                    }
                }
            }

            // Refresh pending handshake timestamps
            lock (pendingConnections)
            {
                var pendingKeysToUpdate = new List<ulong>(pendingConnections.Keys);
                foreach (var uid in pendingKeysToUpdate)
                {
                    if (pendingConnections.TryGetValue(uid, out var pendingConn))
                    {
                        pendingConn.ConnectedAtTime = currentTime;
                        pendingConnections[uid] = pendingConn;
                    }
                }
            }

            GONetLog.Info($"[HotStandby] Refreshed all keepalive timestamps to {currentTime:F2}s after time sync jump");
        }

        private void BeginFailoverReliabilityReset_NoLock(GONetClient activeClient, uint hostEpoch, ushort? standbyHelloPeerAuthorityId = null)
        {
            if (isHost) return;
            BeginReliabilityResetClient_NoLock(activeClient, hostEpoch, standbyHelloPeerAuthorityId);
        }

		        private void BeginReliabilityResetClient_NoLock(GONetClient client, uint hostEpoch, ushort? standbyHelloPeerAuthorityId = null, bool forceResetEvenIfCompletedThisEpoch = false)
		        {
		            if (client == null || client.connectionToServer == null) return;
		            if (hostEpoch == 0) return;

		            ulong uid = client.connectionToServer.InitiatingClientConnectionUID;
		            if (uid == 0) return;

		            if (lastCompletedReliabilityResetEpochByClientConnectionUID.TryGetValue(uid, out uint completedEpoch) &&
		                hostEpoch <= completedEpoch)
		            {
		                // Never allow a reset for an older epoch.
		                if (hostEpoch < completedEpoch || !forceResetEvenIfCompletedThisEpoch)
		                {
		                    if (standbyHelloPeerAuthorityId.HasValue)
		                    {
		                        TrySendStandbyHelloAfterReliabilityReset_NoLock(standbyHelloPeerAuthorityId.Value, client);
		                    }
		                    return;
		                }
		            }

		            float now = (float)GONetMain.Time.ElapsedSeconds;

		            if (!pendingReliabilityResetsByClientConnectionUID.TryGetValue(uid, out var pending))
		            {
		                pending = new PendingReliabilityResetClientState();
		                pendingReliabilityResetsByClientConnectionUID[uid] = pending;
		            }

		            uint reliableSessionId = (pending.HostEpoch == hostEpoch && pending.ReliableSessionId != 0)
		                ? pending.ReliableSessionId
		                : GenerateReliableSessionId();

		            pending.HostEpoch = hostEpoch;
		            pending.ReliableSessionId = reliableSessionId;
		            pending.StartTime = now;
		            pending.LastRequestSentTime = 0f;
		            pending.RequestSendCount = 0;
		            pending.Client = client;
		            pending.Connection = client.connectionToServer;
		            if (standbyHelloPeerAuthorityId.HasValue)
	            {
	                pending.StandbyHelloPeerAuthorityId = standbyHelloPeerAuthorityId;
	            }

	            // Suppress reliable traffic so any old-session packets don't get buffered behind seq=0 after reset.
	            pending.Connection.SuppressReliableTraffic = true;

	            SendReliabilityResetRequest_NoLock(uid, now);
	        }

	        private void SendReliabilityResetRequest_NoLock(ulong uid, float elapsedSeconds)
	        {
	            if (!pendingReliabilityResetsByClientConnectionUID.TryGetValue(uid, out var pending) ||
	                pending.HostEpoch == 0 ||
	                pending.Client == null ||
	                pending.Connection == null)
	            {
	                return;
	            }

		            try
		            {
		                var request = new ReliabilityResetRequestMessage { HostEpoch = pending.HostEpoch, ReliableSessionId = pending.ReliableSessionId };
		                SendStandbyMessageToServerUnreliable(MSG_TYPE_RELIABILITY_RESET_REQUEST, request, pending.Client);
		                pending.LastRequestSentTime = elapsedSeconds;
		                pending.RequestSendCount++;

		                if (pending.RequestSendCount == 1 || pending.RequestSendCount % 4 == 0)
		                {
		                    GONetLog.Warning($"[HotStandby] Sent ReliabilityResetRequest epoch={pending.HostEpoch} sessionId={pending.ReliableSessionId} connUID={uid} attempt={pending.RequestSendCount}");
		                }
		            }
	            catch (Exception ex)
	            {
	                GONetLog.Warning($"[HotStandby] Failed to send ReliabilityResetRequest (epoch={pending.HostEpoch}, connUID={uid}): {ex.Message}");
	            }
	        }

		        private GONetClient AbortReliabilityResetClient_NoLock(ulong uid, PendingReliabilityResetClientState pending, string reason, bool attemptDisconnect)
		        {
		            if (pending.Connection != null)
		            {
		                pending.Connection.SuppressReliableTraffic = false;
		            }

		            pendingReliabilityResetsByClientConnectionUID.Remove(uid);

		            GONetLog.Warning($"[HotStandby] Aborted reliability reset handshake (connUID={uid} epoch={pending.HostEpoch}): {reason}");

		            // Disconnect outside the connectionLock to avoid deadlocks with connection state change callbacks.
		            return attemptDisconnect ? pending.Client : null;
		        }

		        private void ProcessReliabilityResetClient(float elapsedSeconds)
		        {
		            if (pendingReliabilityResetsByClientConnectionUID.Count == 0) return;

		            List<GONetClient> clientsToDisconnect = null;

		            lock (connectionLock)
		            {
		                List<ulong> toAbort = null;

		                foreach (var kvp in pendingReliabilityResetsByClientConnectionUID)
		                {
		                    ulong uid = kvp.Key;
		                    var pending = kvp.Value;

		                    bool isConnectionLost = pending.Client == null ||
		                                            pending.Connection == null ||
		                                            !pending.Client.IsConnectedToServer ||
		                                            pending.Client.Connection != pending.Connection;

		                    if (isConnectionLost)
		                    {
		                        toAbort ??= new List<ulong>();
		                        toAbort.Add(uid);
		                        continue;
		                    }

		                    if (elapsedSeconds - pending.StartTime > RELIABILITY_RESET_TIMEOUT_SECONDS)
		                    {
		                        toAbort ??= new List<ulong>();
		                        toAbort.Add(uid);
		                        continue;
		                    }

		                    if (elapsedSeconds - pending.LastRequestSentTime >= RELIABILITY_RESET_RETRY_INTERVAL_SECONDS)
		                    {
		                        SendReliabilityResetRequest_NoLock(uid, elapsedSeconds);
		                    }
		                }

		                if (toAbort != null)
		                {
		                    foreach (ulong uid in toAbort)
		                    {
		                        if (!pendingReliabilityResetsByClientConnectionUID.TryGetValue(uid, out var pending))
		                        {
		                            continue;
		                        }

		                        bool isConnectionLost = pending.Client == null ||
		                                                pending.Connection == null ||
		                                                !pending.Client.IsConnectedToServer ||
		                                                pending.Client.Connection != pending.Connection;

		                        bool isTimeout = !isConnectionLost && elapsedSeconds - pending.StartTime > RELIABILITY_RESET_TIMEOUT_SECONDS;

		                        string reason = isConnectionLost
		                            ? "connection lost"
		                            : $"timeout after {RELIABILITY_RESET_TIMEOUT_SECONDS}s";

		                        GONetClient clientToDisconnect = AbortReliabilityResetClient_NoLock(uid, pending, reason, attemptDisconnect: isTimeout);
		                        if (clientToDisconnect != null)
		                        {
		                            clientsToDisconnect ??= new List<GONetClient>();
		                            clientsToDisconnect.Add(clientToDisconnect);
		                        }
		                    }
		                }
		            }

		            if (clientsToDisconnect == null) return;

		            foreach (var client in clientsToDisconnect)
		            {
		                try
		                {
		                    client.Disconnect();
		                }
		                catch (Exception ex)
		                {
		                    GONetLog.Warning($"[HotStandby] Failed to disconnect after reliability reset timeout: {ex.Message}");
		                }
		            }
		        }

		        private void ProcessReliabilityResetServer(float elapsedSeconds)
		        {
		            if (pendingReliabilityResetsByConnectionUID.Count == 0) return;

		            List<ulong> toRemove = null;
		            List<(GONetServer Server, ulong ConnectionUID)> toDisconnect = null;

		            lock (connectionLock)
		            {
		                foreach (var kvp in pendingReliabilityResetsByConnectionUID)
		                {
		                    ulong uid = kvp.Key;
		                    var state = kvp.Value;

		                    GONetConnection_ServerToClient connection =
		                        dormantServer?.GetConnectionByUID(uid) ??
		                        GONetMain.gonetServer?.GetConnectionByUID(uid);

				                    if (elapsedSeconds - state.StartTime > RELIABILITY_RESET_TIMEOUT_SECONDS)
				                    {
				                        if (connection != null)
				                        {
				                            connection.SuppressReliableTraffic = false;

				                            // If the reset handshake can't complete, this connection is effectively dead (session ids will not match).
				                            // Force a reconnect to recover cleanly rather than leaving both sides in a permanently mismatched session.
				                            if (dormantServer != null && ReferenceEquals(dormantServer.GetConnectionByUID(uid), connection))
				                            {
				                                toDisconnect ??= new List<(GONetServer, ulong)>();
				                                toDisconnect.Add((dormantServer, uid));
				                            }
				                            else if (GONetMain.gonetServer != null && ReferenceEquals(GONetMain.gonetServer.GetConnectionByUID(uid), connection))
				                            {
				                                toDisconnect ??= new List<(GONetServer, ulong)>();
				                                toDisconnect.Add((GONetMain.gonetServer, uid));
				                            }
				                        }

			                        toRemove ??= new List<ulong>();
			                        toRemove.Add(uid);
			                        GONetLog.Warning($"[HotStandby] Reliability reset timeout on connUID={uid} epoch={state.HostEpoch} sessionId={state.ReliableSessionId} - disconnecting to recover");
			                        continue;
			                    }

		                    if (elapsedSeconds - state.LastCommitSentTime < RELIABILITY_RESET_RETRY_INTERVAL_SECONDS)
		                    {
		                        continue;
		                    }

		                    if (connection == null)
		                    {
		                        toRemove ??= new List<ulong>();
		                        toRemove.Add(uid);
		                        continue;
		                    }

			                    var commit = new ReliabilityResetCommitMessage { HostEpoch = state.HostEpoch, ReliableSessionId = state.ReliableSessionId };
			                    SendStandbyMessageUnreliable(MSG_TYPE_RELIABILITY_RESET_COMMIT, commit, connection);
			                    state.LastCommitSentTime = elapsedSeconds;
			                    state.CommitSendCount++;
			                }

			                if (toRemove != null)
			                {
			                    foreach (var uid in toRemove)
			                    {
			                        pendingReliabilityResetsByConnectionUID.Remove(uid);
			                    }
			                }
		            }

		            if (toDisconnect == null) return;

		            foreach (var item in toDisconnect)
		            {
		                try
		                {
		                    item.Server?.TryDisconnectClientByConnectionUID(item.ConnectionUID, GONetTransportDisconnectReason.Timeout, "reliability-reset-timeout");
		                }
		                catch (Exception ex)
		                {
		                    GONetLog.Warning($"[HotStandby] Failed to disconnect after reliability reset timeout connUID={item.ConnectionUID}: {ex.Message}");
		                }
		            }
		        }

	        private void CheckHandshakeTimeouts(float elapsedSeconds)
	        {
	            List<ulong> toDisconnect = null;

            lock (connectionLock)
            {
                foreach (var kvp in pendingConnections)
                {
                    if (!kvp.Value.HandshakeReceived &&
                        elapsedSeconds - kvp.Value.ConnectedAtTime > HANDSHAKE_TIMEOUT_SECONDS)
                    {
                        toDisconnect ??= new List<ulong>();
                        toDisconnect.Add(kvp.Key);
                    }
                }
            }

            // Disconnect outside lock to avoid deadlock
            if (toDisconnect != null)
            {
                foreach (var uid in toDisconnect)
                {
                    GONetLog.Warning($"[HotStandby] Handshake timeout for connection UID {uid}");
                    // TODO: Disconnect the specific connection
                    lock (connectionLock)
                    {
                        pendingConnections.Remove(uid);
                    }
                }
            }
        }

        private float lastStandbyLogTime = 0;

        private float lastMeshStateLogTime = 0f;

        private static bool IsTerminalStandbyClientFailureState(ClientState state)
        {
            switch (state)
            {
                case ClientState.Disconnected:
                case ClientState.ConnectionDenied:
                case ClientState.ConnectionTimedOut:
                case ClientState.ConnectionRequestTimedOut:
                case ClientState.ChallengeResponseTimedOut:
                case ClientState.ConnectTokenExpired:
                    return true;

                default:
                    return false;
            }
        }

        private void MarkStandbyConnectionFailed_NoLock(StandbyConnection conn, string reason, float currentTime)
        {
            if (conn == null) return;

            ushort peerAuthorityId = conn.PeerAuthorityId;

            // Tear down the underlying client/transport FIRST so we stop any ongoing work immediately.
            if (conn.Client != null)
            {
                var oldClient = conn.Client;
                CleanupClientReliabilityResetTracking_NoLock(oldClient);
                conn.Client = null;

                try { oldClient.Disconnect(); }
                catch { }
            }

            if (conn.MeshClientTransport != null)
            {
                try { conn.MeshClientTransport.Dispose(); }
                catch { }
                conn.MeshClientTransport = null;
            }

            conn.State = StandbyConnectionState.Failed;
            conn.FailureCount++;
            conn.LastConnectionAttemptTime = currentTime; // backoff measured from failure time

            GONetLog.Warning($"[HotStandby] Standby connection FAILED to peer {peerAuthorityId}: reason={reason} failures={conn.FailureCount}");
        }

        private void UpdateStandbyConnections(float elapsedSeconds)
        {
            // CRITICAL DEBUG: Log standby connection states every 0.5s during failover
            bool isInFailover = GONetHostFailoverManager.Instance?.CurrentState != FailoverState.HostAlive;
            if (isInFailover && elapsedSeconds - lastStandbyLogTime > 0.5f)
            {
                lastStandbyLogTime = elapsedSeconds;
                lock (connectionLock)
                {
                    foreach (var c in standbyConnections.Values)
                    {
//                        GONetLog.Warning($"[HotStandby-UPDATE] Standby conn state during failover: peer={c.PeerAuthorityId}, " +
//                            $"standbyState={c.State}, clientState={c.Client?.ConnectionState}, " +
//                            $"isConnected={c.Client?.IsConnectedToServer}, time={elapsedSeconds:F3}s");
                    }
                }
            }

            // Periodic mesh state logging (every 5s during normal operation, every 1s during failover)
            float logInterval = isInFailover ? 1f : 5f;
            if (elapsedSeconds - lastMeshStateLogTime > logInterval)
            {
                lastMeshStateLogTime = elapsedSeconds;
                LogCurrentMeshState($"PERIODIC isFailover={isInFailover}");
            }

            lock (connectionLock)
            {
                foreach (var conn in standbyConnections.Values)
                {
                    if (conn.Client != null)
                    {
                        conn.Client.Update();

                        // CRITICAL: Outbound mesh clients can enter ConnectionTimedOut (or other terminal states)
                        // without firing ClientDisconnected. If we leave the standby connection stuck in
                        // Connecting/AwaitingHandshake, we can end up doing expensive per-frame work forever
                        // (e.g., sync bundling) for dead peers. Fail fast and let backoff scheduling retry later.
                        if (conn.State != StandbyConnectionState.Active)
                        {
                            var clientState = conn.Client.ConnectionState;
                            if (IsTerminalStandbyClientFailureState(clientState) &&
                                (conn.State == StandbyConnectionState.Connecting ||
                                 conn.State == StandbyConnectionState.AwaitingHandshake ||
                                 conn.State == StandbyConnectionState.Connected))
                            {
                                MarkStandbyConnectionFailed_NoLock(conn, $"clientState={clientState}", elapsedSeconds);
                                continue;
                            }

                            // Safety net: if the transport never flips to a terminal failure state, don't let a connection
                            // sit in Connecting forever (zombie connection -> unbounded work).
                            if (conn.State == StandbyConnectionState.Connecting && !conn.Client.IsConnectedToServer)
                            {
                                float stuckSeconds = elapsedSeconds - conn.LastConnectionAttemptTime;
                                if (stuckSeconds > STANDBY_MESH_CLIENT_CONNECT_TIMEOUT_SECONDS + STANDBY_MESH_CLIENT_CONNECT_TIMEOUT_GRACE_SECONDS)
                                {
                                    MarkStandbyConnectionFailed_NoLock(conn, $"connect-stuck {stuckSeconds:F1}s clientState={clientState}", elapsedSeconds);
                                    continue;
                                }
	                            }
	                        }

                        if (conn.State == StandbyConnectionState.Active &&
                            GONetHostFailoverManager.Instance?.DidVoluntarilyDemote == true)
                        {
                            var clientState = conn.Client.ConnectionState;
                            if (IsTerminalStandbyClientFailureState(clientState) || !conn.Client.IsConnectedToServer)
                            {
                                MarkStandbyConnectionFailed_NoLock(conn, $"active-clientState={clientState}", elapsedSeconds);
                                EnqueueConnectionAttempt_NoLock(conn.PeerAuthorityId);
                                continue;
                            }
                        }

	                        // If the transport connection is up but the handshake ack never arrives, resend StandbyHello periodically.
	                        // This keeps mesh establishment robust even when the first few packets are dropped during connection warmup.
	                        if (conn.State == StandbyConnectionState.AwaitingHandshake && conn.Client.IsConnectedToServer)
	                        {
	                            if (conn.LastHelloSentTime <= 0f)
	                            {
	                                conn.LastHelloSentTime = conn.ConnectedAtTime;
	                            }

	                            if (elapsedSeconds - conn.LastHelloSentTime >= STANDBY_HELLO_RESEND_INTERVAL_SECONDS)
	                            {
	                                try
	                                {
	                                    var hello = new StandbyHelloMessage
	                                    {
	                                        AuthorityId = GONetMain.MyAuthorityId,
	                                        SessionGUID = sessionGUID,
	                                        SecretToken = StandbyHelloMessage.ComputeSecretToken(sessionGUID, GONetMain.MyAuthorityId),
	                                        DormantPort = dormantServerPort,
	                                        VirtualPort = dormantVirtualPort
	                                    };

	                                    SendStandbyMessageToServer(MSG_TYPE_STANDBY_HELLO, hello, conn.Client);
	                                    conn.LastHelloSentTime = elapsedSeconds;
	                                    GONetLog.Debug($"[HotStandby] Re-sent StandbyHello to peer {conn.PeerAuthorityId} (awaiting ACK)");
	                                }
	                                catch (Exception ex)
	                                {
	                                    GONetLog.Debug($"[HotStandby] Failed to resend StandbyHello to peer {conn.PeerAuthorityId}: {ex.Message}");
	                                }
	                            }
	                        }

	                        // Check handshake timeout
	                        if (conn.State == StandbyConnectionState.AwaitingHandshake &&
	                            elapsedSeconds - conn.ConnectedAtTime > HANDSHAKE_TIMEOUT_SECONDS)
	                        {
                            MarkStandbyConnectionFailed_NoLock(conn, "handshake-timeout", elapsedSeconds);
                            continue;
                        }
                        // Check keepalive timeout
                        else if (conn.State == StandbyConnectionState.Connected &&
                                 elapsedSeconds - conn.LastKeepaliveTime > KEEPALIVE_TIMEOUT_SECONDS)
                        {
                            MarkStandbyConnectionFailed_NoLock(conn, "keepalive-timeout", elapsedSeconds);
                            continue;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Self-healing: ensures peers that need a connection attempt are queued.
        /// This prevents rare stalls where queue state is cleared/reset while preserving connection entries.
        /// </summary>
        private void EnsureConnectionQueueIsPopulated(float currentTime)
        {
            lock (connectionLock)
            {
                foreach (var kvp in standbyConnections)
                {
                    ushort peerAuthorityId = kvp.Key;
                    var conn = kvp.Value;

                    // Do not schedule attempts for connections already in a "good" or in-progress state.
                    if (conn.State == StandbyConnectionState.Connected ||
                        conn.State == StandbyConnectionState.Active ||
                        conn.State == StandbyConnectionState.Connecting ||
                        conn.State == StandbyConnectionState.AwaitingHandshake)
                    {
                        continue;
                    }

                    // If already queued, nothing to do.
                    if (connectionQueueSet.Contains(peerAuthorityId))
                    {
                        continue;
                    }

                    // Schedule NotStarted immediately.
                    if (conn.State == StandbyConnectionState.NotStarted)
                    {
                        EnqueueConnectionAttempt_NoLock(peerAuthorityId);
                        continue;
                    }

                    // Respect exponential backoff for Failed connections.
                    if (conn.State == StandbyConnectionState.Failed)
                    {
                        // CRITICAL FIX (Dec 2025): Stop retrying after MAX_CONSECUTIVE_FAILURES.
                        // Without this, mesh connections to dead peers retry forever, creating
                        // new GONetClient/transport instances every frame and causing 1 FPS.
                        if (conn.FailureCount >= MAX_CONSECUTIVE_FAILURES)
                        {
                            conn.State = StandbyConnectionState.Closed;
                            GONetLog.Warning($"[HotStandby] Abandoning mesh connection to peer {peerAuthorityId} after {conn.FailureCount} consecutive failures - peer is likely dead");
                            continue;
                        }

                        float retryDelay = Math.Min(BASE_RETRY_DELAY_SECONDS * (float)Math.Pow(2, conn.FailureCount), MAX_RETRY_DELAY_SECONDS);
                        if (currentTime - conn.LastConnectionAttemptTime >= retryDelay)
                        {
                            EnqueueConnectionAttempt_NoLock(peerAuthorityId);
                        }
                    }
                }
            }
        }

        private void ProcessConnectionQueue(float elapsedSeconds)
        {
            ushort peerAuthorityId;

            lock (connectionLock)
            {
                if (connectionQueue.Count == 0) return;
                if (elapsedSeconds - lastConnectionAttemptTime < CONNECTION_STAGGER_DELAY_SECONDS) return;

                if (!connectionQueue.TryDequeue(out peerAuthorityId))
                {
                    return;
                }

                connectionQueueSet.Remove(peerAuthorityId);
                lastConnectionAttemptTime = elapsedSeconds;
            }

            AttemptConnection(peerAuthorityId, elapsedSeconds);
        }

        private void SendKeepalives(float elapsedSeconds)
        {
            if (elapsedSeconds - lastKeepaliveSentTime < KEEPALIVE_INTERVAL_SECONDS) return;
            lastKeepaliveSentTime = elapsedSeconds;

            lock (connectionLock)
            {
                foreach (var conn in standbyConnections.Values)
                {
                    if (conn.State == StandbyConnectionState.Connected && conn.Client != null)
                    {
                        try
                        {
                            // CRITICAL: Use wall-clock time for RTT timestamps, not GONet session time.
                            // GONetMain.Time.ElapsedTicks can have discontinuities during failover,
                            // causing wildly inaccurate RTT calculations.
                            long nowTicks = HighResolutionTimeUtils.UtcNowTicks;
                            var keepalive = new StandbyKeepaliveMessage
                            {
                                AuthorityId = GONetMain.MyAuthorityId,
                                Sequence = conn.KeepaliveSequence++,
                                SentTimestampTicks = nowTicks,
                                EchoTimestampTicks = conn.PeerTimestampToEcho
                            };
                            conn.LastSentTimestampTicks = nowTicks;
                            SendStandbyMessageToServer(MSG_TYPE_STANDBY_KEEPALIVE, keepalive, conn.Client);
                            // COMMENTED (log cleanup) - fires every 5 seconds per peer, spammy
                            //GONetLog.Debug($"[HotStandby] Sent keepalive seq={keepalive.Sequence} to peer {conn.PeerAuthorityId} (rtt={conn.KeepaliveRTT_Ms}ms)");
                        }
                        catch (Exception ex)
                        {
                            GONetLog.Debug($"[HotStandby] Failed to send keepalive to {conn.PeerAuthorityId}: {ex.Message}");
                        }
                    }
                    else
                    {
                        // COMMENTED (log cleanup) - fires when connection not ready, spammy during transitions
                        //GONetLog.Debug($"[HotStandby] Skipping keepalive to peer {conn.PeerAuthorityId}: State={conn.State}, Client={conn.Client != null}");
                    }
                }
            }
        }

        /// <summary>
        /// Dormant server keepalive sequence counter per client.
        /// </summary>
        private readonly Dictionary<ulong, ushort> dormantServerKeepaliveSequence = new Dictionary<ulong, ushort>(128);

        private float lastDormantKeepaliveSentTime;

        private void SendKeepalivesToDormantClients(float elapsedSeconds)
        {
            if (dormantServer == null) return;

            // After the dormant server is promoted to ActiveHost, don't send keepalives.
            // The connections are now regular game connections, not standby connections.
            // Regular heartbeats handle liveness for active connections.
            if (dormantServer.Mode == GONetServerMode.ActiveHost) return;

            // NOTE: We DO send keepalives even when isHost=true (for the original server).
            // The dormant server still has standby clients connected that expect keepalives.
            // Without this, standby clients timeout every 15 seconds and reconnect,
            // causing mesh UI flickering and connection churn.
            // The main server heartbeats are separate from dormant server keepalives.

            if (elapsedSeconds - lastDormantKeepaliveSentTime < KEEPALIVE_INTERVAL_SECONDS) return;
            lastDormantKeepaliveSentTime = elapsedSeconds;

            lock (connectionLock)
            {
                // COMMENTED (log cleanup) - fires every 5 seconds when no clients, spammy
                /*if (authorityMapByConnectionUID.Count == 0)
                {
                    GONetLog.Debug($"[HotStandby] Dormant server: no clients in authority map to send keepalives to");
                }*/

                foreach (var kvp in authorityMapByConnectionUID)
                {
                    ulong uid = kvp.Key;
                    ushort peerAuthorityId = kvp.Value;

                    // Find the connection for this UID
                    var connection = dormantServer.GetConnectionByUID(uid);
                    if (connection != null)
                    {
                        try
                        {
                            if (!dormantServerKeepaliveSequence.TryGetValue(uid, out ushort seq))
                            {
                                dormantServerKeepaliveSequence[uid] = 0;
                                seq = 0;
                            }

                            // CRITICAL: Use wall-clock time for RTT timestamps, not GONet session time.
                            // GONetMain.Time.ElapsedTicks can have discontinuities during failover,
                            // causing wildly inaccurate RTT calculations.
                            long nowTicks = HighResolutionTimeUtils.UtcNowTicks;
                            dormantClientTimestampToEcho.TryGetValue(uid, out long echoTs);
                            dormantClientRTT.TryGetValue(uid, out ushort rtt);

                            var keepalive = new StandbyKeepaliveMessage
                            {
                                AuthorityId = GONetMain.MyAuthorityId,
                                Sequence = seq,
                                SentTimestampTicks = nowTicks,
                                EchoTimestampTicks = echoTs
                            };
                            dormantServerKeepaliveSequence[uid] = (ushort)(seq + 1);
                            dormantClientLastSentTimestamp[uid] = nowTicks;

                            SendStandbyMessage(MSG_TYPE_STANDBY_KEEPALIVE, keepalive, connection);
                            // COMMENTED (log cleanup) - fires every 5 seconds per client, spammy
                            //GONetLog.Debug($"[HotStandby] Dormant server sent keepalive seq={seq} to authority {peerAuthorityId} (UID: {uid}, rtt={rtt}ms)");
                        }
                        catch (Exception ex)
                        {
                            GONetLog.Debug($"[HotStandby] Dormant server failed to send keepalive to {peerAuthorityId}: {ex.Message}");
                        }
                    }
                    else
                    {
                        // COMMENTED (log cleanup) - can fire during connection transitions
                        //GONetLog.Debug($"[HotStandby] Dormant server: no connection found for UID {uid} (authority {peerAuthorityId})");
                    }
                }
            }
        }

        /// <summary>
        /// Sends fast mesh heartbeats to all connected standby clients (host only).
        /// These are sent unreliably at 10Hz to provide redundant failover detection
        /// in case main server heartbeats are delayed due to reliable channel congestion.
        /// </summary>
        private void SendMeshHeartbeats(float elapsedSeconds)
        {
            // Only the host sends mesh heartbeats
            if (!isHost) return;
            if (dormantServer == null) return;

            // Skip if dormant server is now active (failover complete)
            if (dormantServer.Mode == GONetServerMode.ActiveHost) return;

            // Check interval
            if (elapsedSeconds - lastMeshHeartbeatSentTime < MESH_HEARTBEAT_INTERVAL_SECONDS) return;
            lastMeshHeartbeatSentTime = elapsedSeconds;

            // Create the heartbeat message
            var heartbeat = new MeshHeartbeatMessage
            {
                HostAuthorityId = GONetMain.MyAuthorityId,
                HostEpoch = GONetMain.HostEpoch
            };

            lock (connectionLock)
            {
                int sentCount = 0;
                foreach (var kvp in authorityMapByConnectionUID)
                {
                    ulong uid = kvp.Key;
                    var connection = dormantServer.GetConnectionByUID(uid);
                    if (connection != null)
                    {
                        try
                        {
                            // Send unreliably for speed - it's okay if some are lost
                            SendStandbyMessageUnreliable(MSG_TYPE_MESH_HEARTBEAT, heartbeat, connection);
                            sentCount++;
                        }
                        catch (Exception)
                        {
                            // Ignore - unreliable send failures are expected
                        }
                    }
                }

                if (sentCount > 0)
                {
                    //GONetLog.Debug($"[HotStandby] Sent mesh heartbeat to {sentCount} standby clients (epoch={heartbeat.HostEpoch})");
                }
            }
        }

        private void CheckDormantClientTimeouts(float elapsedSeconds)
        {
            if (dormantServer == null) return;

            // After the dormant server is promoted to ActiveHost, skip timeout checking.
            // The connections are now regular game connections, not standby connections.
            // Regular heartbeats and game connection handling take over.
            if (dormantServer.Mode == GONetServerMode.ActiveHost) return;

            List<ulong> staleClients = null;

            lock (connectionLock)
            {
                foreach (var kvp in dormantClientLastKeepalive)
                {
                    if (elapsedSeconds - kvp.Value > KEEPALIVE_TIMEOUT_SECONDS)
                    {
                        staleClients ??= new List<ulong>();
                        staleClients.Add(kvp.Key);
                    }
                }

                if (staleClients != null)
                {
                    foreach (var uid in staleClients)
                    {
                        if (authorityMapByConnectionUID.TryGetValue(uid, out ushort authorityId))
                        {
                            GONetLog.Warning($"[HotStandby] Dormant server: client {authorityId} keepalive timeout, disconnecting");
                            authorityMapByConnectionUID.Remove(uid);
                            connectionUIDByAuthorityId.Remove(authorityId);
                            incomingPeerEndpoints.Remove(authorityId);
                        }
                        dormantClientLastKeepalive.Remove(uid);
                        dormantClientLastKeepaliveSequenceReceived.Remove(uid);
                        dormantClientLastKeepaliveSequenceAdvancedTime.Remove(uid);
                        dormantServerKeepaliveSequence.Remove(uid);
                        dormantClientWatchdogLastResetAttemptTime.Remove(uid);
                        dormantClientWatchdogResetAttemptCount.Remove(uid);
                        dormantClientLastSentTimestamp.Remove(uid);
                        dormantClientTimestampToEcho.Remove(uid);
                        dormantClientRTT.Remove(uid);
                        CleanupServerReliabilityResetTracking_NoLock(uid);
                    }
                }
            }

            // Disconnect outside lock to avoid deadlocks with connection callbacks.
            if (staleClients != null)
            {
                foreach (var uid in staleClients)
                {
                    try
                    {
                        dormantServer.TryDisconnectClientByConnectionUID(uid, GONetTransportDisconnectReason.Timeout, "dormant-keepalive-timeout");
                    }
                    catch (Exception ex)
                    {
                        GONetLog.Warning($"[HotStandby] Dormant server: failed to disconnect stale client connUID={uid}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Lightweight watchdog to keep the standby mesh reliable channels healthy for long-lived sessions.
        /// Detects "alive but stuck" reliable sequencing (e.g., endless retransmits/duplicates) using keepalive SEQUENCE
        /// progress rather than keepalive receipt time, and triggers a coordinated reliability reset.
        /// </summary>
        private void ProcessMeshWatchdog(float elapsedSeconds)
        {
            if (elapsedSeconds - lastMeshWatchdogTime < MESH_WATCHDOG_INTERVAL_SECONDS) return;
            lastMeshWatchdogTime = elapsedSeconds;

            uint hostEpoch = GONetMain.HostEpoch;
            if (hostEpoch == 0) return;

            List<(GONetServer Server, ulong ConnectionUID)> toDisconnect = null;

            lock (connectionLock)
            {
                // Outbound standby mesh clients (this node connecting to peer dormant servers).
                foreach (var conn in standbyConnections.Values)
                {
                    if (conn.State != StandbyConnectionState.Connected) continue;
                    if (conn.Client == null || !conn.Client.IsConnectedToServer || conn.Client.connectionToServer == null) continue;

                    ulong uid = conn.Client.connectionToServer.InitiatingClientConnectionUID;
                    if (uid == 0) continue;

                    // Don't stack multiple resets on the same connection.
                    if (pendingReliabilityResetsByClientConnectionUID.ContainsKey(uid)) continue;

                    bool isStale =
                        elapsedSeconds - conn.LastKeepaliveTime > MESH_WATCHDOG_STALE_SECONDS ||
                        elapsedSeconds - conn.LastKeepaliveSequenceAdvancedTime > MESH_WATCHDOG_STALE_SECONDS;

                    if (!isStale) continue;

                    if (elapsedSeconds - conn.LastWatchdogReliabilityResetTime < MESH_WATCHDOG_RESET_COOLDOWN_SECONDS)
                    {
                        continue;
                    }

                    if (conn.WatchdogReliabilityResetAttemptCount >= MESH_WATCHDOG_MAX_RESETS_BEFORE_RECONNECT)
                    {
                        // Escalate: force a reconnect (the next AttemptConnection will tear down the old client/transport).
                        conn.WatchdogReliabilityResetAttemptCount = 0;
                        conn.State = StandbyConnectionState.Failed;
                        conn.FailureCount++;
                        EnqueueConnectionAttempt_NoLock(conn.PeerAuthorityId);
                        GONetLog.Warning($"[HotStandby-Watchdog] Escalating to reconnect for peer {conn.PeerAuthorityId} after repeated stalled keepalive sequencing");
                        continue;
                    }

                    conn.WatchdogReliabilityResetAttemptCount++;
                    conn.LastWatchdogReliabilityResetTime = elapsedSeconds;

                    BeginReliabilityResetClient_NoLock(conn.Client, hostEpoch, forceResetEvenIfCompletedThisEpoch: true);
                    GONetLog.Info($"[HotStandby-Watchdog] Initiated reliability reset to peer {conn.PeerAuthorityId} (attempt={conn.WatchdogReliabilityResetAttemptCount})");
                }

                // Inbound dormant-server connections (peers connected to OUR dormant server).
                // Detect stalled client->server reliable sequencing via keepalive SEQUENCE progress.
                if (dormantServer != null && dormantServer.Mode != GONetServerMode.ActiveHost)
                {
                    foreach (var kvp in authorityMapByConnectionUID)
                    {
                        ulong uid = kvp.Key;
                        ushort peerAuthorityId = kvp.Value;

                        if (pendingReliabilityResetsByConnectionUID.ContainsKey(uid)) continue;

                        if (!dormantClientLastKeepalive.TryGetValue(uid, out float lastKeepaliveTime))
                        {
                            continue;
                        }

                        if (!dormantClientLastKeepaliveSequenceAdvancedTime.TryGetValue(uid, out float lastSeqAdvanceTime))
                        {
                            lastSeqAdvanceTime = lastKeepaliveTime;
                        }

                        bool isStale =
                            elapsedSeconds - lastKeepaliveTime > MESH_WATCHDOG_STALE_SECONDS ||
                            elapsedSeconds - lastSeqAdvanceTime > MESH_WATCHDOG_STALE_SECONDS;

                        if (!isStale) continue;

                        if (dormantClientWatchdogLastResetAttemptTime.TryGetValue(uid, out float lastResetTime) &&
                            elapsedSeconds - lastResetTime < MESH_WATCHDOG_RESET_COOLDOWN_SECONDS)
                        {
                            continue;
                        }

                        int attempts = dormantClientWatchdogResetAttemptCount.TryGetValue(uid, out int existing) ? existing : 0;
                        if (attempts >= MESH_WATCHDOG_MAX_RESETS_BEFORE_RECONNECT)
                        {
                            dormantClientWatchdogLastResetAttemptTime.Remove(uid);
                            dormantClientWatchdogResetAttemptCount.Remove(uid);
                            toDisconnect ??= new List<(GONetServer, ulong)>();
                            toDisconnect.Add((dormantServer, uid));
                            GONetLog.Warning($"[HotStandby-Watchdog] Escalating to disconnect dormant client {peerAuthorityId} (connUID={uid}) after repeated stalled keepalive sequencing");
                            continue;
                        }

                        var connection = dormantServer.GetConnectionByUID(uid);
                        if (connection == null)
                        {
                            dormantClientWatchdogLastResetAttemptTime.Remove(uid);
                            dormantClientWatchdogResetAttemptCount.Remove(uid);
                            continue;
                        }

                        dormantClientWatchdogLastResetAttemptTime[uid] = elapsedSeconds;
                        dormantClientWatchdogResetAttemptCount[uid] = attempts + 1;

                        // Server-initiated reset: reuse the existing handler to reset server state and send COMMIT unreliably.
                        HandleReliabilityResetRequest(new ReliabilityResetRequestMessage
                        {
                            HostEpoch = hostEpoch,
                            ReliableSessionId = GenerateReliableSessionId()
                        }, connection);

                        GONetLog.Info($"[HotStandby-Watchdog] Initiated server-side reliability reset for dormant client {peerAuthorityId} (connUID={uid}, attempt={attempts + 1})");
                    }
                }
            }

            if (toDisconnect == null) return;

            foreach (var item in toDisconnect)
            {
                try
                {
                    item.Server?.TryDisconnectClientByConnectionUID(item.ConnectionUID, GONetTransportDisconnectReason.Timeout, "mesh-watchdog-stale-reliable");
                }
                catch (Exception ex)
                {
                    GONetLog.Warning($"[HotStandby-Watchdog] Failed to disconnect stale dormant client connUID={item.ConnectionUID}: {ex.Message}");
                }
            }
        }

        #endregion

        #region Peer Management

        /// <summary>
        /// Called when a new peer is discovered via gossip.
        /// The endpoint now directly contains the peer's hot standby port (set by GONetHotStandbyManager.Initialize).
        /// </summary>
        public void OnPeerDiscovered(ushort peerAuthorityId, GONetConnectionEndpoint endpoint)
        {
            if (!isInitialized) return;
            if (peerAuthorityId == GONetMain.MyAuthorityId) return;

            // CRITICAL FIX (Dec 2025): Conditionally skip server authority (1023) based on failover state.
            // - BEFORE failover (HostEpoch == 0): Skip - the original server has no mesh dormant server
            // - AFTER failover (HostEpoch > 0): Allow - the promoted host's dormant server IS a mesh peer
            //   Late-joiners must connect to the promoted host's dormant server for the next failover.
            //   Without this fix, late-joiners show "3/2 peers" - they never connect to the promoted host's dormant.
            if (peerAuthorityId == GONetMain.OwnerAuthorityId_Server && GONetMain.HostEpoch == 0)
            {
                GONetLog.Debug($"[HotStandby] Ignoring peer discovery for server authority {peerAuthorityId} - pre-failover server is not a mesh peer");
                return;
            }
            else if (peerAuthorityId == GONetMain.OwnerAuthorityId_Server)
            {
                GONetLog.Info($"[HotStandby] Processing peer discovery for server authority {peerAuthorityId} (epoch={GONetMain.HostEpoch}) - post-failover, promoted host's dormant IS a mesh peer");
            }

            // NOTE: Clients DO connect to the server's dormant server for a complete mesh.
            // This enables mesh heartbeats as a backup for failover detection.
            // The server connecting to clients AND clients connecting to server creates
            // bidirectional standby connections for robust failover.

            // The endpoint now directly contains the hot standby port (no +1 adjustment needed).
            // This was fixed because port scanning meant probe_port + 1 != actual_hot_standby_port.
            var hotStandbyEndpoint = endpoint;

            lock (connectionLock)
            {
                if (standbyConnections.TryGetValue(peerAuthorityId, out var existingConn))
                {
                    bool endpointChanged =
                        existingConn.PeerEndpoint.Port != hotStandbyEndpoint.Port ||
                        existingConn.PeerEndpoint.IPv4Address != hotStandbyEndpoint.IPv4Address ||
                        existingConn.PeerEndpoint.IPv6AddressHigh != hotStandbyEndpoint.IPv6AddressHigh ||
                        existingConn.PeerEndpoint.IPv6AddressLow != hotStandbyEndpoint.IPv6AddressLow ||
                        existingConn.PeerEndpoint.TransportSpecificId != hotStandbyEndpoint.TransportSpecificId;

                    existingConn.PeerEndpoint = hotStandbyEndpoint;

                    // If endpoint changed, restart the standby connection (unless it's Active game traffic).
                    if (endpointChanged && existingConn.State != StandbyConnectionState.Active)
                    {
                        if (existingConn.Client != null)
                        {
                            var oldClient = existingConn.Client;
                            CleanupClientReliabilityResetTracking_NoLock(oldClient);
                            existingConn.Client = null;
                            try { oldClient.Disconnect(); } catch { }
                        }
                        if (existingConn.MeshClientTransport != null)
                        {
                            try { existingConn.MeshClientTransport.Dispose(); } catch { }
                            existingConn.MeshClientTransport = null;
                        }

                        existingConn.State = StandbyConnectionState.NotStarted;
                        existingConn.FailureCount = 0;
                        EnqueueConnectionAttempt_NoLock(peerAuthorityId);

                        GONetLog.Info($"[HotStandby] Peer {peerAuthorityId} endpoint updated to port {hotStandbyEndpoint.Port}, re-queued for standby connection");
                        if (isHost) pendingMeshTopologyBroadcast = true;
                        return;
                    }

                    // If we still need to connect (e.g., queue was cleared), ensure it's scheduled.
                    if (existingConn.State == StandbyConnectionState.NotStarted ||
                        existingConn.State == StandbyConnectionState.Failed)
                    {
                        EnqueueConnectionAttempt_NoLock(peerAuthorityId);
                    }

                    return;
                }

                var conn = new StandbyConnection(peerAuthorityId, hotStandbyEndpoint);
                standbyConnections[peerAuthorityId] = conn;
                EnqueueConnectionAttempt_NoLock(peerAuthorityId);
                if (isHost) pendingMeshTopologyBroadcast = true;

                GONetLog.Info($"[HotStandby] Peer {peerAuthorityId} discovered at port {hotStandbyEndpoint.Port}, queued for standby connection");
            }
        }

        /// <summary>
        /// Called when a peer leaves.
        /// </summary>
        public void OnPeerLost(ushort peerAuthorityId)
        {
            if (!isInitialized) return;

            lock (connectionLock)
            {
                if (peerAuthorityId == serverDormantShadowAuthorityId &&
                    standbyConnections.TryGetValue(GONetMain.OwnerAuthorityId_Server, out var serverConn) &&
                    serverConn.State == StandbyConnectionState.Active)
                {
                    GONetLog.Debug($"[HotStandby] Preserving host dormant shadow entry {peerAuthorityId} despite peer lost notification");
                    return;
                }

                // Prevent stale queue entries from consuming connection attempt slots.
                RemoveFromConnectionQueue_NoLock(peerAuthorityId);

                if (standbyConnections.TryGetValue(peerAuthorityId, out var conn))
                {
                    if (conn.Client != null)
                    {
                        CleanupClientReliabilityResetTracking_NoLock(conn.Client);
                        try { conn.Client.Disconnect(); }
                        catch { }
                    }
                    // Dispose mesh client transport if present
                    if (conn.MeshClientTransport != null)
                    {
                        try { conn.MeshClientTransport.Dispose(); }
                        catch { }
                        conn.MeshClientTransport = null;
                    }
                    conn.State = StandbyConnectionState.Closed;
                    standbyConnections.Remove(peerAuthorityId);
                    GONetLog.Info($"[HotStandby] Peer {peerAuthorityId} lost, standby connection closed");
                    if (isHost) pendingMeshTopologyBroadcast = true;
                }
            }
        }

        /// <summary>
        /// Called when a mesh topology sync message is received from the host.
        /// SNAPSHOT RECONCILIATION: Treats the message as authoritative truth and reconciles local state to it.
        /// - Prunes peers not in snapshot (except Active connections)
        /// - Refreshes/adds peers from snapshot (even if already connected, to update endpoints)
        /// - Resets on HostEpoch change (failover boundary)
        /// </summary>
        public void OnMeshTopologyReceived(MeshTopologySyncMessage message)
        {
            if (!isInitialized) return;
            if (message?.Peers == null) return;

            var receivedPeers = string.Join(",", message.Peers.Select(p => $"{p.AuthorityId}@{p.DormantServerAddress}:{p.DormantServerPort}"));
            GONetLog.Warning($"[MESH-TOPO] RECV SNAPSHOT: peers=[{receivedPeers}] count={message.Peers.Count} epoch={message.HostEpoch} myAuth={GONetMain.MyAuthorityId}");

            LogCurrentMeshState("BEFORE processing topology");

            // Check for epoch change (failover boundary) - triggers aggressive reset
            bool epochChanged = message.HostEpoch != lastMeshTopologyEpoch;
            if (epochChanged)
            {
                GONetLog.Warning($"[MESH-TOPO] Epoch changed {lastMeshTopologyEpoch} -> {message.HostEpoch}. Resetting mesh state.");
                lastMeshTopologyEpoch = message.HostEpoch;

                // Aggressive reset on failover boundary (keep Active if we need main traffic)
                PurgeAllStandbyConnectionsExceptActive();
            }

            // Build snapshot peer IDs (excluding self)
            var snapshotIds = new HashSet<ushort>(
                message.Peers
                    .Select(p => p.AuthorityId)
                    .Where(id => id != GONetMain.MyAuthorityId)
            );

            // 1) PRUNE: remove any non-Active standby connection not present in snapshot
            // CRITICAL FIX (Dec 2025): Also preserve connections to server authority (1023) - the host's
            // dormant server connection is needed for cascading failover but won't appear in the snapshot
            // (host doesn't include itself in topology messages to clients).
            List<ushort> toRemove;
            lock (connectionLock)
            {
                toRemove = standbyConnections
                    .Where(kvp => kvp.Value.State != StandbyConnectionState.Active
                               && !snapshotIds.Contains(kvp.Key)
                               && kvp.Key != GONetMain.OwnerAuthorityId_Server
                               && (serverDormantShadowAuthorityId == 0 || kvp.Key != serverDormantShadowAuthorityId))
                    .Select(kvp => kvp.Key)
                    .ToList();
            }

            foreach (var staleId in toRemove)
            {
                GONetLog.Warning($"[MESH-TOPO] PRUNE stale peer {staleId} (not in snapshot, not server authority)");
                OnPeerLost(staleId);
            }

            // 2) APPLY: always call OnPeerDiscovered for snapshot peers (even if already connected)
            // This allows endpoint refresh and ensures state converges to truth
            int skippedSelf = 0, refreshedOrAdded = 0;
	            foreach (var peer in message.Peers)
	            {
	                if (peer.AuthorityId == GONetMain.MyAuthorityId)
	                {
	                    skippedSelf++;
	                    continue;
	                }

	                if (peer.DormantServerPort <= 0 || peer.DormantServerPort > ushort.MaxValue)
	                {
	                    GONetLog.Warning($"[MESH-TOPO] Invalid dormant server port for peer {peer.AuthorityId}: {peer.DormantServerPort}");
	                    continue;
	                }

	                GONetConnectionEndpoint endpoint;
	                if (System.Net.IPAddress.TryParse(peer.DormantServerAddress, out var ipAddress))
	                {
	                    endpoint = GONetConnectionEndpoint.CreateFromIPAddress(ipAddress, (ushort)peer.DormantServerPort);
	                }
	                else if (ulong.TryParse(peer.DormantServerAddress, out ulong transportId) && transportId != 0)
	                {
	                    endpoint = GONetConnectionEndpoint.CreateTransportSpecific(transportId);
	                    endpoint.Port = (ushort)peer.DormantServerPort;
	                }
	                else
	                {
	                    GONetLog.Warning($"[MESH-TOPO] Failed to parse endpoint for peer {peer.AuthorityId}: {peer.DormantServerAddress}:{peer.DormantServerPort}");
	                    continue;
	                }

	                // IMPORTANT: do NOT skip "already connected" — snapshot is authoritative
	                // OnPeerDiscovered will handle updating endpoints if changed
	                OnPeerDiscovered(peer.AuthorityId, endpoint);
	                refreshedOrAdded++;
	            }

            GONetLog.Warning($"[MESH-TOPO] RECV summary: refreshedOrAdded={refreshedOrAdded} pruned={toRemove.Count} skippedSelf={skippedSelf} epochChanged={epochChanged} epoch={message.HostEpoch} myAuth={GONetMain.MyAuthorityId}");

            LogCurrentMeshState("AFTER processing topology");
        }

        /// <summary>
        /// Purges unstable standby connections at a failover boundary while preserving:
        /// - <see cref="StandbyConnectionState.Active"/>: the main traffic path post-failover
        /// - <see cref="StandbyConnectionState.Connected"/>: already-established mesh links (helps rapid cascading failover)
        /// - Server authority entry (1023): host dormant-server connection used for cascading failover
        /// </summary>
        private void PurgeAllStandbyConnectionsExceptActive()
        {
            List<ushort> toRemove;
            bool requeuedServerAuthority = false;
            bool requeuedServerShadow = false;
            ushort serverAuthorityId = GONetMain.OwnerAuthorityId_Server;

            lock (connectionLock)
            {
                toRemove = standbyConnections
                    .Where(kvp => kvp.Value.State != StandbyConnectionState.Active
                               && kvp.Value.State != StandbyConnectionState.Connected
                               && kvp.Key != serverAuthorityId
                               && (serverDormantShadowAuthorityId == 0 || kvp.Key != serverDormantShadowAuthorityId))
                    .Select(kvp => kvp.Key)
                    .ToList();

                // Reset queued work to avoid attempting stale peers from the previous epoch.
                // Any required attempts are re-scheduled via EnsureConnectionQueueIsPopulated().
                ClearConnectionQueue_NoLock();

                // BUGFIX (Dec 2025): Epoch purge may clear the connection queue AFTER StandbyHello queued the
                // newly promoted host (server authority). Preserve + re-queue it so AttemptConnection can run.
                if (standbyConnections.TryGetValue(serverAuthorityId, out var serverConn)
                    && serverConn.State == StandbyConnectionState.NotStarted)
                {
                    EnqueueConnectionAttempt_NoLock(serverAuthorityId);
                    requeuedServerAuthority = true;
                }

                if (serverDormantShadowAuthorityId != 0 &&
                    standbyConnections.TryGetValue(serverDormantShadowAuthorityId, out var shadowConn) &&
                    shadowConn.State == StandbyConnectionState.NotStarted)
                {
                    EnqueueConnectionAttempt_NoLock(serverDormantShadowAuthorityId);
                    requeuedServerShadow = true;
                }
            }

            GONetLog.Warning($"[MESH-TOPO] PURGE on epoch change: removing {toRemove.Count} unstable connections (preserving Active/Connected + server authority)");
            if (requeuedServerAuthority)
            {
                GONetLog.Info($"[HotStandby] Re-queued server authority {serverAuthorityId} for connection after epoch purge");
            }
            if (requeuedServerShadow)
            {
                GONetLog.Info($"[HotStandby] Re-queued host dormant shadow {serverDormantShadowAuthorityId} for connection after epoch purge");
            }

            foreach (var id in toRemove)
            {
                OnPeerLost(id);
            }
        }

        /// <summary>
        /// Logs the current mesh state for debugging topology issues.
        /// </summary>
        public void LogCurrentMeshState(string context)
        {
            lock (connectionLock)
            {
                var connStates = new List<string>();
                foreach (var kvp in standbyConnections)
                {
                    connStates.Add($"{kvp.Key}:{kvp.Value.State}");
                }
                var inboundPeers = authorityMapByConnectionUID.Values.Distinct().ToList();

//                GONetLog.Warning($"[MESH-STATE] {context}: myAuth={GONetMain.MyAuthorityId} " +
//                    $"outbound=[{string.Join(",", connStates)}] " +
//                    $"inbound=[{string.Join(",", inboundPeers)}] " +
//                    $"isServer={GONetMain.IsServer}");
            }
        }

        /// <summary>
        /// Checks if we have an active or pending connection to a peer.
        /// </summary>
        public bool IsConnectedToPeer(ushort peerAuthorityId)
        {
            lock (connectionLock)
            {
                if (standbyConnections.TryGetValue(peerAuthorityId, out var conn))
                {
                    // Include Active state - after failover, we already have this connection
                    return conn.State == StandbyConnectionState.Connected ||
                           conn.State == StandbyConnectionState.Connecting ||
                           conn.State == StandbyConnectionState.AwaitingHandshake ||
                           conn.State == StandbyConnectionState.Active;
                }
                return false;
            }
        }

        /// <summary>
        /// Checks if a peer is reachable in the mesh (connected in either direction).
        /// This is a stronger check than <see cref="IsConnectedToPeer"/> because it considers:
        /// 1. Outgoing standby connections (us connecting TO the peer's dormant server)
        /// 2. Incoming connections (peer connected TO our dormant server)
        ///
        /// Use this for critical operations like handoff initiation where we need confidence
        /// that messages can actually reach the peer.
        /// </summary>
        /// <param name="peerAuthorityId">Authority ID of the peer to check</param>
        /// <returns>True if the peer is reachable in the mesh via either direction</returns>
        public bool IsConnectedInMesh(ushort peerAuthorityId)
        {
            lock (connectionLock)
            {
                // Check outgoing connection (us connecting to their dormant server)
                if (standbyConnections.TryGetValue(peerAuthorityId, out var conn))
                {
                    if (conn.State == StandbyConnectionState.Connected ||
                        conn.State == StandbyConnectionState.Active)
                    {
                        return true;
                    }
                }

                // Check incoming connection (them connected to our dormant server)
                if (connectionUIDByAuthorityId.ContainsKey(peerAuthorityId))
                {
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Gets all known peer endpoints for mesh topology sync.
        /// Returns peers that we know an endpoint for (regardless of current connection state).
        /// This keeps topology propagation independent from connection timing.
        /// </summary>
        public IEnumerable<(ushort AuthorityId, ulong PersistentId, GONetConnectionEndpoint Endpoint)> GetAllKnownPeerEndpoints()
        {
            lock (connectionLock)
            {
                foreach (var kvp in standbyConnections)
                {
                    // CRITICAL: Never include server authority (1023) in mesh topology.
                    // The server is the HOST, not a mesh peer.
                    if (kvp.Key == GONetMain.OwnerAuthorityId_Server)
                    {
                        continue;
                    }

                    // If we have a valid endpoint, include it even if connection is NotStarted/Failed.
                    // Clients will converge via snapshot reconciliation and schedule connection attempts locally.
                    if (kvp.Value.PeerEndpoint.Port != 0)
                    {
                        // Use 0 for PersistentId since we don't track it in StandbyConnection
                        yield return (kvp.Key, 0, kvp.Value.PeerEndpoint);
                    }
                }
            }

            // CRITICAL FIX (Dec 2025): Also include peers who connected TO us on our dormant server.
            // These peers sent us a StandbyHello with their dormant port, so we have their endpoint info.
            // This ensures the mesh topology includes ALL known peers, not just those we connected TO.
            lock (connectionLock)
            {
                foreach (var kvp in incomingPeerEndpoints)
                {
                    ushort authorityId = kvp.Key;
                    var endpoint = kvp.Value;

                    // Skip server authority - the server is the HOST, not a mesh peer
                    if (authorityId == GONetMain.OwnerAuthorityId_Server)
                    {
                        continue;
                    }

                    // Skip if we already yielded this peer from standbyConnections
                    if (standbyConnections.ContainsKey(authorityId))
                    {
                        continue;
                    }

                    // Only include if we have valid endpoint info
                    if (endpoint.Port != 0)
                    {
                        yield return (authorityId, 0, endpoint);
                    }
                }
            }
        }

        /// <summary>
        /// Gets a peer's dormant server endpoint if known.
        /// </summary>
        public GONetConnectionEndpoint? GetPeerEndpoint(ushort peerAuthorityId)
        {
            lock (connectionLock)
            {
                if (standbyConnections.TryGetValue(peerAuthorityId, out var conn))
                {
                    return conn.PeerEndpoint;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets a peer's persistent ID if known.
        /// </summary>
        public ulong GetPeerPersistentId(ushort peerAuthorityId)
        {
            // We don't currently track persistent ID in StandbyConnection
            // Return 0 - the topology sync will still work, just without persistent ID tracking
            return 0;
        }

        /// <summary>
        /// Gets all authority IDs of peers that are connected via the mesh.
        /// Used by the failover tiebreaker to ensure all alive peers are considered,
        /// including those not yet propagated through gossip (e.g., late-joiners).
        /// </summary>
        public IEnumerable<ushort> GetConnectedMeshPeerAuthorityIds()
        {
            var peers = new HashSet<ushort>();

            lock (connectionLock)
            {
                // Include peers we have outbound connections to
                // Include both Connected (pre-failover standby) and Active (post-failover promoted) peers
                foreach (var kvp in standbyConnections)
                {
                    // CRITICAL: Never include server authority (1023) - that's the HOST, not a mesh peer
                    if (kvp.Key == GONetMain.OwnerAuthorityId_Server || kvp.Key == serverDormantShadowAuthorityId)
                    {
                        continue;
                    }

                    if (kvp.Value.State == StandbyConnectionState.Connected ||
                        kvp.Value.State == StandbyConnectionState.Active)
                    {
                        peers.Add(kvp.Key);
                    }
                }

                // Also include peers who have connected TO us on our dormant server
                foreach (var kvp in authorityMapByConnectionUID)
                {
                    // CRITICAL: Never include server authority (1023) - that's the HOST, not a mesh peer
                    if (kvp.Value != GONetMain.OwnerAuthorityId_Server && kvp.Value != serverDormantShadowAuthorityId)
                    {
                        peers.Add(kvp.Value);
                    }
                }
            }

            return peers;
        }

        private void AttemptConnection(ushort peerAuthorityId, float currentTime)
        {
            lock (connectionLock)
            {
                if (!standbyConnections.TryGetValue(peerAuthorityId, out var conn))
                    return;

                // Don't attempt connection if already connected or in good state
                if (conn.State == StandbyConnectionState.Connected ||
                    conn.State == StandbyConnectionState.Active ||
                    conn.State == StandbyConnectionState.Connecting ||
                    conn.State == StandbyConnectionState.AwaitingHandshake)
                {
                    return;
                }

                // Check retry backoff
                float retryDelay = Math.Min(BASE_RETRY_DELAY_SECONDS * (float)Math.Pow(2, conn.FailureCount), MAX_RETRY_DELAY_SECONDS);
                if (conn.State == StandbyConnectionState.Failed &&
                    currentTime - conn.LastConnectionAttemptTime < retryDelay)
                {
                    EnqueueConnectionAttempt_NoLock(peerAuthorityId);
                    return;
                }

	                conn.LastConnectionAttemptTime = currentTime;
	                conn.State = StandbyConnectionState.Connecting;

	                // Get the best available address:
	                // - Virtual port transports (Steam): prefer transport-specific IDs (SteamID) with virtual port
	                // - Otherwise: prefer IP (IPv4, then IPv6)
	                string address = null;
	                ushort port = 0;
	                bool usingTransportId = false;

	                if (usesVirtualPorts && conn.PeerEndpoint.HasTransportId)
	                {
	                    usingTransportId = true;
	                    address = conn.PeerEndpoint.TransportSpecificId.ToString();
	                    port = conn.PeerEndpoint.Port;
	                }
	                else if (conn.PeerEndpoint.HasIPv4)
	                {
	                    address = conn.PeerEndpoint.GetIPv4Address().ToString();
	                    port = conn.PeerEndpoint.Port;
	                }
	                else if (conn.PeerEndpoint.HasIPv6)
	                {
	                    address = conn.PeerEndpoint.GetIPv6Address().ToString();
	                    port = conn.PeerEndpoint.Port;
	                }
	                else if (conn.PeerEndpoint.HasTransportId)
	                {
	                    usingTransportId = true;
	                    address = conn.PeerEndpoint.TransportSpecificId.ToString();
	                    port = conn.PeerEndpoint.Port;
	                }

	                if (string.IsNullOrEmpty(address) || port == 0)
	                {
	                    GONetLog.Warning($"[HotStandby] Invalid endpoint for peer {peerAuthorityId}");
	                    conn.State = StandbyConnectionState.Failed;
	                    conn.FailureCount++;
	                    return;
	                }

	                string addressKind = usingTransportId ? "SteamID" : "IP";
	                GONetLog.Info($"[HotStandby] Attempting standby connection to peer {peerAuthorityId} at {address}:{port} ({addressKind})");

                // CRITICAL: Disconnect old client to prevent duplicate callbacks
                // NOTE: We null conn.Client FIRST so that any callbacks triggered during Disconnect()
                // will see conn.Client == null and can be ignored in OnStandbyClientDisconnected
                if (conn.Client != null)
                {
                    var oldClient = conn.Client;
                    CleanupClientReliabilityResetTracking_NoLock(oldClient);
                    conn.Client = null; // Null FIRST before any callbacks can fire

                    try { oldClient.Disconnect(); }
                    catch { }
                }
                // Dispose old mesh client transport if exists
                if (conn.MeshClientTransport != null)
                {
                    try { conn.MeshClientTransport.Dispose(); }
                    catch { }
                    conn.MeshClientTransport = null;
                }

                // Connect on main thread (Unity requires this)
                try
                {
                    GONetClient client;

                    // CRITICAL FIX: Mesh clients must use pluggable transport when enabled
                    // Otherwise they use the old path while dormant servers use new path = incompatible
                    // CRITICAL: Pass isStandbyMeshClient=true so the connection subscribes to its own transport!
                    // Without this, HOST's standby mesh clients can't receive handshake ACKs (Dec 2025 bug fix).
                    if (GONetGlobal.Instance != null && GONetGlobal.Instance.usePluggableTransport)
                    {
                        // Use factory to create correct transport type (NetcodeIO or Steamworks)
                        var meshClientTransport = GONetTransportFactory.CreateAndInitialize();
                        client = new GONetClient(meshClientTransport, isStandbyMeshClient: true);
                        conn.MeshClientTransport = meshClientTransport; // Store for cleanup
                    }
                    else
                    {
                        client = new GONetClient(isStandbyMeshClient: true);
                    }

                    // Improve log clarity: identify this standby mesh client connection in ReliableNetcode logs.
                    if (client.Connection != null)
                    {
                        client.Connection.ConnectionId = $"Dormant-C{GONetMain.MyAuthorityId}->S{peerAuthorityId}";
                    }
                    // Note: IsStandbyMeshClient is now set in constructor (not here) so connection can use it during setup
                    // CRITICAL: Capture client instance in closure to detect stale callbacks from replaced clients
                    var capturedClient = client;
                    client.ClientConnected += _ => OnStandbyClientConnected(peerAuthorityId, capturedClient);
                    client.ClientDisconnected += _ => OnStandbyClientDisconnected(peerAuthorityId, capturedClient);
                    client.ConnectToServer(address, port, STANDBY_MESH_CLIENT_CONNECT_TIMEOUT_SECONDS);

                    conn.Client = client;
                }
                catch (Exception ex)
                {
                    GONetLog.Warning($"[HotStandby] Failed to connect to peer {peerAuthorityId}: {ex.Message}");
                    conn.State = StandbyConnectionState.Failed;
                    conn.FailureCount++;
                    QueueForReconnection(peerAuthorityId);
                }
            }
        }

        private void OnStandbyClientConnected(ushort peerAuthorityId, GONetClient sourceClient)
        {
            // Use the ACTUAL time when connection callback fires, not a stale captured value
            float actualConnectTime = (float)GONetMain.Time.ElapsedSeconds;

            lock (connectionLock)
            {
                if (standbyConnections.TryGetValue(peerAuthorityId, out var conn))
                {
                    // CRITICAL: Ignore callbacks from old/replaced client instances
                    // This prevents stale callbacks from causing spurious state changes
                    if (conn.Client != sourceClient)
                    {
                        GONetLog.Debug($"[HotStandby] Ignoring stale connect callback for peer {peerAuthorityId} - client instance mismatch (old client firing after replacement)");
                        return;
                    }

                    // Don't reset state if already connected - prevents race condition with transport callbacks
                    if (conn.State == StandbyConnectionState.Connected || conn.State == StandbyConnectionState.Active)
	                    {
	                        GONetLog.Debug($"[HotStandby] Ignoring duplicate connect callback for peer {peerAuthorityId} (already {conn.State})");
	                        return;
	                    }

	                    // Keep connection in Connecting state while we coordinate a reliability reset.
	                    // We only transition to AwaitingHandshake once we actually send StandbyHello.
	                    conn.State = StandbyConnectionState.Connecting;

	                    uint epochForReset = GONetMain.HostEpoch;
	                    if (epochForReset != 0)
	                    {
	                        BeginReliabilityResetClient_NoLock(conn.Client, epochForReset, standbyHelloPeerAuthorityId: peerAuthorityId);
	                        GONetLog.Info($"[HotStandby] Connected to peer {peerAuthorityId} - initiating reliability reset before StandbyHello (epoch={epochForReset})");
	                    }
	                    else
	                    {
	                        // Fallback: if HostEpoch is not yet known, send handshake immediately (legacy behavior).
	                        conn.State = StandbyConnectionState.AwaitingHandshake;
	                        conn.ConnectedAtTime = actualConnectTime;

	                        var hello = new StandbyHelloMessage
	                        {
	                            AuthorityId = GONetMain.MyAuthorityId,
	                            SessionGUID = sessionGUID,
	                            SecretToken = StandbyHelloMessage.ComputeSecretToken(sessionGUID, GONetMain.MyAuthorityId),
	                            DormantPort = dormantServerPort,
	                            VirtualPort = dormantVirtualPort
	                        };

		                        try
		                        {
		                            SendStandbyMessageToServer(MSG_TYPE_STANDBY_HELLO, hello, conn.Client);
		                            conn.LastHelloSentTime = actualConnectTime;
		                            GONetLog.Info($"[HotStandby] Connected to peer {peerAuthorityId}, sent handshake");
		                        }
	                        catch (Exception ex)
	                        {
	                            GONetLog.Warning($"[HotStandby] Failed to send handshake to {peerAuthorityId}: {ex.Message}");
	                            conn.State = StandbyConnectionState.Failed;
	                            conn.FailureCount++;
	                        }
	                    }
	                }
	            }
	        }

        private void OnStandbyClientDisconnected(ushort peerAuthorityId, GONetClient sourceClient)
        {
            lock (connectionLock)
            {
                if (standbyConnections.TryGetValue(peerAuthorityId, out var conn))
                {
                    // CRITICAL: Ignore callbacks from old/replaced client instances
                    // When a connection fails and we create a new client, the old client's transport
                    // may still fire disconnect callbacks. We must ignore these stale callbacks.
                    if (conn.Client != sourceClient)
                    {
                        GONetLog.Debug($"[HotStandby-DISCONNECT] Ignoring stale disconnect callback for peer {peerAuthorityId} - client instance mismatch (old client firing after replacement)");
                        return;
                    }

                    // ConnectionUID is per-connection-instance; purge tracking on disconnect to avoid unbounded growth over time.
                    CleanupClientReliabilityResetTracking_NoLock(GetClientConnectionUID(sourceClient));

                    GONetLog.Warning($"[HotStandby-DISCONNECT] Standby client disconnected: peerAuthorityId={peerAuthorityId}, " +
                        $"state={conn.State}, clientState={conn.Client?.ConnectionState}, failureCount={conn.FailureCount}");

                    GONetHostHandoffManager.Instance.NotifyStandbyPeerDisconnected(peerAuthorityId);

                    bool allowActiveReconnect = GONetHostFailoverManager.Instance?.DidVoluntarilyDemote == true;
                    if (conn.State != StandbyConnectionState.Active || allowActiveReconnect)
                    {
                        if (conn.State == StandbyConnectionState.Active && allowActiveReconnect)
                        {
                            float timeSinceHeartbeat = GONetHostFailoverManager.Instance?.TimeSinceLastHeartbeat ?? -1f;
                            GONetLog.Warning($"[HotStandby] Active standby disconnect after voluntary demotion - queuing reconnect " +
                                             $"(peer={peerAuthorityId}, timeSinceHB={timeSinceHeartbeat:F2}s)");
                        }

                        conn.State = StandbyConnectionState.Failed;
                        conn.FailureCount++;
                        QueueForReconnection(peerAuthorityId);
                    }
                }
            }
        }

        private void QueueForReconnection(ushort peerAuthorityId)
        {
            lock (connectionLock)
            {
                EnqueueConnectionAttempt_NoLock(peerAuthorityId);
            }
        }

        private void EnqueueConnectionAttempt_NoLock(ushort peerAuthorityId)
        {
            if (connectionQueueSet.Add(peerAuthorityId))
            {
                connectionQueue.Enqueue(peerAuthorityId);
            }
        }

        private void ClearConnectionQueue_NoLock()
        {
            connectionQueue.Clear();
            connectionQueueSet.Clear();
        }

        private void RemoveFromConnectionQueue_NoLock(ushort peerAuthorityId)
        {
            // Fast path: if not tracked as queued, don't rebuild.
            if (!connectionQueueSet.Remove(peerAuthorityId))
            {
                return;
            }

            if (connectionQueue.Count == 0)
            {
                return;
            }

            // Rebuild queue without the removed peer and re-sync the set.
            var remainingPeers = new List<ushort>(connectionQueue.Count);
            while (connectionQueue.TryDequeue(out ushort queuedPeer))
            {
                if (queuedPeer != peerAuthorityId)
                {
                    remainingPeers.Add(queuedPeer);
                }
            }

            connectionQueueSet.Clear();
            foreach (ushort peer in remainingPeers)
            {
                EnqueueConnectionAttempt_NoLock(peer);
            }
        }

        #endregion

        #region Message Sending

        private void SendStandbyMessage<T>(byte messageType, T message, GONetConnection_ServerToClient connection)
        {
            int bytesUsed = SerializeStandbyMessage(messageType, message);

            // CRITICAL DEBUG: Log every standby message sent via dormant server
            if (messageType == MSG_TYPE_SESSION_PROMOTE || messageType == MSG_TYPE_STANDBY_HELLO_ACK)
            {
                GONetLog.Warning($"[HotStandby-SEND] Sending {(messageType == MSG_TYPE_SESSION_PROMOTE ? "SessionPromote" : "StandbyHelloAck")} via dormant server: bytes={bytesUsed}, " +
                    $"connectionUID={connection.InitiatingClientConnectionUID}, channel={GONetChannel.DistributedHost_Reliable.Id}, " +
                    $"connectionState={connection.GetType().Name}, firstBytes={System.BitConverter.ToString(sendBuffer, 0, Math.Min(8, bytesUsed))}");
            }

            // SLOT RESERVATION (December 2025): DistributedHost_Reliable is System priority
            var priority = GONetChannel.GetMessagePriority(GONetChannel.DistributedHost_Reliable.Id);
            connection.SendMessageOverChannel(sendBuffer, bytesUsed, GONetChannel.DistributedHost_Reliable.Id, priority);
        }

	        private void SendStandbyMessageToServer<T>(byte messageType, T message, GONetClient client)
	        {
	            // CRITICAL: Check IsConnectedToServer before attempting to send.
	            // Without this check, sending on a dead connection causes InvalidOperationException spam.
	            if (client == null || !client.IsConnectedToServer) return;

	            int bytesUsed = SerializeStandbyMessage(messageType, message);
	            client.SendBytesToServer(sendBuffer, bytesUsed, GONetChannel.DistributedHost_Reliable.Id);
	        }

	        private void SendStandbyMessageToServerUnreliable<T>(byte messageType, T message, GONetClient client)
	        {
	            // CRITICAL: Check IsConnectedToServer before attempting to send.
	            if (client == null || !client.IsConnectedToServer) return;

	            int bytesUsed = SerializeStandbyMessage(messageType, message);
	            client.SendBytesToServer(sendBuffer, bytesUsed, GONetChannel.DistributedHost_Unreliable.Id);
	        }

        /// <summary>
        /// Send a standby message unreliably (for high-frequency mesh heartbeats).
        /// </summary>
        private void SendStandbyMessageUnreliable<T>(byte messageType, T message, GONetConnection_ServerToClient connection)
        {
            int bytesUsed = SerializeStandbyMessage(messageType, message);
            // SLOT RESERVATION (December 2025): Unreliable metrics use Gameplay priority (acceptable to drop)
            var priority = GONetChannel.GetMessagePriority(GONetChannel.DistributedHost_Unreliable.Id);
            connection.SendMessageOverChannel(sendBuffer, bytesUsed, GONetChannel.DistributedHost_Unreliable.Id, priority);
        }

        private int SerializeStandbyMessage<T>(byte messageType, T message)
        {
            sendBuffer[0] = messageType;
            var bytes = SerializationUtils.SerializeToBytes(message, out int bytesUsed, out bool needsReturn);

            if (bytesUsed + 1 > sendBuffer.Length)
            {
                Array.Resize(ref sendBuffer, (bytesUsed + 1) * 2);
                sendBuffer[0] = messageType;
            }

            Array.Copy(bytes, 0, sendBuffer, 1, bytesUsed);

            if (needsReturn)
            {
                SerializationUtils.ReturnByteArray(bytes);
            }

            return bytesUsed + 1;
        }

        #endregion

        #region Failover

        /// <summary>
        /// Called when failover occurs and this node needs to switch to a new host.
        /// This is the critical traffic switchover method that:
        /// 1. Pauses sending to prevent split-brain
        /// 2. Resets the reliability layer to avoid sequence mismatches
        /// 3. Switches GONetMain to use the standby connection
        /// 4. Resumes normal game traffic to the new host
        /// </summary>
        /// <param name="newHostAuthorityId">Authority ID of the node becoming the new host</param>
        /// <returns>True if switchover succeeded, false otherwise</returns>
	        public bool TryActivateStandbyConnection(ushort newHostAuthorityId, uint newHostEpoch = 0)
	        {
	            lock (connectionLock)
	            {
                if (!standbyConnections.TryGetValue(newHostAuthorityId, out var conn))
                {
                    // CRITICAL FIX (Dec 2025): Handle re-keyed standby connection lookup.
                    // During promotion, the standby connection is re-keyed from the original peer authority
                    // to the server authority (1023). If we're looking up by the original authority and
                    // it's not found, check if the connection was already activated and re-keyed to 1023.
                    // This fixes the race condition where SessionPromote activates and re-keys the connection
                    // before the failover event tries to activate it using the original authority ID.
                    if (newHostAuthorityId != GONetMain.OwnerAuthorityId_Server &&
                        standbyConnections.TryGetValue(GONetMain.OwnerAuthorityId_Server, out var serverConn) &&
                        serverConn.State == StandbyConnectionState.Active)
                    {
                        GONetLog.Debug($"[HotStandby] Connection to {newHostAuthorityId} was already re-keyed to {GONetMain.OwnerAuthorityId_Server} and is Active (idempotent success)");
                        return true;
                    }

                    GONetLog.Warning($"[HotStandby] No standby connection to new host {newHostAuthorityId}");
                    return false;
                }

                // CRITICAL FIX (December 2025): Make idempotent - if already Active, return success.
                // This handles the case where traffic switchover happens via OnSessionPromoteReceived,
                // and then OnEmergencyPromotionReceived fires OnNewHostDetectedWithOriginalId which
                // tries to activate the same connection again.
                if (conn.State == StandbyConnectionState.Active)
                {
                    // VOLUNTARY HANDOFF FIX (Dec 2025): If we activated while still host, the reliability reset
                    // was skipped (isHost=true). Ensure it runs once we've demoted by re-checking here.
                    uint activeEpochForReset = newHostEpoch != 0 ? newHostEpoch : GONetMain.HostEpoch;
                    if (activeEpochForReset > 0 && conn.Client != null)
                    {
                        BeginFailoverReliabilityReset_NoLock(conn.Client, activeEpochForReset, newHostAuthorityId);
                    }

                    GONetLog.Debug($"[HotStandby] Standby connection to {newHostAuthorityId} already active (idempotent success)");
                    return true;
                }

                if (conn.State != StandbyConnectionState.Connected || conn.Client == null)
                {
                    GONetLog.Warning($"[HotStandby] Standby connection to new host {newHostAuthorityId} not ready (state={conn.State})");
                    return false;
                }

                if (newHostAuthorityId == GONetMain.OwnerAuthorityId_Server)
                {
                    serverDormantShadowAuthorityId = 0;
                }

                GONetLog.Info($"[HotStandby] === TRAFFIC SWITCHOVER BEGIN === New host: {newHostAuthorityId}");

                // Step 1: Mark as active (prevents further standby-mode handling)
                var oldState = conn.State;
                conn.State = StandbyConnectionState.Active;
                GONetLog.Warning($"[MESH-CONN] STATE CHANGE: peer={newHostAuthorityId} {oldState}->{conn.State} (PROMOTION) myAuth={GONetMain.MyAuthorityId}");

	                // IMPORTANT (December 2025): ReliableNetcode state may be inconsistent across failover boundaries.
	                // We coordinate a full reset via an UNRELIABLE handshake after switchover so both sides reset together.

                // Step 2: Mark client as no longer a standby mesh client
                conn.Client.IsStandbyMeshClient = false;

                // Step 3: Switch the active client in GONetMain
                // This makes all game traffic flow through the standby connection
                GONetMain.SwitchToStandbyClient(conn.Client, newHostAuthorityId);

	                // Step 4: Reset time sync to resync with new host
	                // CRITICAL: Without this, client time may drift from new host by several seconds
	                // The new host's time may differ from the old host's time, especially if the
	                // new host was a client that had accumulated some time drift before promotion.
	                GONetMain.ResetTimeSyncGap("failover to new host");

	                // Step 5: Coordinate a full ReliableNetcode reset with the new host to prevent
	                // post-failover reliable stalls (e.g., sender stuck retransmitting old msgSeq forever).
	                uint epochForReset = newHostEpoch != 0 ? newHostEpoch : GONetMain.HostEpoch;
                BeginFailoverReliabilityReset_NoLock(conn.Client, epochForReset, newHostAuthorityId);

	                GONetLog.Info($"[HotStandby] === TRAFFIC SWITCHOVER COMPLETE === Now connected to host {newHostAuthorityId}");

                // CRITICAL FIX (December 2025): Re-key the activated standby connection from peer's original
                // authority to server authority (1023). After promotion, the peer becomes the server and will
                // send StandbyHello with authority 1023 (not their original client authority). Without this
                // re-keying, HandleStandbyHello would create a NEW connection to authority 1023's dormant port
                // because it wouldn't find an existing Active connection for authority 1023.
                // This caused the demoted client to connect only to the ViceHost path (dormant port) instead of
                // maintaining the main sync connection (promoted main server port), resulting in stuck objects
                // after voluntary handoff because regular sync bundles weren't being received.
                if (newHostAuthorityId != GONetMain.OwnerAuthorityId_Server)
                {
                    // First check if there's a stale entry for 1023 that we need to clean up before re-keying
                    if (standbyConnections.TryGetValue(GONetMain.OwnerAuthorityId_Server, out var existingServerEntry) && existingServerEntry != conn)
                    {
                        GONetLog.Info($"[HotStandby] Cleaning up stale server authority entry (state={existingServerEntry.State}) before re-keying");
                        if (existingServerEntry.Client != null)
                        {
                            try
                            {
                                CleanupClientReliabilityResetTracking_NoLock(existingServerEntry.Client);
                                existingServerEntry.Client.Disconnect();
                            }
                            catch { }
                        }
                        if (existingServerEntry.MeshClientTransport != null)
                        {
                            try { existingServerEntry.MeshClientTransport.Dispose(); } catch { }
                            existingServerEntry.MeshClientTransport = null;
                        }
                        standbyConnections.Remove(GONetMain.OwnerAuthorityId_Server);
                    }

                    // Now re-key the activated connection from peer authority to server authority
                    ushort originalPeerAuthority = newHostAuthorityId;
                    standbyConnections.Remove(newHostAuthorityId);
                    conn.PeerAuthorityId = GONetMain.OwnerAuthorityId_Server;
                    standbyConnections[GONetMain.OwnerAuthorityId_Server] = conn;
                    GONetLog.Info($"[HotStandby] Re-keyed Active standby connection from authority {originalPeerAuthority} to {GONetMain.OwnerAuthorityId_Server} (promoted peer now has server identity)");
                    if (originalPeerAuthority != 0 && originalPeerAuthority != GONetMain.OwnerAuthorityId_Server)
                    {
                        serverDormantShadowAuthorityId = originalPeerAuthority;
                        GONetLog.Info($"[HotStandby] Tracking host dormant shadow authority {serverDormantShadowAuthorityId} after re-key");
                    }
                    else
                    {
                        serverDormantShadowAuthorityId = 0;
                    }

                    if (!GONetMain.IsServer &&
                        serverDormantShadowAuthorityId != 0 &&
                        incomingPeerEndpoints.TryGetValue(GONetMain.OwnerAuthorityId_Server, out var hostDormantEndpoint) &&
                        hostDormantEndpoint.Port != 0)
                    {
                        if (!standbyConnections.TryGetValue(serverDormantShadowAuthorityId, out var shadowConn))
                        {
                            var shadowConnNew = new StandbyConnection(serverDormantShadowAuthorityId, hostDormantEndpoint);
                            standbyConnections[serverDormantShadowAuthorityId] = shadowConnNew;
                            EnqueueConnectionAttempt_NoLock(serverDormantShadowAuthorityId);
                            GONetLog.Info($"[HotStandby] Created host dormant shadow connection {serverDormantShadowAuthorityId} at port {hostDormantEndpoint.Port} (post-rekey)");
                        }
                        else
                        {
                            bool endpointChanged =
                                shadowConn.PeerEndpoint.Port != hostDormantEndpoint.Port ||
                                shadowConn.PeerEndpoint.IPv4Address != hostDormantEndpoint.IPv4Address ||
                                shadowConn.PeerEndpoint.IPv6AddressHigh != hostDormantEndpoint.IPv6AddressHigh ||
                                shadowConn.PeerEndpoint.IPv6AddressLow != hostDormantEndpoint.IPv6AddressLow ||
                                shadowConn.PeerEndpoint.TransportSpecificId != hostDormantEndpoint.TransportSpecificId;

                            shadowConn.PeerEndpoint = hostDormantEndpoint;

                            bool shouldRestart = endpointChanged ||
                                shadowConn.State == StandbyConnectionState.Closed ||
                                shadowConn.State == StandbyConnectionState.Failed;

                            if (shadowConn.State == StandbyConnectionState.Active)
                            {
                                if (endpointChanged)
                                {
                                    GONetLog.Info($"[HotStandby] Updated endpoint metadata for host dormant shadow {serverDormantShadowAuthorityId} (port={hostDormantEndpoint.Port}) - not restarting");
                                }
                            }
                            else if (shouldRestart)
                            {
                                if (shadowConn.Client != null)
                                {
                                    var oldClient = shadowConn.Client;
                                    CleanupClientReliabilityResetTracking_NoLock(oldClient);
                                    shadowConn.Client = null;
                                    try { oldClient.Disconnect(); } catch { }
                                }
                                if (shadowConn.MeshClientTransport != null)
                                {
                                    try { shadowConn.MeshClientTransport.Dispose(); } catch { }
                                    shadowConn.MeshClientTransport = null;
                                }

                                shadowConn.State = StandbyConnectionState.NotStarted;
                                shadowConn.FailureCount = 0;
                                shadowConn.WatchdogReliabilityResetAttemptCount = 0;
                                EnqueueConnectionAttempt_NoLock(serverDormantShadowAuthorityId);
                                GONetLog.Info($"[HotStandby] Restarting host dormant shadow connection {serverDormantShadowAuthorityId} due to stored endpoint update");
                            }
                            else if (shadowConn.State == StandbyConnectionState.NotStarted)
                            {
                                EnqueueConnectionAttempt_NoLock(serverDormantShadowAuthorityId);
                            }
                        }
                    }
                }

                // CRITICAL FIX (Dec 2025): Clean up stale server authority entry after cascading failover.
                // When peer X (e.g., authority 3) promotes to become host (authority 1023), we activate
                // standbyConnections[3]. But we may still have a stale standbyConnections[1023] entry
                // pointing to the PREVIOUS dead host's dormant server endpoint.
                // This stale entry causes "1 of 0 peers" in UI because it keeps trying to connect
                // to a dead endpoint instead of recognizing we're already connected via the activated peer.
                //
                // IMPORTANT: Do NOT remove entries in NotStarted state - they may have just been created
                // by HandleStandbyHello with the NEW host's correct dormant server port. Only remove entries
                // that have an active client (Connecting/AwaitingHandshake/Connected states) pointing to
                // an endpoint that is now dead.
                if (newHostAuthorityId != GONetMain.OwnerAuthorityId_Server)
                {
                    // We just activated a peer who became the new server (1023).
                    // Check if there's an old server authority entry that needs cleanup.
                    // NOTE: After the re-key fix above, the 1023 entry IS our activated connection, so skip if same object.
                    if (standbyConnections.TryGetValue(GONetMain.OwnerAuthorityId_Server, out var oldServerConn) && oldServerConn != conn)
                    {
                        // RACE FIX (Dec 2025): Do NOT remove the server-authority (1023) entry just because it's Failed.
                        // After promotion, the host starts a NEW dormant server on a different port and re-sends StandbyHello.
                        // Clients restart their 1023 standby connection to that new dormant port. The first connection attempt
                        // can transiently fail during traffic switchover. If we prune the entry here, we can permanently drop
                        // the only notification of the new dormant port, resulting in incomplete mesh ("3/2 peers") and
                        // post-failover desync/ghost objects.
                        //
                        // The 1023 entry is only considered stale here if it is Failed AND still points to the SAME port as the
                        // newly-activated peer (i.e., the old dormant port which is now the main server). In that case, it cannot
                        // represent the host's *new* dormant server, so it is safe to remove.
                        ushort activeHostPort = conn.PeerEndpoint.Port;
                        ushort serverDormantPort = oldServerConn.PeerEndpoint.Port;
                        bool serverAuthorityEntryStillPointsAtActiveHostPort =
                            activeHostPort != 0 &&
                            serverDormantPort != 0 &&
                            serverDormantPort == activeHostPort;
                        bool shouldRemoveStaleServerAuthorityEntry =
                            oldServerConn.State == StandbyConnectionState.Failed &&
                            serverAuthorityEntryStillPointsAtActiveHostPort;

                        if (shouldRemoveStaleServerAuthorityEntry)
                        {
                            GONetLog.Info($"[HotStandby] Removing stale server authority entry (state={oldServerConn.State}, failures={oldServerConn.FailureCount}, serverDormantPort={serverDormantPort}, activeHostPort={activeHostPort}) - new host is peer {newHostAuthorityId}");

                            // Close the old connection if it exists
                            if (oldServerConn.Client != null)
                            {
                                try
                                {
                                    CleanupClientReliabilityResetTracking_NoLock(oldServerConn.Client);
                                    oldServerConn.Client.Disconnect();
                                }
                                catch { }
                            }
                            if (oldServerConn.MeshClientTransport != null)
                            {
                                try { oldServerConn.MeshClientTransport.Dispose(); } catch { }
                                oldServerConn.MeshClientTransport = null;
                            }

                            standbyConnections.Remove(GONetMain.OwnerAuthorityId_Server);
                        }
                        else
                        {
                            GONetLog.Debug($"[HotStandby] Keeping server authority entry (state={oldServerConn.State}, failures={oldServerConn.FailureCount}, serverDormantPort={serverDormantPort}, activeHostPort={activeHostPort}) - likely host's new dormant server port or pending retry");
                        }
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Gets the standby client for a specific peer authority ID.
        /// </summary>
        public GONetClient GetStandbyClient(ushort peerAuthorityId)
        {
            lock (connectionLock)
            {
                if (standbyConnections.TryGetValue(peerAuthorityId, out var conn) &&
                    conn.State == StandbyConnectionState.Connected &&
                    conn.Client != null)
                {
                    return conn.Client;
                }
                return null;
            }
        }

        /// <summary>
        /// Finds the peer authority ID for a given standby client or its connection.
        /// This is critical for SessionPromote handling - we need to know which peer
        /// sent the message when we receive it on a standby client connection.
        /// </summary>
        /// <param name="client">The GONetClient to look up</param>
        /// <param name="peerAuthorityId">Output: the peer's authority ID if found</param>
        /// <returns>True if the client was found in our standby connections</returns>
        public bool TryGetPeerAuthorityIdForClient(GONetClient client, out ushort peerAuthorityId)
        {
            peerAuthorityId = 0;
            if (client == null) return false;

            lock (connectionLock)
            {
                foreach (var kvp in standbyConnections)
                {
                    if (kvp.Value.Client == client)
                    {
                        peerAuthorityId = kvp.Key;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Finds the peer authority ID for a given GONetConnection that belongs to a standby client.
        /// This is critical for SessionPromote handling - we need to know which peer
        /// sent the message when we receive it on a standby client connection.
        /// NOTE: GONetConnection_ClientToServer.OwnerAuthorityId is ALWAYS 1023 (server authority),
        /// so we cannot use that to identify the peer. Instead, we look up which standby connection
        /// uses this GONetConnection.
        /// </summary>
        /// <param name="connection">The GONetConnection to look up</param>
        /// <param name="peerAuthorityId">Output: the peer's authority ID if found</param>
        /// <returns>True if the connection was found in our standby connections</returns>
        public bool TryGetPeerAuthorityIdForConnection(GONetConnection connection, out ushort peerAuthorityId)
        {
            peerAuthorityId = 0;
            if (connection == null) return false;

            lock (connectionLock)
            {
                foreach (var kvp in standbyConnections)
                {
                    // Compare the connection object to find which standby client owns it
                    if (kvp.Value.Client != null && kvp.Value.Client.Connection == connection)
                    {
                        peerAuthorityId = kvp.Key;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Called when this node becomes the new host.
        /// Promotes the dormant server to active.
        /// </summary>
        public bool OnBecameHost()
        {
            isHost = true;
            serverDormantShadowAuthorityId = 0;

            if (dormantServer == null || !dormantServer.IsRunning)
            {
                GONetLog.Error("[HotStandby] Cannot promote - dormant server not running");
                return false;
            }

            bool promoted = dormantServer.PromoteToActive();

            // CRITICAL (December 2025): Set the promoted dormant server as GONetMain.gonetServer
            // Without this, heartbeat sending fails because GONetMain.gonetServer is null/wrong
            // and SendBytesToAllClients() doesn't reach the connected clients.
            if (promoted)
            {
                GONetMain.SetPromotedServer(dormantServer);

                // INTEGRATION GLUE: Establish client-host loopback connection
                // This is critical for IsGONetReady() to return true after promotion.
                // The client side needs ConnectionState=Connected and IsInitializedWithServer=true.
                // MUST be called AFTER SetPromotedServer so GONetServer is available.
                GONetMain.EstablishClientHostLoopbackForFailover();

                // Steamworks hot-standby promotion detail:
                // The promoted dormant server is typically listening on Steam P2P virtual port 1 (standby mesh).
                // New gameplay clients (SteamID join) connect on virtual port 0, so we must also listen on 0
                // to allow new joiners after failover.
                if (usesVirtualPorts && dormantServer.Transport is SteamworksTransport steamworksTransport)
                {
                    if (!steamworksTransport.EnsureP2PListenSocket(MAIN_VIRTUAL_PORT))
                    {
                        GONetLog.Warning($"[HotStandby] Promoted server is not listening on Steam P2P virtualPort {MAIN_VIRTUAL_PORT} - new clients may fail to connect");
                    }
                }
            }

            // CRITICAL: Clean up standby connections after promotion
            // 1. Remove the dead host entry (if we were authority 1, connecting to 1023,
            //    and now WE are 1023, we don't want to connect to ourselves)
            // 2. Remove entries for peers that are no longer reachable
            lock (connectionLock)
            {
                // The previous host was GONetMain.CurrentHostAuthorityId BEFORE we promoted
                // Now we ARE that authority, so remove our own entry from standby connections
                ushort ourNewAuthorityId = GONetMain.MyAuthorityId; // This is 1023 after promotion
                if (standbyConnections.ContainsKey(ourNewAuthorityId))
                {
                    var selfConn = standbyConnections[ourNewAuthorityId];
                    if (selfConn.Client != null)
                    {
                        CleanupClientReliabilityResetTracking_NoLock(selfConn.Client);
                        try { selfConn.Client.Disconnect(); }
                        catch { }
                    }
                    // Dispose mesh client transport if present
                    if (selfConn.MeshClientTransport != null)
                    {
                        try { selfConn.MeshClientTransport.Dispose(); }
                        catch { }
                    }
                    standbyConnections.Remove(ourNewAuthorityId);
                    GONetLog.Info($"[HotStandby] Removed self-connection entry (authority {ourNewAuthorityId}) after promotion");
                }

                // CRITICAL FIX (December 2025): Clean up stale Active connections to dead peers
                // When self-promoting, we may have Active connections to peers that are now dead.
                // These peers won't have incoming connections to our dormant server, so we can
                // identify them by checking authorityMapByConnectionUID.Values.
                // Example: After cascading failover, we had Active connection to peer 3 (dead host),
                // but peer 3 is dead and will never reconnect, causing "1 of 0 peers" display.
                var incomingPeers = new HashSet<ushort>(authorityMapByConnectionUID.Values.Where(a => a != GONetMain.OwnerAuthorityId_Server));
                var staleActivePeers = standbyConnections
                    .Where(kvp => kvp.Value.State == StandbyConnectionState.Active && !incomingPeers.Contains(kvp.Key))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var stalePeerId in staleActivePeers)
                {
                    var staleConn = standbyConnections[stalePeerId];
                    if (staleConn.Client != null)
                    {
                        CleanupClientReliabilityResetTracking_NoLock(staleConn.Client);
                        try { staleConn.Client.Disconnect(); }
                        catch { }
                    }
                    if (staleConn.MeshClientTransport != null)
                    {
                        try { staleConn.MeshClientTransport.Dispose(); }
                        catch { }
                    }
                    standbyConnections.Remove(stalePeerId);
                    GONetLog.Info($"[HotStandby] Removed stale Active connection to dead peer {stalePeerId} after self-promotion (not in dormant server clients)");
                }

                // Remove the previous host from connection queue if present
                var remainingPeers = new List<ushort>();
                while (connectionQueue.TryDequeue(out ushort queuedPeer))
                {
                    connectionQueueSet.Remove(queuedPeer);
                    if (queuedPeer != ourNewAuthorityId)
                    {
                        remainingPeers.Add(queuedPeer);
                    }
                }
                foreach (ushort peer in remainingPeers)
                {
                    EnqueueConnectionAttempt_NoLock(peer);
                }
                connectionQueueSet.Remove(ourNewAuthorityId);
            }

            if (promoted)
            {
                // FAILOVER FIX (Dec 2025): Include deferred despawns in SessionPromote so clients process them
                // before traffic switchover triggers the ReliableNetcode reset (which suppresses reliable traffic).
                uint[] deferredDespawnIds = null;
                try
                {
                    deferredDespawnIds = GONetHostFailoverManager.Instance.ConsumePendingDespawnNotificationsForSessionPromote();
                    if (deferredDespawnIds != null && deferredDespawnIds.Length > 0)
                    {
                        GONetLog.Info($"[HotStandby] Including {deferredDespawnIds.Length} deferred despawns in SessionPromote (promotion cleanup)");
                    }
                }
                catch (Exception ex)
                {
                    GONetLog.Warning($"[HotStandby] Failed to collect deferred despawns for SessionPromote: {ex.Message}");
                }

                // Send SessionPromote to all connected clients
                var promoteMsg = new SessionPromoteMessage
                {
                    HostEpoch = GONetMain.HostEpoch,
                    SessionGUID = sessionGUID,
                    HostAuthorityId = GONetMain.MyAuthorityId,
                    CurrentTick = GONetMain.Time.ElapsedTicks,
                    DeferredDespawnGONetIds = deferredDespawnIds
                };

                // Track all known peers for retry purposes
                float elapsedSeconds = (float)GONetMain.Time.ElapsedSeconds;
                peersAwaitingSessionPromote.Clear();
                lock (connectionLock)
                {
                    // Add all peers from our outgoing standby connections
                    foreach (var conn in standbyConnections.Values)
                    {
                        if (conn.PeerAuthorityId == GONetMain.OwnerAuthorityId_Server) continue;
                        peersAwaitingSessionPromote.Add(conn.PeerAuthorityId);
                    }
                    // Add all peers from our incoming dormant server connections
                    foreach (var peerAuth in authorityMapByConnectionUID.Values)
                    {
                        if (peerAuth == GONetMain.OwnerAuthorityId_Server) continue;
                        peersAwaitingSessionPromote.Add(peerAuth);
                    }
                }

                lock (connectionLock)
                {
                    foreach (var kvp in authorityMapByConnectionUID)
                    {
                        ulong uid = kvp.Key;
                        ushort peerAuthorityId = kvp.Value;

                        if (peerAuthorityId == GONetMain.OwnerAuthorityId_Server)
                        {
                            GONetLog.Warning($"[HotStandby] Skipping SessionPromote to server authority (connection UID {uid}) - not a gameplay client");
                            continue;
                        }

                        var connection = dormantServer.GetConnectionByUID(uid);
                        if (connection != null)
                        {
                            try
                            {
                                SendStandbyMessage(MSG_TYPE_SESSION_PROMOTE, promoteMsg, connection);
                                GONetLog.Info($"[HotStandby] Sent SessionPromote to authority {peerAuthorityId} (connection UID {uid})");
                                peersAwaitingSessionPromote.Remove(peerAuthorityId);
                            }
                            catch (Exception ex)
                            {
                                GONetLog.Warning($"[HotStandby] Failed to send SessionPromote to authority {peerAuthorityId}: {ex.Message}");
                            }
                        }
                        else
                        {
                            GONetLog.Warning($"[HotStandby] Connection for authority {peerAuthorityId} not found during promotion broadcast");
                        }
                    }
                }

                int preConnectedClientCount;
                lock (connectionLock)
                {
                    preConnectedClientCount = authorityMapByConnectionUID.Values.Count(a => a != GONetMain.OwnerAuthorityId_Server);
                }
                GONetLog.Info($"[HotStandby] This node is now host - dormant server promoted with {preConnectedClientCount} pre-connected clients");

                // CRITICAL FIX: Also send SessionPromote via our OUTGOING standby connections.
                // This handles the case where a peer's incoming connection to our dormant server
                // was lost or not yet established, but our outgoing connection to their dormant
                // server is still working. The peer will receive the promotion message and
                // activate their standby connection to us.
                int outgoingSent = 0;
                foreach (var conn in standbyConnections.Values)
                {
                    // Only send via connected outgoing connections that aren't already used
                    // for the dormant server (those are already notified above)
                    if (conn.State == StandbyConnectionState.Connected && conn.Client != null)
                    {
                        // Skip if this peer is already in our dormant server's authority map
                        // (they would have already received the message via the incoming connection)
                        if (connectionUIDByAuthorityId.ContainsKey(conn.PeerAuthorityId))
                        {
                            GONetLog.Debug($"[HotStandby] Skipping outgoing SessionPromote to {conn.PeerAuthorityId} - already notified via dormant server");
                            peersAwaitingSessionPromote.Remove(conn.PeerAuthorityId);
                            continue;
                        }

                        try
                        {
                            SendStandbyMessageToServer(MSG_TYPE_SESSION_PROMOTE, promoteMsg, conn.Client);
                            outgoingSent++;
                            GONetLog.Info($"[HotStandby] Sent SessionPromote via outgoing connection to peer {conn.PeerAuthorityId}");
                            peersAwaitingSessionPromote.Remove(conn.PeerAuthorityId);
                        }
                        catch (Exception ex)
                        {
                            GONetLog.Warning($"[HotStandby] Failed to send SessionPromote via outgoing to {conn.PeerAuthorityId}: {ex.Message}");
                        }
                    }
                }

                if (outgoingSent > 0)
                {
                    GONetLog.Info($"[HotStandby] Sent SessionPromote via {outgoingSent} outgoing standby connections (backup notification path)");
                }

                // If there are still peers awaiting SessionPromote, set up retries
                if (peersAwaitingSessionPromote.Count > 0)
                {
                    pendingSessionPromote = promoteMsg;
                    sessionPromoteRetryStartTime = elapsedSeconds;
                    lastSessionPromoteRetryTime = elapsedSeconds;
                    GONetLog.Info($"[HotStandby] {peersAwaitingSessionPromote.Count} peers awaiting SessionPromote - will retry: " +
                                 $"[{string.Join(", ", peersAwaitingSessionPromote)}]");
                }
                else
                {
                    pendingSessionPromote = null;
                    GONetLog.Info("[HotStandby] All peers successfully notified of SessionPromote");
                }
            }
            else
            {
                GONetLog.Error("[HotStandby] Mesh not available for emergency failover - manual reconnection required. " +
                              "This can happen if distributed host authority is disabled or hot standby was not initialized.");
            }

            // CRITICAL FIX (Dec 2025): After promotion, the old dormant server is now the main server.
            // We must start a NEW dormant server on a different port for:
            // 1. Future failover capability
            // 2. Late-joiners to establish standby connections (otherwise they connect to main server twice!)
            if (promoted)
            {
                StartNewDormantServerAfterPromotion();

                // MESH TOPOLOGY SYNC (Dec 2025): After failover promotion, broadcast full mesh topology
                // to all connected clients. This ensures late-joiners who connected after the original
                // host died but before this promotion have complete mesh knowledge for the next failover.
                // Without this, cascading failover causes split-brain (multiple nodes self-promote).
                // NOTE: We call this directly rather than with a delay - the server is already operational.
                GONetGossipIntegration.BroadcastMeshTopologyToAllClients();
                GONetLog.Info("[HotStandby] Broadcast mesh topology to all clients after promotion");

                // RECONCILIATION (Dec 2025): Schedule a reconciliation snapshot to catch any missed despawns.
                // Clients that reconnected after despawn notifications were sent will receive this snapshot
                // and destroy any ghost objects that shouldn't exist.
                GONetHostFailoverManager.Instance.ScheduleReconciliationSnapshot(GONetMain.HostEpoch);
            }

            return promoted;
        }

        /// <summary>
        /// CRITICAL (Dec 2025): After promotion, starts a NEW dormant server on a different port.
        /// Without this, the gossip endpoint still points to the old dormant port (now main server),
        /// causing late-joiners to connect to the same port for both main and standby connections.
        /// </summary>
        private void StartNewDormantServerAfterPromotion()
        {
            // The old dormant server is now the main GONet server
            // Clear our reference to it (don't dispose - it's still active as main server!)
            ushort oldDormantPort = dormantServerPort;
            dormantServer = null;
            dormantServerPort = 0;
            dormantVirtualPort = -1;

            // CRITICAL: Do NOT dispose dormantTransport! The promoted GONetServer is still
            // using this same transport instance for listening. Just clear our reference.
            // The transport will be disposed when the server is eventually shut down.
            dormantTransport = null;

            // Clear connection tracking for the old dormant server
            // The connections are now tracked by the main GONet server
            lock (connectionLock)
            {
                authorityMapByConnectionUID.Clear();
                connectionUIDByAuthorityId.Clear();
                incomingPeerEndpoints.Clear();
                pendingConnections.Clear();
                dormantClientLastKeepalive.Clear();
                dormantClientLastKeepaliveSequenceReceived.Clear();
                dormantClientLastKeepaliveSequenceAdvancedTime.Clear();
                dormantClientWatchdogLastResetAttemptTime.Clear();
                dormantClientWatchdogResetAttemptCount.Clear();
                dormantClientLastSentTimestamp.Clear();
                dormantClientTimestampToEcho.Clear();
                dormantClientRTT.Clear();
                dormantServerKeepaliveSequence.Clear();
            }

            // Start a NEW dormant server on the next available port after the old one
            // Skip the old port (now main server) by starting search from oldDormantPort + 1
            ushort newStartingPort = (ushort)(oldDormantPort + 1);

            // Get the transport to use (or create a new one for the new dormant server)
            IGONetTransport mainTransport = GONetMain.gonetServer?.Transport;

	            if (StartDormantServer(newStartingPort, mainTransport))
	            {
	                // Update gossip with the new dormant server binding
	                UpdateGossipEndpointForHotStandby(mainTransport, "PostPromotion");
	                if (dormantServerPort > 0)
	                {
	                    GONetLog.Info($"[HotStandby] Post-promotion: new dormant server on port {dormantServerPort} (old main server on {oldDormantPort})");

	                    // CRITICAL FIX (Dec 2025): Re-send StandbyHello to existing peers with the new dormant server port.
	                    // After promotion, we start a NEW dormant server on a different port. Existing peers still have
	                    // the OLD dormant server port (which is now the main GONet server). Without this notification,
	                    // peers never connect to our new dormant server, causing "1/0 peers" in the UI.
	                    ResendStandbyHelloToExistingPeers();
	                }
            }
            else
            {
                GONetLog.Warning($"[HotStandby] Post-promotion: failed to start new dormant server (late-joiners will not have standby connections)");
            }
        }

        /// <summary>
        /// Re-sends StandbyHello to all existing peers to notify them of our new dormant server port.
        /// Called after promotion when a new dormant server is started on a different port.
        /// </summary>
        private void ResendStandbyHelloToExistingPeers()
        {
            if (dormantServerPort == 0)
            {
                GONetLog.Warning("[HotStandby] Cannot resend StandbyHello - no dormant server port");
                return;
            }

            List<(ushort authorityId, ulong connectionUid)> inboundPeers = new List<(ushort, ulong)>();
            List<(ushort authorityId, GONetClient client)> outboundPeers = new List<(ushort, GONetClient)>();
            HashSet<ushort> notifiedAuthorities = new HashSet<ushort>();

            lock (connectionLock)
            {
                foreach (var kvp in authorityMapByConnectionUID)
                {
                    ushort authorityId = kvp.Value;
                    if (authorityId == GONetMain.OwnerAuthorityId_Server)
                    {
                        continue;
                    }

                    if (notifiedAuthorities.Add(authorityId))
                    {
                        inboundPeers.Add((authorityId, kvp.Key));
                    }
                }

                foreach (var kvp in standbyConnections)
                {
                    // Only send to peers with active/connected clients
                    if (kvp.Value.Client != null && kvp.Value.Client.IsConnectedToServer)
                    {
                        if (notifiedAuthorities.Add(kvp.Key))
                        {
                            outboundPeers.Add((kvp.Key, kvp.Value.Client));
                        }
                    }
                }
            }

            int mainServerPeers = 0;
            var remoteClients = GONetMain.gonetServer?.remoteClients;
            if (remoteClients != null)
            {
                foreach (var remoteClient in remoteClients)
                {
                    if (remoteClient == null) continue;

                    ushort clientAuthorityId = remoteClient.ConnectionToClient.OwnerAuthorityId;
                    if (clientAuthorityId == GONetMain.OwnerAuthorityId_Server) continue;
                    if (notifiedAuthorities.Contains(clientAuthorityId)) continue;

                    mainServerPeers++;
                }
            }

            int totalPeers = inboundPeers.Count + outboundPeers.Count + mainServerPeers;
            if (totalPeers == 0)
            {
                GONetLog.Debug("[HotStandby] No peers to notify of new dormant server port");
                return;
            }

            GONetLog.Info($"[HotStandby] Resending StandbyHello to {totalPeers} peers with new dormant port {dormantServerPort} " +
                          $"(inbound={inboundPeers.Count}, outbound={outboundPeers.Count}, mainServer={mainServerPeers})");

            var hello = new StandbyHelloMessage
            {
                AuthorityId = GONetMain.MyAuthorityId,
                SessionGUID = sessionGUID,
                SecretToken = StandbyHelloMessage.ComputeSecretToken(sessionGUID, GONetMain.MyAuthorityId),
                DormantPort = dormantServerPort,
                VirtualPort = dormantVirtualPort
            };

            if (dormantServer != null)
            {
                foreach (var (authorityId, connectionUid) in inboundPeers)
                {
                    var connection = dormantServer.GetConnectionByUID(connectionUid);
                    if (connection == null)
                    {
                        continue;
                    }

                    try
                    {
                        SendStandbyMessage(MSG_TYPE_STANDBY_HELLO, hello, connection);
                        GONetLog.Debug($"[HotStandby] Sent updated StandbyHello to inbound peer {authorityId} with dormant port {dormantServerPort}");
                    }
                    catch (Exception ex)
                    {
                        GONetLog.Warning($"[HotStandby] Failed to resend StandbyHello to inbound peer {authorityId}: {ex.Message}");
                    }
                }
            }

            foreach (var (authorityId, client) in outboundPeers)
            {
                try
                {
                    SendStandbyMessageToServer(MSG_TYPE_STANDBY_HELLO, hello, client);
                    GONetLog.Debug($"[HotStandby] Sent updated StandbyHello to outbound peer {authorityId} with dormant port {dormantServerPort}");
                }
                catch (Exception ex)
                {
                    GONetLog.Warning($"[HotStandby] Failed to resend StandbyHello to outbound peer {authorityId}: {ex.Message}");
                }
            }

            // CRITICAL FIX (Jan 2026): Also send StandbyHello via the main GONet server to all connected clients.
            // After promotion, the demoted host (old server) is connected via the main GONet server,
            // NOT via standby connections. Without this, the demoted host never learns our new dormant port
            // and cannot connect to us for the next failover (causing "2/1 peers" on promoted host).
            if (GONetMain.gonetServer != null && GONetMain.gonetServer.remoteClients != null)
            {
                int mainServerSent = 0;
                int bytesUsed = SerializeStandbyMessage(MSG_TYPE_STANDBY_HELLO, hello);

                foreach (var remoteClient in GONetMain.gonetServer.remoteClients)
                {
                    if (remoteClient == null) continue;

                    ushort clientAuthorityId = remoteClient.ConnectionToClient.OwnerAuthorityId;

                    // Skip if already notified via inbound/outbound standby connections
                    if (notifiedAuthorities.Contains(clientAuthorityId)) continue;

                    // Skip server authority (shouldn't be here, but just in case)
                    if (clientAuthorityId == GONetMain.OwnerAuthorityId_Server) continue;

                    try
                    {
                        GONetMain.gonetServer.SendBytesToClient(
                            remoteClient,
                            sendBuffer,
                            bytesUsed,
                            GONetChannel.DistributedHost_Reliable.Id
                        );
                        notifiedAuthorities.Add(clientAuthorityId);
                        mainServerSent++;
                        GONetLog.Info($"[HotStandby] Sent updated StandbyHello to main-server client {clientAuthorityId} with dormant port {dormantServerPort}");
                    }
                    catch (Exception ex)
                    {
                        GONetLog.Warning($"[HotStandby] Failed to resend StandbyHello to main-server client {clientAuthorityId}: {ex.Message}");
                    }
                }

                if (mainServerSent > 0)
                {
                    GONetLog.Info($"[HotStandby] Sent StandbyHello to {mainServerSent} additional clients via main GONet server");
                }
            }
        }

        /// <summary>
        /// Called when this node is no longer the host.
        /// </summary>
        public void OnNoLongerHost()
        {
            isHost = false;
            serverDormantShadowAuthorityId = 0;
            pendingSessionPromote = null;
            peersAwaitingSessionPromote.Clear();
            GONetLog.Info("[HotStandby] This node is no longer host");
        }

        /// <summary>
        /// Processes pending SessionPromote retries to peers who haven't received the message yet.
        /// Called from Update when we're the host and have pending retries.
        /// </summary>
        private void ProcessSessionPromoteRetries(float elapsedSeconds)
        {
            if (!pendingSessionPromote.HasValue) return;
            if (peersAwaitingSessionPromote.Count == 0)
            {
                // All peers notified successfully
                GONetLog.Info("[HotStandby] All peers have been notified of SessionPromote");
                pendingSessionPromote = null;
                return;
            }

            // Check for timeout
            if (elapsedSeconds - sessionPromoteRetryStartTime > SESSION_PROMOTE_RETRY_TIMEOUT)
            {
                GONetLog.Warning($"[HotStandby] SessionPromote retry timeout after {SESSION_PROMOTE_RETRY_TIMEOUT}s - " +
                               $"{peersAwaitingSessionPromote.Count} peers still not notified: [{string.Join(", ", peersAwaitingSessionPromote)}]");
                pendingSessionPromote = null;
                peersAwaitingSessionPromote.Clear();
                return;
            }

            // Check if it's time for a retry
            if (elapsedSeconds - lastSessionPromoteRetryTime < SESSION_PROMOTE_RETRY_INTERVAL)
            {
                return;
            }
            lastSessionPromoteRetryTime = elapsedSeconds;

            var promoteMsg = pendingSessionPromote.Value;
            var peersToRetry = new List<ushort>(peersAwaitingSessionPromote);
            int successCount = 0;

            GONetLog.Debug($"[HotStandby] Retrying SessionPromote to {peersToRetry.Count} peers: [{string.Join(", ", peersToRetry)}]");

            foreach (ushort peerAuthorityId in peersToRetry)
            {
                bool sent = false;

                // Try dormant server incoming connection
                lock (connectionLock)
                {
                    if (connectionUIDByAuthorityId.TryGetValue(peerAuthorityId, out ulong uid))
                    {
                        var connection = dormantServer?.GetConnectionByUID(uid);
                        if (connection != null)
                        {
                            try
                            {
                                SendStandbyMessage(MSG_TYPE_SESSION_PROMOTE, promoteMsg, connection);
                                GONetLog.Info($"[HotStandby] Retry: Sent SessionPromote to authority {peerAuthorityId} via dormant server");
                                peersAwaitingSessionPromote.Remove(peerAuthorityId);
                                sent = true;
                                successCount++;
                            }
                            catch (Exception ex)
                            {
                                GONetLog.Debug($"[HotStandby] Retry failed (dormant server) to {peerAuthorityId}: {ex.Message}");
                            }
                        }
                    }
                }

                // Try outgoing standby connection if not already sent
                if (!sent)
                {
                    lock (connectionLock)
                    {
                        if (standbyConnections.TryGetValue(peerAuthorityId, out var conn) &&
                            conn.State == StandbyConnectionState.Connected && conn.Client != null)
                        {
                            try
                            {
                                SendStandbyMessageToServer(MSG_TYPE_SESSION_PROMOTE, promoteMsg, conn.Client);
                                GONetLog.Info($"[HotStandby] Retry: Sent SessionPromote to authority {peerAuthorityId} via outgoing connection");
                                peersAwaitingSessionPromote.Remove(peerAuthorityId);
                                successCount++;
                            }
                            catch (Exception ex)
                            {
                                GONetLog.Debug($"[HotStandby] Retry failed (outgoing) to {peerAuthorityId}: {ex.Message}");
                            }
                        }
                    }
                }
            }

            if (successCount > 0)
            {
                GONetLog.Info($"[HotStandby] SessionPromote retry: {successCount} peers notified, {peersAwaitingSessionPromote.Count} still awaiting");
            }

            // Check if all peers are now notified
            if (peersAwaitingSessionPromote.Count == 0)
            {
                GONetLog.Info("[HotStandby] All peers have been notified of SessionPromote after retries");
                pendingSessionPromote = null;
            }
        }

        /// <summary>
        /// Gets the authority ID for a connection UID from the authority map.
        /// Used after promotion to route messages correctly.
        /// </summary>
        public bool TryGetAuthorityIdForConnection(ulong connectionUID, out ushort authorityId)
        {
            lock (connectionLock)
            {
                return authorityMapByConnectionUID.TryGetValue(connectionUID, out authorityId);
            }
        }

        /// <summary>
        /// Gets the connection UID for an authority ID.
        /// </summary>
        public bool TryGetConnectionUIDForAuthority(ushort authorityId, out ulong connectionUID)
        {
            lock (connectionLock)
            {
                return connectionUIDByAuthorityId.TryGetValue(authorityId, out connectionUID);
            }
        }

        #endregion

        #region Status

        /// <summary>
        /// Gets the status of a standby connection to a specific peer.
        /// </summary>
        public bool TryGetConnectionStatus(ushort peerAuthorityId, out StandbyConnectionState state)
        {
            lock (connectionLock)
            {
                if (standbyConnections.TryGetValue(peerAuthorityId, out var conn))
                {
                    state = conn.State;
                    return true;
                }
            }
            state = StandbyConnectionState.NotStarted;
            return false;
        }

        /// <summary>
        /// Gets the current standby connection state and time since last keepalive activity.
        /// </summary>
        public bool TryGetStandbyConnectionActivity(ushort peerAuthorityId, out StandbyConnectionState state, out float secondsSinceActivity)
        {
            secondsSinceActivity = float.PositiveInfinity;

            lock (connectionLock)
            {
                if (!standbyConnections.TryGetValue(peerAuthorityId, out var conn))
                {
                    state = StandbyConnectionState.NotStarted;
                    return false;
                }

                state = conn.State;
                float lastActivityTime = conn.LastKeepaliveTime > conn.LastKeepaliveSequenceAdvancedTime
                    ? conn.LastKeepaliveTime
                    : conn.LastKeepaliveSequenceAdvancedTime;
                if (lastActivityTime > 0f)
                {
                    secondsSinceActivity = (float)GONetMain.Time.ElapsedSeconds - lastActivityTime;
                }

                return true;
            }
        }

        /// <summary>
        /// Gets a summary of all standby connection states.
        /// </summary>
        public Dictionary<ushort, StandbyConnectionState> GetAllConnectionStates()
        {
            var result = new Dictionary<ushort, StandbyConnectionState>();
            lock (connectionLock)
            {
                foreach (var kvp in standbyConnections)
                {
                    result[kvp.Key] = kvp.Value.State;
                }
            }
            return result;
        }

        /// <summary>
        /// Gets the authority map (for debugging).
        /// </summary>
        public Dictionary<ulong, ushort> GetAuthorityMap()
        {
            lock (connectionLock)
            {
                return new Dictionary<ulong, ushort>(authorityMapByConnectionUID);
            }
        }

        #endregion
    }
}
