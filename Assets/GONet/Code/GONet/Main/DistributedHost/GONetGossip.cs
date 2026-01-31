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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GONet.Utils;
using MemoryPack;
using UnityEngine;

namespace GONet.DistributedHost
{
    /// <summary>
    /// Gossip topology mode - determined by transport capabilities.
    /// </summary>
    public enum GossipTopology
    {
        /// <summary>
        /// Star topology: All gossip flows through the current host.
        /// - Nodes send metrics TO the host
        /// - Host aggregates and broadcasts TO all nodes
        /// - Works with ANY transport (NetcodeIO, etc.)
        /// </summary>
        Star,

        /// <summary>
        /// True P2P mesh: Each peer sends directly to all other peers.
        /// - Lower latency (no hub hop)
        /// - Requires P2P-capable transport (Steamworks, etc.)
        /// </summary>
        Mesh
    }

    /// <summary>
    /// Transport-aware gossip protocol for distributed host authority.
    /// Handles metrics sharing between all nodes in the session.
    ///
    /// Topology adapts to transport capabilities:
    /// - Star (default): All gossip flows through host. Works with any transport.
    /// - Mesh: Direct peer-to-peer when transport supports it (e.g., Steamworks).
    ///
    /// Both topologies achieve the same logical result: all nodes know all metrics.
    ///
    /// Features:
    /// - Transport-aware topology selection
    /// - Delta compression for bandwidth optimization
    /// - Adaptive frequency (0.5 Hz normal, 1 Hz during churn)
    /// </summary>
    public class GONetGossipManager
    {
        #region Constants

        /// <summary>
        /// Normal gossip update frequency (0.5 Hz = every 2 seconds).
        /// </summary>
        public const float NORMAL_UPDATE_INTERVAL_SECONDS = 2.0f;

        /// <summary>
        /// Increased gossip frequency during churn (1 Hz = every second).
        /// </summary>
        public const float CHURN_UPDATE_INTERVAL_SECONDS = 1.0f;

        /// <summary>
        /// Time after a node join/leave before returning to normal frequency.
        /// </summary>
        public const float CHURN_DETECTION_WINDOW_SECONDS = 10.0f;

        /// <summary>
        /// Maximum age (in seconds) before a node's metrics are considered stale.
        /// </summary>
        public const float METRICS_STALE_THRESHOLD_SECONDS = 6.0f;

        /// <summary>
        /// Maximum number of nodes to track in the gossip table.
        /// </summary>
        public const int MAX_TRACKED_NODES = 100;

        #endregion

        #region State

        /// <summary>
        /// Current gossip topology based on transport capabilities.
        /// </summary>
        private GossipTopology currentTopology = GossipTopology.Star;

        /// <summary>
        /// Local node's identity.
        /// </summary>
        private GONetNodeIdentity localIdentity;

        /// <summary>
        /// Current metrics for the local node.
        /// </summary>
        private GONetNodeMetrics localMetrics;

        /// <summary>
        /// Local node's connection endpoint (where others can connect to us).
        /// </summary>
        private GONetConnectionEndpoint localEndpoint;

        /// <summary>
        /// Metrics received from other nodes, keyed by authority ID.
        /// </summary>
        private readonly Dictionary<ushort, NodeMetricsEntry> remoteMetrics = new Dictionary<ushort, NodeMetricsEntry>(MAX_TRACKED_NODES);

        /// <summary>
        /// RTT matrix tracking peer-to-peer latencies.
        /// Key: (source authority ID, destination authority ID), Value: RTT in milliseconds.
        /// Built from peer RTT entries in received gossip messages.
        /// </summary>
        private readonly Dictionary<(ushort src, ushort dst), ushort> peerRTTMatrix = new Dictionary<(ushort, ushort), ushort>(MAX_TRACKED_NODES * 10);

        /// <summary>
        /// Whether this node is currently the host (hub in star topology).
        /// </summary>
        private bool isCurrentHost;

        /// <summary>
        /// Time of last gossip broadcast.
        /// </summary>
        private float lastBroadcastTime;

        /// <summary>
        /// Time of last node join/leave (for churn detection).
        /// </summary>
        private float lastChurnTime;

        /// <summary>
        /// Previous metrics snapshot for delta compression.
        /// </summary>
        private GONetNodeMetrics previousLocalMetrics;

        /// <summary>
        /// Whether the gossip system has been initialized.
        /// </summary>
        private bool isInitialized;

        /// <summary>
        /// Reusable buffer for serialization.
        /// </summary>
        private byte[] serializationBuffer = new byte[256];

        #endregion

        #region Events

        /// <summary>
        /// Fired when a new node joins the mesh.
        /// </summary>
        public event Action<ushort, GONetNodeIdentity> OnNodeJoined;

        /// <summary>
        /// Fired when a node leaves the mesh.
        /// </summary>
        public event Action<ushort> OnNodeLeft;

        /// <summary>
        /// Fired when metrics are received from a remote node.
        /// </summary>
        public event Action<ushort, GONetNodeMetrics> OnMetricsReceived;

        /// <summary>
        /// Fired when a node's metrics become stale.
        /// </summary>
        public event Action<ushort> OnMetricsStale;

        #endregion

        #region Singleton

        private static GONetGossipManager instance;
        public static GONetGossipManager Instance => instance ??= new GONetGossipManager();

