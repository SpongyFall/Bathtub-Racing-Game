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
 * -The ability to commercialize products built on modified source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using System;
using UnityEngine;

namespace GONet.Utils
{
    /// <summary>
    /// Per-value adaptive buffer lead time tracking using reactive IPI (Inter-Packet Interval) measurement.
    ///
    /// PURPOSE:
    /// When backpressure trickle mode slows updates to 2Hz (500ms intervals), a fixed 100ms buffer
    /// causes stutter: client plays through buffer in 0.1s, then waits 0.4s in silence.
    ///
    /// ALGORITHM:
    /// - Measure actual inter-packet interval via EWMA (Exponential Weighted Moving Average)
    /// - Target buffer = interval * 1.5 (need margin for jitter)
    /// - Asymmetric adaptation: fast expand (0.4 alpha) when slowing, slow shrink (0.1 alpha) when recovering
    /// - Clamp to configured min/max bounds
    ///
    /// USAGE:
    /// Call RecordArrival() when each sync value arrives.
    /// Call GetAdaptedLeadTimeTicks() when applying blended values.
    /// </summary>
    internal struct AdaptiveBufferState
    {
        /// <summary>
        /// High-resolution timestamp of last value arrival (ticks).
        /// Used to measure inter-packet interval.
        /// </summary>
        public long lastArrivalTicks;

        /// <summary>
        /// EWMA of inter-packet interval in milliseconds.
        /// Smoothed to prevent jitter from individual packet timing variations.
        /// </summary>
        public float ewmaIntervalMs;

        /// <summary>
        /// Current adapted buffer lead time in milliseconds.
        /// This is what GetAdaptedLeadTimeTicks() returns (converted to ticks).
        /// </summary>
        public float currentLeadTimeMs;

        /// <summary>
        /// Whether this state has been initialized (first arrival recorded).
        /// When false, GetAdaptedLeadTimeTicks() falls back to the global default.
        /// </summary>
        public bool isInitialized;

        /// <summary>
        /// Counter for warmup period. During warmup, we use fast adaptation to quickly
        /// find the true baseline interval, rather than slowly shrinking from an arbitrary default.
        /// </summary>
        private int warmupArrivalCount;

        /// <summary>
        /// Number of arrivals during warmup phase where we use expand alpha
        /// to quickly establish baseline interval. After this, we switch to
        /// asymmetric expand/shrink behavior.
        /// </summary>
        private const int WARMUP_ARRIVALS = 10;

        /// <summary>
        /// Minimum percentage change in interval (relative to EWMA) required to trigger
        /// expand/shrink adaptation. Small variations within this threshold are considered
        /// normal jitter and won't adjust the buffer, providing stable interpolation timing.
        /// 0.15 = 15% threshold (e.g., 42ms ± 6.3ms won't trigger changes)
        /// </summary>
        private const float STABILITY_THRESHOLD = 0.15f;

        /// <summary>
        /// Very slow smoothing alpha for buffer lead time changes AFTER warmup.
        /// This ensures stable interpolation timing - the EWMA can track interval changes,
        /// but the actual buffer used for blending changes very slowly unless there's
        /// a significant shift (triggered by STABILITY_THRESHOLD).
        /// </summary>
        private const float BUFFER_SMOOTHING_ALPHA = 0.02f;

