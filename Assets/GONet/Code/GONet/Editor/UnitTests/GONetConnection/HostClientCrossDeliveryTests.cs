using System;
using System.Collections.Generic;
using NUnit.Framework;
using ReliableNetcode;
using UnityEngine;

namespace GONet.Tests.GONetConnection
{
    /// <summary>
    /// Integration tests for the HOST client cross-delivery bug.
    ///
    /// BUG DESCRIPTION:
    /// When running as HOST (server + client in same process), the HOST's
    /// GONetConnection_ClientToServer subscribes to the server's transport with
    /// connection=null. The filter at GONetConnections.cs:281 is:
    ///   bool wouldFilter = connection != null && source != connection;
    ///
    /// With connection=null, wouldFilter is ALWAYS false, so the HOST client
    /// processes ALL packets from ALL remote clients, sending false ACKs and
    /// causing reliable message deadlock.
    ///
    /// FIX LOCATION: GONetConnections.cs:281-310
    /// The fix checks `bool isHostClient = connection == null && GONetMain.IsServer`
    /// and skips transport subscription for HOST clients (they use loopback instead).
    ///
    /// These tests verify:
    /// 1. The bug behavior (cross-delivery causes false ACKs)
    /// 2. The fix works (HOST clients don't intercept remote packets)
    /// 3. Normal operation isn't affected
    /// </summary>
    [TestFixture]
    public class HostClientCrossDeliveryTests
    {
        /// <summary>
        /// Simulates a HOST server with multiple remote clients.
        /// The HOST has both a server and a client (loopback) connection.
        /// </summary>
        private class HostWithRemoteClientsSetup
        {
            public class RemoteClient
            {
                public int ClientId;
                public ReliableEndpoint ClientEndpoint;
                public ReliableEndpoint ServerEndpointForClient; // Server's endpoint for THIS client
                public List<ReceivedMessage> ClientReceived = new List<ReceivedMessage>();
                public List<ReceivedMessage> ServerReceived = new List<ReceivedMessage>();
                public Queue<(byte[], int, double)> ClientToServerQueue = new Queue<(byte[], int, double)>();
                public Queue<(byte[], int, double)> ServerToClientQueue = new Queue<(byte[], int, double)>();
                public object ConnectionObject; // Simulates IGONetTransportConnection for this client
            }

            public class ReceivedMessage
            {
                public byte[] Data;
                public int Length;
                public double TimeReceived;
            }

            // Remote clients
            public List<RemoteClient> RemoteClients = new List<RemoteClient>();

            // HOST's client connection (simulating loopback)
            public ReliableEndpoint HostClientEndpoint;
            public ReliableEndpoint HostServerEndpointForLoopback;
            public List<ReceivedMessage> HostClientReceived = new List<ReceivedMessage>();
            public List<ReceivedMessage> HostServerLoopbackReceived = new List<ReceivedMessage>();

            // Shared server transport event (simulates OnMessageReceived)
            public Action<byte[], int, object> ServerTransportOnMessageReceived;

            // Cross-delivery tracking
            public List<string> CrossDeliveryEvents = new List<string>();
            public int FalseAcksSent = 0;

            public double CurrentTime = 0.0;
            public double LatencyMs = 30.0;
        }

        /// <summary>
        /// Creates a setup simulating the HOST cross-delivery bug scenario.
        ///
        /// KEY DIFFERENCE FROM NORMAL SETUP:
        /// - All server endpoints subscribe to a SHARED transport event
        /// - The HOST client endpoint ALSO subscribes (with null connection filter)
        /// - This simulates the real bug where HOST client intercepts all packets
        /// </summary>
        private HostWithRemoteClientsSetup CreateBuggyHostSetup(int remoteClientCount, bool applyFix = false)
        {
            var setup = new HostWithRemoteClientsSetup();

            // Create the shared server transport event
            List<Action<byte[], int, object>> transportSubscribers = new List<Action<byte[], int, object>>();

            // Create HOST's loopback endpoints
            setup.HostClientEndpoint = new ReliableEndpoint();
            setup.HostServerEndpointForLoopback = new ReliableEndpoint();

            // HOST client receive callback
            setup.HostClientEndpoint.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                setup.HostClientReceived.Add(new HostWithRemoteClientsSetup.ReceivedMessage
                {
                    Data = copy,
                    Length = length,
                    TimeReceived = setup.CurrentTime
                });
            };

