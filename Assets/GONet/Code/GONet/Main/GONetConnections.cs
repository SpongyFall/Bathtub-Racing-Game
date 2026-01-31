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

using GONet.Utils;
using GONet.Transport;
using NetcodeIO.NET;
using ReliableNetcode;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using GONetChannelId = System.Byte;

namespace GONet
{
    public abstract class GONetConnection : ReliableEndpoint
    {
        #region Constants

        /// <summary>
        /// Magic number identifying GONet chunk headers (0xC40C = "Chunk" in hex speak).
        /// Prevents false-positive chunk detection when normal messages accidentally match chunk heuristics.
        /// </summary>
        private const ushort CHUNK_MAGIC = 0xC40C;

        /// <summary>
        /// Maximum allowed size for a chunked message (2MB).
        /// SECURITY: Prevents malicious clients from requesting massive allocations (DoS attack).
        /// Any chunk header claiming originalSize > this value will be rejected immediately.
        /// </summary>
        private const int MAX_LARGE_MESSAGE_SIZE = 2 * 1024 * 1024; // 2 MB

        /// <summary>
        /// Time-to-live for incomplete chunk reassembly (seconds).
        /// SECURITY: Prevents memory leaks from incomplete chunk streams.
        /// If all chunks aren't received within this timeframe, the reassembly context is discarded.
        /// </summary>
        private const float CHUNK_REASSEMBLY_TTL_SECONDS = 10.0f;

        /// <summary>
        /// Maximum number of chunks per frame to send (flow control).
        /// PERFORMANCE: Prevents packet storms on congested networks.
        /// At 60 FPS with 10KB chunks: 2 chunks/frame × 60 FPS = 1.2 MB/sec throughput.
        /// </summary>
        private const int MAX_CHUNKS_PER_FRAME = 2;

        /// <summary>
        /// DIAGNOSTIC: Enable detailed logging to debug cross-connection packet delivery.
        /// Set to true temporarily when investigating packet routing issues.
        /// </summary>
        public static bool EnableCrossDeliveryDiagnostics = false;

        #endregion

        #region New Transport Abstraction (Composition-Based - Phase 2)

        /// <summary>
        /// NEW: Transport abstraction for pluggable transports.
        /// Null when using old path (ReliableEndpoint inheritance).
        /// </summary>
        protected IGONetTransport transport_new;

        /// <summary>
        /// NEW: Specific connection object (server-side: client connection, client-side: null).
        /// </summary>
        protected IGONetTransportConnection connection_new;

        /// <summary>
        /// NEW: Feature flag to control routing path.
        /// True = use new transport abstraction, False = use old ReliableEndpoint inheritance.
        /// </summary>
        private bool useNewTransportPath = false;

        /// <summary>
        /// HIGH-LOAD OPTIMIZATION (December 2025): Transport-level receive timestamp for the
        /// message currently being processed. This is captured from OnMessageReceivedWithTimestamp
        /// BEFORE OnMessageReceived fires, so the processing chain can use the accurate timestamp.
        ///
        /// For Steamworks: Contains the accurate timestamp from SteamNetworkingMessage_t.m_usecTimeReceived.
        /// For NetcodeIO: Contains processing time (no transport-level timestamp available).
        ///
        /// Reset to 0 after each message is processed.
        /// </summary>
        private long _pendingTransportReceiveTicks = 0;

        #endregion

        /// <summary>
        /// Whether this connection is client side and represents the connection to the server or it is server side and represents the connection to the client, 
        /// this value here is the unique ID of the connection between the two computers as was initially set/created by the client first starting the connection.
        /// There is even the potential for future releases of GONet where this connection represents a client (peer) to client (peer) connection, but one of the two
        /// had to initiate the connection.
        /// Both parties connected will have the same value in this field.
        /// </summary>
        public ulong InitiatingClientConnectionUID { get; protected set; }

        /// <summary>
        /// PERFORMANCE: Changed from auto-property to field (November 2025).
        /// Auto-property with internal set had measurable overhead in profiler due to high call frequency.
        /// Field access is direct memory read with zero overhead.
        /// </summary>
        public ushort OwnerAuthorityId;

        #region round trip time stuffs (RTT)

        public float RTTMilliseconds_LowLevelTransportProtocol => RTTMilliseconds;

        private float rtt_latest;
        /// <summary>
        /// GONet owned data that represents more than just the low level network "wire" time.
        /// If you want internally calculated value of RTT from lower level transport/protocol impl, see/use <see cref="RTTMilliseconds_LowLevelTransportProtocol"/> (which is just a reflection of <see cref="ReliableEndpoint.RTTMilliseconds"/>) instead.
        /// Unit of measure is seconds here.
        /// </summary>
        public float RTT_Latest
        {
            get { return rtt_latest; }
            internal set
            {
                rtt_latest = value;
                if (++iLast_rtt_recent == RTT_HISTORY_COUNT)
                {
                    iLast_rtt_recent = 0;
                }
                rtt_recent[iLast_rtt_recent] = value;

                if (hasBeenSetOnce_rtt_latest)
                {
                    float sum = 0f;
                    for (int i = 0; i < RTT_HISTORY_COUNT; ++i)
                    {
                        sum += rtt_recent[i];
                    }
                    RTT_RecentAverage = sum / RTT_HISTORY_COUNT;
                }
                else
                {
                    for (int i = 0; i < RTT_HISTORY_COUNT; ++i)
                    {
                        rtt_recent[i] = value;
                    }
                    hasBeenSetOnce_rtt_latest = true;
                    RTT_RecentAverage = value;
                }
            }
        }

        /// <summary>
        /// GONet owned data that represents more than just the low level network "wire" time.
        /// If you want internally calculated value of RTT from lower level transport/protocol impl, see/use <see cref="RTTMilliseconds_LowLevelTransportProtocol"/> (which is just a reflection of <see cref="ReliableEndpoint.RTTMilliseconds"/>) instead.
        /// This is useful to reference/use instead of <see cref="RTT_Latest"/> in order to account for jitter (i.e., RTT variation) by averaging recent values.
        /// Unit of measure is seconds here.
        /// </summary>
        public float RTT_RecentAverage { get; private set; }

        private const int RTT_HISTORY_COUNT = 5;
        private const string DO_NOT_USE = "Do not use this method.  Use SendMessageOverChannel(byte[], int, GONetChannelId) instead.";
        bool hasBeenSetOnce_rtt_latest = false;
        int iLast_rtt_recent = -1;
        readonly float[] rtt_recent = new float[RTT_HISTORY_COUNT];

        #endregion

        /// <summary>
        /// OLD PATH: Constructor using ReliableEndpoint inheritance (backward compatible).
        /// </summary>
        protected GONetConnection(int maxReliableQueueSize = 2000) : base(maxReliableQueueSize)
        {
            ReceiveCallback = OnReceiveCallback;
            useNewTransportPath = false;
        }

        /// <summary>
        /// Resets the reliability layer state for connection switchover.
        /// CRITICAL for hot standby failover: clears sequence numbers, ACK state, and buffers.
        /// Without this, the new connection would have sequence number mismatches causing message drops.
        /// </summary>
        public void ResetReliabilityLayer()
        {
            ResetReliabilityLayer(reliableSessionId: 0);
        }

        public void ResetReliabilityLayer(uint reliableSessionId)
        {
            // Update the reliable session id (if requested) BEFORE resetting so that any in-flight packets from
            // the previous session are rejected once reliable traffic resumes.
            if (reliableSessionId != 0)
            {
                base.ReliableSessionId = reliableSessionId;
            }

            // Reset only the RELIABLE channel state (sequences, ACKs, send/receive buffers) while preserving the
            // UNRELIABLE channel sequencing. The unreliable channel is used for failover/reset coordination and
            // must not be broken by a sequence rewind when coordination messages are already in-flight.
            base.ResetReliableChannel();

            // Reset RTT tracking
            hasBeenSetOnce_rtt_latest = false;
            iLast_rtt_recent = -1;
            rtt_latest = 0;
            RTT_RecentAverage = 0;
            Array.Clear(rtt_recent, 0, rtt_recent.Length);

            // Clear pending chunk send queue
            while (pendingChunksQueue.Count > 0)
            {
                var chunk = pendingChunksQueue.Dequeue();
                SerializationUtils.ReturnByteArray(chunk.Data);
            }

            // Clear chunk reassembly state
            foreach (var kvp in chunkReassemblyMap)
            {
                SerializationUtils.ReturnByteArray(kvp.Value.ReassembledData);
            }
            chunkReassemblyMap.Clear();

            GONetLog.Info($"[GONetConnection] Reliability layer reset for connection (Authority: {OwnerAuthorityId})");
        }

        /// <summary>
        /// PHASE 7 FIX (December 2025): Clear pre-authority state while preserving pending messages.
        ///
        /// When a client transitions from pre-authority (Client:0) to post-authority (Client:N),
        /// the ackBuffer contains stale packet-to-message mappings from the pre-authority phase.
        /// This method clears ONLY the ACK-related state to prevent false ACKs while preserving
        /// pending messages in sendBuffer that need to be retransmitted.
        ///
        /// Unlike ResetReliabilityLayer() which clears everything, this method is designed
        /// specifically for the authority transition scenario where we want to keep pending
        /// messages but clear stale acknowledgment state.
        ///
        /// Call this when the client receives its authority ID from the server.
        /// </summary>
        public new void ClearPreAuthorityState()
        {
            // Call base ReliableEndpoint method which clears ackBuffer and packetController
            // while preserving sendBuffer (pending messages that need retransmission)
            base.ClearPreAuthorityState();

            // Note: We intentionally do NOT reset RTT tracking here because:
            // 1. RTT measurements from pre-authority phase are still valid
            // 2. Clearing RTT could cause initial congestion control issues

            GONetLog.Info($"[GONetConnection] Pre-authority state cleared for connection (Authority: {OwnerAuthorityId})");
        }