        /// <summary>
        /// Record a value arrival and update the adaptive buffer state.
        /// Call this in AddToMostRecentChangeQueue_IfAppropriate() for each sync value.
        /// </summary>
        /// <param name="nowTicks">Current high-resolution timestamp (Time.ElapsedTicks or similar)</param>
        public void RecordArrival(long nowTicks)
        {
            if (!isInitialized)
            {
                // Bootstrap: Initialize with default buffer lead time assumption
                // The warmup phase will quickly converge to the true baseline
                ewmaIntervalMs = GONetGlobal.Instance?.valueBlendingBufferLeadTimeMilliseconds ?? 100f;
                currentLeadTimeMs = ewmaIntervalMs;
                lastArrivalTicks = nowTicks;
                isInitialized = true;
                warmupArrivalCount = 0;
                return;
            }

            // Measure actual interval since last arrival
            float intervalMs = (nowTicks - lastArrivalTicks) / (float)TimeSpan.TicksPerMillisecond;
            lastArrivalTicks = nowTicks;

            // Clamp to sane range (ignore outliers from pauses, disconnects, scene loads)
            // 10ms = 100Hz (faster than any sync rate)
            // 2000ms = 0.5Hz (slower than even extreme throttling)
            intervalMs = Mathf.Clamp(intervalMs, 10f, 2000f);

            // Track warmup arrivals
            bool isWarmup = warmupArrivalCount < WARMUP_ARRIVALS;
            if (isWarmup)
            {
                warmupArrivalCount++;
            }

            // Calculate relative change from current EWMA
            float relativeChange = Mathf.Abs(intervalMs - ewmaIntervalMs) / ewmaIntervalMs;

            // Determine adaptation alpha based on phase and change magnitude
            float ewmaAlpha;
            float bufferAlpha;

            if (isWarmup)
            {
                // WARMUP PHASE: Fast adaptation to quickly find true baseline interval
                // Use expand alpha regardless of direction - we're still learning the baseline
                ewmaAlpha = GONetGlobal.Instance?.adaptiveBufferExpandSpeed ?? 0.4f;
                bufferAlpha = ewmaAlpha; // Buffer tracks EWMA closely during warmup
            }
            else if (relativeChange < STABILITY_THRESHOLD)
            {
                // STABLE PHASE: Small jitter within threshold - minimal adaptation
                // Still update EWMA slowly to track gradual drift, but buffer stays stable
                ewmaAlpha = 0.05f; // Very slow EWMA tracking for baseline drift
                bufferAlpha = BUFFER_SMOOTHING_ALPHA; // Near-zero buffer change
            }
            else if (intervalMs > ewmaIntervalMs)
            {
                // EXPAND PHASE: Interval getting LONGER (update rate slowing) → fast adaptation
                // This is critical for backpressure response
                ewmaAlpha = GONetGlobal.Instance?.adaptiveBufferExpandSpeed ?? 0.4f;
                bufferAlpha = ewmaAlpha * 0.5f; // Buffer expands at half the EWMA rate for stability
            }
            else
            {
                // SHRINK PHASE: Interval getting SHORTER (update rate improving) → slow adaptation
                // Prevents premature shrinking before congestion truly clears
                ewmaAlpha = GONetGlobal.Instance?.adaptiveBufferShrinkSpeed ?? 0.1f;
                bufferAlpha = ewmaAlpha * 0.3f; // Buffer shrinks even slower than EWMA
            }

            // Update EWMA of interval
            ewmaIntervalMs = ewmaAlpha * intervalMs + (1f - ewmaAlpha) * ewmaIntervalMs;

            // Calculate target buffer: interval * 1.5 (need margin for jitter)
            float targetLeadTimeMs = ewmaIntervalMs * 1.5f;

            // Clamp to configured bounds
            int minMs = GONetGlobal.Instance?.adaptiveBufferMinLeadTimeMs ?? 100;
            int maxMs = GONetGlobal.Instance?.adaptiveBufferMaxLeadTimeMs ?? 750;
            targetLeadTimeMs = Mathf.Clamp(targetLeadTimeMs, minMs, maxMs);

            // Smooth transition to target (using calculated buffer alpha)
            currentLeadTimeMs = Mathf.Lerp(currentLeadTimeMs, targetLeadTimeMs, bufferAlpha);
        }

        /// <summary>
        /// Get the adapted buffer lead time in ticks.
        /// Use this instead of the fixed valueBlendingBufferLeadTicks when adaptive mode is enabled.
        /// </summary>
        /// <returns>Buffer lead time in high-resolution ticks</returns>
        public long GetAdaptedLeadTimeTicks()
        {
            return (long)(currentLeadTimeMs * TimeSpan.TicksPerMillisecond);
        }

        /// <summary>
        /// Get the adapted buffer lead time in ticks, capped to what the queue actually contains.
        /// This is the SMART version that adapts to what's actually available.
        /// </summary>
        /// <param name="queueTimeSpanTicks">Actual time span in queue (newest - oldest entry timestamps)</param>
        /// <returns>Buffer lead time in ticks, capped to available queue history</returns>
        public long GetAdaptedLeadTimeTicks(long queueTimeSpanTicks)
        {
            long idealLeadTicks = (long)(currentLeadTimeMs * TimeSpan.TicksPerMillisecond);

            // Cap to 80% of actual queue span to leave margin for interpolation
            // (we need at least 2 points on either side of target time)
            long maxSafeLeadTicks = (long)(queueTimeSpanTicks * 0.8f);

            // Use the minimum of ideal and what's actually available
            // But never go below the configured minimum
            int minMs = GONetGlobal.Instance?.adaptiveBufferMinLeadTimeMs ?? 100;
            long minLeadTicks = minMs * TimeSpan.TicksPerMillisecond;

            return Math.Max(minLeadTicks, Math.Min(idealLeadTicks, maxSafeLeadTicks));
        }

        /// <summary>
        /// Reset the adaptive buffer state.
        /// Call this when ownership changes, object respawns, or connection resets.
        /// </summary>
        public void Reset()
        {
            isInitialized = false;
            lastArrivalTicks = 0;
            ewmaIntervalMs = 0;
            currentLeadTimeMs = 0;
            warmupArrivalCount = 0;
        }
    }
}
