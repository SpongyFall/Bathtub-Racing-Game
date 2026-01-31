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

using GONetChannelId = System.Byte;

namespace GONet.DistributedHost
{
    /// <summary>
    /// Integrates the gossip system with GONet's network infrastructure.
    /// Handles initialization, update loop integration, and message send/receive.
    /// </summary>
    public static class GONetGossipIntegration
    {
        #region Message Type IDs

        /// <summary>
        /// Message type ID for GossipMetricsMessage.
        /// Must be unique across all GONet message types.
        /// </summary>
        private const byte MSG_TYPE_GOSSIP_METRICS = 1;

        /// <summary>
        /// Message type ID for GossipAggregateMessage.
        /// </summary>
        private const byte MSG_TYPE_GOSSIP_AGGREGATE = 2;

        /// <summary>
        /// Message type ID for HostHeartbeatMessage.
        /// </summary>
        private const byte MSG_TYPE_HOST_HEARTBEAT = 3;

        // Phase 2: Host Migration message types
        private const byte MSG_TYPE_HANDOFF_PREPARE = 10;
        private const byte MSG_TYPE_HANDOFF_PREPARE_ACK = 11;
        private const byte MSG_TYPE_HANDOFF_DELTA = 12;
        private const byte MSG_TYPE_HANDOFF_COMMIT = 13;
        private const byte MSG_TYPE_HANDOFF_COMPLETE = 14;
        private const byte MSG_TYPE_HANDOFF_ABORT = 15;
        private const byte MSG_TYPE_EMERGENCY_PROMOTION = 16;

	        // Phase 2: Vice Host sync message types
	        private const byte MSG_TYPE_VICE_HOST_FULL_SYNC = 20;
	        private const byte MSG_TYPE_VICE_HOST_DELTA_SYNC = 21;
	        private const byte MSG_TYPE_VICE_HOST_SYNC_ACK = 22;

	        // Hot Standby message types (Phase 2.10)
	        private const byte MSG_TYPE_STANDBY_HELLO = GONetHotStandbyManager.MSG_TYPE_STANDBY_HELLO;
	        private const byte MSG_TYPE_STANDBY_HELLO_ACK = GONetHotStandbyManager.MSG_TYPE_STANDBY_HELLO_ACK;
	        private const byte MSG_TYPE_STANDBY_KEEPALIVE = GONetHotStandbyManager.MSG_TYPE_STANDBY_KEEPALIVE;
	        private const byte MSG_TYPE_SESSION_PROMOTE = GONetHotStandbyManager.MSG_TYPE_SESSION_PROMOTE;
	        private const byte MSG_TYPE_MESH_HEARTBEAT = GONetHotStandbyManager.MSG_TYPE_MESH_HEARTBEAT;
	        private const byte MSG_TYPE_RELIABILITY_RESET_REQUEST = GONetHotStandbyManager.MSG_TYPE_RELIABILITY_RESET_REQUEST;
	        private const byte MSG_TYPE_RELIABILITY_RESET_COMMIT = GONetHotStandbyManager.MSG_TYPE_RELIABILITY_RESET_COMMIT;
	        private const byte MSG_TYPE_RELIABILITY_RESET_COMPLETE = GONetHotStandbyManager.MSG_TYPE_RELIABILITY_RESET_COMPLETE;

        // Mesh topology sync message type (for cascading failover fix)
        private const byte MSG_TYPE_MESH_TOPOLOGY_SYNC = 35;

        #endregion

        #region State

        private static bool isInitialized = false;
        private static bool isSubscribedToEvents = false;

        /// <summary>
        /// Reusable buffer for serialization to avoid allocations.
        /// </summary>
        private static byte[] sendBuffer = new byte[512];

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the gossip integration.
        /// Called from GONetMain when distributed host authority is enabled.
        /// </summary>
        /// <param name="isHost">Whether this node is the initial host</param>
        /// <param name="transport">The main transport for this session (needed for Steamworks virtual ports in hot standby)</param>
        public static void Initialize(bool isHost, IGONetTransport transport)
        {
            if (isInitialized)
            {
                GONetLog.Warning("[GONetGossipIntegration] Already initialized");
                return;
            }

            if (!GONetGlobal.Instance.enableDistributedHostAuthority)
            {
                GONetLog.Debug("[GONetGossipIntegration] Distributed host authority is disabled, skipping initialization");
                return;
            }

            // Subscribe to custom channel payload event for receiving gossip messages
            if (!isSubscribedToEvents)
            {
                GONetMain.OnCustomChannelPayloadReceived += OnCustomChannelPayloadReceived;
                isSubscribedToEvents = true;
            }

            // Initialize the gossip manager
            // For now, assume Star topology (transport doesn't support P2P)
            // TODO: Query transport for SupportsPeerToPeer capability
            bool supportsPeerToPeer = false;

            GONetGossipManager.Instance.Initialize(
                sessionAuthorityId: GONetMain.MyAuthorityId,
                joinedAtTicks: GONetMain.Time.ElapsedTicks,
                supportsPeerToPeer: supportsPeerToPeer,
                isHost: isHost
            );

            // Initialize host identity
            if (isHost)
            {
                // Host: We are the authority
                GONetMain.InitializeHostIdentity(GONetMain.MyAuthorityId);
            }
            else
            {
                // Client: In Phase 1, server (authority 1) is always the host
                GONetMain.InitializeHostIdentity(GONetMain.OwnerAuthorityId_Server);
            }

            // Subscribe to gossip events for logging/debugging
            GONetGossipManager.Instance.OnNodeJoined += OnNodeJoined;
            GONetGossipManager.Instance.OnNodeLeft += OnNodeLeft;
            GONetGossipManager.Instance.OnMetricsReceived += OnMetricsReceived;
            GONetGossipManager.Instance.OnMetricsStale += OnMetricsStale;

            // Phase 2: Initialize host migration managers
            GONetViceHostManager.Instance.Initialize(isHost);
            GONetHostHandoffManager.Instance.Initialize();
            GONetHostFailoverManager.Instance.Initialize(isHost);
            GONetHostMigrationDebug.Initialize();

            // Initialize connectivity probe system with dormant server
            // Port is server port + 1 for dormant server (to avoid conflict with main server)
            ushort dormantPort = (ushort)(GONetGlobal.ServerPort_Actual + 1);
            GONetConnectivityProbeManager.Instance.Initialize(dormantPort);

            // Phase 2.10: Initialize hot standby system for instant failover
            // Uses port after connectivity probe (port + 2 for OS ports, or virtual port 1 for Steam)
            // CRITICAL: Pass transport so Steamworks hot standby can use virtual ports (otherwise disabled)
            ushort hotStandbyPort = (ushort)(dormantPort + 1);
            GONetHotStandbyManager.Instance.Initialize(hotStandbyPort, isHost, transport);

            isInitialized = true;
            GONetLog.Info($"[GONetGossipIntegration] Initialized (isHost={isHost}, topology={GONetGossipManager.Instance.CurrentTopology})");
        }

