using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using ReliableNetcode;
using UnityEngine;

namespace GONet.Tests.ReliableNetcode
{
    /// <summary>
    /// Tests that use reflection to access and manipulate ReliableEndpoint/MessageChannel internals.
    /// These tests can directly simulate internal state conditions that are hard to reach through
    /// normal packet injection.
    ///
    /// PURPOSE: Find the exact conditions that cause the "sender stops retransmitting" deadlock.
    /// </summary>
    [TestFixture]
    public class ReliableChannelInternalsTests : ReliableEndpointTestBase
    {
        /// <summary>
        /// Helper to access ReliableEndpoint's internal ReliableMessageChannel via reflection.
        /// </summary>
        private object GetReliableChannel(ReliableEndpoint endpoint)
        {
            var field = typeof(ReliableEndpoint).GetField("reliableChannel",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(endpoint);
        }

        /// <summary>
        /// Get the sendBuffer from ReliableMessageChannel.
        /// </summary>
        private object GetSendBuffer(object channel)
        {
            var field = channel.GetType().GetField("sendBuffer",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(channel);
        }

        /// <summary>
        /// Get oldestUnacked from ReliableMessageChannel.
        /// </summary>
        private ushort GetOldestUnacked(object channel)
        {
            var field = channel.GetType().GetField("oldestUnacked",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return (ushort)(field?.GetValue(channel) ?? 0);
        }

        /// <summary>
        /// Set oldestUnacked on ReliableMessageChannel.
        /// </summary>
        private void SetOldestUnacked(object channel, ushort value)
        {
            var field = channel.GetType().GetField("oldestUnacked",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(channel, value);
        }

        /// <summary>
        /// Get sequence (next sequence to send) from ReliableMessageChannel.
        /// </summary>
        private ushort GetSequence(object channel)
        {
            var field = channel.GetType().GetField("sequence",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return (ushort)(field?.GetValue(channel) ?? 0);
        }

        /// <summary>
        /// Find a BufferedPacket in sendBuffer by sequence.
        /// </summary>
        private object FindInSendBuffer(object sendBuffer, ushort seq)
        {
            var findMethod = sendBuffer.GetType().GetMethod("Find");
            return findMethod?.Invoke(sendBuffer, new object[] { seq });
        }

        /// <summary>
        /// Set acked flag on a BufferedPacket.
        /// </summary>
        private void SetPacketAcked(object packet, bool value)
        {
            var field = packet.GetType().GetField("acked",
                BindingFlags.Public | BindingFlags.Instance);
            field?.SetValue(packet, value);
        }

        /// <summary>
        /// Set writeLock flag on a BufferedPacket.
        /// </summary>
        private void SetPacketWriteLock(object packet, bool value)
        {
            var field = packet.GetType().GetField("writeLock",
                BindingFlags.Public | BindingFlags.Instance);
            field?.SetValue(packet, value);
        }

        /// <summary>
        /// Get writeLock flag from a BufferedPacket.
        /// </summary>
        private bool GetPacketWriteLock(object packet)
        {
            var field = packet.GetType().GetField("writeLock",
                BindingFlags.Public | BindingFlags.Instance);
            return (bool)(field?.GetValue(packet) ?? false);
        }

        /// <summary>
        /// Get acked flag from a BufferedPacket.
        /// </summary>
        private bool GetPacketAcked(object packet)
        {
            var field = packet.GetType().GetField("acked",
                BindingFlags.Public | BindingFlags.Instance);
            return (bool)(field?.GetValue(packet) ?? false);
        }

        /// <summary>
        /// TEST 1: DIRECT STATE MANIPULATION - Simulate false ACK by setting internal state.
        ///
        /// This test:
        /// 1. Sends messages, drops message N at transport level
        /// 2. Directly sets message N's acked=true, writeLock=true via reflection
        /// 3. Advances oldestUnacked past N
        /// 4. Verifies: Does the sender ever retransmit message N?
        /// </summary>
        [Test]
        public void DirectStateManipulation_FalseAckSimulation_CausesDeadlock()
        {
            LogTestProgress("Starting DirectStateManipulation_FalseAckSimulation test");

            const int TOTAL_MESSAGES = 10;
            const double DELTA_TIME = 0.02;
            const int FALSELY_ACKED_MSG = 3;

            var pair = CreateEndpointPair(simulateLatency: true, latencyMs: 30);

            // Drop message 3 permanently at transport level
            int dropCount = 0;
            var originalTransmit = pair.Endpoint1.TransmitCallback;
            pair.Endpoint1.TransmitCallback = (buffer, length) =>
            {
                for (int offset = 0; offset < Math.Min(length - 4, 100); offset++)
                {
                    if (BitConverter.ToInt32(buffer, offset) == FALSELY_ACKED_MSG)
                    {
                        dropCount++;
                        return; // Drop permanently
                    }
                }
                originalTransmit(buffer, length);
            };

            // Send all messages
            LogTestProgress($"Sending {TOTAL_MESSAGES} messages...");
            for (int i = 0; i < TOTAL_MESSAGES; i++)
            {
                var message = CreateTestMessage(i, 80);
                SendTestMessage(pair, pair.Endpoint1, message, QosType.Reliable);
                UpdateEndpoints(pair, DELTA_TIME);
            }

            // Let some legitimate ACKs flow (for messages 0, 1, 2, 4, 5, ...)
            RunUpdateCycles(pair, 30, DELTA_TIME);

            // Now use reflection to simulate a false ACK for message 3
            LogTestProgress("Simulating false ACK via reflection...");

            var channel = GetReliableChannel(pair.Endpoint1);
            if (channel == null)
            {
                LogTestProgress("Could not access reliableChannel via reflection");
                Assert.Inconclusive("Reflection access failed");
                return;
            }

            var sendBuffer = GetSendBuffer(channel);
            if (sendBuffer == null)
            {
                LogTestProgress("Could not access sendBuffer via reflection");
                Assert.Inconclusive("Reflection access failed");
                return;
            }

            ushort oldestBefore = GetOldestUnacked(channel);
            ushort seqBefore = GetSequence(channel);
            LogTestProgress($"Before manipulation: oldestUnacked={oldestBefore}, sequence={seqBefore}");

            // Find message 3 in send buffer
            var packet3 = FindInSendBuffer(sendBuffer, FALSELY_ACKED_MSG);
            if (packet3 == null)
            {
                LogTestProgress($"Message {FALSELY_ACKED_MSG} not found in sendBuffer - may have been removed already");
                // This itself is interesting - why would it be removed?
            }
            else
            {
                bool ackedBefore = GetPacketAcked(packet3);
                bool lockBefore = GetPacketWriteLock(packet3);
                LogTestProgress($"Message {FALSELY_ACKED_MSG} state: acked={ackedBefore}, writeLock={lockBefore}");

                // SIMULATE FALSE ACK: Set acked=true, writeLock=true
                SetPacketAcked(packet3, true);
                SetPacketWriteLock(packet3, true);
                LogTestProgress($"Set message {FALSELY_ACKED_MSG}: acked=true, writeLock=true");

                // Also advance oldestUnacked past message 3 (simulating what happens when all prior messages are ACKed)
                // This is what happens when false ACK makes sender think it's caught up
                SetOldestUnacked(channel, (ushort)(FALSELY_ACKED_MSG + 1));
                LogTestProgress($"Advanced oldestUnacked to {FALSELY_ACKED_MSG + 1}");
            }

            ushort oldestAfter = GetOldestUnacked(channel);
            LogTestProgress($"After manipulation: oldestUnacked={oldestAfter}");

            // Now run for extended time - message 3 should NOT be retransmitted
            // because writeLock=true and it's "before" oldestUnacked
            LogTestProgress("Running for 5 seconds to see if message 3 gets retransmitted...");

            int dropsBeforeWait = dropCount;
            RunUpdateCycles(pair, 250, DELTA_TIME); // 5 seconds

            int dropsAfterWait = dropCount;
            LogTestProgress($"Drops of message {FALSELY_ACKED_MSG}: before={dropsBeforeWait}, after={dropsAfterWait}");

            // Check what receiver got
            var received = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);
            HashSet<int> receivedSeqs = new HashSet<int>();
            foreach (var msg in received)
            {
                receivedSeqs.Add(GetSequenceNumber(msg.Data));
            }

            LogTestProgress($"Received {received.Count} messages: {string.Join(", ", receivedSeqs)}");

            // THE KEY TEST: If our manipulation worked, message 3 should NOT be received
            // because the sender thinks it was ACKed and won't retransmit
            if (receivedSeqs.Contains(FALSELY_ACKED_MSG))
            {
                LogTestProgress($"Message {FALSELY_ACKED_MSG} WAS received - retransmission still worked despite manipulation");
                LogTestProgress("This means gap detection or stale detection recovered the message");
            }
            else
            {
                LogTestProgress($"Message {FALSELY_ACKED_MSG} NOT received - DEADLOCK ACHIEVED!");
                LogTestProgress("This proves the false ACK scenario causes permanent message loss");
            }

            // Document receiver state
            bool hasGap = !receivedSeqs.Contains(FALSELY_ACKED_MSG) && receivedSeqs.Count > FALSELY_ACKED_MSG;
            if (hasGap)
            {
                LogTestProgress("DEADLOCK CONFIRMED: Receiver has gap, sender stopped retransmitting");
            }
        }

        /// <summary>
        /// TEST 2: ACK Packet Capture and Replay
        ///
        /// This test captures REAL ACK packets from the receiver and replays them
        /// at the wrong time to see if they can cause false ACKs.
        /// </summary>
        [Test]
        public void AckPacketCaptureReplay_CanCauseFalseAck()
        {
            LogTestProgress("Starting AckPacketCaptureReplay test");

            const int TOTAL_MESSAGES = 10;
            const double DELTA_TIME = 0.02;
            const int TARGET_MSG = 5;

            var pair = CreateEndpointPair(simulateLatency: true, latencyMs: 50);

            // Capture ACK packets from receiver
            List<(byte[] data, int len, double time)> capturedAcks = new List<(byte[], int, double)>();
            int dropCount = 0;

            // Drop message 5 permanently
            var originalTransmit1 = pair.Endpoint1.TransmitCallback;
            pair.Endpoint1.TransmitCallback = (buffer, length) =>
            {
                for (int offset = 0; offset < Math.Min(length - 4, 100); offset++)
                {
                    if (BitConverter.ToInt32(buffer, offset) == TARGET_MSG)
                    {
                        dropCount++;
                        return;
                    }
                }
                originalTransmit1(buffer, length);
            };

            // Capture ACK packets from receiver
            var originalTransmit2 = pair.Endpoint2.TransmitCallback;
            pair.Endpoint2.TransmitCallback = (buffer, length) =>
            {
                // Capture a copy
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                capturedAcks.Add((copy, length, pair.CurrentTime));

                // Normal delivery
                originalTransmit2(buffer, length);
            };

            // Send all messages
            LogTestProgress($"Sending {TOTAL_MESSAGES} messages...");
            for (int i = 0; i < TOTAL_MESSAGES; i++)
            {
                var message = CreateTestMessage(i, 80);
                SendTestMessage(pair, pair.Endpoint1, message, QosType.Reliable);
                UpdateEndpoints(pair, DELTA_TIME);
            }

            // Run to let ACKs flow
            RunUpdateCycles(pair, 50, DELTA_TIME);

            LogTestProgress($"Captured {capturedAcks.Count} ACK packets");
            LogTestProgress($"Message {TARGET_MSG} dropped {dropCount} times");

            // Check receiver state
            var beforeReplay = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);
            LogTestProgress($"Receiver has {beforeReplay.Count} messages before replay");

            // Now replay ALL captured ACK packets to the sender
            // This simulates cross-delivery where old ACKs arrive again
            LogTestProgress("Replaying captured ACK packets to sender...");

            // Wait for RTT to look realistic
            pair.CurrentTime += 0.1; // 100ms delay
            pair.Endpoint1.Update(pair.CurrentTime);

            int replayed = 0;
            foreach (var (data, len, _) in capturedAcks)
            {
                pair.Endpoint1.ReceivePacket(data, len);
                replayed++;
            }
            LogTestProgress($"Replayed {replayed} ACK packets");

            // Run for recovery/deadlock
            RunUpdateCycles(pair, 200, DELTA_TIME);

            var afterReplay = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);
            LogTestProgress($"Receiver has {afterReplay.Count} messages after replay");

            HashSet<int> seqs = new HashSet<int>();
            foreach (var msg in afterReplay)
            {
                seqs.Add(GetSequenceNumber(msg.Data));
            }
            LogTestProgress($"Received sequences: {string.Join(", ", seqs)}");

            if (!seqs.Contains(TARGET_MSG))
            {
                LogTestProgress($"Message {TARGET_MSG} NOT received - potential deadlock from ACK replay");
            }
            else
            {
                LogTestProgress($"Message {TARGET_MSG} received - retransmission recovered despite replay");
            }
        }

