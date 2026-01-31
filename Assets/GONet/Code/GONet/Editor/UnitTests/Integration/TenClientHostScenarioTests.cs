using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using ReliableNetcode;
using UnityEngine;

namespace GONet.Tests.Integration
{
    /// <summary>
    /// Integration tests simulating the real-world 10-client scenario:
    /// - 1 HOST (server + client in same process)
    /// - 9 remote clients
    ///
    /// These tests focus on the INITIAL CONNECTION PHASE where the deadlock bug occurs:
    /// - Client connects
    /// - Server sends initial sync data
    /// - Client sends SceneLoadComplete
    /// - Server assigns GONetIds
    ///
    /// The bug manifests as frozen GONetParticipants because critical messages
    /// (like SceneLoadComplete or GONetId assignment) get stuck in reliable deadlock.
    /// </summary>
    [TestFixture]
    public class TenClientHostScenarioTests
    {
        private class ReceivedMessage
        {
            public byte[] Data;
            public int Length;
            public double TimeReceived;
            public int SourceClientId;
        }

        /// <summary>
        /// Simulates a client connection to the server.
        /// Each client has its own ReliableEndpoint pair with the server.
        /// </summary>
        private class ClientConnection
        {
            public int ClientId;
            public ReliableEndpoint ClientEndpoint;
            public ReliableEndpoint ServerEndpointForClient;
            public List<ReceivedMessage> ClientReceived = new List<ReceivedMessage>();
            public List<ReceivedMessage> ServerReceived = new List<ReceivedMessage>();
            public Queue<(byte[], int, double)> ClientToServerQueue = new Queue<(byte[], int, double)>();
            public Queue<(byte[], int, double)> ServerToClientQueue = new Queue<(byte[], int, double)>();
            public bool IsHost; // True for HOST client (loopback)
            public object TransportConnection; // Simulates IGONetTransportConnection
            public double ConnectionTime; // When this client connected
            public bool HasSentSceneLoadComplete;
            public bool HasReceivedGONetId;
        }

        /// <summary>
        /// Simulates the server's shared transport that all connections subscribe to.
        /// This is where cross-delivery can happen.
        /// </summary>
        private class SharedServerTransport
        {
            public List<Action<byte[], int, object>> Subscribers = new List<Action<byte[], int, object>>();

            public void OnMessageReceived(byte[] data, int length, object source)
            {
                // Broadcast to ALL subscribers (this is the cross-delivery vector)
                foreach (var subscriber in Subscribers)
                {
                    subscriber(data, length, source);
                }
            }
        }

        private class TenClientScenario
        {
            public List<ClientConnection> Clients = new List<ClientConnection>();
            public ClientConnection HostClient; // The HOST's client connection
            public SharedServerTransport ServerTransport = new SharedServerTransport();
            public double CurrentTime;
            public double LatencyMs = 30;

            // Statistics
            public int CrossDeliveryEvents;
            public int FalseAcksDetected;
            public List<string> EventLog = new List<string>();
        }

        private TenClientScenario CreateTenClientScenario(bool applyHostFix = true)
        {
            var scenario = new TenClientScenario();

            // Create HOST client first (ClientId = 1, authority)
            var hostClient = CreateClientConnection(scenario, 1, isHost: true, applyHostFix: applyHostFix);
            scenario.HostClient = hostClient;
            scenario.Clients.Add(hostClient);

            // Create 9 remote clients (ClientIds 2-10)
            for (int i = 2; i <= 10; i++)
            {
                var client = CreateClientConnection(scenario, i, isHost: false, applyHostFix: applyHostFix);
                scenario.Clients.Add(client);
            }

            return scenario;
        }

        private ClientConnection CreateClientConnection(TenClientScenario scenario, int clientId, bool isHost, bool applyHostFix)
        {
            var client = new ClientConnection
            {
                ClientId = clientId,
                IsHost = isHost,
                ClientEndpoint = new ReliableEndpoint(),
                ServerEndpointForClient = new ReliableEndpoint(),
                TransportConnection = new object(),
                ConnectionTime = scenario.CurrentTime
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
                    TimeReceived = scenario.CurrentTime,
                    SourceClientId = 0 // From server
                });
            };