        /// <summary>
        /// Shuts down the gossip integration.
        /// </summary>
        public static void Shutdown()
        {
            if (!isInitialized) return;

            GONetGossipManager.Instance.OnNodeJoined -= OnNodeJoined;
            GONetGossipManager.Instance.OnNodeLeft -= OnNodeLeft;
            GONetGossipManager.Instance.OnMetricsReceived -= OnMetricsReceived;
            GONetGossipManager.Instance.OnMetricsStale -= OnMetricsStale;

            // Shutdown hot standby system
            GONetHotStandbyManager.Instance.Shutdown();

            // Shutdown connectivity probe
            GONetConnectivityProbeManager.Instance.Shutdown();

            // Phase 2: Shutdown host migration managers
            GONetHostMigrationDebug.Shutdown();
            GONetHostFailoverManager.Instance.Shutdown();
            GONetHostHandoffManager.Instance.Shutdown();
            GONetViceHostManager.Instance.Shutdown();

            GONetGossipManager.Instance.Shutdown();

            if (isSubscribedToEvents)
            {
                GONetMain.OnCustomChannelPayloadReceived -= OnCustomChannelPayloadReceived;
                isSubscribedToEvents = false;
            }

            isInitialized = false;
            GONetLog.Info("[GONetGossipIntegration] Shut down");
        }

        #endregion

        #region Update Loop

        /// <summary>
        /// Called from GONetMain.Update() to update the gossip system.
        /// </summary>
        public static void Update()
        {
            if (!isInitialized || !GONetGlobal.Instance.enableDistributedHostAuthority)
            {
                return;
            }

            // INSTRUMENTATION: Log periodically to confirm Update is running
            float elapsed = (float)GONetMain.Time.ElapsedSeconds;
            if ((int)elapsed % 10 == 0 && (int)(elapsed * 10) % 10 == 0)
            {
                //GONetLog.Debug($"[GossipIntegration-TRACE] Update running (elapsed={elapsed:F1}s)");
            }

            // Get the connection for RTT measurement
            // For clients: connection to server
            // For server/host: null (we are the authority)
            GONetConnection connection = null;
            if (GONetMain.IsClient && GONetMain.GONetClient != null)
            {
                connection = GONetMain.GONetClient.connectionToServer;
            }

            float elapsedSeconds = (float)GONetMain.Time.ElapsedSeconds;

            try
            {
                GONetGossipManager.Instance.Update(
                    elapsedSeconds: elapsedSeconds,
                    connection: connection
                );
            }
            catch (System.Exception e)
            {
                GONetLog.Error($"[GossipIntegration] Exception in GossipManager.Update: {e.Message}\n{e.StackTrace}");
            }

            // Phase 2: Update host migration managers
            try
            {
                GONetViceHostManager.Instance.Update(elapsedSeconds);
            }
            catch (System.Exception e)
            {
                GONetLog.Error($"[GossipIntegration] Exception in ViceHostManager.Update: {e.Message}\n{e.StackTrace}");
            }

            try
            {
                GONetHostHandoffManager.Instance.Update(elapsedSeconds);
            }
            catch (System.Exception e)
            {
                GONetLog.Error($"[GossipIntegration] Exception in HostHandoffManager.Update: {e.Message}\n{e.StackTrace}");
            }

            // INSTRUMENTATION: Log right before failover update
            // Disabled verbose logging - re-enable for debugging by uncommenting:
            // float timeSinceHB = GONetHostFailoverManager.Instance.TimeSinceLastHeartbeat;
            // if (timeSinceHB > 0.5f)
            // {
            //     GONetLog.Warning($"[GossipIntegration-TRACE] About to call Failover.Update (timeSinceHB={timeSinceHB:F2}s, elapsed={elapsedSeconds:F2}s)");
            // }

            try
            {
                GONetHostFailoverManager.Instance.Update(elapsedSeconds);
                GONetHostFailoverManager.Instance.UpdateReconciliation();
            }
            catch (System.Exception e)
            {
                GONetLog.Error($"[GossipIntegration] Exception in HostFailoverManager.Update: {e.Message}\n{e.StackTrace}");
            }
            GONetHostMigrationDebug.Update();

            // Update connectivity probe (dormant server + peer verification)
            GONetConnectivityProbeManager.Instance.Update(elapsedSeconds);

            // Phase 2.10: Update hot standby manager (dormant GONet server + peer connections)
            GONetHotStandbyManager.Instance.Update(elapsedSeconds);
        }

        #endregion

        #region Network Send

        /// <summary>
        /// Sends a gossip metrics message over the network.
        /// Called by GONetGossipManager when broadcasting metrics.
        /// </summary>
        public static void SendGossipMetrics(GossipMetricsMessage message, ushort? targetAuthorityId = null)
        {
            if (!isInitialized) return;

            // Serialize the message
            int bytesUsed = SerializeGossipMessage(MSG_TYPE_GOSSIP_METRICS, message);

            if (GONetMain.IsServer)
            {
                if (targetAuthorityId.HasValue)
                {
                    // Send to specific client
                    SendToClient(targetAuthorityId.Value, bytesUsed);
                }
                else
                {
                    // Broadcast to all clients
                    SendToAllClients(bytesUsed);
                }
            }
            else if (GONetMain.IsClient)
            {
                // Client sends to server (which is the host in Star topology)
                SendToServer(bytesUsed);
            }
        }

        /// <summary>
        /// Sends an aggregate gossip message over the network.
        /// Only called by the host in Star topology.
        /// </summary>
        public static void SendGossipAggregate(GossipAggregateMessage message)
        {
            if (!isInitialized || !GONetMain.IsServer) return;

            // Serialize the message
            int bytesUsed = SerializeGossipMessage(MSG_TYPE_GOSSIP_AGGREGATE, message);

            // Broadcast to all clients
            SendToAllClients(bytesUsed);
        }

        /// <summary>
        /// Heartbeat counter for throttling during congestion.
        /// Incremented each time SendHostHeartbeat is called (at 8Hz).
        /// Used to reduce heartbeat rate for congested clients from 8Hz to 1Hz.
        /// </summary>
        private static int heartbeatCounter = 0;

        /// <summary>
        /// Sends a host heartbeat message over the network.
        /// Only called by the host.
        ///
        /// CONGESTION-AWARE (Dec 2025 - PRODUCTION REFINEMENT):
        /// When a client is under backpressure, sending reliable heartbeats at 8Hz
        /// adds to the reliable queue faster than it can drain, preventing recovery.
        ///
        /// CRITICAL: Keep heartbeats RELIABLE, but THROTTLE rate during congestion.
        /// - Normal: 8Hz (every call)
        /// - Congestion: 1Hz (every 8th call)
        ///
        /// WHY NOT UNRELIABLE?
        /// During real packet loss (bad WiFi), unreliable packets are dropped first.
        /// Heartbeats keep the connection alive. Dropping them causes disconnect.
        /// Throttling to 1Hz still adds only 1 msg/sec while keeping connection alive.
        /// </summary>
        public static void SendHostHeartbeat(HostHeartbeatMessage message)
        {
            if (!isInitialized || !GONetMain.IsServer) return;
            if (GONetMain.gonetServer == null) return;

            heartbeatCounter++;

            // Serialize the message once (reused for all clients)
            int bytesUsed = SerializeGossipMessage(MSG_TYPE_HOST_HEARTBEAT, message);

            // Send to each client individually, throttling rate for congested clients
            int normalSent = 0;
            int throttledSent = 0;
            int throttledSkipped = 0;

            foreach (var remoteClient in GONetMain.gonetServer.remoteClients)
            {
                if (remoteClient == null) continue;

                ushort clientAuthorityId = remoteClient.ConnectionToClient.OwnerAuthorityId;

                // Check if this client is under backpressure
                bool isUnderBackpressure = GONetMain.IsClientUnderBackpressure(clientAuthorityId, out int queueDepth);

                if (isUnderBackpressure)
                {
                    // THROTTLED: Only send every 8th heartbeat (1Hz instead of 8Hz)
                    // This reduces reliable queue growth from 8/sec to 1/sec
                    // while still keeping the connection alive
                    if (heartbeatCounter % 8 != 0)
                    {
                        throttledSkipped++;
                        continue; // Skip this heartbeat for congested client
                    }
                    throttledSent++;
                }
                else
                {
                    normalSent++;
                }

                // ALWAYS send RELIABLE - heartbeats keep connection alive
                GONetMain.gonetServer.SendBytesToClient(
                    remoteClient,
                    sendBuffer,
                    bytesUsed,
                    GONetChannel.DistributedHost_Reliable.Id
                );
            }

            // Log periodically when throttling (avoid spam)
            if (throttledSent > 0 || throttledSkipped > 0)
            {
                GONetLog.Debug($"[Heartbeat-SEND] Throttled: {normalSent} normal, {throttledSent} sent (1Hz), {throttledSkipped} skipped");
            }
        }