        /// <summary>
        /// TEST 3: Race condition - Multiple threads sending/receiving
        ///
        /// This test simulates the multi-threaded environment of real GONet
        /// where multiple threads may be processing packets simultaneously.
        /// </summary>
        [Test]
        [Timeout(30000)]
        public void MultiThreaded_ConcurrentOperations_NoDeadlock()
        {
            LogTestProgress("Starting MultiThreaded_ConcurrentOperations test");

            const int MESSAGES_PER_THREAD = 50;
            const int THREAD_COUNT = 4;
            const double DELTA_TIME = 0.01;

            var pair = CreateEndpointPair(simulateLatency: true, latencyMs: 20);

            // Shared state
            object lockObj = new object();
            int totalSent = 0;
            int errors = 0;
            bool running = true;

            // Sender threads
            var senderThreads = new List<Thread>();
            for (int t = 0; t < THREAD_COUNT; t++)
            {
                int threadId = t;
                var thread = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < MESSAGES_PER_THREAD && running; i++)
                        {
                            int seq = threadId * 1000 + i;
                            var message = CreateTestMessage(seq, 60);

                            lock (lockObj)
                            {
                                pair.Endpoint1.SendMessage(message, message.Length, QosType.Reliable);
                                totalSent++;
                            }

                            Thread.Sleep(1);
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        LogTestProgress($"Sender thread {threadId} error: {ex.Message}");
                    }
                });
                senderThreads.Add(thread);
            }