        private GONetGossipManager() { }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the gossip manager for the local node.
        /// </summary>
        /// <param name="sessionAuthorityId">The local node's authority ID</param>
        /// <param name="joinedAtTicks">Monotonic timestamp when joined</param>
        /// <param name="supportsPeerToPeer">Whether the transport supports P2P (determines topology)</param>
        /// <param name="isHost">Whether this node is the initial host</param>
        public void Initialize(ushort sessionAuthorityId, long joinedAtTicks, bool supportsPeerToPeer = false, bool isHost = false)
        {
            if (isInitialized)
            {
                GONetLog.Warning("[GONetGossip] Already initialized, skipping duplicate initialization");
                return;
            }

            // Select topology based on transport capabilities
            currentTopology = supportsPeerToPeer ? GossipTopology.Mesh : GossipTopology.Star;
            isCurrentHost = isHost;

            localIdentity = GONetNodeIdentityManager.CreateLocalIdentity(sessionAuthorityId, joinedAtTicks);
            localMetrics = GONetNodeMetrics.CreateDefault(sessionAuthorityId);
            previousLocalMetrics = localMetrics;

            remoteMetrics.Clear();
            peerRTTMatrix.Clear();
            lastBroadcastTime = 0f;
            lastChurnTime = 0f;

            GONetMetricsCollector.Initialize();

            isInitialized = true;
            GONetLog.Info($"[GONetGossip] Initialized for node {sessionAuthorityId} with persistent ID {localIdentity.PersistentId:X16}");
        }

        /// <summary>
        /// Shuts down the gossip manager.
        /// </summary>
        public void Shutdown()
        {
            if (!isInitialized) return;

            remoteMetrics.Clear();
            peerRTTMatrix.Clear();
            isInitialized = false;

            GONetLog.Info("[GONetGossip] Shut down");
        }

        #endregion

        #region Update Loop

        /// <summary>
        /// Called each frame to process gossip. Should be called from GONet's main update loop.
        /// </summary>
        /// <param name="elapsedSeconds">Current time from GONetMain.Time.ElapsedSeconds</param>
        /// <param name="connection">Connection to measure RTT from (null for host)</param>
        public void Update(float elapsedSeconds, GONetConnection connection)
        {
            if (!isInitialized || !GONetGlobal.Instance.enableDistributedHostAuthority)
            {
                return;
            }

            // Determine update interval based on churn
            float updateInterval = IsInChurnMode(elapsedSeconds)
                ? CHURN_UPDATE_INTERVAL_SECONDS
                : NORMAL_UPDATE_INTERVAL_SECONDS;

            // Time to broadcast?
            if (elapsedSeconds - lastBroadcastTime >= updateInterval)
            {
                // Collect local metrics
                localMetrics = GONetMetricsCollector.CollectLocalMetrics(connection, localIdentity.SessionAuthorityId);

                // Broadcast to all peers
                BroadcastMetrics();

                lastBroadcastTime = elapsedSeconds;
                previousLocalMetrics = localMetrics;
            }

            // Check for stale nodes
            CheckForStaleNodes(elapsedSeconds);
        }

        /// <summary>
        /// Checks if we're in churn mode (recent node join/leave).
        /// </summary>
        private bool IsInChurnMode(float currentTime)
        {
            return currentTime - lastChurnTime < CHURN_DETECTION_WINDOW_SECONDS;
        }

        /// <summary>
        /// Marks that churn has occurred (node join/leave).
        /// </summary>
        public void NotifyChurn(float currentTime)
        {
            lastChurnTime = currentTime;
            GONetLog.Debug("[GONetGossip] Churn detected, increasing gossip frequency");
        }

        #endregion

        #region Broadcasting

        /// <summary>
        /// Broadcasts local metrics based on current topology.
        /// - Star: Sends to host (or aggregates/forwards if we ARE the host)
        /// - Mesh: Sends directly to all peers
        /// </summary>
        private void BroadcastMetrics()
        {
            // Create gossip message
            var message = new GossipMetricsMessage
            {
                Identity = localIdentity,
                Metrics = localMetrics,
                Endpoint = localEndpoint,
                HostEpoch = GONetMain.HostEpoch,
                IsDelta = HasMetricsChanged(previousLocalMetrics, localMetrics)
            };

            if (currentTopology == GossipTopology.Star)
            {
                BroadcastMetrics_StarTopology(message);
            }
            else // Mesh
            {
                BroadcastMetrics_MeshTopology(message);
            }

            //GONetLog.Debug($"[GONetGossip] Broadcast metrics ({currentTopology}): RTT={localMetrics.RTT_Average_Ms}ms, " +
//                          $"CPU={localMetrics.CPU_Headroom_Percent}%, Uptime={localMetrics.Uptime_Minutes}min");
        }

        /// <summary>
        /// Star topology: Nodes send to host; host aggregates and broadcasts to all.
        /// </summary>
        private void BroadcastMetrics_StarTopology(GossipMetricsMessage message)
        {
            if (isCurrentHost)
            {
                // Host: aggregate all metrics and broadcast to all connected nodes
                // The aggregate message includes ALL known metrics (including host's own)
                var aggregateMessage = CreateAggregateMessage();

                // Send aggregate to all clients via network
                GONetGossipIntegration.SendGossipAggregate(aggregateMessage);

                //GONetLog.Debug($"[GONetGossip] Host broadcasting aggregate with {remoteMetrics.Count + 1} nodes");
            }
            else
            {
                // Non-host node: send metrics TO the host only
                // In Star topology, the host is the server, so we send to server
                GONetGossipIntegration.SendGossipMetrics(message);

                //GONetLog.Debug($"[GONetGossip] Sent metrics to host (authority {GONetMain.CurrentHostIdentity.HostAuthorityId})");
            }
        }

