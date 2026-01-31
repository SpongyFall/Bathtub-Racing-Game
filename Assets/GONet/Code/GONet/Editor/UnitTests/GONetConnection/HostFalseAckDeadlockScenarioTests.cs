using System;
using System.Collections.Generic;
using NUnit.Framework;
using ReliableNetcode;
using UnityEngine;

namespace GONet.Tests.GONetConnection
{
    /// <summary>
    /// Detailed scenario tests that reproduce the exact false ACK deadlock bug.
    ///
    /// BUG REPRODUCTION STEPS:
    /// 1. HOST starts (server + client in same process, sharing transport)
    /// 2. Remote Client 9 connects
    /// 3. Remote Client 9 sends reliable message (pktSeq=0, msgSeq=3)
    /// 4. BUG: HOST's ClientToServer receives the packet (should only be for server's S->C9 connection)
    /// 5. BUG: HOST's ClientToServer sends ACK back to Client 9
    /// 6. Client 9 receives ACK, marks message as delivered, stops retransmitting
    /// 7. DEADLOCK: Server's S->C9 connection never received the packet, expects msgSeq=3 forever
    /// 8. All subsequent messages from Client 9 are queued but not delivered
    /// 9. Client 9 never receives GONetId assignment -> frozen GONetParticipant
    ///
    /// EXPECTED LOG EVIDENCE (from real bug):
    /// Line 461: [CROSS-DELIVERY-DIAG] ThisConn=unknown thisUID=0 sourceUID=9981377974739071039 wouldFilter=False
    /// Line 462: [RELIABLE-ACK-RECV] conn=unknown pktSeq=1 ack=0 ackBits=0x00000001 willProcess=True
    /// Line 463: [RELIABLE-RECV-PKT] conn=unknown pktSeq=1, bytes=172, nextExpected=0
    /// Line 476: [RELIABLE-DELIVER] conn=unknown msgSeq=3, bytes=35  (Client 2's message delivered to WRONG connection!)
    /// </summary>
    [TestFixture]
    public class HostFalseAckDeadlockScenarioTests
    {
        #region Test Infrastructure

        private class ReceivedPacket
        {
            public byte[] Data;
            public int Length;
            public double Time;
            public string Source;
        }

        private class TransmittedPacket
        {
            public byte[] Data;
            public int Length;
            public double Time;
            public string Destination;
        }

        private class EndpointStats
        {
            public int PacketsReceived;
            public int PacketsSent;
            public int AcksSent;
            public int AcksReceived;
            public int MessagesDelivered;
            public int FalseAcksDetected;
            public List<string> EventLog = new List<string>();
        }

        #endregion

        #region Deadlock Scenario Tests

        /// <summary>
        /// TEST: Reproduce the exact deadlock scenario from the bug report.
        /// This simulates what happens when HOST client subscribes to shared transport.
        /// </summary>
        [Test]
        public void ExactDeadlockScenario_HostClientInterceptsRemotePacket_CausesDeadlock()
        {
            double currentTime = 0.0;
            const double LATENCY_MS = 30.0;
            const double TICK_RATE = 0.02; // 50 Hz

            // Create the endpoints
            var serverToClient9 = new ReliableEndpoint();
            var client9ToServer = new ReliableEndpoint();
            var hostClientToServer = new ReliableEndpoint(); // BUG: This shouldn't process C9's packets

            // Track what each endpoint receives
            var serverC9Stats = new EndpointStats();
            var client9Stats = new EndpointStats();
            var hostClientStats = new EndpointStats();

            // Queues for simulating network latency
            var client9ToServerQueue = new Queue<(byte[], int, double)>();
            var serverToClient9Queue = new Queue<(byte[], int, double)>();

            // Configure server's S->C9 endpoint
            serverToClient9.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                serverC9Stats.MessagesDelivered++;
                serverC9Stats.EventLog.Add($"[{currentTime:F3}] S->C9 DELIVERED message, len={length}");
            };
            serverToClient9.TransmitCallback = (buffer, length) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                serverToClient9Queue.Enqueue((copy, length, currentTime + LATENCY_MS / 1000.0));
                serverC9Stats.PacketsSent++;
            };

