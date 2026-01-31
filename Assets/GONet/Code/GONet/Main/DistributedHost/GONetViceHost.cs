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
using MemoryPack;
using GONet;
using GONet.Utils;
using UnityEngine;

namespace GONet.DistributedHost
{
    /// <summary>
    /// Manages vice host "super client" state replication.
    ///
    /// The vice host receives continuous replication from the current host,
    /// maintaining a shadow copy of all host-only state. This enables:
    /// - Delta-only handoff (bytes instead of megabytes)
    /// - Near-instant graceful migration
    /// - Emergency failover with minimal state loss
    ///
    /// Shadow state includes:
    /// - Persistent events copy (spawns, despawns, scene loads, persistent RPCs)
    /// - GONetId allocation watermarks
    /// - Time offset from host
    /// - Last processed RPC sequence ID per player (for idempotency)
    /// </summary>
    public class GONetViceHostManager
    {
        #region Constants

        /// <summary>
        /// Update frequency for vice host sync in Hz (10 Hz = every 100ms).
        /// Critical systems sync at this rate.
        /// </summary>
        public const float CRITICAL_SYNC_HZ = 10f;

        /// <summary>
        /// Update frequency for full state sync in Hz (1 Hz = every second).
        /// Non-critical systems sync at this rate.
        /// </summary>
        public const float FULL_SYNC_HZ = 1f;

        /// <summary>
        /// Maximum size of delta payload before switching to full sync.
        /// </summary>
        public const int MAX_DELTA_SIZE_BYTES = 16384; // 16 KB

        /// <summary>
        /// Interval between vice host candidate evaluations (in seconds).
        /// </summary>
        public const float EVALUATION_INTERVAL_SECONDS = 1.0f;

        /// <summary>
        /// Cooldown after host promotion before evaluating vice host candidates.
        /// Allows mesh connections to stabilize and avoids rapid churn.
        /// </summary>
        public const float POST_PROMOTION_COOLDOWN_SECONDS = 3.0f;

        /// <summary>
        /// Cooldown after voluntary demotion before the previous host can be reselected as vice host.
        /// Prevents vice host sync from starving normal client sync immediately after handoff.
        /// </summary>
        private const float DEMOTED_HOST_VICE_HOST_COOLDOWN_SECONDS = 30.0f;

        /// <summary>
        /// Minimum ratio improvement required to re-notify for the same candidate.
        /// Example: 0.10 = 10% better than last notified.
        /// </summary>
        private const float BETTER_HOST_RENOTIFY_RATIO_DELTA = 0.10f;

        #endregion

        #region State

        /// <summary>
        /// Whether this node is currently designated as the vice host.
        /// </summary>
        private bool isViceHost;

        /// <summary>
        /// Whether this node is currently the host (and managing a vice host).
        /// </summary>
        private bool isHost;

        /// <summary>
        /// Authority ID of the current vice host (only valid when isHost=true).
        /// </summary>
        private ushort viceHostAuthorityId;

        /// <summary>
        /// Authority ID of a host that recently demoted (voluntary handoff).
        /// </summary>
        private ushort demotedHostAuthorityId;

        /// <summary>
        /// Raw tick timestamp when the demoted host completed demotion.
        /// </summary>
        private long demotedHostDemotionRawTicks;

        /// <summary>
        /// Shadow copy of persistent events for vice host.
        /// Deep copies to avoid reference issues.
        /// </summary>
        private readonly List<ViceHostPersistentEventRecord> shadowPersistentEvents = new List<ViceHostPersistentEventRecord>(256);

        /// <summary>
        /// Last acknowledged sync sequence from vice host.
        /// Used for delta calculation.
        /// </summary>
        private ulong lastAcknowledgedSyncSequence;

        /// <summary>
        /// Last sync sequence received while acting as vice host.
        /// </summary>
        private ulong lastReceivedSyncSequence;

        /// <summary>
        /// Raw tick timestamp of the last sync received (vice host side).
        /// </summary>
        private long lastSyncReceivedRawTicks;

        /// <summary>
        /// Raw tick timestamp of the last sync acknowledgement received (host side).
        /// </summary>
        private long lastSyncAckReceivedRawTicks;

        /// <summary>
        /// Current sync sequence number.
        /// Increments on each sync message sent.
        /// </summary>
        private ulong currentSyncSequence;

        /// <summary>
        /// Time of last critical sync.
        /// </summary>
        private float lastCriticalSyncTime;

        /// <summary>
        /// Time of last full sync.
        /// </summary>
        private float lastFullSyncTime;

        /// <summary>
        /// GONetId allocation watermark - highest raw ID allocated.
        /// </summary>
        private uint gonetIdWatermark;

        /// <summary>
        /// Last processed RPC sequence ID per authority.
        /// Used for idempotency during handoff.
        /// </summary>
        private readonly Dictionary<ushort, uint> lastProcessedRpcSequenceByAuthority = new Dictionary<ushort, uint>(32);

        /// <summary>
        /// Time offset from host's time authority.
        /// </summary>
        private double hostTimeOffset;

        /// <summary>
        /// Whether the vice host manager is initialized.
        /// </summary>
        private bool isInitialized;

        /// <summary>
        /// Serialization buffer for vice host messages.
        /// </summary>
        private byte[] serializationBuffer = new byte[1024];

        /// <summary>
        /// Time of last vice host candidate evaluation.
        /// </summary>
        private float lastEvaluationTime;

        /// <summary>
        /// Current best candidate's score (for hysteresis comparison).
        /// </summary>
        private float currentViceHostScore;

        /// <summary>
        /// Raw tick timestamp when this node last became host (used for cooldown).
        /// </summary>
        private long lastHostPromotionRawTicks;

        /// <summary>
        /// Better host detection tracking (host-only).
        /// </summary>
        private BetterHostDetectionState betterHostState;

        private static readonly long POST_PROMOTION_COOLDOWN_TICKS =
            (long)(POST_PROMOTION_COOLDOWN_SECONDS * TimeSpan.TicksPerSecond);

        private static readonly long DEMOTED_HOST_COOLDOWN_TICKS =
            (long)(DEMOTED_HOST_VICE_HOST_COOLDOWN_SECONDS * TimeSpan.TicksPerSecond);

        #endregion

        #region Events

        /// <summary>
        /// Fired when this node becomes the vice host.
        /// </summary>
        public event Action OnBecameViceHost;

        /// <summary>
        /// Fired when this node stops being the vice host.
        /// </summary>
        public event Action OnStoppedBeingViceHost;

        /// <summary>
        /// Fired when a sync is received (vice host side).
        /// </summary>
        public event Action<ulong> OnSyncReceived;

        /// <summary>
        /// Fired when a sync acknowledgement is received (host side).
        /// </summary>
        public event Action<ulong> OnSyncAcknowledged;

        #endregion

        #region Singleton

        private static GONetViceHostManager instance;
        public static GONetViceHostManager Instance => instance ??= new GONetViceHostManager();

        private GONetViceHostManager() { }

        #endregion

        #region Properties

        /// <summary>
        /// Gets whether this node is the designated vice host.
        /// </summary>
        public bool IsViceHost => isViceHost;

        /// <summary>
        /// Gets whether this node is managing a vice host.
        /// </summary>
        public bool IsManagingViceHost => isHost && viceHostAuthorityId != 0;