        private static int SerializeGossipMessage<T>(byte messageType, T message)
        {
            // Format: [1 byte type][N bytes MemoryPack serialized message]
            sendBuffer[0] = messageType;

            var bytes = SerializationUtils.SerializeToBytes(message, out int bytesUsed, out bool needsReturn);

            // Ensure buffer is large enough
            if (bytesUsed + 1 > sendBuffer.Length)
            {
                Array.Resize(ref sendBuffer, (bytesUsed + 1) * 2);
                sendBuffer[0] = messageType; // Re-set after resize
            }

            Array.Copy(bytes, 0, sendBuffer, 1, bytesUsed);

            if (needsReturn)
            {
                SerializationUtils.ReturnByteArray(bytes);
            }

            return bytesUsed + 1;
        }

        private static void SendToAllClients(int bytesUsed)
        {
            if (GONetMain.gonetServer != null)
            {
                GONetMain.gonetServer.SendBytesToAllClients(
                    sendBuffer,
                    bytesUsed,
                    GONetChannel.DistributedHost_Unreliable.Id
                );
            }
        }

        private static void SendToClient(ushort authorityId, int bytesUsed)
        {
            if (GONetMain.gonetServer != null)
            {
                // Find the remote client by authority ID
                var remoteClient = GONetMain.gonetServer.GetRemoteClientByAuthorityId(authorityId);
                if (remoteClient != null)
                {
                    GONetMain.gonetServer.SendBytesToClient(
                        remoteClient,
                        sendBuffer,
                        bytesUsed,
                        GONetChannel.DistributedHost_Unreliable.Id
                    );
                }
            }
        }

        private static void SendToServer(int bytesUsed)
        {
            SendToServerOnChannel(bytesUsed, GONetChannel.DistributedHost_Unreliable.Id);
        }

        /// <summary>
        /// Safely sends data to server on specified channel.
        /// CRITICAL: Checks IsConnectedToServer before attempting to send.
        /// Without this check, sending on a dead connection causes InvalidOperationException spam
        /// from NetcodeIO.NET.Client.Send (14,000+ errors during failover).
        /// </summary>
        private static bool SendToServerOnChannel(int bytesUsed, byte channelId)
        {
            if (GONetMain.GONetClient != null &&
                GONetMain.GONetClient.IsConnectedToServer &&
                GONetMain.GONetClient.connectionToServer != null)
            {
                // SLOT RESERVATION (December 2025): Use channel-based priority
                var priority = GONetChannel.GetMessagePriority(channelId);
                GONetMain.GONetClient.connectionToServer.SendMessageOverChannel(
                    sendBuffer,
                    bytesUsed,
                    channelId,
                    priority
                );
                return true;
            }
            return false;
        }

        #endregion

        #region Unified Broadcast API

        /// <summary>
        /// UNIFIED API: Broadcasts a message to all peers in the distributed host system.
        ///
        /// This is the PRIMARY API for sending distributed host messages. Callers should NOT
        /// need to know whether the underlying topology is Star or Mesh - this method handles
        /// the routing automatically based on transport capabilities.
        ///
        /// Behavior:
        /// - Star topology (default): Host receives from clients, aggregates, broadcasts back
        /// - Mesh topology (P2P transport): Direct peer-to-peer (when transport supports it)
        ///
        /// The API surface is IDENTICAL regardless of topology. The transport dictates the
        /// implementation details.
        /// </summary>
        /// <typeparam name="T">Message type (must be serializable with MemoryPack)</typeparam>
        /// <param name="message">The message to broadcast</param>
        /// <param name="messageType">Message type identifier for deserialization</param>
        /// <param name="reliable">Use reliable channel (default: false for metrics, true for handoff)</param>
        public static void BroadcastToAllPeers<T>(T message, byte messageType, bool reliable = false) where T : class
        {
            if (!isInitialized) return;

            int bytesUsed = SerializeGossipMessage(messageType, message);
            byte channelId = reliable ? GONetChannel.DistributedHost_Reliable.Id : GONetChannel.DistributedHost_Unreliable.Id;

            // The topology determines HOW we broadcast, but the API is the same
            if (GONetGossipManager.Instance.CurrentTopology == GossipTopology.Star)
            {
                // Star: Route through host (server)
                if (GONetMain.IsServer)
                {
                    // We ARE the host - broadcast to all clients
                    GONetMain.gonetServer?.SendBytesToAllClients(sendBuffer, bytesUsed, channelId);
                }
                else
                {
                    // We're a client - send to host, it will aggregate and rebroadcast
                    SendToServerOnChannel(bytesUsed, channelId);
                }
            }
            else // Mesh
            {
                // Mesh: Direct P2P (when transport supports it)
                // For now, falls back to Star behavior until P2P transport is implemented
                // The key point: CALLER DOESN'T NEED TO KNOW THIS
                if (GONetMain.IsServer)
                {
                    GONetMain.gonetServer?.SendBytesToAllClients(sendBuffer, bytesUsed, channelId);
                }
                else
                {
                    SendToServerOnChannel(bytesUsed, channelId);
                }
            }
        }

        /// <summary>
        /// UNIFIED API: Sends a message to a specific peer by authority ID.
        ///
        /// Works regardless of topology:
        /// - Star: Routes through host if sender is not host
        /// - Mesh: Direct P2P to target (when transport supports it)
        /// </summary>
        public static void SendToPeer<T>(T message, byte messageType, ushort targetAuthorityId, bool reliable = false) where T : class
        {
            if (!isInitialized) return;

            int bytesUsed = SerializeGossipMessage(messageType, message);
            byte channelId = reliable ? GONetChannel.DistributedHost_Reliable.Id : GONetChannel.DistributedHost_Unreliable.Id;

            if (GONetMain.IsServer)
            {
                // Host can send directly to any client
                // DEFENSIVE FIX (Dec 2025): Use TryGet to avoid KeyNotFoundException
                // Bug: After handoff, ViceHostManager tried to send to old viceHostAuthorityId
                // which was no longer in the client dictionary.
                if (GONetMain.gonetServer?.TryGetRemoteClientByAuthorityId(targetAuthorityId, out GONetRemoteClient remoteClient) == true)
                {
                    GONetMain.gonetServer.SendBytesToClient(remoteClient, sendBuffer, bytesUsed, channelId);
                }
            }
            else
            {
                // Client sending to another peer
                if (targetAuthorityId == GONetMain.CurrentHostIdentity.HostAuthorityId)
                {
                    // Target is host - send directly
                    SendToServerOnChannel(bytesUsed, channelId);
                }
                else
                {
                    // Target is another client - in Star, must route through host
                    // In true Mesh with P2P transport, would send directly
                    // For now: send to host with routing info (TODO: implement routed messages)
                    GONetLog.Warning($"[GossipIntegration] Client-to-client messaging not yet implemented (target: {targetAuthorityId})");
                }
            }
        }

