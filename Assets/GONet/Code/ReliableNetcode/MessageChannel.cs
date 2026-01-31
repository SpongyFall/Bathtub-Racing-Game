using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ReliableNetcode.Utils;

namespace ReliableNetcode
{
    /// <summary>
    /// SLOT RESERVATION (December 2025): Message priority for reliable buffer slot allocation.
    ///
    /// <para><b>WHY THIS EXISTS:</b></para>
    /// <para>
    /// During network congestion, the reliable sendBuffer (1024 slots) can fill up with bulk
    /// gameplay messages (spawns, sync updates). When this happens, critical system messages
    /// (scene load events, heartbeats) get stuck behind hundreds of queued messages, causing
    /// scene changes to never propagate and connections to appear frozen.
    /// </para>
    ///
    /// <para><b>SOLUTION:</b></para>
    /// <para>
    /// Reserve the last 56 slots (slots 968-1023) exclusively for System priority messages.
    /// Gameplay messages are limited to the first 968 slots.
    /// This ensures critical system traffic always has buffer space available.
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item><description><b>Gameplay</b>: Spawns, sync events, RPCs - can use slots 0-967 (968 slots)</description></item>
    ///   <item><description><b>System</b>: Scene loads, heartbeats, critical state - can use slots 0-1023 (full 1024)</description></item>
    /// </list>
    /// </summary>
    public enum MessagePriority
    {
        /// <summary>
        /// Normal gameplay traffic: spawns, RPCs, sync updates.
        /// Limited to GAMEPLAY_SLOT_LIMIT slots to leave room for System traffic.
        /// </summary>
        Gameplay = 0,

        /// <summary>
        /// Critical system traffic: scene loads, heartbeats, connection management.
        /// Has access to full buffer capacity, bypassing Gameplay reservation.
        /// </summary>
        System = 1
    }

	    internal abstract class MessageChannel
	    {
	        protected ReliablePacketController packetController;

	        public abstract int ChannelID { get; }

	        public Action<byte[], int> TransmitCallback;
	        /// <summary>
	        /// Callback when a message is ready for delivery.
	        /// Parameters: (buffer, length, receiveTimestamp).
	        /// receiveTimestamp is the transport-level receive time in ticks (0 if unavailable).
	        /// </summary>
	        public Action<byte[], int, long> ReceiveCallback;

	        public uint SessionId
	        {
	            get => packetController?.SessionId ?? 0;
	            set
	            {
	                if (packetController != null)
	                {
	                    packetController.SessionId = value;
	                }
	            }
	        }

	        public abstract void Reset();
	        public abstract void Update(double newTime);
	        /// <summary>
	        /// Process an incoming packet from the transport layer.
	        /// </summary>
	        /// <param name="buffer">Packet data</param>
	        /// <param name="bufferLength">Packet length</param>
	        /// <param name="receiveTimestamp">Transport-level receive timestamp in ticks (0 if unavailable)</param>
	        public abstract void ReceivePacket(byte[] buffer, int bufferLength, long receiveTimestamp = 0);
	        public abstract void SendMessage(byte[] buffer, int bufferLength);

        /// <summary>
        /// Send a message with specified priority for slot reservation.
        /// System priority messages can use the full buffer; Gameplay messages are limited.
        /// Default implementation ignores priority (for backwards compatibility).
        /// </summary>
        public virtual void SendMessage(byte[] buffer, int bufferLength, MessagePriority priority)
        {
            // Default: ignore priority, call the standard SendMessage
            SendMessage(buffer, bufferLength);
        }

        /// <summary>
        /// PHASE 7 FIX: Clear pre-authority state while preserving pending messages.
        /// Override in ReliableMessageChannel to clear ackBuffer without losing sendBuffer.
        /// Default implementation does nothing (unreliable channels don't need this).
        /// </summary>
        public virtual void ClearPreAuthorityState() { }

        public virtual string GetUsageStatistics()
        {
            return packetController == null ? string.Empty : packetController.GetUsageStatistics();
        }

        public virtual void ProcessSendBuffer_IfAppropriate()
        {
        }
    }

    /// <summary>
    /// an unreliable implementation of <see cref="MessageChannel"/>
    /// does not make any guarantees about message reliability except for ignoring duplicate messages
    /// </summary>
    internal class UnreliableMessageChannel : MessageChannel
    {
        public override int ChannelID
        {
            get
            {
                return (int)QosType.Unreliable;
            }
        }

        private ReliableConfig config;
        private SequenceBuffer<ReceivedPacketData> receiveBuffer;

        public UnreliableMessageChannel()
        {
            receiveBuffer = new SequenceBuffer<ReceivedPacketData>(256);

            config = ReliableConfig.DefaultConfig();
            config.TransmitPacketCallback = (buffer, size) => {
                TransmitCallback(buffer, size);
            };
            // Note: For unreliable channel, we pass the pending timestamp (set before ReceivePacket call)
            config.ProcessPacketCallback = (seq, buffer, size, timestamp) => {
                if (!receiveBuffer.Exists(seq)) {
                    receiveBuffer.Insert(seq);
                    ReceiveCallback(buffer, size, timestamp);
                }
            };

            packetController = new ReliablePacketController(config, DateTime.UtcNow.GetTotalSeconds());
        }

        public override void Reset()
        {
            packetController.Reset();
            receiveBuffer.Reset();
        }

        public override void Update(double newTimeSeconds)
        {
            packetController.Update(newTimeSeconds);
        }

        public override void ReceivePacket(byte[] buffer, int bufferLength, long receiveTimestamp = 0)
        {
            packetController.ReceivePacket(buffer, bufferLength, receiveTimestamp);
        }

        public override void SendMessage(byte[] buffer, int bufferLength)
        {
            packetController.SendPacket(buffer, bufferLength, (byte)ChannelID);
        }
    }

    /// <summary>
    /// a reliable ordered implementation of <see cref="MessageChannel"/>
    /// </summary>
    internal class ReliableMessageChannel : MessageChannel
    {
        internal class BufferedPacket
        {
            public bool writeLock = true;
            public double time;
            public ByteBuffer buffer = new ByteBuffer();

            /// <summary>
            /// PHASE 3 FIX (December 2025): Track ACKed status separately from sendBuffer existence.
            /// Messages are NOT removed from sendBuffer when ACKed. Instead, they're marked as acked
            /// and only removed when oldestUnacked advances past them.
            /// This prevents false ACKs (from connection aliasing in hot standby mesh) from causing
            /// permanent message loss - the message remains in buffer for forced retransmission.
            /// </summary>
            public bool acked = false;

            /// <summary>
            /// PHASE 4 FIX (December 2025): Grace period removal.
            /// When oldestUnacked advances past a message, instead of immediate removal, we schedule
            /// removal for a grace period later. This allows stale connection detection to recover
            /// from cases where the ONLY remaining message was falsely ACKed.
            /// Value of 0.0 means not scheduled for removal.
            /// </summary>
            public double scheduledRemovalTime = 0.0;

            /// <summary>
            /// True once this message has been packed into at least one outgoing packet.
            /// Used as a safety guard against false ACKs marking never-sent messages as delivered.
            /// </summary>
            public bool hasBeenTransmitted = false;

            /// <summary>
            /// TIMESTAMP FIX (December 2025): Transport-level receive timestamp.
            /// For out-of-order packets, this stores the ORIGINAL timestamp when the transport
            /// layer received the packet, not when we processed it. This is critical for accurate
            /// RTT calculations in time sync, especially during high-load scenarios where processing
            /// delays can be 50-500ms.
            /// Value of 0 means no timestamp available.
            /// </summary>
            public long receiveTimestamp = 0;
        }

        internal class OutgoingPacketSet
        {
            public List<ushort> MessageIds = new List<ushort>();
        }

        public override int ChannelID
        {
            get
            {
                return (int)QosType.Reliable;
            }
        }

        public float RTTMilliseconds => packetController.RTTMilliseconds;

        public float PacketLoss => packetController.PacketLoss;

        public float SentBandwidthKBPS => packetController.SentBandwidthKBPS;

        public float ReceivedBandwidthKBPS => packetController.ReceivedBandwidthKBPS;

        private ReliableConfig config;
        private bool congestionControl = false;
        private double congestionDisableTimer;
        private double congestionDisableInterval;
        private double lastCongestionSwitchTime;

        private ByteBuffer messagePacker = new ByteBuffer();
        private SequenceBuffer<BufferedPacket> sendBuffer;
        private SequenceBuffer<BufferedPacket> receiveBuffer;
        private SequenceBuffer<OutgoingPacketSet> ackBuffer;

        private Queue<ByteBuffer> messageQueue = new Queue<ByteBuffer>();

        private double lastBufferFlush;
        private double lastMessageSend;
        private double timeSeconds;