        /// <summary>
        /// Gets the authority ID of the vice host (when this node is host).
        /// </summary>
        public ushort ViceHostAuthorityId => viceHostAuthorityId;

        /// <summary>
        /// Gets the current vice host's score (for heartbeat propagation and debug visibility).
        /// </summary>
        public float CurrentViceHostScore => currentViceHostScore;

        /// <summary>
        /// Gets the last acknowledged sync sequence.
        /// </summary>
        public ulong LastAcknowledgedSyncSequence => lastAcknowledgedSyncSequence;

        /// <summary>
        /// Gets the last sync sequence received (vice host side).
        /// </summary>
        public ulong LastReceivedSyncSequence => lastReceivedSyncSequence;

        /// <summary>
        /// Gets the current sync sequence.
        /// </summary>
        public ulong CurrentSyncSequence => currentSyncSequence;

        /// <summary>
        /// Gets the shadow persistent event count (vice host side).
        /// </summary>
        public int ShadowEventCount => shadowPersistentEvents.Count;

        /// <summary>
        /// Gets the last time a voluntary migration was initiated (host only).
        /// </summary>
        public float LastVoluntaryMigrationTime => betterHostState.LastMigrationTime;

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the vice host manager.
        /// </summary>
        /// <param name="asHost">True if this node is the host</param>
        public void Initialize(bool asHost)
        {
            if (isInitialized) return;

            isHost = asHost;
            isViceHost = false;
            viceHostAuthorityId = 0;
            currentSyncSequence = 0;
            lastAcknowledgedSyncSequence = 0;
            lastCriticalSyncTime = 0;
            lastFullSyncTime = 0;
            lastEvaluationTime = 0;
            currentViceHostScore = 0;
            lastHostPromotionRawTicks = 0;
            betterHostState = default;
            ClearDemotedHostTracking();
            shadowPersistentEvents.Clear();
            lastProcessedRpcSequenceByAuthority.Clear();

            isInitialized = true;
            GONetLog.Info($"[ViceHost] Initialized (isHost={asHost})");
        }

        /// <summary>
        /// Shuts down the vice host manager.
        /// </summary>
        public void Shutdown()
        {
            if (!isInitialized) return;

            shadowPersistentEvents.Clear();
            lastProcessedRpcSequenceByAuthority.Clear();
            isViceHost = false;
            isHost = false;
            viceHostAuthorityId = 0;
            currentSyncSequence = 0;
            lastAcknowledgedSyncSequence = 0;
            lastCriticalSyncTime = 0;
            lastFullSyncTime = 0;
            lastEvaluationTime = 0;
            currentViceHostScore = 0;
            lastHostPromotionRawTicks = 0;
            betterHostState = default;
            ClearDemotedHostTracking();
            isInitialized = false;

            GONetLog.Info("[ViceHost] Shut down");
        }

        #endregion

        #region Update Loop

        /// <summary>
        /// Called from GONetMain.Update() to process vice host sync.
        /// </summary>
        public void Update(float elapsedSeconds)
        {
            if (!isInitialized) return;
            if (!GONetGlobal.Instance.enableDistributedHostAuthority) return;

            if (isHost)
            {
                // Host: Evaluate vice host candidates periodically
                EvaluateViceHostCandidates(elapsedSeconds);

                // Host: Send sync updates to vice host (if we have one)
                if (viceHostAuthorityId != 0)
                {
                    UpdateHostSyncToViceHost(elapsedSeconds);
                }
            }
        }

