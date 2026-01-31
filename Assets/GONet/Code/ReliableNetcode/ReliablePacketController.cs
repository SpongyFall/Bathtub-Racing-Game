using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

using ReliableNetcode.Utils;

namespace ReliableNetcode
{
    internal class ReliableConfig
    {
        public string Name;
        public int MaxPacketSize;
        public int FragmentThreshold;
        public int MaxFragments;
        public int FragmentSize;
        public int SentPacketBufferSize;
        public int ReceivedPacketBufferSize;
        public int FragmentReassemblyBufferSize;
        public float RTTSmoothFactor;
        public float PacketLossSmoothingFactor;
        public float BandwidthSmoothingFactor;
        public int PacketHeaderSize;

        public Action<byte[], int> TransmitPacketCallback;
        /// <summary>
        /// Callback when a packet is ready for processing.
        /// Parameters: (sequence, buffer, length, receiveTimestamp).
        /// receiveTimestamp is the transport-level receive time in ticks (0 if unavailable).
        /// </summary>
        public Action<ushort, byte[], int, long> ProcessPacketCallback;
        public Action<ushort> AckPacketCallback;

        /// <summary>
        /// DEBUG: Callback for logging ACK details when sending packets.
        /// Parameters: (pktSeq, ack, ackBits, receivedPktsSequence)
        /// </summary>
        public Action<ushort, ushort, uint, ushort> DebugAckSendCallback;

        /// <summary>
        /// DEBUG: Callback for logging ACK details when receiving packets.
        /// Parameters: (pktSeq, ack, ackBits, isAckProcessed, ackSequenceProcessed)
        /// </summary>
        public Action<ushort, ushort, uint, bool, ushort> DebugAckReceiveCallback;

        /// <summary>
        /// DEBUG: Callback for logging rejected ACKs due to impossibly low RTT.
        /// Parameters: (pktSeq, ack_sequence, rttMilliseconds)
        /// </summary>
        public Action<ushort, ushort, float> DebugFalseAckRejectedCallback;

        public const int IMPORTANT_KEEP_THIS_VALUE_EVEN_THOUGH_IT_SEEMED_LIKE_USING_MTU1400_MADE_SENSE = 1024;

        public static ReliableConfig DefaultConfig()
        {
            var config = new ReliableConfig();
            config.Name = "endpoint";
            config.MaxPacketSize = 16 * IMPORTANT_KEEP_THIS_VALUE_EVEN_THOUGH_IT_SEEMED_LIKE_USING_MTU1400_MADE_SENSE;
            config.FragmentThreshold = IMPORTANT_KEEP_THIS_VALUE_EVEN_THOUGH_IT_SEEMED_LIKE_USING_MTU1400_MADE_SENSE;
            config.MaxFragments = 16;
            config.FragmentSize = IMPORTANT_KEEP_THIS_VALUE_EVEN_THOUGH_IT_SEEMED_LIKE_USING_MTU1400_MADE_SENSE;
            config.SentPacketBufferSize = 256;
            config.ReceivedPacketBufferSize = 256;
            config.FragmentReassemblyBufferSize = 64;
            config.RTTSmoothFactor = 0.25f;
            config.PacketLossSmoothingFactor = 0.1f;
            config.BandwidthSmoothingFactor = 0.1f;
            config.PacketHeaderSize = 28;

            return config;
        }
    }

    internal class ReliablePacketController
    {
        public ReliableConfig config;

        public float RTTMilliseconds
        {
            get { return rttMilliseconds; }
        }

        public float PacketLoss
        {
            get { return packetLoss; }
        }

        public float SentBandwidthKBPS
        {
            get { return sentBandwidthKBPS; }
        }

        public float ReceivedBandwidthKBPS
        {
            get { return receivedBandwidthKBPS; }
        }

        public float AckedBandwidthKBPS
        {
            get { return ackedBandwidthKBPS; }
        }

        private double timeSeconds;
        private float rttMilliseconds;
        private float packetLoss;
        private float sentBandwidthKBPS;
        private float receivedBandwidthKBPS;
        private float ackedBandwidthKBPS;
        private ushort sequence;
        private uint sessionId;
        private SequenceBuffer<SentPacketData> sentPackets;
        private SequenceBuffer<ReceivedPacketData> receivedPackets;
        private SequenceBuffer<FragmentReassemblyData> fragmentReassembly;

