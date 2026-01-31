/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 *
 * Authorized use is explicitly limited to the following:
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on original source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 * -The ability to modify source code for local use only
 * -The ability to distribute products based on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using NUnit.Framework;
using GONet.DistributedHost;
using System.Collections.Generic;

namespace GONet.Editor.UnitTests.DistributedHost
{
    [TestFixture]
    public class GONetGossipTests
    {
        #region GossipTopology Tests

        [Test]
        public void GossipTopology_HasCorrectValues()
        {
            // Verify enum values for serialization stability
            Assert.AreEqual(0, (int)GossipTopology.Star);
            Assert.AreEqual(1, (int)GossipTopology.Mesh);
        }

        #endregion

        #region GossipMetricsMessage Tests

        [Test]
        public void GossipMetricsMessage_CanBeCreated()
        {
            // Arrange
            var identity = new GONetNodeIdentity
            {
                PersistentId = 12345,
                SessionAuthorityId = 1
            };
            var metrics = GONetNodeMetrics.CreateDefault(1);

            // Act
            var message = new GossipMetricsMessage
            {
                Identity = identity,
                Metrics = metrics,
                HostEpoch = 5,
                IsDelta = true
            };

            // Assert
            Assert.AreEqual(identity.PersistentId, message.Identity.PersistentId);
            Assert.AreEqual(1, message.Identity.SessionAuthorityId);
            Assert.AreEqual(5u, message.HostEpoch);
            Assert.IsTrue(message.IsDelta);
        }

        [Test]
        public void GossipMetricsMessage_ImplementsITransientEvent()
        {
            // Arrange
            var message = new GossipMetricsMessage();

            // Assert
            Assert.IsTrue(message is ITransientEvent);
        }

        [Test]
        public void GossipMetricsMessage_ImplementsILocalOnlyPublish()
        {
            // Arrange
            var message = new GossipMetricsMessage();

            // Assert
            Assert.IsTrue(message is ILocalOnlyPublish);
        }

        #endregion

        #region GossipAggregateMessage Tests

        [Test]
        public void GossipAggregateMessage_CanBeCreated()
        {
            // Arrange
            var hostIdentity = new HostIdentity(12345, 2, 1, 0);
            var nodeMetrics = new List<GossipMetricsMessage>
            {
                new GossipMetricsMessage
                {
                    Identity = new GONetNodeIdentity { SessionAuthorityId = 1 },
                    Metrics = GONetNodeMetrics.CreateDefault(1)
                },
                new GossipMetricsMessage
                {
                    Identity = new GONetNodeIdentity { SessionAuthorityId = 2 },
                    Metrics = GONetNodeMetrics.CreateDefault(2)
                }
            };

            // Act
            var message = new GossipAggregateMessage
            {
                HostIdentity = hostIdentity,
                NodeMetrics = nodeMetrics
            };

            // Assert
            Assert.AreEqual(hostIdentity, message.HostIdentity);
            Assert.AreEqual(2, message.NodeMetrics.Count);
        }

        [Test]
        public void GossipAggregateMessage_ImplementsITransientEvent()
        {
            // Arrange
            var message = new GossipAggregateMessage();

            // Assert
            Assert.IsTrue(message is ITransientEvent);
        }

        [Test]
        public void GossipAggregateMessage_ImplementsILocalOnlyPublish()
        {
            // Arrange
            var message = new GossipAggregateMessage();

            // Assert
            Assert.IsTrue(message is ILocalOnlyPublish);
        }

        #endregion

        #region HostHeartbeatMessage Tests

        [Test]
        public void HostHeartbeatMessage_CanBeCreated()
        {
            // Arrange
            var hostIdentity = new HostIdentity(12345, 2, 1, 2);
            var hostMetrics = GONetNodeMetrics.CreateDefault(1);

            // Act
            var message = new HostHeartbeatMessage
            {
                HostIdentity = hostIdentity,
                HostMetrics = hostMetrics,
                ViceHostScore = 85.5f
            };

            // Assert
            Assert.AreEqual(hostIdentity, message.HostIdentity);
            Assert.AreEqual(1, message.HostMetrics.AuthorityId);
            Assert.AreEqual(85.5f, message.ViceHostScore, 0.001f);
        }

        [Test]
        public void HostHeartbeatMessage_ImplementsITransientEvent()
        {
            // Arrange
            var message = new HostHeartbeatMessage();

            // Assert
            Assert.IsTrue(message is ITransientEvent);
        }

        #endregion

        #region Constants Tests