        /// <summary>
        /// Host-side: Periodically evaluate all candidates and update vice host designation.
        /// Uses GONetHostScoring to rank candidates based on network, hardware, stability, and NAT metrics.
        /// </summary>
        private void EvaluateViceHostCandidates(float elapsedSeconds)
        {
            // Allow time for mesh connections to stabilize after promotion.
            if (lastHostPromotionRawTicks != 0 && POST_PROMOTION_COOLDOWN_TICKS > 0)
            {
                long nowRawTicks = GONetMain.Time.RawElapsedTicks;
                if (nowRawTicks - lastHostPromotionRawTicks < POST_PROMOTION_COOLDOWN_TICKS)
                {
                    return;
                }
            }

            // Rate limit evaluation
            if (elapsedSeconds - lastEvaluationTime < EVALUATION_INTERVAL_SECONDS)
                return;

            lastEvaluationTime = elapsedSeconds;

            var gossipManager = GONetGossipManager.Instance;
            if (gossipManager == null || gossipManager.RemoteNodeCount == 0)
            {
                // No candidates - clear vice host if we had one
                if (viceHostAuthorityId != 0)
                {
                    GONetLog.Info($"[ViceHost] No candidates available, clearing vice host (was {viceHostAuthorityId})");
                    SetViceHost(0);
                    currentViceHostScore = 0;
                }
                ResetBetterHostCandidateTracking();
                return;
            }

            // Build candidate list
            var candidates = new List<(ushort authorityId, GONetNodeMetrics metrics, GONetNodeCapabilities capabilities, float avgRttMs, bool isStale)>();

            float now = elapsedSeconds;
            const float STALE_THRESHOLD_SECONDS = 6.0f;
            int metricsCount = 0;
            int skippedSelf = 0;
            int skippedNoIdentity = 0;
            int skippedDemoted = 0;

            foreach (var (authorityId, metrics) in gossipManager.GetAllNodeMetrics())
            {
                metricsCount++;

                // Skip self
                if (authorityId == GONetMain.MyAuthorityId)
                {
                    skippedSelf++;
                    continue;
                }

                if (IsDemotedHostInCooldown(authorityId, out float remainingSeconds))
                {
                    skippedDemoted++;
                    GONetLog.Debug($"[ViceHost] Skipping authority {authorityId}: recently demoted host ({remainingSeconds:0.0}s cooldown remaining)");
                    continue;
                }

                // Get identity for capabilities
                if (!gossipManager.TryGetNodeIdentity(authorityId, out var identity))
                {
                    skippedNoIdentity++;
                    GONetLog.Debug($"[ViceHost] Skipping authority {authorityId}: no identity found");
                    continue;
                }

                // Calculate average RTT for this candidate
                float avgRttMs = gossipManager.GetAverageRTTForCandidate(authorityId, metrics.RTT_Average_Ms);

                // Check staleness
                bool isStale = gossipManager.IsMetricsStale(authorityId, now, STALE_THRESHOLD_SECONDS);

                candidates.Add((authorityId, metrics, identity.Capabilities, avgRttMs, isStale));
            }

            // Debug: Log evaluation summary periodically (every 10 seconds)
            if ((int)elapsedSeconds % 10 == 0 && elapsedSeconds - lastEvaluationTime < 2.0f)
            {
                GONetLog.Debug($"[ViceHost] Evaluation: remoteNodes={gossipManager.RemoteNodeCount}, " +
                              $"metricsCount={metricsCount}, candidates={candidates.Count}, " +
                              $"skippedSelf={skippedSelf}, skippedNoIdentity={skippedNoIdentity}, skippedDemoted={skippedDemoted}, " +
                              $"currentViceHost={viceHostAuthorityId}");
            }

            if (candidates.Count == 0)
            {
                if (viceHostAuthorityId != 0)
                {
                    GONetLog.Info($"[ViceHost] No eligible candidates, clearing vice host (was {viceHostAuthorityId})");
                    SetViceHost(0);
                    currentViceHostScore = 0;
                }
                ResetBetterHostCandidateTracking();
                ClearDiagnostics(elapsedSeconds);
                return;
            }

            // Log all candidate evaluations for debugging (when designating or periodically)
            bool shouldLogAllCandidates = viceHostAuthorityId == 0 || (int)elapsedSeconds % 30 == 0;
            if (shouldLogAllCandidates && candidates.Count > 0)
            {
                GONetLog.Info($"[ViceHost] === Candidate Evaluation ({candidates.Count} candidates) ===");
                foreach (var (authorityId, metrics, capabilities, avgRttMs, isStale) in candidates)
                {
                    var eval = GONetHostScoring.EvaluateCandidate(authorityId, metrics, capabilities, avgRttMs, isStale);
                    string staleTag = isStale ? " [STALE]" : "";
                    GONetLog.Info($"[ViceHost]   Auth {authorityId}: Total={eval.TotalScore:F1} " +
                                 $"(Net={eval.NetworkScore:F0}, HW={eval.HardwareScore:F0}, " +
                                 $"Stab={eval.StabilityScore:F0}, NAT={eval.NATScore:F0}) " +
                                 $"Perf={eval.PerformanceMultiplier:F2}, Eligible={eval.IsEligible}{staleTag}");
                    GONetLog.Info($"[ViceHost]     Raw: RTT={metrics.RTT_Average_Ms}ms, CPU={metrics.CPU_Headroom_Percent}%, " +
                                 $"Frame={metrics.FrameTime_Headroom_Ms}ms, Up={metrics.Uptime_Seconds}s, " +
                                 $"NAT={metrics.NATCompatibilityScore}, Stab={metrics.StabilityScore}, AvgRTT={avgRttMs:F0}ms");
                }
                GONetLog.Info($"[ViceHost] ================================================");
            }

            var localIdentity = gossipManager.LocalIdentity;
            var localMetrics = gossipManager.LocalMetrics;
            float hostAvgRttMs = gossipManager.GetAverageRTTForCandidate(GONetMain.MyAuthorityId, localMetrics.RTT_Average_Ms);
            var hostEvaluation = GONetHostScoring.EvaluateCandidate(
                GONetMain.MyAuthorityId,
                localMetrics,
                localIdentity.Capabilities,
                hostAvgRttMs,
                isStale: false);

            // Evaluate candidates using scoring system
            ushort bestCandidateId = GONetHostScoring.EvaluateBestViceHost(
                candidates,
                viceHostAuthorityId,
                currentViceHostScore,
                out var bestEvaluation);

            bool hasSelectedCandidate = false;
            GONetNodeMetrics selectedCandidateMetrics = default;
            GONetNodeCapabilities selectedCandidateCapabilities = default;
            float selectedCandidateAvgRttMs = 0f;
            bool selectedCandidateIsStale = false;

            if (bestCandidateId != 0)
            {
                foreach (var (authorityId, metrics, capabilities, avgRttMs, isStale) in candidates)
                {
                    if (authorityId == bestCandidateId)
                    {
                        selectedCandidateMetrics = metrics;
                        selectedCandidateCapabilities = capabilities;
                        selectedCandidateAvgRttMs = avgRttMs;
                        selectedCandidateIsStale = isStale;
                        hasSelectedCandidate = true;
                        break;
                    }
                }
            }

            var selectedEvaluation = hasSelectedCandidate
                ? GONetHostScoring.EvaluateCandidate(
                    bestCandidateId,
                    selectedCandidateMetrics,
                    selectedCandidateCapabilities,
                    selectedCandidateAvgRttMs,
                    selectedCandidateIsStale)
                : bestEvaluation;

            // Check if we should change vice host
            if (bestCandidateId != 0 && bestCandidateId != viceHostAuthorityId)
            {
                GONetLog.Info($"[ViceHost] Designating new vice host: Authority {bestCandidateId} " +
                             $"(Score: {selectedEvaluation.TotalScore:F1}, Previous: {viceHostAuthorityId})");

                // Update vice host designation
                ushort previousViceHost = viceHostAuthorityId;
                SetViceHost(bestCandidateId);
                currentViceHostScore = selectedEvaluation.TotalScore;

                // Fire event for the change
                if (previousViceHost != 0)
                {
                    // TODO: Notify previous vice host that they're no longer designated
                }
            }
            else if (bestCandidateId == viceHostAuthorityId && selectedEvaluation.TotalScore > 0)
            {
                // Update score for current vice host (in case it changed)
                currentViceHostScore = selectedEvaluation.TotalScore;
            }

            if (hasSelectedCandidate)
            {
                EvaluateBetterHostCandidate(elapsedSeconds, hostEvaluation, bestCandidateId, selectedEvaluation, selectedCandidateMetrics);
            }
            else
            {
                ResetBetterHostCandidateTracking();
            }

            // Update diagnostics for UI
            UpdateDiagnostics(elapsedSeconds, hostEvaluation, bestCandidateId, selectedEvaluation, selectedCandidateMetrics, hasSelectedCandidate);
        }

        private bool IsDemotedHostInCooldown(ushort authorityId, out float remainingSeconds)
        {
            remainingSeconds = 0f;

            if (authorityId == 0 || authorityId != demotedHostAuthorityId)
            {
                return false;
            }

            if (demotedHostDemotionRawTicks <= 0 || DEMOTED_HOST_COOLDOWN_TICKS <= 0)
            {
                ClearDemotedHostTracking();
                return false;
            }

            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            long elapsedTicks = nowRawTicks - demotedHostDemotionRawTicks;
            if (elapsedTicks < 0)
            {
                elapsedTicks = 0;
            }

            if (elapsedTicks < DEMOTED_HOST_COOLDOWN_TICKS)
            {
                remainingSeconds = (float)((DEMOTED_HOST_COOLDOWN_TICKS - elapsedTicks) / (double)TimeSpan.TicksPerSecond);
                return true;
            }

            ClearDemotedHostTracking();
            return false;
        }