        private volatile ushort oldestUnacked;
        private volatile ushort sequence;
        private volatile ushort nextReceive;
        private volatile bool isTimeToProcessSendBuffer;

        // PHASE 1D: MessageQueue depth tracking for diagnostics
        private double lastQueueDepthLogTime = 0.0;
        private int lastLoggedQueueDepth = 0;

        // Configurable maximum queue size (prevents unbounded memory growth)
        private readonly int maxMessageQueueSize;

        // DIAGNOSTIC (December 2025): Reliable transport tracking for spawn event loss investigation
        // Enable to get detailed per-message logging through the reliable channel
        public static bool EnableDetailedReliableLogging = false;  // High-volume logging: enable only when diagnosing issues

        // DEBUG: Connection identifier for distinguishing logs from multiple connections
        public string ConnectionId = "unknown";

        // Track retransmission counts per message sequence
        private Dictionary<ushort, int> retransmissionCounts = new Dictionary<ushort, int>();

        // Callback for logging (set by GONet to use GONetLog)
        public static Action<string> LogCallback = null;

        // RECEIVE-SIDE GAP DETECTION (December 2025 fix)
        // When nextReceive doesn't advance, we're waiting for a missing message.
        // Track this and send more frequent ACKs to prompt sender retransmission.
        private double lastNextReceiveAdvanceTime = 0.0;
        private ushort lastNextReceiveValue = 0;
        private double lastGapAckTime = 0.0;
        private const double GAP_DETECTION_THRESHOLD_SECONDS = 0.3;  // If stuck for 300ms, start sending extra ACKs
        private const double GAP_ACK_INTERVAL_SECONDS = 0.05;        // Send ACKs every 50ms when stuck
        private int gapDetectionAcksSent = 0;

        // SENDER-SIDE GAP DETECTION (December 2025 fix - Phase 2)
        // Handles case where oldestUnacked stays stuck but newer messages ARE being ACKed.
        // This indicates receiver's ACKs are corrupted/lost or aliasing between connections.
        // Critical for hot standby mesh scenarios with multiple reliable connections.
        private double lastOldestUnackedAdvanceTime = 0.0;
        private ushort lastOldestUnackedValue = 0;
        private double lastForcedRetransmitTime = 0.0;
        private const double SENDER_GAP_DETECTION_THRESHOLD_SECONDS = 1.0;
        private const double SENDER_FORCED_RETRANSMIT_INTERVAL_SECONDS = 0.2;
        private int forcedRetransmitCount = 0;
        private ushort highestAckedSequence = 0;

        // PHASE 4 FIX (December 2025): Stale connection detection for falsely-ACKed last message
        // When we think we're fully caught up (oldestUnacked == sequence) but haven't received
        // application data recently, force retransmit of messages still in grace period.
        private const double GRACE_PERIOD_SECONDS = 3.0;  // Keep messages for 3s after oldestUnacked advances past them
        private const double STALE_CONNECTION_THRESHOLD_SECONDS = 1.5;  // Consider connection stale after 1.5s of no app data
        private double lastDeliveredMessageTime = 0.0;  // When we last delivered a message to the application
        private double lastStaleRetransmitTime = 0.0;  // When we last did a stale-connection retransmit

        // PHASE 6C FIX (December 2025): Rate limit stale detection logging to prevent flooding
        // In pathological cases, 80K+ stale events were logged, causing CPU saturation and heartbeat misses
        private double lastStaleLogTime = 0.0;
        private int staleLogsSuppressedSinceLastLog = 0;
        private const double STALE_LOG_INTERVAL_SECONDS = 1.0;  // Log at most once per second per connection

        // CRITICAL FIX (December 2025): RTT-based adaptive resend timeout to prevent "Reliability Death Spiral"
        //
        // PROBLEM: Hardcoded 0.1s (100ms) timeout causes catastrophic retransmit flooding during congestion.
        // With 9s real RTT, packets retransmit 90 times before first ACK arrives, causing self-inflicted DDoS.
        // This CAUSES the high RTT rather than just being affected by it.
        //
        // SOLUTION: Use smoothed RTT * 2 as timeout, with floor/ceiling to prevent extremes.
        // - Minimum 100ms: Prevents overly aggressive retransmit on fast networks
        // - Maximum 30s: Prevents complete stall on severely degraded networks
        // - Multiplier 2.0: Standard TCP-style RTO = RTT * 2 for jitter tolerance
        //
        // NOTE: This RTT is calculated internally by ReliablePacketController from actual ACK round-trips.
        // It is NOT connected to GONet's filtered TimeSync RTT (which is intentionally clamped for clock sync).
        private const double RESEND_TIMEOUT_MIN_SECONDS = 0.1;    // 100ms minimum
        private const double RESEND_TIMEOUT_MAX_SECONDS = 30.0;   // 30s maximum (prevents complete stall)
        private const double RESEND_TIMEOUT_RTT_MULTIPLIER = 2.0; // Standard TCP-style RTO multiplier

        // SLOT RESERVATION (December 2025): Reserve buffer slots for critical System messages.
        //
        // PROBLEM: During congestion, gameplay messages (spawns, syncs) fill the entire 1024-slot
        // sendBuffer. Critical scene load events get queued behind 700+ messages and never arrive.
        //
        // SOLUTION: Gameplay messages are limited to first 968 slots, reserving 56 for System traffic.
        // This ensures scene changes, heartbeats, and critical state always have buffer space.
        //
        // Buffer capacity: 1024 slots (increased in PHASE 1A from original 256)
        // Gameplay limit:   968 slots (94.5% of capacity)
        // System reserve:    56 slots (5.5% of capacity, same ratio as original 56/256)
        private const int GAMEPLAY_SLOT_LIMIT = 968;  // Max slots for Gameplay priority messages

        /// <summary>
        /// Calculates adaptive resend timeout based on smoothed RTT.
        /// Returns timeout in seconds, bounded by MIN/MAX constants.
        /// </summary>
        private double GetAdaptiveResendTimeoutSeconds()
        {
            double rttSeconds = packetController.RTTMilliseconds / 1000.0;
            double timeout = rttSeconds * RESEND_TIMEOUT_RTT_MULTIPLIER;

            // Clamp to [MIN, MAX] range
            if (timeout < RESEND_TIMEOUT_MIN_SECONDS)
                timeout = RESEND_TIMEOUT_MIN_SECONDS;
            else if (timeout > RESEND_TIMEOUT_MAX_SECONDS)
                timeout = RESEND_TIMEOUT_MAX_SECONDS;

            return timeout;
        }

        private static void Log(string message)
        {
            if (LogCallback != null)
                LogCallback(message);
            else
                System.Diagnostics.Debug.WriteLine(message);
        }

        private const double MALFORMED_LOG_INTERVAL_SECONDS = 1.0;
        private double lastMalformedPacketLogTime = -1.0;
        private int malformedPacketLogsSuppressed = 0;

        private void LogMalformedPacket(string reason, byte[] packetData, int packetLen, long readPos)
        {
            if (lastMalformedPacketLogTime < 0.0 || (timeSeconds - lastMalformedPacketLogTime) >= MALFORMED_LOG_INTERVAL_SECONDS)
            {
                string firstBytesHex = string.Empty;
                if (packetData != null && packetLen > 0)
                {
                    int bytesToLog = Math.Min(packetLen, 8);
                    firstBytesHex = BitConverter.ToString(packetData, 0, bytesToLog).Replace("-", "");
                }

                string suppressedInfo = malformedPacketLogsSuppressed > 0
                    ? $" (suppressed {malformedPacketLogsSuppressed})"
                    : string.Empty;

                Log($"[RELIABLE-MALFORMED] conn={ConnectionId} {reason} pktBytes={packetLen} readPos={readPos} firstBytes={firstBytesHex}{suppressedInfo}");
                malformedPacketLogsSuppressed = 0;
                lastMalformedPacketLogTime = timeSeconds;
            }
            else
            {
                malformedPacketLogsSuppressed++;
            }
        }

        // Thread-local pending timestamp for the current receive operation
        // Set before calling packetController.ReceivePacket, read in processPacket
        private long _pendingReceiveTimestamp = 0;

