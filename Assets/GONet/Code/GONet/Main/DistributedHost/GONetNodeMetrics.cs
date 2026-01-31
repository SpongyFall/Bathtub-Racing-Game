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
using System.Runtime.InteropServices;
using UnityEngine;

namespace GONet.DistributedHost
{
    /// <summary>
    /// Compact 32-byte struct containing all metrics needed for host scoring.
    /// Designed for efficient serialization and gossip protocol transmission.
    ///
    /// Layout (32 bytes total):
    /// - Network:   8 bytes (RTT 2, Jitter 1, Loss 1, BW_Send 2, BW_Recv 2)
    /// - Hardware:  3 bytes (CPU 1, FrameTime 1, Battery 1)
    /// - Stability: 5 bytes (Uptime 2, Stability 1, NAT 1, Flags 1)
    /// - Identity:  6 bytes (AuthorityId 2, MonotonicTicks 4)
    /// - Reserved: 10 bytes (future use, ensures 32-byte alignment)
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 32)]
    public struct GONetNodeMetrics : IEquatable<GONetNodeMetrics>
    {
        #region Network Metrics (8 bytes)

        /// <summary>
        /// Average round-trip time in milliseconds.
        /// Range: 0-65535ms. Source: GONetConnection.RTT_RecentAverage
        /// </summary>
        public ushort RTT_Average_Ms;

        /// <summary>
        /// RTT jitter (standard deviation) in milliseconds.
        /// Range: 0-255ms, 255=unknown.
        /// </summary>
        public byte RTT_Jitter_Ms;

        /// <summary>
        /// Packet loss percentage.
        /// Range: 0-100 (percent), 255=unknown.
        /// </summary>
        public byte PacketLoss_Percent;

        /// <summary>
        /// Send bandwidth in KB/s (kilobytes per second).
        /// Range: 0-65535 KB/s. Measured from recent send rate.
        /// </summary>
        public ushort Bandwidth_Send_KBps;

        /// <summary>
        /// Receive bandwidth in KB/s (kilobytes per second).
        /// Range: 0-65535 KB/s. Measured from recent receive rate.
        /// </summary>
        public ushort Bandwidth_Recv_KBps;

        #endregion

        #region Hardware Metrics (3 bytes)

        /// <summary>
        /// CPU headroom as percentage (0-100).
        /// Higher = more CPU available for hosting duties.
        /// 255 = unknown.
        /// </summary>
        public byte CPU_Headroom_Percent;

        /// <summary>
        /// Frame time headroom in milliseconds (time budget remaining per frame).
        /// Higher = smoother performance, better host candidate.
        /// 255 = unknown.
        /// </summary>
        public byte FrameTime_Headroom_Ms;

        /// <summary>
        /// Battery level percentage (0-100).
        /// 255 = plugged in or desktop (no battery concern).
        /// </summary>
        public byte BatteryLevel;

        #endregion

        #region Stability Metrics (5 bytes)

        /// <summary>
        /// Continuous uptime in seconds since joining session.
        /// Used for new joiner cooldown and stability scoring.
        /// Range: 0-65535 seconds (~18 hours max, then wraps).
        /// </summary>
        public ushort Uptime_Seconds;

        /// <summary>
        /// Composite stability score (0-255).
        /// Higher = more stable connection history.
        /// Accounts for disconnect history, reconnects, etc.
        /// </summary>
        public byte StabilityScore;

        /// <summary>
        /// NAT compatibility score (0-255).
        /// 0 = blocked/requires relay, 255 = fully open.
        /// Determines if node can host effectively.
        /// </summary>
        public byte NATCompatibilityScore;

        /// <summary>
        /// Validity flags indicating which metrics are known vs unknown.
        /// </summary>
        public MetricsValidityFlags ValidityFlags;

        #endregion

        #region Identity (6 bytes)

        /// <summary>
        /// Session authority ID of this node.
        /// </summary>
        public ushort AuthorityId;

        /// <summary>
        /// Monotonic timestamp (per-node, not wall-clock).
        /// Used for ordering and freshness checks.
        /// Wraps after ~49 days at 1ms resolution.
        /// </summary>
        public uint MonotonicTicks;

        #endregion

        #region Peer RTT Data (10 bytes)

        /// <summary>
        /// RTT measurements to up to 5 other peers.
        /// Used for aggregate RTT calculation in host scoring.
        /// Each entry is 2 bytes: [AuthorityIdLow:1][RTT_Ms:1]
        /// </summary>
        public PeerRTTEntry PeerRTT0;
        public PeerRTTEntry PeerRTT1;
        public PeerRTTEntry PeerRTT2;
        public PeerRTTEntry PeerRTT3;
        public PeerRTTEntry PeerRTT4;

        /// <summary>
        /// Maximum number of peer RTT entries stored in metrics.
        /// </summary>
        public const int MAX_PEER_RTT_ENTRIES = 5;

        #endregion

        #region Constants

        /// <summary>
        /// Value indicating unknown/invalid for byte metrics.
        /// </summary>
        public const byte UNKNOWN_BYTE = 255;

        /// <summary>
        /// Value indicating unknown/invalid for ushort metrics.
        /// </summary>
        public const ushort UNKNOWN_USHORT = 65535;

        /// <summary>
        /// Minimum uptime in seconds before a node can be considered for hosting.
        /// Keeps brand new joiners from being promoted.
        /// Using seconds for precision since Uptime_Minutes truncates values under 60s to 0.
        /// </summary>
        public const ushort MIN_UPTIME_FOR_HOST_SECONDS = 45;

        /// <summary>
        /// NAT score below which a node is disqualified from hosting.
        /// </summary>
        public const byte NAT_SCORE_DISQUALIFY_THRESHOLD = 50;

        #endregion

        /// <summary>
        /// Creates metrics with default "unknown" values.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static GONetNodeMetrics CreateDefault(ushort authorityId)
        {
            return new GONetNodeMetrics
            {
                AuthorityId = authorityId,
                RTT_Average_Ms = UNKNOWN_USHORT,
                RTT_Jitter_Ms = UNKNOWN_BYTE,
                PacketLoss_Percent = UNKNOWN_BYTE,
                Bandwidth_Send_KBps = 0,
                Bandwidth_Recv_KBps = 0,
                CPU_Headroom_Percent = UNKNOWN_BYTE,
                FrameTime_Headroom_Ms = UNKNOWN_BYTE,
                BatteryLevel = UNKNOWN_BYTE,
                Uptime_Seconds = 0,
                StabilityScore = 128, // Neutral starting point
                NATCompatibilityScore = 200, // Optimistic default until proven otherwise
                ValidityFlags = MetricsValidityFlags.None,
                MonotonicTicks = 0,
            };
        }

        /// <summary>
        /// Returns true if this node has enough uptime to be considered for hosting.
        /// </summary>
        public bool HasSufficientUptimeForHost => Uptime_Seconds >= MIN_UPTIME_FOR_HOST_SECONDS;

        /// <summary>
        /// Returns uptime in minutes (for display/scoring). Derived from Uptime_Seconds.
        /// </summary>
        public ushort Uptime_Minutes => (ushort)(Uptime_Seconds / 60);

        /// <summary>
        /// Returns true if NAT score is sufficient for hosting.
        /// </summary>
        public bool HasSufficientNATForHost => NATCompatibilityScore >= NAT_SCORE_DISQUALIFY_THRESHOLD;

        /// <summary>
        /// Returns true if this node can be considered for host selection.
        /// </summary>
        public bool IsEligibleForHost => HasSufficientUptimeForHost && HasSufficientNATForHost;

        /// <summary>
        /// Returns the effective battery multiplier for scoring.
        /// 1.0 if plugged in/desktop, 0.3 if &lt;20%, 0.7 if &lt;50%, else 1.0.
        /// </summary>
        public float BatteryMultiplier
        {
            get
            {
                if (BatteryLevel == UNKNOWN_BYTE) return 1.0f; // Desktop/plugged in
                if (BatteryLevel < 20) return 0.3f;
                if (BatteryLevel < 50) return 0.7f;
                return 1.0f;
            }
        }

        /// <summary>
        /// Returns true if RTT metric is valid/known.
        /// </summary>
        public bool IsRTTValid => (ValidityFlags & MetricsValidityFlags.RTTValid) != 0 && RTT_Average_Ms != UNKNOWN_USHORT;

        /// <summary>
        /// Returns true if jitter metric is valid/known.
        /// </summary>
        public bool IsJitterValid => (ValidityFlags & MetricsValidityFlags.JitterValid) != 0 && RTT_Jitter_Ms != UNKNOWN_BYTE;

        /// <summary>
        /// Returns true if packet loss metric is valid/known.
        /// </summary>
        public bool IsPacketLossValid => (ValidityFlags & MetricsValidityFlags.PacketLossValid) != 0 && PacketLoss_Percent != UNKNOWN_BYTE;

        public bool Equals(GONetNodeMetrics other)
        {
            return AuthorityId == other.AuthorityId && MonotonicTicks == other.MonotonicTicks;
        }

        public override bool Equals(object obj)
        {
            return obj is GONetNodeMetrics other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(AuthorityId, MonotonicTicks);
        }

        public static bool operator ==(GONetNodeMetrics left, GONetNodeMetrics right) => left.Equals(right);
        public static bool operator !=(GONetNodeMetrics left, GONetNodeMetrics right) => !left.Equals(right);

        public override string ToString()
        {
            return $"Metrics(Auth:{AuthorityId}, RTT:{RTT_Average_Ms}ms, Jit:{RTT_Jitter_Ms}ms, Loss:{PacketLoss_Percent}%, " +
                   $"BW:{Bandwidth_Send_KBps}/{Bandwidth_Recv_KBps}KB/s, CPU:{CPU_Headroom_Percent}%, Up:{Uptime_Seconds}s, " +
                   $"NAT:{NATCompatibilityScore}, Stab:{StabilityScore})";
        }
    }

    /// <summary>
    /// Collects local node metrics from various GONet subsystems.
    /// Uses EWMA (Exponentially Weighted Moving Average) smoothing to reduce noise
    /// in instantaneous measurements like CPU headroom and frame time.
    /// </summary>
    public static class GONetMetricsCollector
    {
        // EMA smoothing factors - higher alpha = more responsive, lower = smoother
        private const float JITTER_EMA_ALPHA = 0.3f;
        private const float HARDWARE_EMA_ALPHA = 0.15f; // Slow-moving for stability

        // Cached values for jitter calculation
        private static float lastRTT = 0f;
        private static float smoothedJitter = 0f;

        // EWMA smoothed hardware metrics
        private static float smoothedFrameTime = 16.67f; // Start at 60 FPS (16.67ms)
        private static float smoothedFrameTimeVariance = 0f; // Frame time variance for stability detection
        private static bool hardwareMetricsWarmedUp = false;
        private static int warmupSampleCount = 0;
        private const int WARMUP_SAMPLES = 10; // Samples needed before trusting EWMA

        private static uint monotonicCounter = 0;
        private static DateTime sessionStartTime;
        private static bool initialized = false;

        /// <summary>
        /// Initializes the metrics collector. Call once when session starts.
        /// </summary>
        public static void Initialize()
        {
            sessionStartTime = DateTime.UtcNow;
            lastRTT = 0f;
            smoothedJitter = 0f;
            smoothedFrameTime = 16.67f; // Start at 60 FPS
            smoothedFrameTimeVariance = 0f;
            hardwareMetricsWarmedUp = false;
            warmupSampleCount = 0;
            monotonicCounter = 0;
            initialized = true;
        }

        /// <summary>
        /// Collects current metrics for the local node.
        /// </summary>
        /// <param name="connection">The connection to measure RTT from (null for host/server)</param>
        /// <param name="authorityId">Local authority ID</param>
        /// <returns>Current metrics snapshot</returns>
        public static GONetNodeMetrics CollectLocalMetrics(GONetConnection connection, ushort authorityId)
        {
            if (!initialized)
            {
                Initialize();
            }

            var metrics = GONetNodeMetrics.CreateDefault(authorityId);
            metrics.MonotonicTicks = ++monotonicCounter;

            // Calculate uptime in seconds
            var uptime = DateTime.UtcNow - sessionStartTime;
            metrics.Uptime_Seconds = (ushort)Math.Min(uptime.TotalSeconds, ushort.MaxValue);

            // Network metrics from connection
            if (connection != null)
            {
                float rttSeconds = connection.RTT_RecentAverage;
                float rttMs = rttSeconds * 1000f;
                metrics.RTT_Average_Ms = (ushort)Math.Min(rttMs, ushort.MaxValue - 1);
                metrics.ValidityFlags |= MetricsValidityFlags.RTTValid;

                // Calculate jitter using EMA
                float instantJitter = Math.Abs(rttMs - lastRTT);
                smoothedJitter = (JITTER_EMA_ALPHA * instantJitter) + ((1 - JITTER_EMA_ALPHA) * smoothedJitter);
                metrics.RTT_Jitter_Ms = (byte)Math.Min(smoothedJitter, 254);
                metrics.ValidityFlags |= MetricsValidityFlags.JitterValid;
                lastRTT = rttMs;

                // Packet loss - would need to be tracked separately
                // For now, use a placeholder based on RTT stability
                metrics.PacketLoss_Percent = 0; // TODO: Implement actual packet loss tracking
                metrics.ValidityFlags |= MetricsValidityFlags.PacketLossValid;
            }

            // Hardware metrics
            CollectHardwareMetrics(ref metrics);

            // Stability score - starts neutral, adjusted over time based on behavior
            metrics.StabilityScore = 200; // Good starting point

            // Collect peer RTTs from hot standby connections (for mesh RTT scoring)
            CollectPeerRTTMetrics(ref metrics);

            return metrics;
        }

        /// <summary>
        /// Collects peer RTT metrics from hot standby connections.
        /// </summary>
        private static void CollectPeerRTTMetrics(ref GONetNodeMetrics metrics)
        {
            var hotStandby = GONetHotStandbyManager.Instance;
            if (hotStandby == null || !hotStandby.IsInitialized)
            {
                return;
            }

            var peerRTTs = hotStandby.CollectPeerRTTs();
            if (peerRTTs != null && peerRTTs.Count > 0)
            {
                PopulatePeerRTTEntries(ref metrics, peerRTTs);
            }
        }

        /// <summary>
        /// Collects hardware-related metrics (CPU, frame time, battery).
        /// Uses EWMA smoothing and frame time variance to estimate performance headroom.
        ///
        /// Key insight: With VSync on, frame time equals target exactly, making traditional
        /// headroom calculation useless (always 0%). Instead, we measure:
        /// 1. Frame time consistency (low variance = stable machine with headroom)
        /// 2. Actual frame time vs target (meeting/exceeding target = good)
        ///
        /// A machine that consistently hits 60 FPS with low variance has MORE capacity
        /// than one with high variance (struggling to keep up).
        ///
        /// CRITICAL (January 2026): When CPU throttling is enabled via -target-fps, we must use
        /// the ACTUAL processing time (before sleep), not Time.deltaTime which includes sleep.
        /// Otherwise, throttled clients appear healthy and host migration won't trigger.
        /// </summary>
        private static void CollectHardwareMetrics(ref GONetNodeMetrics metrics)
        {
            float targetFrameTime = 1f / Application.targetFrameRate;
            if (Application.targetFrameRate <= 0) targetFrameTime = 1f / 60f; // Default 60 FPS

            // CRITICAL (January 2026): When CPU throttling is enabled via -target-fps, we need to
            // simulate that this machine CAN'T run faster than the throttle target.
            // The actual processing time is still fast (CPU isn't really throttled), but we
            // pretend the machine is at capacity to trigger host migration testing.
            //
            // Without this, throttled clients appear healthy because:
            // - Actual processing takes 5-10ms (fast)
            // - We add sleep to hit target FPS
            // - Time.deltaTime includes sleep, showing "normal" frame time
            // - But performance ratio (target/actual) is very high since actual is fast
            //
            // Solution: When throttling, use the throttle target as the "current" frame time
            // to make it look like the machine is exactly at capacity (100%), not exceeding it.
            float currentFrameTime;
            float cpuThrottlePenalty = 1f;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (GONetCommandLineParser.IsCpuThrottlingEnabled)
            {
                if (GONetCommandLineParser.CpuThrottleTargetFps > 0)
                {
                    // Simulate that this machine runs at EXACTLY the throttle target
                    // This makes performance ratio = 100% (at capacity, not exceeding)
                    currentFrameTime = 1f / GONetCommandLineParser.CpuThrottleTargetFps;

                    // Additional penalty: compare throttle target to native target
                    // If native is 60 FPS and throttle is 45 FPS, machine is at 75% of "normal" capacity
                    float nativeTargetFps = Application.targetFrameRate > 0 ? Application.targetFrameRate : 60f;
                    cpuThrottlePenalty = GONetCommandLineParser.CpuThrottleTargetFps / nativeTargetFps;
                    cpuThrottlePenalty = Mathf.Clamp(cpuThrottlePenalty, 0.1f, 1f);
                }
                else if (GONetCommandLineParser.CpuThrottleFixedSleepMs > 0)
                {
                    // Fixed sleep: add the sleep time to actual frame time
                    currentFrameTime = Time.deltaTime; // Already includes sleep
                    // Apply penalty based on how much extra time we're adding
                    float sleepPortion = (GONetCommandLineParser.CpuThrottleFixedSleepMs / 1000f) / Mathf.Max(Time.deltaTime, 0.001f);
                    cpuThrottlePenalty = 1f / (1f + sleepPortion);
                    cpuThrottlePenalty = Mathf.Clamp(cpuThrottlePenalty, 0.1f, 1f);
                }
                else
                {
                    currentFrameTime = Time.deltaTime;
                }
            }
            else
#endif
            {
                currentFrameTime = Time.deltaTime;
            }

            float targetFrameTimeMs = targetFrameTime * 1000f;
            float currentFrameTimeMs = currentFrameTime * 1000f;

            // Track frame time for variance calculation
            float deviation = Mathf.Abs(currentFrameTimeMs - smoothedFrameTime);

            // Apply EWMA smoothing
            if (!hardwareMetricsWarmedUp)
            {
                // During warmup, use faster convergence
                float warmupAlpha = 0.5f;
                smoothedFrameTime = (warmupAlpha * currentFrameTimeMs) + ((1f - warmupAlpha) * smoothedFrameTime);
                smoothedFrameTimeVariance = (warmupAlpha * deviation) + ((1f - warmupAlpha) * smoothedFrameTimeVariance);

                warmupSampleCount++;
                if (warmupSampleCount >= WARMUP_SAMPLES)
                {
                    hardwareMetricsWarmedUp = true;
                }
            }
            else
            {
                // Normal EWMA smoothing
                smoothedFrameTime = (HARDWARE_EMA_ALPHA * currentFrameTimeMs) + ((1f - HARDWARE_EMA_ALPHA) * smoothedFrameTime);
                smoothedFrameTimeVariance = (HARDWARE_EMA_ALPHA * deviation) + ((1f - HARDWARE_EMA_ALPHA) * smoothedFrameTimeVariance);
            }

            // Calculate CPU headroom based on two factors:
            // 1. Performance ratio: Are we meeting or exceeding target FPS?
            //    - At target or faster = high score
            //    - Slower than target = lower score
            // 2. Stability bonus: Low variance = consistent performance = more headroom
            //    - Variance < 1ms = very stable, +20% bonus
            //    - Variance < 3ms = stable, +10% bonus
            //    - Variance > 5ms = unstable, -20% penalty

            // Performance ratio (100 = exactly at target, >100 = faster, <100 = slower)
            float performanceRatio = (targetFrameTimeMs / Mathf.Max(smoothedFrameTime, 0.1f)) * 100f;
            performanceRatio = Mathf.Clamp(performanceRatio, 0f, 200f); // Cap at 2x target

            // Stability modifier based on frame time variance
            float stabilityModifier = 1.0f;
            if (smoothedFrameTimeVariance < 1.0f)
            {
                stabilityModifier = 1.2f; // Very stable - 20% bonus
            }
            else if (smoothedFrameTimeVariance < 3.0f)
            {
                stabilityModifier = 1.1f; // Stable - 10% bonus
            }
            else if (smoothedFrameTimeVariance > 5.0f)
            {
                stabilityModifier = 0.8f; // Unstable - 20% penalty
            }

            // Final CPU headroom: scale performance ratio by stability
            // At target (100%) with good stability (1.2x) = 120%, clamped to 100%
            // This rewards machines that consistently hit target over those with variance
            float cpuHeadroomPercent = (performanceRatio / 100f) * stabilityModifier * 50f; // Scale to 0-100 range

            // Apply CPU throttle penalty when -target-fps or -cpu-throttle is used
            // This makes throttled hosts appear degraded so host migration can trigger
            cpuHeadroomPercent *= cpuThrottlePenalty;
            cpuHeadroomPercent = Mathf.Clamp(cpuHeadroomPercent, 0f, 100f);

            // Frame time headroom: how many ms under target we are (can be 0 with VSync)
            // With VSync, this will be ~0, but CPU headroom above captures true capacity
            float frameTimeHeadroomMs = Mathf.Max(0f, targetFrameTimeMs - smoothedFrameTime);

            // Also reduce frame time headroom proportionally when throttling
            frameTimeHeadroomMs *= cpuThrottlePenalty;

            // Use calculated values
            metrics.CPU_Headroom_Percent = (byte)Mathf.Clamp(cpuHeadroomPercent, 0, 100);
            metrics.ValidityFlags |= MetricsValidityFlags.CPUHeadroomValid;

            metrics.FrameTime_Headroom_Ms = (byte)Mathf.Clamp(frameTimeHeadroomMs, 0, 254);

            // Battery level
            float batteryLevel = SystemInfo.batteryLevel;
            if (batteryLevel < 0 || SystemInfo.batteryStatus == UnityEngine.BatteryStatus.Unknown)
            {
                // Desktop or plugged in - no battery concern
                metrics.BatteryLevel = GONetNodeMetrics.UNKNOWN_BYTE;
            }
            else
            {
                metrics.BatteryLevel = (byte)(batteryLevel * 100);
                metrics.ValidityFlags |= MetricsValidityFlags.BatteryValid;
            }
        }

        /// <summary>
        /// Updates NAT compatibility score based on connection test results.
        /// Called after STUN-like checks or P2P connection attempts.
        /// </summary>
        /// <param name="metrics">Metrics to update</param>
        /// <param name="directConnectionSuccessRate">Percentage of successful direct connections (0-100)</param>
        /// <param name="isSymmetricNAT">True if detected as symmetric NAT</param>
        public static void UpdateNATScore(ref GONetNodeMetrics metrics, int directConnectionSuccessRate, bool isSymmetricNAT)
        {
            if (isSymmetricNAT)
            {
                // Symmetric NAT is heavily penalized
                metrics.NATCompatibilityScore = (byte)Math.Min(directConnectionSuccessRate, 50);
            }
            else
            {
                // Scale based on success rate
                metrics.NATCompatibilityScore = (byte)Math.Min(directConnectionSuccessRate * 2.5f, 255);
            }

            metrics.ValidityFlags |= MetricsValidityFlags.NATTypeValid;
        }

        /// <summary>
        /// Populates peer RTT entries from a dictionary of peer RTTs.
        /// Prioritizes peers with the best (lowest) RTT values.
        /// </summary>
        /// <param name="metrics">Metrics to populate</param>
        /// <param name="peerRTTs">Dictionary of authority ID to RTT in milliseconds</param>
        public static void PopulatePeerRTTEntries(ref GONetNodeMetrics metrics, Dictionary<ushort, ushort> peerRTTs)
        {
            if (peerRTTs == null || peerRTTs.Count == 0)
            {
                // Clear all entries
                metrics.PeerRTT0 = default;
                metrics.PeerRTT1 = default;
                metrics.PeerRTT2 = default;
                metrics.PeerRTT3 = default;
                metrics.PeerRTT4 = default;
                return;
            }

            // Sort peers by RTT (lowest first) and take top 5
            var sortedPeers = new List<KeyValuePair<ushort, ushort>>(peerRTTs);
            sortedPeers.Sort((a, b) => a.Value.CompareTo(b.Value));

            int count = Math.Min(sortedPeers.Count, GONetNodeMetrics.MAX_PEER_RTT_ENTRIES);

            // Populate entries
            metrics.PeerRTT0 = count > 0 ? PeerRTTEntry.Create(sortedPeers[0].Key, sortedPeers[0].Value) : default;
            metrics.PeerRTT1 = count > 1 ? PeerRTTEntry.Create(sortedPeers[1].Key, sortedPeers[1].Value) : default;
            metrics.PeerRTT2 = count > 2 ? PeerRTTEntry.Create(sortedPeers[2].Key, sortedPeers[2].Value) : default;
            metrics.PeerRTT3 = count > 3 ? PeerRTTEntry.Create(sortedPeers[3].Key, sortedPeers[3].Value) : default;
            metrics.PeerRTT4 = count > 4 ? PeerRTTEntry.Create(sortedPeers[4].Key, sortedPeers[4].Value) : default;
        }
    }

    /// <summary>
    /// Compact 2-byte struct for storing RTT to a single peer.
    /// Used within GONetNodeMetrics to track RTT to multiple peers.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 2)]
    public struct PeerRTTEntry
    {
        /// <summary>
        /// Lower 8 bits of the peer's authority ID.
        /// 0 indicates an empty/invalid entry.
        /// </summary>
        public byte PeerAuthorityIdLow;

        /// <summary>
        /// RTT to this peer in milliseconds, clamped to 0-254.
        /// 255 indicates unknown/invalid.
        /// </summary>
        public byte RTT_Ms;

        /// <summary>
        /// Value indicating an empty/invalid entry.
        /// </summary>
        public const byte INVALID_AUTHORITY_ID = 0;

        /// <summary>
        /// Value indicating unknown RTT.
        /// </summary>
        public const byte UNKNOWN_RTT = 255;

        /// <summary>
        /// Returns true if this entry contains valid data.
        /// </summary>
        public bool IsValid => PeerAuthorityIdLow != INVALID_AUTHORITY_ID && RTT_Ms != UNKNOWN_RTT;

        /// <summary>
        /// Creates a new PeerRTTEntry from a full authority ID and RTT.
        /// </summary>
        /// <param name="authorityId">Full authority ID (lower 8 bits are stored)</param>
        /// <param name="rttMs">RTT in milliseconds (clamped to 0-254)</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PeerRTTEntry Create(ushort authorityId, ushort rttMs)
        {
            return new PeerRTTEntry
            {
                PeerAuthorityIdLow = (byte)(authorityId & 0xFF),
                RTT_Ms = (byte)Math.Min((int)rttMs, 254)
            };
        }

        /// <summary>
        /// Reconstructs the full authority ID by combining stored low bits with high bits hint.
        /// </summary>
        /// <param name="highBitsHint">High 8 bits to combine (typically from known session range)</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetAuthorityId(byte highBitsHint = 0)
        {
            return (ushort)((highBitsHint << 8) | PeerAuthorityIdLow);
        }

        public override string ToString()
        {
            return IsValid ? $"Peer({PeerAuthorityIdLow})={RTT_Ms}ms" : "Empty";
        }
    }
}