        /// <summary>
        /// Updates cached diagnostics for UI consumption.
        /// </summary>
        private void UpdateDiagnostics(
            float elapsedSeconds,
            GONetHostScoring.CandidateEvaluation hostEval,
            ushort candidateId,
            GONetHostScoring.CandidateEvaluation candidateEval,
            GONetNodeMetrics candidateMetrics,
            bool hasCandidate)
        {
            float percentThreshold = GONetGlobal.Instance?.hostMigrationScoreThreshold ?? 0.2f;
            float pointThreshold = GONetGlobal.Instance?.betterHostMinimumDifference ?? 50f;
            float uptimeRequired = GONetGlobal.Instance?.newJoinerHostEligibilityDelaySeconds ?? 45f;
            int samplesRequired = GONetGlobal.Instance != null ? Mathf.Max(1, GONetGlobal.Instance.betterHostSustainSamples) : 5;
            float cooldownSeconds = GONetGlobal.Instance?.hostMigrationCooldownSeconds ?? 15f;

            float scoreDiff = hasCandidate ? candidateEval.TotalScore - hostEval.TotalScore : 0f;
            float scoreRatio = hasCandidate ? GONetHostScoring.ComputeScoreDifferencePercent(hostEval.TotalScore, candidateEval.TotalScore) : 0f;
            float candidateUptime = hasCandidate ? candidateMetrics.Uptime_Seconds : 0f;

            bool meetsPercent = scoreRatio >= percentThreshold;
            bool meetsPoints = scoreDiff >= pointThreshold;
            bool meetsUptime = candidateUptime >= uptimeRequired;

            // Preview threshold = half of real thresholds
            float previewPercentThreshold = percentThreshold * 0.5f;
            float previewPointThreshold = pointThreshold * 0.5f;
            bool meetsPreview = hasCandidate && scoreRatio >= previewPercentThreshold && scoreDiff >= previewPointThreshold;

            // Cooldown check
            bool cooldownExpired = betterHostState.LastMigrationTime <= 0f ||
                                   elapsedSeconds - betterHostState.LastMigrationTime >= cooldownSeconds;
            float cooldownRemaining = cooldownExpired ? 0f :
                                      Mathf.Max(0f, cooldownSeconds - (elapsedSeconds - betterHostState.LastMigrationTime));

            // Sample progress (only counts when all score/uptime thresholds are met)
            int consecutiveSamples = betterHostState.ConsecutiveBetterSamples;
            float sampleProgress = samplesRequired > 0 ? Mathf.Clamp01((float)consecutiveSamples / samplesRequired) : 0f;

            // HANDOFF FIX (Jan 2025): Check vice host sync freshness - was missing, caused button to show ready but migration to fail
            float syncFreshnessRequired = GONetGlobal.Instance?.viceHostSyncStaleSeconds ?? 2f;
            bool viceHostSyncFresh = IsViceHostStateCurrentEnough(syncFreshnessRequired, out float secondsSinceAck, out _);

            // Migration is ready when ALL conditions are met (including vice host sync freshness)
            bool isMigrationReady = hasCandidate && meetsPercent && meetsPoints && meetsUptime &&
                                    consecutiveSamples >= samplesRequired && cooldownExpired && viceHostSyncFresh;

            cachedDiagnostics = new BetterHostDiagnostics
            {
                IsValid = true,
                HasPreviewCandidate = meetsPreview,
                IsMigrationReady = isMigrationReady,
                HostAuthorityId = GONetMain.MyAuthorityId,
                HostScore = hostEval.TotalScore,
                CandidateAuthorityId = candidateId,
                CandidateScore = hasCandidate ? candidateEval.TotalScore : 0f,
                ScoreDifferenceRatio = scoreRatio,
                ScoreDifferenceAbsolute = scoreDiff,
                RequiredPercentThreshold = percentThreshold,
                RequiredPointThreshold = pointThreshold,
                MeetsPercentThreshold = meetsPercent,
                MeetsPointThreshold = meetsPoints,
                CandidateUptimeSeconds = candidateUptime,
                RequiredUptimeSeconds = uptimeRequired,
                MeetsUptimeThreshold = meetsUptime,
                ConsecutiveSamples = consecutiveSamples,
                RequiredSamples = samplesRequired,
                SampleProgress = sampleProgress,
                CooldownExpired = cooldownExpired,
                CooldownRemainingSeconds = cooldownRemaining,
                LastUpdateTime = elapsedSeconds,
                // HANDOFF FIX (Jan 2025): Vice host sync state
                ViceHostSyncFresh = viceHostSyncFresh,
                SecondsSinceViceHostAck = secondsSinceAck,
                RequiredSyncFreshnessSeconds = syncFreshnessRequired
            };
        }

        /// <summary>
        /// Clears diagnostics when there are no candidates.
        /// </summary>
        private void ClearDiagnostics(float elapsedSeconds)
        {
            cachedDiagnostics = new BetterHostDiagnostics
            {
                IsValid = true,
                HasPreviewCandidate = false,
                IsMigrationReady = false,
                HostAuthorityId = GONetMain.MyAuthorityId,
                HostScore = 0f,
                CandidateAuthorityId = 0,
                CandidateScore = 0f,
                RequiredPercentThreshold = GONetGlobal.Instance?.hostMigrationScoreThreshold ?? 0.2f,
                RequiredPointThreshold = GONetGlobal.Instance?.betterHostMinimumDifference ?? 50f,
                RequiredUptimeSeconds = GONetGlobal.Instance?.newJoinerHostEligibilityDelaySeconds ?? 45f,
                RequiredSamples = GONetGlobal.Instance != null ? Mathf.Max(1, GONetGlobal.Instance.betterHostSustainSamples) : 5,
                LastUpdateTime = elapsedSeconds,
                // Vice host sync state - mark as not ready when no candidates
                ViceHostSyncFresh = false,
                SecondsSinceViceHostAck = float.MaxValue,
                RequiredSyncFreshnessSeconds = GONetGlobal.Instance?.viceHostSyncStaleSeconds ?? 2f
            };
        }

        /// <summary>
        /// Host-side: Send periodic sync updates to vice host.
        /// </summary>
        private void UpdateHostSyncToViceHost(float elapsedSeconds)
        {
            // Critical sync (10 Hz)
            float criticalInterval = 1f / CRITICAL_SYNC_HZ;
            if (elapsedSeconds - lastCriticalSyncTime >= criticalInterval)
            {
                SendCriticalSync();
                lastCriticalSyncTime = elapsedSeconds;
            }

            // Full sync (1 Hz)
            float fullInterval = 1f / FULL_SYNC_HZ;
            if (elapsedSeconds - lastFullSyncTime >= fullInterval)
            {
                SendFullSync();
                lastFullSyncTime = elapsedSeconds;
            }
        }

        #endregion

        #region Better Host Detection

        /// <summary>
        /// Records the time a voluntary migration was initiated.
        /// </summary>
        public void RecordVoluntaryMigrationInitiated(float elapsedSeconds)
        {
            betterHostState.LastMigrationTime = elapsedSeconds;
        }

        /// <summary>
        /// Host-side: Checks if the vice host's last sync acknowledgement is fresh enough for migration.
        /// </summary>
        public bool IsViceHostStateCurrentEnough(float maxStaleSeconds, out float secondsSinceAck, out ulong lastAckSequence)
        {
            lastAckSequence = lastAcknowledgedSyncSequence;
            secondsSinceAck = GetSecondsSince(lastSyncAckReceivedRawTicks);

            if (!isHost || lastAckSequence == 0)
            {
                return false;
            }

            return secondsSinceAck <= maxStaleSeconds;
        }

        /// <summary>
        /// Vice host-side: Checks if the latest sync received from the host is fresh enough for handoff.
        /// </summary>
        public bool IsViceHostSyncFresh(float maxStaleSeconds, out float secondsSinceSync, out ulong lastSequence)
        {
            lastSequence = lastReceivedSyncSequence;
            secondsSinceSync = GetSecondsSince(lastSyncReceivedRawTicks);

            if (!isViceHost || lastSequence == 0)
            {
                return false;
            }

            return secondsSinceSync <= maxStaleSeconds;
        }

        /// <summary>
        /// Returns true if a better host candidate has been stable long enough.
        /// </summary>
        public bool TryGetStableBetterHostCandidate(out ushort candidateAuthorityId)
        {
            candidateAuthorityId = 0;

            if (!isHost)
            {
                return false;
            }

            int sustainSamples = GONetGlobal.Instance != null
                ? Mathf.Max(1, GONetGlobal.Instance.betterHostSustainSamples)
                : 1;

            if (betterHostState.CandidateAuthorityId == 0 ||
                betterHostState.ConsecutiveBetterSamples < sustainSamples)
            {
                return false;
            }

            if (viceHostAuthorityId != 0 && betterHostState.CandidateAuthorityId != viceHostAuthorityId)
            {
                return false;
            }

            candidateAuthorityId = betterHostState.CandidateAuthorityId;
            return true;
        }