        /// <summary>
        /// Session identifier included in packet headers to isolate reliability state across resets/failovers.
        /// Packets received with a different session id are dropped before any ACK/sequence processing.
        /// </summary>
        public uint SessionId
        {
            get => sessionId;
            set => sessionId = value;
        }

        public ReliablePacketController(ReliableConfig config, double timeSeconds)
        {
            this.config = config;
            this.timeSeconds = timeSeconds;

            this.sentPackets = new SequenceBuffer<SentPacketData>(config.SentPacketBufferSize);
            this.receivedPackets = new SequenceBuffer<ReceivedPacketData>(config.ReceivedPacketBufferSize);
            this.fragmentReassembly = new SequenceBuffer<FragmentReassemblyData>(config.FragmentReassemblyBufferSize);
            this.sessionId = 0;
        }

        public ushort NextPacketSequence()
        {
            return sequence;
        }

        public void Reset()
        {
            this.sequence = 0;

            for (int i = 0; i < config.FragmentReassemblyBufferSize; i++) {
                FragmentReassemblyData reassemblyData = fragmentReassembly.AtIndex(i);
                if (reassemblyData != null) {
                    reassemblyData.PacketDataBuffer.SetSize(0);
                }
            }

            sentPackets.Reset();
            receivedPackets.Reset();
            fragmentReassembly.Reset();
        }

        public void Update(double newTimeSeconds)
        {
            this.timeSeconds = newTimeSeconds;

            bool doYouCareAboutPayingTheCostToCalculate = false;
            if (doYouCareAboutPayingTheCostToCalculate)
            {
                UpdateUsageStatistics();
            }
        }