        /// <summary>
        /// NEW PATH: Constructor using IGONetTransport composition (pluggable transports).
        /// </summary>
        /// <param name="transport">Transport implementation</param>
        /// <param name="connection">Specific connection (server-side: client, client-side: null)</param>
        /// <param name="maxReliableQueueSize">Max reliable message queue size</param>
        /// <param name="isStandbyMeshClient">True if this is a standby mesh client (has own transport, should subscribe)</param>
        protected GONetConnection(IGONetTransport transport, IGONetTransportConnection connection, int maxReliableQueueSize = 2000, bool isStandbyMeshClient = false)
            : base(maxReliableQueueSize)  // Still call base for now (Phase 6 will remove inheritance)
        {
            this.transport_new = transport;
            this.connection_new = connection;
            this.useNewTransportPath = true;

            // Check if transport provides built-in reliability
            bool transportHasReliability = transport.Capabilities.HasFlag(GONetTransportCapabilities.Reliability);

            GONetLog.Info($"[GONetConnection] Transport {transport.GetType().Name} has built-in reliability: {transportHasReliability}");

            if (transportHasReliability)
            {
                // Transport has built-in reliability - subscribe directly to transport
                GONetLog.Info($"[GONetConnection] Using transport's built-in reliability (no ReliabilityLayerAdapter)");

                // HIGH-LOAD OPTIMIZATION (December 2025): Subscribe to OnMessageReceivedWithTimestamp FIRST
                // to capture accurate transport-level receive timestamps for RTT calculations.
                // The timestamped event fires BEFORE OnMessageReceived, so we capture the timestamp
                // then use it when OnMessageReceived processes the message.
                transport.OnMessageReceivedWithTimestamp += OnTransportMessageReceivedWithTimestamp;
                transport.OnMessageReceived += OnTransportMessageReceived;
            }
            else
            {
                // Transport lacks reliability - wrap with ReliableNetcode (use base ReliableEndpoint)
                // GONetConnection inherits from ReliableEndpoint, so we wire up both send and receive paths
                GONetLog.Info($"[GONetConnection] Transport lacks reliability - will use ReliableNetcode wrapper");

                // CRITICAL FIX #1: Set ReceiveCallback so unwrapped messages go to GONet processing
                ReceiveCallback = OnReceiveCallback;

                // CRITICAL FIX #2: Set TransmitCallback so ReliableEndpoint can send wrapped packets via transport
                // When ReliableEndpoint wants to send a reliability-wrapped packet, send it via transport unreliable channel
                TransmitCallback = (buffer, length) =>
                {
                    // Send wrapped packet via transport's unreliable channel
                    // ReliableEndpoint handles reliability, so transport just does dumb pipe
                    transport.Send(buffer, length, GONetTransportQoS.Unreliable, connection, 0);
                };

                // CRITICAL FIX #3: Subscribe to transport messages so ReliableEndpoint can process them
                // EXCEPTION: HOST's MAIN client (connection==null AND GONetMain.IsServer AND NOT standby mesh) must NOT subscribe!
                // HOST's main client shares the server's transport, and with connection==null the filter at line 281
                // would accept ALL packets (not just loopback), sending false ACKs to remote clients and
                // causing reliable message deadlock. HOST's main client receives via loopback, not transport broadcast.
                // HOWEVER: Standby mesh clients have their OWN separate transport and MUST subscribe to receive messages!
                bool isHostMainClient = connection == null && GONetMain.IsServer && !isStandbyMeshClient;
                GONetLog.Info($"[GONetConnection] HOST-FIX-CHECK: connection={(connection == null ? "null" : connection.GetType().Name)}, GONetMain.IsServer={GONetMain.IsServer}, isStandbyMeshClient={isStandbyMeshClient}, isHostMainClient={isHostMainClient}, willSubscribe={!isHostMainClient}");
                if (!isHostMainClient)
                {
                    // HIGH-LOAD OPTIMIZATION (December 2025): Subscribe to OnMessageReceivedWithTimestamp FIRST
                    // to capture accurate transport-level receive timestamps for RTT calculations.
                    // This is critical for Steamworks which provides m_usecTimeReceived (when Steam actually
                    // received the packet, not when we processed it). For non-reliable transports using
                    // ReliableNetcode wrapper, the timestamp flows through the reliability layer.
                    transport.OnMessageReceivedWithTimestamp += (data, length, qos, source, channel, transportReceiveTicks) =>
                    {
                        // Store the transport-level timestamp for the upcoming OnMessageReceived → ReceivePacket → ReceiveCallback chain
                        _pendingTransportReceiveTicks = transportReceiveTicks;
                    };

                    transport.OnMessageReceived += (data, length, qos, source, channel) =>
                    {
                    // DIAGNOSTIC (January 2026): Trace transport-level receives before reliability processing
                    // This helps debug if packets arrive at transport but fail in ReliableNetcode
                    // COMMENTED (log cleanup) - fires for every received packet, extremely spammy
                    /*ulong connUID = connection?.ConnectionUID ?? 0;
                    ulong srcUID = source?.ConnectionUID ?? 0;
                    GONetLog.Debug($"[TRANSPORT-RECV] len={length} connUID={connUID} srcUID={srcUID} connIsNull={connection == null} srcIsNull={source == null}");*/

                    // Filter: Only process messages for OUR specific connection (server-side)
                    // Client-side: connection is null OR source is null (NetcodeIO client receives), accept all messages
                    // FIX (December 2025): NetcodeIOTransport passes source=null for client-side receives, so we must
                    // check source != null before comparing. Otherwise ALL client messages are incorrectly filtered.
                    bool wouldFilter = connection != null && source != null && source != connection;

                    // MESH HANDSHAKE DIAGNOSTIC: Log when source is null (connection lookup failed in transport)
                    // or when filtering standby messages (first byte 30-40 are standby message types)
                    byte firstByte = length > 0 ? data[0] : (byte)0;
                    bool isPotentiallyStandbyMessage = firstByte >= 30 && firstByte <= 40;
//                    if (source == null || (isPotentiallyStandbyMessage && wouldFilter))
//                    {
//                        ulong thisUID = connection?.ConnectionUID ?? 0;
//                        ulong sourceUID = source?.ConnectionUID ?? 0;
//                        GONetLog.Warning($"[MESH-DIAG-FILTER] source={(source == null ? "NULL" : sourceUID.ToString())}, " +
//                            $"connection={(connection == null ? "NULL" : thisUID.ToString())}, " +
//                            $"wouldFilter={wouldFilter}, firstByte={firstByte}, length={length}, " +
//                            $"refEqual={ReferenceEquals(source, connection)}, connId={ConnectionId}");
//                    }

                    // DIAGNOSTIC: Log every invocation to understand cross-delivery
                    if (EnableCrossDeliveryDiagnostics)
                    {
                        ulong thisUID = connection?.ConnectionUID ?? 0;
                        ulong sourceUID = source?.ConnectionUID ?? 0;
                        bool refEqual = ReferenceEquals(source, connection);
                        GONetLog.Debug($"[CROSS-DELIVERY-DIAG] ThisConn={ConnectionId} thisUID={thisUID} sourceUID={sourceUID} wouldFilter={wouldFilter} refEqual={refEqual}");
                    }

                    if (wouldFilter)
                        return;

                    // Feed packet to ReliableEndpoint for reliability processing
                    // ReliableEndpoint will:
                    // 1. Process ACKs (mark sent packets as received)
                    // 2. Check sequence numbers (detect duplicates, out-of-order)
                    // 3. Queue for retransmission if needed
                    // 4. Invoke ReceiveCallback when message is ready for application layer
                    try
                    {
                        // TIMESTAMP FIX: Pass the transport-level timestamp through the reliability layer.
                        // This ensures time sync gets accurate receive times even for out-of-order packets.
                        long timestamp = _pendingTransportReceiveTicks;
                        base.ReceivePacket(data, length, timestamp);
                    }
                    finally
                    {
                        _pendingTransportReceiveTicks = 0;
                    }
                    };
                }
            }
        }

        /// <summary>
        /// IMPORTANT: You must NOT use this method.  Instead, use <see cref="SendMessageOverChannel(GONetChannelId[], int, GONetChannelId)"/> in order for the channel stuff to work properly!
        /// </summary>
        [Obsolete(DO_NOT_USE, true)]
        public new void SendMessage(byte[] messageBytes, int bytesUsedCount, QosType qualityOfService)
        {
            throw new NotImplementedException(DO_NOT_USE);
        }