        private void EvaluateBetterHostCandidate(
            float elapsedSeconds,
            GONetHostScoring.CandidateEvaluation hostEvaluation,
            ushort bestCandidateId,
            GONetHostScoring.CandidateEvaluation bestCandidateEvaluation,
            GONetNodeMetrics bestCandidateMetrics)
        {
            if (!isHost)
            {
                return;
            }

            if (GONetGlobal.Instance == null || !GONetGlobal.Instance.enableDistributedHostAuthority)
            {
                ResetBetterHostCandidateTracking();
                return;
            }

            if (GONetGlobal.Instance.isPinnedHost || !GONetGlobal.Instance.enableViceHost)
            {
                ResetBetterHostCandidateTracking();
                return;
            }

            if (GONetHostHandoffManager.Instance.IsHandoffInProgress ||
                GONetHostFailoverManager.Instance.IsFailoverInProgress)
            {
                ResetBetterHostCandidateTracking();
                return;
            }

            if (bestCandidateId == 0 || !bestCandidateEvaluation.IsEligible)
            {
                ResetBetterHostCandidateTracking();
                return;
            }

            if (bestCandidateId == GONetMain.MyAuthorityId)
            {
                ResetBetterHostCandidateTracking();
                return;
            }

            float hostScore = hostEvaluation.TotalScore;
            float candidateScore = bestCandidateEvaluation.TotalScore;
            float ratio = GONetHostScoring.ComputeScoreDifferencePercent(hostScore, candidateScore);

            float percentThreshold = GONetGlobal.Instance.hostMigrationScoreThreshold;
            float minDifference = GONetGlobal.Instance.betterHostMinimumDifference;
            bool meetsThreshold = ratio >= percentThreshold &&
                                  (candidateScore - hostScore) >= minDifference;

            float warmupSeconds = GONetGlobal.Instance.newJoinerHostEligibilityDelaySeconds;
            bool passesWarmup = bestCandidateMetrics.Uptime_Seconds >= warmupSeconds;

            if (meetsThreshold && passesWarmup)
            {
                if (bestCandidateId == betterHostState.CandidateAuthorityId)
                {
                    betterHostState.ConsecutiveBetterSamples++;
                }
                else
                {
                    betterHostState.CandidateAuthorityId = bestCandidateId;
                    betterHostState.ConsecutiveBetterSamples = 1;
                    betterHostState.FirstBetterTimestamp = elapsedSeconds;
                }
            }
            else
            {
                ResetBetterHostCandidateTracking();
            }

            int sustainSamples = Mathf.Max(1, GONetGlobal.Instance.betterHostSustainSamples);
            if (betterHostState.ConsecutiveBetterSamples < sustainSamples)
            {
                return;
            }

            float sustainedDurationSeconds = Mathf.Max(0f, elapsedSeconds - betterHostState.FirstBetterTimestamp);
            float cooldownSeconds = GONetGlobal.Instance.hostMigrationCooldownSeconds;
            bool cooldownMet = betterHostState.LastMigrationTime <= 0f ||
                               elapsedSeconds - betterHostState.LastMigrationTime >= cooldownSeconds;

            bool canMigrateNow = GONetGlobal.Instance.betterHostCanMigrateNowCallback?.Invoke() ?? true;
            if (!cooldownMet || !canMigrateNow)
            {
                return;
            }

            if (!ShouldNotifyBetterHost(bestCandidateId, ratio, elapsedSeconds))
            {
                return;
            }

            var evt = new BetterHostAvailableEvent
            {
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks,
                CurrentHostAuthorityId = GONetMain.MyAuthorityId,
                CurrentHostScore = hostScore,
                BetterHostAuthorityId = bestCandidateId,
                BetterHostScore = candidateScore,
                ScoreDifferencePercent = ratio,
                SustainedDurationSeconds = sustainedDurationSeconds
            };

            GONetLog.Info($"[ViceHost] Better host available: candidate={bestCandidateId}, " +
                          $"ratio={ratio:P0}, sustained={sustainedDurationSeconds:F1}s");

            GONetMain.EventBus.Publish(evt);

            betterHostState.LastNotifiedCandidateAuthorityId = bestCandidateId;
            betterHostState.LastNotifiedRatio = ratio;
            betterHostState.LastNotificationTime = elapsedSeconds;

            if (GONetGlobal.Instance.betterHostAutoMigrateEnabled)
            {
                var result = GONetMain.Server_InitiateVoluntaryHostMigration();
                if (result != GONetMain.VoluntaryMigrationResult.Success)
                {
                    GONetLog.Warning($"[ViceHost] Auto-migrate blocked: {result}");
                }
            }
        }

        private bool ShouldNotifyBetterHost(ushort candidateAuthorityId, float ratio, float elapsedSeconds)
        {
            if (betterHostState.LastNotifiedCandidateAuthorityId != candidateAuthorityId)
            {
                return true;
            }

            if (betterHostState.LastNotificationTime <= 0f)
            {
                return true;
            }

            if (ratio > betterHostState.LastNotifiedRatio + BETTER_HOST_RENOTIFY_RATIO_DELTA)
            {
                return true;
            }

            float cooldownSeconds = GONetGlobal.Instance.betterHostEventCooldownSeconds;
            if (elapsedSeconds - betterHostState.LastNotificationTime >= cooldownSeconds)
            {
                return true;
            }

            return false;
        }

        private void ResetBetterHostCandidateTracking()
        {
            betterHostState.CandidateAuthorityId = 0;
            betterHostState.ConsecutiveBetterSamples = 0;
            betterHostState.FirstBetterTimestamp = 0f;
        }

        private struct BetterHostDetectionState
        {
            public ushort CandidateAuthorityId;
            public int ConsecutiveBetterSamples;
            public float FirstBetterTimestamp;
            public ushort LastNotifiedCandidateAuthorityId;
            public float LastNotifiedRatio;
            public float LastNotificationTime;
            public float LastMigrationTime;
        }

        /// <summary>
        /// Cached diagnostics for UI consumption. Updated each evaluation cycle.
        /// </summary>
        private BetterHostDiagnostics cachedDiagnostics;

        #endregion

        #region Diagnostics

        /// <summary>
        /// Diagnostic information about better host detection state.
        /// Used by UI to show real-time progress toward voluntary migration.
        /// </summary>
        public struct BetterHostDiagnostics
        {
            /// <summary>Whether diagnostics data is valid (host is running and evaluating).</summary>
            public bool IsValid;

            /// <summary>Whether there's a candidate that meets the preview threshold (half of real threshold).</summary>
            public bool HasPreviewCandidate;

            /// <summary>Whether the candidate fully meets all thresholds and migration is available.</summary>
            public bool IsMigrationReady;

            /// <summary>Current host's authority ID.</summary>
            public ushort HostAuthorityId;

            /// <summary>Current host's calculated score.</summary>
            public float HostScore;

            /// <summary>Best candidate's authority ID (0 if none).</summary>
            public ushort CandidateAuthorityId;

            /// <summary>Best candidate's calculated score.</summary>
            public float CandidateScore;

            /// <summary>Score difference as ratio (0.25 = 25% better).</summary>
            public float ScoreDifferenceRatio;

            /// <summary>Absolute score difference (candidate - host).</summary>
            public float ScoreDifferenceAbsolute;