        /// <summary>
        /// Mesh topology: Direct peer-to-peer, send to all connected peers.
        /// </summary>
        private void BroadcastMetrics_MeshTopology(GossipMetricsMessage message)
        {
            // In mesh topology with P2P transport, send to all peers
            // For now, this falls back to server broadcast (true P2P requires transport support)
            GONetGossipIntegration.SendGossipMetrics(message);

            GONetLog.Debug($"[GONetGossip] Mesh broadcast to {remoteMetrics.Count} peers");
        }

        /// <summary>
        /// Creates an aggregate message containing all known node metrics.
        /// Used by the host in Star topology to distribute full state.
        /// </summary>
        private GossipAggregateMessage CreateAggregateMessage()
        {
            var allMetrics = new List<GossipMetricsMessage>(remoteMetrics.Count + 1);

            // Include local (host) metrics first
            allMetrics.Add(new GossipMetricsMessage
            {
                Identity = localIdentity,
                Metrics = localMetrics,
                Endpoint = localEndpoint,
                HostEpoch = GONetMain.HostEpoch,
                IsDelta = false
            });

            // Include all remote node metrics
            foreach (var kvp in remoteMetrics)
            {
                allMetrics.Add(new GossipMetricsMessage
                {
                    Identity = kvp.Value.Identity,
                    Metrics = kvp.Value.Metrics,
                    Endpoint = kvp.Value.Endpoint,
                    HostEpoch = GONetMain.HostEpoch,
                    IsDelta = false
                });
            }

            return new GossipAggregateMessage
            {
                HostIdentity = GONetMain.CurrentHostIdentity,
                NodeMetrics = allMetrics,
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };
        }

        /// <summary>
        /// Notifies the gossip manager that this node's host status has changed.
        /// Called during host migration.
        /// </summary>
        public void OnHostStatusChanged(bool isNowHost)
        {
            isCurrentHost = isNowHost;
            GONetLog.Info($"[GONetGossip] Host status changed: isHost={isNowHost}");
        }

        /// <summary>
        /// Updates the local identity's authority ID after failover promotion.
        /// When a peer becomes the new host, their authority ID changes to 1023 (server authority).
        /// The gossip system must update to broadcast with the new authority ID.
        /// </summary>
        /// <param name="newAuthorityId">The new authority ID (typically 1023 for server)</param>
        public void UpdateLocalAuthorityId(ushort newAuthorityId)
        {
            if (!isInitialized)
            {
                GONetLog.Warning("[GONetGossip] Cannot update authority ID - not initialized");
                return;
            }

            ushort oldAuthorityId = localIdentity.SessionAuthorityId;
            if (oldAuthorityId == newAuthorityId)
            {
                return; // No change needed
            }

            // Update the local identity with the new authority ID
            localIdentity.SessionAuthorityId = newAuthorityId;

            // Also update the local metrics authority ID to match
            localMetrics = new GONetNodeMetrics
            {
                AuthorityId = newAuthorityId,
                RTT_Average_Ms = localMetrics.RTT_Average_Ms,
                RTT_Jitter_Ms = localMetrics.RTT_Jitter_Ms,
                PacketLoss_Percent = localMetrics.PacketLoss_Percent,
                Bandwidth_Send_KBps = localMetrics.Bandwidth_Send_KBps,
                Bandwidth_Recv_KBps = localMetrics.Bandwidth_Recv_KBps,
                CPU_Headroom_Percent = localMetrics.CPU_Headroom_Percent,
                FrameTime_Headroom_Ms = localMetrics.FrameTime_Headroom_Ms,
                BatteryLevel = localMetrics.BatteryLevel,
                Uptime_Seconds = localMetrics.Uptime_Seconds,
                StabilityScore = localMetrics.StabilityScore,
                NATCompatibilityScore = localMetrics.NATCompatibilityScore,
                MonotonicTicks = localMetrics.MonotonicTicks
            };

            GONetLog.Info($"[GONetGossip] Local authority ID updated: {oldAuthorityId} -> {newAuthorityId}");
        }

        /// <summary>
        /// Changes the gossip topology (e.g., if transport capabilities change).
        /// </summary>
        public void SetTopology(GossipTopology topology)
        {
            if (currentTopology != topology)
            {
                GONetLog.Info($"[GONetGossip] Topology changed: {currentTopology} -> {topology}");
                currentTopology = topology;
            }
        }

        /// <summary>
        /// Gets the current gossip topology.
        /// </summary>
        public GossipTopology CurrentTopology => currentTopology;

        /// <summary>
        /// Gets whether this node is currently the host (hub in star topology).
        /// </summary>
        public bool IsCurrentHost => isCurrentHost;

        /// <summary>
        /// Checks if metrics have changed significantly from previous values.
        /// Used for delta compression decision.
        /// </summary>
        private bool HasMetricsChanged(in GONetNodeMetrics previous, in GONetNodeMetrics current)
        {
            // Consider "changed" if any metric differs by more than noise threshold
            return Math.Abs(current.RTT_Average_Ms - previous.RTT_Average_Ms) > 10 ||
                   Math.Abs(current.RTT_Jitter_Ms - previous.RTT_Jitter_Ms) > 5 ||
                   current.PacketLoss_Percent != previous.PacketLoss_Percent ||
                   Math.Abs(current.CPU_Headroom_Percent - previous.CPU_Headroom_Percent) > 5 ||
                   current.StabilityScore != previous.StabilityScore ||
                   current.NATCompatibilityScore != previous.NATCompatibilityScore;
        }