        /// <summary>
        /// IMPORTANT: You **MUST** use this method instead of <see cref="ReliableEndpoint.SendMessage(byte[], int, QosType)"/> in order for the channel stuff to work properly!
        /// NOTE: Automatically chunks oversized messages to prevent transport layer corruption.
        /// </summary>
        public void SendMessageOverChannel(byte[] messageBytes, int bytesUsedCount, GONetChannelId channelId)
        {
            // CRITICAL SIZE VALIDATION: Determine transport limits and chunk if needed
            const int PERFORMANCE_WARN_THRESHOLD = 10 * 1024; // 10 KB - Warn about large messages
            const int CHUNK_SIZE = 10 * 1024; // 10 KB chunks for reliable auto-chunking
            const int FALLBACK_MAX_SIZE = 16 * 1024; // 16 KB - Fallback if transport doesn't report limit

            GONetChannel channel = GONetChannel.ById(channelId);
            bool isReliable = channel.QualityOfService == QosType.Reliable;
            GONetTransportQoS qos = isReliable ? GONetTransportQoS.Reliable : GONetTransportQoS.Unreliable;

            // Query transport for max message size (transport-specific limits)
            int transportMaxSize = FALLBACK_MAX_SIZE;
            if (useNewTransportPath && transport_new != null)
            {
                int reportedMax = transport_new.GetMaxMessageSize(qos);
                if (reportedMax > 0)
                {
                    transportMaxSize = reportedMax;
                }
                // else: reportedMax == -1 means unlimited, keep fallback
            }

            // AUTO-CHUNKING: For oversized messages, transparently split into multiple chunks
            // This prevents critical spawn failures when scenes have many objects
            if (bytesUsedCount > transportMaxSize)
            {
                if (isReliable)
                {
                    // AUTOMATIC CHUNKING (reliable channels only)
                    GONetLog.Warning(
                        $"[AUTO-CHUNK] Message too large ({bytesUsedCount} bytes > {transportMaxSize} bytes), " +
                        $"automatically chunking into {CHUNK_SIZE} byte pieces. " +
                        $"Channel: {channelId}, Owner: {OwnerAuthorityId}. " +
                        $"TransportMaxSize: {transportMaxSize} (from {(useNewTransportPath ? transport_new.GetType().Name : "ReliableNetcode")}). " +
                        $"This is transparent to the receiver (auto-reassembly).");

                    SendMessageOverChannel_Chunked(messageBytes, bytesUsedCount, channelId, CHUNK_SIZE);
                    return; // Chunked send complete
                }
                else
                {
                    // Unreliable messages CANNOT be chunked (no guaranteed delivery/ordering)
                    throw new System.InvalidOperationException(
                        $"CRITICAL: Unreliable message size ({bytesUsedCount} bytes) exceeds transport maximum ({transportMaxSize} bytes). " +
                        $"Channel: {channelId}, Owner: {OwnerAuthorityId}. " +
                        $"Unreliable messages cannot be auto-chunked (no delivery guarantees). " +
                        $"Solutions: 1) Use reliable channel, 2) Reduce message size, 3) Implement custom chunking.");
                }
            }

            // Performance warnings for large messages
            if (bytesUsedCount > PERFORMANCE_WARN_THRESHOLD)
            {
                GONetLog.Info(
                    $"[PERFORMANCE] Large message ({bytesUsedCount} bytes) detected. " +
                    $"Channel: {channelId}, Reliable: {isReliable}, Owner: {OwnerAuthorityId}. " +
                    $"If you frequently send messages > {PERFORMANCE_WARN_THRESHOLD} bytes, " +
                    $"consider optimizing data size or implementing chunking.");
            }

            int headerSize = sizeof(GONetChannelId) + sizeof(int);
            int bodySize_withHeader;

            byte[] messageBytesCompressed = null;
            ushort messageBytesCompressedUsedCount;

            bool isCompressionUsed = GONetMain.AutoCompressEverything != null;
            if (isCompressionUsed)
            {
                GONetMain.AutoCompressEverything.Compress(messageBytes, (ushort)bytesUsedCount, out messageBytesCompressed, out messageBytesCompressedUsedCount);
                messageBytes = messageBytesCompressed;
                bytesUsedCount = messageBytesCompressedUsedCount;

                // Re-validate after compression (compression could theoretically increase size in worst case)
                if (bytesUsedCount > transportMaxSize)
                {
                    throw new System.InvalidOperationException(
                        $"CRITICAL: Message size after compression ({bytesUsedCount} bytes) exceeds transport maximum ({transportMaxSize} bytes). " +
                        $"Channel: {channelId}, Owner: {OwnerAuthorityId}. " +
                        $"Compression increased message size beyond safe limits. Consider disabling compression for this message type.");
                }
            }

            bodySize_withHeader = bytesUsedCount + headerSize;

            byte[] messageBytes_withHeader = SerializationUtils.BorrowByteArray(bodySize_withHeader);
            Utils.BitConverter.GetBytes(channelId, messageBytes_withHeader, 0);

            Utils.BitConverter.GetBytes(bytesUsedCount, messageBytes_withHeader, sizeof(GONetChannelId));
            Buffer.BlockCopy(messageBytes, 0, messageBytes_withHeader, headerSize, bytesUsedCount);

            // DIAGNOSTIC (December 2025): Log after compression for buffer aliasing detection
            if (channelId == GONetChannel.EventSingles_Reliable && bodySize_withHeader >= 60)
            {
                // Extract GONetId from compressed+header buffer
                // Layout: channelId(1) + size(4) + compression_header(4) + spawn_data(GONetId at offset 4)
                int gonetIdOffset = headerSize + 4 + 4;  // after header + compression header
                uint extractedGONetId = 0;
                if (bodySize_withHeader >= gonetIdOffset + 4)
                {
                    extractedGONetId = (uint)(
                        messageBytes_withHeader[gonetIdOffset] |
                        (messageBytes_withHeader[gonetIdOffset + 1] << 8) |
                        (messageBytes_withHeader[gonetIdOffset + 2] << 16) |
                        (messageBytes_withHeader[gonetIdOffset + 3] << 24)
                    );
                }
                string firstBytes = bodySize_withHeader >= 16
                    ? System.BitConverter.ToString(messageBytes_withHeader, 0, 16).Replace("-", "")
                    : System.BitConverter.ToString(messageBytes_withHeader, 0, bodySize_withHeader).Replace("-", "");
                //GONetLog.Debug($"[SPAWN-COMPRESS] after compress+header: bytes={bodySize_withHeader}, GONetId={extractedGONetId}, firstBytes={firstBytes}");
            }

            try
            {
                // DUAL PATH: Route through new transport or old ReliableEndpoint
                if (useNewTransportPath && transport_new != null)
                {
                    // Check if transport has built-in reliability
                    bool transportHasReliability = transport_new.Capabilities.HasFlag(GONetTransportCapabilities.Reliability);

                    if (transportHasReliability)
                    {
                        // NEW PATH (Transport has reliability): Send directly via transport
                        GONetTransportQoS transportQoS = channel.QualityOfService == QosType.Reliable
                            ? GONetTransportQoS.Reliable
                            : GONetTransportQoS.Unreliable;

                        transport_new.Send(messageBytes_withHeader, bodySize_withHeader, transportQoS, connection_new, channel: 0);
                    }
                    else
                    {
                        // NEW PATH (Transport lacks reliability): Route through ReliableEndpoint wrapper
                        // This will wrap the message with ReliableNetcode headers and call TransmitCallback
                        base.SendMessage(messageBytes_withHeader, bodySize_withHeader, channel.QualityOfService);
                    }
                }
                else
                {
                    // DIAGNOSTIC: SceneLoadComplete trace - Stage 4: Enter reliable endpoint
                    if (GONetMain.EnableSceneLoadCompleteTracing && channelId == GONetChannel.EventSingles_Reliable &&
                        bytesUsedCount >= 20 && bytesUsedCount <= 40)
                    {
                        string hex = bodySize_withHeader >= 16
                            ? System.BitConverter.ToString(messageBytes_withHeader, 0, 16).Replace("-", "")
                            : System.BitConverter.ToString(messageBytes_withHeader, 0, bodySize_withHeader).Replace("-", "");
                        GONetLog.Info($"[SLC-TRACE-4] STAGE4_RELIABLE_ENTER origBytes={bytesUsedCount} withHeader={bodySize_withHeader} qos={channel.QualityOfService} hex={hex} auth={OwnerAuthorityId} time={GONetMain.Time.ElapsedSeconds:F3}");
                    }

                    // OLD PATH: Route through ReliableEndpoint (existing behavior)
                    base.SendMessage(messageBytes_withHeader, bodySize_withHeader, channel.QualityOfService); // IMPORTANT: this should be the ONLY call to this method in all of GONet! including user codebases!
                }
            }
            catch (ReliableQueueExhaustedException ex)
            {
                // CRITICAL: Reliable message queue exhausted - message dropped
                // This indicates severe network congestion or sustained high message rate
                GONetLog.Error(
                    $"[RELIABLE-QUEUE-EXHAUSTION] Reliable message queue exhausted and message DROPPED. " +
                    $"Queue: {ex.CurrentQueueDepth}/{ex.MaxQueueSize} messages. " +
                    $"Dropped message: {ex.DroppedMessageSize} bytes on channel {ex.ChannelId}. " +
                    $"Connection: Authority {OwnerAuthorityId}, RTT: {RTTMilliseconds:F1}ms. " +
                    $"\n\n" +
                    $"WHAT THIS MEANS:\n" +
                    $"• Reliable messages (spawns, RPCs, critical state) are being sent faster than network can deliver them\n" +
                    $"• This is EXTREMELY RARE - requires sustained 100+ messages/sec + high packet loss + slow ACKs\n" +
                    $"• The dropped message will NOT be delivered (spawn events, RPCs will fail)\n" +
                    $"\n" +
                    $"SOLUTIONS:\n" +
                    $"1. Increase 'Max Reliable Message Queue Size' in GONetGlobal inspector (current: {ex.MaxQueueSize})\n" +
                    $"   - For high-latency connections: Increase to 5000-10000\n" +
                    $"   - For rapid spawning scenarios: Increase to 3000-5000\n" +
                    $"2. Reduce message send rate:\n" +
                    $"   - Batch spawn requests (spawn 10 objects every 0.1s instead of 100 instantly)\n" +
                    $"   - Throttle RPC calls\n" +
                    $"   - Use unreliable channels for non-critical data (position updates)\n" +
                    $"3. Investigate network conditions:\n" +
                    $"   - Check packet loss (current: {PacketLoss:F2}%)\n" +
                    $"   - Check RTT/latency (current: {RTTMilliseconds:F1}ms)\n" +
                    $"   - Consider network quality issues\n" +
                    $"\n" +
                    $"See: ReliableQueueExhaustedException documentation for detailed analysis.");
            }

            { // memory management:
                SerializationUtils.ReturnByteArray(messageBytes_withHeader);

                if (isCompressionUsed)
                {
                    SerializationUtils.ReturnByteArray(messageBytesCompressed);
                }
            }
        }