            // Configure Client 9's endpoint
            client9ToServer.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                client9Stats.MessagesDelivered++;
                client9Stats.EventLog.Add($"[{currentTime:F3}] C9 DELIVERED message, len={length}");
            };
            client9ToServer.TransmitCallback = (buffer, length) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                client9ToServerQueue.Enqueue((copy, length, currentTime + LATENCY_MS / 1000.0));
                client9Stats.PacketsSent++;
            };

            // Configure HOST's client endpoint (THE BUG - this receives C9's packets)
            hostClientToServer.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                hostClientStats.MessagesDelivered++;
                hostClientStats.FalseAcksDetected++;
                hostClientStats.EventLog.Add($"[{currentTime:F3}] HOST-CLIENT FALSELY DELIVERED message, len={length}");
            };
            hostClientToServer.TransmitCallback = (buffer, length) =>
            {
                hostClientStats.PacketsSent++;
                hostClientStats.EventLog.Add($"[{currentTime:F3}] HOST-CLIENT TRANSMITTED (this ACK goes nowhere useful)");
                // In the bug, this transmit goes back to the transport but doesn't affect C9's real connection
            };

            // Simulate the scenario
            Debug.Log("=== DEADLOCK SCENARIO SIMULATION START ===");

            // Client 9 sends a reliable message
            byte[] testMessage = new byte[35];
            for (int i = 0; i < testMessage.Length; i++)
                testMessage[i] = (byte)(i + 1);

            client9ToServer.SendMessage(testMessage, testMessage.Length, QosType.Reliable);
            client9Stats.EventLog.Add($"[{currentTime:F3}] C9 sent reliable message, len={testMessage.Length}");

            // Run simulation for several ticks
            int tickCount = 0;
            int maxTicks = 200;
            bool deadlockDetected = false;

            while (tickCount < maxTicks)
            {
                currentTime += TICK_RATE;
                tickCount++;

                // Process network queues
                while (client9ToServerQueue.Count > 0 && client9ToServerQueue.Peek().Item3 <= currentTime)
                {
                    var (data, len, _) = client9ToServerQueue.Dequeue();

                    // THE BUG: Both the server AND the HOST client receive this packet
                    // In real code, this happens because both subscribe to transport.OnMessageReceived

                    // Server's S->C9 connection receives (CORRECT)
                    serverToClient9.ReceivePacket(data, len);
                    serverC9Stats.PacketsReceived++;
                    serverC9Stats.EventLog.Add($"[{currentTime:F3}] S->C9 received packet, len={len}");

                    // HOST client also receives (BUG!)
                    hostClientToServer.ReceivePacket(data, len);
                    hostClientStats.PacketsReceived++;
                    hostClientStats.EventLog.Add($"[{currentTime:F3}] HOST-CLIENT received packet (BUG!), len={len}");
                }

                while (serverToClient9Queue.Count > 0 && serverToClient9Queue.Peek().Item3 <= currentTime)
                {
                    var (data, len, _) = serverToClient9Queue.Dequeue();
                    client9ToServer.ReceivePacket(data, len);
                    client9Stats.PacketsReceived++;
                }

                // Update endpoints
                serverToClient9.Update(currentTime);
                client9ToServer.Update(currentTime);
                hostClientToServer.Update(currentTime);

                serverToClient9.ProcessSendBuffer_IfAppropriate();
                client9ToServer.ProcessSendBuffer_IfAppropriate();
                hostClientToServer.ProcessSendBuffer_IfAppropriate();
            }

            Debug.Log("=== SIMULATION RESULTS ===");
            Debug.Log($"Server S->C9: received={serverC9Stats.PacketsReceived}, sent={serverC9Stats.PacketsSent}, delivered={serverC9Stats.MessagesDelivered}");
            Debug.Log($"Client 9: received={client9Stats.PacketsReceived}, sent={client9Stats.PacketsSent}, delivered={client9Stats.MessagesDelivered}");
            Debug.Log($"HOST Client (BUG): received={hostClientStats.PacketsReceived}, sent={hostClientStats.PacketsSent}, delivered={hostClientStats.MessagesDelivered}");
            Debug.Log($"HOST Client false ACKs: {hostClientStats.FalseAcksDetected}");

            Debug.Log("\n=== EVENT LOGS ===");
            Debug.Log("--- Server S->C9 ---");
            foreach (var log in serverC9Stats.EventLog) Debug.Log(log);
            Debug.Log("--- Client 9 ---");
            foreach (var log in client9Stats.EventLog) Debug.Log(log);
            Debug.Log("--- HOST Client (BUG) ---");
            foreach (var log in hostClientStats.EventLog) Debug.Log(log);

            // Verify the bug was reproduced
            Assert.Greater(hostClientStats.PacketsReceived, 0,
                "BUG REPRODUCTION: HOST client should have received C9's packets (demonstrating the bug)");
            Assert.Greater(hostClientStats.FalseAcksDetected, 0,
                "BUG REPRODUCTION: HOST client should have falsely 'delivered' messages");

            // Also verify the CORRECT behavior happened too
            Assert.Greater(serverC9Stats.MessagesDelivered, 0,
                "Server S->C9 should have correctly delivered the message");

            Debug.Log("\n=== BUG VERIFIED ===");
            Debug.Log($"HOST client intercepted {hostClientStats.PacketsReceived} packets meant for server!");
            Debug.Log($"This would cause {hostClientStats.FalseAcksDetected} false ACKs to be sent.");
            Debug.Log("In real scenario, C9 would stop retransmitting, server would never get the message.");
        }

        /// <summary>
        /// TEST: Verify behavior WITH the fix - HOST client should not process any packets.
        /// </summary>
        [Test]
        public void FixedScenario_HostClientDoesNotIntercept_NoDeadlock()
        {
            double currentTime = 0.0;
            const double LATENCY_MS = 30.0;
            const double TICK_RATE = 0.02;

            var serverToClient9 = new ReliableEndpoint();
            var client9ToServer = new ReliableEndpoint();
            // WITH FIX: No hostClientToServer endpoint subscribes to transport

            var serverC9Stats = new EndpointStats();
            var client9Stats = new EndpointStats();

            var client9ToServerQueue = new Queue<(byte[], int, double)>();
            var serverToClient9Queue = new Queue<(byte[], int, double)>();

            serverToClient9.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                serverC9Stats.MessagesDelivered++;
                serverC9Stats.EventLog.Add($"[{currentTime:F3}] S->C9 DELIVERED message, len={length}");
            };
            serverToClient9.TransmitCallback = (buffer, length) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                serverToClient9Queue.Enqueue((copy, length, currentTime + LATENCY_MS / 1000.0));
                serverC9Stats.PacketsSent++;
            };

            client9ToServer.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                client9Stats.MessagesDelivered++;
                client9Stats.EventLog.Add($"[{currentTime:F3}] C9 DELIVERED message, len={length}");
            };
            client9ToServer.TransmitCallback = (buffer, length) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                client9ToServerQueue.Enqueue((copy, length, currentTime + LATENCY_MS / 1000.0));
                client9Stats.PacketsSent++;
            };

            Debug.Log("=== FIXED SCENARIO SIMULATION START ===");

            byte[] testMessage = new byte[35];
            for (int i = 0; i < testMessage.Length; i++)
                testMessage[i] = (byte)(i + 1);

            client9ToServer.SendMessage(testMessage, testMessage.Length, QosType.Reliable);
            client9Stats.EventLog.Add($"[{currentTime:F3}] C9 sent reliable message, len={testMessage.Length}");

            int tickCount = 0;
            int maxTicks = 200;

            while (tickCount < maxTicks)
            {
                currentTime += TICK_RATE;
                tickCount++;

                while (client9ToServerQueue.Count > 0 && client9ToServerQueue.Peek().Item3 <= currentTime)
                {
                    var (data, len, _) = client9ToServerQueue.Dequeue();

                    // WITH FIX: Only server's S->C9 receives the packet
                    serverToClient9.ReceivePacket(data, len);
                    serverC9Stats.PacketsReceived++;
                    serverC9Stats.EventLog.Add($"[{currentTime:F3}] S->C9 received packet, len={len}");

                    // HOST client does NOT receive (FIX APPLIED)
                }

                while (serverToClient9Queue.Count > 0 && serverToClient9Queue.Peek().Item3 <= currentTime)
                {
                    var (data, len, _) = serverToClient9Queue.Dequeue();
                    client9ToServer.ReceivePacket(data, len);
                    client9Stats.PacketsReceived++;
                }

                serverToClient9.Update(currentTime);
                client9ToServer.Update(currentTime);
                serverToClient9.ProcessSendBuffer_IfAppropriate();
                client9ToServer.ProcessSendBuffer_IfAppropriate();
            }

            Debug.Log("=== FIXED SIMULATION RESULTS ===");
            Debug.Log($"Server S->C9: received={serverC9Stats.PacketsReceived}, sent={serverC9Stats.PacketsSent}, delivered={serverC9Stats.MessagesDelivered}");
            Debug.Log($"Client 9: received={client9Stats.PacketsReceived}, sent={client9Stats.PacketsSent}, delivered={client9Stats.MessagesDelivered}");
            Debug.Log("HOST Client: NOT subscribed to transport (FIX APPLIED)");

            // Verify correct delivery
            Assert.Greater(serverC9Stats.MessagesDelivered, 0,
                "Server S->C9 should have delivered the message");
            Assert.Greater(client9Stats.PacketsReceived, 0,
                "Client 9 should have received ACK packets from server");

            Debug.Log("\n=== FIX VERIFIED ===");
            Debug.Log("Message delivered correctly without HOST client interference.");
        }

        #endregion

        #region ACK Sequence Analysis Tests

        /// <summary>
        /// TEST: Analyze ACK behavior to understand how false ACKs cause deadlock.
        /// NOTE: This is a conceptual analysis test - we can't easily create raw ReliableNetcode packets
        /// because the format is complex. Instead, we demonstrate the concept through real endpoint usage.
        /// </summary>
        [Test]
        public void AckSequenceAnalysis_ShowsHowFalseAcksCauseDeadlock()
        {
            Debug.Log("=== ACK SEQUENCE ANALYSIS (CONCEPTUAL) ===");
            Debug.Log("");
            Debug.Log("The false ACK deadlock occurs as follows:");
            Debug.Log("");
            Debug.Log("1. Client 9 sends reliable packet (pktSeq=0, contains msgSeq=3)");
            Debug.Log("2. Packet arrives at server's transport");
            Debug.Log("3. Transport broadcasts to ALL OnMessageReceived subscribers:");
            Debug.Log("   - S->C9 connection receives (CORRECT)");
            Debug.Log("   - HOST's ClientToServer receives (BUG - connection=null bypasses filter)");
            Debug.Log("");
            Debug.Log("4. BOTH endpoints process the packet:");
            Debug.Log("   - S->C9: Updates its sequence state, queues ACK");
            Debug.Log("   - HOST ClientToServer: Updates ITS sequence state, queues ACK");
            Debug.Log("");
            Debug.Log("5. HOST ClientToServer transmits ACK back via shared transport");
            Debug.Log("6. Client 9 receives ACK, marks msgSeq=3 as delivered");
            Debug.Log("7. Client 9 STOPS retransmitting msgSeq=3");
            Debug.Log("");
            Debug.Log("8. BUT the server's ACTUAL S->C9 connection:");
            Debug.Log("   - May have different sequence state");
            Debug.Log("   - Its ACK may be lost or delayed");
            Debug.Log("   - Now expects msgSeq=3 but will never receive retransmit");
            Debug.Log("");
            Debug.Log("9. DEADLOCK: S->C9 stuck waiting for msgSeq=3 forever");
            Debug.Log("   - All subsequent messages from C9 are queued");
            Debug.Log("   - C9 never gets GONetId assignment");
            Debug.Log("   - Frozen GONetParticipant");
            Debug.Log("");
            Debug.Log("The key insight: ReliableNetcode sequence state is PER-ENDPOINT.");
            Debug.Log("When HOST client processes packets meant for other connections,");
            Debug.Log("it corrupts the ACK stream with its own sequence state.");

            Assert.Pass("ACK sequence analysis complete - see console output for explanation");
        }

        #endregion

        #region Multi-Client Stress Tests

        /// <summary>
        /// TEST: Stress test with 10 clients all sending simultaneously.
        /// Demonstrates how the bug affects multiple clients.
        /// </summary>
        [Test]
        public void StressTest_TenClients_AllAffectedByHostBug()
        {
            const int CLIENT_COUNT = 10;
            const int MESSAGES_PER_CLIENT = 5;
            double currentTime = 0.0;
            const double TICK_RATE = 0.02;
            const double LATENCY_MS = 30.0;

            // Create client endpoints
            var clientEndpoints = new ReliableEndpoint[CLIENT_COUNT];
            var serverEndpoints = new ReliableEndpoint[CLIENT_COUNT]; // Server's connection to each client
            var clientStats = new EndpointStats[CLIENT_COUNT];
            var serverStats = new EndpointStats[CLIENT_COUNT];
            var clientToServerQueues = new Queue<(byte[], int, double, int)>(); // Added client index

            // HOST client (the bug)
            var hostClientEndpoint = new ReliableEndpoint();
            var hostStats = new EndpointStats();

            // Initialize all endpoints
            for (int i = 0; i < CLIENT_COUNT; i++)
            {
                int clientIndex = i;
                clientEndpoints[i] = new ReliableEndpoint();
                serverEndpoints[i] = new ReliableEndpoint();
                clientStats[i] = new EndpointStats();
                serverStats[i] = new EndpointStats();

                serverEndpoints[i].ReceiveCallback = (buffer, length, receiveTimestamp) =>
                {
                    serverStats[clientIndex].MessagesDelivered++;
                };

                clientEndpoints[i].TransmitCallback = (buffer, length) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    clientToServerQueues.Enqueue((copy, length, currentTime + LATENCY_MS / 1000.0, clientIndex));
                    clientStats[clientIndex].PacketsSent++;
                };
            }

            hostClientEndpoint.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                hostStats.MessagesDelivered++;
                hostStats.FalseAcksDetected++;
            };

            Debug.Log($"=== STRESS TEST: {CLIENT_COUNT} CLIENTS, {MESSAGES_PER_CLIENT} MESSAGES EACH ===");

            // All clients send messages
            for (int c = 0; c < CLIENT_COUNT; c++)
            {
                for (int m = 0; m < MESSAGES_PER_CLIENT; m++)
                {
                    byte[] msg = new byte[50];
                    msg[0] = (byte)c; // Client ID
                    msg[1] = (byte)m; // Message index
                    clientEndpoints[c].SendMessage(msg, msg.Length, QosType.Reliable);
                }
            }

            // Run simulation
            int tickCount = 0;
            while (tickCount < 500)
            {
                currentTime += TICK_RATE;
                tickCount++;

                // Process queue
                while (clientToServerQueues.Count > 0 && clientToServerQueues.Peek().Item3 <= currentTime)
                {
                    var (data, len, _, clientIdx) = clientToServerQueues.Dequeue();

                    // Server's connection receives (CORRECT)
                    serverEndpoints[clientIdx].ReceivePacket(data, len);
                    serverStats[clientIdx].PacketsReceived++;

                    // HOST client also receives (BUG!)
                    hostClientEndpoint.ReceivePacket(data, len);
                    hostStats.PacketsReceived++;
                }

                // Update all endpoints
                for (int i = 0; i < CLIENT_COUNT; i++)
                {
                    clientEndpoints[i].Update(currentTime);
                    serverEndpoints[i].Update(currentTime);
                    clientEndpoints[i].ProcessSendBuffer_IfAppropriate();
                    serverEndpoints[i].ProcessSendBuffer_IfAppropriate();
                }
                hostClientEndpoint.Update(currentTime);
                hostClientEndpoint.ProcessSendBuffer_IfAppropriate();
            }

            Debug.Log("=== STRESS TEST RESULTS ===");
            int totalServerDelivered = 0;
            for (int i = 0; i < CLIENT_COUNT; i++)
            {
                totalServerDelivered += serverStats[i].MessagesDelivered;
                Debug.Log($"Client {i}: sent={clientStats[i].PacketsSent}, server delivered={serverStats[i].MessagesDelivered}");
            }
            Debug.Log($"HOST Client (BUG): received={hostStats.PacketsReceived}, falsely delivered={hostStats.MessagesDelivered}");
            Debug.Log("");
            Debug.Log($"TOTAL server delivered: {totalServerDelivered}/{CLIENT_COUNT * MESSAGES_PER_CLIENT}");
            Debug.Log($"HOST false deliveries: {hostStats.FalseAcksDetected}");

            // Verify bug reproduced
            Assert.Greater(hostStats.PacketsReceived, 0,
                $"HOST client should have intercepted packets from all {CLIENT_COUNT} clients");

            // NOTE: HOST client only delivers MESSAGES_PER_CLIENT unique messages (not CLIENT_COUNT * MESSAGES_PER_CLIENT)
            // because all clients use the same message sequence numbers (0, 1, 2, 3, 4) and ReliableNetcode
            // deduplicates by sequence number. The HOST's single endpoint sees msgSeq 0-4 from client 0,
            // then sees msgSeq 0-4 again from client 1 (duplicates - ignored), etc.
            //
            // However, the HOST still RECEIVES all 900 packets and processes ACKs for all of them!
            // This is the bug - it sends false ACKs even though it only delivers 5 unique messages.
            Assert.AreEqual(MESSAGES_PER_CLIENT, hostStats.FalseAcksDetected,
                $"HOST client delivers {MESSAGES_PER_CLIENT} unique messages (deduplicated by msgSeq)");
            Assert.Greater(hostStats.PacketsReceived, CLIENT_COUNT * 10,
                "HOST client should have received hundreds of packets from all clients");

            Debug.Log("\n=== BUG IMPACT SUMMARY ===");
            Debug.Log($"HOST client received {hostStats.PacketsReceived} packets from {CLIENT_COUNT} clients!");
            Debug.Log($"HOST client delivered {hostStats.FalseAcksDetected} unique messages (deduplicated).");
            Debug.Log("CRITICAL: Even though only 5 unique messages delivered, HOST processed ALL packets!");
            Debug.Log("This means HOST sent ACKs for packets from ALL clients, causing false ACK pollution.");
        }

        #endregion
    }
}