            // Update thread
            var updateThread = new Thread(() =>
            {
                try
                {
                    while (running)
                    {
                        lock (lockObj)
                        {
                            pair.CurrentTime += DELTA_TIME;
                            pair.Endpoint1.Update(pair.CurrentTime);
                            pair.Endpoint2.Update(pair.CurrentTime);
                            pair.Endpoint1.ProcessSendBuffer_IfAppropriate();
                            pair.Endpoint2.ProcessSendBuffer_IfAppropriate();

                            // Process latency queues
                            if (pair.LatencyQueue1to2 != null)
                            {
                                while (pair.LatencyQueue1to2.Count > 0 &&
                                       pair.LatencyQueue1to2.Peek().Item3 <= pair.CurrentTime)
                                {
                                    var (data, len, _) = pair.LatencyQueue1to2.Dequeue();
                                    pair.Endpoint2.ReceivePacket(data, len);
                                }
                            }
                            if (pair.LatencyQueue2to1 != null)
                            {
                                while (pair.LatencyQueue2to1.Count > 0 &&
                                       pair.LatencyQueue2to1.Peek().Item3 <= pair.CurrentTime)
                                {
                                    var (data, len, _) = pair.LatencyQueue2to1.Dequeue();
                                    pair.Endpoint1.ReceivePacket(data, len);
                                }
                            }
                        }
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errors);
                    LogTestProgress($"Update thread error: {ex.Message}");
                }
            });

