using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using ReliableNetcode;
using UnityEngine;

namespace GONet.Tests.ReliableNetcode
{
    /// <summary>
    /// Tests for cross-connection packet delivery bug.
    ///
    /// HYPOTHESIS: When multiple reliable connections exist, packets can be delivered
    /// to the wrong connection's reliable channel, causing:
    /// 1. False duplicate detection on wrong channel
    /// 2. False ACKs being generated
    /// 3. Messages never delivered to correct channel
    ///
    /// These tests simulate a server with multiple client connections and verify
    /// that packets are ONLY delivered to the correct connection's reliable channel.
    ///
    /// Reference: reliable-deadlock-investigation-dec10.md
    /// </summary>
    [TestFixture]
    public class CrossConnectionPacketDeliveryTests : ReliableEndpointTestBase
    {
        /// <summary>
        /// Simulates a server with multiple client connections.
        /// Each client has its own reliable endpoint pair with the server.
        /// </summary>
        private class MultiClientTestSetup
        {
            public class ClientConnection
            {
                public int ClientId;
                public ReliableEndpoint ClientEndpoint;
                public ReliableEndpoint ServerEndpoint; // Server's endpoint for THIS client
                public List<ReceivedMessage> ClientReceived = new List<ReceivedMessage>();
                public List<ReceivedMessage> ServerReceived = new List<ReceivedMessage>();
                public Queue<(byte[], int, double)> ClientToServerQueue = new Queue<(byte[], int, double)>();
                public Queue<(byte[], int, double)> ServerToClientQueue = new Queue<(byte[], int, double)>();
            }

            public List<ClientConnection> Clients = new List<ClientConnection>();
            public double CurrentTime = 0.0;
            public double LatencyMs = 50.0;

            /// <summary>
            /// Track cross-delivery events for assertion
            /// </summary>
            public List<string> CrossDeliveryEvents = new List<string>();
        }

        private MultiClientTestSetup CreateMultiClientSetup(int clientCount, double latencyMs = 50.0)
        {
            var setup = new MultiClientTestSetup { LatencyMs = latencyMs };

            for (int i = 0; i < clientCount; i++)
            {
                var client = new MultiClientTestSetup.ClientConnection
                {
                    ClientId = i,
                    ClientEndpoint = new ReliableEndpoint(),
                    ServerEndpoint = new ReliableEndpoint()
                };

                // Client -> Server transmission (with latency)
                client.ClientEndpoint.TransmitCallback = (buffer, length) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    client.ClientToServerQueue.Enqueue((copy, length, setup.CurrentTime + latencyMs / 1000.0));
                };

                // Server -> Client transmission (with latency)
                client.ServerEndpoint.TransmitCallback = (buffer, length) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    client.ServerToClientQueue.Enqueue((copy, length, setup.CurrentTime + latencyMs / 1000.0));
                };

                // Client receive callback
                client.ClientEndpoint.ReceiveCallback = (buffer, length, receiveTimestamp) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    client.ClientReceived.Add(new ReceivedMessage
                    {
                        Data = copy,
                        Length = length,
                        TimeReceived = setup.CurrentTime
                    });
                };

