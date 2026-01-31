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

using GONet.Generation;
using GONet.Utils;
using ReliableNetcode;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

using GONetCodeGenerationId = System.Byte;
using GONetChannelId = System.Byte;
using System.IO;
using System.Runtime.Serialization;
using System.Net;
using System.Collections;
using System.Diagnostics;
using GONet.PluginAPI;
using System.Text;
using System.Runtime.InteropServices;

namespace GONet
{
    public static partial class GONetMain
    {
        #region Per-Client Congestion Control (Late-Joiner Backpressure System)

        /// <summary>
        /// Tracks congestion state for each connected client to prevent late-joiner initialization failures.
        ///
        /// PROBLEM SOLVED:
        /// - Early-joiners work: Connect when scene is quiet (no unreliable traffic competing with reliable init messages)
        /// - Late-joiners fail: Connect while 800 objects syncing (unreliable flood saturates OS socket, blocks reliable InitComplete)
        ///
        /// SOLUTION:
        /// - Monitor each client's reliable queue depth (from transport layer GetUsageStatistics())
        /// - When reliable queue > high watermark (500), SUPPRESS unreliable traffic to that client only
        /// - When reliable queue < low watermark (150), RESUME unreliable traffic
        /// - Hysteresis prevents oscillation (require N consecutive checks before state change)
        ///
        /// This allows late-joiners to complete initialization (InitComplete arrives) even with 800+ objects actively syncing.
        /// </summary>
        private class ClientCongestionState
        {
            /// <summary>
            /// Client's authority ID (for logging/diagnostics).
            /// </summary>
            public ushort authorityId;

            /// <summary>
            /// Current reliable message queue depth from transport layer.
            /// Extracted from GetUsageStatistics() "messageQueue.Count:" field.
            /// Updated once per frame (throttled to avoid excessive parsing).
            /// </summary>
            public int reliableQueueDepth;

            /// <summary>
            /// Whether unreliable traffic is currently suppressed for this client.
            /// TRUE = Client's reliable queue is backing up, drop all unreliable messages
            /// FALSE = Normal operation, all unreliable messages sent
            /// </summary>
            public bool isUnreliableSuppressed;

            /// <summary>
            /// Last time we checked this client's queue depth (high-resolution ticks).
            /// Throttled to once per frame (~16ms) to avoid excessive GetUsageStatistics() parsing overhead.
            /// </summary>
            public long lastCheckTicks;

            /// <summary>
            /// Number of consecutive checks where queue depth > high watermark.
            /// Must reach hysteresis threshold before suppressing unreliable (prevents flapping).
            /// </summary>
            public int consecutiveHighWatermarks;

            /// <summary>
            /// Number of consecutive checks where queue depth < low watermark.
            /// Must reach hysteresis threshold before resuming unreliable (prevents flapping).
            /// </summary>
            public int consecutiveLowWatermarks;

            /// <summary>
            /// Total unreliable messages dropped for this client due to backpressure (diagnostic counter).
            /// </summary>
            public long totalUnreliableDropped;

            /// <summary>
            /// Total unreliable messages allowed through during suppression (trickle mode).
            /// </summary>
            public long totalUnreliableTrickleSent;

            /// <summary>
            /// Last time an unreliable message was allowed through during suppression (ticks).
            /// -1 when never allowed.
            /// </summary>
            public long lastUnreliableTrickleTicks;

            /// <summary>
            /// High-resolution timestamp when unreliable suppression started (for duration tracking).
            /// -1 when not suppressed.
            /// </summary>
            public long suppressionStartTicks;

            #region ADAPTIVE TRICKLE RATE: Per-client adaptive trickle interval
            /// <summary>
            /// EMA-smoothed congestion severity (0.0 = at low watermark/recovered, 1.0 = at/above high watermark).
            /// Used to calculate adaptive trickle interval via Lerp between min/max bounds.
            /// Updated in UpdateClientCongestionState() when suppressed.
            /// </summary>
            public float smoothedCongestionSeverity;

            /// <summary>
            /// Current adaptive trickle interval in milliseconds.
            /// Calculated as: Lerp(minTrickleIntervalMs, maxTrickleIntervalMs, smoothedCongestionSeverity)
            /// -1 when adaptive trickle is disabled or client is not suppressed.
            /// </summary>
            public int currentAdaptiveTrickleIntervalMs;
            #endregion

            #region TRICKLE BATCH SIZE: Allow multiple packets per interval
            /// <summary>
            /// Number of packets/fragments sent so far in the current trickle interval.
            /// Reset to 0 when a new interval starts (lastUnreliableTrickleTicks updated).
            /// Allows complete sync bundles (all fragments) to get through per interval.
            /// </summary>
            public int currentTrickleBatchCount;
            #endregion
        }