            // Server-side receive callback for this client
            client.ServerEndpointForClient.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                client.ServerReceived.Add(new ReceivedMessage
                {
                    Data = copy,
                    Length = length,
                    TimeReceived = scenario.CurrentTime,
                    SourceClientId = clientId
                });
            };

            if (isHost)
            {
                // HOST uses LOOPBACK - direct transmission, no transport
                client.ClientEndpoint.TransmitCallback = (buffer, length) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    // Direct to server endpoint (loopback)
                    client.ServerEndpointForClient.ReceivePacket(copy, length);
                };

                client.ServerEndpointForClient.TransmitCallback = (buffer, length) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    // Direct to client endpoint (loopback)
                    client.ClientEndpoint.ReceivePacket(copy, length);
                };

                // THE BUG: HOST client subscribes to shared transport (if fix not applied)
                if (!applyHostFix)
                {
                    // BUGGY: Subscribe with null connection filter
                    object hostConnection = null;
                    scenario.ServerTransport.Subscribers.Add((data, len, source) =>
                    {
                        // With null connection, filter is always false - accepts ALL packets
                        bool wouldFilter = hostConnection != null && source != hostConnection;
                        if (!wouldFilter)
                        {
                            scenario.CrossDeliveryEvents++;
                            // HOST processes remote client packets - THE BUG!
                            client.ClientEndpoint.ReceivePacket(data, len);
                        }
                    });
                }
                // If fix applied, HOST doesn't subscribe to transport (uses loopback only)
            }
            else
            {
                // Remote client transmits through queue (simulating network latency)
                client.ClientEndpoint.TransmitCallback = (buffer, length) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    client.ClientToServerQueue.Enqueue((copy, length, scenario.CurrentTime + scenario.LatencyMs / 1000.0));
                };

                // Server transmits to client through queue
                client.ServerEndpointForClient.TransmitCallback = (buffer, length) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    client.ServerToClientQueue.Enqueue((copy, length, scenario.CurrentTime + scenario.LatencyMs / 1000.0));
                };

                // Server-side subscription to shared transport (with proper filtering)
                var thisClient = client;
                scenario.ServerTransport.Subscribers.Add((data, len, source) =>
                {
                    // Proper filter: only accept if source matches this connection
                    bool wouldFilter = thisClient.TransportConnection != null && source != thisClient.TransportConnection;
                    if (!wouldFilter)
                    {
                        thisClient.ServerEndpointForClient.ReceivePacket(data, len);
                    }
                });
            }

            return client;
        }

        private void UpdateScenario(TenClientScenario scenario, double deltaTime)
        {
            scenario.CurrentTime += deltaTime;

            foreach (var client in scenario.Clients)
            {
                if (!client.IsHost)
                {
                    // Process client -> server queue (through shared transport)
                    while (client.ClientToServerQueue.Count > 0 &&
                           client.ClientToServerQueue.Peek().Item3 <= scenario.CurrentTime)
                    {
                        var (data, len, _) = client.ClientToServerQueue.Dequeue();
                        // Dispatch through shared transport
                        scenario.ServerTransport.OnMessageReceived(data, len, client.TransportConnection);
                    }

                    // Process server -> client queue
                    while (client.ServerToClientQueue.Count > 0 &&
                           client.ServerToClientQueue.Peek().Item3 <= scenario.CurrentTime)
                    {
                        var (data, len, _) = client.ServerToClientQueue.Dequeue();
                        client.ClientEndpoint.ReceivePacket(data, len);
                    }
                }

                // Update endpoints
                client.ClientEndpoint.Update(scenario.CurrentTime);
                client.ServerEndpointForClient.Update(scenario.CurrentTime);
                client.ClientEndpoint.ProcessSendBuffer_IfAppropriate();
                client.ServerEndpointForClient.ProcessSendBuffer_IfAppropriate();
            }
        }

        private byte[] CreateMessage(int clientId, int msgType, int msgIndex)
        {
            // Message format: [clientId (4)] [msgType (4)] [msgIndex (4)] [padding]
            byte[] msg = new byte[100];
            Buffer.BlockCopy(BitConverter.GetBytes(clientId), 0, msg, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(msgType), 0, msg, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(msgIndex), 0, msg, 8, 4);
            return msg;
        }

        private const int MSG_TYPE_SCENE_LOAD_COMPLETE = 1;
        private const int MSG_TYPE_GONET_ID_ASSIGNMENT = 2;
        private const int MSG_TYPE_SYNC_DATA = 3;
        private const int MSG_TYPE_HEARTBEAT = 4;

        private void Log(TenClientScenario scenario, string message)
        {
            scenario.EventLog.Add($"[{scenario.CurrentTime:F3}s] {message}");
            Debug.Log($"[TenClient] {message}");
        }

        /// <summary>
        /// TEST 1: Basic 10-client scenario with fix applied - should work correctly.
        /// </summary>
        [Test]
        public void TenClients_WithFix_AllClientsSync()
        {
            Debug.Log("[TenClient] Starting TenClients_WithFix_AllClientsSync test");

            var scenario = CreateTenClientScenario(applyHostFix: true);
            const double DELTA_TIME = 0.016; // 60 FPS
            const int MSGS_PER_CLIENT = 20;

            // Simulate initial connection sequence
            Debug.Log("[TenClient] Phase 1: Initial sync messages from server to clients");

            // Server sends initial sync to all clients
            foreach (var client in scenario.Clients)
            {
                for (int i = 0; i < 5; i++)
                {
                    var msg = CreateMessage(0, MSG_TYPE_SYNC_DATA, i);
                    client.ServerEndpointForClient.SendMessage(msg, msg.Length, QosType.Reliable);
                }
            }
            UpdateScenario(scenario, DELTA_TIME);

            Debug.Log("[TenClient] Phase 2: Clients send SceneLoadComplete");

            // Each client sends SceneLoadComplete
            foreach (var client in scenario.Clients)
            {
                var slc = CreateMessage(client.ClientId, MSG_TYPE_SCENE_LOAD_COMPLETE, 0);
                client.ClientEndpoint.SendMessage(slc, slc.Length, QosType.Reliable);
                client.HasSentSceneLoadComplete = true;
            }

            // Run simulation
            Debug.Log("[TenClient] Phase 3: Running simulation for 5 seconds...");
            for (int i = 0; i < 300; i++) // 5 seconds at 60 FPS
            {
                UpdateScenario(scenario, DELTA_TIME);

                // Server sends GONetId assignments after receiving SceneLoadComplete
                foreach (var client in scenario.Clients)
                {
                    if (!client.HasReceivedGONetId)
                    {
                        // Check if server received SceneLoadComplete from this client
                        bool gotSlc = client.ServerReceived.Any(m =>
                            BitConverter.ToInt32(m.Data, 4) == MSG_TYPE_SCENE_LOAD_COMPLETE);

                        if (gotSlc)
                        {
                            var idMsg = CreateMessage(0, MSG_TYPE_GONET_ID_ASSIGNMENT, client.ClientId);
                            client.ServerEndpointForClient.SendMessage(idMsg, idMsg.Length, QosType.Reliable);
                            client.HasReceivedGONetId = true;
                        }
                    }
                }

                // Periodic heartbeats
                if (i % 30 == 0) // Every 0.5s
                {
                    foreach (var client in scenario.Clients)
                    {
                        var hb = CreateMessage(client.ClientId, MSG_TYPE_HEARTBEAT, i);
                        client.ClientEndpoint.SendMessage(hb, hb.Length, QosType.Reliable);
                    }
                }
            }

            // Check results
            Debug.Log("[TenClient] Checking results...");

            int clientsWithSlcReceived = 0;
            int clientsWithIdReceived = 0;

            foreach (var client in scenario.Clients)
            {
                bool serverGotSlc = client.ServerReceived.Any(m =>
                    BitConverter.ToInt32(m.Data, 4) == MSG_TYPE_SCENE_LOAD_COMPLETE);
                bool clientGotId = client.ClientReceived.Any(m =>
                    BitConverter.ToInt32(m.Data, 4) == MSG_TYPE_GONET_ID_ASSIGNMENT);

                if (serverGotSlc) clientsWithSlcReceived++;
                if (clientGotId) clientsWithIdReceived++;

                Debug.Log($"[TenClient] Client {client.ClientId} (HOST={client.IsHost}): " +
                         $"ServerGotSLC={serverGotSlc}, ClientGotId={clientGotId}, " +
                         $"ServerMsgs={client.ServerReceived.Count}, ClientMsgs={client.ClientReceived.Count}");
            }

            Debug.Log($"[TenClient] Cross-delivery events: {scenario.CrossDeliveryEvents}");
            Debug.Log($"[TenClient] Clients with SLC received by server: {clientsWithSlcReceived}/10");
            Debug.Log($"[TenClient] Clients with GONetId received: {clientsWithIdReceived}/10");

            Assert.AreEqual(0, scenario.CrossDeliveryEvents,
                "With fix applied, no cross-delivery should occur");
            Assert.AreEqual(10, clientsWithSlcReceived,
                "All clients should have SceneLoadComplete received by server");
            Assert.AreEqual(10, clientsWithIdReceived,
                "All clients should receive GONetId assignment");

            Debug.Log("[TenClient] Test PASSED - All clients synced correctly with fix applied");
        }

        /// <summary>
        /// TEST 2: 10-client scenario WITHOUT fix - should show cross-delivery bug.
        /// </summary>
        [Test]
        public void TenClients_WithoutFix_CrossDeliveryOccurs()
        {
            Debug.Log("[TenClient] Starting TenClients_WithoutFix_CrossDeliveryOccurs test");

            var scenario = CreateTenClientScenario(applyHostFix: false);
            const double DELTA_TIME = 0.016;

            // Simulate same sequence as above
            foreach (var client in scenario.Clients)
            {
                for (int i = 0; i < 5; i++)
                {
                    var msg = CreateMessage(0, MSG_TYPE_SYNC_DATA, i);
                    client.ServerEndpointForClient.SendMessage(msg, msg.Length, QosType.Reliable);
                }
            }

            foreach (var client in scenario.Clients)
            {
                var slc = CreateMessage(client.ClientId, MSG_TYPE_SCENE_LOAD_COMPLETE, 0);
                client.ClientEndpoint.SendMessage(slc, slc.Length, QosType.Reliable);
            }

            // Run simulation
            for (int i = 0; i < 300; i++)
            {
                UpdateScenario(scenario, DELTA_TIME);

                foreach (var client in scenario.Clients)
                {
                    if (!client.HasReceivedGONetId)
                    {
                        bool gotSlc = client.ServerReceived.Any(m =>
                            BitConverter.ToInt32(m.Data, 4) == MSG_TYPE_SCENE_LOAD_COMPLETE);

                        if (gotSlc)
                        {
                            var idMsg = CreateMessage(0, MSG_TYPE_GONET_ID_ASSIGNMENT, client.ClientId);
                            client.ServerEndpointForClient.SendMessage(idMsg, idMsg.Length, QosType.Reliable);
                            client.HasReceivedGONetId = true;
                        }
                    }
                }
            }

            Debug.Log($"[TenClient] Cross-delivery events: {scenario.CrossDeliveryEvents}");

            // Without fix, cross-delivery SHOULD occur
            Assert.Greater(scenario.CrossDeliveryEvents, 0,
                "Without fix, cross-delivery should occur when HOST subscribes to shared transport");

            Debug.Log("[TenClient] Test PASSED - Cross-delivery detected without fix");
        }

        /// <summary>
        /// TEST 3: Heavy load during initial sync - many messages rapidly.
        /// </summary>
        [Test]
        public void TenClients_HeavyInitialLoad_NoDeadlock()
        {
            Debug.Log("[TenClient] Starting TenClients_HeavyInitialLoad test");

            var scenario = CreateTenClientScenario(applyHostFix: true);
            const double DELTA_TIME = 0.016;
            const int INITIAL_SYNC_MSGS = 50; // Heavy initial sync

            // Server blasts initial sync to all clients
            Debug.Log("[TenClient] Sending heavy initial sync...");
            foreach (var client in scenario.Clients)
            {
                for (int i = 0; i < INITIAL_SYNC_MSGS; i++)
                {
                    var msg = CreateMessage(0, MSG_TYPE_SYNC_DATA, i);
                    client.ServerEndpointForClient.SendMessage(msg, msg.Length, QosType.Reliable);
                }
            }

            // All clients send SceneLoadComplete simultaneously
            foreach (var client in scenario.Clients)
            {
                var slc = CreateMessage(client.ClientId, MSG_TYPE_SCENE_LOAD_COMPLETE, 0);
                client.ClientEndpoint.SendMessage(slc, slc.Length, QosType.Reliable);
            }

            // Run simulation with heavy traffic
            Debug.Log("[TenClient] Running heavy load simulation...");
            for (int i = 0; i < 500; i++)
            {
                UpdateScenario(scenario, DELTA_TIME);

                // Continuous traffic
                if (i % 5 == 0)
                {
                    foreach (var client in scenario.Clients)
                    {
                        var msg = CreateMessage(client.ClientId, MSG_TYPE_SYNC_DATA, i);
                        client.ClientEndpoint.SendMessage(msg, msg.Length, QosType.Reliable);
                    }
                }
            }

            // Verify all clients received their initial sync
            int clientsFullySync = 0;
            foreach (var client in scenario.Clients)
            {
                int syncMsgsReceived = client.ClientReceived.Count(m =>
                    BitConverter.ToInt32(m.Data, 4) == MSG_TYPE_SYNC_DATA);

                Debug.Log($"[TenClient] Client {client.ClientId}: received {syncMsgsReceived}/{INITIAL_SYNC_MSGS} sync msgs");

                if (syncMsgsReceived >= INITIAL_SYNC_MSGS)
                {
                    clientsFullySync++;
                }
            }

            Assert.AreEqual(10, clientsFullySync,
                $"All 10 clients should receive full initial sync, but only {clientsFullySync} did");

            Debug.Log("[TenClient] Test PASSED - Heavy initial load handled without deadlock");
        }

        /// <summary>
        /// TEST 4: Staggered client connections (realistic scenario).
        /// </summary>
        [Test]
        public void TenClients_StaggeredConnections_AllSync()
        {
            Debug.Log("[TenClient] Starting TenClients_StaggeredConnections test");

            var scenario = CreateTenClientScenario(applyHostFix: true);
            const double DELTA_TIME = 0.016;
            const double CONNECTION_INTERVAL = 0.5; // 500ms between connections

            // Stagger the connections
            for (int clientIdx = 0; clientIdx < scenario.Clients.Count; clientIdx++)
            {
                var client = scenario.Clients[clientIdx];

                // Set connection time
                client.ConnectionTime = clientIdx * CONNECTION_INTERVAL;

                Debug.Log($"[TenClient] Client {client.ClientId} connecting at {client.ConnectionTime}s");
            }

            // Run simulation with staggered processing
            double maxConnTime = scenario.Clients.Max(c => c.ConnectionTime);
            int totalCycles = (int)((maxConnTime + 5.0) / DELTA_TIME); // Run 5s after last connection

            for (int cycle = 0; cycle < totalCycles; cycle++)
            {
                UpdateScenario(scenario, DELTA_TIME);

                // Process connections as they occur
                foreach (var client in scenario.Clients)
                {
                    if (scenario.CurrentTime >= client.ConnectionTime && !client.HasSentSceneLoadComplete)
                    {
                        // Server sends initial sync
                        for (int i = 0; i < 10; i++)
                        {
                            var msg = CreateMessage(0, MSG_TYPE_SYNC_DATA, i);
                            client.ServerEndpointForClient.SendMessage(msg, msg.Length, QosType.Reliable);
                        }

                        // Client sends SceneLoadComplete
                        var slc = CreateMessage(client.ClientId, MSG_TYPE_SCENE_LOAD_COMPLETE, 0);
                        client.ClientEndpoint.SendMessage(slc, slc.Length, QosType.Reliable);
                        client.HasSentSceneLoadComplete = true;

                        Debug.Log($"[TenClient] Client {client.ClientId} sent SceneLoadComplete at {scenario.CurrentTime:F2}s");
                    }

                    // Server sends GONetId after receiving SceneLoadComplete
                    if (client.HasSentSceneLoadComplete && !client.HasReceivedGONetId)
                    {
                        bool gotSlc = client.ServerReceived.Any(m =>
                            BitConverter.ToInt32(m.Data, 4) == MSG_TYPE_SCENE_LOAD_COMPLETE);

                        if (gotSlc)
                        {
                            var idMsg = CreateMessage(0, MSG_TYPE_GONET_ID_ASSIGNMENT, client.ClientId);
                            client.ServerEndpointForClient.SendMessage(idMsg, idMsg.Length, QosType.Reliable);
                            client.HasReceivedGONetId = true;
                            Debug.Log($"[TenClient] Server sent GONetId to Client {client.ClientId}");
                        }
                    }
                }
            }

            // Verify all clients synced
            int clientsSynced = 0;
            foreach (var client in scenario.Clients)
            {
                bool gotId = client.ClientReceived.Any(m =>
                    BitConverter.ToInt32(m.Data, 4) == MSG_TYPE_GONET_ID_ASSIGNMENT);

                if (gotId) clientsSynced++;

                Debug.Log($"[TenClient] Client {client.ClientId}: GONetId received = {gotId}");
            }

            Assert.AreEqual(10, clientsSynced,
                $"All 10 clients should sync, but only {clientsSynced} did");

            Debug.Log("[TenClient] Test PASSED - Staggered connections all synced");
        }

        /// <summary>
        /// TEST 5: Packet loss during initial sync - tests retransmission.
        /// </summary>
        [Test]
        public void TenClients_WithPacketLoss_Recovers()
        {
            Debug.Log("[TenClient] Starting TenClients_WithPacketLoss test");

            var scenario = CreateTenClientScenario(applyHostFix: true);
            const double DELTA_TIME = 0.016;
            const double LOSS_RATE = 0.10; // 10% packet loss

            System.Random rng = new System.Random(12345);
            int droppedPackets = 0;

            // Add packet loss to all non-HOST clients
            foreach (var client in scenario.Clients.Where(c => !c.IsHost))
            {
                var origTransmit = client.ClientEndpoint.TransmitCallback;
                client.ClientEndpoint.TransmitCallback = (buffer, length) =>
                {
                    if (rng.NextDouble() < LOSS_RATE)
                    {
                        Interlocked.Increment(ref droppedPackets);
                        return; // Drop packet
                    }
                    origTransmit(buffer, length);
                };
            }

            // Run standard sync sequence
            foreach (var client in scenario.Clients)
            {
                for (int i = 0; i < 10; i++)
                {
                    var msg = CreateMessage(0, MSG_TYPE_SYNC_DATA, i);
                    client.ServerEndpointForClient.SendMessage(msg, msg.Length, QosType.Reliable);
                }

                var slc = CreateMessage(client.ClientId, MSG_TYPE_SCENE_LOAD_COMPLETE, 0);
                client.ClientEndpoint.SendMessage(slc, slc.Length, QosType.Reliable);
            }

            // Run for longer to allow retransmissions
            for (int i = 0; i < 600; i++) // 10 seconds
            {
                UpdateScenario(scenario, DELTA_TIME);

                foreach (var client in scenario.Clients)
                {
                    if (!client.HasReceivedGONetId)
                    {
                        bool gotSlc = client.ServerReceived.Any(m =>
                            BitConverter.ToInt32(m.Data, 4) == MSG_TYPE_SCENE_LOAD_COMPLETE);

                        if (gotSlc)
                        {
                            var idMsg = CreateMessage(0, MSG_TYPE_GONET_ID_ASSIGNMENT, client.ClientId);
                            client.ServerEndpointForClient.SendMessage(idMsg, idMsg.Length, QosType.Reliable);
                            client.HasReceivedGONetId = true;
                        }
                    }
                }
            }

            Debug.Log($"[TenClient] Dropped packets: {droppedPackets}");

            int clientsSynced = 0;
            foreach (var client in scenario.Clients)
            {
                bool gotId = client.ClientReceived.Any(m =>
                    BitConverter.ToInt32(m.Data, 4) == MSG_TYPE_GONET_ID_ASSIGNMENT);

                if (gotId) clientsSynced++;
            }

            Debug.Log($"[TenClient] Clients synced despite {LOSS_RATE * 100}% loss: {clientsSynced}/10");

            Assert.AreEqual(10, clientsSynced,
                $"All clients should sync despite packet loss, but only {clientsSynced} did");

            Debug.Log("[TenClient] Test PASSED - Recovered from packet loss");
        }

        /// <summary>
        /// TEST 6: Multi-threaded scenario - simulates real Unity threading.
        /// </summary>
        [Test]
        [Timeout(60000)]
        public void TenClients_MultiThreaded_NoDeadlock()
        {
            Debug.Log("[TenClient] Starting TenClients_MultiThreaded test");

            var scenario = CreateTenClientScenario(applyHostFix: true);
            const double DELTA_TIME = 0.016;

            object lockObj = new object();
            bool running = true;
            int errors = 0;

            // Client sender threads (simulate game threads sending data)
            var clientThreads = new List<Thread>();
            foreach (var client in scenario.Clients)
            {
                var c = client;
                var thread = new Thread(() =>
                {
                    try
                    {
                        int msgIdx = 0;
                        while (running)
                        {
                            lock (lockObj)
                            {
                                var msg = CreateMessage(c.ClientId, MSG_TYPE_SYNC_DATA, msgIdx++);
                                c.ClientEndpoint.SendMessage(msg, msg.Length, QosType.Reliable);
                            }
                            Thread.Sleep(10 + c.ClientId); // Stagger timing
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        Debug.Log($"[TenClient] Client {c.ClientId} thread error: {ex.Message}");
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
                        lock (lockObj)
                        {
                            UpdateScenario(scenario, DELTA_TIME);
                        }
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errors);
                    Debug.Log($"[TenClient] Update thread error: {ex.Message}");
                }
            });

            // Start threads
            foreach (var t in clientThreads) t.Start();
            updateThread.Start();

            // Run for 3 seconds
            Thread.Sleep(3000);
            running = false;

            // Wait for threads with timeout
            foreach (var t in clientThreads) t.Join(3000);
            updateThread.Join(3000);

            // Final flush
            lock (lockObj)
            {
                for (int i = 0; i < 200; i++)
                {
                    UpdateScenario(scenario, DELTA_TIME);
                }
            }

            // Check results
            int totalSent = 0;
            int totalReceived = 0;

            foreach (var client in scenario.Clients)
            {
                totalReceived += client.ServerReceived.Count;
                Debug.Log($"[TenClient] Client {client.ClientId}: Server received {client.ServerReceived.Count} messages");
            }

            Debug.Log($"[TenClient] Total received by server: {totalReceived}, Errors: {errors}");

            Assert.AreEqual(0, errors, "Thread errors occurred");
            Assert.Greater(totalReceived, 0, "Should have received some messages");

            Debug.Log("[TenClient] Test PASSED - Multi-threaded scenario completed without deadlock");
        }

        /// <summary>
        /// TEST 7: Specific reproduction of Client 3 deadlock scenario.
        ///
        /// From logs: Client 3 stuck at nextExpected=1029 after initial sync.
        /// This test simulates the exact conditions.
        /// </summary>
        [Test]
        public void Client3Deadlock_Reproduction_Attempt()
        {
            Debug.Log("[TenClient] Starting Client3Deadlock_Reproduction test");

            var scenario = CreateTenClientScenario(applyHostFix: true);
            const double DELTA_TIME = 0.016;

            // Focus on Client 3 (index 2 since HOST is index 0)
            var client3 = scenario.Clients[2]; // ClientId = 3

            // Drop a specific message sequence for Client 3
            int targetMsgSeq = 50; // Arbitrary "stuck" sequence
            int dropCount = 0;

            var origTransmit = client3.ServerEndpointForClient.TransmitCallback;
            client3.ServerEndpointForClient.TransmitCallback = (buffer, length) =>
            {
                // Drop packet containing our target message permanently
                for (int offset = 0; offset < Math.Min(length - 4, 100); offset++)
                {
                    if (BitConverter.ToInt32(buffer, offset) == targetMsgSeq)
                    {
                        dropCount++;
                        return; // Drop permanently
                    }
                }
                origTransmit(buffer, length);
            };

            // Send heavy traffic to Client 3
            Debug.Log($"[TenClient] Sending 100 messages to Client 3, dropping msg {targetMsgSeq}...");
            for (int i = 0; i < 100; i++)
            {
                var msg = CreateMessage(0, MSG_TYPE_SYNC_DATA, i);
                client3.ServerEndpointForClient.SendMessage(msg, msg.Length, QosType.Reliable);

                // Also send to other clients (normal traffic)
                foreach (var c in scenario.Clients.Where(x => x != client3))
                {
                    var m = CreateMessage(0, MSG_TYPE_SYNC_DATA, i);
                    c.ServerEndpointForClient.SendMessage(m, m.Length, QosType.Reliable);
                }

                UpdateScenario(scenario, DELTA_TIME);
            }

            // Run for extended time
            Debug.Log("[TenClient] Running for 10 seconds...");
            for (int i = 0; i < 600; i++)
            {
                UpdateScenario(scenario, DELTA_TIME);
            }

            // Check Client 3's state
            int client3Msgs = client3.ClientReceived.Count(m =>
                BitConverter.ToInt32(m.Data, 4) == MSG_TYPE_SYNC_DATA);

            Debug.Log($"[TenClient] Client 3 received {client3Msgs}/100 sync messages");
            Debug.Log($"[TenClient] Message {targetMsgSeq} dropped {dropCount} times");

            // Client 3 should have messages 0 to targetMsgSeq-1, but NOT targetMsgSeq or later
            HashSet<int> receivedSeqs = new HashSet<int>();
            foreach (var msg in client3.ClientReceived)
            {
                if (BitConverter.ToInt32(msg.Data, 4) == MSG_TYPE_SYNC_DATA)
                {
                    int seq = BitConverter.ToInt32(msg.Data, 8);
                    receivedSeqs.Add(seq);
                }
            }

            bool hasTarget = receivedSeqs.Contains(targetMsgSeq);
            int maxSeq = receivedSeqs.Count > 0 ? receivedSeqs.Max() : -1;

            Debug.Log($"[TenClient] Client 3 max received seq: {maxSeq}");
            Debug.Log($"[TenClient] Client 3 has target msg {targetMsgSeq}: {hasTarget}");

            if (!hasTarget && maxSeq < targetMsgSeq)
            {
                Debug.Log($"[TenClient] DEADLOCK SCENARIO: Client 3 stuck before message {targetMsgSeq}");
            }
            else if (!hasTarget && maxSeq >= targetMsgSeq)
            {
                Debug.Log($"[TenClient] WARNING: Gap detected - messages after {targetMsgSeq} delivered without it");
            }
            else
            {
                Debug.Log("[TenClient] No deadlock - message eventually delivered or retransmission worked");
            }

            // Check other clients (should not be affected)
            foreach (var client in scenario.Clients.Where(c => c != client3))
            {
                int msgs = client.ClientReceived.Count(m =>
                    BitConverter.ToInt32(m.Data, 4) == MSG_TYPE_SYNC_DATA);
                Assert.AreEqual(100, msgs, $"Client {client.ClientId} should receive all 100 messages");
            }

            Debug.Log("[TenClient] Test completed - deadlock reproduction attempted");
        }
    }
}
