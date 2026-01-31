/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GONet.Transport;
using NUnit.Framework;

namespace GONet.Tests.Transport
{
    /// <summary>
    /// Unit tests for NetworkConditionSimulator.
    /// Tests latency, jitter, packet loss, and duplication simulation.
    /// </summary>
    [TestFixture]
    public class NetworkConditionSimulatorTests
    {
        private MockTransport mockTransport;
        private NetworkConditionSimulator simulator;

        [SetUp]
        public void SetUp()
        {
            mockTransport = new MockTransport();
        }

        [TearDown]
        public void TearDown()
        {
            simulator?.Dispose();
            mockTransport?.Dispose();
        }

        #region Basic Functionality Tests

        [Test]
        public void Send_WhenSimulationDisabled_SendsImmediately()
        {
            // Arrange
            var config = new NetworkConditionConfig { IsEnabled = false };
            simulator = new NetworkConditionSimulator(mockTransport, config);

            byte[] data = new byte[] { 1, 2, 3 };

            // Act
            simulator.Send(data, data.Length, GONetTransportQoS.Unreliable);

            // Assert - packet should be sent immediately (no Update needed)
            Assert.AreEqual(1, mockTransport.SentPackets.Count, "Packet should be sent immediately when simulation disabled");
        }

        [Test]
        public void Send_WhenSimulationEnabled_DelaysPacket()
        {
            // Arrange
            var config = new NetworkConditionConfig
            {
                IsEnabled = true,
                LatencyMs = 100,
                JitterMs = 0
            };
            simulator = new NetworkConditionSimulator(mockTransport, config);

            byte[] data = new byte[] { 1, 2, 3 };

            // Act
            simulator.Send(data, data.Length, GONetTransportQoS.Unreliable);
            simulator.Update(); // Process queue immediately

            // Assert - packet should NOT be sent yet (still in delay queue)
            Assert.AreEqual(0, mockTransport.SentPackets.Count, "Packet should be delayed");
            Assert.AreEqual(1, simulator.SendQueueCount, "Packet should be in send queue");
        }

        [Test]
        public void Send_AfterLatencyExpires_PacketDelivered()
        {
            // Arrange
            var config = new NetworkConditionConfig
            {
                IsEnabled = true,
                LatencyMs = 50,
                JitterMs = 0
            };
            simulator = new NetworkConditionSimulator(mockTransport, config);

            byte[] data = new byte[] { 1, 2, 3 };

            // Act
            simulator.Send(data, data.Length, GONetTransportQoS.Unreliable);

            // Wait for latency to expire
            Thread.Sleep(70); // 50ms latency + buffer
            simulator.Update();

            // Assert
            Assert.AreEqual(1, mockTransport.SentPackets.Count, "Packet should be delivered after latency expires");
            Assert.AreEqual(0, simulator.SendQueueCount, "Send queue should be empty");
        }

        #endregion

        #region Latency Tests

        [Test]
        public void Latency_MeasuredCorrectly()
        {
            // Arrange
            const int expectedLatencyMs = 100;
            var config = new NetworkConditionConfig
            {
                IsEnabled = true,
                LatencyMs = expectedLatencyMs,
                JitterMs = 0
            };
            simulator = new NetworkConditionSimulator(mockTransport, config);

            byte[] data = new byte[] { 1, 2, 3 };
            var stopwatch = Stopwatch.StartNew();

            // Act
            simulator.Send(data, data.Length, GONetTransportQoS.Unreliable);

            // Poll until delivered or timeout
            while (mockTransport.SentPackets.Count == 0 && stopwatch.ElapsedMilliseconds < 500)
            {
                Thread.Sleep(10);
                simulator.Update();
            }

            stopwatch.Stop();

            // Assert
            Assert.AreEqual(1, mockTransport.SentPackets.Count, "Packet should be delivered");
            Assert.GreaterOrEqual(stopwatch.ElapsedMilliseconds, expectedLatencyMs - 10, "Latency should be at least expected");
            Assert.LessOrEqual(stopwatch.ElapsedMilliseconds, expectedLatencyMs + 50, "Latency shouldn't exceed expected by much");
        }

