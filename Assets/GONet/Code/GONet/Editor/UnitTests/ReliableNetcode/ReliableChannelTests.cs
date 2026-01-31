using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using ReliableNetcode;
using UnityEngine;

namespace GONet.Tests.ReliableNetcode
{
    /// <summary>
    /// Tests for reliable channel specific behavior - ordering guarantees
    /// </summary>
    [TestFixture]
    public class ReliableChannelTests : ReliableEndpointTestBase
    {
        [Test]
        public void ReliableChannel_GuaranteesOrder()
        {
            LogTestProgress("Starting ReliableChannel_GuaranteesOrder test");

            var pair = CreateEndpointPair();

            const int MESSAGE_COUNT = 100;
            const double DELTA_TIME = 0.01;

            // Send 100 reliable messages with sequence numbers
            LogTestProgress($"Sending {MESSAGE_COUNT} reliable messages...");
            for (int i = 0; i < MESSAGE_COUNT; i++)
            {
                var message = CreateTestMessage(i, 120);
                SendTestMessage(pair, pair.Endpoint1, message, QosType.Reliable);
                UpdateEndpoints(pair, DELTA_TIME);
            }

            // Process remaining messages
            RunUpdateCycles(pair, 100, DELTA_TIME);

            // Extract reliable messages
            var reliableMessages = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);

            LogTestProgress($"Received {reliableMessages.Count}/{MESSAGE_COUNT} reliable messages");

            // Verify all messages arrived
            Assert.AreEqual(MESSAGE_COUNT, reliableMessages.Count,
                $"Expected {MESSAGE_COUNT} messages, received {reliableMessages.Count}");

            // Verify messages arrived in exact order
            for (int i = 0; i < reliableMessages.Count; i++)
            {
                int sequence = GetSequenceNumber(reliableMessages[i].Data);
                Assert.AreEqual(i, sequence,
                    $"Message {i} has sequence {sequence} - OUT OF ORDER!");
            }

            LogTestProgress("Test PASSED - All messages arrived in order");
        }

        [Test]
        public void SuppressReliableTraffic_QueuesAndDeliversAfterResume()
        {
            LogTestProgress("Starting SuppressReliableTraffic_QueuesAndDeliversAfterResume test");

            var pair = CreateEndpointPair();

            const double DELTA_TIME = 0.01;

            // Suppress reliable traffic on the sender and send a reliable message.
            // The message should be queued locally (not dropped) and delivered after suppression is lifted.
            pair.Endpoint1.SuppressReliableTraffic = true;

            var message = CreateTestMessage(12345, 120);
            SendTestMessage(pair, pair.Endpoint1, message, QosType.Reliable);

            RunUpdateCycles(pair, 50, DELTA_TIME);

            var reliableBeforeResume = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);
            Assert.AreEqual(0, reliableBeforeResume.Count, "Reliable message should not transmit while suppressed");

            // Resume and ensure the queued message is delivered.
            pair.Endpoint1.SuppressReliableTraffic = false;

            RunUpdateCycles(pair, 200, DELTA_TIME);