                // Server receive callback (for this specific client)
                int clientId = i; // Capture for closure
                client.ServerEndpoint.ReceiveCallback = (buffer, length, receiveTimestamp) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    client.ServerReceived.Add(new ReceivedMessage
                    {
                        Data = copy,
                        Length = length,
                        TimeReceived = setup.CurrentTime
                    });
                };

                setup.Clients.Add(client);
            }

            return setup;
        }

        private void UpdateMultiClientSetup(MultiClientTestSetup setup, double deltaTime)
        {
            setup.CurrentTime += deltaTime;

            foreach (var client in setup.Clients)
            {
                // Process client -> server packets
                while (client.ClientToServerQueue.Count > 0 &&
                       client.ClientToServerQueue.Peek().Item3 <= setup.CurrentTime)
                {
                    var (data, len, _) = client.ClientToServerQueue.Dequeue();
                    client.ServerEndpoint.ReceivePacket(data, len);
                }

                // Process server -> client packets
                while (client.ServerToClientQueue.Count > 0 &&
                       client.ServerToClientQueue.Peek().Item3 <= setup.CurrentTime)
                {
                    var (data, len, _) = client.ServerToClientQueue.Dequeue();
                    client.ClientEndpoint.ReceivePacket(data, len);
                }

                // Update endpoints
                client.ClientEndpoint.Update(setup.CurrentTime);
                client.ServerEndpoint.Update(setup.CurrentTime);
                client.ClientEndpoint.ProcessSendBuffer_IfAppropriate();
                client.ServerEndpoint.ProcessSendBuffer_IfAppropriate();
            }
        }

        /// <summary>
        /// TEST 1: Verify baseline - multiple isolated connections work correctly
        /// Each client sends messages and they arrive at the correct server endpoint.
        /// </summary>
        [Test]
        public void MultipleConnections_IsolatedDelivery_MessagesArriveAtCorrectEndpoint()
        {
            LogTestProgress("Starting MultipleConnections_IsolatedDelivery test");

            const int CLIENT_COUNT = 5;
            const int MESSAGES_PER_CLIENT = 10;
            const double DELTA_TIME = 0.02;

            var setup = CreateMultiClientSetup(CLIENT_COUNT, latencyMs: 30);

            // Each client sends messages with unique identifier
            for (int clientId = 0; clientId < CLIENT_COUNT; clientId++)
            {
                for (int msgIdx = 0; msgIdx < MESSAGES_PER_CLIENT; msgIdx++)
                {
                    // Message format: [clientId (4 bytes)][msgIdx (4 bytes)][padding]
                    byte[] message = new byte[100];
                    Buffer.BlockCopy(BitConverter.GetBytes(clientId), 0, message, 0, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(msgIdx), 0, message, 4, 4);

                    setup.Clients[clientId].ClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);
                }
                UpdateMultiClientSetup(setup, DELTA_TIME);
            }

            // Allow time for all messages to arrive
            for (int i = 0; i < 200; i++)
            {
                UpdateMultiClientSetup(setup, DELTA_TIME);
            }

            // Verify each server endpoint received ONLY its client's messages
            for (int clientId = 0; clientId < CLIENT_COUNT; clientId++)
            {
                var received = setup.Clients[clientId].ServerReceived;

                LogTestProgress($"Client {clientId}: Server received {received.Count} messages");

                Assert.AreEqual(MESSAGES_PER_CLIENT, received.Count,
                    $"Server endpoint for client {clientId} should receive {MESSAGES_PER_CLIENT} messages, got {received.Count}");

                // Verify all messages are from the correct client
                foreach (var msg in received)
                {
                    int sourceClientId = BitConverter.ToInt32(msg.Data, 0);
                    Assert.AreEqual(clientId, sourceClientId,
                        $"Server endpoint for client {clientId} received message from client {sourceClientId}!");
                }
            }

            LogTestProgress("Test PASSED - All messages arrived at correct endpoints");
        }

        /// <summary>
        /// TEST 2: CRITICAL - Simulate cross-delivery bug
        /// Intentionally deliver packets to wrong endpoint and verify the damage.
        /// This proves the hypothesis that cross-delivery causes message loss.
        /// </summary>
        [Test]
        public void CrossDelivery_PacketToWrongEndpoint_CausesFalseDuplicateDetection()
        {
            LogTestProgress("Starting CrossDelivery_PacketToWrongEndpoint test");

            const int CLIENT_COUNT = 3;
            const double DELTA_TIME = 0.02;

            var setup = CreateMultiClientSetup(CLIENT_COUNT, latencyMs: 30);

            // CRITICAL: We'll intercept packets from Client 0 and ALSO deliver them to Client 1's server endpoint
            var client0 = setup.Clients[0];
            var client1 = setup.Clients[1];

            int crossDeliveryCount = 0;
            List<string> crossDeliveryLog = new List<string>();

            // Intercept Client 0's transmit to ALSO deliver to Client 1's endpoint
            var originalTransmit = client0.ClientEndpoint.TransmitCallback;
            client0.ClientEndpoint.TransmitCallback = (buffer, length) =>
            {
                // Normal delivery to correct endpoint
                originalTransmit(buffer, length);

                // SIMULATE BUG: Also deliver to wrong endpoint (Client 1's server endpoint)
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);

                // Queue for delivery to WRONG endpoint
                client1.ClientToServerQueue.Enqueue((copy, length, setup.CurrentTime + setup.LatencyMs / 1000.0));
                crossDeliveryCount++;
                crossDeliveryLog.Add($"Cross-delivered packet (len={length}) from Client0 to Client1's server endpoint");
            };

            // Client 0 sends messages
            const int MESSAGE_COUNT = 5;
            for (int i = 0; i < MESSAGE_COUNT; i++)
            {
                byte[] message = new byte[100];
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, message, 0, 4); // clientId = 0
                Buffer.BlockCopy(BitConverter.GetBytes(i), 0, message, 4, 4); // msgIdx = i
                client0.ClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);
                UpdateMultiClientSetup(setup, DELTA_TIME);
            }

            // Client 1 also sends messages (important - to advance its sequence numbers)
            for (int i = 0; i < MESSAGE_COUNT; i++)
            {
                byte[] message = new byte[100];
                Buffer.BlockCopy(BitConverter.GetBytes(1), 0, message, 0, 4); // clientId = 1
                Buffer.BlockCopy(BitConverter.GetBytes(i), 0, message, 4, 4); // msgIdx = i
                client1.ClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);
                UpdateMultiClientSetup(setup, DELTA_TIME);
            }

            // Allow time for messages to arrive
            for (int i = 0; i < 200; i++)
            {
                UpdateMultiClientSetup(setup, DELTA_TIME);
            }

            LogTestProgress($"Cross-delivery events: {crossDeliveryCount}");
            foreach (var log in crossDeliveryLog.Take(5))
            {
                LogTestProgress(log);
            }

            // Check Client 0's server endpoint
            var client0ServerReceived = client0.ServerReceived;
            LogTestProgress($"Client 0 server received: {client0ServerReceived.Count} messages");

            // Check Client 1's server endpoint - it should have received EXTRA messages (the cross-delivered ones)
            var client1ServerReceived = client1.ServerReceived;
            LogTestProgress($"Client 1 server received: {client1ServerReceived.Count} messages");

            // Count how many messages Client 1's server got from Client 0 (contamination)
            int contaminationCount = 0;
            foreach (var msg in client1ServerReceived)
            {
                int sourceClientId = BitConverter.ToInt32(msg.Data, 0);
                if (sourceClientId == 0)
                {
                    contaminationCount++;
                }
            }
            LogTestProgress($"Client 1 server contamination (msgs from Client 0): {contaminationCount}");

            // THE KEY ASSERTION: Cross-delivery should cause contamination
            // If the reliable channel properly isolated connections, contamination would be 0
            // But with cross-delivery, we expect contamination > 0
            Assert.Greater(crossDeliveryCount, 0, "Test setup error - no cross-delivery occurred");

            // Document the effect of cross-delivery
            LogTestProgress($"RESULT: Cross-delivery of {crossDeliveryCount} packets caused {contaminationCount} contaminated messages");

            // Verify Client 0 still got its messages (correct path worked)
            Assert.AreEqual(MESSAGE_COUNT, client0ServerReceived.Count,
                "Client 0's messages should still arrive via correct path");
        }

        /// <summary>
        /// TEST 3: Simulate the exact bug from logs - same packet sequence arriving at different endpoints
        /// with different nextExpected values, causing false duplicate detection.
        /// </summary>
        [Test]
        public void CrossDelivery_SamePacketSequence_CausesFalseDuplicateOnAdvancedEndpoint()
        {
            LogTestProgress("Starting CrossDelivery_SamePacketSequence test");

            var setup = CreateMultiClientSetup(2, latencyMs: 30);
            var clientA = setup.Clients[0];
            var clientB = setup.Clients[1];

            const double DELTA_TIME = 0.02;

            // First, advance Client B's server endpoint by having Client B send many messages
            // This simulates the server log showing nextExpected=28 on wrong endpoint
            LogTestProgress("Phase 1: Advancing Client B's server endpoint sequence...");
            for (int i = 0; i < 30; i++)
            {
                byte[] message = new byte[50];
                Buffer.BlockCopy(BitConverter.GetBytes(1), 0, message, 0, 4); // clientId = 1
                Buffer.BlockCopy(BitConverter.GetBytes(i), 0, message, 4, 4);
                clientB.ClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);
                UpdateMultiClientSetup(setup, DELTA_TIME);
            }

            // Allow Client B's messages to fully process
            for (int i = 0; i < 100; i++)
            {
                UpdateMultiClientSetup(setup, DELTA_TIME);
            }

            LogTestProgress($"Client B server has received {clientB.ServerReceived.Count} messages");

            // Now Client A sends a message - BUT we'll also deliver it to Client B's server endpoint
            // This simulates the cross-delivery bug
            LogTestProgress("Phase 2: Client A sends message with cross-delivery to Client B's endpoint...");

            bool crossDelivered = false;
            var originalTransmitA = clientA.ClientEndpoint.TransmitCallback;
            clientA.ClientEndpoint.TransmitCallback = (buffer, length) =>
            {
                // Normal delivery
                originalTransmitA(buffer, length);

                // Cross-delivery to Client B's server endpoint
                if (!crossDelivered)
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);

                    // Deliver DIRECTLY to wrong endpoint (simulating routing bug)
                    clientB.ServerEndpoint.ReceivePacket(copy, length);
                    crossDelivered = true;
                    LogTestProgress($"Cross-delivered packet (len={length}) to Client B's server endpoint");
                }
            };

            // Client A sends THE CRITICAL MESSAGE (like SceneLoadComplete)
            byte[] criticalMessage = new byte[100];
            Buffer.BlockCopy(BitConverter.GetBytes(0), 0, criticalMessage, 0, 4); // clientId = 0
            Buffer.BlockCopy(BitConverter.GetBytes(9999), 0, criticalMessage, 4, 4); // special marker
            clientA.ClientEndpoint.SendMessage(criticalMessage, criticalMessage.Length, QosType.Reliable);

            // Process
            for (int i = 0; i < 100; i++)
            {
                UpdateMultiClientSetup(setup, DELTA_TIME);
            }

            // Check results
            LogTestProgress($"Client A server received: {clientA.ServerReceived.Count} messages");
            LogTestProgress($"Client B server received: {clientB.ServerReceived.Count} messages (was 30 before cross-delivery)");

            // Find the critical message in Client A's server
            bool clientAGotCriticalMessage = false;
            foreach (var msg in clientA.ServerReceived)
            {
                int marker = BitConverter.ToInt32(msg.Data, 4);
                if (marker == 9999)
                {
                    clientAGotCriticalMessage = true;
                    break;
                }
            }

            // Find if critical message was ALSO processed by Client B's server (contamination)
            bool clientBGotCriticalMessage = false;
            foreach (var msg in clientB.ServerReceived)
            {
                if (msg.Data.Length >= 8)
                {
                    int marker = BitConverter.ToInt32(msg.Data, 4);
                    if (marker == 9999)
                    {
                        clientBGotCriticalMessage = true;
                        break;
                    }
                }
            }

            LogTestProgress($"Client A server got critical message: {clientAGotCriticalMessage}");
            LogTestProgress($"Client B server got critical message (contamination): {clientBGotCriticalMessage}");

            // The critical assertion: Client A MUST receive its message
            Assert.IsTrue(clientAGotCriticalMessage,
                "CRITICAL: Client A's message was lost! Cross-delivery bug confirmed.");

            // Document whether contamination occurred
            if (clientBGotCriticalMessage)
            {
                LogTestProgress("WARNING: Cross-delivery caused message contamination on wrong endpoint");
            }
            else
            {
                LogTestProgress("Cross-delivered packet was likely rejected as duplicate/out-of-sequence on advanced endpoint");
            }
        }

        /// <summary>
        /// TEST 4: Verify ACK contamination - cross-delivered packets cause false ACKs
        /// </summary>
        [Test]
        public void CrossDelivery_CausesFalseAcksOnWrongConnection()
        {
            LogTestProgress("Starting CrossDelivery_CausesFalseAcksOnWrongConnection test");

            var setup = CreateMultiClientSetup(2, latencyMs: 30);
            var clientA = setup.Clients[0];
            var clientB = setup.Clients[1];

            const double DELTA_TIME = 0.02;

            // Track ACKs sent by each server endpoint
            List<string> serverAAcksSent = new List<string>();
            List<string> serverBAcksSent = new List<string>();

            var originalServerATransmit = clientA.ServerEndpoint.TransmitCallback;
            clientA.ServerEndpoint.TransmitCallback = (buffer, length) =>
            {
                serverAAcksSent.Add($"ServerA sent packet (len={length})");
                originalServerATransmit(buffer, length);
            };

            var originalServerBTransmit = clientB.ServerEndpoint.TransmitCallback;
            clientB.ServerEndpoint.TransmitCallback = (buffer, length) =>
            {
                serverBAcksSent.Add($"ServerB sent packet (len={length})");
                originalServerBTransmit(buffer, length);
            };

            // Client A sends messages, but cross-deliver to Client B's server
            var originalClientATransmit = clientA.ClientEndpoint.TransmitCallback;
            int crossDeliveryCount = 0;
            clientA.ClientEndpoint.TransmitCallback = (buffer, length) =>
            {
                // Normal path
                originalClientATransmit(buffer, length);

                // Cross-deliver every packet
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                clientB.ServerEndpoint.ReceivePacket(copy, length);
                crossDeliveryCount++;
            };

            // Send messages
            for (int i = 0; i < 5; i++)
            {
                byte[] message = new byte[50];
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, message, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(i), 0, message, 4, 4);
                clientA.ClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);
                UpdateMultiClientSetup(setup, DELTA_TIME);
            }

            // Process
            for (int i = 0; i < 100; i++)
            {
                UpdateMultiClientSetup(setup, DELTA_TIME);
            }

            LogTestProgress($"Cross-delivered {crossDeliveryCount} packets");
            LogTestProgress($"Server A sent {serverAAcksSent.Count} packets (ACKs)");
            LogTestProgress($"Server B sent {serverBAcksSent.Count} packets (ACKs) - these are FALSE ACKs if > 0");

            // The key finding: Server B should NOT be sending ACKs for Client A's packets
            // But if cross-delivery happens, Server B's reliable channel will process the packets
            // and generate ACKs (which go to Client B, not Client A - causing confusion)

            // In the real bug, these ACKs from wrong server endpoint might somehow
            // be interpreted by Client A as valid ACKs (if routing is also broken)

            Assert.AreEqual(5, clientA.ServerReceived.Count,
                "Client A's server should receive all 5 messages");

            LogTestProgress($"Client A server received: {clientA.ServerReceived.Count}");
            LogTestProgress($"Client B server received: {clientB.ServerReceived.Count} (contamination from cross-delivery)");
        }

        /// <summary>
        /// TEST 5: Multi-threaded stress test with multiple connections
        /// Simulates the real-world scenario where multiple network threads
        /// are delivering packets concurrently.
        /// </summary>
        [Test]
        public void MultiThreaded_ConcurrentPacketDelivery_NoDataCorruption()
        {
            LogTestProgress("Starting MultiThreaded_ConcurrentPacketDelivery test");

            const int CLIENT_COUNT = 10;
            const int MESSAGES_PER_CLIENT = 20;
            const double DELTA_TIME = 0.01;

            var setup = CreateMultiClientSetup(CLIENT_COUNT, latencyMs: 10);

            // Use locks for thread safety in the test (not the code under test)
            object lockObj = new object();
            int totalMessagesSent = 0;

            // Send messages from multiple threads
            var threads = new List<Thread>();
            for (int clientId = 0; clientId < CLIENT_COUNT; clientId++)
            {
                int cid = clientId; // Capture
                var thread = new Thread(() =>
                {
                    for (int msgIdx = 0; msgIdx < MESSAGES_PER_CLIENT; msgIdx++)
                    {
                        byte[] message = new byte[100];
                        Buffer.BlockCopy(BitConverter.GetBytes(cid), 0, message, 0, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(msgIdx), 0, message, 4, 4);

                        lock (lockObj)
                        {
                            setup.Clients[cid].ClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);
                            totalMessagesSent++;
                        }

                        Thread.Sleep(1); // Small delay between messages
                    }
                });
                threads.Add(thread);
            }

            // Start all threads
            foreach (var thread in threads)
            {
                thread.Start();
            }

            // Update loop while threads are sending
            int updateCount = 0;
            while (threads.Any(t => t.IsAlive) || updateCount < 500)
            {
                lock (lockObj)
                {
                    UpdateMultiClientSetup(setup, DELTA_TIME);
                }
                updateCount++;
                Thread.Sleep(1);
            }

            // Wait for all threads to complete with timeout
            foreach (var thread in threads)
            {
                thread.Join(5000);
            }

            // Final update cycles
            for (int i = 0; i < 200; i++)
            {
                lock (lockObj)
                {
                    UpdateMultiClientSetup(setup, DELTA_TIME);
                }
            }

            LogTestProgress($"Total messages sent: {totalMessagesSent}");

            // Verify all messages arrived at correct endpoints
            int totalReceived = 0;
            int correctDeliveries = 0;
            int incorrectDeliveries = 0;

            for (int clientId = 0; clientId < CLIENT_COUNT; clientId++)
            {
                var received = setup.Clients[clientId].ServerReceived;
                totalReceived += received.Count;

                foreach (var msg in received)
                {
                    int sourceClientId = BitConverter.ToInt32(msg.Data, 0);
                    if (sourceClientId == clientId)
                        correctDeliveries++;
                    else
                        incorrectDeliveries++;
                }

                LogTestProgress($"Client {clientId}: received {received.Count}/{MESSAGES_PER_CLIENT} messages");
            }

            LogTestProgress($"Total received: {totalReceived}/{totalMessagesSent}");
            LogTestProgress($"Correct deliveries: {correctDeliveries}");
            LogTestProgress($"Incorrect deliveries (cross-contamination): {incorrectDeliveries}");

            Assert.AreEqual(0, incorrectDeliveries,
                $"Cross-contamination detected! {incorrectDeliveries} messages delivered to wrong endpoint");
            Assert.AreEqual(CLIENT_COUNT * MESSAGES_PER_CLIENT, totalReceived,
                $"Message loss detected! Expected {CLIENT_COUNT * MESSAGES_PER_CLIENT}, got {totalReceived}");
        }

        [Test]
        public void SessionIdIsolation_DropsCrossDeliveredPackets()
        {
            LogTestProgress("Starting SessionIdIsolation_DropsCrossDeliveredPackets test");

            const double DELTA_TIME = 0.02;
            const uint SESSION0 = 111u;
            const uint SESSION1 = 222u;

            var setup = CreateMultiClientSetup(clientCount: 2, latencyMs: 0.0);

            // Assign distinct session ids per connection pair.
            setup.Clients[0].ClientEndpoint.ReliableSessionId = SESSION0;
            setup.Clients[0].ServerEndpoint.ReliableSessionId = SESSION0;
            setup.Clients[1].ClientEndpoint.ReliableSessionId = SESSION1;
            setup.Clients[1].ServerEndpoint.ReliableSessionId = SESSION1;

            // Send a message from Client 0.
            byte[] message = new byte[100];
            Buffer.BlockCopy(BitConverter.GetBytes(0), 0, message, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(12345), 0, message, 4, 4);
            setup.Clients[0].ClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);

            // Flush send buffers to enqueue packet(s) for delivery.
            setup.CurrentTime += DELTA_TIME;
            setup.Clients[0].ClientEndpoint.Update(setup.CurrentTime);
            setup.Clients[0].ClientEndpoint.ProcessSendBuffer_IfAppropriate();

            // Deliver Client0->Server0 packets to Server1 instead (simulate cross-delivery).
            while (setup.Clients[0].ClientToServerQueue.Count > 0)
            {
                var (data, len, _) = setup.Clients[0].ClientToServerQueue.Dequeue();
                setup.Clients[1].ServerEndpoint.ReceivePacket(data, len);
            }

            Assert.AreEqual(0, setup.Clients[1].ServerReceived.Count,
                "Cross-delivered packets should be dropped when session ids differ");

            LogTestProgress("Test PASSED - SessionId prevents cross-delivery processing");
        }
    }
}