        [Test]
        public void Latency_MultiplePackets_EachDelayedIndependently()
        {
            // Arrange
            var config = new NetworkConditionConfig
            {
                IsEnabled = true,
                LatencyMs = 100,
                JitterMs = 0
            };
            simulator = new NetworkConditionSimulator(mockTransport, config);

            // Act - Send 5 packets 20ms apart
            for (int i = 0; i < 5; i++)
            {
                simulator.Send(new byte[] { (byte)i }, 1, GONetTransportQoS.Unreliable);
                Thread.Sleep(20);
            }

            // At this point (100ms after first send), first packet should be ready
            Thread.Sleep(20); // Extra buffer
            simulator.Update();

            // Assert - First packet delivered, others still queued
            Assert.GreaterOrEqual(mockTransport.SentPackets.Count, 1, "At least first packet should be delivered");
            Assert.Less(mockTransport.SentPackets.Count, 5, "Not all packets should be delivered yet");

            // Wait for all packets
            Thread.Sleep(150);
            simulator.Update();

            Assert.AreEqual(5, mockTransport.SentPackets.Count, "All packets should be delivered");
        }

        #endregion

        #region Jitter Tests

        [Test]
        public void Jitter_AddsVarianceToLatency()
        {
            // Arrange
            var config = new NetworkConditionConfig
            {
                IsEnabled = true,
                LatencyMs = 50,
                JitterMs = 30 // ±30ms variance
            };
            simulator = new NetworkConditionSimulator(mockTransport, config);

            var deliveryTimes = new List<long>();

            // Act - Send 20 packets and measure delivery times
            for (int i = 0; i < 20; i++)
            {
                var sw = Stopwatch.StartNew();
                simulator.Send(new byte[] { (byte)i }, 1, GONetTransportQoS.Unreliable);

                // Wait for delivery
                while (mockTransport.SentPackets.Count <= i && sw.ElapsedMilliseconds < 200)
                {
                    Thread.Sleep(5);
                    simulator.Update();
                }

                sw.Stop();
                deliveryTimes.Add(sw.ElapsedMilliseconds);
            }

            // Assert - Check variance exists
            long min = long.MaxValue, max = long.MinValue;
            foreach (var t in deliveryTimes)
            {
                if (t < min) min = t;
                if (t > max) max = t;
            }

            long variance = max - min;

            // With ±30ms jitter, we expect some variance (at least 10ms spread over 20 samples)
            Assert.GreaterOrEqual(variance, 10, $"Expected jitter variance, got min={min}ms, max={max}ms");

            // All times should be within expected bounds (20ms to 80ms)
            foreach (var t in deliveryTimes)
            {
                Assert.GreaterOrEqual(t, 15, $"Delivery time {t}ms below expected minimum");
                Assert.LessOrEqual(t, 120, $"Delivery time {t}ms above expected maximum");
            }
        }

        #endregion

        #region Packet Loss Tests

        [Test]
        public void PacketLoss_DropsPackets()
        {
            // Arrange - 50% packet loss
            var config = new NetworkConditionConfig
            {
                IsEnabled = true,
                LatencyMs = 10,
                JitterMs = 0,
                PacketLossPercent = 50f
            };
            simulator = new NetworkConditionSimulator(mockTransport, config);

            // Act - Send 100 packets
            for (int i = 0; i < 100; i++)
            {
                simulator.Send(new byte[] { (byte)i }, 1, GONetTransportQoS.Unreliable);
            }

            // Wait for all to process
            Thread.Sleep(50);
            simulator.Update();

            // Assert - With 50% loss, we expect roughly 40-60 packets (allow wide margin for randomness)
            int delivered = mockTransport.SentPackets.Count;
            Assert.GreaterOrEqual(delivered, 25, $"Too many packets dropped: {delivered}/100");
            Assert.LessOrEqual(delivered, 75, $"Too few packets dropped: {delivered}/100");

            UnityEngine.Debug.Log($"[PacketLoss Test] 50% loss: {delivered}/100 packets delivered ({100-delivered}% actual loss)");
        }