        #endregion

        #region Receiving

        /// <summary>
        /// Processes a received gossip message from a remote node.
        /// Used in Mesh topology or by Host receiving from individual nodes in Star topology.
        /// </summary>
        /// <param name="message">The received gossip message</param>
        /// <param name="receiveTime">Time when message was received</param>
        public void OnGossipMessageReceived(GossipMetricsMessage message, float receiveTime)
        {
            if (!isInitialized) return;

            // Ignore our own messages
            if (message.Identity.SessionAuthorityId == localIdentity.SessionAuthorityId)
            {
                return;
            }

            // Epoch check - ignore stale messages
            // CRITICAL FIX: If we're the HOST, accept gossip from clients even if their epoch is stale.
            // Newly connected clients initialize with epoch 0 and won't know the promoted host's epoch.
            // The host IS the authority for epoch - it should accept direct gossip from its own clients.
            bool isHost = GONetMain.IsServer;
            if (!isHost && GONetMain.IsEpochStale(message.HostEpoch))
            {
                GONetLog.Debug($"[GONetGossip] Ignoring stale message from {message.Identity.SessionAuthorityId} (epoch {message.HostEpoch} < {GONetMain.HostEpoch})");
                return;
            }

            ushort authorityId = message.Identity.SessionAuthorityId;

            // New node?
            bool isNewNode = !remoteMetrics.ContainsKey(authorityId);

            // Update metrics table
            remoteMetrics[authorityId] = new NodeMetricsEntry
            {
                Identity = message.Identity,
                Metrics = message.Metrics,
                Endpoint = message.Endpoint,
                LastUpdateTime = receiveTime
            };

            // Extract peer RTT entries and update RTT matrix
            UpdatePeerRTTMatrix(authorityId, message.Metrics);

            // Fire events
            if (isNewNode)
            {
                GONetLog.Info($"[GONetGossip] New node joined: {authorityId} (PersistentId: {message.Identity.PersistentId:X16})");
                OnNodeJoined?.Invoke(authorityId, message.Identity);
            }

            OnMetricsReceived?.Invoke(authorityId, message.Metrics);
        }

        /// <summary>
        /// Processes an aggregate gossip message from the host.
        /// Used in Star topology by non-host nodes to receive all metrics at once.
        /// </summary>
        /// <param name="message">The received aggregate message</param>
        /// <param name="receiveTime">Time when message was received</param>
        public void OnAggregateMessageReceived(GossipAggregateMessage message, float receiveTime)
        {
            if (!isInitialized) return;

            // Only non-hosts should process aggregate messages
            if (isCurrentHost)
            {
                GONetLog.Debug("[GONetGossip] Host ignoring aggregate message (we are the source)");
                return;
            }

            // Epoch check - ignore stale messages
            if (GONetMain.IsEpochStale(message.HostIdentity.HostEpoch))
            {
                GONetLog.Debug($"[GONetGossip] Ignoring stale aggregate (epoch {message.HostIdentity.HostEpoch} < {GONetMain.HostEpoch})");
                return;
            }

            // Validate host identity matches our known host
            var hostIdentity = message.HostIdentity;
            if (!GONetMain.IsHostIdentityValid(in hostIdentity))
            {
                GONetLog.Warning($"[GONetGossip] Aggregate from unrecognized host {message.HostIdentity.HostAuthorityId}, ignoring");
                return;
            }

            // Process each node's metrics from the aggregate
            if (message.NodeMetrics != null)
            {
                foreach (var nodeMsg in message.NodeMetrics)
                {
                    // Skip our own metrics
                    if (nodeMsg.Identity.SessionAuthorityId == localIdentity.SessionAuthorityId)
                    {
                        continue;
                    }

                    ushort authorityId = nodeMsg.Identity.SessionAuthorityId;
                    bool isNewNode = !remoteMetrics.ContainsKey(authorityId);

                    remoteMetrics[authorityId] = new NodeMetricsEntry
                    {
                        Identity = nodeMsg.Identity,
                        Metrics = nodeMsg.Metrics,
                        Endpoint = nodeMsg.Endpoint,
                        LastUpdateTime = receiveTime
                    };

                    if (isNewNode)
                    {
                        GONetLog.Info($"[GONetGossip] New node from aggregate: {authorityId} (PersistentId: {nodeMsg.Identity.PersistentId:X16})");
                        OnNodeJoined?.Invoke(authorityId, nodeMsg.Identity);
                    }

                    OnMetricsReceived?.Invoke(authorityId, nodeMsg.Metrics);
                }

                //GONetLog.Debug($"[GONetGossip] Processed aggregate with {message.NodeMetrics.Count} nodes");
            }
        }

        #endregion

        #region Stale Detection

        /// <summary>
        /// Checks for and removes stale nodes that haven't sent updates.
        /// </summary>
        private void CheckForStaleNodes(float currentTime)
        {
            var staleNodes = new List<ushort>();

            foreach (var kvp in remoteMetrics)
            {
                if (currentTime - kvp.Value.LastUpdateTime > METRICS_STALE_THRESHOLD_SECONDS)
                {
                    staleNodes.Add(kvp.Key);
                }
            }

            foreach (var authorityId in staleNodes)
            {
                //GONetLog.Warning($"[GONetGossip] Node {authorityId} metrics are stale (no update for {METRICS_STALE_THRESHOLD_SECONDS}s)");
                OnMetricsStale?.Invoke(authorityId);
                // Note: We don't remove here - the host failover system will handle node removal
            }
        }