        /// <summary>
        /// Map of client authorityId → congestion state.
        /// Created when client connects, removed when client disconnects.
        /// </summary>
        private static readonly Dictionary<ushort, ClientCongestionState> _clientCongestionStates = new Dictionary<ushort, ClientCongestionState>();

        /// <summary>
        /// Per-client backpressure telemetry (for FRAME-METRICS logging).
        /// Tracks total drops, suppression time, state changes.
        /// </summary>
        private static long _totalBackpressureDrops = 0;
        private static int _totalSuppressionStateChanges = 0;

        /// <summary>
        /// Get or create congestion state for a client. Called from SendBytesToRemoteConnection().
        /// </summary>
        private static ClientCongestionState GetOrCreateCongestionState(ushort authorityId)
        {
            if (!_clientCongestionStates.TryGetValue(authorityId, out ClientCongestionState state))
            {
                state = new ClientCongestionState
                {
                    authorityId = authorityId,
                    reliableQueueDepth = 0,
                    isUnreliableSuppressed = false,
                    lastCheckTicks = 0,
                    consecutiveHighWatermarks = 0,
                    consecutiveLowWatermarks = 0,
                    totalUnreliableDropped = 0,
                    totalUnreliableTrickleSent = 0,
                    lastUnreliableTrickleTicks = -1,
                    suppressionStartTicks = -1,
                    // ADAPTIVE TRICKLE: Start at ~0.33 which Lerps to ~200ms (matching original fixed interval)
                    // Lerp(50, 500, 0.33) ≈ 200ms
                    smoothedCongestionSeverity = 0.33f,
                    currentAdaptiveTrickleIntervalMs = -1,
                    // TRICKLE BATCH SIZE: Start at 0, incremented each packet sent within interval
                    currentTrickleBatchCount = 0
                };
                _clientCongestionStates[authorityId] = state;
            }
            return state;
        }

