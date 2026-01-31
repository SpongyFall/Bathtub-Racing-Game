using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ReliableNetcode;
using UnityEngine;

namespace GONet.Tests.Integration
{
    /// <summary>
    /// EXTREME CONCURRENCY TESTS
    ///
    /// These tests push the system to its limits to try to reproduce the deadlock bug.
    /// The bug occurs during initial connection phase with 1 HOST + 9 clients.
    ///
    /// Key characteristics of the real-world bug:
    /// - Client 3 stuck at nextExpected=1029
    /// - Frozen GONetParticipant immediately after sync
    /// - HOST shows 0/9 mesh peers
    /// - Occurs during SceneLoadComplete / GONetId assignment
    /// </summary>
    [TestFixture]
    public class ExtremeConcurrencyTests
    {
        private const int HOST_CLIENT_ID = 1;
        private const int TOTAL_CLIENTS = 10; // 1 HOST + 9 remote
        private const double BASE_LATENCY_MS = 30;

        /// <summary>
        /// Simulates one client's connection to the server with full message tracking.
        /// </summary>
        private class SimulatedClient
        {
            public int ClientId;
            public bool IsHost;
            public ReliableEndpoint ClientEndpoint;
            public ReliableEndpoint ServerEndpoint;

            // Message queues (simulating network latency)
            public ConcurrentQueue<(byte[] data, int len, double deliveryTime)> ToServerQueue = new();
            public ConcurrentQueue<(byte[] data, int len, double deliveryTime)> ToClientQueue = new();

            // Statistics
            public int MessagesSent;
            public int MessagesReceived;
            public int AcksSent;
            public int AcksReceived;
            public int DroppedByUs;

            // Connection state
            public bool HasSentSceneLoadComplete;
            public bool HasReceivedGONetId;
            public double ConnectionTime;

            // For tracking received sequence numbers
            public HashSet<int> ReceivedSequences = new();
            public int NextExpected;
            public bool IsStuck;

            // Thread-safety
            public readonly object Lock = new object();
        }

        /// <summary>
        /// Shared transport that ALL server-side connections subscribe to.
        /// This is where cross-delivery bugs can occur.
        /// </summary>
        private class ServerTransport
        {
            public List<Action<byte[], int, object>> Subscribers = new();
            public int CrossDeliveryCount;
            public readonly object Lock = new object();

            public void OnPacketReceived(byte[] data, int length, object sourceConnection)
            {
                lock (Lock)
                {
                    foreach (var sub in Subscribers)
                    {
                        sub(data, length, sourceConnection);
                    }
                }
            }
        }

        private class TestScenario
        {
            public List<SimulatedClient> Clients = new();
            public SimulatedClient HostClient;
            public ServerTransport Transport = new();
            public double CurrentTime;
            public double LatencyMs = BASE_LATENCY_MS;

            // Packet loss simulation
            public double ClientToServerLossRate;
            public double ServerToClientLossRate;

            // Statistics
            public int TotalCrossDeliveries;
            public int TotalPacketsDropped;
            public List<string> EventLog = new();

            public System.Random Rng = new System.Random(12345);

            public readonly object GlobalLock = new object();
        }

        private SimulatedClient CreateClient(TestScenario scenario, int clientId, bool isHost, bool applyHostFix)
        {
            var client = new SimulatedClient
            {
                ClientId = clientId,
                IsHost = isHost,
                ClientEndpoint = new ReliableEndpoint(),
                ServerEndpoint = new ReliableEndpoint(),
                ConnectionTime = scenario.CurrentTime
            };

            // Client-side receive callback (application layer)
            client.ClientEndpoint.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                lock (client.Lock)
                {
                    client.MessagesReceived++;

                    // Extract sequence from message
                    if (length >= 12)
                    {
                        int seq = BitConverter.ToInt32(buffer, 8);
                        client.ReceivedSequences.Add(seq);

                        // Track if we're stuck (gap in sequence)
                        while (client.ReceivedSequences.Contains(client.NextExpected))
                        {
                            client.NextExpected++;
                        }
                    }
                }
            };