        private void UpdateUsageStatistics()
        {
            // calculate packet loss
            {
                uint baseSequence = (uint)((sentPackets.sequence - config.SentPacketBufferSize + 1) + 0xFFFF);

                int numDropped = 0;
                int numSamples = config.SentPacketBufferSize >> 1; // config.SentPacketBufferSize / 2;
                for (int i = 0; i < numSamples; i++)
                {
                    ushort sequence = (ushort)(baseSequence + i);
                    var sentPacketData = sentPackets.Find(sequence);
                    if (sentPacketData != null && !sentPacketData.acked)
                        numDropped++;
                }

                float packetLoss = (float)numDropped / (float)numSamples;
                if (float.IsNaN(packetLoss) || float.IsInfinity(packetLoss))
                {
                    packetLoss = 0;
                }
                if (Math.Abs(this.packetLoss - packetLoss) > 0.00001f)
                {
                    this.packetLoss += (packetLoss - this.packetLoss) * config.PacketLossSmoothingFactor;
                }
                else
                {
                    this.packetLoss = packetLoss;
                }
            }

            // calculate sent bandwidth
            {
                uint baseSequence = (uint)((sentPackets.sequence - config.SentPacketBufferSize + 1) + 0xFFFF);

                int bytesSent = 0;
                double startTime = double.MaxValue;
                double finishTime = 0.0;
                int numSamples = config.SentPacketBufferSize / 2;
                for (int i = 0; i < numSamples; i++)
                {
                    ushort sequence = (ushort)(baseSequence + i);
                    var sentPacketData = sentPackets.Find(sequence);
                    if (sentPacketData == null) continue;

                    bytesSent += (int)sentPacketData.packetBytes;
                    startTime = (startTime < sentPacketData.timeSeconds) ? startTime : sentPacketData.timeSeconds; // Math.Min(startTime, sentPacketData.time);
                    finishTime = (finishTime > sentPacketData.timeSeconds) ? finishTime : sentPacketData.timeSeconds; // Math.Max(finishTime, sentPacketData.time);
                }

                if (startTime != double.MaxValue && finishTime != 0.0)
                {
                    float sentBandwidth = (float)bytesSent / (float)(finishTime - startTime) * 8f / 1000f;
                    if (float.IsNaN(sentBandwidth) || float.IsInfinity(sentBandwidth))
                    {
                        sentBandwidth = 0;
                    }
                    if (Math.Abs(this.sentBandwidthKBPS - sentBandwidth) > 0.00001f)
                    {
                        this.sentBandwidthKBPS += (sentBandwidth - this.sentBandwidthKBPS) * config.BandwidthSmoothingFactor;
                    }
                    else
                    {
                        this.sentBandwidthKBPS = sentBandwidth;
                    }
                }
            }

            // calculate received bandwidth
            lock (receivedPackets)
            {
                uint baseSequence = (uint)((receivedPackets.sequence - config.ReceivedPacketBufferSize + 1) + 0xFFFF);

                int bytesReceived = 0;
                double startTime = double.MaxValue;
                double finishTime = 0.0;
                int numSamples = config.ReceivedPacketBufferSize / 2;
                for (int i = 0; i < numSamples; i++)
                {
                    ushort sequence = (ushort)(baseSequence + i);
                    var receivedPacketData = receivedPackets.Find(sequence);
                    if (receivedPacketData == null) continue;

                    bytesReceived += (int)receivedPacketData.packetBytes;
                    startTime = (startTime < receivedPacketData.time) ? startTime : receivedPacketData.time; // Math.Min(startTime, receivedPacketData.time);
                    finishTime = (finishTime > receivedPacketData.time) ? finishTime : receivedPacketData.time; // Math.Max(finishTime, receivedPacketData.time);
                }

                if (startTime != double.MaxValue && finishTime != 0.0)
                {
                    float receivedBandwidth = (float)bytesReceived / (float)(finishTime - startTime) * 8f / 1000f;
                    if (float.IsNaN(receivedBandwidth) || float.IsInfinity(receivedBandwidth))
                    {
                        receivedBandwidth = 0;
                    }
                    if (Math.Abs(this.receivedBandwidthKBPS - receivedBandwidth) > 0.00001f)
                    {
                        this.receivedBandwidthKBPS += (receivedBandwidth - this.receivedBandwidthKBPS) * config.BandwidthSmoothingFactor;
                    }
                    else
                    {
                        this.receivedBandwidthKBPS = receivedBandwidth;
                    }
                }
            }

            // calculate acked bandwidth
            {
                uint baseSequence = (uint)((sentPackets.sequence - config.SentPacketBufferSize + 1) + 0xFFFF);

                int bytesSent = 0;
                double startTime = double.MaxValue;
                double finishTime = 0.0;
                int numSamples = config.SentPacketBufferSize / 2;
                for (int i = 0; i < numSamples; i++)
                {
                    ushort sequence = (ushort)(baseSequence + i);
                    var sentPacketData = sentPackets.Find(sequence);
                    if (sentPacketData == null || sentPacketData.acked == false) continue;

                    bytesSent += (int)sentPacketData.packetBytes;
                    startTime = (startTime < sentPacketData.timeSeconds) ? startTime : sentPacketData.timeSeconds; // Math.Min(startTime, sentPacketData.time);
                    finishTime = (finishTime > sentPacketData.timeSeconds) ? finishTime : sentPacketData.timeSeconds; // Math.Max(finishTime, sentPacketData.time);
                }

                if (startTime != double.MaxValue && finishTime != 0.0)
                {
                    float ackedBandwidth = (float)bytesSent / (float)(finishTime - startTime) * 8f / 1000f;
                    if (float.IsNaN(ackedBandwidth) || float.IsInfinity(ackedBandwidth))
                    {
                        ackedBandwidth = 0;
                    }
                    if (Math.Abs(this.ackedBandwidthKBPS - ackedBandwidth) > 0.00001f)
                    {
                        this.ackedBandwidthKBPS += (ackedBandwidth - this.ackedBandwidthKBPS) * config.BandwidthSmoothingFactor;
                    }
                    else
                    {
                        this.ackedBandwidthKBPS = ackedBandwidth;
                    }
                }
            }

            //GONet.GONetLog.Info("hashCode[" + GetHashCode() + "] statistics: " + GetUsageStatistics());
        }