        /// <summary>
        /// Update client's congestion state by parsing GetUsageStatistics() and applying hysteresis-based state machine.
        /// Called once per frame per client (throttled) from SendBytesToRemoteConnection().
        /// </summary>
        private static void UpdateClientCongestionState(GONetConnection connection, ClientCongestionState state)
        {
            if (connection == null || GONetGlobal.Instance == null)
            {
                return;
            }

            // Parse reliable queue depth from transport statistics
            int queueDepth = ParseMessageQueueCount(connection);

            // CRITICAL FIX: If we can't get queue depth (GetUsageStatistics returns null/empty or parsing failed),
            // DON'T update state machine. This prevents false positives where queueDepth=0 triggers low watermark incorrectly.
            // -1 = parsing failed, can't determine queue depth
            if (queueDepth < 0)
            {
                // Parsing failed completely - don't update state machine.
                // However, still honor suppression timeout to avoid permanent unreliable starvation.
                if (state.isUnreliableSuppressed &&
                    GONetGlobal.Instance != null &&
                    GONetGlobal.Instance.maxSuppressionTimeoutSeconds > 0 &&
                    state.suppressionStartTicks >= 0)
                {
                    long suppressionDurationMs = (Time.ElapsedTicks - state.suppressionStartTicks) / TimeSpan.TicksPerMillisecond;
                    long timeoutMs = GONetGlobal.Instance.maxSuppressionTimeoutSeconds * 1000;

                    if (suppressionDurationMs > timeoutMs)
                    {
                        state.isUnreliableSuppressed = false;
                        state.suppressionStartTicks = -1;
                        _totalSuppressionStateChanges++;

                        GONetLog.Warning($"[BACKPRESSURE-TIMEOUT] ⚠️ Client {state.authorityId} suppression TIMEOUT after {suppressionDurationMs}ms " +
                                        $"(max: {timeoutMs}ms). Queue depth unavailable; forcing unreliable traffic resumption to avoid permanent suppression. " +
                                        $"Dropped: {state.totalUnreliableDropped} msgs.");
                    }
                }

                return;
            }

            state.reliableQueueDepth = queueDepth;

            // Get configured watermarks (with defaults if GONetGlobal not available)
            int highWatermark = GONetGlobal.Instance.reliableQueueHighWatermark;
            int lowWatermark = GONetGlobal.Instance.reliableQueueLowWatermark;
            int hysteresisCount = GONetGlobal.Instance.congestionHysteresisCount;

            // Hysteresis state machine: Require N consecutive checks before state change
            if (queueDepth > highWatermark)
            {
                state.consecutiveHighWatermarks++;
                state.consecutiveLowWatermarks = 0;

                if (!state.isUnreliableSuppressed && state.consecutiveHighWatermarks >= hysteresisCount)
                {
                    // TRANSITION: Normal → Suppressed
                    state.isUnreliableSuppressed = true;
                    state.suppressionStartTicks = Time.ElapsedTicks;
                    _totalSuppressionStateChanges++;

                    // CRITICAL DIAGNOSTIC (November 20, 2025): ALWAYS log state transitions (not just when logging enabled)
                    // State transitions are rare (2-4 times during late-joiner init) and critical for debugging
                    GONetLog.Warning($"[BACKPRESSURE] ⚠️ Client {state.authorityId} reliable queue at {queueDepth} (>{highWatermark} high watermark), SUPPRESSING unreliable traffic (consecutive high: {state.consecutiveHighWatermarks})");
                }
            }
            else if (queueDepth < lowWatermark)
            {
                state.consecutiveLowWatermarks++;
                state.consecutiveHighWatermarks = 0;

                if (state.isUnreliableSuppressed && state.consecutiveLowWatermarks >= hysteresisCount)
                {
                    // TRANSITION: Suppressed → Normal
                    long suppressionDurationMs = state.suppressionStartTicks >= 0
                        ? (Time.ElapsedTicks - state.suppressionStartTicks) / TimeSpan.TicksPerMillisecond
                        : 0;

                    state.isUnreliableSuppressed = false;
                    state.suppressionStartTicks = -1;
                    _totalSuppressionStateChanges++;

                    // CRITICAL DIAGNOSTIC (November 20, 2025): ALWAYS log state transitions (not just when logging enabled)
                    // State transitions are rare (2-4 times during late-joiner init) and critical for debugging
                    GONetLog.Info($"[BACKPRESSURE] ✅ Client {state.authorityId} reliable queue at {queueDepth} (<{lowWatermark} low watermark), RESUMING unreliable traffic (suppressed for {suppressionDurationMs}ms, dropped {state.totalUnreliableDropped} unreliable msgs)");
                }
            }
            else
            {
                // In hysteresis zone [lowWatermark, highWatermark] - don't change state, reset counters
                state.consecutiveHighWatermarks = 0;
                state.consecutiveLowWatermarks = 0;
            }

            // ADAPTIVE TRICKLE: Update interval based on congestion severity when suppressed
            if (state.isUnreliableSuppressed)
            {
                UpdateAdaptiveTrickleInterval(state, queueDepth);
            }
            else
            {
                // Reset to ~0.33 which Lerps to ~200ms (matching original fixed interval) for next suppression
                state.smoothedCongestionSeverity = 0.33f;
                state.currentAdaptiveTrickleIntervalMs = -1;
            }

            // SAFETY NET: Timeout check to prevent clients from staying suppressed forever
            // AGGRESSIVE RECOVERY (Dec 2025): Use relaxed threshold during timeout to prevent permanent freeze
            // Problem: Window drag / brief hiccups can push queue to 600-1000, but normal recovery requires queue < 200
            // Solution: During timeout, use highWatermark * multiplier (default 2.0 = 1000) as recovery threshold
            if (state.isUnreliableSuppressed &&
                GONetGlobal.Instance.maxSuppressionTimeoutSeconds > 0 &&
                state.suppressionStartTicks >= 0)
            {
                long suppressionDurationMs = (Time.ElapsedTicks - state.suppressionStartTicks) / TimeSpan.TicksPerMillisecond;
                long timeoutMs = GONetGlobal.Instance.maxSuppressionTimeoutSeconds * 1000;

                if (suppressionDurationMs > timeoutMs)
                {
                    // AGGRESSIVE TIMEOUT RECOVERY: Use relaxed threshold (default 2x highWatermark = 1000)
                    // This allows recovery even when queue is elevated but not catastrophic
                    float multiplier = GONetGlobal.Instance.timeoutRecoveryThresholdMultiplier;
                    int aggressiveRecoveryThreshold = (int)(highWatermark * multiplier);

                    if (queueDepth > aggressiveRecoveryThreshold)
                    {
                        // TIMEOUT but SEVERELY congested (queue > 1000) - stay suppressed and reset timer
                        state.suppressionStartTicks = Time.ElapsedTicks;

                        GONetLog.Warning($"[BACKPRESSURE-TIMEOUT] ⚠️ Client {state.authorityId} suppression TIMEOUT after {suppressionDurationMs}ms " +
                                        $"(max: {timeoutMs}ms). Queue SEVERELY congested ({queueDepth} > {aggressiveRecoveryThreshold} aggressive threshold); staying suppressed. " +
                                        $"Dropped: {state.totalUnreliableDropped} msgs.");
                    }
                    else
                    {
                        // AGGRESSIVE TIMEOUT RECOVERY: Queue is elevated but not catastrophic - force resume
                        // With congestion-aware heartbeats, recovery should be safe even with queue at 600-1000
                        state.isUnreliableSuppressed = false;
                        state.suppressionStartTicks = -1;
                        _totalSuppressionStateChanges++;

                        string recoveryLevel = queueDepth <= highWatermark ? "NORMAL" : "AGGRESSIVE";
                        GONetLog.Warning($"[BACKPRESSURE-TIMEOUT] ✅ Client {state.authorityId} {recoveryLevel} RECOVERY after {suppressionDurationMs}ms " +
                                        $"(max: {timeoutMs}ms). Queue={queueDepth} (threshold={aggressiveRecoveryThreshold}, hwm={highWatermark}). " +
                                        $"Forcing unreliable traffic resumption. Dropped: {state.totalUnreliableDropped} msgs.");
                    }
                }
            }
        }