        /// <summary>
        /// Called when a node disconnects.
        /// </summary>
        public void OnNodeDisconnected(ushort authorityId, float currentTime)
        {
            if (remoteMetrics.Remove(authorityId))
            {
                GONetLog.Info($"[GONetGossip] Node {authorityId} disconnected and removed from gossip table");
                OnNodeLeft?.Invoke(authorityId);
                NotifyChurn(currentTime);
            }
        }

        /// <summary>
        /// Called when this node's local authority ID changes (e.g., during promotion to host).
        /// Clears any stale remote entry for the new authority ID (the old server's entry)
        /// and updates the local identity to the new authority ID.
        /// </summary>
        /// <param name="newAuthorityId">The new authority ID for this node</param>
        /// <param name="currentTime">Current elapsed seconds</param>
        public void OnLocalAuthorityChanged(ushort newAuthorityId, float currentTime)
        {
            if (!isInitialized) return;

            // Remove any stale entry for our new authority ID (e.g., old server's metrics when we promote to 1023)
            if (remoteMetrics.Remove(newAuthorityId))
            {
                GONetLog.Info($"[GONetGossip] Cleared stale remote metrics for authority {newAuthorityId} (now local after promotion)");
            }

            // Update local identity to new authority ID
            ushort oldAuthorityId = localIdentity.SessionAuthorityId;
            localIdentity = GONetNodeIdentityManager.CreateLocalIdentity(newAuthorityId, localIdentity.JoinedAtTicks);
            // NOTE: GONetNodeMetrics is a struct without OwnerAuthorityId - metrics are tied to identity via localIdentity
            localMetrics = GONetNodeMetrics.CreateDefault(newAuthorityId);
            isCurrentHost = true;

            GONetLog.Info($"[GONetGossip] Local authority changed from {oldAuthorityId} to {newAuthorityId} (promoted to host)");
        }

        #endregion

        #region Queries

        /// <summary>
        /// Gets the local node's current identity.
        /// </summary>
        public GONetNodeIdentity LocalIdentity => localIdentity;

        /// <summary>
        /// Gets the local node's current metrics.
        /// </summary>
        public GONetNodeMetrics LocalMetrics => localMetrics;

        /// <summary>
        /// Gets the local node's connection endpoint.
        /// </summary>
        public GONetConnectionEndpoint LocalEndpoint => localEndpoint;

        /// <summary>
        /// Sets the local node's connection endpoint.
        /// Called by the transport layer when it knows where this node can be reached.
        /// </summary>
        public void SetLocalEndpoint(GONetConnectionEndpoint endpoint)
        {
            localEndpoint = endpoint;
            GONetLog.Info($"[GONetGossip] Local endpoint set: {endpoint}");
        }

        /// <summary>
        /// Gets metrics for a specific remote node.
        /// </summary>
        /// <param name="authorityId">The node's authority ID</param>
        /// <param name="metrics">The output metrics if found</param>
        /// <returns>True if the node exists in the gossip table</returns>
        public bool TryGetNodeMetrics(ushort authorityId, out GONetNodeMetrics metrics)
        {
            if (remoteMetrics.TryGetValue(authorityId, out var entry))
            {
                metrics = entry.Metrics;
                return true;
            }

            metrics = default;
            return false;
        }

        /// <summary>
        /// Gets all known node metrics (excluding local node).
        /// </summary>
        /// <returns>Enumerable of (authorityId, metrics) pairs</returns>
        public IEnumerable<(ushort AuthorityId, GONetNodeMetrics Metrics)> GetAllNodeMetrics()
        {
            foreach (var kvp in remoteMetrics)
            {
                yield return (kvp.Key, kvp.Value.Metrics);
            }
        }

        /// <summary>
        /// Gets the number of known remote nodes.
        /// </summary>
        public int RemoteNodeCount => remoteMetrics.Count;

        /// <summary>
        /// Gets all known node identities (including local node).
        /// </summary>
        public IEnumerable<GONetNodeIdentity> GetAllNodeIdentities()
        {
            yield return localIdentity;

            foreach (var kvp in remoteMetrics)
            {
                yield return kvp.Value.Identity;
            }
        }

        /// <summary>
        /// Gets the connection endpoint for a specific node.
        /// </summary>
        /// <param name="authorityId">The node's authority ID</param>
        /// <param name="endpoint">The output endpoint if found</param>
        /// <returns>True if the node exists and has endpoint info</returns>
        public bool TryGetNodeEndpoint(ushort authorityId, out GONetConnectionEndpoint endpoint)
        {
            // Check if it's the local node
            if (authorityId == localIdentity.SessionAuthorityId)
            {
                endpoint = localEndpoint;
                return true;
            }

            if (remoteMetrics.TryGetValue(authorityId, out var entry))
            {
                endpoint = entry.Endpoint;
                return true;
            }

            endpoint = default;
            return false;
        }