        public void SendAck(byte channelID)
        {
            ushort ack;
            uint ackBits;

            ushort receivedPktsSeq;
            lock( receivedPackets )
            {
                receivedPackets.GenerateAckBits(out ack, out ackBits);
                receivedPktsSeq = receivedPackets.sequence;
            }

            // DEBUG: Log ACK details being sent (sequence 0xFFFF indicates ACK-only packet)
            config.DebugAckSendCallback?.Invoke(0xFFFF, ack, ackBits, receivedPktsSeq);

            byte[] transmitData = BufferPool.GetBuffer(16);
            int headerBytes = PacketIO.WriteAckPacket(transmitData, channelID, sessionId, ack, ackBits);

            config.TransmitPacketCallback(transmitData, headerBytes);

            BufferPool.ReturnBuffer(transmitData);
        }

        public ushort SendPacket(byte[] packetData, int length, byte channelID)
        {
            if (length > config.MaxPacketSize)
                throw new ArgumentOutOfRangeException(string.Concat("Packet is too large to send, max packet size is ", config.MaxPacketSize, " bytes"));

            ushort sequence = this.sequence++;
            ushort ack;
            uint ackBits;

            ushort receivedPktsSeq;
            lock (receivedPackets)
            {
                receivedPackets.GenerateAckBits(out ack, out ackBits);
                receivedPktsSeq = receivedPackets.sequence;
            }

            // DEBUG: Log ACK details being sent
            config.DebugAckSendCallback?.Invoke(sequence, ack, ackBits, receivedPktsSeq);

            SentPacketData sentPacketData = sentPackets.Insert(sequence);
            sentPacketData.timeSeconds = this.timeSeconds;
            sentPacketData.packetBytes = (uint)(config.PacketHeaderSize + length);
            sentPacketData.acked = false;

            if (length <= config.FragmentThreshold) {
                // regular packet

                byte[] transmitData = BufferPool.GetBuffer(2048);
                int headerBytes = PacketIO.WritePacketHeader(transmitData, channelID, sessionId, sequence, ack, ackBits);
                int transmitBufferLength = length + headerBytes;

                Buffer.BlockCopy(packetData, 0, transmitData, headerBytes, length);

                config.TransmitPacketCallback(transmitData, transmitBufferLength);

                BufferPool.ReturnBuffer(transmitData);
            }
            else {
                // fragmented packet

                byte[] packetHeader = BufferPool.GetBuffer(Defines.MAX_PACKET_HEADER_BYTES);

                int packetHeaderBytes = 0;

                try {
                    packetHeaderBytes = PacketIO.WritePacketHeader(packetHeader, channelID, sessionId, sequence, ack, ackBits);
                }
                catch {
                    throw;
                }

                int numFragments = (length / config.FragmentSize) + ((length % config.FragmentSize) != 0 ? 1 : 0);
                //int fragmentBufferSize = Defines.FRAGMENT_HEADER_BYTES + Defines.MAX_PACKET_HEADER_BYTES + config.FragmentSize;

                byte[] fragmentPacketData = BufferPool.GetBuffer(2048);
                int qpos = 0;

                byte prefixByte = 1;
                prefixByte |= (byte)((channelID & 0x03) << 6);

                for (int fragmentID = 0; fragmentID < numFragments; fragmentID++) {
                    using (var writer = ByteArrayReaderWriter.Get(fragmentPacketData)) {
                        writer.Write(prefixByte);
                        writer.Write(channelID);
                        writer.Write(sessionId);
                        writer.Write(sequence);
                        writer.Write((byte)fragmentID);
                        writer.Write((byte)(numFragments - 1));

                        if (fragmentID == 0) {
                            writer.WriteBuffer(packetHeader, packetHeaderBytes);
                        }

                        int bytesToCopy = config.FragmentSize;
                        if (qpos + bytesToCopy > length)
                            bytesToCopy = length - qpos;

                        for (int i = 0; i < bytesToCopy; i++)
                            writer.Write(packetData[qpos++]);

                        int fragmentPacketBytes = (int)writer.WritePosition;
                        config.TransmitPacketCallback(fragmentPacketData, fragmentPacketBytes);
                    }
                }

                BufferPool.ReturnBuffer(packetHeader);
                BufferPool.ReturnBuffer(fragmentPacketData);
            }

            return sequence;
        }