        public ReliableMessageChannel(int maxQueueSize = 2000)
        {
            maxMessageQueueSize = maxQueueSize;
            config = ReliableConfig.DefaultConfig();
            config.TransmitPacketCallback = (buffer, size) => {
                TransmitCallback(buffer, size);
            };
            config.ProcessPacketCallback = processPacketWithTimestamp;
            config.AckPacketCallback = ackPacket;

            // DEBUG: ACK instrumentation for investigating false ACK issue
            config.DebugAckSendCallback = (pktSeq, ack, ackBits, receivedPktsSeq) => {
                if (EnableDetailedReliableLogging)
                {
                    string pktSeqStr = pktSeq == 0xFFFF ? "ACK-ONLY" : pktSeq.ToString();
                    Log($"[RELIABLE-ACK-SEND] conn={ConnectionId} pktSeq={pktSeqStr} ack={ack} ackBits=0x{ackBits:X8} receivedPkts.seq={receivedPktsSeq}");
                }
            };
            config.DebugAckReceiveCallback = (pktSeq, ack, ackBits, willProcessAck, ourSentSeq) => {
                if (EnableDetailedReliableLogging)
                {
                    Log($"[RELIABLE-ACK-RECV] conn={ConnectionId} pktSeq={pktSeq} ack={ack} ackBits=0x{ackBits:X8} willProcess={willProcessAck} ourSentSeq={ourSentSeq}");
                }
            };
            config.DebugFalseAckRejectedCallback = (pktSeq, ackSeq, rttMs) => {
                // Gate behind EnableDetailedReliableLogging to prevent log spam (can be 30K+ entries)
                if (!EnableDetailedReliableLogging)
                    return;
                // RTT markers indicate different rejection types:
                // -1000.0f = Phase 6B packet-level rejection (primary ack outside valid range)
                // -999.0f = Phase 6A per-bit rejection (individual ackSeq outside sent range)
                // < 0.5ms = Phase 5 RTT validation failure (impossibly fast round-trip)
                string reason;
                if (rttMs <= -1000.0f)
                    reason = $"PACKET_PRIMARY_ACK_INVALID (primary ack={ackSeq} outside valid range - ENTIRE packet rejected, cross-connection delivery)";
                else if (rttMs <= -999.0f)
                    reason = $"BIT_SEQUENCE_OUT_OF_RANGE (ackSeq={ackSeq} outside sent range - individual bit rejected)";
                else
                    reason = $"RTT_TOO_LOW={rttMs:F1}ms (below 0.5ms threshold - cross-connection delivery)";
                Log($"[RELIABLE-FALSE-ACK-REJECTED] conn={ConnectionId} pktSeq={pktSeq} {reason}");
            };

            // PHASE 1A FIX: Increased capacity from 256 → 1024 to handle realistic production burst scenarios
            // Previous capacity (256) was insufficient for medium/high latency scenarios (100-250ms RTT)
            // New capacity (1024) handles burst up to 1000 messages even at high latency
            // Memory cost: ~3.5MB per connection (acceptable trade-off for reliability)
            // See: INVESTIGATION_BATCH_MESSAGE_LOSS_2025-10-12.md Section 10.7
            sendBuffer = new SequenceBuffer<BufferedPacket>(1024);
            receiveBuffer = new SequenceBuffer<BufferedPacket>(1024);
            ackBuffer = new SequenceBuffer<OutgoingPacketSet>(1024);

            timeSeconds = DateTime.UtcNow.GetTotalSeconds();
            lastBufferFlush = -1.0;
            lastMessageSend = 0.0;
            this.packetController = new ReliablePacketController(config, timeSeconds);

            this.congestionDisableInterval = 5.0;

            this.sequence = 0;
            this.nextReceive = 0;
            this.oldestUnacked = 0;

            // PHASE 3 FIX: Initialize gap detection times to current time (not 0.0)
            // This prevents false "stuckFor=55 years" logs on first gap detection
            this.lastOldestUnackedAdvanceTime = timeSeconds;
            this.lastNextReceiveAdvanceTime = timeSeconds;

            // PHASE 4 FIX: Initialize stale connection detection time
            this.lastDeliveredMessageTime = timeSeconds;
            this.lastStaleRetransmitTime = 0.0;
        }

        public override void Reset()
        {
            this.packetController.Reset();
            this.sendBuffer.Reset();
            this.receiveBuffer.Reset();
            this.ackBuffer.Reset();

            // NOTE: We intentionally do NOT clear messageQueue here.
            // Reset is used in failover/reconnect scenarios where queued (not-yet-sent) outbound messages
            // should continue to send once the reliability session is re-established.

            this.messagePacker.SetSize(0);
            this.tempList.Clear();
            this.retransmissionCounts.Clear();

            this.lastBufferFlush = -1.0;
            this.lastMessageSend = 0.0;

            this.congestionControl = false;
            this.lastCongestionSwitchTime = 0.0;
            this.congestionDisableTimer = 0.0;
            this.congestionDisableInterval = 5.0;

            this.sequence = 0;
            this.nextReceive = 0;
            this.oldestUnacked = 0;

            // Reset gap detection state (receive-side)
            // PHASE 3 FIX: Use current time, not 0.0, to prevent false "stuckFor=55 years" logs
            this.lastNextReceiveAdvanceTime = this.timeSeconds;
            this.lastNextReceiveValue = 0;
            this.lastGapAckTime = 0.0;
            this.gapDetectionAcksSent = 0;

            // Reset gap detection state (sender-side)
            // PHASE 3 FIX: Use current time, not 0.0, to prevent false "stuckFor=55 years" logs
            this.lastOldestUnackedAdvanceTime = this.timeSeconds;
            this.lastOldestUnackedValue = 0;
            this.lastForcedRetransmitTime = 0.0;
            this.forcedRetransmitCount = 0;
            this.highestAckedSequence = 0;

            // Reset stale connection detection state (PHASE 4 FIX)
            this.lastDeliveredMessageTime = this.timeSeconds;
            this.lastStaleRetransmitTime = 0.0;
        }

        /// <summary>
        /// PHASE 7 FIX (December 2025): Clear pre-authority state to prevent false ACKs.
        ///
        /// When a client transitions from pre-authority (Client:0) to post-authority (Client:N),
        /// the ackBuffer contains stale packet-to-message mappings from the pre-authority phase.
        /// If a cross-connection ACK (from mesh or other source) triggers ackPacket() with an
        /// old packet sequence, it would find a stale entry and mark messages as ACKed that
        /// were never actually received by the server.
        ///
        /// This method clears ONLY the state needed to prevent false ACKs while preserving
        /// pending messages in sendBuffer that need to be retransmitted.
        ///
        /// Call this when authority is assigned to the client.
        /// </summary>
        public override void ClearPreAuthorityState()
        {
            base.ClearPreAuthorityState(); // Good OOP practice - call base even if currently empty

            // Clear ACK mappings from pre-authority packets.
            // This prevents false ACKs from triggering stale packet-to-message mappings.
            // When ackPacket(seq) is called with a stale sequence, ackBuffer.Find(seq) will
            // return null and the callback will return early without marking messages as ACKed.
            this.ackBuffer.Reset();

            // Clear sent packet timing data from pre-authority phase.
            // This prevents RTT=0ms issues when calculating RTT for new packets.
            // Also resets the packet sequence counter so new packets start fresh.
            this.packetController.Reset();

            // NOTE: We intentionally do NOT clear sendBuffer here!
            // Messages in sendBuffer need to be retransmitted since they weren't
            // acknowledged on the post-authority connection. The reliable layer
            // will assign them NEW packet sequences when they're next transmitted.

            // Reset sequence counters to avoid collision with stale packet sequences
            // Messages will get fresh packet sequences when retransmitted
            // (But message sequences in sendBuffer are preserved)

            // Log the reset for debugging
            Log($"[PHASE7-RESET] ClearPreAuthorityState called - ackBuffer and packetController reset, sendBuffer preserved ({sendBuffer.Size} messages pending)");
        }