            /// <summary>Required percentage threshold from settings.</summary>
            public float RequiredPercentThreshold;

            /// <summary>Required absolute point threshold from settings.</summary>
            public float RequiredPointThreshold;

            /// <summary>Whether score percentage threshold is met.</summary>
            public bool MeetsPercentThreshold;

            /// <summary>Whether absolute point threshold is met.</summary>
            public bool MeetsPointThreshold;

            /// <summary>Candidate's uptime in seconds.</summary>
            public float CandidateUptimeSeconds;

            /// <summary>Required uptime for host eligibility.</summary>
            public float RequiredUptimeSeconds;

            /// <summary>Whether uptime requirement is met.</summary>
            public bool MeetsUptimeThreshold;

            /// <summary>Current consecutive samples where candidate has been better.</summary>
            public int ConsecutiveSamples;

            /// <summary>Required consecutive samples.</summary>
            public int RequiredSamples;

            /// <summary>Progress toward sample requirement (0.0 to 1.0).</summary>
            public float SampleProgress;

            /// <summary>Whether cooldown from last migration has expired.</summary>
            public bool CooldownExpired;

            /// <summary>Seconds remaining in cooldown (0 if expired).</summary>
            public float CooldownRemainingSeconds;

            /// <summary>Timestamp when diagnostics were last updated.</summary>
            public float LastUpdateTime;

            // Vice host sync state (HANDOFF FIX Jan 2025 - was missing, caused button to show ready but migration to fail)
            /// <summary>Whether vice host sync acknowledgements are fresh enough for migration.</summary>
            public bool ViceHostSyncFresh;

            /// <summary>Seconds since last sync acknowledgement from vice host.</summary>
            public float SecondsSinceViceHostAck;

            /// <summary>Maximum allowed seconds for sync freshness.</summary>
            public float RequiredSyncFreshnessSeconds;
        }

        /// <summary>
        /// Gets the current better host diagnostics for UI display.
        /// Returns false if not currently acting as host or diagnostics unavailable.
        /// </summary>
        public bool TryGetBetterHostDiagnostics(out BetterHostDiagnostics diagnostics)
        {
            diagnostics = cachedDiagnostics;
            return cachedDiagnostics.IsValid && isHost;
        }

        #endregion

        #region Host Status Changes

        /// <summary>
        /// Called when this node becomes the host.
        /// </summary>
        public void OnBecameHost()
        {
            isHost = true;
            isViceHost = false;
            // CRITICAL FIX (Dec 2025): Clear viceHostAuthorityId on promotion to prevent KeyNotFoundException
            // Bug: After handoff, promoted host still had old viceHostAuthorityId set (possibly itself when
            // it was vice host), causing sync attempts to a non-existent client.
            viceHostAuthorityId = 0;  // Will re-elect a new vice host
            currentViceHostScore = 0;
            shadowPersistentEvents.Clear(); // No longer need shadow copy - we ARE the source
            lastHostPromotionRawTicks = GONetMain.Time.RawElapsedTicks;
            betterHostState = default;
            lastAcknowledgedSyncSequence = 0;
            lastReceivedSyncSequence = 0;
            lastSyncReceivedRawTicks = 0;
            lastSyncAckReceivedRawTicks = 0;
            ClearDemotedHostTracking();

            GONetLog.Info("[ViceHost] This node is now the host");
        }

        /// <summary>
        /// Called when this node stops being the host (graceful handoff).
        /// </summary>
        public void OnStoppedBeingHost()
        {
            isHost = false;
            viceHostAuthorityId = 0;
            betterHostState = default;
            lastAcknowledgedSyncSequence = 0;
            lastSyncAckReceivedRawTicks = 0;
            ClearDemotedHostTracking();

            GONetLog.Info("[ViceHost] This node is no longer the host");
        }

        /// <summary>
        /// Called when this node was the host but got demoted by a higher-epoch host.
        /// This happens when the old host comes back after a network partition and finds
        /// someone else has taken over.
        /// </summary>
        public void OnDemotedFromHost()
        {
            isHost = false;
            isViceHost = false;
            viceHostAuthorityId = 0;
            shadowPersistentEvents.Clear();
            betterHostState = default;
            lastAcknowledgedSyncSequence = 0;
            lastReceivedSyncSequence = 0;
            lastSyncReceivedRawTicks = 0;
            lastSyncAckReceivedRawTicks = 0;
            ClearDemotedHostTracking();

            GONetLog.Warning("[ViceHost] This node was demoted from host - another node has higher epoch");
        }

        /// <summary>
        /// Tracks a recently demoted host to avoid reselecting it as vice host immediately after handoff.
        /// </summary>
        public void SetDemotedHostAuthority(ushort demotedHostNewAuthorityId)
        {
            if (demotedHostNewAuthorityId == 0)
            {
                ClearDemotedHostTracking();
                return;
            }

            demotedHostAuthorityId = demotedHostNewAuthorityId;
            demotedHostDemotionRawTicks = GONetMain.Time.RawElapsedTicks;

            GONetLog.Info($"[ViceHost] Tracking demoted host authority {demotedHostNewAuthorityId} - excluded from vice host for {DEMOTED_HOST_VICE_HOST_COOLDOWN_SECONDS:0.#}s");
        }

        /// <summary>
        /// Called when this node is designated as vice host.
        /// </summary>
        public void OnDesignatedAsViceHost()
        {
            if (isViceHost) return;

            isViceHost = true;
            shadowPersistentEvents.Clear();
            lastAcknowledgedSyncSequence = 0;
            lastReceivedSyncSequence = 0;
            lastSyncReceivedRawTicks = 0;

            GONetLog.Info("[ViceHost] This node is now the designated vice host");
            OnBecameViceHost?.Invoke();
        }

        /// <summary>
        /// Called when this node is no longer the vice host.
        /// </summary>
        public void OnRemovedAsViceHost()
        {
            if (!isViceHost) return;

            isViceHost = false;
            shadowPersistentEvents.Clear();
            lastReceivedSyncSequence = 0;
            lastSyncReceivedRawTicks = 0;

            GONetLog.Info("[ViceHost] This node is no longer the vice host");
            OnStoppedBeingViceHost?.Invoke();
        }

        /// <summary>
        /// Host-side: Sets the vice host authority ID.
        /// </summary>
        public void SetViceHost(ushort authorityId)
        {
            if (!isHost)
            {
                GONetLog.Warning("[ViceHost] Cannot set vice host - not the host");
                return;
            }

            ushort previousViceHost = viceHostAuthorityId;
            viceHostAuthorityId = authorityId;
            currentSyncSequence = 0;
            lastAcknowledgedSyncSequence = 0;
            lastSyncAckReceivedRawTicks = 0;
            GONetMain.UpdateViceHostAuthority(authorityId);

            if (previousViceHost != authorityId)
            {
                GONetLog.Info($"[ViceHost] Vice host changed: {previousViceHost} -> {authorityId}");

                // Send initial full sync to new vice host
                if (authorityId != 0)
                {
                    SendFullSync();
                }
            }
        }

        private void ClearDemotedHostTracking()
        {
            demotedHostAuthorityId = 0;
            demotedHostDemotionRawTicks = 0;
        }

        #endregion

        #region Host-Side Sync