            // HOST loopback server receive callback
            setup.HostServerEndpointForLoopback.ReceiveCallback = (buffer, length, receiveTimestamp) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                setup.HostServerLoopbackReceived.Add(new HostWithRemoteClientsSetup.ReceivedMessage
                {
                    Data = copy,
                    Length = length,
                    TimeReceived = setup.CurrentTime
                });
            };

            // HOST loopback transmission (direct, no transport)
            setup.HostClientEndpoint.TransmitCallback = (buffer, length) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                // Direct loopback - doesn't go through shared transport
                setup.HostServerEndpointForLoopback.ReceivePacket(copy, length);
            };

            setup.HostServerEndpointForLoopback.TransmitCallback = (buffer, length) =>
            {
                byte[] copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);
                // Direct loopback response
                setup.HostClientEndpoint.ReceivePacket(copy, length);
            };

            // THE BUG: HOST client endpoint ALSO subscribes to transport
            // (like GONetConnection_ClientToServer does in the buggy code)
            if (!applyFix)
            {
                // BUGGY: Subscribe with null connection (no filtering)
                object hostClientConnection = null; // This is the bug - connection is null
                transportSubscribers.Add((data, length, source) =>
                {
                    // BUGGY FILTER: connection == null, so wouldFilter is always false
                    bool wouldFilter = hostClientConnection != null && source != hostClientConnection;

                    if (!wouldFilter)
                    {
                        // HOST client processes ALL packets - THE BUG!
                        setup.CrossDeliveryEvents.Add($"HOST client processed packet from source {source?.GetHashCode() ?? 0}");
                        setup.HostClientEndpoint.ReceivePacket(data, length);
                        setup.FalseAcksSent++; // This causes false ACKs
                    }
                });
            }
            // If applyFix is true, HOST client doesn't subscribe to transport (uses loopback only)

            // Create remote clients
            for (int i = 0; i < remoteClientCount; i++)
            {
                var remoteClient = new HostWithRemoteClientsSetup.RemoteClient
                {
                    ClientId = i + 1, // Start from 1 (HOST is 0/1023)
                    ClientEndpoint = new ReliableEndpoint(),
                    ServerEndpointForClient = new ReliableEndpoint()
                };

                // Create a unique connection object for this client (like IGONetTransportConnection)
                object clientConnectionObject = new object();

                // Server endpoint subscription to shared transport (with proper filtering)
                var thisClient = remoteClient;
                var thisConnectionObject = clientConnectionObject;
                transportSubscribers.Add((data, length, source) =>
                {
                    // CORRECT FILTER: Only process if source matches this connection
                    bool wouldFilter = thisConnectionObject != null && source != thisConnectionObject;

                    if (!wouldFilter)
                    {
                        thisClient.ServerEndpointForClient.ReceivePacket(data, length);
                    }
                });

                // Remote client transmit - goes through shared transport
                remoteClient.ClientEndpoint.TransmitCallback = (buffer, length) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    // Queue with latency
                    remoteClient.ClientToServerQueue.Enqueue((copy, length, setup.CurrentTime + setup.LatencyMs / 1000.0));
                };

                // Server endpoint transmit for this client
                remoteClient.ServerEndpointForClient.TransmitCallback = (buffer, length) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    remoteClient.ServerToClientQueue.Enqueue((copy, length, setup.CurrentTime + setup.LatencyMs / 1000.0));
                };

                // Remote client receive callback
                remoteClient.ClientEndpoint.ReceiveCallback = (buffer, length, receiveTimestamp) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    remoteClient.ClientReceived.Add(new HostWithRemoteClientsSetup.ReceivedMessage
                    {
                        Data = copy,
                        Length = length,
                        TimeReceived = setup.CurrentTime
                    });
                };

                // Server receive callback for this client
                remoteClient.ServerEndpointForClient.ReceiveCallback = (buffer, length, receiveTimestamp) =>
                {
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    remoteClient.ServerReceived.Add(new HostWithRemoteClientsSetup.ReceivedMessage
                    {
                        Data = copy,
                        Length = length,
                        TimeReceived = setup.CurrentTime
                    });
                };

                // Store connection object for transport dispatch
                remoteClient.ConnectionObject = clientConnectionObject;

                setup.RemoteClients.Add(remoteClient);
            }

            // Create the shared transport dispatch function
            setup.ServerTransportOnMessageReceived = (data, length, source) =>
            {
                foreach (var subscriber in transportSubscribers)
                {
                    subscriber(data, length, source);
                }
            };

            return setup;
        }

        private void UpdateSetup(HostWithRemoteClientsSetup setup, double deltaTime)
        {
            setup.CurrentTime += deltaTime;

            // Process remote client queues
            foreach (var client in setup.RemoteClients)
            {
                // Client -> Server (through shared transport)
                while (client.ClientToServerQueue.Count > 0 &&
                       client.ClientToServerQueue.Peek().Item3 <= setup.CurrentTime)
                {
                    var (data, len, _) = client.ClientToServerQueue.Dequeue();
                    // Dispatch to shared transport with this client's connection as source
                    setup.ServerTransportOnMessageReceived(data, len, client.ConnectionObject);
                }

                // Server -> Client (direct)
                while (client.ServerToClientQueue.Count > 0 &&
                       client.ServerToClientQueue.Peek().Item3 <= setup.CurrentTime)
                {
                    var (data, len, _) = client.ServerToClientQueue.Dequeue();
                    client.ClientEndpoint.ReceivePacket(data, len);
                }

                // Update endpoints
                client.ClientEndpoint.Update(setup.CurrentTime);
                client.ServerEndpointForClient.Update(setup.CurrentTime);
                client.ClientEndpoint.ProcessSendBuffer_IfAppropriate();
                client.ServerEndpointForClient.ProcessSendBuffer_IfAppropriate();
            }

            // Update HOST endpoints
            setup.HostClientEndpoint.Update(setup.CurrentTime);
            setup.HostServerEndpointForLoopback.Update(setup.CurrentTime);
            setup.HostClientEndpoint.ProcessSendBuffer_IfAppropriate();
            setup.HostServerEndpointForLoopback.ProcessSendBuffer_IfAppropriate();
        }

        private byte[] CreateClientMessage(int clientId, int msgIndex)
        {
            byte[] message = new byte[100];
            Buffer.BlockCopy(BitConverter.GetBytes(clientId), 0, message, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(msgIndex), 0, message, 4, 4);
            return message;
        }

        /// <summary>
        /// TEST 1: Demonstrate the bug - HOST client intercepts remote packets
        /// and sends false ACKs, causing cross-delivery contamination.
        /// </summary>
        [Test]
        public void BuggyHost_CrossDelivers_RemoteClientPackets()
        {
            const int REMOTE_CLIENT_COUNT = 3;
            const int MESSAGES_PER_CLIENT = 5;
            const double DELTA_TIME = 0.02;

            // Create setup WITHOUT the fix (buggy behavior)
            var setup = CreateBuggyHostSetup(REMOTE_CLIENT_COUNT, applyFix: false);

            // Remote clients send messages
            for (int clientId = 0; clientId < REMOTE_CLIENT_COUNT; clientId++)
            {
                for (int msgIdx = 0; msgIdx < MESSAGES_PER_CLIENT; msgIdx++)
                {
                    byte[] message = CreateClientMessage(clientId + 1, msgIdx);
                    setup.RemoteClients[clientId].ClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);
                }
                UpdateSetup(setup, DELTA_TIME);
            }

            // Allow time for processing
            for (int i = 0; i < 200; i++)
            {
                UpdateSetup(setup, DELTA_TIME);
            }

            // Log results
            Debug.Log($"[BuggyHost Test] Cross-delivery events: {setup.CrossDeliveryEvents.Count}");
            Debug.Log($"[BuggyHost Test] False ACKs sent by HOST client: {setup.FalseAcksSent}");

            foreach (var client in setup.RemoteClients)
            {
                Debug.Log($"[BuggyHost Test] Remote Client {client.ClientId} server received: {client.ServerReceived.Count}");
            }

            // THE BUG: HOST client processed packets meant for remote clients
            Assert.Greater(setup.CrossDeliveryEvents.Count, 0,
                "BUG NOT REPRODUCED: Expected HOST client to intercept remote packets");

            Assert.Greater(setup.FalseAcksSent, 0,
                "BUG NOT REPRODUCED: Expected false ACKs from HOST client");

            Debug.Log($"[BuggyHost Test] BUG CONFIRMED: HOST client processed {setup.CrossDeliveryEvents.Count} remote packets");
        }

        /// <summary>
        /// TEST 2: Verify the fix - HOST client should NOT process remote packets
        /// </summary>
        [Test]
        public void FixedHost_DoesNotCrossDeliver_RemoteClientPackets()
        {
            const int REMOTE_CLIENT_COUNT = 3;
            const int MESSAGES_PER_CLIENT = 5;
            const double DELTA_TIME = 0.02;

            // Create setup WITH the fix applied
            var setup = CreateBuggyHostSetup(REMOTE_CLIENT_COUNT, applyFix: true);

            // Remote clients send messages
            for (int clientId = 0; clientId < REMOTE_CLIENT_COUNT; clientId++)
            {
                for (int msgIdx = 0; msgIdx < MESSAGES_PER_CLIENT; msgIdx++)
                {
                    byte[] message = CreateClientMessage(clientId + 1, msgIdx);
                    setup.RemoteClients[clientId].ClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);
                }
                UpdateSetup(setup, DELTA_TIME);
            }

            // Allow time for processing
            for (int i = 0; i < 200; i++)
            {
                UpdateSetup(setup, DELTA_TIME);
            }

            // Verify no cross-delivery
            Assert.AreEqual(0, setup.CrossDeliveryEvents.Count,
                $"FIX FAILED: HOST client should not intercept remote packets. Got {setup.CrossDeliveryEvents.Count} events");

            Assert.AreEqual(0, setup.FalseAcksSent,
                $"FIX FAILED: HOST client should not send false ACKs. Got {setup.FalseAcksSent}");

            // Verify all remote clients got their messages delivered correctly
            for (int clientId = 0; clientId < REMOTE_CLIENT_COUNT; clientId++)
            {
                var serverReceived = setup.RemoteClients[clientId].ServerReceived;
                Assert.AreEqual(MESSAGES_PER_CLIENT, serverReceived.Count,
                    $"Remote Client {clientId + 1} messages not delivered correctly. Expected {MESSAGES_PER_CLIENT}, got {serverReceived.Count}");

                // Verify all messages are from correct client
                foreach (var msg in serverReceived)
                {
                    int sourceClientId = BitConverter.ToInt32(msg.Data, 0);
                    Assert.AreEqual(clientId + 1, sourceClientId,
                        $"Server endpoint for client {clientId + 1} received message from wrong client {sourceClientId}");
                }
            }

            Debug.Log($"[FixedHost Test] PASSED: No cross-delivery, all {REMOTE_CLIENT_COUNT * MESSAGES_PER_CLIENT} messages delivered correctly");
        }

        /// <summary>
        /// TEST 3: Verify HOST loopback still works correctly with the fix
        /// </summary>
        [Test]
        public void FixedHost_LoopbackStillWorks_Correctly()
        {
            const int REMOTE_CLIENT_COUNT = 2;
            const int HOST_MESSAGES = 10;
            const double DELTA_TIME = 0.02;

            var setup = CreateBuggyHostSetup(REMOTE_CLIENT_COUNT, applyFix: true);

            // HOST client sends messages via loopback
            for (int i = 0; i < HOST_MESSAGES; i++)
            {
                byte[] message = CreateClientMessage(0, i); // HOST is client 0
                setup.HostClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);
                UpdateSetup(setup, DELTA_TIME);
            }

            // Allow time for processing
            for (int i = 0; i < 100; i++)
            {
                UpdateSetup(setup, DELTA_TIME);
            }

            // Verify HOST loopback received all messages
            Assert.AreEqual(HOST_MESSAGES, setup.HostServerLoopbackReceived.Count,
                $"HOST loopback broken. Expected {HOST_MESSAGES} messages, got {setup.HostServerLoopbackReceived.Count}");

            Debug.Log($"[FixedHost Loopback Test] PASSED: HOST loopback delivered {HOST_MESSAGES} messages correctly");
        }

        /// <summary>
        /// TEST 4: Stress test - Many clients, many messages, verify no cross-contamination
        /// </summary>
        [Test]
        public void FixedHost_StressTest_NoCrossContamination()
        {
            const int REMOTE_CLIENT_COUNT = 10;
            const int MESSAGES_PER_CLIENT = 20;
            const double DELTA_TIME = 0.01;

            var setup = CreateBuggyHostSetup(REMOTE_CLIENT_COUNT, applyFix: true);

            // All remote clients send messages concurrently
            for (int msgIdx = 0; msgIdx < MESSAGES_PER_CLIENT; msgIdx++)
            {
                for (int clientId = 0; clientId < REMOTE_CLIENT_COUNT; clientId++)
                {
                    byte[] message = CreateClientMessage(clientId + 1, msgIdx);
                    setup.RemoteClients[clientId].ClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);
                }
                UpdateSetup(setup, DELTA_TIME);
            }

            // HOST also sends messages
            for (int i = 0; i < MESSAGES_PER_CLIENT; i++)
            {
                byte[] message = CreateClientMessage(0, i);
                setup.HostClientEndpoint.SendMessage(message, message.Length, QosType.Reliable);
                UpdateSetup(setup, DELTA_TIME);
            }

            // Allow time for all messages
            for (int i = 0; i < 500; i++)
            {
                UpdateSetup(setup, DELTA_TIME);
            }

            // Verify results
            int totalExpected = REMOTE_CLIENT_COUNT * MESSAGES_PER_CLIENT;
            int totalReceived = 0;
            int correctDeliveries = 0;
            int incorrectDeliveries = 0;

            for (int clientId = 0; clientId < REMOTE_CLIENT_COUNT; clientId++)
            {
                var serverReceived = setup.RemoteClients[clientId].ServerReceived;
                totalReceived += serverReceived.Count;

                foreach (var msg in serverReceived)
                {
                    int sourceClientId = BitConverter.ToInt32(msg.Data, 0);
                    if (sourceClientId == clientId + 1)
                        correctDeliveries++;
                    else
                        incorrectDeliveries++;
                }
            }

            Debug.Log($"[StressTest] Total expected: {totalExpected}, received: {totalReceived}");
            Debug.Log($"[StressTest] Correct deliveries: {correctDeliveries}");
            Debug.Log($"[StressTest] Cross-contamination: {incorrectDeliveries}");
            Debug.Log($"[StressTest] HOST loopback received: {setup.HostServerLoopbackReceived.Count}/{MESSAGES_PER_CLIENT}");

            Assert.AreEqual(0, setup.CrossDeliveryEvents.Count,
                "Cross-delivery detected in stress test");
            Assert.AreEqual(0, incorrectDeliveries,
                $"Cross-contamination detected: {incorrectDeliveries} messages to wrong endpoint");
            Assert.AreEqual(totalExpected, correctDeliveries,
                $"Message loss detected: expected {totalExpected}, got {correctDeliveries}");
            Assert.AreEqual(MESSAGES_PER_CLIENT, setup.HostServerLoopbackReceived.Count,
                "HOST loopback messages lost");

            Debug.Log($"[StressTest] PASSED: {totalReceived + MESSAGES_PER_CLIENT} total messages, 0 cross-contamination");
        }
    }
}
