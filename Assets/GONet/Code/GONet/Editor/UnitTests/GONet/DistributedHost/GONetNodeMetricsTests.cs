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
using System.Runtime.InteropServices;

namespace GONet.Editor.UnitTests.DistributedHost
{
    [TestFixture]
    public class GONetNodeMetricsTests
    {
        #region Struct Layout Tests

        [Test]
        public void GONetNodeMetrics_Size_Is32Bytes()
        {
            // Assert - struct should be exactly 32 bytes for efficient serialization
            int size = Marshal.SizeOf<GONetNodeMetrics>();
            Assert.AreEqual(32, size, "GONetNodeMetrics should be exactly 32 bytes");
        }

        #endregion

        #region CreateDefault Tests

        [Test]
        public void CreateDefault_SetsAuthorityId()
        {
            // Arrange
            ushort authorityId = 42;

            // Act
            var metrics = GONetNodeMetrics.CreateDefault(authorityId);

            // Assert
            Assert.AreEqual(authorityId, metrics.AuthorityId);
        }

        [Test]
        public void CreateDefault_SetsNetworkMetricsToUnknown()
        {
            // Act
            var metrics = GONetNodeMetrics.CreateDefault(1);

            // Assert
            Assert.AreEqual(GONetNodeMetrics.UNKNOWN_USHORT, metrics.RTT_Average_Ms);
            Assert.AreEqual(GONetNodeMetrics.UNKNOWN_BYTE, metrics.RTT_Jitter_Ms);
            Assert.AreEqual(GONetNodeMetrics.UNKNOWN_BYTE, metrics.PacketLoss_Percent);
        }

        [Test]
        public void CreateDefault_SetsHardwareMetricsToUnknown()
        {
            // Act
            var metrics = GONetNodeMetrics.CreateDefault(1);

            // Assert
            Assert.AreEqual(GONetNodeMetrics.UNKNOWN_BYTE, metrics.CPU_Headroom_Percent);
            Assert.AreEqual(GONetNodeMetrics.UNKNOWN_BYTE, metrics.FrameTime_Headroom_Ms);
            Assert.AreEqual(GONetNodeMetrics.UNKNOWN_BYTE, metrics.BatteryLevel);
        }

        [Test]
        public void CreateDefault_SetsNeutralStabilityScore()
        {
            // Act
            var metrics = GONetNodeMetrics.CreateDefault(1);

            // Assert - should start with neutral stability
            Assert.AreEqual(128, metrics.StabilityScore, "Should have neutral starting stability");
        }

        [Test]
        public void CreateDefault_SetsOptimisticNATScore()
        {
            // Act
            var metrics = GONetNodeMetrics.CreateDefault(1);

            // Assert - optimistic until proven otherwise
            Assert.AreEqual(200, metrics.NATCompatibilityScore, "Should have optimistic NAT score by default");
        }

        [Test]
        public void CreateDefault_SetsZeroUptime()
        {
            // Act
            var metrics = GONetNodeMetrics.CreateDefault(1);

            // Assert
            Assert.AreEqual(0, metrics.Uptime_Minutes);
        }

        [Test]
        public void CreateDefault_SetsNoValidityFlags()
        {
            // Act
            var metrics = GONetNodeMetrics.CreateDefault(1);

            // Assert
            Assert.AreEqual(MetricsValidityFlags.None, metrics.ValidityFlags);
        }

        #endregion

        #region Host Eligibility Tests

        [Test]
        public void HasSufficientUptimeForHost_FalseWhenZero()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.Uptime_Seconds = 0;

            // Assert
            Assert.IsFalse(metrics.HasSufficientUptimeForHost);
        }

        [Test]
        public void HasSufficientUptimeForHost_TrueWhenAtMinimum()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.Uptime_Seconds = GONetNodeMetrics.MIN_UPTIME_FOR_HOST_SECONDS;