        /// <summary>
        /// Sends critical state (high-frequency) to vice host.
        /// </summary>
        private void SendCriticalSync()
        {
            if (viceHostAuthorityId == 0) return;

            var message = new ViceHostDeltaSyncMessage
            {
                HostIdentity = GONetMain.CurrentHostIdentity,
                SyncSequence = ++currentSyncSequence,
                BaseSequence = lastAcknowledgedSyncSequence,
                NewEventsData = null,
                GONetIdWatermark = gonetIdWatermark,
                RpcSequenceDeltas = new Dictionary<ushort, uint>(lastProcessedRpcSequenceByAuthority),
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };

            GONetGossipIntegration.SendViceHostDeltaSync(message, viceHostAuthorityId);

            GONetLog.Debug($"[ViceHost] Sending critical sync #{currentSyncSequence} to authority {viceHostAuthorityId}");
        }

        /// <summary>
        /// Sends full state snapshot to vice host.
        /// </summary>
        private void SendFullSync()
        {
            if (viceHostAuthorityId == 0) return;

            var message = new ViceHostFullSyncMessage
            {
                HostIdentity = GONetMain.CurrentHostIdentity,
                SyncSequence = ++currentSyncSequence,
                HostTimeElapsedSeconds = GONetMain.Time.ElapsedSeconds,
                PersistentEventCount = 0, // TODO: Serialize persistent events
                GONetIdWatermark = gonetIdWatermark,
                RpcSequenceWatermarks = new Dictionary<ushort, uint>(lastProcessedRpcSequenceByAuthority)
            };

            GONetGossipIntegration.SendViceHostFullSync(message, viceHostAuthorityId);
            GONetLog.Debug($"[ViceHost] Sending full sync #{currentSyncSequence} to authority {viceHostAuthorityId}");
        }

        /// <summary>
        /// Records a processed RPC for idempotency tracking.
        /// </summary>
        public void RecordProcessedRpc(ushort sourceAuthority, uint rpcSequence)
        {
            if (!isHost) return;

            if (!lastProcessedRpcSequenceByAuthority.TryGetValue(sourceAuthority, out uint currentMax) ||
                rpcSequence > currentMax)
            {
                lastProcessedRpcSequenceByAuthority[sourceAuthority] = rpcSequence;
            }
        }

        /// <summary>
        /// Updates GONetId watermark.
        /// </summary>
        public void UpdateGONetIdWatermark(uint highestAllocatedId)
        {
            if (highestAllocatedId > gonetIdWatermark)
            {
                gonetIdWatermark = highestAllocatedId;
            }
        }

        #endregion

        #region Vice Host-Side Receive

        private bool TryAdoptViceHostDesignation(in HostIdentity hostIdentity, string sourceTag)
        {
            if (!GONetMain.IsHostIdentityValid(in hostIdentity))
            {
                return false;
            }

            bool hostMatches = hostIdentity.HostEpoch == GONetMain.HostEpoch &&
                               hostIdentity.HostAuthorityId == GONetMain.CurrentHostIdentity.HostAuthorityId;

            if (hostMatches)
            {
                if (GONetMain.CurrentHostIdentity.ViceHostAuthorityId != hostIdentity.ViceHostAuthorityId)
                {
                    GONetMain.UpdateViceHostAuthority(hostIdentity.ViceHostAuthorityId);
                }
            }
            else
            {
                GONetMain.AdoptHostIdentity(
                    hostIdentity.HostEpoch,
                    hostIdentity.HostAuthorityId,
                    hostIdentity.ViceHostAuthorityId);
            }

            if (!hostIdentity.IsViceHost(GONetMain.MyAuthorityId))
            {
                return false;
            }

            if (!isViceHost)
            {
                GONetHostFailoverManager.Instance.OnDesignatedAsViceHost();
                OnDesignatedAsViceHost();
                GONetLog.Info($"[ViceHost] Adopted vice host designation from {sourceTag} sync");
            }

            return true;
        }

        /// <summary>
        /// Vice host receives a full sync message.
        /// </summary>
        public void OnFullSyncReceived(ViceHostFullSyncMessage message)
        {
            if (!isViceHost)
            {
                if (!TryAdoptViceHostDesignation(message.HostIdentity, "full"))
                {
                    GONetLog.Warning("[ViceHost] Received full sync but not designated as vice host");
                    return;
                }
            }

            // Update shadow state
            gonetIdWatermark = message.GONetIdWatermark;
            hostTimeOffset = message.HostTimeElapsedSeconds - GONetMain.Time.ElapsedSeconds;

            lastProcessedRpcSequenceByAuthority.Clear();
            foreach (var kvp in message.RpcSequenceWatermarks)
            {
                lastProcessedRpcSequenceByAuthority[kvp.Key] = kvp.Value;
            }

            lastReceivedSyncSequence = message.SyncSequence;
            lastSyncReceivedRawTicks = GONetMain.Time.RawElapsedTicks;

            // Acknowledge receipt
            SendSyncAcknowledgement(message.SyncSequence);

            // COMMENTED (log cleanup) - fires every ~1 second on sync, very spammy
            //GONetLog.Debug($"[ViceHost] Received full sync #{message.SyncSequence} " +
            //              $"(GONetId watermark: {gonetIdWatermark}, time offset: {hostTimeOffset:F3}s)");

            OnSyncReceived?.Invoke(message.SyncSequence);
        }

        /// <summary>
        /// Vice host receives a delta sync message.
        /// </summary>
        public void OnDeltaSyncReceived(ViceHostDeltaSyncMessage message)
        {
            if (!isViceHost)
            {
                if (!TryAdoptViceHostDesignation(message.HostIdentity, "delta"))
                {
                    GONetLog.Warning("[ViceHost] Received delta sync but not designated as vice host");
                    return;
                }
            }

            // Apply delta to shadow state
            // TODO: Apply persistent event deltas
            // TODO: Update watermarks

            lastReceivedSyncSequence = message.SyncSequence;
            lastSyncReceivedRawTicks = GONetMain.Time.RawElapsedTicks;

            SendSyncAcknowledgement(message.SyncSequence);

            //GONetLog.Debug($"[ViceHost] Received delta sync #{message.SyncSequence}"); // COMMENTED - spammy log (log cleanup)

            OnSyncReceived?.Invoke(message.SyncSequence);
        }

        /// <summary>
        /// Host receives a sync acknowledgement from the vice host.
        /// </summary>
        public void OnSyncAckReceived(ViceHostSyncAck message)
        {
            if (!isHost)
            {
                GONetLog.Warning("[ViceHost] Received sync ack but not host");
                return;
            }

            if (message.AcknowledgedSequence >= lastAcknowledgedSyncSequence)
            {
                lastAcknowledgedSyncSequence = message.AcknowledgedSequence;
            }

            lastSyncAckReceivedRawTicks = GONetMain.Time.RawElapsedTicks;

            GONetLog.Debug($"[ViceHost] Received sync ack #{message.AcknowledgedSequence}");

            OnSyncAcknowledged?.Invoke(message.AcknowledgedSequence);
        }

        /// <summary>
        /// Sends sync acknowledgement back to host.
        /// </summary>
        private void SendSyncAcknowledgement(ulong syncSequence)
        {
            var ack = new ViceHostSyncAck
            {
                AcknowledgedSequence = syncSequence
            };

            GONetGossipIntegration.SendViceHostSyncAck(ack);

            //GONetLog.Debug($"[ViceHost] Sent ack for sync #{syncSequence}"); // COMMENTED - spammy log (log cleanup)
        }

        #endregion