        /// <summary>
        /// Process an incoming packet from the transport layer.
        /// </summary>
        /// <param name="packetData">Packet data</param>
        /// <param name="bufferLength">Packet length</param>
        /// <param name="receiveTimestamp">Transport-level receive timestamp in ticks (0 if unavailable)</param>
        public void ReceivePacket(byte[] packetData, int bufferLength, long receiveTimestamp = 0)
        {
            if (bufferLength > config.MaxPacketSize)
                throw new ArgumentOutOfRangeException("Packet is larger than max packet size");

            if (packetData == null)
                throw new InvalidOperationException("Tried to receive null packet!");

            if (bufferLength > packetData.Length)
                throw new InvalidOperationException("Buffer length exceeds actual packet length!");

            byte prefixByte = packetData[0];

            if ((prefixByte & 1) == 0) {
                // regular packet

                ushort sequence;
                ushort ack;
                uint ackBits;

                byte channelID;

                uint packetSessionId;
                int packetHeaderBytes = PacketIO.ReadPacketHeader(packetData, 0, bufferLength, out channelID, out packetSessionId, out sequence, out ack, out ackBits);

                // Drop packets from a different reliability session (e.g., in-flight packets from before a reset).
                if (packetSessionId != sessionId)
                {
                    return;
                }

                bool isStale;
                lock( receivedPackets )
                    isStale = !receivedPackets.TestInsert(sequence);

                // DEBUG: Log received ACK details (will process ACKs if !isStale or ACK-only packet)
                bool willProcessAck = !isStale || (prefixByte & 0x80) != 0;
                config.DebugAckReceiveCallback?.Invoke(sequence, ack, ackBits, willProcessAck, this.sequence);

                if (!isStale && (prefixByte & 0x80) == 0) {
                    if (packetHeaderBytes >= bufferLength)
                        throw new FormatException("Buffer too small for packet data!");

                    ByteBuffer tempBuffer = ObjPool<ByteBuffer>.Get();
                    tempBuffer.SetSize(bufferLength - packetHeaderBytes);
                    tempBuffer.BufferCopy(packetData, packetHeaderBytes, 0, tempBuffer.Length);

                    // process packet - pass through the transport receive timestamp
                    config.ProcessPacketCallback(sequence, tempBuffer.InternalBuffer, tempBuffer.Length, receiveTimestamp);

                    // add to received buffer
                    lock (receivedPackets) {
                        ReceivedPacketData receivedPacketData = receivedPackets.Insert(sequence);

                        if (receivedPacketData == null)
                            throw new InvalidOperationException("Failed to insert received packet!");

                        receivedPacketData.time = this.timeSeconds;
                        receivedPacketData.packetBytes = (uint)(config.PacketHeaderSize + bufferLength);
                    }

                    ObjPool<ByteBuffer>.Return(tempBuffer);
                }

                if (!isStale || (prefixByte & 0x80) != 0) {
                    // PHASE 6B FIX (December 2025): Validate primary ACK field before processing any ackBits.
                    //
                    // PROBLEM: Cross-connection delivery causes mesh packets to reach main connection.
                    // A mesh packet with ack=31 reaches a connection that only sent 2 packets.
                    // Phase 6A rejects ackSeq 31-2 individually, but ackSeq 0-1 pass because we DID send those.
                    // The RTT check passes because timing is coincidentally valid (~80ms).
                    //
                    // SOLUTION: If the PRIMARY ack field is outside our valid sent range, reject the ENTIRE packet.
                    // This prevents ANY ackBits from being processed, including coincidentally valid ones.
                    //
                    // Valid ACK range: [oldestTrackedSeq, this.sequence)
                    // - this.sequence is the NEXT sequence we will send (haven't sent it yet)
                    // - oldestTrackedSeq is the oldest sequence still in our sent buffer
                    // - Any ack outside this range indicates cross-connection delivery
                    //
                    // ZERO TOLERANCE: There is NO legitimate case where ack >= this.sequence.
                    // The remote can only ACK packets we've actually sent.
                    ushort oldestTrackedSeq_primary = (ushort)(this.sequence - config.SentPacketBufferSize);
                    bool primaryAckWithinSentRange = PacketIO.SequenceLessThan(ack, this.sequence);
                    bool primaryAckNotTooOld = !PacketIO.SequenceLessThan(ack, oldestTrackedSeq_primary);

                    if (!primaryAckWithinSentRange || !primaryAckNotTooOld) {
                        // Primary ACK field is invalid - reject entire packet's ACK processing
                        // This packet originated from a different connection (cross-delivery)
                        // Log with -1000.0f to indicate packet-level rejection (vs -999.0f for per-bit rejection)
                        config.DebugFalseAckRejectedCallback?.Invoke(sequence, ack, -1000.0f);
                        // Skip all ACK processing for this packet
                    }
                    else {
                    for (int i = 0; i < 32; i++) {
                        if ((ackBits & 1) != 0) {
                            ushort ack_sequence = (ushort)(ack - i);

                            // PHASE 6 FIX (December 2025): Validate ACK sequence is within our sent range.
                            // An ACK for a sequence we haven't sent yet is impossible - indicates cross-connection delivery.
                            // This catches the scenario where mesh connection ACKs (ack=30) reach a channel
                            // that has only sent 2 packets (ourSentSeq=2).
                            //
                            // Valid range: [this.sequence - SentPacketBufferSize, this.sequence)
                            // - We've sent sequences from 0 up to (this.sequence - 1)
                            // - We only track the most recent SentPacketBufferSize sequences
                            ushort oldestTrackedSeq = (ushort)(this.sequence - config.SentPacketBufferSize);
                            bool isWithinSentRange = PacketIO.SequenceLessThan(ack_sequence, this.sequence);
                            bool isNotTooOld = !PacketIO.SequenceLessThan(ack_sequence, oldestTrackedSeq);

                            if (!isWithinSentRange || !isNotTooOld) {
                                // ACK for sequence we haven't sent or is too old - cross-connection delivery
                                config.DebugFalseAckRejectedCallback?.Invoke(sequence, ack_sequence, -999.0f); // -999 indicates sequence validation failure
                                ackBits >>= 1;
                                continue;
                            }

                            SentPacketData sentPacketData = sentPackets.Find(ack_sequence);

                            if (sentPacketData != null && !sentPacketData.acked) {
                                float rttMilliseconds = (float)(this.timeSeconds - sentPacketData.timeSeconds) * 1000.0f;

                                // PHASE 5 FIX (December 2025): Reject ACKs with impossibly low RTT.
                                // An RTT of 0.0ms or negative is physically impossible - it means the ACK packet
                                // arrived before or at the same instant the original packet was sent.
                                // This can happen due to cross-connection packet delivery where an ACK intended
                                // for a different client is processed by this client's reliable channel.
                                // The sentPacketData.timeSeconds may be from a completely different session,
                                // causing the RTT calculation to produce impossible values.
                                //
                                // Minimum realistic RTT considerations:
                                // - Localhost: ~0.1-0.5ms (same machine, no network)
                                // - LAN: 1-5ms (local network)
                                // - Internet: 10-300ms+ (depending on distance)
                                //
                                // We use 0.5ms as the threshold because:
                                // - It catches impossible 0.0ms RTT from cross-connection delivery
                                // - It's well below realistic localhost RTT
                                // - It avoids false positives on very fast local networks
                                const float MIN_REALISTIC_RTT_MS = 0.5f;
                                if (rttMilliseconds < MIN_REALISTIC_RTT_MS) {
                                    // Suspicious ACK - RTT is impossibly low.
                                    // Do NOT mark as acked; the higher-level retransmission logic will
                                    // eventually retransmit this packet if it wasn't genuinely received.
                                    // This is a defense-in-depth against false ACKs from cross-connection delivery.

                                    // Log the rejected ACK for debugging
                                    config.DebugFalseAckRejectedCallback?.Invoke(sequence, ack_sequence, rttMilliseconds);

                                    continue;
                                }

                                sentPacketData.acked = true;

                                if (config.AckPacketCallback != null)
                                    config.AckPacketCallback(ack_sequence);

                                if ((this.rttMilliseconds == 0f && rttMilliseconds > 0f) || Math.Abs(this.rttMilliseconds - rttMilliseconds) < 0.00001f) {
                                    this.rttMilliseconds = rttMilliseconds;
                                }
                                else {
                                    this.rttMilliseconds += (rttMilliseconds - this.rttMilliseconds) * config.RTTSmoothFactor;
                                }
                            }
                        }

                        ackBits >>= 1;
                    }
                    }  // Close Phase 6B else block
                }
            }
            else {
                // fragment packet

                int fragmentID;
                int numFragments;
                int fragmentBytes;

                ushort sequence;
                ushort ack;
                uint ackBits;

                byte fragmentChannelID;

                uint fragmentSessionId;
                int fragmentHeaderBytes = PacketIO.ReadFragmentHeader(packetData, 0, bufferLength, config.MaxFragments, config.FragmentSize,
                    out fragmentID, out numFragments, out fragmentBytes, out fragmentSessionId, out sequence, out ack, out ackBits, out fragmentChannelID);

                // Drop fragments from a different reliability session.
                if (fragmentSessionId != sessionId)
                {
                    return;
                }

                FragmentReassemblyData reassemblyData = fragmentReassembly.Find(sequence);
                if (reassemblyData == null) {
                    reassemblyData = fragmentReassembly.Insert(sequence);

                    // failed to insert into buffer (stale)
                    if (reassemblyData == null)
                        return;

                    reassemblyData.Sequence = sequence;
                    reassemblyData.Ack = 0;
                    reassemblyData.AckBits = 0;
                    reassemblyData.NumFragmentsReceived = 0;
                    reassemblyData.NumFragmentsTotal = numFragments;
                    reassemblyData.PacketBytes = 0;
                    Array.Clear(reassemblyData.FragmentReceived, 0, reassemblyData.FragmentReceived.Length);
                }

                if (numFragments != reassemblyData.NumFragmentsTotal)
                    return;

                if (reassemblyData.FragmentReceived[fragmentID])
                    return;

                reassemblyData.NumFragmentsReceived++;
                reassemblyData.FragmentReceived[fragmentID] = true;

                byte[] tempFragmentData = BufferPool.GetBuffer(2048);
                Buffer.BlockCopy(packetData, fragmentHeaderBytes, tempFragmentData, 0, bufferLength - fragmentHeaderBytes);
                
                reassemblyData.StoreFragmentData(fragmentChannelID, fragmentSessionId, sequence, ack, ackBits, fragmentID, config.FragmentSize, tempFragmentData, bufferLength - fragmentHeaderBytes);
                BufferPool.ReturnBuffer(tempFragmentData);

                if (reassemblyData.NumFragmentsReceived == reassemblyData.NumFragmentsTotal) {
                    // grab internal buffer and pass it to ReceivePacket. Internal buffer will be packet marked as normal packet, so it will go through normal packet path

                    // copy into new buffer to remove preceding offset (used to simplify variable length header handling)
                    ByteBuffer temp = ObjPool<ByteBuffer>.Get();
                    temp.SetSize(reassemblyData.PacketDataBuffer.Length - reassemblyData.HeaderOffset);
                    Buffer.BlockCopy(reassemblyData.PacketDataBuffer.InternalBuffer, reassemblyData.HeaderOffset, temp.InternalBuffer, 0, temp.Length);

                    // receive packet
                    this.ReceivePacket(temp.InternalBuffer, temp.Length);

                    // return temp buffer
                    ObjPool<ByteBuffer>.Return(temp);

                    // clear reassembly
                    reassemblyData.PacketDataBuffer.SetSize(0);
                    fragmentReassembly.Remove(sequence);
                }
            }
        }

        public string GetUsageStatistics()
        {
            StringBuilder stringBuilder = new StringBuilder(2000);

            const string RTT = "RTTMilliseconds: ";
            const string PL = " PacketLoss: ";
            const string SB = " SentBandwidthKBPS: ";
            const string RB = " ReceivedBandwidthKBPS: ";
            const string AB = " AckedBandwidthKBPS: ";
            const string SP = " sentPackets.Size: ";
            const string RP = " receivedPackets.Size: ";
            const string FR = " fragmentReassembly.Size: ";

            stringBuilder
                .Append(RTT).Append(RTTMilliseconds)
                .Append(PL).Append(PacketLoss)
                .Append(SB).Append(SentBandwidthKBPS)
                .Append(RB).Append(ReceivedBandwidthKBPS)
                .Append(AB).Append(AckedBandwidthKBPS)
                .Append(SP).Append(sentPackets.Size)
                .Append(RP).Append(receivedPackets.Size)
                .Append(FR).Append(fragmentReassembly.Size)
                ;

            return stringBuilder.ToString();
        }
    }
}