        [Test]
        public void GossipManager_Constants_HaveCorrectValues()
        {
            // Normal update every 2 seconds
            Assert.AreEqual(2.0f, GONetGossipManager.NORMAL_UPDATE_INTERVAL_SECONDS);

            // Churn mode every 1 second
            Assert.AreEqual(1.0f, GONetGossipManager.CHURN_UPDATE_INTERVAL_SECONDS);

            // Churn detection window
            Assert.AreEqual(10.0f, GONetGossipManager.CHURN_DETECTION_WINDOW_SECONDS);

            // Stale threshold
            Assert.AreEqual(6.0f, GONetGossipManager.METRICS_STALE_THRESHOLD_SECONDS);

            // Max tracked nodes for scalability
            Assert.AreEqual(100, GONetGossipManager.MAX_TRACKED_NODES);
        }

        #endregion

        #region Topology Documentation Tests

        /// <summary>
        /// Documents the expected behavior in Star topology.
        /// Star topology: All gossip flows through the current host.
        /// - Non-host nodes send metrics TO the host
        /// - Host aggregates and broadcasts TO all nodes
        /// </summary>
        [Test]
        public void StarTopology_Documentation()
        {
            // This test documents expected Star topology behavior:
            // 1. Non-host nodes send GossipMetricsMessage TO the host
            // 2. Host receives all GossipMetricsMessage from nodes
            // 3. Host creates GossipAggregateMessage containing ALL node metrics
            // 4. Host broadcasts GossipAggregateMessage to ALL nodes
            // 5. Non-host nodes receive GossipAggregateMessage and update their metrics table

            Assert.Pass("Star topology behavior documented");
        }

        /// <summary>
        /// Documents the expected behavior in Mesh topology.
        /// Mesh topology: True P2P, each peer sends directly to all other peers.
        /// - Requires P2P-capable transport (e.g., Steamworks)
        /// </summary>
        [Test]
        public void MeshTopology_Documentation()
        {
            // This test documents expected Mesh topology behavior:
            // 1. Each node sends GossipMetricsMessage to ALL other nodes
            // 2. Each node receives GossipMetricsMessage from ALL other nodes
            // 3. No aggregation needed - direct peer-to-peer
            // 4. Lower latency (no hub hop)

            Assert.Pass("Mesh topology behavior documented");
        }

        #endregion

        #region MetricsValidityFlags Tests

        [Test]
        public void MetricsValidityFlags_HasCorrectFlags()
        {
            // Verify all expected flags exist
            Assert.AreEqual(0, (int)MetricsValidityFlags.None);

            // RTT and network flags
            Assert.IsTrue((MetricsValidityFlags.RTTValid & MetricsValidityFlags.RTTValid) != 0);
            Assert.IsTrue((MetricsValidityFlags.JitterValid & MetricsValidityFlags.JitterValid) != 0);
            Assert.IsTrue((MetricsValidityFlags.PacketLossValid & MetricsValidityFlags.PacketLossValid) != 0);

            // Hardware flags
            Assert.IsTrue((MetricsValidityFlags.CPUHeadroomValid & MetricsValidityFlags.CPUHeadroomValid) != 0);
            Assert.IsTrue((MetricsValidityFlags.BatteryValid & MetricsValidityFlags.BatteryValid) != 0);

            // NAT flag
            Assert.IsTrue((MetricsValidityFlags.NATTypeValid & MetricsValidityFlags.NATTypeValid) != 0);
        }

        [Test]
        public void MetricsValidityFlags_CanBeCombined()
        {
            // Arrange & Act
            var combined = MetricsValidityFlags.RTTValid | MetricsValidityFlags.JitterValid | MetricsValidityFlags.CPUHeadroomValid;

            // Assert
            Assert.IsTrue((combined & MetricsValidityFlags.RTTValid) != 0);
            Assert.IsTrue((combined & MetricsValidityFlags.JitterValid) != 0);
            Assert.IsTrue((combined & MetricsValidityFlags.CPUHeadroomValid) != 0);
            Assert.IsFalse((combined & MetricsValidityFlags.PacketLossValid) != 0);
        }

        #endregion

        #region Message Timestamp Tests

        [Test]
        public void GossipMetricsMessage_OccurredAtElapsedTicks_CanBeSet()
        {
            // Arrange
            var message = new GossipMetricsMessage();

            // Act
            message.OccurredAtElapsedTicks = 123456789;

            // Assert
            Assert.AreEqual(123456789, message.OccurredAtElapsedTicks);
        }

        [Test]
        public void GossipAggregateMessage_OccurredAtElapsedTicks_CanBeSet()
        {
            // Arrange
            var message = new GossipAggregateMessage();

            // Act
            message.OccurredAtElapsedTicks = 987654321;

            // Assert
            Assert.AreEqual(987654321, message.OccurredAtElapsedTicks);
        }

        [Test]
        public void HostHeartbeatMessage_OccurredAtElapsedTicks_CanBeSet()
        {
            // Arrange
            var message = new HostHeartbeatMessage();

            // Act
            message.OccurredAtElapsedTicks = 555555555;

            // Assert
            Assert.AreEqual(555555555, message.OccurredAtElapsedTicks);
        }

        #endregion
    }
}