        #region Handoff Preparation

        /// <summary>
        /// Vice host: Prepares to take over as host.
        /// Called when handoff is initiated.
        /// </summary>
        /// <returns>True if ready for handoff</returns>
        public bool PrepareForHandoff()
        {
            if (!isViceHost)
            {
                GONetLog.Error("[ViceHost] Cannot prepare for handoff - not the vice host");
                return false;
            }

            float maxStaleSeconds = GetViceHostSyncStaleSeconds();
            if (!IsViceHostSyncFresh(maxStaleSeconds, out float secondsSinceSync, out ulong lastSequence))
            {
                string reason = lastSequence == 0
                    ? "no sync data received yet"
                    : $"last sync {secondsSinceSync:F2}s ago (max {maxStaleSeconds:F2}s)";

                GONetLog.Warning($"[ViceHost] Rejecting handoff - {reason}");
                return false;
            }

            GONetLog.Info($"[ViceHost] Preparing for handoff (last sync: #{lastSequence}, {secondsSinceSync:F2}s ago)");
            return true;
        }

        /// <summary>
        /// Vice host: Gets the handoff state to apply after becoming host.
        /// </summary>
        public ViceHostHandoffState GetHandoffState()
        {
            return new ViceHostHandoffState
            {
                LastSyncSequence = lastReceivedSyncSequence,
                GONetIdWatermark = gonetIdWatermark,
                HostTimeOffset = hostTimeOffset,
                RpcSequenceWatermarks = new Dictionary<ushort, uint>(lastProcessedRpcSequenceByAuthority),
                ShadowEventCount = shadowPersistentEvents.Count
            };
        }

        private float GetViceHostSyncStaleSeconds()
        {
            const float fallbackSeconds = 2f;
            if (GONetGlobal.Instance == null)
            {
                return fallbackSeconds;
            }

            return Mathf.Max(0.1f, GONetGlobal.Instance.viceHostSyncStaleSeconds);
        }

        private static float GetSecondsSince(long rawTicks)
        {
            if (rawTicks <= 0)
            {
                return float.PositiveInfinity;
            }

            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            long deltaTicks = nowRawTicks - rawTicks;
            if (deltaTicks < 0)
            {
                deltaTicks = 0;
            }

            return (float)(deltaTicks / (double)TimeSpan.TicksPerSecond);
        }

        /// <summary>
        /// New host: Applies the handoff state after promotion.
        /// </summary>
        public void ApplyHandoffState(ViceHostHandoffState state)
        {
            gonetIdWatermark = state.GONetIdWatermark;
            hostTimeOffset = state.HostTimeOffset;

            lastProcessedRpcSequenceByAuthority.Clear();
            foreach (var kvp in state.RpcSequenceWatermarks)
            {
                lastProcessedRpcSequenceByAuthority[kvp.Key] = kvp.Value;
            }

            GONetLog.Info($"[ViceHost] Applied handoff state (GONetId watermark: {gonetIdWatermark})");
        }

        #endregion
    }

    #region Message Types

    /// <summary>
    /// Full sync message from host to vice host.
    /// Contains complete state snapshot.
    /// </summary>
    [MemoryPackable]
    public partial class ViceHostFullSyncMessage : ITransientEvent
    {
        /// <summary>
        /// Host identity snapshot (includes vice host designation).
        /// </summary>
        public HostIdentity HostIdentity { get; set; }

        /// <summary>
        /// Monotonically increasing sync sequence.
        /// </summary>
        public ulong SyncSequence { get; set; }

        /// <summary>
        /// Host's elapsed time for offset calculation.
        /// </summary>
        public double HostTimeElapsedSeconds { get; set; }

        /// <summary>
        /// Number of persistent events included.
        /// </summary>
        public int PersistentEventCount { get; set; }

        /// <summary>
        /// Serialized persistent events (if any).
        /// </summary>
        public byte[] PersistentEventsData { get; set; }

        /// <summary>
        /// Highest allocated GONetId raw value.
        /// </summary>
        public uint GONetIdWatermark { get; set; }

        /// <summary>
        /// Last processed RPC sequence per authority.
        /// </summary>
        public Dictionary<ushort, uint> RpcSequenceWatermarks { get; set; }

        /// <summary>
        /// Timestamp when created.
        /// </summary>
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    /// <summary>
    /// Delta sync message from host to vice host.
    /// Contains only changes since last acknowledged sync.
    /// </summary>
    [MemoryPackable]
    public partial class ViceHostDeltaSyncMessage : ITransientEvent
    {
        /// <summary>
        /// Host identity snapshot (includes vice host designation).
        /// </summary>
        public HostIdentity HostIdentity { get; set; }

        /// <summary>
        /// Monotonically increasing sync sequence.
        /// </summary>
        public ulong SyncSequence { get; set; }

        /// <summary>
        /// Base sequence this delta is relative to.
        /// </summary>
        public ulong BaseSequence { get; set; }

        /// <summary>
        /// New persistent events since base.
        /// </summary>
        public byte[] NewEventsData { get; set; }

        /// <summary>
        /// Updated GONetId watermark (0 if unchanged).
        /// </summary>
        public uint GONetIdWatermark { get; set; }

        /// <summary>
        /// Updated RPC sequence watermarks (only changed entries).
        /// </summary>
        public Dictionary<ushort, uint> RpcSequenceDeltas { get; set; }

        /// <summary>
        /// Timestamp when created.
        /// </summary>
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    /// <summary>
    /// Acknowledgement from vice host to host.
    /// </summary>
    [MemoryPackable]
    public partial class ViceHostSyncAck : ITransientEvent
    {
        /// <summary>
        /// Highest acknowledged sync sequence.
        /// </summary>
        public ulong AcknowledgedSequence { get; set; }

        /// <summary>
        /// Timestamp when created.
        /// </summary>
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    #endregion

    #region State Structures

    /// <summary>
    /// Record of a persistent event in the vice host shadow state.
    /// </summary>
    public struct ViceHostPersistentEventRecord
    {
        /// <summary>
        /// Type of the event (for deserialization).
        /// </summary>
        public ViceHostEventType EventType;

        /// <summary>
        /// Serialized event data.
        /// </summary>
        public byte[] SerializedData;

        /// <summary>
        /// Original event tick for ordering.
        /// </summary>
        public long OccurredAtTicks;
    }

    /// <summary>
    /// Types of persistent events tracked for vice host.
    /// </summary>
    public enum ViceHostEventType : byte
    {
        Unknown = 0,
        Instantiate = 1,
        Despawn = 2,
        SceneLoad = 3,
        SceneUnload = 4,
        PersistentRpc = 5,
        OwnerAuthorityAssignment = 6
    }

    /// <summary>
    /// State transferred during handoff.
    /// </summary>
    public struct ViceHostHandoffState
    {
        /// <summary>
        /// Last sync sequence received.
        /// </summary>
        public ulong LastSyncSequence;

        /// <summary>
        /// GONetId allocation watermark.
        /// </summary>
        public uint GONetIdWatermark;

        /// <summary>
        /// Time offset from previous host.
        /// </summary>
        public double HostTimeOffset;

        /// <summary>
        /// RPC sequence watermarks per authority.
        /// </summary>
        public Dictionary<ushort, uint> RpcSequenceWatermarks;

        /// <summary>
        /// Number of shadow events ready.
        /// </summary>
        public int ShadowEventCount;
    }

    #endregion
}