        public override void Update(double newTimeSeconds)
        {
            double dt = newTimeSeconds - timeSeconds;
            timeSeconds = newTimeSeconds;
            this.packetController.Update(timeSeconds);

            // see if we can pop messages off of the message queue and put them on the send queue
            if (messageQueue.Count > 0) {
                // Count send buffer size ONCE before dequeue loop (optimization)
                int sendBufferSize = 0;
                for (ushort seq = oldestUnacked; PacketIO.SequenceLessThan(seq, this.sequence); seq++) {
                    if (sendBuffer.Exists(seq))
                        sendBufferSize++;
                }

                // Dequeue multiple messages per update to prevent channel starvation
                // When many messages flood one reliable channel (e.g., spawn burst), this ensures
                // other reliable messages (e.g., position updates) aren't delayed excessively
                const int MAX_DEQUEUE_PER_UPDATE = 100;  // Process up to 100 messages per update
                const double MAX_DEQUEUE_TIME_MS = 0.5;  // Stop after 0.5ms to protect frame time

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                int dequeuedCount = 0;

                while (messageQueue.Count > 0 &&
                       sendBufferSize < sendBuffer.Size &&
                       dequeuedCount < MAX_DEQUEUE_PER_UPDATE &&
                       stopwatch.Elapsed.TotalMilliseconds < MAX_DEQUEUE_TIME_MS)
                {
                    var message = messageQueue.Dequeue();
                    SendMessage(message.InternalBuffer, message.Length);
                    ObjPool<ByteBuffer>.Return(message);

                    sendBufferSize++;  // Track locally (safe: only this thread dequeues in this loop)
                    dequeuedCount++;
                }
            }

            // update congestion mode
            {
                // conditions are bad if round-trip-time exceeds 250ms
                bool conditionsBad = (this.packetController.RTTMilliseconds >= 250f);

                // if conditions are bad, immediately enable congestion control and reset the congestion timer
                if (conditionsBad) {
                    if (this.congestionControl == false) {
                        // if we're within 10 seconds of the last time we switched, double the threshold interval
                        if (timeSeconds - lastCongestionSwitchTime < 10.0)
                        {
                            double times2 = congestionDisableInterval * 2;
                            congestionDisableInterval = (times2 < 60.0) ? times2 : 60.0; // Math.Min(congestionDisableInterval * 2, 60.0);
                        }

                        lastCongestionSwitchTime = timeSeconds;
                    }

                    this.congestionControl = true;
                    this.congestionDisableTimer = 0.0;
                }

                // if we're in bad mode, and conditions are good, update the timer and see if we can disable congestion control
                if (this.congestionControl && !conditionsBad) {
                    this.congestionDisableTimer += dt;
                    if (this.congestionDisableTimer >= this.congestionDisableInterval) {
                        this.congestionControl = false;
                        lastCongestionSwitchTime = timeSeconds;
                        congestionDisableTimer = 0.0;
                    }
                }

                // as long as conditions are good, halve the threshold interval every 10 seconds
                if (this.congestionControl == false) {
                    congestionDisableTimer += dt;
                    if (congestionDisableTimer >= 10.0)
                    {
                        double half = congestionDisableInterval * 0.5;
                        congestionDisableInterval = (half > 5.0) ? half : 5.0; //  Math.Max(congestionDisableInterval * 0.5, 5.0);
                    }
                }
            }

            const double CONGESTED_SEND_RATE_HZ = 1.0 / 10.0;
            const double NORMAL_SEND_RATE_HZ = 1.0 / 90.0; // GONet changed from original value of 0.033
            double flushInterval = congestionControl ? CONGESTED_SEND_RATE_HZ : NORMAL_SEND_RATE_HZ;

            if (timeSeconds - lastBufferFlush >= flushInterval) {
                isTimeToProcessSendBuffer = true;
            }

            // PHASE 1D FIX: MessageQueue depth logging for production diagnostics
            // Log queue depth at different severity levels based on thresholds
            // This provides visibility into transport congestion during operation
            // See: INVESTIGATION_BATCH_MESSAGE_LOSS_2025-10-12.md Section 10.6
            int currentQueueDepth = messageQueue.Count;
            if (currentQueueDepth > 0)
            {
                // Log every 1 second at INFO level if queue is building (>50 messages)
                // Log every 0.5 seconds at WARNING level if queue is high (>200 messages)
                // Log every 0.1 seconds at CRITICAL level if queue is near limit (>400 messages)
                double logInterval = 1.0;  // Default: INFO level, 1 second
                string severity = "INFO";

                if (currentQueueDepth > 400) {
                    logInterval = 0.1;
                    severity = "CRITICAL";
                } else if (currentQueueDepth > 200) {
                    logInterval = 0.5;
                    severity = "WARNING";
                } else if (currentQueueDepth <= 50) {
                    logInterval = 5.0;  // Low queue depth: log every 5 seconds
                }

                bool shouldLog = (timeSeconds - lastQueueDepthLogTime >= logInterval);
                bool queueDepthChanged = (currentQueueDepth != lastLoggedQueueDepth);

                if (shouldLog && queueDepthChanged)
                {
                    // Use System.Diagnostics.Debug for low-level transport logging
                    // This appears in Unity Editor console during development
                    System.Diagnostics.Debug.WriteLine(
                        $"[{severity}] ReliableMessageChannel: messageQueue depth = {currentQueueDepth} " +
                        $"(sendBuffer: {sendBuffer.Size}, RTT: {packetController.RTTMilliseconds:F1}ms, " +
                        $"congestionControl: {congestionControl})");

                    lastQueueDepthLogTime = timeSeconds;
                    lastLoggedQueueDepth = currentQueueDepth;
                }
            }
            else if (lastLoggedQueueDepth > 0)
            {
                // Queue drained - log recovery
                System.Diagnostics.Debug.WriteLine(
                    $"[INFO] ReliableMessageChannel: messageQueue drained (was {lastLoggedQueueDepth}, now 0)");
                lastLoggedQueueDepth = 0;
            }

            // PHASE 4 FIX: Remove messages whose grace period has expired
            // These messages were ACKed and scheduled for removal, now safe to actually remove
            ProcessGracePeriodRemovals();

            // RECEIVE-SIDE GAP DETECTION (December 2025 fix)
            // If we have buffered messages waiting for a missing earlier message,
            // send extra ACKs to prompt the sender to retransmit the missing message.
            // This helps recover from cases where the sender incorrectly thinks a packet was ACKed
            // or where the 32-bit ACK window has moved past the missing packet.
            CheckAndHandleReceiveGap();
        }

        /// <summary>
        /// PHASE 4 FIX: Process removal of messages whose grace period has expired.
        /// Messages are kept in sendBuffer for GRACE_PERIOD_SECONDS after oldestUnacked advances past them,
        /// allowing stale connection detection to force retransmit if needed.
        /// </summary>
        private void ProcessGracePeriodRemovals()
        {
            // Scan through messages that are scheduled for removal and remove expired ones
            // We need to track which sequence to start from - use a wider window to catch any scheduled
            // Go back up to buffer size from current sequence to find any scheduled removals
            ushort startSeq = (ushort)(this.sequence > sendBuffer.Size ? this.sequence - sendBuffer.Size : 0);

            for (ushort seq = startSeq; PacketIO.SequenceLessThan(seq, this.sequence); seq++)
            {
                var packet = sendBuffer.Find(seq);
                if (packet != null && packet.scheduledRemovalTime > 0.0 && timeSeconds >= packet.scheduledRemovalTime)
                {
                    if (EnableDetailedReliableLogging)
                    {
                        Log($"[RELIABLE-GRACE-REMOVE] Removing expired: msgSeq={seq}, scheduledTime={packet.scheduledRemovalTime:F2}s, currentTime={timeSeconds:F2}s");
                    }

                    sendBuffer.Remove(seq);
                }
            }
        }

        /// <summary>
        /// Checks if we're stuck waiting for a missing message on the receive side.
        /// If so, sends extra ACKs to prompt retransmission from the sender.
        /// </summary>
        private void CheckAndHandleReceiveGap()
        {
            // Check if nextReceive has advanced
            if (nextReceive != lastNextReceiveValue)
            {
                lastNextReceiveValue = nextReceive;
                lastNextReceiveAdvanceTime = timeSeconds;
                gapDetectionAcksSent = 0;  // Reset counter when we make progress
                return;
            }

            // Check if there are buffered messages waiting (indicating a gap)
            bool hasBufferedMessages = false;
            int bufferedCount = 0;
            ushort firstBufferedSeq = 0;

            // Look ahead to see if we have messages waiting to be delivered
            for (ushort seq = (ushort)(nextReceive + 1); seq != (ushort)(nextReceive + 33); seq++)
            {
                if (receiveBuffer.Exists(seq))
                {
                    if (!hasBufferedMessages)
                    {
                        hasBufferedMessages = true;
                        firstBufferedSeq = seq;
                    }
                    bufferedCount++;
                }
            }

            if (!hasBufferedMessages)
            {
                // No gap - nothing buffered, we're just waiting for new messages
                return;
            }

            // We have a gap - check if we've been stuck long enough
            double stuckDuration = timeSeconds - lastNextReceiveAdvanceTime;

            if (stuckDuration >= GAP_DETECTION_THRESHOLD_SECONDS)
            {
                // We're stuck! Send extra ACKs to prompt retransmission
                if (timeSeconds - lastGapAckTime >= GAP_ACK_INTERVAL_SECONDS)
                {
                    sendAckPacket();
                    lastGapAckTime = timeSeconds;
                    gapDetectionAcksSent++;

                    if (EnableDetailedReliableLogging || gapDetectionAcksSent <= 5 || gapDetectionAcksSent % 10 == 0)
                    {
                        Log($"[RELIABLE-GAP-DETECT] Receive gap detected! nextExpected={nextReceive}, " +
                            $"firstBuffered={firstBufferedSeq}, bufferedCount={bufferedCount}, " +
                            $"stuckFor={stuckDuration:F2}s, extraAcksSent={gapDetectionAcksSent}");
                    }
                }
            }
        }