        /// <summary>
        /// Gets the persistent ID for a specific node (survives session restarts, used for ownership tracking).
        /// </summary>
        /// <param name="authorityId">The node's session authority ID</param>
        /// <param name="persistentId">The output persistent ID if found</param>
        /// <returns>True if the node exists and has identity info</returns>
        public bool TryGetNodePersistentId(ushort authorityId, out ulong persistentId)
        {
            // Check if it's the local node
            if (authorityId == localIdentity.SessionAuthorityId)
            {
                persistentId = localIdentity.PersistentId;
                return true;
            }

            if (remoteMetrics.TryGetValue(authorityId, out var entry))
            {
                persistentId = entry.Identity.PersistentId;
                return true;
            }

            persistentId = 0;
            return false;
        }

        /// <summary>
        /// Gets the full identity for a specific node.
        /// </summary>
        /// <param name="authorityId">The node's session authority ID</param>
        /// <param name="identity">The output identity if found</param>
        /// <returns>True if the node exists</returns>
        public bool TryGetNodeIdentity(ushort authorityId, out GONetNodeIdentity identity)
        {
            // Check if it's the local node
            if (authorityId == localIdentity.SessionAuthorityId)
            {
                identity = localIdentity;
                return true;
            }

            if (remoteMetrics.TryGetValue(authorityId, out var entry))
            {
                identity = entry.Identity;
                return true;
            }

            identity = default;
            return false;
        }

        /// <summary>
        /// Checks if a node's metrics are stale (older than threshold).
        /// </summary>
        /// <param name="authorityId">The node's authority ID</param>
        /// <param name="currentTime">Current time for comparison</param>
        /// <param name="staleThresholdSeconds">How old metrics must be to be considered stale</param>
        /// <returns>True if metrics are stale or unknown</returns>
        public bool IsMetricsStale(ushort authorityId, float currentTime, float staleThresholdSeconds = 6.0f)
        {
            // Local metrics are never stale
            if (authorityId == localIdentity.SessionAuthorityId)
                return false;

            if (remoteMetrics.TryGetValue(authorityId, out var entry))
            {
                return (currentTime - entry.LastUpdateTime) > staleThresholdSeconds;
            }

            // Unknown node is considered stale
            return true;
        }

        /// <summary>
        /// Checks if a node is known to the gossip system.
        /// </summary>
        /// <param name="authorityId">The node's authority ID</param>
        /// <returns>True if the node is known (local or remote)</returns>
        public bool IsNodeKnown(ushort authorityId)
        {
            return authorityId == localIdentity.SessionAuthorityId ||
                   remoteMetrics.ContainsKey(authorityId);
        }

        /// <summary>
        /// Gets the highest known client authority ID from gossip.
        /// Used during failover promotion to initialize the authority ID counter
        /// so new clients don't get assigned IDs that collide with existing clients.
        /// </summary>
        /// <returns>Highest client authority ID (excludes server 1023), or 0 if no clients known</returns>
        public ushort GetHighestKnownClientAuthorityId()
        {
            ushort highest = 0;

            // Check local authority (if we were a client)
            if (localIdentity.SessionAuthorityId != GONetMain.OwnerAuthorityId_Server &&
                localIdentity.SessionAuthorityId > highest)
            {
                highest = localIdentity.SessionAuthorityId;
            }

            // Check all remote nodes
            foreach (var authorityId in remoteMetrics.Keys)
            {
                // Skip server authority (1023) - we only care about client IDs
                if (authorityId != GONetMain.OwnerAuthorityId_Server && authorityId > highest)
                {
                    highest = authorityId;
                }
            }

            return highest;
        }

        /// <summary>
        /// Gets all known node endpoints (including local node).
        /// </summary>
        /// <returns>Enumerable of (authorityId, endpoint) pairs</returns>
        public IEnumerable<(ushort AuthorityId, GONetConnectionEndpoint Endpoint)> GetAllNodeEndpoints()
        {
            yield return (localIdentity.SessionAuthorityId, localEndpoint);

            foreach (var kvp in remoteMetrics)
            {
                yield return (kvp.Key, kvp.Value.Endpoint);
            }
        }

        #endregion

        #region Peer RTT Matrix

        /// <summary>
        /// Updates the peer RTT matrix with peer RTT entries from a node's metrics.
        /// </summary>
        /// <param name="sourceAuthorityId">Authority ID of the node that reported these RTTs</param>
        /// <param name="metrics">The node's metrics containing peer RTT entries</param>
        private void UpdatePeerRTTMatrix(ushort sourceAuthorityId, GONetNodeMetrics metrics)
        {
            // Extract peer RTT entries from the metrics struct
            AddPeerRTTToMatrix(sourceAuthorityId, metrics.PeerRTT0);
            AddPeerRTTToMatrix(sourceAuthorityId, metrics.PeerRTT1);
            AddPeerRTTToMatrix(sourceAuthorityId, metrics.PeerRTT2);
            AddPeerRTTToMatrix(sourceAuthorityId, metrics.PeerRTT3);
            AddPeerRTTToMatrix(sourceAuthorityId, metrics.PeerRTT4);
        }

        /// <summary>
        /// Adds a single peer RTT entry to the matrix.
        /// </summary>
        private void AddPeerRTTToMatrix(ushort sourceAuthorityId, PeerRTTEntry entry)
        {
            if (!entry.IsValid)
                return;

            // Reconstruct the full authority ID (using 0 for high bits since our authority IDs are typically small)
            ushort destAuthorityId = entry.GetAuthorityId(0);

            // Store both directions (source->dest measurement implies dest->source is similar)
            peerRTTMatrix[(sourceAuthorityId, destAuthorityId)] = entry.RTT_Ms;
        }