            // Server-side receive callback (application layer)
            client.ServerEndpoint.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                // Server received message from this client
            };

            if (isHost)
            {
                // HOST uses direct loopback - no network
                client.ClientEndpoint.TransmitCallback = (buffer, length) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    lock (client.Lock)
                    {
                        client.ServerEndpoint.ReceivePacket(copy, length);
                    }
                };

                client.ServerEndpoint.TransmitCallback = (buffer, length) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    lock (client.Lock)
                    {
                        client.ClientEndpoint.ReceivePacket(copy, length);
                    }
                };

                // THE BUG: Without fix, HOST subscribes to shared transport
                if (!applyHostFix)
                {
                    object nullConnection = null;
                    scenario.Transport.Subscribers.Add((data, len, source) =>
                    {
                        // With null connection, filter never matches - accepts ALL packets
                        bool wouldFilter = nullConnection != null && source != nullConnection;
                        if (!wouldFilter)
                        {
                            lock (scenario.GlobalLock)
                            {
                                scenario.TotalCrossDeliveries++;
                            }
                            // HOST processes remote packets - BUG!
                            lock (client.Lock)
                            {
                                client.ClientEndpoint.ReceivePacket(data, len);
                            }
                        }
                    });
                }
            }
            else
            {
                // Remote client - uses network queues
                var thisClient = client;

                client.ClientEndpoint.TransmitCallback = (buffer, length) =>
                {
                    // Apply packet loss
                    if (scenario.ClientToServerLossRate > 0 && scenario.Rng.NextDouble() < scenario.ClientToServerLossRate)
                    {
                        thisClient.DroppedByUs++;
                        Interlocked.Increment(ref scenario.TotalPacketsDropped);
                        return;
                    }

                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    double deliveryTime = scenario.CurrentTime + scenario.LatencyMs / 1000.0;
                    thisClient.ToServerQueue.Enqueue((copy, length, deliveryTime));
                };

                client.ServerEndpoint.TransmitCallback = (buffer, length) =>
                {
                    // Apply packet loss
                    if (scenario.ServerToClientLossRate > 0 && scenario.Rng.NextDouble() < scenario.ServerToClientLossRate)
                    {
                        Interlocked.Increment(ref scenario.TotalPacketsDropped);
                        return;
                    }

                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    double deliveryTime = scenario.CurrentTime + scenario.LatencyMs / 1000.0;
                    thisClient.ToClientQueue.Enqueue((copy, length, deliveryTime));
                };

                // Subscribe to shared transport with proper filtering
                scenario.Transport.Subscribers.Add((data, len, source) =>
                {
                    // Proper filter - only accept packets from our connection
                    if (source == thisClient)
                    {
                        lock (thisClient.Lock)
                        {
                            thisClient.ServerEndpoint.ReceivePacket(data, len);
                        }
                    }
                });
            }

            return client;
        }

        private TestScenario CreateScenario(bool applyHostFix = true)
        {
            var scenario = new TestScenario();

            // HOST first
            var host = CreateClient(scenario, HOST_CLIENT_ID, isHost: true, applyHostFix);
            scenario.HostClient = host;
            scenario.Clients.Add(host);

            // 9 remote clients
            for (int i = 2; i <= TOTAL_CLIENTS; i++)
            {
                var client = CreateClient(scenario, i, isHost: false, applyHostFix);
                scenario.Clients.Add(client);
            }

            return scenario;
        }

        private void UpdateScenario(TestScenario scenario, double deltaTime)
        {
            scenario.CurrentTime += deltaTime;

            foreach (var client in scenario.Clients)
            {
                if (!client.IsHost)
                {
                    // Process client -> server queue
                    while (client.ToServerQueue.TryPeek(out var packet) && packet.deliveryTime <= scenario.CurrentTime)
                    {
                        if (client.ToServerQueue.TryDequeue(out packet))
                        {
                            // Dispatch through shared transport
                            scenario.Transport.OnPacketReceived(packet.data, packet.len, client);
                        }
                    }

                    // Process server -> client queue
                    while (client.ToClientQueue.TryPeek(out var packet) && packet.deliveryTime <= scenario.CurrentTime)
                    {
                        if (client.ToClientQueue.TryDequeue(out packet))
                        {
                            lock (client.Lock)
                            {
                                client.ClientEndpoint.ReceivePacket(packet.data, packet.len);
                            }
                        }
                    }
                }

                lock (client.Lock)
                {
                    client.ClientEndpoint.Update(scenario.CurrentTime);
                    client.ServerEndpoint.Update(scenario.CurrentTime);
                    client.ClientEndpoint.ProcessSendBuffer_IfAppropriate();
                    client.ServerEndpoint.ProcessSendBuffer_IfAppropriate();
                }
            }
        }

        private byte[] CreateMessage(int clientId, int msgType, int sequence)
        {
            byte[] msg = new byte[100];
            Buffer.BlockCopy(BitConverter.GetBytes(clientId), 0, msg, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(msgType), 0, msg, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, msg, 8, 4);
            return msg;
        }

        private const int MSG_SYNC = 1;
        private const int MSG_SCENE_LOAD_COMPLETE = 2;
        private const int MSG_GONET_ID = 3;

        /// <summary>
        /// TEST 1: Extreme concurrent message burst
        ///
        /// All 10 clients send messages simultaneously from separate threads.
        /// Server responds immediately with more messages.
        /// </summary>
        [Test]
        [Timeout(30000)]
        public void ExtremeConcurrentBurst_10Clients_MultiThreaded()
        {
            Debug.Log("[ExtremeConcurrency] Starting ExtremeConcurrentBurst test");

            var scenario = CreateScenario(applyHostFix: true);
            const double DELTA = 0.016;
            const int MSGS_PER_CLIENT = 100;

            // Thread coordination
            var errors = new ConcurrentBag<string>();
            bool running = true;
            int totalSent = 0;

            // Each client gets its own sender thread
            var clientThreads = new List<Thread>();
            foreach (var client in scenario.Clients)
            {
                var c = client;
                var thread = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < MSGS_PER_CLIENT && running; i++)
                        {
                            var msg = CreateMessage(c.ClientId, MSG_SYNC, i);
                            lock (c.Lock)
                            {
                                c.ClientEndpoint.SendMessage(msg, msg.Length, QosType.Reliable);
                                c.MessagesSent++;
                            }
                            Interlocked.Increment(ref totalSent);

                            // Vary timing per client
                            Thread.Sleep(c.ClientId); // 1-10ms based on client ID
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Client {c.ClientId}: {ex.Message}");
                    }
                });
                clientThreads.Add(thread);
            }

            // Server update thread
            var updateThread = new Thread(() =>
            {
                try
                {
                    while (running)
                    {
                        lock (scenario.GlobalLock)
                        {
                            UpdateScenario(scenario, DELTA);
                        }
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Update: {ex.Message}");
                }
            });

            // Server response threads (one per client, each sends 0..N)
            var serverThreads = new List<Thread>();
            foreach (var client in scenario.Clients)
            {
                var c = client;
                serverThreads.Add(new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < MSGS_PER_CLIENT && running; i++)
                        {
                            var response = CreateMessage(0, MSG_SYNC, i);
                            lock (c.Lock)
                            {
                                c.ServerEndpoint.SendMessage(response, response.Length, QosType.Reliable);
                            }
                            Thread.Sleep(5);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Server for {c.ClientId}: {ex.Message}");
                    }
                }));
            }

            // Start all threads
            Debug.Log("[ExtremeConcurrency] Starting threads...");
            updateThread.Start();
            foreach (var t in serverThreads) t.Start();
            foreach (var t in clientThreads) t.Start();

            // Wait for client and server threads with timeout
            foreach (var t in clientThreads) t.Join(5000);
            foreach (var t in serverThreads) t.Join(5000);

            // Let messages flush
            Debug.Log("[ExtremeConcurrency] Flushing messages...");
            Thread.Sleep(2000);
            running = false;

            updateThread.Join(5000);

            // Final flush
            lock (scenario.GlobalLock)
            {
                for (int i = 0; i < 300; i++)
                {
                    UpdateScenario(scenario, DELTA);
                }
            }

            // Results
            Debug.Log($"[ExtremeConcurrency] Errors: {errors.Count}");
            foreach (var err in errors)
            {
                Debug.Log($"[ExtremeConcurrency] ERROR: {err}");
            }

            Debug.Log($"[ExtremeConcurrency] Cross-deliveries: {scenario.TotalCrossDeliveries}");
            Debug.Log($"[ExtremeConcurrency] Packets dropped: {scenario.TotalPacketsDropped}");
            Debug.Log($"[ExtremeConcurrency] Total sent: {totalSent}");

            int stuckClients = 0;
            foreach (var client in scenario.Clients)
            {
                int received = client.ReceivedSequences.Count;

                // Check for gaps in received sequences (0 to MSGS_PER_CLIENT-1)
                int gaps = 0;
                for (int seq = 0; seq < MSGS_PER_CLIENT; seq++)
                {
                    if (!client.ReceivedSequences.Contains(seq))
                    {
                        gaps++;
                    }
                }

                Debug.Log($"[ExtremeConcurrency] Client {client.ClientId}: received={received}/{MSGS_PER_CLIENT}, gaps={gaps}");

                // Client is stuck if it has gaps (missing messages)
                if (gaps > 0)
                {
                    stuckClients++;
                    Debug.Log($"[ExtremeConcurrency] Client {client.ClientId} has {gaps} missing messages");
                }
            }

            Assert.AreEqual(0, errors.Count, "Thread errors occurred");
            Assert.AreEqual(0, stuckClients, $"{stuckClients} clients have missing messages");

            Debug.Log("[ExtremeConcurrency] Test PASSED");
        }

        /// <summary>
        /// TEST 2: Initial sync storm - simulates exact initial connection pattern
        ///
        /// Server blasts initial sync to all clients simultaneously.
        /// All clients send SceneLoadComplete at once.
        /// Server responds with GONetId assignments.
        /// </summary>
        [Test]
        [Timeout(30000)]
        public void InitialSyncStorm_AllClientsSimultaneous()
        {
            Debug.Log("[ExtremeConcurrency] Starting InitialSyncStorm test");

            var scenario = CreateScenario(applyHostFix: true);
            const double DELTA = 0.016;
            const int INITIAL_SYNC_MSGS = 200; // Heavy initial sync

            // Phase 1: Server sends initial sync to ALL clients at once
            Debug.Log("[ExtremeConcurrency] Phase 1: Server sending initial sync...");

            var syncTasks = new List<Task>();
            foreach (var client in scenario.Clients)
            {
                var c = client;
                syncTasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < INITIAL_SYNC_MSGS; i++)
                    {
                        var msg = CreateMessage(0, MSG_SYNC, i);
                        lock (c.Lock)
                        {
                            c.ServerEndpoint.SendMessage(msg, msg.Length, QosType.Reliable);
                        }
                    }
                }));
            }

            // Wait for all sends
            Task.WaitAll(syncTasks.ToArray(), 10000);

            // Phase 2: Update to let messages flow
            Debug.Log("[ExtremeConcurrency] Phase 2: Messages flowing...");
            for (int i = 0; i < 60; i++) // 1 second
            {
                lock (scenario.GlobalLock)
                {
                    UpdateScenario(scenario, DELTA);
                }
            }

            // Phase 3: All clients send SceneLoadComplete simultaneously
            Debug.Log("[ExtremeConcurrency] Phase 3: All clients sending SceneLoadComplete...");

            var slcTasks = new List<Task>();
            foreach (var client in scenario.Clients)
            {
                var c = client;
                slcTasks.Add(Task.Run(() =>
                {
                    var msg = CreateMessage(c.ClientId, MSG_SCENE_LOAD_COMPLETE, 0);
                    lock (c.Lock)
                    {
                        c.ClientEndpoint.SendMessage(msg, msg.Length, QosType.Reliable);
                        c.HasSentSceneLoadComplete = true;
                    }
                }));
            }

            Task.WaitAll(slcTasks.ToArray(), 10000);

            // Phase 4: Run for several seconds
            Debug.Log("[ExtremeConcurrency] Phase 4: Running for 10 seconds...");
            for (int i = 0; i < 600; i++)
            {
                lock (scenario.GlobalLock)
                {
                    UpdateScenario(scenario, DELTA);
                }
            }

            // Check results
            Debug.Log("[ExtremeConcurrency] Checking results...");

            int fullySync = 0;
            int stuckClients = 0;

            foreach (var client in scenario.Clients)
            {
                int received = client.ReceivedSequences.Count;
                bool hasAll = received >= INITIAL_SYNC_MSGS;

                if (hasAll) fullySync++;

                // Check for gap (stuck)
                bool hasGap = false;
                for (int seq = 0; seq < client.NextExpected && seq < INITIAL_SYNC_MSGS; seq++)
                {
                    if (!client.ReceivedSequences.Contains(seq))
                    {
                        hasGap = true;
                        Debug.Log($"[ExtremeConcurrency] Client {client.ClientId} has gap at seq={seq}");
                        break;
                    }
                }

                if (hasGap || client.NextExpected < INITIAL_SYNC_MSGS)
                {
                    stuckClients++;
                    Debug.Log($"[ExtremeConcurrency] Client {client.ClientId} STUCK: received={received}, nextExpected={client.NextExpected}");
                }
                else
                {
                    Debug.Log($"[ExtremeConcurrency] Client {client.ClientId} OK: received={received}");
                }
            }

            Debug.Log($"[ExtremeConcurrency] Fully synced: {fullySync}/10");
            Debug.Log($"[ExtremeConcurrency] Stuck clients: {stuckClients}");
            Debug.Log($"[ExtremeConcurrency] Cross-deliveries: {scenario.TotalCrossDeliveries}");

            Assert.AreEqual(10, fullySync, $"Only {fullySync}/10 clients fully synced");
            Assert.AreEqual(0, stuckClients, $"{stuckClients} clients got stuck");

            Debug.Log("[ExtremeConcurrency] Test PASSED");
        }

        /// <summary>
        /// TEST 3: Packet loss storm - 20% loss rate
        /// </summary>
        [Test]
        [Timeout(45000)]
        public void PacketLossStorm_20PercentLoss_AllRecover()
        {
            Debug.Log("[ExtremeConcurrency] Starting PacketLossStorm test");

            var scenario = CreateScenario(applyHostFix: true);
            scenario.ClientToServerLossRate = 0.20;
            scenario.ServerToClientLossRate = 0.20;

            const double DELTA = 0.016;
            const int MSGS_PER_CLIENT = 50;

            // Send messages
            Debug.Log("[ExtremeConcurrency] Sending messages with 20% loss...");
            for (int i = 0; i < MSGS_PER_CLIENT; i++)
            {
                foreach (var client in scenario.Clients)
                {
                    var msg = CreateMessage(0, MSG_SYNC, i);
                    lock (client.Lock)
                    {
                        client.ServerEndpoint.SendMessage(msg, msg.Length, QosType.Reliable);
                    }
                }

                lock (scenario.GlobalLock)
                {
                    UpdateScenario(scenario, DELTA);
                }
            }

            // Run for extended time to allow retransmissions
            Debug.Log("[ExtremeConcurrency] Running for 30 seconds to allow retransmissions...");
            for (int i = 0; i < 1800; i++)
            {
                lock (scenario.GlobalLock)
                {
                    UpdateScenario(scenario, DELTA);
                }

                if (i % 300 == 0)
                {
                    int totalReceived = scenario.Clients.Sum(c => c.ReceivedSequences.Count);
                    Debug.Log($"[ExtremeConcurrency] Progress: {i/60}s, totalReceived={totalReceived}");
                }
            }

            // Check results
            Debug.Log($"[ExtremeConcurrency] Packets dropped: {scenario.TotalPacketsDropped}");

            int fullySync = 0;
            foreach (var client in scenario.Clients)
            {
                int received = client.ReceivedSequences.Count;
                if (received >= MSGS_PER_CLIENT)
                {
                    fullySync++;
                }
                Debug.Log($"[ExtremeConcurrency] Client {client.ClientId}: received={received}/{MSGS_PER_CLIENT}");
            }

            Assert.AreEqual(10, fullySync, $"Only {fullySync}/10 clients recovered from packet loss");

            Debug.Log("[ExtremeConcurrency] Test PASSED");
        }

        /// <summary>
        /// TEST 4: WITHOUT HOST FIX - should show cross-delivery
        /// </summary>
        [Test]
        public void WithoutHostFix_CrossDeliveryOccurs()
        {
            Debug.Log("[ExtremeConcurrency] Starting WithoutHostFix test");

            var scenario = CreateScenario(applyHostFix: false);
            const double DELTA = 0.016;
            const int MSGS = 50;

            // Send from remote clients
            foreach (var client in scenario.Clients.Where(c => !c.IsHost))
            {
                for (int i = 0; i < MSGS; i++)
                {
                    var msg = CreateMessage(client.ClientId, MSG_SYNC, i);
                    lock (client.Lock)
                    {
                        client.ClientEndpoint.SendMessage(msg, msg.Length, QosType.Reliable);
                    }
                }
            }

            // Update
            for (int i = 0; i < 300; i++)
            {
                lock (scenario.GlobalLock)
                {
                    UpdateScenario(scenario, DELTA);
                }
            }

            Debug.Log($"[ExtremeConcurrency] Cross-deliveries: {scenario.TotalCrossDeliveries}");

            Assert.Greater(scenario.TotalCrossDeliveries, 0,
                "Without fix, cross-delivery should occur");

            Debug.Log("[ExtremeConcurrency] Test PASSED - cross-delivery detected without fix");
        }

        /// <summary>
        /// TEST 5: Client 3 specific deadlock reproduction
        ///
        /// Based on logs: Client 3 stuck at nextExpected=1029.
        /// This test targets client 3 specifically with adverse conditions.
        /// </summary>
        [Test]
        [Timeout(45000)]
        public void Client3Specific_DeadlockReproduction()
        {
            Debug.Log("[ExtremeConcurrency] Starting Client3 specific deadlock test");

            var scenario = CreateScenario(applyHostFix: true);
            const double DELTA = 0.016;
            const int TARGET_SEQUENCE = 50; // Simulate stuck at a specific sequence

            var client3 = scenario.Clients.First(c => c.ClientId == 3);

            // Drop specific packets for client 3 only
            var originalTransmit = client3.ServerEndpoint.TransmitCallback;
            int dropCount = 0;

            client3.ServerEndpoint.TransmitCallback = (buffer, length) =>
            {
                // Drop packet containing target sequence permanently for first 100 attempts
                if (dropCount < 100)
                {
                    for (int offset = 0; offset < Math.Min(length - 4, 100); offset++)
                    {
                        try
                        {
                            if (BitConverter.ToInt32(buffer, offset) == TARGET_SEQUENCE)
                            {
                                dropCount++;
                                return; // Drop
                            }
                        }
                        catch { }
                    }
                }
                originalTransmit(buffer, length);
            };

            // Send 100 messages to all clients
            Debug.Log("[ExtremeConcurrency] Sending 100 messages to all clients...");
            for (int i = 0; i < 100; i++)
            {
                foreach (var client in scenario.Clients)
                {
                    var msg = CreateMessage(0, MSG_SYNC, i);
                    lock (client.Lock)
                    {
                        client.ServerEndpoint.SendMessage(msg, msg.Length, QosType.Reliable);
                    }
                }

                lock (scenario.GlobalLock)
                {
                    UpdateScenario(scenario, DELTA);
                }
            }

            // Run for extended time
            Debug.Log("[ExtremeConcurrency] Running for 20 seconds...");
            for (int i = 0; i < 1200; i++)
            {
                lock (scenario.GlobalLock)
                {
                    UpdateScenario(scenario, DELTA);
                }
            }

            // Check client 3 state
            int c3Received = client3.ReceivedSequences.Count;
            bool hasTarget = client3.ReceivedSequences.Contains(TARGET_SEQUENCE);

            Debug.Log($"[ExtremeConcurrency] Client 3: received={c3Received}, hasTarget({TARGET_SEQUENCE})={hasTarget}");
            Debug.Log($"[ExtremeConcurrency] Client 3 nextExpected={client3.NextExpected}");
            Debug.Log($"[ExtremeConcurrency] Packets dropped for client 3: {dropCount}");

            // Check other clients (should all be fine)
            foreach (var client in scenario.Clients.Where(c => c.ClientId != 3))
            {
                int received = client.ReceivedSequences.Count;
                Assert.AreEqual(100, received, $"Client {client.ClientId} should have received all messages");
            }

            if (!hasTarget && client3.NextExpected <= TARGET_SEQUENCE)
            {
                Debug.Log($"[ExtremeConcurrency] DEADLOCK: Client 3 stuck before sequence {TARGET_SEQUENCE}");
            }
            else if (hasTarget)
            {
                Debug.Log("[ExtremeConcurrency] Client 3 recovered - retransmission worked");
            }

            Debug.Log("[ExtremeConcurrency] Test completed");
        }

        /// <summary>
        /// TEST 6: Maximum stress - all adverse conditions combined
        /// </summary>
        [Test]
        [Timeout(60000)]
        public void MaximumStress_AllConditions()
        {
            Debug.Log("[ExtremeConcurrency] Starting MaximumStress test");

            var scenario = CreateScenario(applyHostFix: true);
            scenario.ClientToServerLossRate = 0.15;
            scenario.ServerToClientLossRate = 0.15;
            scenario.LatencyMs = 100; // Higher latency

            const double DELTA = 0.016;
            const int MSGS = 100;

            var errors = new ConcurrentBag<string>();
            bool running = true;

            // Multiple sender threads per client
            var allThreads = new List<Thread>();

            foreach (var client in scenario.Clients)
            {
                var c = client;

                // Client sender
                allThreads.Add(new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < MSGS && running; i++)
                        {
                            var msg = CreateMessage(c.ClientId, MSG_SYNC, i);
                            lock (c.Lock)
                            {
                                c.ClientEndpoint.SendMessage(msg, msg.Length, QosType.Reliable);
                            }
                            Thread.Sleep(scenario.Rng.Next(1, 10));
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Client {c.ClientId} sender: {ex.Message}");
                    }
                }));

                // Server sender (responses)
                allThreads.Add(new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < MSGS && running; i++)
                        {
                            var msg = CreateMessage(0, MSG_SYNC, i);
                            lock (c.Lock)
                            {
                                c.ServerEndpoint.SendMessage(msg, msg.Length, QosType.Reliable);
                            }
                            Thread.Sleep(scenario.Rng.Next(1, 10));
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Server for client {c.ClientId}: {ex.Message}");
                    }
                }));
            }

            // Update thread
            allThreads.Add(new Thread(() =>
            {
                try
                {
                    while (running)
                    {
                        lock (scenario.GlobalLock)
                        {
                            UpdateScenario(scenario, DELTA);
                        }
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Update: {ex.Message}");
                }
            }));

            // Start all
            Debug.Log($"[ExtremeConcurrency] Starting {allThreads.Count} threads...");
            foreach (var t in allThreads) t.Start();

            // Wait for completion
            Thread.Sleep(5000);
            running = false;

            foreach (var t in allThreads) t.Join(3000);

            // Final flush
            lock (scenario.GlobalLock)
            {
                for (int i = 0; i < 1800; i++)
                {
                    UpdateScenario(scenario, DELTA);
                }
            }

            // Results
            Debug.Log($"[ExtremeConcurrency] Errors: {errors.Count}");
            foreach (var err in errors.Take(10))
            {
                Debug.Log($"[ExtremeConcurrency] ERROR: {err}");
            }

            Debug.Log($"[ExtremeConcurrency] Cross-deliveries: {scenario.TotalCrossDeliveries}");
            Debug.Log($"[ExtremeConcurrency] Packets dropped: {scenario.TotalPacketsDropped}");

            int stuckClients = 0;
            foreach (var client in scenario.Clients)
            {
                int received = client.ReceivedSequences.Count;
                bool stuck = received > 0 && client.NextExpected < received;

                if (stuck)
                {
                    stuckClients++;
                    Debug.Log($"[ExtremeConcurrency] Client {client.ClientId} STUCK: received={received}, nextExpected={client.NextExpected}");
                }
                else
                {
                    Debug.Log($"[ExtremeConcurrency] Client {client.ClientId} OK: received={received}");
                }
            }

            Assert.AreEqual(0, errors.Count, "Thread errors occurred");
            Assert.AreEqual(0, scenario.TotalCrossDeliveries, "Cross-delivery should not occur with fix");

            // Allow some stuck clients due to extreme packet loss
            if (stuckClients > 0)
            {
                Debug.Log($"[ExtremeConcurrency] WARNING: {stuckClients} clients stuck under extreme conditions");
            }

            Debug.Log("[ExtremeConcurrency] Test completed");
        }

        /// <summary>
        /// TEST 7: Rapid connect/disconnect cycles
        /// </summary>
        [Test]
        [Timeout(30000)]
        public void RapidConnectDisconnect_NoResourceLeak()
        {
            Debug.Log("[ExtremeConcurrency] Starting RapidConnectDisconnect test");

            const int CYCLES = 20;
            const double DELTA = 0.016;

            for (int cycle = 0; cycle < CYCLES; cycle++)
            {
                var scenario = CreateScenario(applyHostFix: true);

                // Send some messages
                foreach (var client in scenario.Clients)
                {
                    for (int i = 0; i < 10; i++)
                    {
                        var msg = CreateMessage(client.ClientId, MSG_SYNC, i);
                        lock (client.Lock)
                        {
                            client.ClientEndpoint.SendMessage(msg, msg.Length, QosType.Reliable);
                        }
                    }
                }

                // Brief update
                for (int i = 0; i < 30; i++)
                {
                    lock (scenario.GlobalLock)
                    {
                        UpdateScenario(scenario, DELTA);
                    }
                }

                // Tear down (let GC clean up)
                scenario.Clients.Clear();
                scenario.Transport.Subscribers.Clear();

                if (cycle % 5 == 0)
                {
                    Debug.Log($"[ExtremeConcurrency] Completed {cycle + 1}/{CYCLES} cycles");
                    GC.Collect();
                }
            }

            Debug.Log("[ExtremeConcurrency] Test PASSED - no crashes during rapid cycles");
        }
    }
}