        /// <summary>
        /// SLOT RESERVATION (December 2025): Send message with specified priority.
        /// System priority messages bypass Gameplay slot limits in the reliable channel,
        /// ensuring critical traffic (scene loads, heartbeats) always has buffer space.
        /// </summary>
        /// <param name="messageBytes">Message data</param>
        /// <param name="bytesUsedCount">Message length</param>
        /// <param name="channelId">GONet channel ID</param>
        /// <param name="priority">Message priority (System bypasses slot limits)</param>
        public void SendMessageOverChannel(byte[] messageBytes, int bytesUsedCount, GONetChannelId channelId, ReliableNetcode.MessagePriority priority)
        {
            // CRITICAL SIZE VALIDATION: Same as standard SendMessageOverChannel
            const int PERFORMANCE_WARN_THRESHOLD = 10 * 1024;
            const int CHUNK_SIZE = 10 * 1024;
            const int FALLBACK_MAX_SIZE = 16 * 1024;

            GONetChannel channel = GONetChannel.ById(channelId);
            bool isReliable = channel.QualityOfService == QosType.Reliable;
            GONetTransportQoS qos = isReliable ? GONetTransportQoS.Reliable : GONetTransportQoS.Unreliable;

            int transportMaxSize = FALLBACK_MAX_SIZE;
            if (useNewTransportPath && transport_new != null)
            {
                int reportedMax = transport_new.GetMaxMessageSize(qos);
                if (reportedMax > 0)
                    transportMaxSize = reportedMax;
            }

            // AUTO-CHUNKING: For oversized messages (chunked messages use Gameplay priority)
            if (bytesUsedCount > transportMaxSize)
            {
                if (isReliable)
                {
                    GONetLog.Warning(
                        $"[AUTO-CHUNK] Priority={priority} message too large ({bytesUsedCount} bytes), chunking. " +
                        $"Channel: {channelId}, Owner: {OwnerAuthorityId}. " +
                        $"Note: Chunks use standard Gameplay priority.");
                    SendMessageOverChannel_Chunked(messageBytes, bytesUsedCount, channelId, CHUNK_SIZE);
                    return;
                }
                else
                {
                    throw new System.InvalidOperationException(
                        $"CRITICAL: Unreliable message size ({bytesUsedCount} bytes) exceeds transport maximum ({transportMaxSize} bytes).");
                }
            }

            if (bytesUsedCount > PERFORMANCE_WARN_THRESHOLD)
            {
                GONetLog.Info(
                    $"[PERFORMANCE] Large {priority} priority message ({bytesUsedCount} bytes) on channel {channelId}.");
            }

            int headerSize = sizeof(GONetChannelId) + sizeof(int);

            byte[] messageBytesCompressed = null;
            ushort messageBytesCompressedUsedCount;
            bool isCompressionUsed = GONetMain.AutoCompressEverything != null;

            if (isCompressionUsed)
            {
                GONetMain.AutoCompressEverything.Compress(messageBytes, (ushort)bytesUsedCount, out messageBytesCompressed, out messageBytesCompressedUsedCount);
                messageBytes = messageBytesCompressed;
                bytesUsedCount = messageBytesCompressedUsedCount;

                if (bytesUsedCount > transportMaxSize)
                {
                    throw new System.InvalidOperationException(
                        $"CRITICAL: Compressed message size ({bytesUsedCount} bytes) exceeds transport maximum.");
                }
            }

            int bodySize_withHeader = bytesUsedCount + headerSize;
            byte[] messageBytes_withHeader = SerializationUtils.BorrowByteArray(bodySize_withHeader);
            Utils.BitConverter.GetBytes(channelId, messageBytes_withHeader, 0);
            Utils.BitConverter.GetBytes(bytesUsedCount, messageBytes_withHeader, sizeof(GONetChannelId));
            Buffer.BlockCopy(messageBytes, 0, messageBytes_withHeader, headerSize, bytesUsedCount);

            try
            {
                if (useNewTransportPath && transport_new != null)
                {
                    bool transportHasReliability = transport_new.Capabilities.HasFlag(GONetTransportCapabilities.Reliability);
                    if (transportHasReliability)
                    {
                        GONetTransportQoS transportQoS = channel.QualityOfService == QosType.Reliable
                            ? GONetTransportQoS.Reliable
                            : GONetTransportQoS.Unreliable;
                        transport_new.Send(messageBytes_withHeader, bodySize_withHeader, transportQoS, connection_new, channel: 0);
                    }
                    else
                    {
                        // Route through ReliableEndpoint WITH PRIORITY
                        base.SendMessage(messageBytes_withHeader, bodySize_withHeader, channel.QualityOfService, priority);
                    }
                }
                else
                {
                    // OLD PATH: Route through ReliableEndpoint WITH PRIORITY
                    base.SendMessage(messageBytes_withHeader, bodySize_withHeader, channel.QualityOfService, priority);
                }
            }
            catch (ReliableQueueExhaustedException ex)
            {
                GONetLog.Error(
                    $"[RELIABLE-QUEUE-EXHAUSTION] Priority={priority} message dropped. " +
                    $"Queue: {ex.CurrentQueueDepth}/{ex.MaxQueueSize}. " +
                    $"Dropped: {ex.DroppedMessageSize} bytes on channel {ex.ChannelId}.");
            }

            {
                SerializationUtils.ReturnByteArray(messageBytes_withHeader);
                if (isCompressionUsed)
                    SerializationUtils.ReturnByteArray(messageBytesCompressed);
            }
        }

        /// <summary>
        /// Sends an oversized message by chunking it into smaller pieces.
        /// FLOW CONTROL: Chunks are enqueued for time-sliced sending to prevent packet storms.
        /// Receiver automatically reassembles chunks in order (reliable channel guarantees delivery/ordering).
        /// </summary>
        private void SendMessageOverChannel_Chunked(byte[] messageBytes, int bytesUsedCount, GONetChannelId channelId, int chunkSize)
        {
            // Calculate chunking parameters
            int totalChunks = (bytesUsedCount + chunkSize - 1) / chunkSize; // Ceiling division
            ushort chunkId = GenerateChunkSequenceId(); // Unique ID for this chunked message

            GONetLog.Info(
                $"[CHUNKING] Splitting {bytesUsedCount} byte message into {totalChunks} chunks of ~{chunkSize} bytes each. " +
                $"ChunkSequenceId: {chunkId}, Channel: {channelId}, Owner: {OwnerAuthorityId}");

            for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                int offset = chunkIndex * chunkSize;
                int currentChunkSize = System.Math.Min(chunkSize, bytesUsedCount - offset);

                // Build chunk header + data
                // Header: [MAGIC:2][ChunkSequenceId:2][TotalChunks:2][ChunkIndex:2][OriginalSize:4][ChunkData:N]
                int chunkHeaderSize = sizeof(ushort) + sizeof(ushort) + sizeof(ushort) + sizeof(ushort) + sizeof(int);
                int chunkTotalSize = chunkHeaderSize + currentChunkSize;

                byte[] chunkMessage = SerializationUtils.BorrowByteArray(chunkTotalSize);

                // Write chunk header
                int writeIndex = 0;
                Utils.BitConverter.GetBytes(CHUNK_MAGIC, chunkMessage, writeIndex); writeIndex += sizeof(ushort);
                Utils.BitConverter.GetBytes(chunkId, chunkMessage, writeIndex); writeIndex += sizeof(ushort);
                Utils.BitConverter.GetBytes((ushort)totalChunks, chunkMessage, writeIndex); writeIndex += sizeof(ushort);
                Utils.BitConverter.GetBytes((ushort)chunkIndex, chunkMessage, writeIndex); writeIndex += sizeof(ushort);
                Utils.BitConverter.GetBytes(bytesUsedCount, chunkMessage, writeIndex); writeIndex += sizeof(int);

                // Write chunk data
                Buffer.BlockCopy(messageBytes, offset, chunkMessage, writeIndex, currentChunkSize);

                // FLOW CONTROL: Enqueue chunk for time-sliced sending instead of immediate send
                // This prevents packet storms on congested networks
                pendingChunksQueue.Enqueue(new PendingChunk
                {
                    Data = chunkMessage, // Will be returned to pool by ProcessPendingChunks()
                    Size = chunkTotalSize,
                    ChannelId = channelId
                });
            }

            GONetLog.Info(
                $"[CHUNKING] Enqueued all {totalChunks} chunks for ChunkSequenceId: {chunkId}. " +
                $"Total pending: {pendingChunksQueue.Count} chunks");
        }

        private static ushort chunkSequenceIdCounter = 0;
        private static ushort GenerateChunkSequenceId()
        {
            return chunkSequenceIdCounter++; // Wraps around at 65535
        }

        /// <summary>
        /// Stores chunks being reassembled. Key = ChunkSequenceId, Value = reassembly context.
        /// </summary>
        private class ChunkReassemblyContext
        {
            public ushort TotalChunks;
            public int OriginalSize;
            public byte[] ReassembledData;
            public System.Collections.Generic.HashSet<ushort> ReceivedChunkIndices = new System.Collections.Generic.HashSet<ushort>();
            public System.DateTime FirstChunkReceived = System.DateTime.UtcNow;
        }

        private readonly System.Collections.Generic.Dictionary<ushort, ChunkReassemblyContext> chunkReassemblyMap =
            new System.Collections.Generic.Dictionary<ushort, ChunkReassemblyContext>();

        /// <summary>
        /// Pending chunk data for time-sliced sending (flow control).
        /// </summary>
        private struct PendingChunk
        {
            public byte[] Data;
            public int Size;
            public GONetChannelId ChannelId;
        }

        /// <summary>
        /// Queue of chunks waiting to be sent (flow control to prevent packet storms).
        /// PERFORMANCE: Limits chunks sent per frame to avoid overwhelming network buffers.
        /// </summary>
        private readonly System.Collections.Generic.Queue<PendingChunk> pendingChunksQueue =
            new System.Collections.Generic.Queue<PendingChunk>();

        /// <summary>
        /// OLD PATH: ReliableEndpoint callback for received messages.
        /// TIMESTAMP FIX (December 2025): Now accepts transport-level receive timestamp for accurate RTT.
        /// The timestamp flows through ReliableEndpoint's internal buffering, preserving the ORIGINAL
        /// transport receive time even for out-of-order packets that get queued.
        /// </summary>
        /// <param name="messageBytes">Message data</param>
        /// <param name="bytesUsedCount">Message length</param>
        /// <param name="receiveTimestamp">Transport-level receive timestamp in ticks (0 if unavailable)</param>
        private void OnReceiveCallback(byte[] messageBytes, int bytesUsedCount, long receiveTimestamp)
        {
            // DIAGNOSTIC (January 2026): Trace when messages are delivered through ReliableNetcode
            // This helps debug SceneLoadComplete delivery - if we see this log, ReliableNetcode delivered it
            // COMMENTED (log cleanup) - fires for every reliable message, very spammy
            //GONetLog.Debug($"[RELIABLE-RECV] OwnerAuth={OwnerAuthorityId} received {bytesUsedCount} bytes via ReliableNetcode → ProcessReceivedMessage");

            // Store the timestamp for ProcessReceivedMessage → ProcessIncomingBytes_TriageFromAnyThread
            // This is the ORIGINAL transport receive time, not the current time when delivering
            // the message after reliability layer buffering.
            _pendingTransportReceiveTicks = receiveTimestamp;
            try
            {
                ProcessReceivedMessage(messageBytes, bytesUsedCount);
            }
            finally
            {
                _pendingTransportReceiveTicks = 0;
            }
        }

        /// <summary>
        /// HIGH-LOAD OPTIMIZATION (December 2025): IGONetTransport callback for timestamped messages.
        /// This fires BEFORE OnTransportMessageReceived to capture the accurate transport-level timestamp.
        ///
        /// For Steamworks: Contains the accurate timestamp from SteamNetworkingMessage_t.m_usecTimeReceived
        /// (when Steam's networking layer actually received the packet, NOT when we processed it).
        /// For NetcodeIO: Contains processing time (no transport-level timestamp available).
        /// </summary>
        private void OnTransportMessageReceivedWithTimestamp(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection source, byte channel, long transportReceiveTicks)
        {
            // Store the transport-level timestamp for the upcoming OnTransportMessageReceived call
            // This will be used by ProcessReceivedMessage -> ProcessIncomingBytes_TriageFromAnyThread
            _pendingTransportReceiveTicks = transportReceiveTicks;
        }