        /// <summary>
        /// Gets the average RTT from a candidate to all other peers.
        /// Used for network centrality scoring in host selection.
        /// </summary>
        /// <param name="candidateAuthorityId">Authority ID of the candidate to evaluate</param>
        /// <param name="fallbackRttMs">Fallback RTT to use if no peer data available (typically RTT to current host)</param>
        /// <returns>Average RTT in milliseconds</returns>
        public float GetAverageRTTForCandidate(ushort candidateAuthorityId, float fallbackRttMs = GONetHostScoring.DEFAULT_UNKNOWN_RTT_MS)
        {
            float sum = 0f;
            int count = 0;

            foreach (var kvp in peerRTTMatrix)
            {
                // Include RTTs where candidate is either source or destination
                if (kvp.Key.src == candidateAuthorityId || kvp.Key.dst == candidateAuthorityId)
                {
                    sum += kvp.Value;
                    count++;
                }
            }

            if (count == 0)
            {
                // No peer RTT data - use fallback with penalty
                return fallbackRttMs * GONetHostScoring.UNKNOWN_PEER_RTT_PENALTY;
            }

            float avgKnown = sum / count;

            // If we have less than half the expected peer RTTs, apply partial penalty
            int expectedRttCount = (remoteMetrics.Count > 0) ? (remoteMetrics.Count - 1) * 2 : 0;
            if (expectedRttCount > 0 && count < expectedRttCount / 2)
            {
                float missingRatio = 1f - ((float)count / expectedRttCount);
                float missingPenalty = missingRatio * 50f; // Up to 50ms penalty for missing data
                avgKnown += missingPenalty;
            }

            return avgKnown;
        }

        /// <summary>
        /// Gets the total number of peer RTT measurements in the matrix.
        /// </summary>
        public int PeerRTTMatrixCount => peerRTTMatrix.Count;

        /// <summary>
        /// Gets a copy of the peer RTT matrix for external use.
        /// </summary>
        public Dictionary<(ushort src, ushort dst), ushort> GetPeerRTTMatrix()
        {
            return new Dictionary<(ushort, ushort), ushort>(peerRTTMatrix);
        }

        /// <summary>
        /// Clears the peer RTT matrix. Called on shutdown or session reset.
        /// </summary>
        private void ClearPeerRTTMatrix()
        {
            peerRTTMatrix.Clear();
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes a gossip message to a byte array.
        /// </summary>
        private int SerializeGossipMessage<T>(T message, byte[] buffer)
        {
            // Use GONet's SerializationUtils for efficient serialization
            var bytes = GONet.Utils.SerializationUtils.SerializeToBytes(message, out int bytesUsed, out bool needsReturn);

            if (bytesUsed > buffer.Length)
            {
                Array.Resize(ref serializationBuffer, bytesUsed * 2);
                buffer = serializationBuffer;
            }

            Array.Copy(bytes, buffer, bytesUsed);

            if (needsReturn)
            {
                GONet.Utils.SerializationUtils.ReturnByteArray(bytes);
            }

            return bytesUsed;
        }

        /// <summary>
        /// Deserializes a gossip message from a byte array.
        /// </summary>
        public static GossipMetricsMessage DeserializeGossipMessage(byte[] buffer, int length)
        {
            var span = new ReadOnlySpan<byte>(buffer, 0, length);
            return GONet.Utils.SerializationUtils.DeserializeFromBytes<GossipMetricsMessage>(span);
        }

        /// <summary>
        /// Deserializes an aggregate gossip message from a byte array.
        /// </summary>
        public static GossipAggregateMessage DeserializeAggregateMessage(byte[] buffer, int length)
        {
            var span = new ReadOnlySpan<byte>(buffer, 0, length);
            return GONet.Utils.SerializationUtils.DeserializeFromBytes<GossipAggregateMessage>(span);
        }

        #endregion

        #region Internal Types

        /// <summary>
        /// Entry in the remote metrics table.
        /// </summary>
        private struct NodeMetricsEntry
        {
            public GONetNodeIdentity Identity;
            public GONetNodeMetrics Metrics;
            public GONetConnectionEndpoint Endpoint;
            public float LastUpdateTime;
        }

        #endregion
    }

    /// <summary>
    /// Gossip protocol message containing node metrics.
    ///
    /// <para><b>Network Transmission:</b> This message is NOT sent via the GONet event bus relay system.
    /// Instead, it is serialized with MemoryPack and sent directly as raw bytes over the
    /// <see cref="GONetChannel.DistributedHost_Unreliable"/> channel using
    /// <see cref="GONetGossipIntegration"/>. This approach provides full control over routing
    /// (Star topology: clients→host, host→all) and avoids event bus overhead for high-frequency messages.</para>
    ///
    /// <para><b>ILocalOnlyPublish:</b> This interface is implemented to prevent the event bus relay
    /// in <c>GONetMain.OnAnyEvent_RelayToRemoteConnections_IfAppropriate</c> from attempting to
    /// send this message over the network. The message IS published locally via the event bus
    /// for any local subscribers (logging, debugging, UI), but network transmission is handled
    /// separately by <see cref="GONetGossipIntegration"/>.</para>
    ///
    /// <para><b>Record/Replay:</b> These messages are NOT captured by GONet's persistence system
    /// (record/replay feature) because that system only records <c>SyncEvent_ValueChangeProcessed</c>
    /// events, not <see cref="ITransientEvent"/>. This is intentional - gossip is infrastructure
    /// metadata, not game state that needs replay.</para>
    /// </summary>
    [MemoryPackable]
    public partial class GossipMetricsMessage : ITransientEvent, ILocalOnlyPublish
    {
        /// <summary>
        /// The sender's node identity.
        /// </summary>
        public GONetNodeIdentity Identity { get; set; }