        /// <summary>
        /// Parse "messageQueue.Count: 123" from connection.GetUsageStatistics() string.
        /// Returns -1 if parsing fails (can't determine queue depth).
        /// Returns >= 0 for valid queue depths (0 means empty queue).
        /// </summary>
        private static int ParseMessageQueueCount(GONetConnection connection)
        {
            try
            {
                string stats = connection.GetUsageStatistics();
                if (string.IsNullOrEmpty(stats))
                {
                    // DIAGNOSTIC: Log first failure per connection to understand why stats are missing
                    if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableCongestionStateLogging)
                    {
                        GONetLog.Debug($"[BACKPRESSURE] GetUsageStatistics() returned null/empty for client {connection.OwnerAuthorityId}");
                    }
                    return -1; // Can't determine queue depth
                }

                // Extract "messageQueue.Count: 123"
                const string SEARCH = "messageQueue.Count:";
                int queueIndex = stats.IndexOf(SEARCH);
                if (queueIndex >= 0)
                {
                    int colonPos = queueIndex + SEARCH.Length;
                    int valueStart = colonPos;
                    while (valueStart < stats.Length && char.IsWhiteSpace(stats[valueStart]))
                        valueStart++;

                    int valueEnd = valueStart;
                    while (valueEnd < stats.Length && char.IsDigit(stats[valueEnd]))
                        valueEnd++;

                    if (valueEnd > valueStart && int.TryParse(stats.Substring(valueStart, valueEnd - valueStart), out int count))
                    {
                        return count;
                    }
                }

                // DIAGNOSTIC: Log parsing failure to see actual format
                if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableCongestionStateLogging)
                {
                    GONetLog.Debug($"[BACKPRESSURE] Failed to find 'messageQueue.Count:' in GetUsageStatistics() for client {connection.OwnerAuthorityId}. Stats string: {stats.Substring(0, Math.Min(200, stats.Length))}");
                }
            }
            catch (Exception ex)
            {
                GONetLog.Warning($"[BACKPRESSURE] Failed to parse messageQueue.Count from GetUsageStatistics(): {ex.Message}");
            }