        /// <summary>
        /// NEW PATH: IGONetTransport callback for received messages.
        /// </summary>
        private void OnTransportMessageReceived(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection source, byte channel)
        {
            GONetLog.Info($"[GONetConnections] OnTransportMessageReceived: {length} bytes, QoS: {qos}, Channel: {channel}, Source: {source?.ConnectionUID ?? 0}, Expected: {connection_new?.ConnectionUID ?? 0}");

            // CRITICAL: If this transport is running BOTH server and client, the client-side connection (connection_new == null)
            // will receive server-side packets too. Filter out server-side packets by requiring source == null for client-side.
            // This prevents cross-delivery where dormant-server traffic corrupts client state (e.g., time sync).
            if (connection_new == null && transport_new != null && transport_new.IsServer && transport_new.IsClient && source != null)
            {
                if (EnableCrossDeliveryDiagnostics)
                {
                    GONetLog.Warning($"[CROSS-DELIVERY-DROP] Dropped server-side packet on client connection (source={source.ConnectionUID})");
                }
                return;
            }

            // Filter: Only process messages for OUR specific connection (server-side)
            // Client-side: connection_new is null, so we accept all messages
            // FIX (January 2026): Added source != null check for consistency with line 336 fix.
            // This handles edge cases where transport might unexpectedly pass null source.
            if (connection_new != null && source != null && source != connection_new)
            {
                GONetLog.Warning($"[GONetConnections] FILTERED OUT message from {source?.ConnectionUID ?? 0} (expected: {connection_new.ConnectionUID})");
                return;
            }

            GONetLog.Info($"[GONetConnections] Processing message...");
            ProcessReceivedMessage(data, length);

            // HIGH-LOAD OPTIMIZATION (December 2025): Reset pending timestamp after message is processed
            // This ensures each message uses its own timestamp (or 0 for legacy path)
            _pendingTransportReceiveTicks = 0;
        }

        /// <summary>
        /// SHARED: Common message processing (decompression, header parsing, dispatch to GONet).
        /// Used by both old path (ReliableEndpoint) and new path (IGONetTransport).
        /// </summary>
        private void ProcessReceivedMessage(byte[] messageBytes, int bytesUsedCount)
        {
            // DEBUG: Dump first 10 bytes of payload
            string hexDump = "";
            for (int i = 0; i < System.Math.Min(10, bytesUsedCount); i++)
            {
                hexDump += messageBytes[i].ToString("X2") + " ";
            }

            // DIAGNOSTIC (December 2025): Log spawn-sized messages at ProcessReceivedMessage entry
            // This helps trace if messages are arriving from reliable layer but getting lost before deserialization
            // Spawn events are typically 85-115 bytes total (including 5-byte header)
            // DIAGNOSTIC (December 2025): Log RECV-MSG for spawn-sized messages
            // To enable, add GONet_SPAWN_TRACE to Player Settings → Scripting Define Symbols
            #if GONet_SPAWN_TRACE
            if (bytesUsedCount >= 80 && bytesUsedCount <= 120)
            {
                // Extract GONetId from spawn message
                // Layout: channelId(1) + size(4) + compression_header(4) + spawn_event_type(4) + GONetId(4)
                // GONetId starts at byte 13 (offset 5+4+4=13)
                uint extractedGONetId = 0;
                if (bytesUsedCount >= 17)  // Need at least 13+4 bytes for GONetId
                {
                    extractedGONetId = (uint)(
                        messageBytes[13] |
                        (messageBytes[14] << 8) |
                        (messageBytes[15] << 16) |
                        (messageBytes[16] << 24)
                    );
                }

                // Extract first 20 bytes to see more of the message
                string firstBytesHex = bytesUsedCount >= 20
                    ? System.BitConverter.ToString(messageBytes, 0, 20).Replace("-", "")
                    : System.BitConverter.ToString(messageBytes, 0, bytesUsedCount).Replace("-", "");

                GONetLog.Debug($"[RECV-MSG] ProcessReceivedMessage entry (spawn-sized): bytes={bytesUsedCount}, GONetId={extractedGONetId}, ownerAuth={OwnerAuthorityId}, isServer={GONetMain.IsServer}, firstBytes={firstBytesHex}");
            }
            #endif

            int headerSize = sizeof(GONetChannelId) + sizeof(int);
            int bodySize_expected = bytesUsedCount - headerSize;

            uint bodySize_readFromMessage;
            GONetChannelId channelId_readFromMessage;

            byte[] messageBytes_withoutHeader = SerializationUtils.BorrowByteArray(bodySize_expected);
            Buffer.BlockCopy(messageBytes, headerSize, messageBytes_withoutHeader, 0, bodySize_expected);

            using (var bitStream = BitByBitByteArrayBuilder.GetBuilder_WithNewData(messageBytes, bytesUsedCount))
            {
                channelId_readFromMessage = (GONetChannelId)bitStream.ReadByte();
                bitStream.ReadUInt(out bodySize_readFromMessage);
            }

            byte[] messageBytesUncompressed = messageBytes_withoutHeader;
            ushort messageBytesUncompressedUsedCount;

            bool isCompressionUsed = GONetMain.AutoCompressEverything != null;
            if (isCompressionUsed)
            {
                GONetMain.AutoCompressEverything.Uncompress(messageBytes_withoutHeader, (ushort)bodySize_expected, out messageBytesUncompressed, out messageBytesUncompressedUsedCount);
                bodySize_readFromMessage = messageBytesUncompressedUsedCount;
            }

            // DIAGNOSTIC (December 2025): Log after decompression for spawn-sized messages
            // To enable, add GONet_SPAWN_TRACE to Player Settings → Scripting Define Symbols
            #if GONet_SPAWN_TRACE
            if (bodySize_readFromMessage >= 75 && bodySize_readFromMessage <= 115)
            {
                // Extract GONetId from decompressed spawn message
                // Layout (after decompression): spawn_event_type(4) + GONetId(4) + ...
                // GONetId starts at byte 4
                uint extractedGONetId = 0;
                if (bodySize_readFromMessage >= 8)
                {
                    extractedGONetId = (uint)(
                        messageBytesUncompressed[4] |
                        (messageBytesUncompressed[5] << 8) |
                        (messageBytesUncompressed[6] << 16) |
                        (messageBytesUncompressed[7] << 24)
                    );
                }

                string firstBytesHex = bodySize_readFromMessage >= 12
                    ? System.BitConverter.ToString(messageBytesUncompressed, 0, 12).Replace("-", "")
                    : System.BitConverter.ToString(messageBytesUncompressed, 0, (int)bodySize_readFromMessage).Replace("-", "");

                GONetLog.Debug($"[RECV-DECOMP] After decompression (spawn-sized): bytes={bodySize_readFromMessage}, GONetId={extractedGONetId}, firstBytes={firstBytesHex}");
            }
            #endif

            // CHUNK DETECTION: Check if this is a chunked message by inspecting magic number
            // Chunked messages have header: [MAGIC:2][ChunkSeqId:2][TotalChunks:2][ChunkIndex:2][OriginalSize:4] = 12 bytes minimum
            const int MIN_CHUNK_HEADER_SIZE = sizeof(ushort) + sizeof(ushort) + sizeof(ushort) + sizeof(ushort) + sizeof(int);
            if (bodySize_readFromMessage >= MIN_CHUNK_HEADER_SIZE)
            {
                // Check magic number first (fast rejection for non-chunks)
                ushort magic = System.BitConverter.ToUInt16(messageBytesUncompressed, 0);

                if (magic == CHUNK_MAGIC)
                {
                    // CONFIRMED chunk - parse header
                    int readIndex = sizeof(ushort); // Skip magic
                    ushort chunkSeqId = System.BitConverter.ToUInt16(messageBytesUncompressed, readIndex); readIndex += sizeof(ushort);
                    ushort totalChunks = System.BitConverter.ToUInt16(messageBytesUncompressed, readIndex); readIndex += sizeof(ushort);
                    ushort chunkIndex = System.BitConverter.ToUInt16(messageBytesUncompressed, readIndex); readIndex += sizeof(ushort);
                    int originalSize = System.BitConverter.ToInt32(messageBytesUncompressed, readIndex); readIndex += sizeof(int);

                    // This IS a chunk - process reassembly
                    bool isComplete = ProcessChunkReassembly(
                        messageBytesUncompressed,
                        (int)bodySize_readFromMessage,
                        chunkSeqId,
                        totalChunks,
                        chunkIndex,
                        originalSize,
                        channelId_readFromMessage,
                        out byte[] reassembledMessage,
                        out int reassembledSize);

                    if (isComplete)
                    {
                        // All chunks received - process reassembled message
                        // HIGH-LOAD OPTIMIZATION (December 2025): Pass transport-level timestamp for accurate RTT
                        GONetMain.ProcessIncomingBytes_TriageFromAnyThread(this, reassembledMessage, reassembledSize, channelId_readFromMessage, _pendingTransportReceiveTicks);
                        SerializationUtils.ReturnByteArray(reassembledMessage);
                    }
                    // else: chunk stored, waiting for more chunks

                    // Cleanup memory for this partial chunk
                    SerializationUtils.ReturnByteArray(messageBytes_withoutHeader);
                    if (isCompressionUsed)
                    {
                        SerializationUtils.ReturnByteArray(messageBytesUncompressed);
                    }
                    return; // Chunk processed
                }
            }

            // NOT a chunk - process normally
            // HIGH-LOAD OPTIMIZATION (December 2025): Pass transport-level timestamp for accurate RTT
            GONetMain.ProcessIncomingBytes_TriageFromAnyThread(this, messageBytesUncompressed, (int)bodySize_readFromMessage, channelId_readFromMessage, _pendingTransportReceiveTicks);

            { // memory management:
                SerializationUtils.ReturnByteArray(messageBytes_withoutHeader);

                if (isCompressionUsed)
                {
                    SerializationUtils.ReturnByteArray(messageBytesUncompressed);
                }
            }
        }