        /// <summary>
        /// The sender's current metrics.
        /// </summary>
        public GONetNodeMetrics Metrics { get; set; }

        /// <summary>
        /// The sender's connection endpoint info.
        /// Used for failover reconnection - other nodes can connect to this endpoint
        /// if this node becomes the new host.
        /// </summary>
        public GONetConnectionEndpoint Endpoint { get; set; }

        /// <summary>
        /// Current host epoch (for stale message rejection).
        /// </summary>
        public uint HostEpoch { get; set; }

        /// <summary>
        /// Whether this is a delta update (metrics unchanged indicator).
        /// </summary>
        public bool IsDelta { get; set; }

        /// <summary>
        /// Timestamp when message was created.
        /// </summary>
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    /// <summary>
    /// Host heartbeat message broadcast by the current host.
    /// Contains host identity and designated vice host.
    ///
    /// <para><b>Network Transmission:</b> This message is NOT sent via the GONet event bus relay system.
    /// Instead, it is serialized with MemoryPack and sent directly as raw bytes over the
    /// <see cref="GONetChannel.DistributedHost_Reliable"/> channel (reliable because heartbeats
    /// are critical for host liveness detection). See <see cref="GONetGossipIntegration"/> for
    /// the send/receive implementation.</para>
    ///
    /// <para><b>No ILocalOnlyPublish:</b> Unlike the other gossip messages, this does not implement
    /// <see cref="ILocalOnlyPublish"/> because it is not currently published to the local event bus.
    /// It is only used for network transmission. If local publishing is added in the future,
    /// <see cref="ILocalOnlyPublish"/> should be added to prevent accidental relay.</para>
    ///
    /// <para><b>Record/Replay:</b> Not captured by the persistence system - see
    /// <see cref="GossipMetricsMessage"/> documentation for rationale.</para>
    /// </summary>
    [MemoryPackable]
    public partial class HostHeartbeatMessage : ITransientEvent
    {
        /// <summary>
        /// Current host identity (includes epoch and vice host designation).
        /// </summary>
        public HostIdentity HostIdentity { get; set; }

        /// <summary>
        /// Host's current metrics (for comparison).
        /// </summary>
        public GONetNodeMetrics HostMetrics { get; set; }

        /// <summary>
        /// Vice host's latest known score.
        /// </summary>
        public float ViceHostScore { get; set; }

        /// <summary>
        /// The ORIGINAL authority ID of the host peer (before they became 1023 via self-promotion).
        /// This is critical for hot standby lookup when a peer promoted to host - receivers have
        /// standby connections keyed by the peer's original authority ID, not their post-promotion
        /// server authority ID (1023).
        /// Value is 0 if the host was the original server (never promoted from a client).
        /// </summary>
        public ushort HostPeerOriginalAuthorityId { get; set; }

        /// <summary>
        /// Authoritative host time (ElapsedTicks) at heartbeat send time.
        /// Used to preserve time continuity during failover.
        /// </summary>
        public long HostElapsedTicks { get; set; }

        /// <summary>
        /// Timestamp when message was created.
        /// </summary>
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    /// <summary>
    /// Aggregate gossip message used by host in Star topology.
    /// Contains metrics from ALL known nodes, broadcast to all connected peers.
    ///
    /// <para><b>Star Topology Role:</b> In Star topology, the host receives individual
    /// <see cref="GossipMetricsMessage"/> from each client, aggregates them into this message,
    /// and broadcasts it to all clients. This allows every node to learn about all other nodes
    /// with only O(N) messages instead of O(N²) in a full mesh.</para>
    ///
    /// <para><b>Network Transmission:</b> This message is NOT sent via the GONet event bus relay system.
    /// Instead, it is serialized with MemoryPack and sent directly as raw bytes over the
    /// <see cref="GONetChannel.DistributedHost_Unreliable"/> channel using
    /// <see cref="GONetGossipIntegration.SendGossipAggregate"/>. Only the host sends this message.</para>
    ///
    /// <para><b>ILocalOnlyPublish:</b> This interface is implemented to prevent the event bus relay
    /// in <c>GONetMain.OnAnyEvent_RelayToRemoteConnections_IfAppropriate</c> from attempting to
    /// send this message over the network. The message IS published locally via the event bus
    /// for any local subscribers (logging, debugging, UI), but network transmission is handled
    /// separately by <see cref="GONetGossipIntegration"/>.</para>
    ///
    /// <para><b>Record/Replay:</b> Not captured by the persistence system - see
    /// <see cref="GossipMetricsMessage"/> documentation for rationale.</para>
    /// </summary>
    [MemoryPackable]
    public partial class GossipAggregateMessage : ITransientEvent, ILocalOnlyPublish
    {
        /// <summary>
        /// Current host identity (for epoch validation).
        /// </summary>
        public HostIdentity HostIdentity { get; set; }

        /// <summary>
        /// Metrics from all known nodes (including host).
        /// </summary>
        public List<GossipMetricsMessage> NodeMetrics { get; set; }

        /// <summary>
        /// Timestamp when message was created.
        /// </summary>
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }
}