            // Assert
            Assert.IsTrue(metrics.HasSufficientUptimeForHost);
        }

        [Test]
        public void HasSufficientUptimeForHost_TrueWhenAboveMinimum()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.Uptime_Seconds = 3600; // 1 hour in seconds

            // Assert
            Assert.IsTrue(metrics.HasSufficientUptimeForHost);
        }

        [Test]
        public void HasSufficientNATForHost_FalseWhenBelowThreshold()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.NATCompatibilityScore = GONetNodeMetrics.NAT_SCORE_DISQUALIFY_THRESHOLD - 1;

            // Assert
            Assert.IsFalse(metrics.HasSufficientNATForHost);
        }

        [Test]
        public void HasSufficientNATForHost_TrueWhenAtThreshold()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.NATCompatibilityScore = GONetNodeMetrics.NAT_SCORE_DISQUALIFY_THRESHOLD;

            // Assert
            Assert.IsTrue(metrics.HasSufficientNATForHost);
        }

        [Test]
        public void HasSufficientNATForHost_TrueWhenAboveThreshold()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.NATCompatibilityScore = 200;

            // Assert
            Assert.IsTrue(metrics.HasSufficientNATForHost);
        }

        [Test]
        public void IsEligibleForHost_RequiresBothUptimeAndNAT()
        {
            // Arrange - has uptime but bad NAT
            var metrics1 = GONetNodeMetrics.CreateDefault(1);
            metrics1.Uptime_Seconds = 600; // 10 minutes
            metrics1.NATCompatibilityScore = 10;

            // Arrange - has NAT but no uptime
            var metrics2 = GONetNodeMetrics.CreateDefault(2);
            metrics2.Uptime_Seconds = 0;
            metrics2.NATCompatibilityScore = 200;

            // Arrange - has both
            var metrics3 = GONetNodeMetrics.CreateDefault(3);
            metrics3.Uptime_Seconds = 600; // 10 minutes
            metrics3.NATCompatibilityScore = 200;

            // Assert
            Assert.IsFalse(metrics1.IsEligibleForHost, "Should fail with bad NAT");
            Assert.IsFalse(metrics2.IsEligibleForHost, "Should fail with no uptime");
            Assert.IsTrue(metrics3.IsEligibleForHost, "Should pass with both");
        }

        #endregion

        #region Battery Multiplier Tests

        [Test]
        public void BatteryMultiplier_ReturnsOne_WhenUnknown()
        {
            // Arrange - 255 = plugged in / desktop
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.BatteryLevel = GONetNodeMetrics.UNKNOWN_BYTE;

            // Assert
            Assert.AreEqual(1.0f, metrics.BatteryMultiplier);
        }

        [Test]
        public void BatteryMultiplier_ReturnsPointThree_WhenBelow20Percent()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.BatteryLevel = 19;

            // Assert
            Assert.AreEqual(0.3f, metrics.BatteryMultiplier);
        }

        [Test]
        public void BatteryMultiplier_ReturnsPointSeven_WhenBelow50Percent()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.BatteryLevel = 49;

            // Assert
            Assert.AreEqual(0.7f, metrics.BatteryMultiplier);
        }

        [Test]
        public void BatteryMultiplier_ReturnsOne_WhenAbove50Percent()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.BatteryLevel = 80;

            // Assert
            Assert.AreEqual(1.0f, metrics.BatteryMultiplier);
        }

        [Test]
        public void BatteryMultiplier_ReturnsOne_WhenAt100Percent()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.BatteryLevel = 100;

            // Assert
            Assert.AreEqual(1.0f, metrics.BatteryMultiplier);
        }

        #endregion

        #region Validity Flag Tests

        [Test]
        public void IsRTTValid_TrueWhenFlagSetAndValueNotUnknown()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.ValidityFlags = MetricsValidityFlags.RTTValid;
            metrics.RTT_Average_Ms = 50;

            // Assert
            Assert.IsTrue(metrics.IsRTTValid);
        }

        [Test]
        public void IsRTTValid_FalseWhenFlagNotSet()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.RTT_Average_Ms = 50;
            // ValidityFlags not set

            // Assert
            Assert.IsFalse(metrics.IsRTTValid);
        }

        [Test]
        public void IsRTTValid_FalseWhenValueIsUnknown()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.ValidityFlags = MetricsValidityFlags.RTTValid;
            metrics.RTT_Average_Ms = GONetNodeMetrics.UNKNOWN_USHORT;

            // Assert
            Assert.IsFalse(metrics.IsRTTValid);
        }

        [Test]
        public void IsJitterValid_TrueWhenFlagSetAndValueNotUnknown()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.ValidityFlags = MetricsValidityFlags.JitterValid;
            metrics.RTT_Jitter_Ms = 10;

            // Assert
            Assert.IsTrue(metrics.IsJitterValid);
        }

        [Test]
        public void IsPacketLossValid_TrueWhenFlagSetAndValueNotUnknown()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.ValidityFlags = MetricsValidityFlags.PacketLossValid;
            metrics.PacketLoss_Percent = 5;

            // Assert
            Assert.IsTrue(metrics.IsPacketLossValid);
        }

        #endregion

        #region Equality Tests

        [Test]
        public void Equality_BasedOnAuthorityIdAndTicks()
        {
            // Arrange
            var m1 = GONetNodeMetrics.CreateDefault(42);
            m1.MonotonicTicks = 1000;
            m1.RTT_Average_Ms = 50;

            var m2 = GONetNodeMetrics.CreateDefault(42);
            m2.MonotonicTicks = 1000;
            m2.RTT_Average_Ms = 100; // Different RTT

            // Assert - equality based on AuthorityId + MonotonicTicks only
            Assert.AreEqual(m1, m2);
        }

        [Test]
        public void Inequality_DifferentAuthorityId()
        {
            // Arrange
            var m1 = GONetNodeMetrics.CreateDefault(42);
            m1.MonotonicTicks = 1000;

            var m2 = GONetNodeMetrics.CreateDefault(43);
            m2.MonotonicTicks = 1000;

            // Assert
            Assert.AreNotEqual(m1, m2);
        }

        [Test]
        public void Inequality_DifferentTicks()
        {
            // Arrange
            var m1 = GONetNodeMetrics.CreateDefault(42);
            m1.MonotonicTicks = 1000;

            var m2 = GONetNodeMetrics.CreateDefault(42);
            m2.MonotonicTicks = 2000;

            // Assert
            Assert.AreNotEqual(m1, m2);
        }

        #endregion

        #region ToString Tests

        [Test]
        public void ToString_ContainsKeyMetrics()
        {
            // Arrange
            var metrics = GONetNodeMetrics.CreateDefault(42);
            metrics.RTT_Average_Ms = 50;
            metrics.CPU_Headroom_Percent = 80;
            metrics.Uptime_Seconds = 600; // 10 minutes = 600 seconds

            // Act
            string str = metrics.ToString();

            // Assert
            Assert.IsTrue(str.Contains("42"), "Should contain AuthorityId");
            Assert.IsTrue(str.Contains("50"), "Should contain RTT");
            Assert.IsTrue(str.Contains("80"), "Should contain CPU");
            Assert.IsTrue(str.Contains("600"), "Should contain Uptime in seconds");
        }

        #endregion

        #region Constants Tests

        [Test]
        public void Constants_HaveCorrectValues()
        {
            Assert.AreEqual(255, GONetNodeMetrics.UNKNOWN_BYTE);
            Assert.AreEqual(65535, GONetNodeMetrics.UNKNOWN_USHORT);
            Assert.AreEqual(45, GONetNodeMetrics.MIN_UPTIME_FOR_HOST_SECONDS);
            Assert.AreEqual(50, GONetNodeMetrics.NAT_SCORE_DISQUALIFY_THRESHOLD);
        }

        #endregion
    }
}