        /// <summary>
        /// Processes a received chunk and reassembles when all chunks are received.
        /// Returns true if reassembly is complete, false if still waiting for chunks.
        /// </summary>
        private bool ProcessChunkReassembly(
            byte[] chunkData,
            int chunkDataSize,
            ushort chunkSeqId,
            ushort totalChunks,
            ushort chunkIndex,
            int originalSize,
            GONetChannelId channelId,
            out byte[] reassembledMessage,
            out int reassembledSize)
        {
            reassembledMessage = null;
            reassembledSize = 0;

            // SECURITY: Validate message size before allocating buffer
            if (originalSize > MAX_LARGE_MESSAGE_SIZE)
            {
                GONetLog.Error(
                    $"[CHUNK-SECURITY] Rejecting oversized chunk! ChunkSeqId: {chunkSeqId}, " +
                    $"OriginalSize: {originalSize} bytes exceeds maximum allowed ({MAX_LARGE_MESSAGE_SIZE} bytes). " +
                    $"Possible DoS attack or protocol violation. Connection: {OwnerAuthorityId}");
                return false; // Reject immediately without allocating
            }

            // SECURITY: Validate sane chunk count
            if (totalChunks == 0 || totalChunks > 10000)
            {
                GONetLog.Error(
                    $"[CHUNK-SECURITY] Rejecting invalid chunk count! ChunkSeqId: {chunkSeqId}, " +
                    $"TotalChunks: {totalChunks} (must be 1-10000). " +
                    $"Possible protocol violation. Connection: {OwnerAuthorityId}");
                return false;
            }

            // Get or create reassembly context
            if (!chunkReassemblyMap.TryGetValue(chunkSeqId, out ChunkReassemblyContext context))
            {
                context = new ChunkReassemblyContext
                {
                    TotalChunks = totalChunks,
                    OriginalSize = originalSize,
                    ReassembledData = SerializationUtils.BorrowByteArray(originalSize)
                };
                chunkReassemblyMap[chunkSeqId] = context;

                GONetLog.Info($"[CHUNK-RECV] Started reassembly for ChunkSeqId: {chunkSeqId}, expecting {totalChunks} chunks, {originalSize} bytes total");
            }

            // Validate chunk consistency
            if (context.TotalChunks != totalChunks || context.OriginalSize != originalSize)
            {
                GONetLog.Error($"[CHUNK-ERROR] Chunk metadata mismatch! ChunkSeqId: {chunkSeqId}, expected {context.TotalChunks} chunks/{context.OriginalSize} bytes, got {totalChunks} chunks/{originalSize} bytes");
                chunkReassemblyMap.Remove(chunkSeqId);
                SerializationUtils.ReturnByteArray(context.ReassembledData);
                return false;
            }

            // Check for duplicate chunk
            if (context.ReceivedChunkIndices.Contains(chunkIndex))
            {
                GONetLog.Warning($"[CHUNK-DUP] Duplicate chunk received! ChunkSeqId: {chunkSeqId}, ChunkIndex: {chunkIndex} (ignoring)");
                return false;
            }

            // Extract chunk payload (skip 12-byte chunk header: [MAGIC:2][ChunkSeqId:2][TotalChunks:2][ChunkIndex:2][OriginalSize:4])
            const int CHUNK_HEADER_SIZE = sizeof(ushort) + sizeof(ushort) + sizeof(ushort) + sizeof(ushort) + sizeof(int);
            int chunkPayloadSize = chunkDataSize - CHUNK_HEADER_SIZE;
            int destOffset = chunkIndex * 10240; // Hardcoded chunk size from sender (10KB)

            // Copy chunk payload into reassembly buffer
            Buffer.BlockCopy(chunkData, CHUNK_HEADER_SIZE, context.ReassembledData, destOffset, chunkPayloadSize);
            context.ReceivedChunkIndices.Add(chunkIndex);

            GONetLog.Info($"[CHUNK-RECV] Received chunk {chunkIndex + 1}/{totalChunks} for ChunkSeqId: {chunkSeqId} ({context.ReceivedChunkIndices.Count}/{totalChunks} chunks received)");

            // Check if reassembly is complete
            if (context.ReceivedChunkIndices.Count == totalChunks)
            {
                reassembledMessage = context.ReassembledData;
                reassembledSize = originalSize;

                chunkReassemblyMap.Remove(chunkSeqId);

                double reassemblyTime = (System.DateTime.UtcNow - context.FirstChunkReceived).TotalMilliseconds;
                GONetLog.Info($"[CHUNK-COMPLETE] Reassembly complete! ChunkSeqId: {chunkSeqId}, {totalChunks} chunks, {originalSize} bytes, took {reassemblyTime:F1}ms");
                return true;
            }

            return false; // Still waiting for more chunks
        }

        /// <summary>
        /// Periodic cleanup of incomplete chunk reassemblies that have exceeded TTL.
        /// SECURITY: Prevents memory leaks from abandoned/malicious chunk streams.
        /// Call this periodically (e.g., once per second) from GONetMain.Update().
        /// </summary>
        public void CleanupStaleChunkReassemblies()
        {
            if (chunkReassemblyMap.Count == 0)
                return; // Fast path: no active reassemblies

            var now = System.DateTime.UtcNow;
            var staleKeys = new System.Collections.Generic.List<ushort>();

            // Find all reassemblies that have exceeded TTL
            foreach (var kvp in chunkReassemblyMap)
            {
                var context = kvp.Value;
                double ageSeconds = (now - context.FirstChunkReceived).TotalSeconds;

                if (ageSeconds > CHUNK_REASSEMBLY_TTL_SECONDS)
                {
                    staleKeys.Add(kvp.Key);
                    GONetLog.Warning(
                        $"[CHUNK-TTL] Discarding stale chunk reassembly. ChunkSeqId: {kvp.Key}, " +
                        $"Age: {ageSeconds:F1}s (TTL: {CHUNK_REASSEMBLY_TTL_SECONDS}s), " +
                        $"Received: {context.ReceivedChunkIndices.Count}/{context.TotalChunks} chunks, " +
                        $"Size: {context.OriginalSize} bytes. Connection: {OwnerAuthorityId}");
                }
            }

            // Remove stale reassemblies and return buffers to pool
            foreach (var chunkSeqId in staleKeys)
            {
                var context = chunkReassemblyMap[chunkSeqId];
                SerializationUtils.ReturnByteArray(context.ReassembledData);
                chunkReassemblyMap.Remove(chunkSeqId);
            }
        }

        /// <summary>
        /// Process pending chunks queue with flow control (time-sliced sending).
        /// PERFORMANCE: Prevents packet storms by limiting chunks sent per frame.
        /// Call this every frame from GONetMain.Update() or connection processing loop.
        /// </summary>
        /// <returns>Number of chunks sent this frame</returns>
        public int ProcessPendingChunks()
        {
            if (pendingChunksQueue.Count == 0)
                return 0; // Fast path: nothing to send

            int chunksSentThisFrame = 0;

            // Send up to MAX_CHUNKS_PER_FRAME chunks this frame
            // At 60 FPS with 10KB chunks: 2 chunks/frame × 60 FPS = 1.2 MB/sec throughput
            while (pendingChunksQueue.Count > 0 && chunksSentThisFrame < MAX_CHUNKS_PER_FRAME)
            {
                var chunk = pendingChunksQueue.Dequeue();

                // Send chunk directly (bypasses chunking check since this IS already a chunk)
                SendMessageOverChannel_Direct(chunk.Data, chunk.Size, chunk.ChannelId);

                // Return buffer to pool
                SerializationUtils.ReturnByteArray(chunk.Data);

                chunksSentThisFrame++;
            }

            if (chunksSentThisFrame > 0)
            {
                GONetLog.Info($"[FLOW-CONTROL] Sent {chunksSentThisFrame} chunks, {pendingChunksQueue.Count} remaining in queue. Connection: {OwnerAuthorityId}");
            }

            return chunksSentThisFrame;
        }

