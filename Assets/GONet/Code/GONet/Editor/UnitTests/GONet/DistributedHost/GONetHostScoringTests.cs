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
    /// <summary>
    /// Unit tests for GONetHostScoring - the smart host promotion ranking system.
    /// </summary>
    [TestFixture]
    public class GONetHostScoringTests
    {
        #region Eligibility Tests

        [Test]
        public void IsEligibleForHost_WithCanHostCapability_ReturnsTrue()
        {
            // Arrange
            var metrics = CreateTestMetrics(uptimeSeconds: 300, natScore: 200);
            var capabilities = GONetNodeCapabilities.CanHost;

            // Act
            bool eligible = GONetHostScoring.IsEligibleForHost(metrics, capabilities);

            // Assert
            Assert.IsTrue(eligible, "Node with CanHost capability and sufficient metrics should be eligible");
        }

        [Test]
        public void IsEligibleForHost_WithoutCanHostCapability_ReturnsFalse()
        {
            // Arrange
            var metrics = CreateTestMetrics(uptimeSeconds: 300, natScore: 200);
            var capabilities = GONetNodeCapabilities.None;

            // Act
            bool eligible = GONetHostScoring.IsEligibleForHost(metrics, capabilities);

            // Assert
            Assert.IsFalse(eligible, "Node without CanHost capability should be ineligible");
        }

        [Test]
        public void IsEligibleForHost_WithRequiresRelay_ReturnsFalse()
        {
            // Arrange
            var metrics = CreateTestMetrics(uptimeSeconds: 300, natScore: 200);
            var capabilities = GONetNodeCapabilities.CanHost | GONetNodeCapabilities.RequiresRelay;

            // Act
            bool eligible = GONetHostScoring.IsEligibleForHost(metrics, capabilities);

            // Assert
            Assert.IsFalse(eligible, "Node requiring relay should be ineligible");
        }

        [Test]
        public void IsEligibleForHost_WithLowUptime_ReturnsFalse()
        {
            // Arrange - uptime below threshold (< 1 minute)
            var metrics = CreateTestMetrics(uptimeSeconds: 0, natScore: 200);
            var capabilities = GONetNodeCapabilities.CanHost;

            // Act
            bool eligible = GONetHostScoring.IsEligibleForHost(metrics, capabilities);

            // Assert
            Assert.IsFalse(eligible, "Node with insufficient uptime should be ineligible");
        }

        [Test]
        public void IsEligibleForHost_WithLowNATScore_ReturnsFalse()
        {
            // Arrange - NAT score below threshold (< 50)
            var metrics = CreateTestMetrics(uptimeSeconds: 300, natScore: 40);
            var capabilities = GONetNodeCapabilities.CanHost;

            // Act
            bool eligible = GONetHostScoring.IsEligibleForHost(metrics, capabilities);

            // Assert
            Assert.IsFalse(eligible, "Node with insufficient NAT score should be ineligible");
        }

        [Test]
        public void IsEligibleForHost_WithCriticalBattery_ReturnsFalse()
        {
            // Arrange - battery below 10%
            var metrics = CreateTestMetrics(uptimeSeconds: 300, natScore: 200);
            metrics.BatteryLevel = 5; // Critical level
            var capabilities = GONetNodeCapabilities.CanHost;

            // Act
            bool eligible = GONetHostScoring.IsEligibleForHost(metrics, capabilities);

            // Assert
            Assert.IsFalse(eligible, "Node with critical battery should be ineligible");
        }

        [Test]
        public void IsEligibleForHost_WithDesktopNoBattery_ReturnsTrue()
        {
            // Arrange - desktop (unknown battery = 255)
            var metrics = CreateTestMetrics(uptimeSeconds: 300, natScore: 200);
            metrics.BatteryLevel = GONetNodeMetrics.UNKNOWN_BYTE;
            var capabilities = GONetNodeCapabilities.CanHost;

            // Act
            bool eligible = GONetHostScoring.IsEligibleForHost(metrics, capabilities);

            // Assert
            Assert.IsTrue(eligible, "Desktop node (no battery) should be eligible");
        }

        #endregion

        #region Score Calculation Tests

        [Test]
        public void CalculateNetworkScore_LowerRTT_HigherScore()
        {
            // Arrange
            var lowRttMetrics = CreateTestMetrics(rttMs: 50);
            var highRttMetrics = CreateTestMetrics(rttMs: 200);

            // Act
            float lowRttScore = GONetHostScoring.CalculateNetworkScore(lowRttMetrics, 50);
            float highRttScore = GONetHostScoring.CalculateNetworkScore(highRttMetrics, 200);

            // Assert
            Assert.Greater(lowRttScore, highRttScore, "Lower RTT should produce higher network score");
        }

        [Test]
        public void CalculateNetworkScore_JitterPenalty_ReducesScore()
        {
            // Arrange
            var lowJitterMetrics = CreateTestMetrics(rttMs: 50, jitterMs: 5);
            var highJitterMetrics = CreateTestMetrics(rttMs: 50, jitterMs: 50);

            // Act
            float lowJitterScore = GONetHostScoring.CalculateNetworkScore(lowJitterMetrics, 50);
            float highJitterScore = GONetHostScoring.CalculateNetworkScore(highJitterMetrics, 50);

            // Assert
            Assert.Greater(lowJitterScore, highJitterScore, "Higher jitter should reduce network score");
        }

        [Test]
        public void CalculateHardwareScore_MoreCPUHeadroom_HigherScore()
        {
            // Arrange
            var highCpuMetrics = CreateTestMetrics();
            highCpuMetrics.CPU_Headroom_Percent = 80;

            var lowCpuMetrics = CreateTestMetrics();
            lowCpuMetrics.CPU_Headroom_Percent = 20;

            // Act
            float highCpuScore = GONetHostScoring.CalculateHardwareScore(highCpuMetrics);
            float lowCpuScore = GONetHostScoring.CalculateHardwareScore(lowCpuMetrics);

            // Assert
            Assert.Greater(highCpuScore, lowCpuScore, "More CPU headroom should produce higher hardware score");
        }

        [Test]
        public void CalculateHardwareScore_LowBattery_PenalizesScore()
        {
            // Arrange
            var fullBatteryMetrics = CreateTestMetrics();
            fullBatteryMetrics.CPU_Headroom_Percent = 50;
            fullBatteryMetrics.BatteryLevel = 80; // Good battery

            var lowBatteryMetrics = CreateTestMetrics();
            lowBatteryMetrics.CPU_Headroom_Percent = 50;
            lowBatteryMetrics.BatteryLevel = 20; // Low battery

            // Act
            float fullBatteryScore = GONetHostScoring.CalculateHardwareScore(fullBatteryMetrics);
            float lowBatteryScore = GONetHostScoring.CalculateHardwareScore(lowBatteryMetrics);

            // Assert
            Assert.Greater(fullBatteryScore, lowBatteryScore, "Low battery should reduce hardware score");
        }

        [Test]
        public void CalculateStabilityScore_UptimeBeyondThreshold_NoAdditionalBonus()
        {
            // Arrange - both are above the 45s minimum threshold
            var highUptimeMetrics = CreateTestMetrics(uptimeSeconds: 3600); // 1 hour
            var lowUptimeMetrics = CreateTestMetrics(uptimeSeconds: 120);   // 2 minutes

            // Act
            float highUptimeScore = GONetHostScoring.CalculateStabilityScore(highUptimeMetrics);
            float lowUptimeScore = GONetHostScoring.CalculateStabilityScore(lowUptimeMetrics);

            // Assert - once past threshold, uptime doesn't give additional advantage
            // This prevents "first-to-connect wins" in live games where longer uptime
            // actually means you're more likely to leave soon
            Assert.AreEqual(highUptimeScore, lowUptimeScore,
                "Uptime beyond minimum threshold should not give additional score advantage");
        }

        [Test]
        public void CalculateStabilityScore_BelowThreshold_ZeroUptimeBonus()
        {
            // Arrange - one below threshold, one above
            var aboveThresholdMetrics = CreateTestMetrics(uptimeSeconds: 60);  // Above 45s
            var belowThresholdMetrics = CreateTestMetrics(uptimeSeconds: 30);  // Below 45s

            // Act
            float aboveScore = GONetHostScoring.CalculateStabilityScore(aboveThresholdMetrics);
            float belowScore = GONetHostScoring.CalculateStabilityScore(belowThresholdMetrics);

            // Assert - below threshold gets no uptime bonus (200 points less)
            Assert.Greater(aboveScore, belowScore,
                "Node above uptime threshold should score higher than one below");
        }

        [Test]
        public void CalculateNATScore_HigherNATCompatibility_HigherScore()
        {
            // Arrange
            var openNatMetrics = CreateTestMetrics(natScore: 255);
            var restrictedNatMetrics = CreateTestMetrics(natScore: 100);

            // Act
            float openNatScore = GONetHostScoring.CalculateNATScore(openNatMetrics);
            float restrictedNatScore = GONetHostScoring.CalculateNATScore(restrictedNatMetrics);

            // Assert
            Assert.Greater(openNatScore, restrictedNatScore, "Better NAT compatibility should produce higher NAT score");
        }

        #endregion

        #region Tiebreaker Tests

        [Test]
        public void DeterministicCompare_HigherNAT_Wins()
        {
            // Arrange
            var highNatMetrics = CreateTestMetrics(natScore: 200, uptimeSeconds: 300);
            var lowNatMetrics = CreateTestMetrics(natScore: 100, uptimeSeconds: 300);

            // Act
            int result = GONetHostScoring.DeterministicCompare(1, 2, highNatMetrics, lowNatMetrics);

            // Assert
            Assert.Less(result, 0, "Higher NAT score should win (negative = first candidate preferred)");
        }

        [Test]
        public void DeterministicCompare_SameNAT_HigherUptimeWins()
        {
            // Arrange
            var highUptimeMetrics = CreateTestMetrics(natScore: 200, uptimeSeconds: 3600);
            var lowUptimeMetrics = CreateTestMetrics(natScore: 200, uptimeSeconds: 300);

            // Act
            int result = GONetHostScoring.DeterministicCompare(1, 2, highUptimeMetrics, lowUptimeMetrics);

            // Assert
            Assert.Less(result, 0, "With equal NAT, higher uptime should win");
        }

        [Test]
        public void DeterministicCompare_SameMetrics_LowerAuthorityIdWins()
        {
            // Arrange
            var sameMetrics1 = CreateTestMetrics(natScore: 200, uptimeSeconds: 300);
            var sameMetrics2 = CreateTestMetrics(natScore: 200, uptimeSeconds: 300);

            // Act
            int result = GONetHostScoring.DeterministicCompare(1, 2, sameMetrics1, sameMetrics2);

            // Assert
            Assert.Less(result, 0, "With equal metrics, lower authority ID should win");
        }

        [Test]
        public void DeterministicCompare_IsDeterministic()
        {
            // Arrange
            var metrics1 = CreateTestMetrics(natScore: 150, uptimeSeconds: 600);
            var metrics2 = CreateTestMetrics(natScore: 150, uptimeSeconds: 600);

            // Act - compare both directions
            int result1 = GONetHostScoring.DeterministicCompare(1, 2, metrics1, metrics2);
            int result2 = GONetHostScoring.DeterministicCompare(2, 1, metrics2, metrics1);

            // Assert - results should be opposite (deterministic ordering)
            Assert.AreEqual(-result1, result2, "Comparison should be deterministic regardless of order");
        }

        #endregion

        #region Candidate Evaluation Tests

        [Test]
        public void EvaluateCandidate_EligibleNode_ReturnsValidScore()
        {
            // Arrange
            var metrics = CreateTestMetrics(rttMs: 50, uptimeSeconds: 300, natScore: 200);
            var capabilities = GONetNodeCapabilities.CanHost;

            // Act
            var eval = GONetHostScoring.EvaluateCandidate(1, metrics, capabilities, 50, false);

            // Assert
            Assert.IsTrue(eval.IsEligible, "Eligible node should have IsEligible=true");
            Assert.Greater(eval.TotalScore, 0, "Eligible node should have positive score");
        }

        [Test]
        public void EvaluateCandidate_IneligibleNode_ReturnsZeroScore()
        {
            // Arrange
            var metrics = CreateTestMetrics(uptimeSeconds: 0); // Ineligible due to low uptime
            var capabilities = GONetNodeCapabilities.CanHost;

            // Act
            var eval = GONetHostScoring.EvaluateCandidate(1, metrics, capabilities, 50, false);

            // Assert
            Assert.IsFalse(eval.IsEligible, "Ineligible node should have IsEligible=false");
            Assert.AreEqual(0, eval.TotalScore, "Ineligible node should have zero score");
        }

        [Test]
        public void EvaluateCandidate_StaleMetrics_AppliesPenalty()
        {
            // Arrange
            var metrics = CreateTestMetrics(rttMs: 50, uptimeSeconds: 300, natScore: 200);
            var capabilities = GONetNodeCapabilities.CanHost;

            // Act
            var freshEval = GONetHostScoring.EvaluateCandidate(1, metrics, capabilities, 50, isStale: false);
            var staleEval = GONetHostScoring.EvaluateCandidate(1, metrics, capabilities, 50, isStale: true);

            // Assert
            Assert.Greater(freshEval.TotalScore, staleEval.TotalScore, "Stale metrics should reduce score");
        }

        [Test]
        public void EvaluateCandidate_LowPerformance_AppliesPenalty()
        {
            // Arrange
            var lowPerfMetrics = CreateTestMetrics(
                rttMs: 50,
                uptimeSeconds: 300,
                natScore: 200,
                cpuHeadroom: GONetHostScoring.CRITICAL_CPU_HEADROOM_THRESHOLD_PERCENT);
            lowPerfMetrics.FrameTime_Headroom_Ms = GONetHostScoring.CRITICAL_FRAME_HEADROOM_MS;
            var capabilities = GONetNodeCapabilities.CanHost;

            var baselineMetrics = CreateTestMetrics(rttMs: 50, uptimeSeconds: 300, natScore: 200, cpuHeadroom: 50);

            // Act
            var lowPerfEval = GONetHostScoring.EvaluateCandidate(1, lowPerfMetrics, capabilities, 50, false);
            var baselineEval = GONetHostScoring.EvaluateCandidate(1, baselineMetrics, capabilities, 50, false);

            // Assert
            Assert.IsTrue(lowPerfEval.IsEligible, "Low performance should penalize, not disqualify");
            Assert.Less(lowPerfEval.PerformanceMultiplier, 1f, "Low performance should apply a penalty multiplier");
            Assert.Greater(baselineEval.TotalScore, lowPerfEval.TotalScore, "Penalty should reduce total score");
        }

        #endregion

        #region Best Candidate Selection Tests

        [Test]
        public void EvaluateBestViceHost_SelectsBestCandidate()
        {
            // Arrange
            var candidates = new List<(ushort, GONetNodeMetrics, GONetNodeCapabilities, float, bool)>
            {
                (1, CreateTestMetrics(rttMs: 200, uptimeSeconds: 300), GONetNodeCapabilities.CanHost, 200, false),
                (2, CreateTestMetrics(rttMs: 50, uptimeSeconds: 300), GONetNodeCapabilities.CanHost, 50, false), // Best RTT
                (3, CreateTestMetrics(rttMs: 150, uptimeSeconds: 300), GONetNodeCapabilities.CanHost, 150, false)
            };

            // Act
            ushort bestId = GONetHostScoring.EvaluateBestViceHost(candidates, 0, 0, out var bestEval);

            // Assert
            Assert.AreEqual(2, bestId, "Should select candidate with best network (lowest RTT)");
            Assert.IsTrue(bestEval.IsEligible, "Best candidate should be eligible");
        }

        [Test]
        public void EvaluateBestViceHost_Hysteresis_PreventsFlapping()
        {
            // Arrange - current vice host has score 800, new candidate has slightly better score 805
            var candidates = new List<(ushort, GONetNodeMetrics, GONetNodeCapabilities, float, bool)>
            {
                (1, CreateTestMetrics(rttMs: 48, uptimeSeconds: 300, natScore: 200), GONetNodeCapabilities.CanHost, 48, false), // Slightly better
                (2, CreateTestMetrics(rttMs: 50, uptimeSeconds: 300, natScore: 200), GONetNodeCapabilities.CanHost, 50, false)  // Current vice host
            };

            // Calculate scores to set up hysteresis test
            var currentViceHostEval = GONetHostScoring.EvaluateCandidate(2,
                CreateTestMetrics(rttMs: 50, uptimeSeconds: 300, natScore: 200),
                GONetNodeCapabilities.CanHost, 50, false);

            // Act - should NOT change because difference is less than threshold
            ushort bestId = GONetHostScoring.EvaluateBestViceHost(candidates, 2, currentViceHostEval.TotalScore, out _);

            // Assert - should keep current vice host due to hysteresis
            Assert.AreEqual(2, bestId, "Should keep current vice host due to hysteresis threshold");
        }

        [Test]
        public void EvaluateBestViceHost_ExcludesIneligibleCandidates()
        {
            // Arrange
            var candidates = new List<(ushort, GONetNodeMetrics, GONetNodeCapabilities, float, bool)>
            {
                (1, CreateTestMetrics(rttMs: 50, uptimeSeconds: 0), GONetNodeCapabilities.CanHost, 50, false), // Ineligible: low uptime
                (2, CreateTestMetrics(rttMs: 100, uptimeSeconds: 300, natScore: 30), GONetNodeCapabilities.CanHost, 100, false), // Ineligible: low NAT
                (3, CreateTestMetrics(rttMs: 150, uptimeSeconds: 300), GONetNodeCapabilities.CanHost, 150, false) // Eligible
            };

            // Act
            ushort bestId = GONetHostScoring.EvaluateBestViceHost(candidates, 0, 0, out var bestEval);

            // Assert
            Assert.AreEqual(3, bestId, "Should select only eligible candidate");
        }

        [Test]
        public void EvaluateBestViceHost_NoEligibleCandidates_ReturnsZero()
        {
            // Arrange - all ineligible
            var candidates = new List<(ushort, GONetNodeMetrics, GONetNodeCapabilities, float, bool)>
            {
                (1, CreateTestMetrics(uptimeSeconds: 0), GONetNodeCapabilities.CanHost, 50, false), // Ineligible
                (2, CreateTestMetrics(uptimeSeconds: 0), GONetNodeCapabilities.CanHost, 100, false) // Ineligible
            };

            // Act
            ushort bestId = GONetHostScoring.EvaluateBestViceHost(candidates, 0, 0, out _);

            // Assert
            Assert.AreEqual(0, bestId, "Should return 0 when no eligible candidates");
        }

        #endregion

        #region Helper Methods

        private static GONetNodeMetrics CreateTestMetrics(
            ushort rttMs = 100,
            byte jitterMs = 10,
            ushort uptimeSeconds = 300, // 5 minutes default
            byte natScore = 200,
            byte cpuHeadroom = 50,
            byte stabilityScore = 200)
        {
            var metrics = GONetNodeMetrics.CreateDefault(1);
            metrics.RTT_Average_Ms = rttMs;
            metrics.RTT_Jitter_Ms = jitterMs;
            metrics.Uptime_Seconds = uptimeSeconds;
            metrics.NATCompatibilityScore = natScore;
            metrics.CPU_Headroom_Percent = cpuHeadroom;
            metrics.FrameTime_Headroom_Ms = 10;
            metrics.StabilityScore = stabilityScore;
            metrics.BatteryLevel = GONetNodeMetrics.UNKNOWN_BYTE; // Desktop
            metrics.ValidityFlags = MetricsValidityFlags.AllValid;
            return metrics;
        }

        #endregion
    }
}