            var reliableAfterResume = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);
            Assert.AreEqual(1, reliableAfterResume.Count, "Queued reliable message should deliver after suppression is lifted");
            Assert.AreEqual(12345, GetSequenceNumber(reliableAfterResume[0].Data), "Delivered message payload did not match the queued message");

            LogTestProgress("Test PASSED - Reliable traffic suppression queues and resumes correctly");
        }

        [Test]
        public void ReliableChannel_AllMessagesArrive_WithPacketLoss()
        {
            LogTestProgress("Starting ReliableChannel_AllMessagesArrive_WithPacketLoss test");

            var pair = CreateEndpointPair(simulateLatency: true, latencyMs: 50);

            const int MESSAGE_COUNT = 50;
            const double DELTA_TIME = 0.01;

            // Simulate packet loss by randomly dropping some transmissions
            int transmitAttempts = 0;
            int transmitDropped = 0;
            var originalTransmit1 = pair.Endpoint1.TransmitCallback;

            pair.Endpoint1.TransmitCallback = (buffer, length) =>
            {
                transmitAttempts++;
                // Drop 20% of packets to simulate network conditions
                if (UnityEngine.Random.value > 0.8f)
                {
                    transmitDropped++;
                    return; // Drop packet
                }
                originalTransmit1(buffer, length);
            };

            LogTestProgress($"Sending {MESSAGE_COUNT} reliable messages with simulated packet loss...");
            for (int i = 0; i < MESSAGE_COUNT; i++)
            {
                var message = CreateTestMessage(i, 150);
                SendTestMessage(pair, pair.Endpoint1, message, QosType.Reliable);
                UpdateEndpoints(pair, DELTA_TIME);
            }

            // Allow plenty of time for retransmissions
            RunUpdateCycles(pair, 300, DELTA_TIME);

            var reliableMessages = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);

            LogTestProgress($"Transmit attempts: {transmitAttempts}, Dropped: {transmitDropped} ({(float)transmitDropped / transmitAttempts * 100:F1}%)");
            LogTestProgress($"Received {reliableMessages.Count}/{MESSAGE_COUNT} reliable messages");

            // All messages should eventually arrive despite packet loss
            Assert.AreEqual(MESSAGE_COUNT, reliableMessages.Count,
                $"Reliable channel lost messages even with retransmission!");

            // Verify order
            for (int i = 0; i < reliableMessages.Count; i++)
            {
                int sequence = GetSequenceNumber(reliableMessages[i].Data);
                Assert.AreEqual(i, sequence, "Messages out of order despite reliable channel");
            }

            LogTestProgress("Test PASSED - All messages arrived in order despite packet loss");
        }

        [Test]
        public void ReliableChannel_LargeMessages_ArriveInOrder()
        {
            LogTestProgress("Starting ReliableChannel_LargeMessages_ArriveInOrder test");

            var pair = CreateEndpointPair();

            const int MESSAGE_COUNT = 30;
            const double DELTA_TIME = 0.01;

            // Send large messages (near fragmentation threshold)
            LogTestProgress($"Sending {MESSAGE_COUNT} large reliable messages...");
            for (int i = 0; i < MESSAGE_COUNT; i++)
            {
                var message = CreateTestMessage(i, 900); // Large payload
                SendTestMessage(pair, pair.Endpoint1, message, QosType.Reliable);
                UpdateEndpoints(pair, DELTA_TIME);
            }

            RunUpdateCycles(pair, 150, DELTA_TIME);

            var reliableMessages = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);

            LogTestProgress($"Received {reliableMessages.Count}/{MESSAGE_COUNT} large messages");

            Assert.AreEqual(MESSAGE_COUNT, reliableMessages.Count,
                "Large messages lost");

            // Verify order
            for (int i = 0; i < reliableMessages.Count; i++)
            {
                int sequence = GetSequenceNumber(reliableMessages[i].Data);
                Assert.AreEqual(i, sequence, "Large messages out of order");
            }

            LogTestProgress("Test PASSED - Large messages arrived in order");
        }

        [Test]
        public void ReliableChannel_RapidBurst_MaintainsOrder()
        {
            LogTestProgress("Starting ReliableChannel_RapidBurst_MaintainsOrder test");

            var pair = CreateEndpointPair();

            const int BURST_SIZE = 200;

            // Send all messages in rapid succession (single frame burst)
            LogTestProgress($"Sending burst of {BURST_SIZE} messages in rapid succession...");
            for (int i = 0; i < BURST_SIZE; i++)
            {
                var message = CreateTestMessage(i, 100);
                SendTestMessage(pair, pair.Endpoint1, message, QosType.Reliable);
            }

            // Single update to trigger send
            UpdateEndpoints(pair, 0.016);

            // Process with regular updates
            RunUpdateCycles(pair, 300, 0.016);

            var reliableMessages = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);

            LogTestProgress($"Received {reliableMessages.Count}/{BURST_SIZE} messages from rapid burst");

            Assert.AreEqual(BURST_SIZE, reliableMessages.Count,
                $"Messages lost from rapid burst ({reliableMessages.Count}/{BURST_SIZE})");

            // Verify strict ordering
            for (int i = 0; i < reliableMessages.Count; i++)
            {
                int sequence = GetSequenceNumber(reliableMessages[i].Data);
                Assert.AreEqual(i, sequence,
                    $"Rapid burst broke ordering at index {i} (sequence: {sequence})");
            }

            LogTestProgress("Test PASSED - Rapid burst maintained strict ordering");
        }

        [Test]
        [Timeout(20000)]
        public void ReliableEndpoint_ConcurrentSendAndUpdate_DoesNotLoseMessages()
        {
            LogTestProgress("Starting ReliableEndpoint_ConcurrentSendAndUpdate_DoesNotLoseMessages test");

            const int MESSAGE_COUNT = 1000;
            const int SENDER_THREAD_COUNT = 4;

            var endpoint1 = new ReliableEndpoint();
            var endpoint2 = new ReliableEndpoint();

            // Make logs easier to read if enabled
            endpoint1.ConnectionId = "A->B";
            endpoint2.ConnectionId = "B<-A";

            var toEndpoint2 = new ConcurrentQueue<(byte[] data, int len)>();
            var toEndpoint1 = new ConcurrentQueue<(byte[] data, int len)>();

            endpoint1.TransmitCallback = (buffer, length) =>
            {
                var copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                toEndpoint2.Enqueue((copy, length));
            };

            endpoint2.TransmitCallback = (buffer, length) =>
            {
                var copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                toEndpoint1.Enqueue((copy, length));
            };

            var received = new ConcurrentDictionary<int, byte>();
            endpoint2.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                if (length < 4) return;
                int id = BitConverter.ToInt32(buffer, 0);
                received.TryAdd(id, 0);
            };

            bool running = true;
            int nextId = 0;

            Thread updateThread = new Thread(() =>
            {
                double time = 0.0;
                const double dt = 0.002; // 2ms
                while (Volatile.Read(ref running))
                {
                    // Deliver network packets before stepping time (simulates receive callback arriving between frames)
                    while (toEndpoint2.TryDequeue(out var pktTo2))
                    {
                        endpoint2.ReceivePacket(pktTo2.data, pktTo2.len);
                    }

                    while (toEndpoint1.TryDequeue(out var pktTo1))
                    {
                        endpoint1.ReceivePacket(pktTo1.data, pktTo1.len);
                    }

                    time += dt;
                    endpoint1.Update(time);
                    endpoint2.Update(time);
                    endpoint1.ProcessSendBuffer_IfAppropriate();
                    endpoint2.ProcessSendBuffer_IfAppropriate();

                    // Small yield to avoid starving sender threads
                    Thread.Sleep(0);
                }
            });
            updateThread.IsBackground = true;
            updateThread.Start();

            var senderThreads = new List<Thread>(SENDER_THREAD_COUNT);
            for (int t = 0; t < SENDER_THREAD_COUNT; t++)
            {
                var thread = new Thread(() =>
                {
                    while (true)
                    {
                        int id = Interlocked.Increment(ref nextId) - 1;
                        if (id >= MESSAGE_COUNT) break;

                        // Small message with unique ID
                        byte[] msg = new byte[32];
                        Buffer.BlockCopy(BitConverter.GetBytes(id), 0, msg, 0, 4);
                        endpoint1.SendMessage(msg, msg.Length, QosType.Reliable);

                        // Encourage interleaving with update thread
                        Thread.Sleep(0);
                    }
                });

                thread.IsBackground = true;
                senderThreads.Add(thread);
                thread.Start();
            }

            foreach (var t in senderThreads)
            {
                t.Join(5000);
            }

            // Wait for delivery or timeout
            var sw = Stopwatch.StartNew();
            while (received.Count < MESSAGE_COUNT && sw.ElapsedMilliseconds < 8000)
            {
                Thread.Sleep(10);
            }

            Volatile.Write(ref running, false);
            updateThread.Join(2000);

            Assert.AreEqual(MESSAGE_COUNT, received.Count, $"Expected {MESSAGE_COUNT} messages delivered, got {received.Count}");
            for (int i = 0; i < MESSAGE_COUNT; i++)
            {
                Assert.IsTrue(received.ContainsKey(i), $"Missing delivered message id={i}");
            }

            LogTestProgress("Test PASSED - Concurrent send/update delivered all messages");
        }

        [Test]
        public void ReliableSessionId_ResetDropsOldInFlightPackets()
        {
            LogTestProgress("Starting ReliableSessionId_ResetDropsOldInFlightPackets test");

            var pair = CreateEndpointPair(simulateLatency: false);

            var bufferedPackets = new List<(byte[] Data, int Length)>();

            // Capture pre-reset packets from Endpoint1 without delivering them.
            pair.Endpoint1.TransmitCallback = (buffer, length) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                bufferedPackets.Add((copy, length));
            };

            // Drop ACKs during the pre-reset capture phase.
            pair.Endpoint2.TransmitCallback = (_, __) => { };

            // Send a message in session 0 and flush once to emit the packet(s).
            var preResetMessage = CreateTestMessage(1, 80);
            SendTestMessage(pair, pair.Endpoint1, preResetMessage, QosType.Reliable);
            UpdateEndpoints(pair, 0.01);

            Assert.Greater(bufferedPackets.Count, 0, "Expected at least one pre-reset packet captured");

            // Simulate a coordinated reliability reset that changes the session id.
            const uint NEW_SESSION_ID = 123u;
            pair.Endpoint1.ReliableSessionId = NEW_SESSION_ID;
            pair.Endpoint2.ReliableSessionId = NEW_SESSION_ID;
            pair.Endpoint1.ResetReliableChannel();
            pair.Endpoint2.ResetReliableChannel();

            // Deliver old-session packets after the reset; these must be dropped.
            foreach (var p in bufferedPackets)
            {
                pair.Endpoint2.ReceivePacket(p.Data, p.Length);
            }

            Assert.AreEqual(0, pair.Endpoint2Received.Count, "Old-session packets should be dropped after session id reset");

            // Restore normal bidirectional delivery for post-reset traffic.
            pair.Endpoint1.TransmitCallback = (buffer, length) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                pair.Endpoint2.ReceivePacket(copy, length);
            };
            pair.Endpoint2.TransmitCallback = (buffer, length) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                pair.Endpoint1.ReceivePacket(copy, length);
            };

            var postResetMessage = CreateTestMessage(2, 80);
            SendTestMessage(pair, pair.Endpoint1, postResetMessage, QosType.Reliable);
            RunUpdateCycles(pair, 50, 0.01);

            var receivedReliable = pair.Endpoint2Received.FindAll(m => m.Channel == QosType.Reliable);
            Assert.IsTrue(receivedReliable.Exists(m => GetSequenceNumber(m.Data) == 2),
                "Post-reset message should be delivered");

            LogTestProgress("Test PASSED - Old in-flight packets cannot poison new session");
        }
    }
}