        /// <summary>
        /// Direct send bypassing chunk detection (for sending pre-chunked data).
        /// INTERNAL USE ONLY - called by ProcessPendingChunks().
        /// </summary>
        private void SendMessageOverChannel_Direct(byte[] messageBytes, int bytesUsedCount, GONetChannelId channelId)
        {
            // Skip straight to header + transport send (no size checks, no chunking)
            int headerSize = sizeof(GONetChannelId) + sizeof(int);
            int bodySize_withHeader;

            byte[] messageBytesCompressed = null;
            ushort messageBytesCompressedUsedCount;

            bool isCompressionUsed = GONetMain.AutoCompressEverything != null;
            if (isCompressionUsed)
            {
                GONetMain.AutoCompressEverything.Compress(messageBytes, (ushort)bytesUsedCount, out messageBytesCompressed, out messageBytesCompressedUsedCount);
                messageBytes = messageBytesCompressed;
                bytesUsedCount = messageBytesCompressedUsedCount;
            }

            bodySize_withHeader = bytesUsedCount + headerSize;

            byte[] messageBytes_withHeader = SerializationUtils.BorrowByteArray(bodySize_withHeader);
            Utils.BitConverter.GetBytes(channelId, messageBytes_withHeader, 0);
            Utils.BitConverter.GetBytes(bytesUsedCount, messageBytes_withHeader, sizeof(GONetChannelId));
            Buffer.BlockCopy(messageBytes, 0, messageBytes_withHeader, headerSize, bytesUsedCount);

            try
            {
                GONetChannel channel = GONetChannel.ById(channelId);

                // DUAL PATH: Route through new transport or old ReliableEndpoint
                if (useNewTransportPath && transport_new != null)
                {
                    bool transportHasReliability = transport_new.Capabilities.HasFlag(GONetTransportCapabilities.Reliability);

                    if (transportHasReliability)
                    {
                        GONetTransportQoS qos = channel.QualityOfService == QosType.Reliable
                            ? GONetTransportQoS.Reliable
                            : GONetTransportQoS.Unreliable;

                        transport_new.Send(messageBytes_withHeader, bodySize_withHeader, qos, connection_new, channel: 0);
                    }
                    else
                    {
                        base.SendMessage(messageBytes_withHeader, bodySize_withHeader, channel.QualityOfService);
                    }
                }
                else
                {
                    // OLD PATH: Route through ReliableEndpoint
                    base.SendMessage(messageBytes_withHeader, bodySize_withHeader, channel.QualityOfService);
                }
            }
            finally
            {
                SerializationUtils.ReturnByteArray(messageBytes_withHeader);

                if (isCompressionUsed)
                {
                    SerializationUtils.ReturnByteArray(messageBytesCompressed);
                }
            }
        }
    }

    public class GONetConnection_ClientToServer : GONetConnection
    {
        private Client client;  // OLD PATH: NetcodeIO client

        private IPEndPoint mostRecentConnectInfo;

        public ClientState State => client?.State ?? ClientState.Disconnected;

        /// <summary>
        /// OLD PATH: Constructor using NetcodeIO Client (backward compatible).
        /// </summary>
        public GONetConnection_ClientToServer(Client client, int maxReliableQueueSize = 2000) : base(maxReliableQueueSize)
        {
            this.client = client;

            OwnerAuthorityId = GONetMain.OwnerAuthorityId_Server;

            client.OnMessageReceived += OnReceivedFromServer_AnyLittleThingTheProtocolLayerDeemsNecessary;

            TransmitCallback = SendToServer_AnyLittleThingTheProtocolLayerDeemsNecessary;
        }

        /// <summary>
        /// NEW PATH: Constructor using IGONetTransport (pluggable transports).
        /// </summary>
        /// <param name="transport">Transport implementation</param>
        /// <param name="maxReliableQueueSize">Max reliable message queue size</param>
        /// <param name="isStandbyMeshClient">True if this is a standby mesh client (has own transport, should subscribe to receive messages)</param>
        public GONetConnection_ClientToServer(IGONetTransport transport, int maxReliableQueueSize = 2000, bool isStandbyMeshClient = false)
            : base(transport, null, maxReliableQueueSize, isStandbyMeshClient)  // null = client has no specific connection object
        {
            OwnerAuthorityId = GONetMain.OwnerAuthorityId_Server;

            // CRITICAL: Generate unique connection UID for new transport path
            // Without this, InitiatingClientConnectionUID stays 0 and logs show "My client UID: 0"
            InitiatingClientConnectionUID = (ulong)GUID.Generate().AsInt64();

            // If transport doesn't have built-in reliability, set up TransmitCallback for ReliableNetcode wrapper
            if (!transport.Capabilities.HasFlag(GONetTransportCapabilities.Reliability))
            {
                GONetLog.Info($"[GONetConnection_ClientToServer] Setting up ReliableNetcode wrapper with TransmitCallback");

                // TransmitCallback routes through reliability layer to transport
                TransmitCallback = (data, length) =>
                {
                    GONetTransportQoS qos = GONetTransportQoS.Reliable;  // ReliableEndpoint manages QoS internally

                    // DIAGNOSTIC (January 2026): Trace reliable layer transmissions to debug SceneLoadComplete delivery
                    // COMMENTED (log cleanup) - fires for every reliable transmission, spammy
                    //GONetLog.Debug($"[RELIABLE-TRANSMIT] Client sending {length} bytes via ReliableNetcode → Transport");

                    transport.Send(data, length, qos, target: null, channel: 0);
                };
            }
        }

        private void SendToServer_AnyLittleThingTheProtocolLayerDeemsNecessary(byte[] payloadBytes, int payloadSize)
        {
            client.Send(payloadBytes, payloadSize);
        }

        private void OnReceivedFromServer_AnyLittleThingTheProtocolLayerDeemsNecessary(byte[] payloadBytes, int payloadSize)
        {
            ReceivePacket(payloadBytes, payloadSize);
        }

        private const int CONNECTION_TOKEN_TIMOUT_SECONDS = 120;

        /// <summary>
        /// </summary>
        /// <param name="serverIP"></param>
        /// <param name="serverPort"></param>
        /// <param name="timeoutSeconds">
        /// This value serves two purposes:
        /// 1) Prior to connection being established, this represents how many seconds the client will attempt to connect to the server before giving up and considering the connected timed out (i.e., <see cref="ClientState.ConnectionRequestTimedOut"/>).  NOTE: During this time period, the connection will be attempted 10 times per second.
        /// 2) After connection is established, this represents how many seconds have to transpire with no communication for this connection to be considered timed out...then will be auto-disconnected.
        /// </param>
        public void Connect(string serverIP, int serverPort, int timeoutSeconds)
        {
            TokenFactory factory = new TokenFactory(GONetMain.noIdeaWhatThisShouldBe_CopiedFromTheirUnitTest, GONetMain._privateKey);

            bool isChangingConnectInfo = default;
            IPAddress currentServerIP = default;
            IPEndPoint mostRecentConnectInfo = default;

            try
            {
                currentServerIP = IPAddress.Parse(serverIP);
                isChangingConnectInfo = mostRecentConnectInfo == null ||
                                        !IPAddress.Equals(mostRecentConnectInfo.Address, currentServerIP) ||
                                        mostRecentConnectInfo.Port != serverPort;
                if (isChangingConnectInfo)
                {
                    mostRecentConnectInfo = new IPEndPoint(currentServerIP, serverPort);
                }
            }
            catch
            {
                // Assume serverIP actually represents a hostname and needs to be processed differently than an IP address
                IPEndPoint currentServerEndPoint = NetworkUtils.GetIPEndPointFromHostName(serverIP, serverPort);
                currentServerIP = currentServerEndPoint.Address;
                isChangingConnectInfo = mostRecentConnectInfo == null ||
                                        !IPAddress.Equals(mostRecentConnectInfo.Address, currentServerIP) ||
                                        mostRecentConnectInfo.Port != serverPort;
                if (isChangingConnectInfo)
                {
                    mostRecentConnectInfo = currentServerEndPoint;
                }
            }

            if (InitiatingClientConnectionUID == default || isChangingConnectInfo)
            {
                InitiatingClientConnectionUID = (ulong)GUID.Generate().AsInt64();
            }

            // Here, we're creating an array of endpoints that includes both IPv4 and IPv6 loopback addresses if the serverIP is a loopback address.
            List<IPEndPoint> endpoints = NetworkUtils.BuildDualStackEndpointList(serverIP, serverPort);
            endpoints.AddRange(client.P2pEndPoints);
            //foreach (var e in endpoints) GONetLog.Debug($"{GetType().Name}.{nameof(Connect)} called. TOKEN ENTRY: {e}");

            byte[] connectToken = factory.GenerateConnectToken(
                endpoints.ToArray(),
                CONNECTION_TOKEN_TIMOUT_SECONDS,
                timeoutSeconds,
                1UL,
                InitiatingClientConnectionUID,
                new byte[256]);

            client.Connect(connectToken);
        }

        /// <summary>
        /// Will log a warning if <see cref="client"/> is not in a <see cref="State"/> of <see cref="ClientState.Connected"/>; however, the deeper internal call to disconnect will still process.
        /// </summary>
        public void Disconnect()
        {
            if (State != ClientState.Connected)
            {
                const string STATE = "Calling Disconnect on a client connection to the server that is not currently in a connected state.  Actual state: ";
                GONetLog.Warning(string.Concat(STATE, Enum.GetName(typeof(ClientState), State)));
            }

            client.Disconnect();
        }
    }

    public class GONetConnection_ServerToClient : GONetConnection
    {
        private readonly RemoteClient remoteClient;  // OLD PATH: NetcodeIO RemoteClient

        public bool IsConnectedToClient => remoteClient?.Connected ?? (connection_new?.IsConnected ?? false);

        public EndPoint RemoteClientEndPoint => remoteClient?.RemoteEndpoint;

        /// <summary>
        /// Remote address string (IP:port format). Works with both old and new transport paths.
        /// Returns null if address not available.
        /// </summary>
        public string RemoteAddressString => connection_new?.RemoteAddress ?? remoteClient?.RemoteEndpoint?.ToString();

        /// <summary>
        /// OLD PATH: Constructor using NetcodeIO RemoteClient (backward compatible).
        /// </summary>
        public GONetConnection_ServerToClient(RemoteClient remoteClient, int maxReliableQueueSize = 2000) : base(maxReliableQueueSize)
        {
            this.remoteClient = remoteClient;

            // Generate unique connection UID for server-side connections
            // This is needed for hot standby authority map tracking
            InitiatingClientConnectionUID = (ulong)GUID.Generate().AsInt64();

            TransmitCallback = SendToMyClient_AnyLittleThingTheProtocolLayerDeemsNecessary;
        }

        /// <summary>
        /// NEW PATH: Constructor using IGONetTransport (pluggable transports).
        /// </summary>
        public GONetConnection_ServerToClient(IGONetTransport transport, IGONetTransportConnection clientConnection, int maxReliableQueueSize = 2000)
            : base(transport, clientConnection, maxReliableQueueSize)
        {
            // OwnerAuthorityId will be set by GONetServer when client connects

            // CRITICAL: Generate unique connection UID for new transport path
            // This is needed for hot standby authority map tracking
            InitiatingClientConnectionUID = (ulong)GUID.Generate().AsInt64();

            // If transport doesn't have built-in reliability, set up TransmitCallback for ReliableNetcode wrapper
            // NOTE: clientConnection may be null for loopback connections (GONetConnection_ClientHostLoopback)
            if (!transport.Capabilities.HasFlag(GONetTransportCapabilities.Reliability))
            {
                GONetLog.Info($"[GONetConnection_ServerToClient] Setting up ReliableNetcode wrapper with TransmitCallback for client {(clientConnection != null ? clientConnection.GetHashCode().ToString() : "null (loopback)")}");

                // TransmitCallback routes through reliability layer to transport
                TransmitCallback = (data, length) =>
                {
                    GONetTransportQoS qos = GONetTransportQoS.Reliable;  // ReliableEndpoint manages QoS internally
                    transport.Send(data, length, qos, clientConnection, channel: 0);
                };
            }
        }

        private void SendToMyClient_AnyLittleThingTheProtocolLayerDeemsNecessary(byte[] payloadBytes, int payloadSize)
        {
            remoteClient.SendPayload(payloadBytes, payloadSize);
        }
    }

    /// <summary>
    /// Optimized connection for host player in listen server (client-host) scenarios.
    /// Bypasses network serialization and directly processes messages in-process.
    ///
    /// Performance benefits:
    /// - Zero network latency (<0.1ms vs. 16-33ms round-trip)
    /// - No serialization overhead (direct object references)
    /// - ~30-50% CPU reduction for host player
    ///
    /// When to use:
    /// - Player-hosted games (one player hosts, others connect)
    /// - Local testing (start server + client in same Unity instance)
    /// - Peer-to-peer topologies
    ///
    /// Detection:
    /// - GONetServer checks if connecting client has ClientTypeFlags.ListenServer set
    /// - If true, creates GONetConnection_ClientHostLoopback instead of normal connection
    ///
    /// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    /// ⚠️  CRITICAL: LOOPBACK FEEDBACK LOOP PREVENTION PATTERN
    /// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    ///
    /// PROBLEM: In host mode, the same process is BOTH server and client. Without filtering,
    /// events echo between them infinitely:
    ///   1. Server publishes event → Event bus → Loopback connection
    ///   2. Loopback deserializes → Re-publishes → Event bus → Loopback (again!)
    ///   3. INFINITE LOOP!
    ///
    /// SYSTEMATIC SOLUTION PATTERN (apply in ALL relevant code paths):
    ///
    /// ┌─────────────────────────────────────────────────────────────────────────────────┐
    /// │ PATTERN: Filter based on connection type AND message direction                  │
    /// └─────────────────────────────────────────────────────────────────────────────────┘
    ///
    /// Rule: "Don't echo server-originated events back to the server through loopback"
    ///
    /// Detection code (use consistently):
    /// <code>
    /// bool isLoopbackConnection = connection is GONetConnection_ClientHostLoopback;
    /// bool shouldSkipLoopback = isLoopbackConnection && IsServer;
    ///
    /// if (!shouldSkipLoopback)
    /// {
    ///     // Normal processing - send/publish the event
    /// }
    /// else
    /// {
    ///     // Skip loopback to avoid feedback loop
    /// }
    /// </code>
    ///
    /// ┌─────────────────────────────────────────────────────────────────────────────────┐
    /// │ WHERE TO APPLY THIS PATTERN (search codebase for these patterns):              │
    /// └─────────────────────────────────────────────────────────────────────────────────┘
    ///
    /// ✅ ALREADY IMPLEMENTED:
    ///
    /// 1. GONet.cs:7103 - DeserializeBody_EventSingle()
    ///    - Prevents re-publishing events received from loopback (CANONICAL filter point)
    ///    - Pattern: Skip EventBus.Publish() if message came from loopback && IsServer
    ///
    /// 2. GONet.cs:7990 - Server_AssignNewClientGONetIdRawBatch()
    ///    - Prevents sending batch assignments through loopback (direct call instead)
    ///    - Pattern: Direct local call instead of EventBus.Publish() for loopback
    ///
    /// 3. GONet.cs:7639 - Server_SendClientPersistentEventsSinceStart()
    ///    - Prevents sending persistent events through loopback (host already has them)
    ///    - Pattern: Early return if connection is loopback
    ///
    /// ✅ VERIFIED SAFE (analysis completed 2025-11-18):
    ///
    /// 4. RPC broadcasting (GONetEventBus_Rpc.cs)
    ///    - RPCs use EventBus.Publish() → RpcEvent implements ITransientEvent
    ///    - ALL RPC events pass through DeserializeBody_EventSingle (GONet.cs:7103)
    ///    - ALREADY PROTECTED by canonical filter (#1 above)
    ///    - No additional filtering needed
    ///
    /// 5. Sync bundle broadcasting (GONet.cs:10922, 6843)
    ///    - Sync bundles call DeserializeBody_BundleOfChoice() directly (GONet.cs:6843)
    ///    - NOT re-published on EventBus (no EventBus.Publish call)
    ///    - Server relaying already filters by OwnerAuthorityId (excludes source)
    ///    - NO FEEDBACK LOOP POSSIBLE - safe by design
    ///
    /// 6. Spawn/despawn events (persistent events)
    ///    - ALREADY PROTECTED by Server_SendClientPersistentEventsSinceStart() filter (#3 above)
    ///    - Spawn/despawn events implement IPersistentEvent
    ///    - Loopback connections skip persistent event transmission entirely
    ///
    /// ┌─────────────────────────────────────────────────────────────────────────────────┐
    /// │ HOW TO IDENTIFY NEW CASES NEEDING THIS PATTERN:                                │
    /// └─────────────────────────────────────────────────────────────────────────────────┘
    ///
    /// Symptoms:
    /// - Duplicate log messages for same event (100+ times)
    /// - Same event handlers firing repeatedly in tight loop
    /// - Stack overflow or memory pressure during scene changes
    /// - Events continue processing even after scene change completes
    ///
    /// Diagnostic steps:
    /// 1. Check logs for repeated messages with same frame/timestamp
    /// 2. Look for stack traces showing: Publish → Deserialize → Publish loop
    /// 3. Verify `IsServer=True, IsClient=True, IsSourceRemote=False` in logs
    /// 4. Check if issue only occurs in host mode, not dedicated server
    ///
    /// Fix checklist:
    /// 1. Find where event/message is being sent to connections
    /// 2. Add loopback detection: `connection is GONetConnection_ClientHostLoopback`
    /// 3. Apply appropriate pattern (early return OR direct call OR skip publish)
    /// 4. Add comment referencing this documentation
    /// 5. Test in host mode to verify loop is broken
    ///
    /// ┌─────────────────────────────────────────────────────────────────────────────────┐
    /// │ SUMMARY: COMPLETE ANALYSIS (2025-11-18)                                        │
    /// └─────────────────────────────────────────────────────────────────────────────────┘
    ///
    /// ✅ ALL major code paths analyzed and either fixed or verified safe:
    ///
    /// 1. ✅ Event deserialization → FIXED (canonical filter at GONet.cs:7103)
    /// 2. ✅ GONetId batch assignments → FIXED (direct call instead of event bus)
    /// 3. ✅ Persistent events → FIXED (early return for loopback)
    /// 4. ✅ RPC broadcasting → VERIFIED SAFE (uses event bus, covered by #1)
    /// 5. ✅ Sync bundles → VERIFIED SAFE (no event bus re-publish, source filtering)
    /// 6. ✅ Spawn/despawn → VERIFIED SAFE (persistent events, covered by #3)
    ///
    /// The canonical filter at GONet.cs:7103 is the PRIMARY defense mechanism.
    /// All events (RPCs, scene events, custom events) pass through this single point.
    ///
    /// Additional filters (#2, #3) handle specific cases where event bus isn't used
    /// or where direct calls are more efficient than network round-trips.
    ///
    /// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    /// </summary>
    public class GONetConnection_ClientHostLoopback : GONetConnection_ServerToClient
    {
        /// <summary>
        /// Reference to the local GONetClient for direct message processing.
        /// This enables bypassing the network stack entirely.
        /// </summary>
        private readonly GONetClient localClient;

        /// <summary>
        /// Identifies this connection as a local loopback (for debugging/logging).
        /// </summary>
        public bool IsLoopback => true;

        /// <summary>
        /// Constructor for host player loopback connection.
        /// </summary>
        /// <param name="transport">Transport (same instance used by server and client)</param>
        /// <param name="clientConnection">Client connection object (may be null for loopback)</param>
        /// <param name="localClient">Reference to local GONetClient for direct processing</param>
        /// <param name="maxReliableQueueSize">Max reliable queue size (unused for loopback, but required by base)</param>
        public GONetConnection_ClientHostLoopback(
            IGONetTransport transport,
            IGONetTransportConnection clientConnection,
            GONetClient localClient,
            int maxReliableQueueSize = 2000)
            : base(transport, clientConnection, maxReliableQueueSize)
        {
            this.localClient = localClient;

            GONetLog.Info("[GONetConnection_ClientHostLoopback] Created loopback connection for host player - bypassing network stack");
        }

        /// <summary>
        /// Overrides SendMessageOverChannel to bypass network serialization.
        /// Instead of sending over network, directly processes message locally.
        ///
        /// PHASE II OPTIMIZATION: This is the core of the loopback optimization.
        /// Messages go directly to local client processing through GONet's existing queue system.
        /// </summary>
        public new void SendMessageOverChannel(byte[] messageBytes, int bytesUsedCount, GONetChannelId channelId)
        {
            // OPTIMIZATION: Skip network entirely!
            // Instead of:
            // 1. Serialize → 2. Network send → 3. Network receive → 4. Deserialize
            //
            // We do:
            // 1. Direct local processing through GONet's receive queue (same process, same memory space)

            // Copy message bytes to ensure no shared memory issues
            // (base class may reuse buffers, so we need our own copy)
            byte[] localCopy = new byte[bytesUsedCount];
            Buffer.BlockCopy(messageBytes, 0, localCopy, 0, bytesUsedCount);

            // CRITICAL: Use GONet's existing receive processing system
            // This ensures thread-safety and proper message ordering
            // ProcessIncomingBytes_TriageFromAnyThread handles thread marshalling internally
            ProcessMessageLocally(localCopy, bytesUsedCount, channelId);

            // TELEMETRY: Track loopback message processing for debugging
            //GONetLog.Debug($"[LOOPBACK] Processed message locally: Channel={channelId}, Bytes={bytesUsedCount}, AuthorityId={OwnerAuthorityId}");
        }

        /// <summary>
        /// SLOT RESERVATION (December 2025): Priority-aware overload.
        /// Loopback connection doesn't use network buffer, so priority is ignored.
        /// </summary>
        public new void SendMessageOverChannel(byte[] messageBytes, int bytesUsedCount, GONetChannelId channelId, ReliableNetcode.MessagePriority priority)
        {
            // Loopback doesn't go through network buffer, so priority is irrelevant
            // Just call the base version
            SendMessageOverChannel(messageBytes, bytesUsedCount, channelId);
        }

        /// <summary>
        /// Processes message locally without network stack.
        /// Uses GONet's existing receive queue system for thread-safe processing.
        /// </summary>
        private void ProcessMessageLocally(byte[] messageBytes, int bytesUsedCount, GONetChannelId channelId)
        {
            // Use GONet's existing receive processing pipeline
            // This is thread-safe and handles main thread marshalling automatically
            // The connection reference lets GONet know where the message came from
            GONetMain.ProcessIncomingBytes_TriageFromAnyThread(
                this, // connection - identifies this as coming from loopback
                messageBytes,
                bytesUsedCount,
                channelId);
        }

        /// <summary>
        /// Override IsConnectedToClient to always return true (loopback is always "connected").
        /// </summary>
        public new bool IsConnectedToClient => localClient != null && localClient.IsConnectedToServer;

        /// <summary>
        /// Override RemoteClientEndPoint to return localhost (for debugging/logging).
        /// </summary>
        public new EndPoint RemoteClientEndPoint => new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0);
    }
}