            return -1; // Couldn't parse
        }

        /// <summary>
        /// Remove congestion state when client disconnects (cleanup to prevent memory leak).
        /// Called from Server_OnClientDisconnected handler.
        /// </summary>
        private static void RemoveCongestionState(ushort authorityId)
        {
            _clientCongestionStates.Remove(authorityId);
        }

        /// <summary>
        /// PUBLIC API: Check if a specific client is currently under backpressure (unreliable traffic suppressed).
        ///
        /// Used by distributed host systems (heartbeats, vice host sync) to determine whether to:
        /// - Send reliably (normal) vs unreliably (during congestion) to allow queue to drain
        /// - Skip optional traffic entirely during severe congestion
        ///
        /// CRITICAL FOR RECOVERY:
        /// When a client is under backpressure, reliable messages continue adding to the queue.
        /// If heartbeats/sync continue reliably at 8-10 Hz, the queue can grow indefinitely
        /// and NEVER recover. Switching to unreliable during congestion allows the queue to drain.
        /// </summary>
        /// <param name="authorityId">Client authority ID to check</param>
        /// <param name="reliableQueueDepth">Output: Current reliable queue depth (or -1 if unknown)</param>
        /// <returns>True if client is under backpressure (unreliable traffic suppressed)</returns>
        public static bool IsClientUnderBackpressure(ushort authorityId, out int reliableQueueDepth)
        {
            reliableQueueDepth = -1;

            if (_clientCongestionStates.TryGetValue(authorityId, out var state))
            {
                reliableQueueDepth = state.reliableQueueDepth;
                return state.isUnreliableSuppressed;
            }

            return false;
        }

        #region Adaptive Trickle Rate

        /// <summary>
        /// Update adaptive trickle interval using congestion severity.
        /// Uses EMA smoothing on severity, then Lerp between min/max bounds.
        ///
        /// ALGORITHM:
        /// 1. Calculate raw severity (0.0 = at low watermark, 1.0 = at/above high watermark)
        /// 2. Apply EMA smoothing to prevent jitter from momentary fluctuations
        /// 3. Lerp between minTrickleIntervalMs (fast, 50ms) and maxTrickleIntervalMs (slow, 500ms)
        ///
        /// Called from UpdateClientCongestionState() when client is suppressed.
        /// </summary>
        private static void UpdateAdaptiveTrickleInterval(ClientCongestionState state, int queueDepth)
        {
            if (GONetGlobal.Instance == null || !GONetGlobal.Instance.enableAdaptiveTrickle)
            {
                state.currentAdaptiveTrickleIntervalMs = -1;
                return;
            }

            int highWatermark = GONetGlobal.Instance.reliableQueueHighWatermark;
            int lowWatermark = GONetGlobal.Instance.reliableQueueLowWatermark;

            // Calculate raw severity (0.0 = at low watermark, 1.0 = at/above high watermark)
            float rawSeverity;
            if (queueDepth >= highWatermark)
            {
                rawSeverity = 1f;
            }
            else if (queueDepth <= lowWatermark)
            {
                rawSeverity = 0f;
            }
            else
            {
                // Linear interpolation between watermarks
                rawSeverity = (float)(queueDepth - lowWatermark) / (highWatermark - lowWatermark);
            }

            // EMA smoothing on severity (prevents jitter from momentary congestion fluctuations)
            float alpha = GONetGlobal.Instance.trickleAdaptationAlpha;
            state.smoothedCongestionSeverity = alpha * rawSeverity + (1f - alpha) * state.smoothedCongestionSeverity;

            // Lerp between min (fast, low congestion) and max (slow, high congestion) based on smoothed severity
            int minMs = GONetGlobal.Instance.minTrickleIntervalMs;
            int maxMs = GONetGlobal.Instance.maxTrickleIntervalMs;
            state.currentAdaptiveTrickleIntervalMs = (int)UnityEngine.Mathf.Lerp(minMs, maxMs, state.smoothedCongestionSeverity);
        }

        #endregion

        #region Temporal Thinning (Smart Congestion Management)

        /// <summary>
        /// Diagnostic counters for temporal thinning operations.
        /// </summary>
        private static long _sendQueueThinningCount = 0;
        private static long _receiveQueueThinningCount = 0;
        private static long _sendQueueMessagesDropped = 0;
        private static long _receiveQueueMessagesDropped = 0;

        /// <summary>
        /// DIAGNOSTIC: Counters for sync packet reception (per client).
        /// Used to verify if Client 2 receives same number of position updates as Client 1.
        /// </summary>
        private static long _diagnosticSyncPacketsReceived_Total = 0;
        private static long _diagnosticSyncPacketsReceived_Unreliable = 0;
        private static long _diagnosticSyncPacketsReceived_Reliable = 0;
        private static System.Diagnostics.Stopwatch _diagnosticSyncPacketTimer = System.Diagnostics.Stopwatch.StartNew();
        private static long _diagnosticLastLogTime_Ms = 0;

        /// <summary>
        /// Pooled lists for temporal thinning (zero allocations).
        /// Reused across all thinning operations.
        /// </summary>
        private static readonly List<NetworkData> _tempReliableMessages = new List<NetworkData>(1000);
        private static readonly List<NetworkData> _tempUnreliableMessages = new List<NetworkData>(10000);

        /// <summary>
        /// Stopwatch for CPU time-boxing on send thread.
        /// Used for dual-trigger thinning (queue count OR CPU time budget exceeded).
        /// </summary>
        private static readonly System.Diagnostics.Stopwatch _sendThreadProcessingStopwatch = new System.Diagnostics.Stopwatch();

        /// <summary>
        /// RELIABLE MESSAGE FRAME SPREADING: Calculate adaptive processing limit based on congestion severity.
        ///
        /// PURPOSE: Prevents Unity main thread stutter when reliable message queues back up.
        /// Unlike thinning (which drops unreliable messages), frame spreading DEFERS reliable messages
        /// to next frame (lossless), protecting main thread frame time.
        ///
        /// ADAPTIVE ESCALATION (3 levels + panic valve):
        /// - Light congestion (1-2x threshold): Process baseline (e.g., 100 msg/frame)
        /// - Medium congestion (2-3x threshold): Process baseline/2 (e.g., 50 msg/frame)
        /// - Heavy congestion (3-10x threshold): Process baseline/4 (e.g., 25 msg/frame)
        /// - PANIC (>10x threshold): Process ALL messages (int.MaxValue) - better to lag than lose sync
        ///
        /// PANIC VALVE RATIONALE:
        /// If queue > 2000 messages (10x default threshold of 200), the game state is so far behind
        /// that catching up is more important than frame rate. Disable frame spreading and force the
        /// main thread to process everything, even if it causes stutter. Losing synchronization is
        /// worse than temporary lag.
        ///
        /// SELF-CORRECTING: As queue drains, congestion severity decreases, processing limit increases.
        /// System naturally recovers to baseline throughput.
        ///
        /// THREADING CONTEXT: Main Unity thread only (receive-side).
        /// Send thread (background) doesn't need frame spreading since it has no frame budget constraints.
        /// </summary>
        /// <param name="congestionSeverity">Overage multiplier (1.0 = at threshold, 2.0 = 2x over, etc.)</param>
        /// <returns>Max messages to process this frame (int.MaxValue = panic mode, process all)</returns>
        private static int CalculateReliableProcessingLimit(double congestionSeverity)
        {
            // Null safety: Use sensible defaults if GONetGlobal not initialized
            int baselineLimit = GONetGlobal.Instance != null
                ? GONetGlobal.Instance.frameSpreadingSettings.reliableProcessingBaselineLimit
                : 100;

            bool isAdaptiveEnabled = GONetGlobal.Instance != null
                && GONetGlobal.Instance.frameSpreadingSettings.enableAdaptiveFrameSpreading;

            // PANIC VALVE: If congestion is extreme (>10x threshold), process EVERYTHING
            // Example: 2000 messages / 200 threshold = 10.0x congestion
            // Rationale: Game state is too far behind, catching up is more important than frame rate
            const double PANIC_THRESHOLD = 10.0;
            if (congestionSeverity > PANIC_THRESHOLD)
            {
                if (GONetGlobal.Instance != null && GONetGlobal.Instance.frameSpreadingSettings.enableFrameSpreadingLogging)
                {
                    GONetLog.Warning($"[RECV-SPREAD-PANIC] PANIC VALVE ACTIVATED! Congestion severity {congestionSeverity:F1}x exceeds panic threshold {PANIC_THRESHOLD}x. Processing ALL messages (disabling frame spreading). Better to lag than lose sync!");
                }
                return int.MaxValue; // Process everything, frame time be damned
            }

            // Fixed limit when adaptive disabled
            if (!isAdaptiveEnabled)
            {
                return baselineLimit;
            }

            // ADAPTIVE ESCALATION: More aggressive spreading as congestion worsens
            if (congestionSeverity >= 3.0)
            {
                return baselineLimit / 4; // Heavy: 25 messages/frame (default baseline=100)
            }
            else if (congestionSeverity >= 2.0)
            {
                return baselineLimit / 2; // Medium: 50 messages/frame (default baseline=100)
            }
            else
            {
                return baselineLimit; // Light: 100 messages/frame (default baseline=100)
            }
        }

        /// <summary>
        /// SEND-SIDE TEMPORAL THINNING: Intelligently thins send queue when it backs up.
        ///
        /// Instead of dropping random packets at 90% threshold, thins entire queue by keeping
        /// every Nth unreliable message while preserving ALL reliable messages.
        ///
        /// BENEFITS:
        /// - Continuous 50% fidelity timeline (vs random gaps)
        /// - Prevents network flooding BEFORE it happens
        /// - Smoother degradation under load
        ///
        /// ADAPTIVE THINNING (November 2025): Adjusts thinning rate based on congestion severity
        /// - Light congestion (1-2x threshold): Drop 50% (keep every 2nd)
        /// - Medium congestion (2-3x threshold): Drop 66% (keep every 3rd)
        /// - Heavy congestion (3x+ threshold): Drop 75% (keep every 4th)
        ///
        /// ALGORITHM:
        /// 1. Dequeue ALL messages into temporary lists
        /// 2. Separate reliable vs unreliable
        /// 3. Keep ALL reliable messages
        /// 4. Keep every Nth unreliable message (adaptive temporal sampling)
        /// 5. Re-enqueue kept messages
        /// 6. Return dropped messages' byte arrays to pool (prevent leak!)
        /// </summary>
        /// <param name="queue">Queue to thin</param>
        /// <param name="singleProducerQueues">Producer queues for resource pool</param>
        /// <param name="congestionSeverity">Overage multiplier (1.0 = at threshold, 2.0 = 2x over, etc.)</param>
        private static void ThinSendQueue(ConcurrentQueue<NetworkData> queue, SingleProducerQueues singleProducerQueues, double congestionSeverity = 1.0)
        {
            int originalCount = queue.Count;
            if (originalCount == 0) return;

            // Clear pooled lists (reuse from previous operations)
            _tempReliableMessages.Clear();
            _tempUnreliableMessages.Clear();

            // Dequeue ALL messages and separate by reliability
            NetworkData networkData;
            while (queue.TryDequeue(out networkData))
            {
                bool isReliable = GONetChannel.ById(networkData.channelId).QualityOfService == QosType.Reliable;
                if (isReliable)
                {
                    _tempReliableMessages.Add(networkData);
                }
                else
                {
                    _tempUnreliableMessages.Add(networkData);
                }
            }

            // ADAPTIVE THINNING: Calculate keepEveryNth based on congestion severity
            int keepEveryNth;
            if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableAdaptiveThinning && congestionSeverity > 1.0)
            {
                // Adaptive algorithm: More aggressive thinning as congestion increases
                if (congestionSeverity >= 3.0)
                    keepEveryNth = 4;  // Heavy congestion: Drop 75% (keep every 4th)
                else if (congestionSeverity >= 2.0)
                    keepEveryNth = 3;  // Medium congestion: Drop 66% (keep every 3rd)
                else
                    keepEveryNth = GONetGlobal.Instance.temporalThinningKeepEveryNth;  // Light congestion: Use baseline (default 2 = 50%)
            }
            else
            {
                // Fixed thinning when adaptive disabled or no severity info
                keepEveryNth = GONetGlobal.Instance != null ? GONetGlobal.Instance.temporalThinningKeepEveryNth : 2;
            }

            // Re-enqueue ALL reliable messages (always preserve)
            for (int i = 0; i < _tempReliableMessages.Count; i++)
            {
                queue.Enqueue(_tempReliableMessages[i]);
            }

            // Re-enqueue every Nth unreliable message (temporal sampling)
            int keptUnreliable = 0;
            for (int i = 0; i < _tempUnreliableMessages.Count; i++)
            {
                if (i % keepEveryNth == 0)
                {
                    queue.Enqueue(_tempUnreliableMessages[i]);
                    keptUnreliable++;
                }
                else
                {
                    // CRITICAL: Return dropped message's byte array to pool to prevent memory leak!
                    singleProducerQueues.resourcePool.Return(_tempUnreliableMessages[i].messageBytes);
                }
            }

            int droppedUnreliable = _tempUnreliableMessages.Count - keptUnreliable;
            int newCount = queue.Count;

            // Update diagnostics
            _sendQueueThinningCount++;
            _sendQueueMessagesDropped += droppedUnreliable;

            // Log thinning operation with adaptive level info
            if (GONetGlobal.Instance == null || GONetGlobal.Instance.enableCongestionLogging)
            {
                string adaptiveInfo = GONetGlobal.Instance != null && GONetGlobal.Instance.enableAdaptiveThinning && congestionSeverity > 1.0
                    ? $" [ADAPTIVE: {congestionSeverity:F1}x overage → keep every {keepEveryNth}th = {(1.0 / keepEveryNth * 100):F0}% fidelity]"
                    : $" [FIXED: keep every {keepEveryNth}th]";

                GONetLog.Warning($"[SEND-THIN] Thinned send queue: {originalCount} → {newCount} messages " +
                                $"(reliable: {_tempReliableMessages.Count}, unreliable kept: {keptUnreliable}, dropped: {droppedUnreliable})" +
                                adaptiveInfo +
                                $" [Total thinnings: {_sendQueueThinningCount}, total dropped: {_sendQueueMessagesDropped}]");
            }
        }

        /// <summary>
        /// RECEIVE-SIDE TEMPORAL THINNING: Intelligently thins receive queue when client falls behind.
        ///
        /// Prevents main thread freezes (24-second hang with 800 objects) by thinning processing queue
        /// before it explodes (1449 → 6380 messages).
        ///
        /// BENEFITS:
        /// - Prevents 24-second main thread freezes
        /// - Defense-in-depth (catches bursts that bypassed send-side thinning)
        /// - Continuous timeline vs catastrophic backlog
        ///
        /// ADAPTIVE THINNING (November 2025): Adjusts thinning rate based on congestion severity
        /// - Light congestion (1-2x threshold): Drop 50% (keep every 2nd)
        /// - Medium congestion (2-3x threshold): Drop 66% (keep every 3rd)
        /// - Heavy congestion (3x+ threshold): Drop 75% (keep every 4th)
        /// </summary>
        /// <param name="queue">Queue to thin</param>
        /// <param name="singleProducerQueues">Producer queues for resource pool</param>
        /// <param name="congestionSeverity">Overage multiplier (1.0 = at threshold, 2.0 = 2x over, etc.)</param>
        private static void ThinReceiveQueue(ConcurrentQueue<NetworkData> queue, SingleProducerQueues singleProducerQueues, double congestionSeverity = 1.0)
        {
            int originalCount = queue.Count;
            if (originalCount == 0) return;

            // Clear pooled lists (reuse from previous operations)
            _tempReliableMessages.Clear();
            _tempUnreliableMessages.Clear();

            // Dequeue ALL messages and separate by reliability
            NetworkData networkData;
            while (queue.TryDequeue(out networkData))
            {
                bool isReliable = GONetChannel.ById(networkData.channelId).QualityOfService == QosType.Reliable;
                if (isReliable)
                {
                    _tempReliableMessages.Add(networkData);
                }
                else
                {
                    _tempUnreliableMessages.Add(networkData);
                }
            }

            // ADAPTIVE THINNING: Calculate keepEveryNth based on congestion severity
            int keepEveryNth;
            if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableAdaptiveThinning && congestionSeverity > 1.0)
            {
                // Adaptive algorithm: More aggressive thinning as congestion increases
                if (congestionSeverity >= 3.0)
                    keepEveryNth = 4;  // Heavy congestion: Drop 75% (keep every 4th)
                else if (congestionSeverity >= 2.0)
                    keepEveryNth = 3;  // Medium congestion: Drop 66% (keep every 3rd)
                else
                    keepEveryNth = GONetGlobal.Instance.temporalThinningKeepEveryNth;  // Light congestion: Use baseline (default 2 = 50%)
            }
            else
            {
                // Fixed thinning when adaptive disabled or no severity info
                keepEveryNth = GONetGlobal.Instance != null ? GONetGlobal.Instance.temporalThinningKeepEveryNth : 2;
            }

            // Re-enqueue ALL reliable messages (always preserve)
            for (int i = 0; i < _tempReliableMessages.Count; i++)
            {
                queue.Enqueue(_tempReliableMessages[i]);
            }

            // Re-enqueue every Nth unreliable message (temporal sampling)
            int keptUnreliable = 0;
            for (int i = 0; i < _tempUnreliableMessages.Count; i++)
            {
                if (i % keepEveryNth == 0)
                {
                    queue.Enqueue(_tempUnreliableMessages[i]);
                    keptUnreliable++;
                }
                else
                {
                    // CRITICAL: Return dropped message's byte array to pool to prevent memory leak!
                    // Must use the cross-thread return queue since arrays were borrowed on network thread
                    singleProducerQueues.queueForPostWorkResourceReturn.Enqueue(_tempUnreliableMessages[i]);
                }
            }

            int droppedUnreliable = _tempUnreliableMessages.Count - keptUnreliable;
            int newCount = queue.Count;

            // Update diagnostics
            _receiveQueueThinningCount++;
            _receiveQueueMessagesDropped += droppedUnreliable;

            // Log thinning operation with adaptive level info
            if (GONetGlobal.Instance == null || GONetGlobal.Instance.enableCongestionLogging)
            {
                string adaptiveInfo = GONetGlobal.Instance != null && GONetGlobal.Instance.enableAdaptiveThinning && congestionSeverity > 1.0
                    ? $" [ADAPTIVE: {congestionSeverity:F1}x overage → keep every {keepEveryNth}th = {(1.0 / keepEveryNth * 100):F0}% fidelity]"
                    : $" [FIXED: keep every {keepEveryNth}th]";

                GONetLog.Warning($"[RECV-THIN] Thinned receive queue: {originalCount} → {newCount} messages " +
                                $"(reliable: {_tempReliableMessages.Count}, unreliable kept: {keptUnreliable}, dropped: {droppedUnreliable})" +
                                adaptiveInfo +
                                $" [Total thinnings: {_receiveQueueThinningCount}, total dropped: {_receiveQueueMessagesDropped}]");
            }
        }

        #endregion
        #endregion

    }
}
