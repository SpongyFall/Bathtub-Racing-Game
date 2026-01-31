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

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace GONet.DistributedHost
{
    /// <summary>
    /// Deterministic scoring system for vice host selection.
    /// Evaluates candidates based on network quality, hardware capacity, stability, and NAT compatibility.
    ///
    /// Score formula: TotalScore = NetworkScore + HardwareScore + StabilityScore + NATScore
    /// Balanced weights: Network 30%, Hardware 30%, Stability 25%, NAT 15%
    ///
    /// This runs on the host to select the best vice host candidate.
    /// All scoring is deterministic to ensure consistent results across nodes during failover tiebreaks.
    /// </summary>
    public static class GONetHostScoring
    {
        #region Weight Constants

        /// <summary>
        /// Network quality weight (RTT to all peers, jitter, packet loss).
        /// Lower RTT = better network centrality = better host candidate.
        /// </summary>
        public const float NETWORK_WEIGHT = 0.30f;

        /// <summary>
        /// Hardware capacity weight (CPU headroom, frame time, battery).
        /// More headroom = more capacity to handle hosting duties.
        /// </summary>
        public const float HARDWARE_WEIGHT = 0.30f;

        /// <summary>
        /// Stability weight (uptime, connection stability score).
        /// Longer uptime = more stable = better host candidate.
        /// </summary>
        public const float STABILITY_WEIGHT = 0.25f;

        /// <summary>
        /// NAT compatibility weight (ability to accept incoming connections).
        /// Better NAT = easier for peers to connect = better host candidate.
        /// </summary>
        public const float NAT_WEIGHT = 0.15f;

        #endregion

        #region Score Constants

        /// <summary>
        /// Maximum points for each score component (before weighting).
        /// Total max score = 1000 points.
        /// </summary>
        public const float MAX_COMPONENT_SCORE = 1000f;

        /// <summary>
        /// Hysteresis threshold to prevent vice host flapping.
        /// New candidate must exceed current by this amount to trigger change.
        /// Kept low (5 points) because hardware metrics are often unreliable,
        /// leading to similar scores where network RTT is the main differentiator.
        /// </summary>
        public const float VICE_HOST_CHANGE_THRESHOLD = 5f;

        /// <summary>
        /// Epsilon for floating point score comparisons in tiebreaker.
        /// </summary>
        public const float SCORE_EPSILON = 0.001f;

        /// <summary>
        /// Penalty multiplier applied when metrics are stale (>6 seconds old).
        /// </summary>
        public const float STALE_METRICS_PENALTY = 0.75f;

        /// <summary>
        /// Default RTT to use when unknown (pessimistic but not disqualifying).
        /// </summary>
        public const float DEFAULT_UNKNOWN_RTT_MS = 200f;

        /// <summary>
        /// Default jitter to use when unknown.
        /// </summary>
        public const float DEFAULT_UNKNOWN_JITTER_MS = 50f;

        /// <summary>
        /// Multiplier for unknown peer RTT fallback (use RTT-to-host * this).
        /// </summary>
        public const float UNKNOWN_PEER_RTT_PENALTY = 1.2f;

        /// <summary>
        /// Critical battery level below which node is disqualified from hosting.
        /// </summary>
        public const byte CRITICAL_BATTERY_LEVEL = 10;

        /// <summary>
        /// CPU headroom thresholds for performance penalty (percent).
        /// </summary>
        public const byte LOW_CPU_HEADROOM_THRESHOLD_PERCENT = 15;
        public const byte CRITICAL_CPU_HEADROOM_THRESHOLD_PERCENT = 5;

        /// <summary>
        /// Frame-time headroom thresholds for performance penalty (milliseconds).
        /// </summary>
        public const byte LOW_FRAME_HEADROOM_MS = 2;
        public const byte CRITICAL_FRAME_HEADROOM_MS = 1;

        /// <summary>
        /// Multipliers applied to total score when performance headroom is low.
        /// </summary>
        public const float LOW_PERFORMANCE_MULTIPLIER = 0.6f;
        public const float CRITICAL_PERFORMANCE_MULTIPLIER = 0.3f;

        #endregion

        #region Eligibility Checking

        /// <summary>
        /// Checks if a node is eligible to become host.
        /// </summary>
        /// <param name="metrics">Node metrics</param>
        /// <param name="capabilities">Node capabilities</param>
        /// <returns>True if eligible, false if disqualified</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEligibleForHost(GONetNodeMetrics metrics, GONetNodeCapabilities capabilities)
        {
            // Must have CanHost capability
            if ((capabilities & GONetNodeCapabilities.CanHost) == 0)
                return false;

            // RequiresRelay is disqualifying
            if ((capabilities & GONetNodeCapabilities.RequiresRelay) != 0)
                return false;

            // Minimum uptime requirement (new joiner cooldown)
            if (!metrics.HasSufficientUptimeForHost)
                return false;

            // NAT score must be sufficient
            if (!metrics.HasSufficientNATForHost)
                return false;

            // Critical battery level is disqualifying (unless plugged in/desktop)
            if (metrics.BatteryLevel != GONetNodeMetrics.UNKNOWN_BYTE &&
                metrics.BatteryLevel < CRITICAL_BATTERY_LEVEL)
                return false;

            return true;
        }

        /// <summary>
        /// Checks eligibility using node identity (which contains capabilities).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEligibleForHost(GONetNodeMetrics metrics, GONetNodeIdentity identity)
        {
            return IsEligibleForHost(metrics, identity.Capabilities);
        }

        #endregion

        #region Score Calculation

        /// <summary>
        /// Calculates the total score for a host candidate.
        /// </summary>
        /// <param name="metrics">Node metrics</param>
        /// <param name="avgRttToAllPeersMs">Average RTT to all peers in milliseconds (from RTT matrix)</param>
        /// <param name="isStale">True if metrics are stale (>6 seconds old)</param>
        /// <returns>Total score (0-1000)</returns>
        public static float CalculateTotalScore(GONetNodeMetrics metrics, float avgRttToAllPeersMs, bool isStale = false)
        {
            float networkScore = CalculateNetworkScore(metrics, avgRttToAllPeersMs);
            float hardwareScore = CalculateHardwareScore(metrics);
            float stabilityScore = CalculateStabilityScore(metrics);
            float natScore = CalculateNATScore(metrics);

            float totalScore = (networkScore * NETWORK_WEIGHT) +
                              (hardwareScore * HARDWARE_WEIGHT) +
                              (stabilityScore * STABILITY_WEIGHT) +
                              (natScore * NAT_WEIGHT);

            // Apply stale penalty if metrics are old
            if (isStale)
            {
                totalScore *= STALE_METRICS_PENALTY;
            }

            totalScore *= GetPerformancePenaltyMultiplier(metrics);

            return Mathf.Max(0f, totalScore);
        }

        /// <summary>
        /// Calculates network quality score (0-1000).
        /// Lower RTT and jitter = higher score.
        /// </summary>
        /// <param name="metrics">Node metrics</param>
        /// <param name="avgRttToAllPeersMs">Average RTT to all peers (from mesh measurement)</param>
        /// <returns>Network score 0-1000</returns>
        public static float CalculateNetworkScore(GONetNodeMetrics metrics, float avgRttToAllPeersMs)
        {
            // RTT component: 0ms = 700 points, 400ms+ = 0 points (linear)
            // Using aggregate RTT to all peers for network centrality
            float rttScore = Mathf.Max(0f, 700f - (avgRttToAllPeersMs * 1.75f));

            // Jitter penalty: -2 points per ms of jitter
            float jitter = metrics.IsJitterValid ? metrics.RTT_Jitter_Ms : DEFAULT_UNKNOWN_JITTER_MS;
            float jitterPenalty = jitter * 2f;

            // Packet loss penalty: -10 points per 1% packet loss (severe penalty)
            float packetLoss = metrics.IsPacketLossValid ? metrics.PacketLoss_Percent : 0f;
            float packetLossPenalty = packetLoss * 10f;

            float networkScore = Mathf.Max(0f, rttScore - jitterPenalty - packetLossPenalty);
            return Mathf.Min(networkScore, MAX_COMPONENT_SCORE);
        }

        /// <summary>
        /// Calculates hardware capacity score (0-1000).
        /// More headroom = higher score.
        /// </summary>
        public static float CalculateHardwareScore(GONetNodeMetrics metrics)
        {
            // CPU headroom: 0-100% maps to 0-500 points
            float cpuHeadroom = (metrics.ValidityFlags & MetricsValidityFlags.CPUHeadroomValid) != 0
                ? metrics.CPU_Headroom_Percent
                : 50f; // Default to neutral
            float cpuScore = cpuHeadroom * 5f;

            // Frame time headroom: 0-33ms maps to 0-500 points
            // 33ms = 30fps target, more headroom = smoother performance
            float frameHeadroom = metrics.FrameTime_Headroom_Ms != GONetNodeMetrics.UNKNOWN_BYTE
                ? metrics.FrameTime_Headroom_Ms
                : 10f; // Default to modest headroom
            float frameScore = Mathf.Min(frameHeadroom * 15f, 500f);

            float hardwareScore = cpuScore + frameScore;

            // Apply battery multiplier (penalizes mobile devices on low battery)
            hardwareScore *= metrics.BatteryMultiplier;

            return Mathf.Min(hardwareScore, MAX_COMPONENT_SCORE);
        }

        /// <summary>
        /// Calculates stability score (0-1000).
        /// Once minimum uptime threshold is met, uptime doesn't give additional points.
        /// This prevents "first-to-connect" advantage in games where longer playtime
        /// actually means you're more likely to leave soon.
        /// </summary>
        public static float CalculateStabilityScore(GONetNodeMetrics metrics)
        {
            // Uptime: flat bonus once minimum threshold is met, no advantage for longer uptime
            // Rationale: In live games, longer uptime = more likely to leave soon
            float uptimeScore = metrics.HasSufficientUptimeForHost ? 200f : 0f;

            // Stability track record: 0-255 raw maps to 0-800 points
            // This reflects disconnect history, reconnects, packet loss, etc.
            // This is the PRIMARY stability factor since it reflects actual connection quality
            float stabilityMultiplier = metrics.StabilityScore / 255f;
            float stabilityBonus = stabilityMultiplier * 800f;

            return Mathf.Min(uptimeScore + stabilityBonus, MAX_COMPONENT_SCORE);
        }

        /// <summary>
        /// Calculates NAT compatibility score (0-1000).
        /// Open NAT = higher score = easier for peers to connect.
        /// </summary>
        public static float CalculateNATScore(GONetNodeMetrics metrics)
        {
            // NAT score: 0-255 raw maps to 0-1000 points
            // Already validated to be >= 50 (threshold) if we reach here
            float natScore = (metrics.NATCompatibilityScore / 255f) * MAX_COMPONENT_SCORE;
            return natScore;
        }

        #endregion

        #region Candidate Evaluation

        /// <summary>
        /// Result of candidate evaluation.
        /// </summary>
        public struct CandidateEvaluation
        {
            public ushort AuthorityId;
            public float TotalScore;
            public float NetworkScore;
            public float HardwareScore;
            public float StabilityScore;
            public float NATScore;
            public float PerformanceMultiplier;
            public bool IsEligible;

            public override string ToString()
            {
                return $"Candidate(Auth:{AuthorityId}, Score:{TotalScore:F1}, N:{NetworkScore:F0} H:{HardwareScore:F0} S:{StabilityScore:F0} NAT:{NATScore:F0}, Perf:{PerformanceMultiplier:F2}, Eligible:{IsEligible})";
            }
        }

        /// <summary>
        /// Evaluates a single candidate and returns detailed scoring breakdown.
        /// </summary>
        /// <param name="authorityId">Candidate's authority ID</param>
        /// <param name="metrics">Candidate's metrics</param>
        /// <param name="capabilities">Candidate's capabilities</param>
        /// <param name="avgRttToAllPeersMs">Average RTT to all peers</param>
        /// <param name="isStale">True if metrics are stale</param>
        public static CandidateEvaluation EvaluateCandidate(
            ushort authorityId,
            GONetNodeMetrics metrics,
            GONetNodeCapabilities capabilities,
            float avgRttToAllPeersMs,
            bool isStale = false)
        {
            var eval = new CandidateEvaluation
            {
                AuthorityId = authorityId,
                IsEligible = IsEligibleForHost(metrics, capabilities),
                PerformanceMultiplier = 1f
            };

            if (!eval.IsEligible)
            {
                // Return zero scores for ineligible candidates
                return eval;
            }

            eval.NetworkScore = CalculateNetworkScore(metrics, avgRttToAllPeersMs) * NETWORK_WEIGHT;
            eval.HardwareScore = CalculateHardwareScore(metrics) * HARDWARE_WEIGHT;
            eval.StabilityScore = CalculateStabilityScore(metrics) * STABILITY_WEIGHT;
            eval.NATScore = CalculateNATScore(metrics) * NAT_WEIGHT;

            eval.TotalScore = eval.NetworkScore + eval.HardwareScore + eval.StabilityScore + eval.NATScore;

            if (isStale)
            {
                eval.TotalScore *= STALE_METRICS_PENALTY;
            }

            eval.PerformanceMultiplier = GetPerformancePenaltyMultiplier(metrics);
            eval.TotalScore *= eval.PerformanceMultiplier;
            eval.TotalScore = Mathf.Max(0f, eval.TotalScore);

            return eval;
        }

        /// <summary>
        /// Evaluates all candidates and returns the best one for vice host.
        /// </summary>
        /// <param name="candidates">Dictionary of authority ID to (metrics, capabilities, avgRttMs, isStale)</param>
        /// <param name="currentViceHostId">Current vice host ID (for hysteresis)</param>
        /// <param name="currentViceHostScore">Current vice host score (for hysteresis)</param>
        /// <param name="bestEvaluation">Output: best candidate's evaluation</param>
        /// <returns>Authority ID of best candidate, or 0 if none eligible</returns>
        public static ushort EvaluateBestViceHost(
            IEnumerable<(ushort authorityId, GONetNodeMetrics metrics, GONetNodeCapabilities capabilities, float avgRttMs, bool isStale)> candidates,
            ushort currentViceHostId,
            float currentViceHostScore,
            out CandidateEvaluation bestEvaluation)
        {
            bestEvaluation = default;
            ushort bestCandidateId = 0;
            float bestScore = float.MinValue;
            GONetNodeMetrics bestMetrics = default;

            foreach (var (authorityId, metrics, capabilities, avgRttMs, isStale) in candidates)
            {
                var eval = EvaluateCandidate(authorityId, metrics, capabilities, avgRttMs, isStale);

                if (!eval.IsEligible)
                    continue;

                // Check if this candidate is better than current best
                bool isBetter = false;
                if (eval.TotalScore > bestScore + SCORE_EPSILON)
                {
                    isBetter = true;
                }
                else if (Math.Abs(eval.TotalScore - bestScore) <= SCORE_EPSILON)
                {
                    // Tiebreaker needed
                    if (bestCandidateId == 0)
                    {
                        isBetter = true;
                    }
                    else
                    {
                        int comparison = DeterministicCompare(authorityId, bestCandidateId, metrics, bestMetrics);
                        isBetter = comparison < 0; // Lower comparison = better
                    }
                }

                if (isBetter)
                {
                    bestScore = eval.TotalScore;
                    bestCandidateId = authorityId;
                    bestMetrics = metrics;
                    bestEvaluation = eval;
                }
            }

            // Apply hysteresis: don't change vice host unless new candidate exceeds threshold
            if (currentViceHostId != 0 && bestCandidateId != currentViceHostId)
            {
                if (bestScore <= currentViceHostScore + VICE_HOST_CHANGE_THRESHOLD)
                {
                    // Keep current vice host due to hysteresis
                    return currentViceHostId;
                }
            }

            return bestCandidateId;
        }

        /// <summary>
        /// Deterministic comparison for tiebreaking when scores are equal.
        /// Returns negative if a should be preferred, positive if b should be preferred.
        /// </summary>
        /// <param name="authorityIdA">First candidate authority ID</param>
        /// <param name="authorityIdB">Second candidate authority ID</param>
        /// <param name="metricsA">First candidate metrics</param>
        /// <param name="metricsB">Second candidate metrics</param>
        /// <returns>Negative if A preferred, positive if B preferred, 0 if equal</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DeterministicCompare(ushort authorityIdA, ushort authorityIdB, GONetNodeMetrics metricsA, GONetNodeMetrics metricsB)
        {
            // Tiebreaker 1: Higher NAT score wins
            int natComparison = metricsB.NATCompatibilityScore.CompareTo(metricsA.NATCompatibilityScore);
            if (natComparison != 0)
                return natComparison;

            // Tiebreaker 2: Higher uptime wins
            int uptimeComparison = metricsB.Uptime_Minutes.CompareTo(metricsA.Uptime_Minutes);
            if (uptimeComparison != 0)
                return uptimeComparison;

            // Tiebreaker 3: Lower authority ID wins (final, always deterministic)
            return authorityIdA.CompareTo(authorityIdB);
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Calculates the average RTT for a candidate from the RTT matrix.
        /// Falls back to RTT-to-host with penalty if mesh RTT data is incomplete.
        /// </summary>
        /// <param name="candidateAuthorityId">Candidate to calculate average RTT for</param>
        /// <param name="rttMatrix">RTT matrix: (source, dest) -> RTT in ms</param>
        /// <param name="fallbackRttToHostMs">Fallback RTT (to host) if mesh data unavailable</param>
        /// <param name="totalPeerCount">Total number of peers in mesh</param>
        /// <returns>Average RTT in milliseconds</returns>
        public static float CalculateAverageRTTForCandidate(
            ushort candidateAuthorityId,
            Dictionary<(ushort src, ushort dst), ushort> rttMatrix,
            float fallbackRttToHostMs,
            int totalPeerCount)
        {
            if (rttMatrix == null || rttMatrix.Count == 0)
            {
                // No mesh RTT data - use fallback with penalty
                return fallbackRttToHostMs * UNKNOWN_PEER_RTT_PENALTY;
            }

            float sum = 0f;
            int count = 0;

            foreach (var kvp in rttMatrix)
            {
                // Include RTTs where candidate is either source or destination
                if (kvp.Key.src == candidateAuthorityId || kvp.Key.dst == candidateAuthorityId)
                {
                    sum += kvp.Value;
                    count++;
                }
            }

            if (count == 0)
            {
                // No RTT data for this candidate
                return fallbackRttToHostMs * UNKNOWN_PEER_RTT_PENALTY;
            }

            float avgKnown = sum / count;

            // If we have less than half the expected peer RTTs, apply partial penalty
            int expectedRttCount = (totalPeerCount - 1) * 2; // Both directions
            if (count < expectedRttCount / 2)
            {
                float missingRatio = 1f - ((float)count / expectedRttCount);
                float missingPenalty = missingRatio * 50f; // Up to 50ms penalty for missing data
                avgKnown += missingPenalty;
            }

            return avgKnown;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetPerformancePenaltyMultiplier(GONetNodeMetrics metrics)
        {
            float multiplier = 1f;

            // Only apply CPU penalty if we have a valid, non-zero value
            // CPU=0% often indicates unreliable data (loading, spikes) rather than actual poor performance
            if ((metrics.ValidityFlags & MetricsValidityFlags.CPUHeadroomValid) != 0 &&
                metrics.CPU_Headroom_Percent > 0)
            {
                if (metrics.CPU_Headroom_Percent <= CRITICAL_CPU_HEADROOM_THRESHOLD_PERCENT)
                {
                    multiplier = CRITICAL_PERFORMANCE_MULTIPLIER;
                }
                else if (metrics.CPU_Headroom_Percent <= LOW_CPU_HEADROOM_THRESHOLD_PERCENT)
                {
                    multiplier = LOW_PERFORMANCE_MULTIPLIER;
                }
            }

            // Only apply frame time penalty if we have a valid, non-zero value
            // Frame=0ms often indicates unreliable data rather than actual poor performance
            if (metrics.FrameTime_Headroom_Ms != GONetNodeMetrics.UNKNOWN_BYTE &&
                metrics.FrameTime_Headroom_Ms > 0)
            {
                if (metrics.FrameTime_Headroom_Ms <= CRITICAL_FRAME_HEADROOM_MS)
                {
                    multiplier = Math.Min(multiplier, CRITICAL_PERFORMANCE_MULTIPLIER);
                }
                else if (metrics.FrameTime_Headroom_Ms <= LOW_FRAME_HEADROOM_MS)
                {
                    multiplier = Math.Min(multiplier, LOW_PERFORMANCE_MULTIPLIER);
                }
            }

            return multiplier;
        }

        /// <summary>
        /// Gets a debug string showing score breakdown for a candidate.
        /// </summary>
        public static string GetScoreBreakdown(CandidateEvaluation eval)
        {
            if (!eval.IsEligible)
            {
                return $"Authority {eval.AuthorityId}: INELIGIBLE";
            }

            string perfNote = eval.PerformanceMultiplier < 0.999f
                ? $" PerfMul={eval.PerformanceMultiplier:F2}"
                : string.Empty;

            return $"Authority {eval.AuthorityId}: Total={eval.TotalScore:F1} " +
                   $"(Net={eval.NetworkScore:F0}, HW={eval.HardwareScore:F0}, " +
                   $"Stab={eval.StabilityScore:F0}, NAT={eval.NATScore:F0}){perfNote}";
        }

        /// <summary>
        /// Computes how much better a candidate is compared to the host.
        /// Returns a ratio where 0.25 means 25% better.
        /// </summary>
        public static float ComputeScoreDifferencePercent(float hostScore, float candidateScore)
        {
            const float EPSILON = 1f;
            float safeDenominator = Mathf.Max(hostScore, EPSILON);
            return (candidateScore / safeDenominator) - 1f;
        }

        #endregion
    }
}