        /// <summary>
        /// SENDER-SIDE GAP DETECTION (December 2025 fix - Phase 2 + Phase 3 + Phase 4)
        /// Detects when oldestUnacked stays stuck while newer messages ARE being ACKed.
        /// This indicates the oldest message was falsely ACKed (due to connection aliasing
        /// in hot standby mesh scenarios) or its ACK was corrupted.
        /// Forces retransmission of stuck messages to recover from deadlock.
        ///
        /// PHASE 3 FIX: Also retransmits messages that were marked as ACKed but where
        /// oldestUnacked hasn't advanced - these may have been falsely ACKed.
        ///
        /// PHASE 4 FIX: Stale connection detection - handles case where the ONLY unacked
        /// message was falsely ACKed. In this case, oldestUnacked == sequence (we think
        /// we're done), but we're not receiving application data from the peer.
        /// Force retransmit of messages still in grace period.
        /// </summary>
        private void CheckAndHandleSendGap()
        {
            // PHASE 4 FIX: Check for stale connection even when we think we're "fully caught up"
            // If oldestUnacked == sequence but we haven't received app data recently, something is wrong
            if (oldestUnacked == this.sequence)
            {
                // Reset tracking when fully caught up
                lastOldestUnackedAdvanceTime = timeSeconds;
                lastOldestUnackedValue = oldestUnacked;
                forcedRetransmitCount = 0;

                // PHASE 4 FIX: Check for stale connection - we think we're done but peer isn't progressing
                // Only check if we've sent at least one message
                if (this.sequence > 0)
                {
                    CheckAndHandleStaleConnection();
                }
                return;
            }

            // Track when oldestUnacked changes (advances)
            if (oldestUnacked != lastOldestUnackedValue)
            {
                lastOldestUnackedValue = oldestUnacked;
                lastOldestUnackedAdvanceTime = timeSeconds;
                forcedRetransmitCount = 0;
                return;
            }

            // Check if newer messages have been ACKed while oldestUnacked is stuck
            // This is the key indicator of a false-ACK situation
            bool newerMessagesAcked = PacketIO.SequenceGreaterThan(highestAckedSequence, oldestUnacked);
            if (!newerMessagesAcked)
            {
                // No newer messages ACKed yet - normal case, just waiting for ACKs
                return;
            }

            // oldestUnacked is stuck but newer messages ARE being ACKed - anomaly detected!
            double stuckDuration = timeSeconds - lastOldestUnackedAdvanceTime;

            if (stuckDuration >= SENDER_GAP_DETECTION_THRESHOLD_SECONDS)
            {
                // Force retransmit oldest unacked messages at regular intervals
                if (timeSeconds - lastForcedRetransmitTime >= SENDER_FORCED_RETRANSMIT_INTERVAL_SECONDS)
                {
                    lastForcedRetransmitTime = timeSeconds;
                    forcedRetransmitCount++;

                    // PHASE 3 FIX: Retransmit ALL messages from oldestUnacked to highestAcked that are still in buffer.
                    // This includes messages that were "ACKed" but where oldestUnacked hasn't advanced (false ACKs).
                    // We retransmit up to MAX_FORCED_RETRANSMIT messages per interval.
                    int forcedCount = 0;
                    const int MAX_FORCED_RETRANSMIT = 5;  // Increased from 3 to cover more potential false ACKs

                    for (ushort seq = oldestUnacked;
                         PacketIO.SequenceLessThan(seq, this.sequence) && forcedCount < MAX_FORCED_RETRANSMIT;
                         seq++)
                    {
                        var packet = sendBuffer.Find(seq);
                        if (packet != null)
                        {
                            // PHASE 3 FIX: Clear acked flag and writeLock to allow retransmission.
                            // If this message was falsely ACKed, this gives it another chance to be delivered.
                            bool wasAcked = packet.acked;
                            packet.acked = false;
                            packet.writeLock = false;

                            // Force immediate retransmit by resetting time to very old value
                            packet.time = -1.0;
                            forcedCount++;

                            if (EnableDetailedReliableLogging || forcedRetransmitCount <= 5 || forcedRetransmitCount % 10 == 0)
                            {
                                Log($"[RELIABLE-SEND-GAP] Forcing retransmit of stuck msgSeq={seq}: " +
                                    $"oldestUnacked={oldestUnacked}, highestAcked={highestAckedSequence}, " +
                                    $"stuckFor={stuckDuration:F2}s, forcedRetransmits={forcedRetransmitCount}, " +
                                    $"wasAcked={wasAcked}");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// PHASE 4 FIX: Stale connection detection.
        /// Handles the case where we think we're "fully caught up" (oldestUnacked == sequence)
        /// but the peer isn't progressing (we haven't received application data recently).
        /// This happens when the ONLY remaining unacked message was falsely ACKed.
        /// Forces retransmit of messages still in grace period buffer.
        /// </summary>
        private void CheckAndHandleStaleConnection()
        {
            // Check if we have any messages still in grace period (scheduled for removal but not yet removed)
            bool hasGracePeriodMessages = false;
            int gracePeriodCount = 0;

            // Scan sendBuffer for messages with scheduledRemovalTime set
            ushort startSeq = (ushort)(this.sequence > sendBuffer.Size ? this.sequence - sendBuffer.Size : 0);
            for (ushort seq = startSeq; PacketIO.SequenceLessThan(seq, this.sequence); seq++)
            {
                var packet = sendBuffer.Find(seq);
                if (packet != null && packet.scheduledRemovalTime > 0.0)
                {
                    hasGracePeriodMessages = true;
                    gracePeriodCount++;
                }
            }

            if (!hasGracePeriodMessages)
            {
                // No grace period messages - nothing to retransmit
                return;
            }

            // Check if connection seems stale (haven't received application data recently)
            double timeSinceLastAppMessage = timeSeconds - lastDeliveredMessageTime;

            if (timeSinceLastAppMessage < STALE_CONNECTION_THRESHOLD_SECONDS)
            {
                // Connection is healthy - receiving data from peer
                return;
            }

            // Connection seems stale! We think we're done but haven't received data in a while.
            // Force retransmit of grace period messages at regular intervals.
            if (timeSeconds - lastStaleRetransmitTime >= SENDER_FORCED_RETRANSMIT_INTERVAL_SECONDS)
            {
                lastStaleRetransmitTime = timeSeconds;

                int forcedCount = 0;
                const int MAX_STALE_RETRANSMIT = 5;

                for (ushort seq = startSeq;
                     PacketIO.SequenceLessThan(seq, this.sequence) && forcedCount < MAX_STALE_RETRANSMIT;
                     seq++)
                {
                    var packet = sendBuffer.Find(seq);
                    if (packet != null && packet.scheduledRemovalTime > 0.0)
                    {
                        // Clear flags to allow retransmission
                        packet.acked = false;
                        packet.writeLock = false;
                        packet.scheduledRemovalTime = 0.0;  // Clear scheduled removal - need to re-ACK

                        // Force immediate retransmit
                        packet.time = -1.0;
                        forcedCount++;

                        // Reset oldestUnacked to this message since it's being retransmitted
                        if (PacketIO.SequenceLessThan(seq, oldestUnacked) || oldestUnacked == this.sequence)
                        {
                            oldestUnacked = seq;
                        }

                        // PHASE 6C: Rate-limited logging to prevent log flooding (80K+ events observed in pathological cases)
                        staleLogsSuppressedSinceLastLog++;
                    }
                }

                // Log once per interval with summary of retransmits
                if (forcedCount > 0 && (timeSeconds - lastStaleLogTime >= STALE_LOG_INTERVAL_SECONDS))
                {
                    Log($"[RELIABLE-STALE] conn={ConnectionId} Stale connection detected! Forced {forcedCount} retransmits, " +
                        $"timeSinceLastAppMsg={timeSinceLastAppMessage:F2}s, gracePeriodMsgs={gracePeriodCount}" +
                        (staleLogsSuppressedSinceLastLog > forcedCount ? $", suppressedLogs={staleLogsSuppressedSinceLastLog - forcedCount}" : ""));
                    lastStaleLogTime = timeSeconds;
                    staleLogsSuppressedSinceLastLog = 0;
                }
            }
        }

        public override void ProcessSendBuffer_IfAppropriate()
        {
            if (isTimeToProcessSendBuffer)
            {
                isTimeToProcessSendBuffer = false;
                lastBufferFlush = timeSeconds;

                // Check for sender-side gap (stuck oldestUnacked with newer ACKs arriving)
                CheckAndHandleSendGap();

                processSendBuffer();
            }
        }

        public override void ReceivePacket(byte[] buffer, int bufferLength, long receiveTimestamp = 0)
        {
            // Store the timestamp for use in the processPacket callback
            _pendingReceiveTimestamp = receiveTimestamp;
            try
            {
                this.packetController.ReceivePacket(buffer, bufferLength, receiveTimestamp);
            }
            finally
            {
                _pendingReceiveTimestamp = 0;
            }
        }

        /// <summary>
        /// Wrapper for processPacket that passes the pending receive timestamp.
        /// Called by ReliablePacketController when a packet is ready for processing.
        /// </summary>
        private void processPacketWithTimestamp(ushort seq, byte[] packetData, int packetLen, long receiveTimestamp)
        {
            processPacket(seq, packetData, packetLen, receiveTimestamp);
        }

        public override void SendMessage(byte[] buffer, int bufferLength)
        {
            // Default to Gameplay priority (backwards compatible)
            SendMessage(buffer, bufferLength, MessagePriority.Gameplay);
        }

        /// <summary>
        /// SLOT RESERVATION (December 2025): Send message with specified priority.
        /// System messages have access to the full 1024-slot buffer.
        /// Gameplay messages are limited to 968 slots, reserving 56 for System traffic.
        /// </summary>
        public override void SendMessage(byte[] buffer, int bufferLength, MessagePriority priority)
        {
            // DIAGNOSTIC: SceneLoadComplete trace - Stage 5: Enter ReliableMessageChannel
            // Messages with GONet header are ~30 bytes for SceneLoadComplete (original 25 + header 5)
            bool isSlcSized = bufferLength >= 25 && bufferLength <= 45;
            if (EnableDetailedReliableLogging && isSlcSized)
            {
                string hex = bufferLength >= 16
                    ? BitConverter.ToString(buffer, 0, 16).Replace("-", "")
                    : BitConverter.ToString(buffer, 0, bufferLength).Replace("-", "");
                Log($"[SLC-TRACE-5] STAGE5_RELIABLE_CHANNEL_ENTER bytes={bufferLength} hex={hex} nextSeq={this.sequence} oldestUnacked={oldestUnacked} priority={priority}");
            }

            int sendBufferSize = 0;
            for (ushort seq = oldestUnacked; PacketIO.SequenceLessThan(seq, this.sequence); seq++) {
                if (sendBuffer.Exists(seq))
                    sendBufferSize++;
            }

            // SLOT RESERVATION: Determine effective buffer limit based on message priority
            // - Gameplay messages: Limited to GAMEPLAY_SLOT_LIMIT (968) to leave room for System
            // - System messages: Can use full buffer capacity (1024)
            int effectiveLimit = priority == MessagePriority.System ? sendBuffer.Size : GAMEPLAY_SLOT_LIMIT;
            bool isBufferFull = sendBufferSize >= effectiveLimit;

            if (isBufferFull) {
                // PHASE 1B FIX: Bounds checking to prevent unbounded messageQueue growth
                // In extreme edge cases (sustained 100+ spawns/sec + high packet loss), messageQueue could grow without limit
                // This safety valve prevents memory exhaustion by throwing an exception when queue exceeds threshold
                // Threshold: Configurable via maxMessageQueueSize (default 2000 messages = ~2.4MB at 1200 bytes/message average)
                // NOTE: This is EXTREMELY RARE - requires sustained burst + high packet loss + slow ACKs simultaneously
                // See: INVESTIGATION_BATCH_MESSAGE_LOSS_2025-10-12.md Section 8.1

                if (messageQueue.Count >= maxMessageQueueSize)
                {
                    // CRITICAL: Queue exhaustion - throw exception to allow higher-level handling
                    // This indicates severe network degradation (>90% packet loss) or server overload
                    // Exception will be caught at GONet layer for error logging and diagnostics
                    throw new ReliableQueueExhaustedException(
                        currentQueueDepth: messageQueue.Count,
                        maxQueueSize: maxMessageQueueSize,
                        droppedMessageSize: bufferLength,
                        channelId: ChannelID);
                }

                ByteBuffer tempBuff = ObjPool<ByteBuffer>.Get();
                tempBuff.SetSize(bufferLength);
                tempBuff.BufferCopy(buffer, 0, 0, bufferLength);
                messageQueue.Enqueue(tempBuff);

                // DIAGNOSTIC: Log when message is queued due to full sendBuffer
                if (EnableDetailedReliableLogging)
                {
                    Log($"[RELIABLE-QUEUE] Message queued (buffer at limit): bytes={bufferLength}, priority={priority}, sendBuffer={sendBufferSize}/{effectiveLimit} (max={sendBuffer.Size}), msgQueue={messageQueue.Count}, oldestUnacked={oldestUnacked}, nextSeq={this.sequence}");
                }

                return;
            }

            ushort sequence = this.sequence;
            var packet = sendBuffer.Insert(sequence);
            if (packet == null)
            {
                // SequenceBuffer rejected insertion (sequence is outside window) - should not happen under normal flow.
                // Drop the message rather than corrupting state.
                Log($"[RELIABLE-SEND-ERROR] conn={ConnectionId} Failed to insert msgSeq={sequence} into sendBuffer (buffer window exceeded). Dropping message bytes={bufferLength}.");
                return;
            }

            this.sequence++;

            // IMPORTANT: SequenceBuffer reuses BufferedPacket instances. Reset all per-message state here.
            packet.writeLock = true;
            packet.acked = false;
            packet.scheduledRemovalTime = 0.0;
            packet.hasBeenTransmitted = false;

            // DIAGNOSTIC: Log when message is assigned a sequence number
            if (EnableDetailedReliableLogging)
            {
                // Extract potential GONetId from message bytes (offset 5 = after channel header)
                // GONetId is a uint at offset 5-8 in spawn event messages
                uint possibleGONetId = 0;
                if (bufferLength >= 9)
                {
                    possibleGONetId = (uint)(buffer[5] | (buffer[6] << 8) | (buffer[7] << 16) | (buffer[8] << 24));
                }
                Log($"[RELIABLE-SEQ] Message assigned seq={sequence}: bytes={bufferLength}, possibleGONetId={possibleGONetId}, sendBuffer={sendBufferSize + 1}/{sendBuffer.Size}, oldestUnacked={oldestUnacked}");
                retransmissionCounts[sequence] = 0;

                // DIAGNOSTIC: SceneLoadComplete trace - Stage 6: Assigned sequence number
                if (isSlcSized)
                {
                    string hex = bufferLength >= 16
                        ? BitConverter.ToString(buffer, 0, 16).Replace("-", "")
                        : BitConverter.ToString(buffer, 0, bufferLength).Replace("-", "");
                    Log($"[SLC-TRACE-6] STAGE6_SEQ_ASSIGNED seq={sequence} bytes={bufferLength} hex={hex} sendBufferUtil={sendBufferSize + 1}/{sendBuffer.Size}");
                }
            }

            packet.time = -1.0;

            // ensure size for header
            int varLength = getVariableLengthBytes((ushort)bufferLength);
            packet.buffer.SetSize(bufferLength + 2 + varLength);

            using (var writer = ByteArrayReaderWriter.Get(packet.buffer.InternalBuffer)) {
                writer.Write(sequence);

                writeVariableLengthUShort((ushort)bufferLength, writer);
                writer.WriteBuffer(buffer, bufferLength);
            }

            // signal that packet is ready to be sent
            packet.writeLock = false;
        }

        private void sendAckPacket()
        {
            packetController.SendAck((byte)ChannelID);
        }

        private int getVariableLengthBytes(ushort val)
        {
            if (val > 0x7fff) {
                throw new ArgumentOutOfRangeException();
            }

            byte b2 = (byte)(val >> 7);
            return (b2 != 0) ? 2 : 1;
        }

        private void writeVariableLengthUShort(ushort val, ByteArrayReaderWriter writer)
        {
            if (val > 0x7fff) {
                throw new ArgumentOutOfRangeException();
            }

            byte b1 = (byte)(val & 0x007F); // write the lowest 7 bits
            byte b2 = (byte)(val >> 7);     // write remaining 8 bits

            // if there's a second byte to write, set the continue flag
            if (b2 != 0) {
                b1 |= 0x80;
            }

            // write bytes
            writer.Write(b1);
            if (b2 != 0)
                writer.Write(b2);
        }

        private bool TryReadVariableLengthUShort(ByteArrayReaderWriter reader, int packetLen, out ushort val)
        {
            val = 0;

            if (reader.ReadPosition >= packetLen)
            {
                return false;
            }

            byte b1 = reader.ReadByte();
            val |= (ushort)(b1 & 0x7F);

            if ((b1 & 0x80) != 0)
            {
                if (reader.ReadPosition >= packetLen)
                {
                    return false;
                }

                val |= (ushort)(reader.ReadByte() << 7);
            }

            return true;
        }

        protected List<ushort> tempList = new List<ushort>();
        protected void processSendBuffer()
        {
            int numUnacked = 0;
            for (ushort seq = oldestUnacked; PacketIO.SequenceLessThan(seq, this.sequence); seq++)
                numUnacked++;

            for (ushort seq = oldestUnacked; PacketIO.SequenceLessThan(seq, this.sequence); seq++) {
                // PHASE 1A FIX (PART 2): Use dynamic sendBuffer.Size instead of hardcoded 256
                // This hardcoded value prevented messages beyond the first 256 from being sent
                // even after increasing buffer capacity to 1024 in constructor
                // never send message ID >= ( oldestUnacked + bufferSize )
                if (seq >= (oldestUnacked + sendBuffer.Size))
                    break;

                // CRITICAL FIX: Use RTT-based adaptive timeout instead of hardcoded 0.1s
                // This prevents "Reliability Death Spiral" where aggressive retransmits cause the congestion
                var packet = sendBuffer.Find(seq);
                if (packet != null && !packet.writeLock) {
                    double resendTimeout = GetAdaptiveResendTimeoutSeconds();
                    if (timeSeconds - packet.time < resendTimeout)
                        continue;

                    bool packetFits = false;

                    if (packet.buffer.Length < config.FragmentThreshold)
                        packetFits = (messagePacker.Length + packet.buffer.Length) <= (config.FragmentThreshold - Defines.MAX_PACKET_HEADER_BYTES);
                    else
                        packetFits = (messagePacker.Length + packet.buffer.Length) <= (config.MaxPacketSize - Defines.FRAGMENT_HEADER_BYTES - Defines.MAX_PACKET_HEADER_BYTES);

                    // if the packet won't fit, flush the message packer
                    if (!packetFits) {
                        flushMessagePacker();
                    }

                    // DIAGNOSTIC: Track first send vs retransmission
                    bool isRetransmit = packet.time >= 0;
                    if (EnableDetailedReliableLogging && isRetransmit)
                    {
                        int retrCount = 0;
                        if (retransmissionCounts.TryGetValue(seq, out retrCount))
                        {
                            retrCount++;
                            retransmissionCounts[seq] = retrCount;
                        }
                        double timeSinceLastSend = timeSeconds - packet.time;
                        Log($"[RELIABLE-RETR] Retransmitting msgSeq={seq}: attempt={retrCount}, timeSinceLastSend={timeSinceLastSend:F3}s, bytes={packet.buffer.Length}, oldestUnacked={oldestUnacked}");
                    }

                    // Mark that this message has been packed into at least one outgoing packet.
                    packet.hasBeenTransmitted = true;

                    packet.time = timeSeconds;

                    int ptr = messagePacker.Length;
                    messagePacker.SetSize(messagePacker.Length + packet.buffer.Length);
                    messagePacker.BufferCopy(packet.buffer, 0, ptr, packet.buffer.Length);

                    tempList.Add(seq);

                    lastMessageSend = timeSeconds;
                }
            }

            // if it has been 0.1 seconds since the last time we sent a message, send an empty message
            if (timeSeconds - lastMessageSend >= 0.1) {
                sendAckPacket();
                lastMessageSend = timeSeconds;
            }

            // flush any remaining messages in message packer
            flushMessagePacker();
        }

        protected void flushMessagePacker(bool bufferAck = true)
        {
            if (messagePacker.Length > 0) {
                ushort outgoingSeq = packetController.SendPacket(messagePacker.InternalBuffer, messagePacker.Length, (byte)ChannelID);
                var outgoingPacket = ackBuffer.Insert(outgoingSeq);

                // store message IDs so we can map packet-level acks to message ID acks
                outgoingPacket.MessageIds.Clear();
                outgoingPacket.MessageIds.AddRange(tempList);

                // DIAGNOSTIC: Log when messages are flushed/transmitted in a packet
                if (EnableDetailedReliableLogging && tempList.Count > 0)
                {
                    string msgSeqs = string.Join(",", tempList);
                    Log($"[RELIABLE-XMIT] Packet transmitted: pktSeq={outgoingSeq}, contains msgSeqs=[{msgSeqs}], totalBytes={messagePacker.Length}, RTT={packetController.RTTMilliseconds:F1}ms");

                    // DIAGNOSTIC: SceneLoadComplete trace - Stage 7: Packet transmitted
                    // If any of our small messages (SLC-sized) are in this batch, log it
                    foreach (var msgSeq in tempList)
                    {
                        var pkt = sendBuffer.Find(msgSeq);
                        if (pkt != null && pkt.buffer.Length >= 25 && pkt.buffer.Length <= 50)
                        {
                            Log($"[SLC-TRACE-7] STAGE7_TRANSMIT pktSeq={outgoingSeq} msgSeq={msgSeq} msgBytes={pkt.buffer.Length} totalPktBytes={messagePacker.Length}");
                        }
                    }
                }

                messagePacker.SetSize(0);
                tempList.Clear();
            }
        }

        protected void ackPacket(ushort seq)
        {
            // first, map seq to message IDs and ack them
            var outgoingPacket = ackBuffer.Find(seq);
            if (outgoingPacket == null)
                return;

            // DIAGNOSTIC: Log when ACK is received for a packet
            if (EnableDetailedReliableLogging && outgoingPacket.MessageIds.Count > 0)
            {
                string msgSeqs = string.Join(",", outgoingPacket.MessageIds);
                Log($"[RELIABLE-ACK] Received ACK: pktSeq={seq}, confirming msgSeqs=[{msgSeqs}], RTT={packetController.RTTMilliseconds:F1}ms, oldestUnacked={oldestUnacked}");

                // Cleanup retransmission tracking for ACKed messages
                foreach (var msgId in outgoingPacket.MessageIds)
                {
                    retransmissionCounts.Remove(msgId);
                }
            }

            // PHASE 3 FIX (December 2025): Mark messages as ACKed but DON'T remove from sendBuffer yet.
            // Messages are only removed when oldestUnacked advances past them.
            // This ensures that even if a message is falsely ACKed (due to connection aliasing in hot standby mesh),
            // it remains in the buffer and can be retransmitted by sender-side gap detection.
            for (int i = 0; i < outgoingPacket.MessageIds.Count; i++) {
                ushort messageID = outgoingPacket.MessageIds[i];

                var packet = sendBuffer.Find(messageID);
                if (packet != null) {
                    // SAFETY GUARD: Never treat a message as ACKed before we've ever transmitted it.
                    // This can happen if a stale ackBuffer mapping is hit (e.g., cross-connection delivery or session reset).
                    // If we incorrectly set writeLock=true here, the message may never be sent, permanently stalling nextExpected on the receiver.
                    if (!packet.hasBeenTransmitted)
                    {
                        Log($"[RELIABLE-ACK-UNSENT] conn={ConnectionId} Ignoring ACK for msgSeq={messageID} in pktSeq={seq} because it has not been transmitted yet. " +
                            "This indicates stale ACK state or cross-connection delivery; leaving message unacked so it can be sent normally.");
                        continue;
                    }

                    packet.acked = true;
                    packet.writeLock = true;  // Prevent normal retransmission

                    // Track highest ACKed sequence for sender-side gap detection
                    if (PacketIO.SequenceGreaterThan(messageID, highestAckedSequence))
                    {
                        highestAckedSequence = messageID;
                    }
                }
            }

            // Update oldest unacked message - now checks acked flag instead of buffer existence
            // Find the first message that is either not in buffer (never sent) or not yet acked
            ushort previousOldestUnacked = oldestUnacked;
            bool allAcked = true;
            for (ushort sequence = oldestUnacked; sequence == this.sequence || PacketIO.SequenceLessThan(sequence, this.sequence); sequence++) {
                var packet = sendBuffer.Find(sequence);
                // Message is "unacked" if it exists in buffer and hasn't been ACKed yet
                if (packet != null && !packet.acked) {
                    oldestUnacked = sequence;
                    allAcked = false;
                    break;
                }
            }

            if (allAcked)
                oldestUnacked = this.sequence;

            // PHASE 4 FIX: Schedule removal for messages that oldestUnacked has advanced past.
            // Instead of immediate removal, we schedule removal for after a grace period.
            // This allows stale connection detection to recover from cases where the ONLY
            // remaining message was falsely ACKed (gap detection can't catch this case).
            if (PacketIO.SequenceGreaterThan(oldestUnacked, previousOldestUnacked))
            {
                for (ushort sequence = previousOldestUnacked; PacketIO.SequenceLessThan(sequence, oldestUnacked); sequence++)
                {
                    var packet = sendBuffer.Find(sequence);
                    if (packet != null && packet.scheduledRemovalTime == 0.0)
                    {
                        packet.scheduledRemovalTime = timeSeconds + GRACE_PERIOD_SECONDS;

                        if (EnableDetailedReliableLogging)
                        {
                            Log($"[RELIABLE-GRACE] Scheduled removal: msgSeq={sequence}, removalTime={packet.scheduledRemovalTime:F2}s (in {GRACE_PERIOD_SECONDS}s)");
                        }
                    }
                }
            }
        }

        // process incoming packets and turn them into messages
        /// <param name="receiveTimestamp">Transport-level receive timestamp in ticks (0 if unavailable)</param>
        protected void processPacket(ushort seq, byte[] packetData, int packetLen, long receiveTimestamp = 0)
        {
            // DIAGNOSTIC: Log incoming packet with connection identifier
            if (EnableDetailedReliableLogging)
            {
                Log($"[RELIABLE-RECV-PKT] conn={ConnectionId} pktSeq={seq}, bytes={packetLen}, nextExpected={nextReceive}, timestamp={receiveTimestamp}");
            }

            if (packetLen <= 0)
            {
                LogMalformedPacket("empty packet", packetData, packetLen, 0);
                return;
            }

            using (var reader = ByteArrayReaderWriter.Get(packetData)) {
                while (reader.ReadPosition < packetLen) {
                    if (reader.ReadPosition + sizeof(ushort) > packetLen)
                    {
                        LogMalformedPacket("truncated message header", packetData, packetLen, reader.ReadPosition);
                        break;
                    }

                    // get message bytes and send to receive callback
                    ushort messageID = reader.ReadUInt16();
                    if (!TryReadVariableLengthUShort(reader, packetLen, out ushort messageLength))
                    {
                        LogMalformedPacket("truncated message length", packetData, packetLen, reader.ReadPosition);
                        break;
                    }

                    if (messageLength == 0)
                        continue;

                    long remainingBytes = packetLen - reader.ReadPosition;
                    if (messageLength > remainingBytes)
                    {
                        LogMalformedPacket($"messageLength {messageLength} exceeds remaining {remainingBytes}", packetData, packetLen, reader.ReadPosition);
                        break;
                    }

                    bool isNewMessage = !receiveBuffer.Exists(messageID);
                    if (isNewMessage) {
                        var receivedMessage = receiveBuffer.Insert(messageID);

                        // FIX: Handle null return from Insert() for stale messages
                        // A message can be "stale" if it's more than bufferSize (1024) sequence numbers behind
                        // the current highest received sequence. This can happen under extreme packet loss
                        // where retransmissions arrive very late. Previously this caused a NullReferenceException.
                        if (receivedMessage == null)
                        {
                            // Message is stale - skip it but still read past its bytes in the buffer
                            // so we can process subsequent messages in this packet correctly
                            if (EnableDetailedReliableLogging)
                            {
                                Log($"[RELIABLE-RECV-STALE] conn={ConnectionId} msgSeq={messageID}, bytes={messageLength}, nextExpected={nextReceive}. " +
                                    $"Message is too far behind current receive window.");
                            }
                            reader.SeekRead(reader.ReadPosition + messageLength);
                            continue;
                        }

                        receivedMessage.buffer.SetSize(messageLength);
                        reader.ReadBytesIntoBuffer(receivedMessage.buffer.InternalBuffer, messageLength);

                        // TIMESTAMP FIX: Store the transport-level receive timestamp with the buffered message.
                        // This preserves the ORIGINAL receive time even for out-of-order packets that
                        // get queued in receiveBuffer before delivery.
                        receivedMessage.receiveTimestamp = receiveTimestamp;

                        // DIAGNOSTIC: Log new message received
                        if (EnableDetailedReliableLogging)
                        {
                            // Extract potential GONetId from message (after channel header)
                            uint possibleGONetId = 0;
                            if (messageLength >= 9)
                            {
                                byte[] buf = receivedMessage.buffer.InternalBuffer;
                                possibleGONetId = (uint)(buf[5] | (buf[6] << 8) | (buf[7] << 16) | (buf[8] << 24));
                            }
                            Log($"[RELIABLE-RECV-MSG] conn={ConnectionId} msgSeq={messageID}, bytes={messageLength}, possibleGONetId={possibleGONetId}, nextExpected={nextReceive}, timestamp={receiveTimestamp}");

                            // DIAGNOSTIC: SceneLoadComplete trace - Stage 8: Message received at reliable layer
                            if (messageLength >= 25 && messageLength <= 45)
                            {
                                string hex = messageLength >= 16
                                    ? BitConverter.ToString(receivedMessage.buffer.InternalBuffer, 0, 16).Replace("-", "")
                                    : BitConverter.ToString(receivedMessage.buffer.InternalBuffer, 0, messageLength).Replace("-", "");
                                Log($"[SLC-TRACE-8] STAGE8_RELIABLE_RECV msgSeq={messageID} bytes={messageLength} hex={hex} nextExpected={nextReceive}");
                            }
                        }
                    }
                    else {
                        // DIAGNOSTIC: Log duplicate message (already received)
                        if (EnableDetailedReliableLogging)
                        {
                            Log($"[RELIABLE-RECV-DUP] conn={ConnectionId} msgSeq={messageID}, bytes={messageLength}, nextExpected={nextReceive}");
                        }
                        reader.SeekRead(reader.ReadPosition + messageLength);
                    }

                    // keep returning the next message we're expecting as long as it's available
                    while (receiveBuffer.Exists(nextReceive)) {
                        var msg = receiveBuffer.Find(nextReceive);

                        // DIAGNOSTIC: Log message delivery to application
                        if (EnableDetailedReliableLogging)
                        {
                            Log($"[RELIABLE-DELIVER] conn={ConnectionId} msgSeq={nextReceive}, bytes={msg.buffer.Length}, timestamp={msg.receiveTimestamp}");

                            // DIAGNOSTIC: SceneLoadComplete trace - Stage 9: Delivered to application
                            if (msg.buffer.Length >= 25 && msg.buffer.Length <= 45)
                            {
                                string hex = msg.buffer.Length >= 16
                                    ? BitConverter.ToString(msg.buffer.InternalBuffer, 0, 16).Replace("-", "")
                                    : BitConverter.ToString(msg.buffer.InternalBuffer, 0, msg.buffer.Length).Replace("-", "");
                                Log($"[SLC-TRACE-9] STAGE9_DELIVER_TO_APP msgSeq={nextReceive} bytes={msg.buffer.Length} hex={hex}");
                            }
                        }

                        // TIMESTAMP FIX: Pass the stored receive timestamp from the buffered message.
                        // This ensures time sync gets the ORIGINAL transport receive time, not the
                        // time when we finally delivered this message after waiting for earlier ones.
                        ReceiveCallback(msg.buffer.InternalBuffer, msg.buffer.Length, msg.receiveTimestamp);

                        // PHASE 4 FIX: Track when we last delivered a message to the application
                        // Used for stale connection detection
                        lastDeliveredMessageTime = timeSeconds;

                        receiveBuffer.Remove(nextReceive);
                        nextReceive++;
                    }
                }
            }
        }

        public override string GetUsageStatistics()
        {
            // PHASE 1C FIX: Added transport telemetry for messageQueue and sendBuffer utilization
            // Previously had no visibility into messageQueue depth (caused Test 9 failure)
            // Now exposes critical metrics for diagnosing congestion and transport health
            // See: INVESTIGATION_BATCH_MESSAGE_LOSS_2025-10-12.md Section 10.3

            // Calculate sendBuffer utilization (how full is it?)
            int sendBufferUtilization = 0;
            for (ushort seq = oldestUnacked; PacketIO.SequenceLessThan(seq, this.sequence); seq++) {
                if (sendBuffer.Exists(seq))
                    sendBufferUtilization++;
            }

            StringBuilder stringBuilder = new StringBuilder(2000);

            const string SB = " sendBuffer.Size: ";
            const string RB = " receiveBuffer.Size: ";
            const string AB = " ackBuffer: ";
            const string LBF = " lastBufferFlush: ";
            const string LMS = " lastMessageSend: ";
            const string TS = " timeSeconds: ";
            const string OU = " oldestUnacked: ";
            const string SEQ = " sequence: ";
            const string NR = " nextReceive: ";
            const string LCST = " lastCongestionSwitchTime: ";
            const string MQ = " messageQueue.Count: ";      // NEW: messageQueue depth
            const string SBU = " sendBufferUtilization: ";   // NEW: sendBuffer occupancy

            stringBuilder
                .Append(base.GetUsageStatistics())
                .Append(SB).Append(sendBuffer.Size)
                .Append(RB).Append(receiveBuffer.Size)
                .Append(AB).Append(ackBuffer.Size)
                .Append(LBF).Append(lastBufferFlush)
                .Append(LMS).Append(lastMessageSend)
                .Append(TS).Append(timeSeconds)
                .Append(OU).Append(oldestUnacked)
                .Append(SEQ).Append(sequence)
                .Append(NR).Append(nextReceive)
                .Append(LCST).Append(lastCongestionSwitchTime)
                .Append(MQ).Append(messageQueue.Count)          // NEW: Queue depth visibility
                .Append(SBU).Append(sendBufferUtilization)      // NEW: Buffer utilization visibility
                ;

            return stringBuilder.ToString();
        }
    }
}