        [Test]
        public void PacketLoss_ZeroPercent_NoDrops()
        {
            // Arrange
            var config = new NetworkConditionConfig
            {
                IsEnabled = true,
                LatencyMs = 10,
                JitterMs = 0,
                PacketLossPercent = 0f
            };
            simulator = new NetworkConditionSimulator(mockTransport, config);

            // Act
            for (int i = 0; i < 50; i++)
            {
                simulator.Send(new byte[] { (byte)i }, 1, GONetTransportQoS.Unreliable);
            }

            Thread.Sleep(50);
            simulator.Update();

            // Assert
            Assert.AreEqual(50, mockTransport.SentPackets.Count, "No packets should be dropped with 0% loss");
        }

        #endregion

        #region Duplication Tests

        [Test]
        public void Duplication_CreatesExtraPackets()
        {
            // Arrange - 100% duplication (every packet duplicated)
            var config = new NetworkConditionConfig
            {
                IsEnabled = true,
                LatencyMs = 10,
                JitterMs = 0,
                PacketLossPercent = 0f,
                DuplicatePercent = 100f
            };
            simulator = new NetworkConditionSimulator(mockTransport, config);

            // Act
            for (int i = 0; i < 10; i++)
            {
                simulator.Send(new byte[] { (byte)i }, 1, GONetTransportQoS.Unreliable);
            }

            Thread.Sleep(50);
            simulator.Update();

            // Assert - With 100% duplication, we expect 20 packets (10 original + 10 duplicates)
            Assert.AreEqual(20, mockTransport.SentPackets.Count, "Each packet should be duplicated");
        }

        [Test]
        public void Duplication_PartialRate_CreatesSomeDuplicates()
        {
            // Arrange - 50% duplication
            var config = new NetworkConditionConfig
            {
                IsEnabled = true,
                LatencyMs = 10,
                JitterMs = 0,
                PacketLossPercent = 0f,
                DuplicatePercent = 50f
            };
            simulator = new NetworkConditionSimulator(mockTransport, config);

            // Act
            for (int i = 0; i < 100; i++)
            {
                simulator.Send(new byte[] { (byte)i }, 1, GONetTransportQoS.Unreliable);
            }

            Thread.Sleep(50);
            simulator.Update();

            // Assert - With 50% duplication, expect 125-175 packets (100 + ~50 duplicates)
            int delivered = mockTransport.SentPackets.Count;
            Assert.GreaterOrEqual(delivered, 115, $"Expected some duplicates: {delivered}/100+");
            Assert.LessOrEqual(delivered, 175, $"Too many duplicates: {delivered}/100+");

            UnityEngine.Debug.Log($"[Duplication Test] 50% dup: {delivered} packets from 100 sent ({delivered - 100} duplicates)");
        }

        #endregion

        #region Receive-Side Simulation Tests

        [Test]
        public void Receive_SimulatesLatency()
        {
            // Arrange
            var config = new NetworkConditionConfig
            {
                IsEnabled = true,
                LatencyMs = 50,
                JitterMs = 0
            };
            simulator = new NetworkConditionSimulator(mockTransport, config);

            var receivedPackets = new List<byte[]>();
            simulator.OnMessageReceived += (data, length, qos, source, channel) =>
            {
                byte[] copy = new byte[length];
                Array.Copy(data, copy, length);
                receivedPackets.Add(copy);
            };

            // Act - Simulate incoming packet from mock transport
            mockTransport.SimulateIncomingPacket(new byte[] { 42 }, 1, GONetTransportQoS.Unreliable, null, 0);

            // Immediately after - should be in receive queue
            simulator.Update();
            Assert.AreEqual(0, receivedPackets.Count, "Packet should be delayed in receive queue");

            // Wait for latency
            Thread.Sleep(70);
            simulator.Update();

            // Assert
            Assert.AreEqual(1, receivedPackets.Count, "Packet should be delivered after latency");
            Assert.AreEqual(42, receivedPackets[0][0], "Packet data should be preserved");
        }