        /// <summary>
        /// UNIFIED API: Sends a message to the current host.
        ///
        /// Works regardless of topology - always sends to whoever is the current host.
        /// </summary>
        public static void SendToHost<T>(T message, byte messageType, bool reliable = false) where T : class
        {
            if (!isInitialized) return;
            if (GONetMain.IsServer) return; // We ARE the host, don't send to self

            int bytesUsed = SerializeGossipMessage(messageType, message);
            byte channelId = reliable ? GONetChannel.DistributedHost_Reliable.Id : GONetChannel.DistributedHost_Unreliable.Id;

            SendToServerOnChannel(bytesUsed, channelId);
        }

        #endregion

        #region Network Receive

        /// <summary>
        /// Handles incoming custom channel payloads.
        /// Called by GONet when a message arrives on a non-core channel.
        /// </summary>
        private static void OnCustomChannelPayloadReceived(
            GONetChannelId channelId,
            GONetConnection relatedConnection,
            byte[] messageBytes,
            int bytesUsedCount)
        {
            // Only process distributed host channels
            if (channelId != GONetChannel.DistributedHost_Unreliable.Id &&
                channelId != GONetChannel.DistributedHost_Reliable.Id)
            {
                return;
            }

            if (bytesUsedCount < 2)
            {
                GONetLog.Warning("[GONetGossipIntegration] Received malformed gossip message (too short)");
                return;
            }

            // First byte is message type
            byte messageType = messageBytes[0];

            // CRITICAL DEBUG: Log all incoming DistributedHost messages
//            GONetLog.Warning($"[GossipIntegration-RECV] Received DistributedHost message: type={messageType}, " +
//                $"channel={channelId}, bytes={bytesUsedCount}, connection={relatedConnection?.GetType().Name ?? "null"}, " +
//                $"connectionUID={(relatedConnection as GONetConnection_ServerToClient)?.InitiatingClientConnectionUID ?? 0}, " +
//                $"firstBytes={System.BitConverter.ToString(messageBytes, 0, Math.Min(8, bytesUsedCount))}");
            var payloadSpan = new ReadOnlySpan<byte>(messageBytes, 1, bytesUsedCount - 1);
            float receiveTime = (float)GONetMain.Time.ElapsedSeconds;

            // Debug: Log all distributed host channel messages (especially heartbeats)
            if (messageType == MSG_TYPE_HOST_HEARTBEAT)
            {
                //GONetLog.Debug($"[Heartbeat-RECV] Received heartbeat on channel {channelId}, size={bytesUsedCount}, from authority {relatedConnection?.OwnerAuthorityId}, time={receiveTime:F3}");
            }

            try
            {
                switch (messageType)
                {
                    case MSG_TYPE_GOSSIP_METRICS:
                        var metricsMessage = SerializationUtils.DeserializeFromBytes<GossipMetricsMessage>(payloadSpan);
                        GONetGossipManager.Instance.OnGossipMessageReceived(metricsMessage, receiveTime);
                        break;

                    case MSG_TYPE_GOSSIP_AGGREGATE:
                        var aggregateMessage = SerializationUtils.DeserializeFromBytes<GossipAggregateMessage>(payloadSpan);
                        GONetGossipManager.Instance.OnAggregateMessageReceived(aggregateMessage, receiveTime);
                        break;

                    case MSG_TYPE_HOST_HEARTBEAT:
                        var heartbeatMessage = SerializationUtils.DeserializeFromBytes<HostHeartbeatMessage>(payloadSpan);
                        OnHostHeartbeatReceived(heartbeatMessage, relatedConnection);
                        break;

                    // Phase 2: Handoff messages
                    case MSG_TYPE_HANDOFF_PREPARE:
                        var prepareMsg = SerializationUtils.DeserializeFromBytes<HostHandoffPrepareMessage>(payloadSpan);
                        GONetHostHandoffManager.Instance.OnHandoffPrepare(prepareMsg);
                        break;

                    case MSG_TYPE_HANDOFF_PREPARE_ACK:
                        var prepareAckMsg = SerializationUtils.DeserializeFromBytes<ViceHostPrepareAckMessage>(payloadSpan);
                        GONetHostHandoffManager.Instance.OnViceHostPrepareAck(prepareAckMsg);
                        break;

                    case MSG_TYPE_HANDOFF_DELTA:
                        var deltaMsg = SerializationUtils.DeserializeFromBytes<HostHandoffDeltaMessage>(payloadSpan);
                        GONetHostHandoffManager.Instance.OnHandoffDelta(deltaMsg);
                        break;

                    case MSG_TYPE_HANDOFF_COMMIT:
                        var commitMsg = SerializationUtils.DeserializeFromBytes<HostHandoffCommitMessage>(payloadSpan);
                        GONetHostHandoffManager.Instance.OnHandoffCommit(commitMsg);
                        break;

                    case MSG_TYPE_HANDOFF_COMPLETE:
                        var completeMsg = SerializationUtils.DeserializeFromBytes<NewHostCompleteMessage>(payloadSpan);
                        GONetHostHandoffManager.Instance.OnNewHostComplete(completeMsg);
                        break;

                    case MSG_TYPE_HANDOFF_ABORT:
                        var abortMsg = SerializationUtils.DeserializeFromBytes<HostHandoffAbortMessage>(payloadSpan);
                        GONetHostHandoffManager.Instance.OnHandoffAbort(abortMsg);
                        break;

                    case MSG_TYPE_EMERGENCY_PROMOTION:
                        var emergencyMsg = SerializationUtils.DeserializeFromBytes<EmergencyHostPromotionMessage>(payloadSpan);
                        GONetHostFailoverManager.Instance.OnEmergencyPromotionReceived(emergencyMsg);
                        break;

                    // Phase 2: Vice Host sync messages
                    case MSG_TYPE_VICE_HOST_FULL_SYNC:
                        var fullSyncMsg = SerializationUtils.DeserializeFromBytes<ViceHostFullSyncMessage>(payloadSpan);
                        GONetViceHostManager.Instance.OnFullSyncReceived(fullSyncMsg);
                        break;

                    case MSG_TYPE_VICE_HOST_DELTA_SYNC:
                        var deltaSyncMsg = SerializationUtils.DeserializeFromBytes<ViceHostDeltaSyncMessage>(payloadSpan);
                        GONetViceHostManager.Instance.OnDeltaSyncReceived(deltaSyncMsg);
                        break;

                    case MSG_TYPE_VICE_HOST_SYNC_ACK:
                        // Host receives ack from vice host
                        var syncAckMsg = SerializationUtils.DeserializeFromBytes<ViceHostSyncAck>(payloadSpan);
                        GONetViceHostManager.Instance.OnSyncAckReceived(syncAckMsg);
                        GONetLog.Debug($"[GossipIntegration] Received vice host sync ack: seq {syncAckMsg.AcknowledgedSequence}");
                        break;

                    // Hot Standby messages (Phase 2.10)
                    case MSG_TYPE_STANDBY_HELLO:
                        var helloMsg = SerializationUtils.DeserializeFromBytes<StandbyHelloMessage>(payloadSpan);
                        // This is received on dormant server - route to hot standby manager
                        // relatedConnection is the server-to-client connection
                        if (relatedConnection is GONetConnection_ServerToClient serverConn)
                        {
                            GONetHotStandbyManager.Instance.HandleStandbyHello(helloMsg, serverConn);
                        }
                        break;

                    case MSG_TYPE_STANDBY_HELLO_ACK:
                        var helloAckMsg = SerializationUtils.DeserializeFromBytes<StandbyHelloAckMessage>(payloadSpan);
//                        GONetLog.Warning($"[GossipIntegration-RECV] Received StandbyHelloAck: ServerAuthorityId={helloAckMsg.ServerAuthorityId}, " +
//                            $"Accepted={helloAckMsg.Accepted}, connection={relatedConnection?.GetType().Name ?? "null"}");
                        ushort peerAuthorityId = helloAckMsg.ServerAuthorityId;
                        if (relatedConnection != null &&
                            GONetHotStandbyManager.Instance.TryGetPeerAuthorityIdForConnection(relatedConnection, out ushort resolvedPeerAuthorityId))
                        {
                            peerAuthorityId = resolvedPeerAuthorityId;
                        }
                        else if (peerAuthorityId == GONetMain.OwnerAuthorityId_Server)
                        {
                            GONetLog.Warning("[GossipIntegration] StandbyHelloAck could not resolve peer authority - using server authority fallback");
                        }

                        GONetHotStandbyManager.Instance.HandleStandbyHelloAck(helloAckMsg, peerAuthorityId);
                        break;

                    case MSG_TYPE_STANDBY_KEEPALIVE:
                        var keepaliveMsg = SerializationUtils.DeserializeFromBytes<StandbyKeepaliveMessage>(payloadSpan);
                        // Route to appropriate handler based on whether this arrived on dormant server
                        if (relatedConnection is GONetConnection_ServerToClient serverConnKeepalive)
                        {
                            // This is the dormant server receiving keepalive from a standby client
                            GONetHotStandbyManager.Instance.HandleStandbyKeepaliveOnServer(keepaliveMsg, serverConnKeepalive);
                        }
                        else
                        {
                            // This is a standby client receiving keepalive from a dormant server
                            // CRITICAL FIX (Dec 2025): Resolve the peer authority from the connection, not the message.
                            // After handoff, a client may have both an Active game connection (keyed as 1023) and a
                            // shadow connection to the server's dormant (keyed as 1022). Keepalives from the dormant
                            // server have AuthorityId=1023, but should route to the shadow connection (1022), not
                            // the Active connection. Without this fix, the shadow times out waiting for keepalives.
                            ushort keepalivePeerAuthorityId = keepaliveMsg.AuthorityId;
                            if (relatedConnection != null &&
                                GONetHotStandbyManager.Instance.TryGetPeerAuthorityIdForConnection(relatedConnection, out ushort resolvedKeepalivePeerAuthorityId))
                            {
                                keepalivePeerAuthorityId = resolvedKeepalivePeerAuthorityId;
                            }
                            GONetHotStandbyManager.Instance.HandleStandbyKeepalive(keepaliveMsg, keepalivePeerAuthorityId);
	                    }
	                        break;

	                    case MSG_TYPE_SESSION_PROMOTE:
	                        var promoteMsg = SerializationUtils.DeserializeFromBytes<SessionPromoteMessage>(payloadSpan);
	                        OnSessionPromoteReceived(promoteMsg, relatedConnection);
	                        break;

	                    case MSG_TYPE_RELIABILITY_RESET_REQUEST:
	                        var resetReq = SerializationUtils.DeserializeFromBytes<ReliabilityResetRequestMessage>(payloadSpan);
	                        if (relatedConnection is GONetConnection_ServerToClient resetReqServerConn)
	                        {
	                            GONetHotStandbyManager.Instance.HandleReliabilityResetRequest(resetReq, resetReqServerConn);
	                        }
	                        break;

	                    case MSG_TYPE_RELIABILITY_RESET_COMMIT:
	                        var resetCommit = SerializationUtils.DeserializeFromBytes<ReliabilityResetCommitMessage>(payloadSpan);
	                        GONetHotStandbyManager.Instance.HandleReliabilityResetCommit(resetCommit, relatedConnection);
	                        break;

	                    case MSG_TYPE_RELIABILITY_RESET_COMPLETE:
	                        var resetComplete = SerializationUtils.DeserializeFromBytes<ReliabilityResetCompleteMessage>(payloadSpan);
	                        if (relatedConnection is GONetConnection_ServerToClient resetCompleteServerConn)
	                        {
	                            GONetHotStandbyManager.Instance.HandleReliabilityResetComplete(resetComplete, resetCompleteServerConn);
	                        }
	                        break;

	                    case MSG_TYPE_MESH_HEARTBEAT:
	                        var meshHeartbeat = SerializationUtils.DeserializeFromBytes<MeshHeartbeatMessage>(payloadSpan);
	                        GONetHotStandbyManager.Instance.HandleMeshHeartbeat(meshHeartbeat);
	                        break;

                    case MSG_TYPE_MESH_TOPOLOGY_SYNC:
                        var topologyMsg = SerializationUtils.DeserializeFromBytes<MeshTopologySyncMessage>(payloadSpan);
                        GONetHotStandbyManager.Instance.OnMeshTopologyReceived(topologyMsg);
                        break;

                    default:
                        GONetLog.Warning($"[GONetGossipIntegration] Unknown gossip message type: {messageType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                GONetLog.Error($"[GONetGossipIntegration] Failed to deserialize gossip message type {messageType}: {ex.Message}");
            }
        }

        private static void OnHostHeartbeatReceived(HostHeartbeatMessage message, GONetConnection relatedConnection)
        {
            // Validate and update host identity
            var hostIdentity = message.HostIdentity;
            bool isValid = GONetMain.IsHostIdentityValid(in hostIdentity);

            // Debug: Log validation details for heartbeats
            //GONetLog.Debug($"[Heartbeat-VALID] Heartbeat validation: isValid={isValid}, " +
//                          $"recv.SessionGUID={hostIdentity.SessionGUID}, local.SessionGUID={GONetMain.SessionGUID}, " +
//                          $"recv.Epoch={hostIdentity.HostEpoch}, local.Epoch={GONetMain.HostEpoch}, " +
//                          $"GUIDMatch={hostIdentity.SessionGUID == GONetMain.SessionGUID}, EpochOK={hostIdentity.HostEpoch >= GONetMain.HostEpoch}");

            if (isValid)
            {
                long receivedRawTicks = GONetMain.Time.RawElapsedTicks;
                GONetMain.RecordHostHeartbeatTime(in hostIdentity, message.HostElapsedTicks, receivedRawTicks);

                // Update our view of the host if this is newer
                if (hostIdentity.HostEpoch > GONetMain.HostEpoch)
                {
                    GONetLog.Info($"[GONetGossipIntegration] Host identity updated: epoch {hostIdentity.HostEpoch}, " +
                                 $"host={hostIdentity.HostAuthorityId}, viceHost={hostIdentity.ViceHostAuthorityId}");
                }

                // Phase 2: Forward to failover manager for heartbeat tracking
                GONetHostFailoverManager.Instance.OnHostHeartbeatReceived(message);
            }
            else
            {
                GONetLog.Warning($"[GONetGossipIntegration] Received invalid host heartbeat from authority {relatedConnection?.OwnerAuthorityId}. " +
                               $"SessionGUID mismatch: recv={hostIdentity.SessionGUID}, local={GONetMain.SessionGUID}. " +
                               $"Epoch: recv={hostIdentity.HostEpoch}, local={GONetMain.HostEpoch}");
            }
        }

        /// <summary>
        /// Handles SessionPromote message - signals that standby connection should become active.
        /// </summary>
        /// <param name="message">The promotion message</param>
        /// <param name="relatedConnection">The connection this message arrived on (used to identify sender)</param>
        private static void OnSessionPromoteReceived(SessionPromoteMessage message, GONetConnection relatedConnection)
        {
            // Validate epoch is higher than current
            if (message.HostEpoch <= GONetMain.HostEpoch)
            {
                GONetLog.Warning($"[GossipIntegration] Received stale SessionPromote (epoch {message.HostEpoch}, current {GONetMain.HostEpoch})");
                return;
            }

            // Validate session GUID
            if (message.SessionGUID != GONetMain.SessionGUID)
            {
                GONetLog.Warning($"[GossipIntegration] SessionPromote GUID mismatch");
                return;
            }

            GONetLog.Info($"[GossipIntegration] Received SessionPromote: new host={message.HostAuthorityId}, epoch={message.HostEpoch}");

            // FAILOVER FIX (Dec 2025): Apply deferred despawns BEFORE traffic switchover triggers a ReliableNetcode reset.
            // During switchover, reliable traffic can be suppressed temporarily, which can cause some clients to miss
            // despawn notifications sent immediately after host promotion. Delivering the list via SessionPromote ensures
            // all clients process these despawns deterministically.
            if (message.DeferredDespawnGONetIds != null && message.DeferredDespawnGONetIds.Length > 0)
            {
                int despawnCount = message.DeferredDespawnGONetIds.Length;
                GONetLog.Warning($"[Failover] Applying {despawnCount} deferred despawns from SessionPromote (epoch={message.HostEpoch})");

                for (int i = 0; i < despawnCount; i++)
                {
                    uint gonetId = message.DeferredDespawnGONetIds[i];
                    if (gonetId == 0)
                    {
                        continue;
                    }

                    try
                    {
                        // Publish locally as a remote-sourced despawn so the normal despawn handler runs
                        // (tombstones, deferred-spawn cancellation, Unity Destroy), without relaying back over the wire.
                        GONetMain.EventBus.Publish(
                            new DespawnGONetParticipantEvent { GONetId = gonetId },
                            remoteSourceAuthorityId: message.HostAuthorityId,
                            targetClientAuthorityId: GONetMain.MyAuthorityId);
                    }
                    catch (Exception ex)
                    {
                        GONetLog.Warning($"[Failover] Failed to apply deferred despawn for GONetId {gonetId}: {ex.Message}");
                    }
                }
            }

            // Determine which standby connection to activate.
            // The sender's authority ID after promotion (HostAuthorityId) is typically 1023 (server authority),
            // but our standby connection is keyed by their ORIGINAL authority ID before promotion.
            //
            // We need to figure out the sender's original authority ID:
            // 1. If received on our dormant server, look up the sender from our authority map
            // 2. If received on our standby client, we already know the peer's authority
            ushort senderOriginalAuthorityId = 0;

            if (relatedConnection is GONetConnection_ServerToClient serverToClient)
            {
                // Received on our dormant server - the sender is a peer who connected to us
                // Look up their authority ID from our dormant server's authority map
                if (GONetHotStandbyManager.Instance.TryGetAuthorityIdForConnection(serverToClient.InitiatingClientConnectionUID, out ushort authorityId))
                {
                    senderOriginalAuthorityId = authorityId;
                    GONetLog.Debug($"[GossipIntegration] SessionPromote received on dormant server from authority {senderOriginalAuthorityId} (UID: {serverToClient.InitiatingClientConnectionUID})");
                }
                else
                {
                    GONetLog.Warning($"[GossipIntegration] SessionPromote received from unknown connection UID {serverToClient.InitiatingClientConnectionUID}");
                }
            }
            else if (relatedConnection != null)
            {
                // Received on our standby client - we connected to this peer's dormant server
                // CRITICAL FIX (December 2025): We CANNOT use relatedConnection.OwnerAuthorityId here!
                // For GONetConnection_ClientToServer, OwnerAuthorityId is ALWAYS 1023 (server authority constant),
                // regardless of which peer's dormant server we're connected to.
                // Instead, we need to look up which standby connection owns this GONetConnection.
                if (GONetHotStandbyManager.Instance.TryGetPeerAuthorityIdForConnection(relatedConnection, out ushort peerAuthority))
                {
                    senderOriginalAuthorityId = peerAuthority;
                    GONetLog.Debug($"[GossipIntegration] SessionPromote received on standby client to peer {senderOriginalAuthorityId}");
                }
                else
                {
                    GONetLog.Warning($"[GossipIntegration] SessionPromote received on client connection but couldn't identify which standby peer (connection.OwnerAuthorityId={relatedConnection.OwnerAuthorityId} is always server authority, not helpful)");
                }
            }

	            // Try to activate using the sender's original authority ID first
	            bool activated = false;
	            if (senderOriginalAuthorityId != 0)
	            {
	                activated = GONetHotStandbyManager.Instance.TryActivateStandbyConnection(senderOriginalAuthorityId, message.HostEpoch);
	                if (activated)
	                {
	                    GONetLog.Info($"[GossipIntegration] Traffic switchover complete to new host {message.HostAuthorityId} (via standby to original authority {senderOriginalAuthorityId})");
	                }
	            }

	            // If that didn't work, try using the new host authority ID directly
	            if (!activated)
	            {
	                activated = GONetHotStandbyManager.Instance.TryActivateStandbyConnection(message.HostAuthorityId, message.HostEpoch);
	                if (activated)
	                {
	                    GONetLog.Info($"[GossipIntegration] Traffic switchover complete to new host {message.HostAuthorityId}");
	                }
	            }

            if (!activated)
            {
                GONetLog.Error($"[GossipIntegration] Failed to activate standby connection to new host {message.HostAuthorityId} - manual reconnect may be required");
            }

            // CRITICAL: Notify failover manager that promotion happened via hot standby mesh.
            // This handles the case where the client was in WaitingForTiebreaker and the emergency
            // promotion was received via the mesh (not the main GONet channel which may be dead).
            // Create an EmergencyHostPromotionMessage to pass to the failover manager.
            var emergencyPromotion = new EmergencyHostPromotionMessage
            {
                NewHostAuthorityId = message.HostAuthorityId,
                NewHostEpoch = message.HostEpoch,
                PreviousHostAuthorityId = GONetMain.CurrentHostIdentity.HostAuthorityId,
                PromotingPeerOriginalAuthorityId = senderOriginalAuthorityId,
                FailoverReason = "SessionPromote via mesh",
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };
            GONetHostFailoverManager.Instance.OnEmergencyPromotionReceived(emergencyPromotion);
        }

        #endregion

        #region Phase 2: Handoff Message Sending

        /// <summary>
        /// Sends a handoff prepare message to the target vice host.
        /// </summary>
        public static void SendHandoffPrepare(HostHandoffPrepareMessage message, ushort targetViceHostId)
        {
            if (!isInitialized) return;
            SendToPeer(message, MSG_TYPE_HANDOFF_PREPARE, targetViceHostId, reliable: true);
        }

        /// <summary>
        /// Sends a prepare acknowledgement back to the host.
        /// </summary>
        public static void SendHandoffPrepareAck(ViceHostPrepareAckMessage message)
        {
            if (!isInitialized) return;
            SendToHost(message, MSG_TYPE_HANDOFF_PREPARE_ACK, reliable: true);
        }

        /// <summary>
        /// Sends delta state to the vice host.
        /// </summary>
        public static void SendHandoffDelta(HostHandoffDeltaMessage message, ushort targetViceHostId)
        {
            if (!isInitialized) return;
            SendToPeer(message, MSG_TYPE_HANDOFF_DELTA, targetViceHostId, reliable: true);
        }

        /// <summary>
        /// Broadcasts handoff commit to all nodes.
        /// </summary>
        public static void SendHandoffCommit(HostHandoffCommitMessage message)
        {
            if (!isInitialized) return;
            BroadcastToAllPeers(message, MSG_TYPE_HANDOFF_COMMIT, reliable: true);
        }

        /// <summary>
        /// Broadcasts new host complete to all nodes.
        /// </summary>
        public static void SendNewHostComplete(NewHostCompleteMessage message)
        {
            if (!isInitialized) return;
            BroadcastToAllPeers(message, MSG_TYPE_HANDOFF_COMPLETE, reliable: true);
        }

        /// <summary>
        /// Broadcasts handoff abort to all nodes.
        /// </summary>
        public static void SendHandoffAbort(HostHandoffAbortMessage message)
        {
            if (!isInitialized) return;
            BroadcastToAllPeers(message, MSG_TYPE_HANDOFF_ABORT, reliable: true);
        }

        /// <summary>
        /// Broadcasts emergency promotion to all nodes.
        /// </summary>
        public static void SendEmergencyPromotion(EmergencyHostPromotionMessage message)
        {
            if (!isInitialized) return;
            BroadcastToAllPeers(message, MSG_TYPE_EMERGENCY_PROMOTION, reliable: true);
        }

        /// <summary>
        /// Sends vice host full sync to the designated vice host.
        ///
        /// CONGESTION-AWARE (Dec 2025):
        /// When vice host is under backpressure, switch to unreliable to allow queue to drain.
        /// Full syncs can be retried on next 1Hz cycle if lost.
        /// </summary>
        public static void SendViceHostFullSync(ViceHostFullSyncMessage message, ushort viceHostId)
        {
            if (!isInitialized) return;

            // Check if vice host is under backpressure - switch to unreliable to allow recovery
            bool isUnderBackpressure = GONetMain.IsClientUnderBackpressure(viceHostId, out _);
            bool useReliable = !isUnderBackpressure;

            if (isUnderBackpressure)
            {
                GONetLog.Debug($"[ViceHost] Full sync to {viceHostId} sent UNRELIABLY (client under backpressure)");
            }

            SendToPeer(message, MSG_TYPE_VICE_HOST_FULL_SYNC, viceHostId, reliable: useReliable);
        }

        /// <summary>
        /// Sends vice host delta sync to the designated vice host.
        /// </summary>
        public static void SendViceHostDeltaSync(ViceHostDeltaSyncMessage message, ushort viceHostId)
        {
            if (!isInitialized) return;
            SendToPeer(message, MSG_TYPE_VICE_HOST_DELTA_SYNC, viceHostId, reliable: false); // Unreliable for high-freq updates
        }

        /// <summary>
        /// Sends sync acknowledgement from vice host to host.
        /// </summary>
        public static void SendViceHostSyncAck(ViceHostSyncAck message)
        {
            if (!isInitialized) return;
            SendToHost(message, MSG_TYPE_VICE_HOST_SYNC_ACK, reliable: true);
        }

        /// <summary>
        /// Sends a full mesh topology SNAPSHOT to a single client (no broadcast to others).
        /// This is the building block for snapshot-based topology sync.
        /// </summary>
        private static void SendMeshTopologySnapshotToClientOnly(ushort recipientAuthorityId)
        {
            if (!isInitialized || !GONetMain.IsServer) return;

            var hotStandby = GONetHotStandbyManager.Instance;
            if (hotStandby == null) return;

            // Build snapshot excluding recipient (receiver also skips self, but this keeps payload cleaner)
            var entries = new List<MeshTopologyEntry>();
            foreach (var peer in hotStandby.GetAllKnownPeerEndpoints())
            {
                if (peer.AuthorityId == recipientAuthorityId)
                    continue;

                string address;
                if (peer.Endpoint.HasTransportId)
                {
                    // Steamworks P2P: address is SteamID string, port is virtual port
                    address = peer.Endpoint.TransportSpecificId.ToString();
                }
                else if (peer.Endpoint.HasIPv4)
                {
                    address = peer.Endpoint.GetIPv4Address().ToString();
                }
                else if (peer.Endpoint.HasIPv6)
                {
                    address = peer.Endpoint.GetIPv6Address().ToString();
                }
                else
                {
                    address = "";
                }

                entries.Add(new MeshTopologyEntry
                {
                    AuthorityId = peer.AuthorityId,
                    PersistentId = peer.PersistentId,
                    DormantServerAddress = address,
                    DormantServerPort = peer.Endpoint.Port
                });
            }

            // IMPORTANT: send even when entries.Count == 0 so clients can prune stale state
            var message = new MeshTopologySyncMessage
            {
                Peers = entries,
                HostEpoch = (int)GONetMain.HostEpoch
            };

            int bytesUsed = SerializeGossipMessage(MSG_TYPE_MESH_TOPOLOGY_SYNC, message);

            var remoteClient = GONetMain.gonetServer?.GetRemoteClientByAuthorityId(recipientAuthorityId);
            if (remoteClient != null)
            {
                // CONGESTION-AWARE (Dec 2025): Switch to unreliable during backpressure to allow queue to drain
                // Mesh topology can be retried on next sync cycle if lost
                bool isUnderBackpressure = GONetMain.IsClientUnderBackpressure(recipientAuthorityId, out _);
                byte channelId = isUnderBackpressure
                    ? GONetChannel.DistributedHost_Unreliable.Id
                    : GONetChannel.DistributedHost_Reliable.Id;

                GONetMain.gonetServer.SendBytesToClient(remoteClient, sendBuffer, bytesUsed, channelId);

                var peerList = string.Join(",", entries.Select(e => $"{e.AuthorityId}@{e.DormantServerAddress}:{e.DormantServerPort}"));
                string channelName = isUnderBackpressure ? "UNRELIABLE(backpressure)" : "reliable";
                GONetLog.Warning($"[MESH-TOPO] SNAPSHOT SEND to client {recipientAuthorityId} ({channelName}): peers=[{peerList}] count={entries.Count} epoch={message.HostEpoch} myAuth={GONetMain.MyAuthorityId}");
            }
        }

        /// <summary>
        /// Called when a new client joins:
        /// - Send full snapshot to the new client
        /// - Send full snapshot to all existing clients (so everyone converges)
        /// NO DELTAS - always full snapshots for consistency.
        /// </summary>
        /// <param name="newClientAuthorityId">Authority ID of the newly joined client</param>
        public static void SendMeshTopologyToClient(ushort newClientAuthorityId)
        {
            if (!isInitialized || !GONetMain.IsServer) return;
            if (GONetMain.gonetServer == null) return;

            GONetLog.Warning($"[MESH-TOPO] New client {newClientAuthorityId} joined - sending snapshots to all");

            // 1) New client gets snapshot
            SendMeshTopologySnapshotToClientOnly(newClientAuthorityId);

            // 2) Everyone else gets snapshot (so they learn about the new client AND can prune stale)
            foreach (var client in GONetMain.gonetServer.remoteClients)
            {
                if (client == null) continue;

                ushort recipient = client.ConnectionToClient.OwnerAuthorityId;
                if (recipient == newClientAuthorityId) continue;

                // Skip sending to self (loopback connection)
                if (recipient == GONetMain.MyAuthorityId) continue;

                SendMeshTopologySnapshotToClientOnly(recipient);
            }
        }

        /// <summary>
        /// Broadcasts full mesh topology SNAPSHOT to all connected clients.
        /// Called after failover promotion to ensure all nodes have consistent mesh knowledge.
        /// Uses snapshot-only sender to avoid N×(N-1) broadcast storms.
        /// </summary>
        public static void BroadcastMeshTopologyToAllClients()
        {
            if (!isInitialized || !GONetMain.IsServer) return;

            var hotStandby = GONetHotStandbyManager.Instance;
            if (hotStandby == null) return;

            // Log what we know about the mesh before broadcasting
            var knownPeers = hotStandby.GetAllKnownPeerEndpoints().ToList();
            var peerList = string.Join(",", knownPeers.Select(p => $"{p.AuthorityId}@{p.Endpoint.Port}"));
            GONetLog.Warning($"[MESH-TOPO] BroadcastAll START: knownPeers=[{peerList}] count={knownPeers.Count} myAuth={GONetMain.MyAuthorityId}");
            hotStandby.LogCurrentMeshState("BroadcastAll");

            if (GONetMain.gonetServer == null) return;

            var clientIds = new List<ushort>();
            foreach (var client in GONetMain.gonetServer.remoteClients)
            {
                if (client == null) continue;

                ushort id = client.ConnectionToClient.OwnerAuthorityId;

                // CRITICAL FIX (Dec 2025): Skip sending to self (loopback connection).
                // After client-host promotion, there may be a loopback connection with authority 1023.
                // Without this check, we send snapshots to ourselves (wasteful and confusing in logs).
                if (id == GONetMain.MyAuthorityId)
                {
                    continue;
                }

                clientIds.Add(id);

                // IMPORTANT: use the client-only sender (no nested broadcasts)
                SendMeshTopologySnapshotToClientOnly(id);
            }

            GONetLog.Warning($"[MESH-TOPO] BroadcastAll DONE: sentTo=[{string.Join(",", clientIds)}] myAuth={GONetMain.MyAuthorityId}");
        }

        #endregion

        #region Gossip Event Handlers

        private static void OnNodeJoined(ushort authorityId, GONetNodeIdentity identity)
        {
            GONetLog.Info($"[GONetGossipIntegration] Node joined gossip mesh: authority={authorityId}, persistentId={identity.PersistentId:X16}");

            // CRITICAL FIX (Dec 2025): Conditionally skip peer discovery for server authority (1023).
            // - BEFORE failover (HostEpoch == 0): Skip - the original server has no dormant server mesh connection
            // - AFTER failover (HostEpoch > 0): Allow - the promoted host's dormant server IS part of the mesh
            //   Late-joiners need to connect to the promoted host's dormant server for the NEXT failover.
            //   Without this, late-joiners show "3/2 peers" because they never connect to the promoted host's dormant.
            if (authorityId == GONetMain.OwnerAuthorityId_Server && GONetMain.HostEpoch == 0)
            {
                GONetLog.Debug($"[GONetGossipIntegration] Skipping peer discovery for server authority {authorityId} - pre-failover server is not a mesh peer");
                return;
            }
            else if (authorityId == GONetMain.OwnerAuthorityId_Server)
            {
                // CRITICAL (Dec 2025): After failover, late-joiners MUST connect to the promoted host's dormant server.
                // This enables full mesh topology for the next potential failover.
                GONetLog.Info($"[GONetGossipIntegration] Allowing peer discovery for server authority {authorityId} - post-failover (epoch={GONetMain.HostEpoch}), promoted host's dormant server is a mesh peer");
            }

            // Notify churn detection
            GONetGossipManager.Instance.NotifyChurn((float)GONetMain.Time.ElapsedSeconds);

            // Phase 2.10: Notify hot standby manager to establish standby connection
            if (GONetGossipManager.Instance.TryGetNodeEndpoint(authorityId, out var endpoint))
            {
                GONetHotStandbyManager.Instance.OnPeerDiscovered(authorityId, endpoint);
            }
        }

        private static void OnNodeLeft(ushort authorityId)
        {
            GONetLog.Info($"[GONetGossipIntegration] Node left gossip mesh: authority={authorityId}");

            // Skip for server authority - server is not tracked as a mesh peer
            if (authorityId == GONetMain.OwnerAuthorityId_Server)
            {
                return;
            }

            // Notify churn detection
            GONetGossipManager.Instance.NotifyChurn((float)GONetMain.Time.ElapsedSeconds);

            // Phase 2.10: Notify hot standby manager to close standby connection
            GONetHotStandbyManager.Instance.OnPeerLost(authorityId);
        }

        private static void OnMetricsReceived(ushort authorityId, GONetNodeMetrics metrics)
        {
            // Log only in debug mode to avoid spam
            //GONetLog.Debug($"[GONetGossipIntegration] Metrics received from authority={authorityId}: " +
//                          $"RTT={metrics.RTT_Average_Ms}ms, CPU={metrics.CPU_Headroom_Percent}%");
        }

        private static void OnMetricsStale(ushort authorityId)
        {
            //GONetLog.Warning($"[GONetGossipIntegration] Metrics stale for authority={authorityId}");
        }

        #endregion

        #region Host Status

        /// <summary>
        /// Called when this node becomes or stops being the host.
        /// </summary>
        public static void OnHostStatusChanged(bool isNowHost)
        {
            if (!isInitialized) return;

            GONetGossipManager.Instance.OnHostStatusChanged(isNowHost);

            if (isNowHost)
            {
                // CRITICAL FIX (Dec 2025): When promoting to host, clear any stale gossip entry
                // for our new authority ID (1023 = server authority). The old server's metrics
                // are still in remoteMetrics and will cause endless "Node 1023 metrics are stale" warnings.
                GONetGossipManager.Instance.OnLocalAuthorityChanged(
                    GONetMain.OwnerAuthorityId_Server,
                    (float)GONetMain.Time.ElapsedSeconds);

                GONetLog.Info("[GONetGossipIntegration] This node is now the host");
            }
            else
            {
                GONetLog.Info("[GONetGossipIntegration] This node is no longer the host");
            }
        }

        /// <summary>
        /// Called when a client disconnects to clean up gossip state.
        /// </summary>
        public static void OnClientDisconnected(ushort authorityId)
        {
            if (!isInitialized) return;

            GONetGossipManager.Instance.OnNodeDisconnected(authorityId, (float)GONetMain.Time.ElapsedSeconds);
        }

        #endregion
    }
}