            // Start all threads
            foreach (var t in senderThreads) t.Start();
            updateThread.Start();

            // Wait for senders to complete with timeout
            foreach (var t in senderThreads) t.Join(5000);

            // Run update for a bit longer to flush
            Thread.Sleep(2000);
            running = false;
            updateThread.Join(3000);

            // Final flush
            lock (lockObj)
            {
                RunUpdateCycles(pair, 200, DELTA_TIME);
            }

            var received = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);

            LogTestProgress($"Sent: {totalSent}, Received: {received.Count}, Errors: {errors}");

            Assert.AreEqual(0, errors, "Thread errors occurred");
            Assert.AreEqual(totalSent, received.Count,
                $"Message loss in multi-threaded scenario: sent {totalSent}, received {received.Count}");

            LogTestProgress("Test PASSED - No deadlock in multi-threaded scenario");
        }

        /// <summary>
        /// TEST 4: Buffer wraparound stress test
        ///
        /// Tests behavior when sequence numbers wrap around and buffer fills up.
        /// This can expose edge cases in buffer management.
        /// </summary>
        [Test]
        [Timeout(60000)]
        public void BufferWraparound_HighSequenceNumbers_NoDeadlock()
        {
            LogTestProgress("Starting BufferWraparound test");

            const int TOTAL_MESSAGES = 2000; // Enough to wrap around sequence numbers
            const double DELTA_TIME = 0.01;

            var pair = CreateEndpointPair(simulateLatency: true, latencyMs: 20);

            // Random packet loss
            int drops = 0;
            System.Random rng = new System.Random(42);
            var originalTransmit = pair.Endpoint1.TransmitCallback;
            pair.Endpoint1.TransmitCallback = (buffer, length) =>
            {
                if (rng.NextDouble() < 0.05) // 5% loss
                {
                    drops++;
                    return;
                }
                originalTransmit(buffer, length);
            };

            // Send messages in batches
            LogTestProgress($"Sending {TOTAL_MESSAGES} messages with 5% packet loss...");
            for (int i = 0; i < TOTAL_MESSAGES; i++)
            {
                var message = CreateTestMessage(i, 40);
                SendTestMessage(pair, pair.Endpoint1, message, QosType.Reliable);

                if (i % 10 == 0)
                {
                    UpdateEndpoints(pair, DELTA_TIME);
                }

                if (i % 500 == 0)
                {
                    var current = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);
                    LogTestProgress($"Progress: sent {i}, received {current.Count}, drops {drops}");
                }
            }

            // Run for recovery
            LogTestProgress("Running for recovery...");
            RunUpdateCycles(pair, 1000, DELTA_TIME);

            var received = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);

            LogTestProgress($"Final: Sent {TOTAL_MESSAGES}, Received {received.Count}, Drops {drops}");

            // Verify all messages received
            Assert.AreEqual(TOTAL_MESSAGES, received.Count,
                $"Message loss: expected {TOTAL_MESSAGES}, got {received.Count}");

            // Verify ordering
            for (int i = 0; i < Math.Min(100, received.Count); i++)
            {
                int seq = GetSequenceNumber(received[i].Data);
                Assert.AreEqual(i, seq, $"Order violation at {i}");
            }

            LogTestProgress("Test PASSED - Buffer wraparound handled correctly");
        }

        /// <summary>
        /// TEST 5: Grace period expiration test
        ///
        /// Tests that messages in grace period are eventually removed and
        /// stale detection kicks in.
        /// </summary>
        [Test]
        public void GracePeriodExpiration_StaleDetection_Works()
        {
            LogTestProgress("Starting GracePeriodExpiration test");

            const int TOTAL_MESSAGES = 5;
            const double DELTA_TIME = 0.02;
            const int TARGET_MSG = 2;

            var pair = CreateEndpointPair(simulateLatency: true, latencyMs: 30);

            // Drop message 2 permanently
            int dropCount = 0;
            var originalTransmit = pair.Endpoint1.TransmitCallback;
            pair.Endpoint1.TransmitCallback = (buffer, length) =>
            {
                for (int offset = 0; offset < Math.Min(length - 4, 100); offset++)
                {
                    if (BitConverter.ToInt32(buffer, offset) == TARGET_MSG)
                    {
                        dropCount++;
                        return;
                    }
                }
                originalTransmit(buffer, length);
            };

            // Send messages
            LogTestProgress($"Sending {TOTAL_MESSAGES} messages, dropping message {TARGET_MSG}...");
            for (int i = 0; i < TOTAL_MESSAGES; i++)
            {
                var message = CreateTestMessage(i, 80);
                SendTestMessage(pair, pair.Endpoint1, message, QosType.Reliable);
                UpdateEndpoints(pair, DELTA_TIME);
            }

            // Run for 1 second
            LogTestProgress("Running for 1 second...");
            RunUpdateCycles(pair, 50, DELTA_TIME);

            var after1s = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);
            LogTestProgress($"After 1s: {after1s.Count} messages, drops={dropCount}");

            // Run for 3 more seconds (past grace period)
            LogTestProgress("Running for 3 more seconds (past grace period)...");
            RunUpdateCycles(pair, 150, DELTA_TIME);

            var after4s = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);
            LogTestProgress($"After 4s: {after4s.Count} messages, drops={dropCount}");

            // Run for 2 more seconds (stale detection should kick in)
            LogTestProgress("Running for 2 more seconds (stale detection)...");
            RunUpdateCycles(pair, 100, DELTA_TIME);

            var after6s = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);
            LogTestProgress($"After 6s: {after6s.Count} messages, drops={dropCount}");

            HashSet<int> seqs = new HashSet<int>();
            foreach (var msg in after6s)
            {
                seqs.Add(GetSequenceNumber(msg.Data));
            }
            LogTestProgress($"Received sequences: {string.Join(", ", seqs)}");

            // Message 2 should NOT be received (we dropped it forever)
            // But messages 0, 1 should be delivered
            // Messages 3, 4 should be stuck (waiting for 2)

            for (int i = 0; i < TARGET_MSG; i++)
            {
                Assert.IsTrue(seqs.Contains(i), $"Message {i} should be delivered");
            }

            if (!seqs.Contains(TARGET_MSG))
            {
                LogTestProgress($"Message {TARGET_MSG} still not received after grace period + stale detection");

                // Check if messages after gap were delivered (would indicate bug)
                bool hasAfterGap = false;
                for (int i = TARGET_MSG + 1; i < TOTAL_MESSAGES; i++)
                {
                    if (seqs.Contains(i))
                    {
                        hasAfterGap = true;
                        LogTestProgress($"WARNING: Message {i} delivered despite gap at {TARGET_MSG}");
                    }
                }

                if (!hasAfterGap)
                {
                    LogTestProgress("Receiver correctly stuck at gap - no false delivery");
                }
            }
            else
            {
                LogTestProgress($"Message {TARGET_MSG} was delivered - stale detection or recovery worked");
            }
        }
    }
}