        #endregion

        #region Config Presets Tests

        [Test]
        public void Presets_HaveReasonableValues()
        {
            var lan = NetworkConditionConfig.LAN;
            var goodWifi = NetworkConditionConfig.GoodWiFi;
            var internet = NetworkConditionConfig.Internet;
            var poorWifi = NetworkConditionConfig.PoorWiFi;
            var bad = NetworkConditionConfig.BadNetwork;

            // Assert increasing latency
            Assert.Less(lan.LatencyMs, goodWifi.LatencyMs, "LAN should have lower latency than GoodWiFi");
            Assert.Less(goodWifi.LatencyMs, internet.LatencyMs, "GoodWiFi should have lower latency than Internet");
            Assert.Less(internet.LatencyMs, poorWifi.LatencyMs, "Internet should have lower latency than PoorWiFi");
            Assert.Less(poorWifi.LatencyMs, bad.LatencyMs, "PoorWiFi should have lower latency than BadNetwork");

            // Assert all are enabled
            Assert.IsTrue(lan.IsEnabled);
            Assert.IsTrue(goodWifi.IsEnabled);
            Assert.IsTrue(internet.IsEnabled);
            Assert.IsTrue(poorWifi.IsEnabled);
            Assert.IsTrue(bad.IsEnabled);
        }

        #endregion

        #region Mock Transport

        /// <summary>
        /// Mock transport for testing. Captures sent packets and allows simulating received packets.
        /// </summary>
        private class MockTransport : IGONetTransport
        {
            public List<(byte[] Data, int Length, GONetTransportQoS QoS)> SentPackets { get; } = new List<(byte[], int, GONetTransportQoS)>();

            public event Func<IGONetTransportConnectionRequest, bool> OnServerConnectionRequested;
            public event Action<IGONetTransportConnection> OnServerClientConnected;
            public event Action<IGONetTransportConnection, GONetTransportDisconnectReason> OnServerClientDisconnected;
            public event Action OnClientConnected;
            public event Action<GONetTransportDisconnectReason> OnClientDisconnected;
            public event Action<GONetTransportClientState> OnClientStateChanged;
            public event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte> OnMessageReceived;
            public event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte, long> OnMessageReceivedWithTimestamp;

            public float RTTMilliseconds => 0;
            public float PacketLoss => 0;
            public float SentBandwidthKBPS => 0;
            public float ReceivedBandwidthKBPS => 0;
            public bool IsServer => false;
            public bool IsClient => true;
            public bool IsConnected => true;
            public GONetTransportCapabilities Capabilities => GONetTransportCapabilities.None;

            public void Initialize(GONetTransportConfig config) { }
            public void Shutdown() { }
            public void Dispose() { }
            public void StartServer(int port, int maxConnections) { }
            public void StopServer() { }
            public void DisconnectConnection(IGONetTransportConnection connection, GONetTransportDisconnectReason reason) { }
            public void ConnectClient(string address, int port, int timeoutSeconds, byte[] authData = null) { }
            public void DisconnectClient() { }
            public void Update() { }
            public bool IsServerRunningLocally(int port) => false;
            public int GetMaxMessageSize(GONetTransportQoS qos) => 1200;

            public void Send(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection target = null, byte channel = 0)
            {
                byte[] copy = new byte[length];
                Array.Copy(data, copy, length);
                SentPackets.Add((copy, length, qos));
            }

            public void Broadcast(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection excludeConnection = null, byte channel = 0)
            {
                Send(data, length, qos, null, channel);
            }

            /// <summary>
            /// Simulate receiving a packet from remote endpoint.
            /// </summary>
            public void SimulateIncomingPacket(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection source, byte channel)
            {
                OnMessageReceived?.Invoke(data, length, qos, source, channel);
            }
        }

        #endregion
    }
}
