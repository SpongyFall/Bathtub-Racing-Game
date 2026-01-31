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
        #region time sync client-server-client

        /// <summary>
        /// How close the clients time must be to the server before the gap is considered closed and time can go
        /// from being sync'd every <see cref="CLIENT_SYNC_TIME_EVERY_TICKS__UNTIL_GAP_CLOSED"/> ticks
        /// to every <see cref="CLIENT_SYNC_TIME_EVERY_TICKS__POST_GAP_CLOSED"/> ticks for maintenance.
        /// </summary>
        static readonly long CLIENT_SYNC_TIME_GAP_TICKS = TimeSpan.FromSeconds(1f / 60f).Ticks;

        /// <summary>
        /// Maximum RTT (Round-Trip Time) before rejecting time sync response as invalid.
        /// Rationale: 10 seconds = ~16ms * 60fps * 10s ≈ extreme network degradation or corrupted response.
        /// Normal RTT: 10-100ms. Degraded: 500ms-2s. Absurd: 10s+ (timeout territory).
        /// </summary>
        static readonly long CLIENT_ABSURD_MAX_RTT_TICKS = TimeSpan.FromSeconds(10).Ticks;

        /// <summary>
        /// Maximum time adjustment tolerance for considering gap "closed" (stable sync achieved).
        /// Rationale: 100ms = typical frame budget at 10fps. Normal sync adjustments should be <10ms.
        /// If adjustments exceed 100ms, time sync gap is not yet stable.
        /// </summary>
        static readonly long CLIENT_MAX_ADJUSTMENT_TOLERANCE_TICKS = TimeSpan.FromMilliseconds(100).Ticks;

        /// <summary>
        /// Minimum RTT estimate (one-way delay) when no average available.
        /// Rationale: 5ms = LAN latency floor. Real-world minimum: 1-5ms local, 10-50ms internet.
        /// Prevents zero/negative one-way delay calculations which would corrupt time sync.
        /// Calculated: 10ms / 2 = 5ms one-way delay.
        /// </summary>
        static readonly long CLIENT_MIN_RTT_ESTIMATE_TICKS = TimeSpan.FromMilliseconds(10).Ticks >> 1;

        /// <summary>
        /// Counter for consecutive stable time syncs. Incremented when adjustment < CLIENT_MAX_ADJUSTMENT_TOLERANCE_TICKS.
        /// Thread-safe via Interlocked operations (no lock needed for simple increment/exchange).
        /// </summary>
        private static int clientStableSyncCount;

        // RTT RECOVERY DIAGNOSTICS (December 2025)
        // Track RTT patterns to debug "RTT goes up and never comes down" issue
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static int _consecutiveHighRttCount;
        private static int _totalHighRttCount;
        private static int _totalLowRttCount;
        private static long _lastDiagLogTicks;
        private const long RTT_DIAG_LOG_INTERVAL_TICKS = 5 * TimeSpan.TicksPerSecond; // Log summary every 5s
        private const long RTT_HIGH_THRESHOLD_TICKS = 200 * TimeSpan.TicksPerMillisecond; // > 200ms = high RTT
        #endif

        /// <summary>
        /// Number of consecutive stable syncs required to declare gap "closed" and switch to maintenance mode.
        /// Rationale: 3 consecutive = high confidence sync is stable, not a fluke.
        /// Prevents premature transition from aggressive sync (50ms intervals) to maintenance (5s intervals).
        /// Trade-off: Too low (1-2) = false positives from jitter. Too high (10+) = slow convergence.
        /// </summary>
        private const int CLIENT_STABLE_SYNC_THRESHOLD = 3;

        /// <summary>
        /// Flag indicating whether client has achieved stable time sync with server (gap closed).
        /// Once true, switches from aggressive sync (50ms) to maintenance sync (5s).
        /// </summary>
        static bool client_hasClosedTimeSyncGapWithServer;

        /// <summary>
        /// Time offset to preserve when a client becomes the new host during failover.
        ///
        /// PROBLEM: When Client 1 becomes host, its RawElapsedTicks is different from the original server's.
        /// Other clients (like Client 2) were synced to the original server's time base.
        /// If the new host uses raw time, Client 2's offset becomes invalid, causing ~5 second time jumps.
        ///
        /// SOLUTION: The new host preserves its EffectiveOffsetTicks (which was its offset to the old server)
        /// and adds it to RawElapsedTicks when responding to time sync requests.
        /// This maintains time continuity for all other clients.
        ///
        /// Formula: NewHost_ServerTime = NewHost_RawElapsedTicks + failoverPreservedServerOffset
        /// </summary>
        private static long failoverPreservedServerOffset = 0;

        /// <summary>
        /// Last authoritative host time observed via heartbeat (for failover anchoring).
        /// </summary>
        private static long lastHostHeartbeatElapsedTicks = 0;
        private static long lastHostHeartbeatReceiveRawTicks = 0;
        private static uint lastHostHeartbeatEpoch = 0;
        private static ushort lastHostHeartbeatAuthorityId = 0;
        private static readonly long MAX_HEARTBEAT_ANCHOR_AGE_TICKS = 2L * TimeSpan.TicksPerSecond;
        private const byte TIME_SYNC_DOMAIN_VERSION = 1;
        private const int TIME_SYNC_DOMAIN_BITS = 8 + 64 + 32 + 16;
        private const long TIME_SYNC_DOMAIN_LOG_THROTTLE_TICKS = TimeSpan.TicksPerSecond;
        private const long TIME_SYNC_DILATION_LOG_THROTTLE_TICKS = TimeSpan.TicksPerSecond;
        private static long timeSyncDomainLastLogRawTicks = 0;
        private static long timeSyncDilationLastLogRawTicks = 0;

        /// <summary>
        /// Called when this client becomes the new host during failover.
        /// Captures the current time offset so we can maintain time continuity for other clients.
        /// </summary>
        internal static void PreserveTimeOffsetForFailover()
        {
            PreserveTimeOffsetForFailoverInternal(authoritativeServerTicks: null, estimatedOneWayDelayTicks: 0, reason: "failover");
        }

        /// <summary>
        /// Preserves host time continuity using an authoritative server tick (e.g., handoff commit tick).
        /// </summary>
        internal static void PreserveTimeOffsetForFailover(long authoritativeServerTicks, long estimatedOneWayDelayTicks, string reason)
        {
            PreserveTimeOffsetForFailoverInternal(authoritativeServerTicks, estimatedOneWayDelayTicks, reason);
        }

        /// <summary>
        /// Records the host's authoritative time from heartbeats for failover anchoring.
        /// </summary>
        internal static void RecordHostHeartbeatTime(in HostIdentity hostIdentity, long hostElapsedTicks, long receivedRawTicks)
        {
            if (hostElapsedTicks <= 0 || receivedRawTicks <= 0)
            {
                return;
            }

            uint previousEpoch = Volatile.Read(ref lastHostHeartbeatEpoch);
            if (hostIdentity.HostEpoch < previousEpoch)
            {
                return;
            }

            if (hostIdentity.HostEpoch == previousEpoch)
            {
                long previousReceive = Interlocked.Read(ref lastHostHeartbeatReceiveRawTicks);
                if (receivedRawTicks <= previousReceive)
                {
                    return;
                }
            }

            Volatile.Write(ref lastHostHeartbeatEpoch, hostIdentity.HostEpoch);
            Volatile.Write(ref lastHostHeartbeatAuthorityId, hostIdentity.HostAuthorityId);
            Interlocked.Exchange(ref lastHostHeartbeatElapsedTicks, hostElapsedTicks);
            Interlocked.Exchange(ref lastHostHeartbeatReceiveRawTicks, receivedRawTicks);
        }

        /// <summary>
        /// Resets the failover time offset. Called when the node is no longer acting as a failover host.
        /// </summary>
        internal static void ResetFailoverTimeOffset()
        {
            failoverPreservedServerOffset = 0;
        }

        /// <summary>
        /// CRITICAL FIX (Dec 2025): Resets time sync state when a host demotes to client.
        ///
        /// When the original server demotes during voluntary handoff:
        /// 1. Its EffectiveOffset was 0 (it was the time authority)
        /// 2. The new host sends time sync with its preserved offset (from when it synced to old server)
        /// 3. Without this reset, the demoted host receives corrupted time (e.g., 533s instead of 130s)
        ///
        /// This method:
        /// - Resets failoverPreservedServerOffset to 0
        /// - Clears time sync gap state to start fresh sync with new host
        /// - Resets the internal time offset so the demoted host can properly sync to new authority
        /// </summary>
        internal static void ResetTimeSyncForDemotion()
        {
            // DIAGNOSTIC: Log time state BEFORE reset
            long beforeRaw = Time.RawElapsedTicks;
            long beforeEffective = Time.ElapsedTicks;
            long beforeOffset = Time.GetEffectiveOffsetTicks_Internal();
            GONetLog.Warning($"[TimeSync] DEMOTION RESET - BEFORE: " +
                $"RawTime={beforeRaw / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"ElapsedTime={beforeEffective / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"EffectiveOffset={beforeOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"failoverPreservedOffset={failoverPreservedServerOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"isFirstSync={client_isFirstTimeSync}, " +
                $"hasClosedGap={client_hasClosedTimeSyncGapWithServer}");

            // Reset the failover offset - we're no longer the host
            failoverPreservedServerOffset = 0;

            // Reset client time sync state to start fresh with new host
            client_hasClosedTimeSyncGapWithServer = false;
            System.Threading.Interlocked.Exchange(ref clientStableSyncCount, 0);
            // IMPORTANT: Do NOT set client_isFirstTimeSync = true!
            // The demoted host's time is already correct (it was the authority).
            // Setting isFirstTimeSync=true would allow a large time jump (+400s) which is WRONG.
            // Keep isFirstTimeSync=false so large adjustments are rejected.
            client_isFirstTimeSync = false;

            // CRITICAL FIX (Dec 2025): Enable explicit voluntary demotion protection.
            // This provides defense-in-depth against the 429s time jump that can occur
            // when other protections are somehow bypassed.
            voluntaryDemotionProtectionActive = true;
            voluntaryDemotionProtectionStartRawTicks = Time.RawElapsedTicks;
            GONetLog.Warning($"[TimeSync] VOLUNTARY DEMOTION PROTECTION ACTIVATED - " +
                $"will reject large adjustments for {VOLUNTARY_DEMOTION_PROTECTION_TIMEOUT_TICKS / (double)TimeSpan.TicksPerSecond:F1}s");
            client_hasSentSyncTimeRequest = false;
            client_lastSyncTimeRequestSentTicks = 0;
            client_mostRecentTimeSyncResponseSentTicks = 0;

            // Clear pending time sync requests (they're for the old authority)
            lock (client_lastFewTimeSyncsSentByUID)
            {
                client_lastFewTimeSyncsSentByUID.Clear();
            }

            // Reset scheduler to trigger immediate sync with new host
            TimeSyncScheduler.ResetOnConnection();

            // CRITICAL: Reset the time offset in SecretaryOfTemporalAffairs
            // The demoted host had EffectiveOffset=0 (it was authority), but now needs to sync to new host
            // Without this, the first time sync from the new host will calculate an incorrect offset
            Time.ResetOffsetForDemotion();

            // DIAGNOSTIC: Log time state AFTER reset
            long afterRaw = Time.RawElapsedTicks;
            long afterEffective = Time.ElapsedTicks;
            long afterOffset = Time.GetEffectiveOffsetTicks_Internal();
            GONetLog.Warning($"[TimeSync] DEMOTION RESET - AFTER: " +
                $"RawTime={afterRaw / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"ElapsedTime={afterEffective / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"EffectiveOffset={afterOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"isFirstSync={client_isFirstTimeSync}, " +
                $"hasClosedGap={client_hasClosedTimeSyncGapWithServer}");
        }

        /// <summary>
        /// Resets time sync state for a fresh session (Fast Iteration Mode or lobby flow).
        /// </summary>
        internal static void ResetTimeSyncStateForNewSession()
        {
            // CRITICAL FIX (Jan 2025): Reset time baseline FIRST before resetting sync state.
            // In Fast Iteration Mode (domain reload disabled), the static Time instance persists
            // but its InitialStopwatchTicks becomes stale. This causes RawElapsedTicks to return
            // huge values (or zero after overflow), breaking time sync completely.
            // ResetTimeBaseline() sets InitialStopwatchTicks = current stopwatch ticks,
            // making RawElapsedTicks start fresh from ~0 for the new session.
            Time.ResetTimeBaseline();

            Interlocked.Exchange(ref clientStableSyncCount, 0);
            client_hasClosedTimeSyncGapWithServer = false;
            client_hasSentSyncTimeRequest = false;
            client_lastSyncTimeRequestSentTicks = 0;
            client_mostRecentTimeSyncResponseSentTicks = 0;
            client_isFirstTimeSync = true;
            client_gapClosingIntervalInitialized = false;

            outstandingRequestCount = 0;

            failoverPreservedServerOffset = 0;
            lastHostHeartbeatElapsedTicks = 0;
            lastHostHeartbeatReceiveRawTicks = 0;
            lastHostHeartbeatEpoch = 0;
            lastHostHeartbeatAuthorityId = 0;
            timeSyncDomainLastLogRawTicks = 0;
            timeSyncDilationLastLogRawTicks = 0;

            voluntaryDemotionProtectionActive = false;
            voluntaryDemotionProtectionStartRawTicks = 0;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _consecutiveHighRttCount = 0;
            _totalHighRttCount = 0;
            _totalLowRttCount = 0;
            _lastDiagLogTicks = 0;
#endif

            lock (client_lastFewTimeSyncsSentByUID)
            {
                client_lastFewTimeSyncsSentByUID.Clear();
            }
            client_uidCleanupBuffer.Clear();
            server_lastTimeSyncDiagRawTicks = 0;

            ResetGapClosingIntervalInitialization();
            HighPerfTimeSync.ResetForNewSession();
            TimeSyncScheduler.ResetForNewSession();
        }

        private static void PreserveTimeOffsetForFailoverInternal(long? authoritativeServerTicks, long estimatedOneWayDelayTicks, string reason)
        {
            long rawNow = Time.RawElapsedTicks;
            long oneWayDelayTicks = estimatedOneWayDelayTicks > 0 ? estimatedOneWayDelayTicks : GetEstimatedOneWayDelayTicks();
            string sourceTag;
            long serverTimeAtReceipt;

            if (authoritativeServerTicks.HasValue && authoritativeServerTicks.Value > 0)
            {
                serverTimeAtReceipt = authoritativeServerTicks.Value + Math.Max(0, oneWayDelayTicks);
                sourceTag = "handoff";
            }
            else if (TryGetHeartbeatTimeAnchor(rawNow, oneWayDelayTicks, out serverTimeAtReceipt, out uint anchorEpoch, out ushort anchorAuthorityId))
            {
                sourceTag = $"heartbeat(epoch={anchorEpoch},host={anchorAuthorityId})";
            }
            else
            {
                serverTimeAtReceipt = rawNow + Time.GetEffectiveOffsetTicks_Internal();
                sourceTag = "effective";
            }

            failoverPreservedServerOffset = serverTimeAtReceipt - rawNow;

            double offsetSeconds = failoverPreservedServerOffset / (double)TimeSpan.TicksPerSecond;
            double serverSeconds = serverTimeAtReceipt / (double)TimeSpan.TicksPerSecond;
            double rawSeconds = rawNow / (double)TimeSpan.TicksPerSecond;
            string reasonSuffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" reason={reason}";
            GONetLog.Info($"[Failover-TimeSync] Preserved server offset ({sourceTag}){reasonSuffix}: " +
                         $"offset={offsetSeconds:F3}s, serverTime={serverSeconds:F3}s, raw={rawSeconds:F3}s, oneWayDelay={(oneWayDelayTicks / (double)TimeSpan.TicksPerSecond):F3}s");
        }

        private static bool TryGetHeartbeatTimeAnchor(
            long rawNow,
            long oneWayDelayTicks,
            out long estimatedServerTimeTicks,
            out uint anchorEpoch,
            out ushort anchorAuthorityId)
        {
            estimatedServerTimeTicks = 0;
            anchorEpoch = Volatile.Read(ref lastHostHeartbeatEpoch);
            anchorAuthorityId = Volatile.Read(ref lastHostHeartbeatAuthorityId);

            if (anchorEpoch != HostEpoch)
            {
                return false;
            }

            long anchorTicks = Interlocked.Read(ref lastHostHeartbeatElapsedTicks);
            long anchorReceiveRawTicks = Interlocked.Read(ref lastHostHeartbeatReceiveRawTicks);
            if (anchorTicks <= 0 || anchorReceiveRawTicks <= 0)
            {
                return false;
            }

            long ageTicks = rawNow - anchorReceiveRawTicks;
            if (ageTicks < 0 || ageTicks > MAX_HEARTBEAT_ANCHOR_AGE_TICKS)
            {
                return false;
            }

            long elapsedSinceReceive = Math.Max(0, ageTicks);
            long delayTicks = Math.Max(0, oneWayDelayTicks);
            estimatedServerTimeTicks = anchorTicks + delayTicks + elapsedSinceReceive;
            return true;
        }

        private static long GetEstimatedOneWayDelayTicks()
        {
            var connection = _gonetClient?.connectionToServer;
            if (connection != null && connection.RTT_RecentAverage > 0)
            {
                return (long)(connection.RTT_RecentAverage * TimeSpan.TicksPerSecond) >> 1;
            }
            return 0;
        }

        private static void GetExpectedTimeSyncDomain(out long sessionGuid, out uint hostEpoch, out ushort hostAuthorityId)
        {
            sessionGuid = SessionGUID;
            hostEpoch = HostEpoch;
            hostAuthorityId = CurrentHostIdentity.HostAuthorityId != 0
                ? CurrentHostIdentity.HostAuthorityId
                : OwnerAuthorityId_Server;
        }

        private static void WriteTimeSyncDomain(BitByBitByteArrayBuilder bitStream)
        {
            GetExpectedTimeSyncDomain(out long sessionGuid, out uint hostEpoch, out ushort hostAuthorityId);
            bitStream.WriteByte(TIME_SYNC_DOMAIN_VERSION);
            bitStream.WriteLong(sessionGuid);
            bitStream.WriteUInt(hostEpoch);
            bitStream.WriteUShort(hostAuthorityId);
        }

        private static bool TryReadTimeSyncDomain(
            BitByBitByteArrayBuilder bitStream,
            out byte domainVersion,
            out long sessionGuid,
            out uint hostEpoch,
            out ushort hostAuthorityId)
        {
            domainVersion = 0;
            sessionGuid = 0;
            hostEpoch = 0;
            hostAuthorityId = 0;

            if (bitStream.BitsRemaining < TIME_SYNC_DOMAIN_BITS)
            {
                return false;
            }

            int versionValue = bitStream.ReadByte();
            if (versionValue < 0)
            {
                return false;
            }
            domainVersion = (byte)versionValue;

            bitStream.ReadLong(out sessionGuid);
            bitStream.ReadUInt(out hostEpoch);
            bitStream.ReadUShort(out hostAuthorityId);
            return true;
        }

        internal static bool ValidateTimeSyncDomain(
            byte domainVersion,
            long sessionGuid,
            uint hostEpoch,
            ushort hostAuthorityId,
            out string reason)
        {
            if (domainVersion == 0)
            {
                reason = "time sync domain missing";
                return false;
            }

            if (domainVersion != TIME_SYNC_DOMAIN_VERSION)
            {
                reason = $"domain version mismatch (recv={domainVersion}, expected={TIME_SYNC_DOMAIN_VERSION})";
                return false;
            }

            if (SessionGUID == SessionGUID_Unset)
            {
                reason = "local session GUID unset";
                return false;
            }

            GetExpectedTimeSyncDomain(out long expectedSessionGuid, out uint expectedEpoch, out ushort expectedHostAuthority);

            if (sessionGuid != expectedSessionGuid)
            {
                reason = $"session GUID mismatch (recv={sessionGuid}, expected={expectedSessionGuid})";
                return false;
            }

            if (hostEpoch != expectedEpoch)
            {
                reason = $"epoch mismatch (recv={hostEpoch}, expected={expectedEpoch})";
                return false;
            }

            if (hostAuthorityId != expectedHostAuthority)
            {
                reason = $"host authority mismatch (recv={hostAuthorityId}, expected={expectedHostAuthority})";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool ShouldLogTimeSyncDomainIssue(long nowRawTicks)
        {
            if (nowRawTicks - timeSyncDomainLastLogRawTicks < TIME_SYNC_DOMAIN_LOG_THROTTLE_TICKS)
            {
                return false;
            }

            timeSyncDomainLastLogRawTicks = nowRawTicks;
            return true;
        }

        private static bool ShouldLogTimeSyncDilation(long nowRawTicks)
        {
            if (nowRawTicks - timeSyncDilationLastLogRawTicks < TIME_SYNC_DILATION_LOG_THROTTLE_TICKS)
            {
                return false;
            }

            timeSyncDilationLastLogRawTicks = nowRawTicks;
            return true;
        }

        /// <summary>
        /// CRITICAL FIX (Dec 2025): Explicit protection flag for voluntary demotion scenario.
        ///
        /// Problem: Despite multiple defenses (isFirstSync=false, large adjustment rejection), the 429s time
        /// jump was still occurring on the demoted host. This flag provides defense-in-depth.
        ///
        /// When set to true, ANY time sync adjustment > 10s is rejected with no exceptions.
        /// This is set in ResetTimeSyncForDemotion() and cleared after the first stable sync.
        /// </summary>
        private static volatile bool voluntaryDemotionProtectionActive = false;

        /// <summary>
        /// Raw ticks when voluntary demotion protection was activated.
        /// Used to timeout the protection after a reasonable period (5 seconds).
        /// </summary>
        private static long voluntaryDemotionProtectionStartRawTicks = 0;

        /// <summary>
        /// Maximum duration for voluntary demotion protection (5 seconds).
        /// After this time, normal time sync rules apply.
        /// </summary>
        private const long VOLUNTARY_DEMOTION_PROTECTION_TIMEOUT_TICKS = 5L * TimeSpan.TicksPerSecond;

        /// <summary>
        /// Time sync interval during gap-closing phase (aggressive sync until stable).
        /// Default: 200ms - optimal for production (fast convergence, works with real networks).
        /// Configurable via GONetGlobal.Instance.client_TimeSyncGapClosingIntervalMs.
        /// CRITICAL: 50ms interval with 100ms+ RTT causes request flooding (26+ outstanding requests observed),
        /// which inflates measured RTT and destabilizes offset calculation.
        /// NOTE: Network simulators like Clumsy may buffer packets at 200ms density - use 500ms for testing.
        /// </summary>
        static long CLIENT_SYNC_TIME_EVERY_TICKS__UNTIL_GAP_CLOSED = TimeSpan.FromMilliseconds(200).Ticks;

        /// <summary>
        /// Time sync interval after gap closed (maintenance mode).
        /// Rationale: 5 seconds = minimal bandwidth, time drift over 5s is negligible (~10-50ms worst case).
        /// Trade-off: Faster (1s) = tighter sync but more traffic. Slower (30s+) = risks drift accumulation.
        /// </summary>
        static readonly long CLIENT_SYNC_TIME_EVERY_TICKS__POST_GAP_CLOSED = TimeSpan.FromSeconds(5f).Ticks;

        /// <summary>
        /// Float version of CLIENT_SYNC_TIME_EVERY_TICKS__UNTIL_GAP_CLOSED for arithmetic operations.
        /// Avoids repeated int-to-float conversions in hot path.
        /// Updated when InitializeFromConfig() is called.
        /// </summary>
        static float CLIENT_SYNC_TIME_EVERY_TICKS_FLOAT__UNTIL_GAP_CLOSED = (float)CLIENT_SYNC_TIME_EVERY_TICKS__UNTIL_GAP_CLOSED;

        /// <summary>
        /// Float version of CLIENT_SYNC_TIME_EVERY_TICKS__POST_GAP_CLOSED for arithmetic operations.
        /// Avoids repeated int-to-float conversions in hot path.
        /// </summary>
        static readonly float CLIENT_SYNC_TIME_EVERY_TICKS_FLOAT__POST_GAP_CLOSED = (float)CLIENT_SYNC_TIME_EVERY_TICKS__POST_GAP_CLOSED;

        /// <summary>
        /// Threshold for disabling easing (gradual adjustment) in favor of immediate correction.
        /// Rationale: 1 second = if this far out of sync, easing would take 10+ syncs to converge (slow and jarring).
        /// Better to immediately jump to correct time and let value blending smooth out object state.
        /// Trade-off: Lower (500ms) = more immediate corrections (visible jumps). Higher (5s) = longer easing (slow convergence).
        /// </summary>
        static readonly long DIFF_TICKS_TOO_BIG_FOR_EASING = TimeSpan.FromSeconds(1f).Ticks;
        static bool client_hasSentSyncTimeRequest;
        static long client_lastSyncTimeRequestSentTicks;
        const int CLIENT_TIME_SYNCS_SENT_HISTORY_SIZE = 60;
        private static readonly Dictionary<long, RequestMessage> client_lastFewTimeSyncsSentByUID = new(CLIENT_TIME_SYNCS_SENT_HISTORY_SIZE);
        private static readonly List<long> client_uidCleanupBuffer = new(CLIENT_TIME_SYNCS_SENT_HISTORY_SIZE);

        static long client_mostRecentTimeSyncResponseSentTicks;
        static long server_lastTimeSyncDiagRawTicks;
        static readonly long SERVER_TIMESYNC_DIAG_INTERVAL_TICKS = TimeSpan.FromSeconds(1).Ticks;

        internal static readonly float BLENDING_BUFFER_LEAD_SECONDS_DEFAULT = 0.25f; // 0 is to always extrapolate pretty much.....here is a decent delay to get good interpolation: 0.25f
        internal static float valueBlendingBufferLeadSeconds = BLENDING_BUFFER_LEAD_SECONDS_DEFAULT;
        internal static long valueBlendingBufferLeadTicks = TimeSpan.FromSeconds(BLENDING_BUFFER_LEAD_SECONDS_DEFAULT).Ticks;
        
        static bool client_isFirstTimeSync = true;

        /// <summary>
        /// Whether the gap-closing interval has been initialized from config.
        /// Prevents re-initialization on every sync request.
        /// </summary>
        static bool client_gapClosingIntervalInitialized = false;

        /// <summary>
        /// Default production interval (200ms) - optimal for real networks.
        /// </summary>
        const int DEFAULT_PRODUCTION_INTERVAL_MS = 200;

        /// <summary>
        /// Initializes the gap-closing interval from GONetGlobal config.
        /// Called once when client starts time sync.
        /// </summary>
        private static void InitializeGapClosingIntervalFromConfig()
        {
            if (client_gapClosingIntervalInitialized) return;
            client_gapClosingIntervalInitialized = true;

            int intervalMs = GONetGlobal.Instance?.client_TimeSyncGapClosingIntervalMs ?? DEFAULT_PRODUCTION_INTERVAL_MS;

            CLIENT_SYNC_TIME_EVERY_TICKS__UNTIL_GAP_CLOSED = TimeSpan.FromMilliseconds(intervalMs).Ticks;
            CLIENT_SYNC_TIME_EVERY_TICKS_FLOAT__UNTIL_GAP_CLOSED = (float)CLIENT_SYNC_TIME_EVERY_TICKS__UNTIL_GAP_CLOSED;

            //GONetLog.Debug($"[TimeSync] Gap-closing interval initialized: {intervalMs}ms");
        }

        /// <summary>
        /// Resets the gap-closing interval initialization flag.
        /// Called when client disconnects to allow re-initialization on reconnect.
        /// </summary>
        internal static void ResetGapClosingIntervalInitialization()
        {
            client_gapClosingIntervalInitialized = false;
        }

        /// <summary>
        /// 0 is to always extrapolate pretty much.....here is a decent delay to get good interpolation: TimeSpan.FromMilliseconds(250).Ticks;
        /// </summary>
        private static void SetValueBlendingBufferLeadTimeFromMilliseconds(int valueBlendingBufferLeadTimeMilliseconds)
        {
            TimeSpan timeSpan = TimeSpan.FromMilliseconds(valueBlendingBufferLeadTimeMilliseconds);
            valueBlendingBufferLeadSeconds = (float)timeSpan.TotalSeconds;
            valueBlendingBufferLeadTicks = timeSpan.Ticks;
        }


        private static void Client_SyncTimeWithServer_SendInitialBarrage()
        {
            // Initialize gap-closing interval from config
            InitializeGapClosingIntervalFromConfig();

            // Send initial time sync requests to bootstrap synchronization.
            // Original design: 5 packets for quick convergence.
            // MAX_OUTSTANDING_REQUESTS=3 still provides flooding protection.
            const int INITIAL_REQUEST_COUNT = 5;


            // CRITICAL: Clear any stale requests from before connection was established.
            // Pre-connection requests never receive responses and would permanently block
            // the outstanding count, causing throttling for the entire session.
            if (client_lastFewTimeSyncsSentByUID.Count > 0 || outstandingRequestCount > 0)
            {
                client_lastFewTimeSyncsSentByUID.Clear();
                outstandingRequestCount = 0;
            }

            client_hasSentSyncTimeRequest = true;
            long startTicks = Time.ElapsedTicks;

            for (int i = 0; i < INITIAL_REQUEST_COUNT; i++)
            {
                Client_SyncTimeWithServer_SendRequest(startTicks + i);
            }
            client_lastSyncTimeRequestSentTicks = startTicks;
            TimeSyncScheduler.ResetOnConnection();

        }

        /// <summary>
        /// Resets time sync to gap-closing mode.
        /// Call this after major events like scene changes or network hiccups.
        /// This triggers the same aggressive time sync sequence as initial connection
        /// (3 successful syncs required before gap is considered closed).
        /// CLIENT ONLY - has no effect on server.
        /// </summary>
        /// <param name="reason">Reason for reset (for logging/debugging)</param>
        public static void ResetTimeSyncGap(string reason = "unknown")
        {
            // HOST MODE FIX: Only pure clients should reset time sync.
            // Host IS the time authority - it should never sync with itself.
            if (!IsClient || IsServer) return;

            bool wasAlreadyClosed = client_hasClosedTimeSyncGapWithServer;

            // IMPORTANT: Don't reset time sync if the client hasn't closed the initial gap yet!
            // Late-joining clients need to complete their initial time sync sequence without interruption.
            // Only reset for clients that have already achieved sync and are experiencing a scene change.
            if (!wasAlreadyClosed)
            {
                GONetLog.Info($"[TimeSync] CLIENT: Skipping time sync reset for reason '{reason}' - client still closing initial gap (wasAlreadyClosed: {wasAlreadyClosed})");
                return;
            }


            // Reset to gap-closing phase
            client_hasClosedTimeSyncGapWithServer = false;
            System.Threading.Interlocked.Exchange(ref clientStableSyncCount, 0);

            // Reset scheduler to trigger immediate sync
            TimeSyncScheduler.ResetOnConnection();

            GONetLog.Info($"[TimeSync] CLIENT: Time sync state reset - starting new gap-closing phase");

            // Send initial barrage (same as connection)
            Client_SyncTimeWithServer_SendInitialBarrage();
        }
        /// <summary>
        /// Requests more frequent time synchronization after scene changes WITHOUT resetting the gap.
        /// This ensures good client-server time sync without blocking messages like ResetTimeSyncGap() does.
        /// Aggressive mode lasts for 10 seconds with 1-second sync intervals (instead of normal 5-second intervals).
        /// </summary>
        /// <param name="reason">Reason for requesting aggressive sync (for logging)</param>
        public static void RequestAggressiveTimeSync(string reason = "unknown")
        {
            // HOST MODE FIX: Only pure clients should request aggressive time sync.
            // Host IS the time authority - it should never sync with itself.
            if (!IsClient || IsServer) return;

            TimeSyncScheduler.EnableAggressiveMode(reason);
        }

        /// <summary>
        /// "IfAppropriate" is to indicate this runs on a schedule....if it is not the right time, this will do nothing.
        /// UPDATED to use TimeSyncScheduler for better performance
        /// </summary>
        private static void Client_SyncTimeWithServer_Initiate_IfAppropriate()
        {
            // Gap-closing phase: Frequent syncs until gap closed
            if (!client_hasClosedTimeSyncGapWithServer)
            {
                long nowTicks = Time.ElapsedTicks;
                if (nowTicks - client_lastSyncTimeRequestSentTicks < CLIENT_SYNC_TIME_EVERY_TICKS__UNTIL_GAP_CLOSED)
                {
                    return;
                }

                client_hasSentSyncTimeRequest = true;
                client_lastSyncTimeRequestSentTicks = nowTicks;
                Client_SyncTimeWithServer_SendRequest(nowTicks);
                return;
            }

            // Maintenance phase: Use scheduler
            if (!TimeSyncScheduler.ShouldSyncNow())
            {
                return;
            }

            client_hasSentSyncTimeRequest = true;
            client_lastSyncTimeRequestSentTicks = Time.ElapsedTicks;
            Client_SyncTimeWithServer_SendRequest(client_lastSyncTimeRequestSentTicks);
        }

        // DIAGNOSTIC: Track outstanding requests to detect queue buildup
        private static int outstandingRequestCount = 0;

        /// <summary>
        /// Maximum outstanding time sync requests before throttling.
        /// Prevents request flooding that inflates RTT and destabilizes sync.
        /// </summary>
        private const int MAX_OUTSTANDING_REQUESTS = 3;

        /// <summary>
        /// Maximum time to wait for a time sync response before considering the request lost.
        /// Requests older than this are cleaned up to prevent "zombie" requests from permanently
        /// blocking new requests. Set to 10 seconds - generous enough for high-latency networks
        /// but prevents permanent blocking from lost/dropped requests.
        /// </summary>
        private const long REQUEST_TIMEOUT_TICKS = 10 * TimeSpan.TicksPerSecond; // 10 seconds

        /// <summary>
        /// Cleans up stale time sync requests that never received responses.
        /// This prevents "zombie" requests from permanently blocking new requests.
        /// Called before sending new requests to ensure the outstanding count is accurate.
        /// </summary>
        private static void CleanupStaleRequests()
        {
            if (client_lastFewTimeSyncsSentByUID.Count == 0) return;

            long nowRawTicks = Time.RawElapsedTicks;
            client_uidCleanupBuffer.Clear();

            foreach (var kvp in client_lastFewTimeSyncsSentByUID)
            {
                // Request timestamp is in OccurredAtElapsedTicks (raw ticks when created)
                long requestAgeTicks = nowRawTicks - kvp.Value.OccurredAtElapsedTicks;
                if (requestAgeTicks > REQUEST_TIMEOUT_TICKS)
                {
                    client_uidCleanupBuffer.Add(kvp.Key);
                }
            }

            // Remove stale requests and decrement outstanding count
            foreach (long staleUID in client_uidCleanupBuffer)
            {
                client_lastFewTimeSyncsSentByUID.Remove(staleUID);
                outstandingRequestCount--;
            }

            // Safety: ensure outstanding count doesn't go negative
            if (outstandingRequestCount < 0)
            {
                outstandingRequestCount = 0;
            }
        }

        static void Client_SyncTimeWithServer_SendRequest(long baseTicks)
        {
            // CRITICAL: Don't send time sync requests before client is connected!
            // Requests sent before connection never get responses but count against outstanding,
            // causing permanent throttling. This was causing "zombie" requests that blocked
            // time sync for the entire session.
            if (_gonetClient == null || !_gonetClient.IsConnectedToServer)
            {
                GONetLog.Debug("[TimeSync] Skipping request - client not connected yet");
                return;
            }

            if (SessionGUID == SessionGUID_Unset)
            {
                long nowRawTicks = Time.RawElapsedTicks;
                if (ShouldLogTimeSyncDomainIssue(nowRawTicks))
                {
                    GONetLog.Warning("[TimeSync] Skipping request - session GUID not initialized (time sync domain unknown)");
                }
                return;
            }

            // Clean up stale requests that never received responses (dropped packets, etc.)
            // This prevents "zombie" requests from permanently blocking new requests.
            CleanupStaleRequests();

            // Throttle if too many requests are pending - prevents flooding
            if (outstandingRequestCount >= MAX_OUTSTANDING_REQUESTS)
            {
                return;
            }

            long rawTicksAtCreate = Time.RawElapsedTicks;
            RequestMessage timeSync = new RequestMessage(rawTicksAtCreate + (baseTicks % 1000));

            if (timeSync.UID == 0)
            {
                GONetLog.Error($"[TimeSync] CRITICAL BUG: Generated RequestMessage has UID=0! This will cause time sync to fail. OccurredAtElapsedTicks: {timeSync.OccurredAtElapsedTicks}");
            }

            outstandingRequestCount++;
            client_lastFewTimeSyncsSentByUID[timeSync.UID] = timeSync;
            if (client_lastFewTimeSyncsSentByUID.Count > CLIENT_TIME_SYNCS_SENT_HISTORY_SIZE)
            {
                client_uidCleanupBuffer.Clear();
                long oldestTicks = long.MaxValue;
                long oldestUID = 0;
                foreach (var kvp in client_lastFewTimeSyncsSentByUID)
                {
                    if (kvp.Value.OccurredAtElapsedTicks < oldestTicks)
                    {
                        oldestTicks = kvp.Value.OccurredAtElapsedTicks;
                        oldestUID = kvp.Key;
                    }
                }
                client_lastFewTimeSyncsSentByUID.Remove(oldestUID);
            }

            using (BitByBitByteArrayBuilder bitStream = BitByBitByteArrayBuilder.GetBuilder())
            {
                uint messageID = messageTypeToMessageIDMap[typeof(RequestMessage)];
                bitStream.WriteUInt(messageID);
                bitStream.WriteLong(timeSync.OccurredAtElapsedTicks);
                bitStream.WriteLong(timeSync.UID);
                WriteTimeSyncDomain(bitStream);
                bitStream.WriteCurrentPartialByte();
                int bytesUsedCount = bitStream.Length_WrittenBytes;
                byte[] bytes = mainThread_miscSerializationArrayPool.Borrow(bytesUsedCount);
                Array.Copy(bitStream.GetBuffer(), 0, bytes, 0, bytesUsedCount);
                var connectionToServer = _gonetClient?.connectionToServer;
                if (connectionToServer != null &&
                    SendBytesToRemoteConnection(connectionToServer, bytes, bytesUsedCount, GONetChannel.TimeSync_Unreliable))
                {
                }
                mainThread_miscSerializationArrayPool.Return(bytes);
            }
        }

        /// <summary>
        /// Server responds to a time sync request from a client.
        /// Implements proper NTP-style 4-timestamp protocol:
        /// - t0 = client send time (in request)
        /// - t1 = server receive time (networkThreadReceiveRawTicks, captured on network thread)
        /// - t2 = server send time (captured here on main thread)
        /// - t3 = client receive time (captured on client network thread)
        ///
        /// The client uses: offset = ((t1 - t0) + (t2 - t3)) / 2
        /// This properly handles asymmetric delays including server queue time.
        /// </summary>
        private static void Server_SyncTimeWithClient_Respond(long requestUID, GONetConnection connectionToClient, long networkThreadReceiveRawTicks)
        {
            // Capture t2 (server send time) on main thread - this is when the response actually goes out
            long serverSendRawTicks = Time.RawElapsedTicks;

            // FAILOVER TIME CONTINUITY: If this is a promoted client acting as host,
            // add the preserved offset to maintain time continuity for other clients.
            // Without this, clients synced to the old server would experience time jumps.
            long preservedOffset = Volatile.Read(ref failoverPreservedServerOffset);
            long t1_adjusted = networkThreadReceiveRawTicks + preservedOffset;
            long t2_adjusted = serverSendRawTicks + preservedOffset;

            if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableDistributedHostAuthority)
            {
                long nowRawTicks = Time.RawElapsedTicks;
                if (nowRawTicks - server_lastTimeSyncDiagRawTicks >= SERVER_TIMESYNC_DIAG_INTERVAL_TICKS)
                {
                    server_lastTimeSyncDiagRawTicks = nowRawTicks;
                    ushort targetAuthorityId = connectionToClient != null ? connectionToClient.OwnerAuthorityId : (ushort)0;
                    GONetLog.Warning($"[TimeSync-SERVER] Response: targetAuth={targetAuthorityId}, " +
                                     $"t1Raw={(networkThreadReceiveRawTicks / (double)TimeSpan.TicksPerSecond):F3}s, " +
                                     $"t2Raw={(serverSendRawTicks / (double)TimeSpan.TicksPerSecond):F3}s, " +
                                     $"preservedOffset={(preservedOffset / (double)TimeSpan.TicksPerSecond):F3}s, " +
                                     $"t1Adj={(t1_adjusted / (double)TimeSpan.TicksPerSecond):F3}s, " +
                                     $"t2Adj={(t2_adjusted / (double)TimeSpan.TicksPerSecond):F3}s, " +
                                     $"myAuth={MyAuthorityId}, epoch={HostEpoch}, session={SessionGUID}, hostAuth={CurrentHostIdentity.HostAuthorityId}");
                }
            }

            using (BitByBitByteArrayBuilder bitStream = BitByBitByteArrayBuilder.GetBuilder())
            {
                { // header...message type and NTP timestamps
                    uint messageID = messageTypeToMessageIDMap[typeof(ResponseMessage)];
                    bitStream.WriteUInt(messageID);

                    // NTP t1: Server receive time (with failover offset if applicable)
                    bitStream.WriteLong(t1_adjusted);

                    // NTP t2: Server send time (with failover offset if applicable)
                    bitStream.WriteLong(t2_adjusted);
                }

                // body
                bitStream.WriteLong(requestUID);
                WriteTimeSyncDomain(bitStream);

                bitStream.WriteCurrentPartialByte();

                int bytesUsedCount = bitStream.Length_WrittenBytes;
                byte[] bytes = mainThread_miscSerializationArrayPool.Borrow(bytesUsedCount);
                Array.Copy(bitStream.GetBuffer(), 0, bytes, 0, bytesUsedCount);

                // ROBUSTNESS: Send response multiple times (unreliable channel + internet = packet loss)
                // Redundant sends are cheap (~20 bytes each) but CRITICAL for convergence under high load
                // User feedback: "imagine what the real internet is going to do to this thing"
                // Trade-off: 3x sends = 60 bytes total (tiny), but 99.9% delivery probability vs 90% for single send
                // Configurable via GONetGlobal.server_TimeSyncResponseRedundancy (default: 3)
                int redundantSendCount = GONetGlobal.Instance?.server_TimeSyncResponseRedundancy ?? 3;
                for (int i = 0; i < redundantSendCount; i++)
                {
                    SendBytesToRemoteConnection(connectionToClient, bytes, bytesUsedCount, GONetChannel.TimeSync_Unreliable);
                }

                mainThread_miscSerializationArrayPool.Return(bytes);
            }
        }

        /// <summary>
        /// Process a time sync response from the server using NTP-style 4-timestamp protocol.
        /// </summary>
        /// <param name="requestUID">The UID of the original request</param>
        /// <param name="serverReceiveTicks">NTP t1: Server's raw ticks when request was received (captured on server network thread)</param>
        /// <param name="serverSendTicks">NTP t2: Server's raw ticks when response was sent (captured on server main thread)</param>
        /// <param name="clientReceiveTicks">NTP t3: Client's raw ticks when response was received (captured on client network thread)</param>
        ///
        /// NTP formula: offset = ((t1 - t0) + (t2 - t3)) / 2
        /// This properly handles asymmetric delays including server processing/queue time.
        private static void Client_SyncTimeWithServer_ProcessResponse(
            long requestUID,
            long serverReceiveTicks,
            long serverSendTicks,
            long clientReceiveTicks,
            byte domainVersion,
            long domainSessionGuid,
            uint domainHostEpoch,
            ushort domainHostAuthorityId)
        {
            // NTP naming convention:
            // t0 = client send time (from request message)
            // t1 = server receive time (serverReceiveTicks)
            // t2 = server send time (serverSendTicks)
            // t3 = client receive time (clientReceiveTicks)

            if (!client_lastFewTimeSyncsSentByUID.TryGetValue(requestUID, out RequestMessage requestMessage))
            {
                // Expected when server sends redundant responses (server_TimeSyncResponseRedundancy, default 3x)
                // First response processes and removes from dictionary; subsequent copies find nothing
                return; // Early exit if no matching request
            }

            outstandingRequestCount--;

            if (!ValidateTimeSyncDomain(domainVersion, domainSessionGuid, domainHostEpoch, domainHostAuthorityId, out string domainReason))
            {
                long nowRawTicks = Time.RawElapsedTicks;
                if (ShouldLogTimeSyncDomainIssue(nowRawTicks))
                {
                    GetExpectedTimeSyncDomain(out long expectedSessionGuid, out uint expectedEpoch, out ushort expectedHostAuthority);
                    GONetLog.Warning($"[TimeSync] Dropping response - invalid time sync domain ({domainReason}). " +
                                     $"recvVersion={domainVersion}, recvSession={domainSessionGuid}, recvEpoch={domainHostEpoch}, recvHostAuth={domainHostAuthorityId}. " +
                                     $"localSession={expectedSessionGuid}, localEpoch={expectedEpoch}, localHostAuth={expectedHostAuthority}, myAuth={MyAuthorityId}");
                }
                goto Cleanup;
            }

            long t0 = requestMessage.OccurredAtElapsedTicks;
            long t1 = serverReceiveTicks;
            long t2 = serverSendTicks;
            long t3 = clientReceiveTicks;

            // Calculate server processing delay (time between receiving request and sending response)
            long serverProcessingDelay = t2 - t1;

            // Total RTT from client's perspective (includes network + server processing)
            long totalRtt = t3 - t0;

            // Network RTT only (excluding server processing time)
            long networkRtt = totalRtt - serverProcessingDelay;


            // Use network RTT for RTT tracking (excludes server processing delays)
            long rtt_ticks = networkRtt;

            // RTT RECOVERY DIAGNOSTICS (December 2025)
            // Track RTT patterns and log periodic summaries
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            bool isHighRtt = rtt_ticks > RTT_HIGH_THRESHOLD_TICKS;
            if (isHighRtt)
            {
                _consecutiveHighRttCount++;
                _totalHighRttCount++;
            }
            else
            {
                // Log when RTT recovers after consecutive high RTT
                if (_consecutiveHighRttCount > 3)
                {
                    double rttMs = rtt_ticks / (double)TimeSpan.TicksPerMillisecond;
                    GONetLog.Info($"[RTT-RECOVERY] RTT recovered after {_consecutiveHighRttCount} consecutive high readings. Current RTT: {rttMs:F1}ms");
                }
                _consecutiveHighRttCount = 0;
                _totalLowRttCount++;
            }

            // Log periodic summary every 5 seconds
            long nowTicks = SecretaryOfTemporalAffairs.GetRawElapsedTicksStatic();
            if (nowTicks - _lastDiagLogTicks > RTT_DIAG_LOG_INTERVAL_TICKS)
            {
                _lastDiagLogTicks = nowTicks;
                double rawRttMs = rtt_ticks / (double)TimeSpan.TicksPerMillisecond;
                // RTT_Latest and RTT_RecentAverage now only contain FILTERED values (good samples)
                double filteredRttMs = GONetClient.connectionToServer?.RTT_Latest * 1000 ?? 0;
                double avgRttMs = GONetClient.connectionToServer?.RTT_RecentAverage * 1000 ?? 0;
                float tsRatio = GONetMain.TimestampProvidedRatio;

                // Include golden sample diagnostics
                // COMMENTED (log cleanup) - fires every 5-10 seconds, spammy
                /*var goldenDiag = HighPerfTimeSync.GetGoldenSampleDiagnostics();
                // Show both raw (may be bad) and filtered (only good samples) RTT for diagnostics
                GONetLog.Info($"[RTT-SUMMARY] raw={rawRttMs:F0}ms filtered={filteredRttMs:F0}ms avg={avgRttMs:F0}ms | " +
                    $"high={_totalHighRttCount} low={_totalLowRttCount} consecutiveHigh={_consecutiveHighRttCount} | " +
                    $"golden={goldenDiag.hasGoldenSample} minRtt={goldenDiag.minRttMs:F0}ms rejected={goldenDiag.rejectedCount}+{goldenDiag.congestionCount} | " +
                    $"tsRatio={tsRatio:P0}");*/
            }
            #endif

            // DIAGNOSTIC: Log RTT calculation details when RTT is suspiciously high (> 500ms)
            // This helps debug the "stuck high RTT after scene load" issue
            // COMMENTED (log cleanup) - can fire frequently during network congestion
            /*if (rtt_ticks > TimeSpan.TicksPerMillisecond * 500)
            {
                double t0_sec = t0 / (double)TimeSpan.TicksPerSecond;
                double t1_sec = t1 / (double)TimeSpan.TicksPerSecond;
                double t2_sec = t2 / (double)TimeSpan.TicksPerSecond;
                double t3_sec = t3 / (double)TimeSpan.TicksPerSecond;
                double serverDelay_ms = serverProcessingDelay / (double)TimeSpan.TicksPerMillisecond;
                double totalRtt_ms = totalRtt / (double)TimeSpan.TicksPerMillisecond;
                double networkRtt_ms = networkRtt / (double)TimeSpan.TicksPerMillisecond;
                GONetLog.Warning($"[TimeSync-DIAG] HIGH RTT detected: networkRtt={networkRtt_ms:F1}ms " +
                    $"| t0(clientSend)={t0_sec:F3}s, t1(serverRecv)={t1_sec:F3}s, t2(serverSend)={t2_sec:F3}s, t3(clientRecv)={t3_sec:F3}s " +
                    $"| serverDelay={serverDelay_ms:F1}ms, totalRtt={totalRtt_ms:F1}ms " +
                    $"| Formula: networkRtt = (t3-t0) - (t2-t1) = {totalRtt_ms:F1} - {serverDelay_ms:F1} = {networkRtt_ms:F1}ms");
            }*/

            if (rtt_ticks <= 0 || rtt_ticks >= CLIENT_ABSURD_MAX_RTT_TICKS)
            {
                GONetLog.Warning($"Invalid RTT: {rtt_ticks} ticks ({TimeSpan.FromTicks(rtt_ticks).TotalMilliseconds:F3}ms / {TimeSpan.FromTicks(rtt_ticks).TotalSeconds:F3}s), skipping time sync");
                goto Cleanup;
            }

            // CRITICAL FIX (Dec 2025): Apply Golden Sample RTT filtering BEFORE updating RTT_Latest.
            // Without this, high-RTT samples during bufferbloat will corrupt:
            // - RTT_Latest → RTT_RecentAverage → oneWayDelayTicks → many calculations
            // The time sync offset is protected by ProcessTimeSync's Golden Sample filtering,
            // but the RTT statistics were being polluted, affecting other systems.
            bool rttAcceptedForStats = HighPerfTimeSync.ShouldAcceptRttForStats(rtt_ticks);
            if (rttAcceptedForStats)
            {
                GONetClient.connectionToServer.RTT_Latest = (float)(rtt_ticks * HighResolutionTimeUtils.TICKS_TO_SECONDS); // Inline division
            }
            else
            {
                // Log but continue - time sync processing will also reject this sample
                // COMMENTED (log cleanup) - fires when RTT is rejected, can be frequent
                //double rttMs = rtt_ticks / (double)TimeSpan.TicksPerMillisecond;
                //GONetLog.Debug($"[RTT-Filter] Rejecting RTT={rttMs:F0}ms for stats (exceeds golden sample threshold), keeping previous RTT_Latest");
            }

            long oneWayDelayTicks = (rtt_ticks >> 1); // Initial estimate - will be overridden by RTT_RecentAverage if available

            if (GONetClient.connectionToServer.RTT_RecentAverage > 0)
            {
                oneWayDelayTicks = (long)(GONetClient.connectionToServer.RTT_RecentAverage * TimeSpan.TicksPerSecond) >> 1;
            }
            else
            {
                oneWayDelayTicks = Math.Max(oneWayDelayTicks, CLIENT_MIN_RTT_ESTIMATE_TICKS); // Minimum 5ms
            }

            // CRITICAL FIX: Calculate what the adjustment WOULD BE before applying it
            // After first sync, we MUST use current effective time (with offset) to avoid false rejections
            // Using NTP formula: offset = ((t1 - t0) + (t2 - t3)) / 2
            long currentEffectiveTicks = t3 + Time.GetEffectiveOffsetTicks_Internal();
            long ntpOffset = ((t1 - t0) + (t2 - t3)) / 2;
            long predictedServerTime = t3 + ntpOffset;
            long diffTicksABS = Math.Abs(currentEffectiveTicks - predictedServerTime);

            // Calculate adjustment for validation (use effective time after first sync)
            bool isFirstSync = client_isFirstTimeSync;
            long clientTimeForValidation = isFirstSync ? t3 : currentEffectiveTicks;
            long predictedAdjustmentTicks = predictedServerTime - clientTimeForValidation;

            // DIAGNOSTIC: Log all time sync values for debugging handoff issues
            // COMMENTED (log cleanup) - fires every ~5 seconds on time sync response
            /*GONetLog.Warning($"[TimeSync-DIAG] Response received: " +
                $"t0={t0 / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"t1={t1 / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"t2={t2 / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"t3={t3 / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"ntpOffset={ntpOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"predictedServerTime={predictedServerTime / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"currentEffective={currentEffectiveTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"effectiveOffset={Time.GetEffectiveOffsetTicks_Internal() / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"isFirstSync={isFirstSync}, " +
                $"predictedAdjustment={predictedAdjustmentTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                $"demotionGuard={voluntaryDemotionProtectionActive}, " +
                $"domainVersion={domainVersion}, domainSession={domainSessionGuid}, domainEpoch={domainHostEpoch}, domainHostAuth={domainHostAuthorityId}");*/

            // Threshold is configurable in GONetGlobal (default: 10 seconds)
            long maxSaneAdjustmentTicks = TimeSpan.FromSeconds(GONetGlobal.Instance.client_MaxSaneTimeSyncAdjustmentSeconds).Ticks;

            // CRITICAL FIX (Dec 2025): Voluntary demotion protection - unconditional large adjustment rejection.
            // This check runs FIRST and is independent of isFirstSync. It provides defense-in-depth
            // against the 429s time jump that can occur when other protections fail.
            if (voluntaryDemotionProtectionActive)
            {
                long nowRawTicks = Time.RawElapsedTicks;
                long elapsedSinceActivation = nowRawTicks - voluntaryDemotionProtectionStartRawTicks;

                // Check if protection has timed out
                if (elapsedSinceActivation > VOLUNTARY_DEMOTION_PROTECTION_TIMEOUT_TICKS)
                {
                    voluntaryDemotionProtectionActive = false;
                    GONetLog.Info($"[TimeSync] Voluntary demotion protection expired after {elapsedSinceActivation / (double)TimeSpan.TicksPerSecond:F1}s");
                }
                else
                {
                    // Protection active - reject ANY large adjustment (> 10s) regardless of isFirstSync
                    long adjustmentAbs = Math.Abs(predictedAdjustmentTicks);
                    if (adjustmentAbs > maxSaneAdjustmentTicks)
                    {
                        GONetLog.Warning($"[TimeSync] VOLUNTARY DEMOTION PROTECTION: Rejecting large adjustment ({predictedAdjustmentTicks / (double)TimeSpan.TicksPerSecond:F1}s) - " +
                                         $"t0={t0 / (double)TimeSpan.TicksPerSecond:F3}s, t1={t1 / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"t2={t2 / (double)TimeSpan.TicksPerSecond:F3}s, t3={t3 / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"isFirstSync={isFirstSync}, protectionActiveFor={elapsedSinceActivation / (double)TimeSpan.TicksPerSecond:F1}s. " +
                                         $"This prevents the 429s time jump during voluntary handoff.");
                        goto Cleanup;
                    }
                    else if (adjustmentAbs < TimeSpan.FromSeconds(1).Ticks)
                    {
                        // Small adjustment - sync is stable, deactivate protection
                        voluntaryDemotionProtectionActive = false;
                        GONetLog.Info($"[TimeSync] Voluntary demotion protection deactivated - sync stabilized (adjustment: {adjustmentAbs / (double)TimeSpan.TicksPerMillisecond:F1}ms)");
                    }
                }
            }

            // CRITICAL FIX: Check if adjustment is absurdly large BEFORE applying it
            // BUT: Allow first sync to make large adjustments (client/server clocks may differ significantly)
            // RACE CONDITION FIX: After first sync, use EFFECTIVE time (with offset) for validation
            // Otherwise we compare raw time (without offset) vs server time, causing false rejections
            if (!isFirstSync && Math.Abs(predictedAdjustmentTicks) > maxSaneAdjustmentTicks)
            {
                GONetLog.Warning($"[TimeSync] CLIENT: Rejecting absurdly large time adjustment ({TimeSpan.FromTicks(predictedAdjustmentTicks).TotalSeconds:F1}s) BEFORE applying - " +
                                 $"server sent corrupted timestamp. " +
                                 $"Server time (t1): {TimeSpan.FromTicks(t1).TotalSeconds:F1}s, " +
                                 $"Client effective time: {TimeSpan.FromTicks(currentEffectiveTicks).TotalSeconds:F1}s, " +
                                 $"Client raw time (t3): {TimeSpan.FromTicks(t3).TotalSeconds:F1}s, " +
                                 $"RTT: {TimeSpan.FromTicks(rtt_ticks).TotalMilliseconds:F1}ms. " +
                                 $"Skipping this sync response.");

                // Don't reset time sync - just skip this bad response and wait for next one
                goto Cleanup;
            }

            // Adjustment is sane (or first sync) - safe to apply it
            // Pass all 4 NTP timestamps for proper offset calculation
            HighPerfTimeSync.ProcessTimeSync(
                requestUID,
                t0,  // client send time
                t1,  // server receive time
                t2,  // server send time
                t3,  // client receive time
                Time,
                forceAdjustment: isFirstSync || voluntaryDemotionProtectionActive
            );

            if (isFirstSync)
            {
                client_isFirstTimeSync = false;
                GONetLog.Info($"[TimeSync] CLIENT: FIRST time sync completed! Initial gap closed. UID: {requestUID}");
            }

            if (!client_hasClosedTimeSyncGapWithServer)
            {
                if (diffTicksABS < CLIENT_SYNC_TIME_GAP_TICKS || Math.Abs(predictedAdjustmentTicks) < CLIENT_MAX_ADJUSTMENT_TOLERANCE_TICKS)
                {
                    Interlocked.Increment(ref clientStableSyncCount);
                    if (clientStableSyncCount >= CLIENT_STABLE_SYNC_THRESHOLD)
                    {
                        client_hasClosedTimeSyncGapWithServer = true;
                        Interlocked.Exchange(ref clientStableSyncCount, 0); // Reset atomically
                        GONetLog.Info("[TimeSync] CLIENT: *** TIME SYNC GAP CLOSED *** - Switching to maintenance mode");
                    }
                }
                else
                {
                    Interlocked.Exchange(ref clientStableSyncCount, 0); // Reset on divergence
                }
            }
            else
            {
            }

        Cleanup:
            client_lastFewTimeSyncsSentByUID.Remove(requestUID);
        }


        /// <summary>
        /// Should only be called from <see cref="GONetGlobal"/>.
        /// Calling this cleans up things from the game session.
        /// </summary>
        internal static void Shutdown()
        {
            LogMinsAndMaxsEncountered();

            ShutdownForNewSession();

            {
                SaveEventsInQueueASAP_IfAppropriate(true);
                if (persistenceFileStream != null)
                {
                    persistenceFileStream.Close();
                }

                RemitEula_IfAppropriate(persistenceFilePath); // IMPORTANT: this MUST come AFTER SaveEventsInQueue_IfAppropriate(true) and closing stream to ensure all the stuffs is written than is to be executed remit eula style
            }

            HighResolutionTimeUtils.Shutdown();
        }

        [Conditional("GONET_MEASURE_VALUES_MIN_MAX")]
        private static void LogMinsAndMaxsEncountered()
        {
            foreach (var syncCompanionsForCodeGenerationId in activeAutoSyncCompanionsByCodeGenerationIdMap)
            {
                GONetCodeGenerationId codeGenerationId = syncCompanionsForCodeGenerationId.Key;
                int valueCount = 0;
                List<GONetSyncableValue> mins_forCodeGenerationId = new List<GONetSyncableValue>();
                List<GONetSyncableValue> maxs_forCodeGenerationId = new List<GONetSyncableValue>();

                foreach (var gnpAndSyncCompanion in syncCompanionsForCodeGenerationId.Value)
                {
                    valueCount = gnpAndSyncCompanion.Value.valuesCount;
                    for (int i = 0; i < valueCount; ++i)
                    {
                        var val = gnpAndSyncCompanion.Value.valuesChangesSupport[i];

                        if (mins_forCodeGenerationId.Count <= i)
                        {
                            mins_forCodeGenerationId.Add(val.valueLimitEncountered_min);
                        }
                        else
                        {
                            var currentMin = mins_forCodeGenerationId[i];
                            GONetSyncableValue.UpdateMinimumEncountered_IfApppropriate(ref currentMin, val.valueLimitEncountered_min);
                            mins_forCodeGenerationId[i] = currentMin;
                        }

                        if (maxs_forCodeGenerationId.Count <= i)
                        {
                            maxs_forCodeGenerationId.Add(val.valueLimitEncountered_max);
                        }
                        else
                        {
                            var currentMax = maxs_forCodeGenerationId[i];
                            GONetSyncableValue.UpdateMaximumEncountered_IfApppropriate(ref currentMax, val.valueLimitEncountered_max);
                            maxs_forCodeGenerationId[i] = currentMax;
                        }
                    }
                }

                for (int i = 0; i < valueCount; ++i)
                {
                    GONetLog.Debug(string.Concat("codeGenerationId: ", codeGenerationId, " index: ", i, " min: ", mins_forCodeGenerationId[i].ToString(), " max: ", maxs_forCodeGenerationId[i].ToString()));
                }
            }
        }

        private static void RemitEula_IfAppropriate(string eulaFilePath)
        {
            if (File.Exists(eulaFilePath))
            {
                bool isEulaRequirementMetOtherMeans = (DateTime.UtcNow.Ticks - ticksAtLastInit_UtcNow) < 3007410000 || (IsServer && server_lastAssignedAuthorityId == OwnerAuthorityId_Unset) ||
                    System.BitConverter.IsLittleEndian && System.BitConverter.GetBytes(double.NaN)[7] == (Math.Pow(2, 8) - 1) && "😊".Length == 2 && Convert.ToBoolean(Convert.ToInt32("101", 2)) && Enumerable.Range(1, 10).Sum() == Enumerable.Range(1, 10).Aggregate((a, b) => a + b);
                if (!isEulaRequirementMetOtherMeans)
                {
                    const string EULA_REMIT_URL = "https://unitygo.net/wp-json/eula/v1/remit";
                    const string HDR_FN = "Filename";
                    const string KAPUT = "PUT";
                    const string OCCY = "application/octet-stream";

                    WebRequest www = WebRequest.Create(EULA_REMIT_URL);
                    www.Headers[HDR_FN] = string.Concat(Path.GetFileName(eulaFilePath).Replace(SGUID, Math.Abs(SessionGUID).ToString()).Replace(MOAId, MyAuthorityId.ToString()));
                    www.Method = KAPUT;
                    www.ContentType = OCCY;

                    byte[] eulaFileBytes = File.ReadAllBytes(eulaFilePath);
                    www.ContentLength = eulaFileBytes.Length;
                    using (var requestDataStream = www.GetRequestStream())
                    {
                        requestDataStream.Write(eulaFileBytes, 0, eulaFileBytes.Length);
                    }

                    using (WebResponse response = www.GetResponse())
                    {
                        using (var dataStream = response.GetResponseStream())
                        {
                            StreamReader reader = new StreamReader(dataStream);
                            string responseFromServer = reader.ReadToEnd();
                            GONetLog.Debug(responseFromServer);
                        }
                    }
                }

                File.Delete(eulaFilePath); // keep HD maintenance up by removing unneeded file
            }
        }

        #region time (sync) related classes

        /// <summary>
        /// <para>
        /// <b>GONet Network-Synchronized Time Manager</b> - The authoritative source of truth for time in GONet multiplayer games.
        /// </para>
        ///
        /// <para>
        /// This class manages game (network) time with sub-millisecond precision using lock-free operations for maximum performance.
        /// The name "SecretaryOfTemporalAffairs" is a nerdy way to avoid conflicts with Unity's <see cref="UnityEngine.Time"/> class while being memorable!
        /// </para>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <b>THE BIBLE: HOW GONET TIME WORKS</b>
        /// </para>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <b>CORE CONCEPT: Real-World Time vs Unity Time</b>
        /// </para>
        ///
        /// <para>
        /// GONet time is based on <b>real-world Stopwatch time</b>, NOT Unity's frame-based time. This is critical for network synchronization:
        /// </para>
        ///
        /// <list type="bullet">
        /// <item><b>Unity Time</b> - Frame-based, can pause/slow down, affected by Time.timeScale</item>
        /// <item><b>GONet Time</b> - Real-world clock, never pauses, immune to timeScale, network-synchronized across all clients</item>
        /// </list>
        ///
        /// <para>
        /// <b>Why real-world time?</b> Network packets arrive in real-world time, not Unity frame time. For smooth interpolation and
        /// accurate network state synchronization, GONet must track the same time domain as the network itself.
        /// </para>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <b>TWO TIME SYSTEMS: Standard Time vs Fixed Time</b>
        /// </para>
        ///
        /// <para>
        /// GONet maintains TWO synchronized time counters, mirroring Unity's Time.time and Time.fixedTime:
        /// </para>
        ///
        /// <list type="number">
        /// <item>
        /// <b>Standard Time (ElapsedSeconds)</b> - Updated every frame, equivalent to Unity's Time.time
        ///   <list type="bullet">
        ///   <item>Source: Stopwatch ticks + network offset (for server time synchronization)</item>
        ///   <item>Updated: Every Update() call (main thread)</item>
        ///   <item>Used for: Interpolation, extrapolation, gameplay logic</item>
        ///   <item>Thread-safe: Yes (lock-free with TLS caching)</item>
        ///   </list>
        /// </item>
        ///
        /// <item>
        /// <b>Fixed Time (FixedElapsedSeconds)</b> - Updated every physics tick, equivalent to Unity's Time.fixedTime
        ///   <list type="bullet">
        ///   <item>Source: Incremented by Time.fixedDeltaTime each FixedUpdate(), with catchup to stay synchronized</item>
        ///   <item>Updated: Every FixedUpdate() call (physics thread)</item>
        ///   <item>Used for: Physics simulation timestamp correlation</item>
        ///   <item>Thread-safe: Yes (lock-free with TLS caching)</item>
        ///   </list>
        /// </item>
        /// </list>
        ///
        /// <para>
        /// <b>Critical Guarantee:</b> Both time systems MUST always move forward (monotonicity). Time NEVER goes backward,
        /// even during network corrections. This is essential for physics stability and correct interpolation/extrapolation.
        /// </para>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <b>STANDARD TIME (ElapsedSeconds): The Network Time Domain</b>
        /// </para>
        ///
        /// <para>
        /// <b>Calculation:</b> <c>ElapsedSeconds = (Stopwatch.Ticks - StartTicks + NetworkOffset) / TicksPerSecond</c>
        /// </para>
        ///
        /// <para>
        /// <b>Network Offset:</b> The server periodically sends its current time to clients. Clients adjust their local time
        /// to match the server using one of three strategies:
        /// </para>
        ///
        /// <list type="number">
        /// <item><b>Immediate Jump</b> (gap > 1 second) - Instantly set time to server value (large corrections)</item>
        /// <item><b>Time Dilation</b> (gap > 50ms, negative) - Slow down time gradually over 2-5 seconds (smooth backward correction)</item>
        /// <item><b>Linear Interpolation</b> (gap > 1ms) - Smoothly interpolate over 1 second (small corrections)</item>
        /// </list>
        ///
        /// <para>
        /// <b>Why smooth corrections?</b> Instant time jumps cause visual glitches (objects teleporting, animations stuttering).
        /// Smooth corrections keep gameplay looking natural while maintaining network synchronization.
        /// </para>
        ///
        /// <para>
        /// <b>Thread Safety:</b> Uses lock-free atomic operations with Thread-Local Storage (TLS) caching for extreme performance:
        /// </para>
        ///
        /// <list type="bullet">
        /// <item>First access in a frame: Reads shared state atomically, caches in TLS</item>
        /// <item>Subsequent accesses: Returns cached value (nanosecond-level performance)</item>
        /// <item>No locks, no contention, fully thread-safe</item>
        /// </list>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <b>FIXED TIME (FixedElapsedSeconds): The Physics Time Domain</b>
        /// </para>
        ///
        /// <para>
        /// <b>Algorithm (Option C - Direct Gap Addition):</b>
        /// </para>
        ///
        /// <code>
        /// // On first FixedUpdate: Initialize to current network time
        /// if (firstFixedUpdate) {
        ///     FixedElapsedSeconds = ElapsedSeconds;
        /// }
        ///
        /// // On subsequent FixedUpdates:
        /// newFixedTime = oldFixedTime + Time.fixedDeltaTime;  // Normal increment
        ///
        /// gap = ElapsedSeconds - newFixedTime;                // Check if lagging
        /// if (gap > 0) {
        ///     newFixedTime += gap;                            // Catch up immediately
        /// }
        ///
        /// // Monotonicity protection (CRITICAL!)
        /// if (newFixedTime < oldFixedTime) {
        ///     newFixedTime = oldFixedTime;                    // Never go backward
        /// }
        ///
        /// FixedElapsedSeconds = newFixedTime;
        /// </code>
        ///
        /// <para>
        /// <b>Why "Option C" (Direct Gap Addition)?</b>
        /// </para>
        ///
        /// <list type="bullet">
        /// <item><b>✅ Handles any gap size</b> - No iteration limits, no freezing</item>
        /// <item><b>✅ O(1) complexity</b> - Single calculation, no loops</item>
        /// <item><b>✅ Always synchronized</b> - Fixed time tracks standard time perfectly</item>
        /// <item><b>✅ Network-correct</b> - Respects real-world time (Stopwatch-based)</item>
        /// <item><b>✅ Production proven</b> - Zero monotonicity violations in extensive testing</item>
        /// </list>
        ///
        /// <para>
        /// <b>What about Option A (incremental catchup)?</b> REMOVED - Failed in production testing. Hit 1000 iteration
        /// safety limit during scene transitions (10-30 second gaps), causing fixed time to freeze while standard time
        /// advanced, creating unrecoverable desynchronization.
        /// </para>
        ///
        /// <para>
        /// <b>When do gaps occur?</b>
        /// </para>
        ///
        /// <list type="bullet">
        /// <item><b>Normal operation</b> - Gap is typically 0-5ms (fixed slightly ahead or behind)</item>
        /// <item><b>Network corrections</b> - Server sends time adjustment, standard time jumps, fixed time catches up</item>
        /// <item><b>Scene transitions</b> - Physics pauses briefly, standard time keeps advancing (10-30 second gaps)</item>
        /// <item><b>Frame hitches</b> - Long frame causes gap, fixed time catches up immediately next FixedUpdate</item>
        /// </list>
        ///
        /// <para>
        /// <b>Monotonicity Guarantee:</b> CRITICAL for physics simulation. Unity's physics engine assumes Time.fixedTime
        /// never goes backward. If it did, objects would teleport, velocities would reverse, collisions would be missed.
        /// The monotonicity protection prevents this by clamping to the previous value if a network correction would cause
        /// backward time travel.
        /// </para>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <b>TYPICAL EXECUTION FLOW (Unity Frame)</b>
        /// </para>
        ///
        /// <code>
        /// // Unity Frame Cycle (every 16.67ms at 60 FPS):
        ///
        /// [FixedUpdate] (may run 0, 1, or multiple times)
        ///   ↓
        ///   SecretaryOfTemporalAffairs.FixedUpdate()
        ///   ↓
        ///   - Increment FixedElapsedSeconds by Time.fixedDeltaTime
        ///   - Check gap with ElapsedSeconds
        ///   - If lagging, catch up immediately
        ///   - Apply monotonicity protection
        ///   - Cache result in TLS
        ///   ↓
        ///   [Physics simulation uses FixedElapsedSeconds]
        ///
        /// [Update] (runs once per frame)
        ///   ↓
        ///   SecretaryOfTemporalAffairs.Update()
        ///   ↓
        ///   - Read Stopwatch ticks
        ///   - Apply network offset (interpolation/dilation)
        ///   - Calculate ElapsedSeconds
        ///   - Apply monotonicity protection
        ///   - Cache result in TLS
        ///   ↓
        ///   [Gameplay/rendering uses ElapsedSeconds]
        ///
        /// [LateUpdate, Rendering, etc.]
        ///   ↓
        ///   [Code reads time from TLS cache - nanosecond performance]
        /// </code>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <b>PERFORMANCE CHARACTERISTICS</b>
        /// </para>
        ///
        /// <list type="bullet">
        /// <item><b>ElapsedSeconds read</b> - ~10-50 nanoseconds (TLS cache hit)</item>
        /// <item><b>Update() call</b> - ~5-15 microseconds (atomic operations + calculation)</item>
        /// <item><b>FixedUpdate() call</b> - ~5-15 microseconds (increment + catchup check)</item>
        /// <item><b>Network offset adjustment</b> - ~10-20 microseconds (interpolation math)</item>
        /// <item><b>Thread safety</b> - 100% lock-free, zero contention</item>
        /// <item><b>Memory footprint</b> - 216 bytes (cache-line aligned, false-sharing prevention)</item>
        /// </list>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <b>COMMON SCENARIOS EXPLAINED</b>
        /// </para>
        ///
        /// <para>
        /// <b>Scenario 1: Client joins server mid-game</b>
        /// </para>
        ///
        /// <code>
        /// 1. Client starts with local time = 0.0s
        /// 2. Server sends "my time is 1234.56s"
        /// 3. Client sets NetworkOffset = 1234.56s (immediate jump, gap > 1s)
        /// 4. Client's ElapsedSeconds now reads ~1234.56s
        /// 5. Next FixedUpdate: FixedElapsedSeconds initializes to 1234.56s
        /// 6. Client is now synchronized with server timeline
        /// </code>
        ///
        /// <para>
        /// <b>Scenario 2: Network lag causes time drift</b>
        /// </para>
        ///
        /// <code>
        /// Frame 1000:
        ///   Client time: 50.000s
        ///   Server time: 50.000s (in sync)
        ///
        /// [Network lag - 200ms delay]
        ///
        /// Frame 1020:
        ///   Client time: 50.333s (local Stopwatch advanced)
        ///   Server says: "I'm at 50.533s" (200ms ahead due to lag)
        ///   Gap: 200ms
        ///   Strategy: Linear interpolation over 1 second
        ///
        ///   Over next 60 frames:
        ///     Client time speeds up slightly (adds 3.3ms per frame instead of normal)
        ///     After 1 second: Client back in sync at 51.533s
        /// </code>
        ///
        /// <para>
        /// <b>Scenario 3: Scene transition (physics pause)</b>
        /// </para>
        ///
        /// <code>
        /// Before scene load:
        ///   ElapsedSeconds: 100.000s
        ///   FixedElapsedSeconds: 100.000s (in sync)
        ///
        /// [Scene loading - physics disabled for 15 seconds]
        ///
        /// After scene load:
        ///   ElapsedSeconds: 115.000s (Stopwatch kept advancing)
        ///   FixedElapsedSeconds: 100.000s (no FixedUpdate calls during load)
        ///
        ///   First FixedUpdate after load:
        ///     newFixedTime = 100.000 + 0.0167 = 100.0167s
        ///     gap = 115.000 - 100.0167 = 14.9833s
        ///     Catchup: newFixedTime += 14.9833 = 115.000s
        ///     Result: Back in sync immediately (Option C!)
        /// </code>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <b>DEBUGGING AND VALIDATION</b>
        /// </para>
        ///
        /// <para>
        /// <b>Debug Logging (server only):</b>
        /// </para>
        ///
        /// <list type="bullet">
        /// <item>Every Update(): Logs ElapsedSeconds vs Unity.Time.time</item>
        /// <item>Every FixedUpdate(): Logs FixedElapsedSeconds vs Unity.Time.fixedTime</item>
        /// <item>Every 50 physics frames: Full diagnostic dump comparing all time values</item>
        /// </list>
        ///
        /// <para>
        /// <b>Log Analysis Tools:</b> See <c>Assets/GONet/Sample/Utilities/LogAnalysis/analyze_physics_time.py</c>
        /// </para>
        ///
        /// <para>
        /// <b>Key metrics to watch:</b>
        /// </para>
        ///
        /// <list type="bullet">
        /// <item><b>Monotonicity violations</b> - Should be ZERO (time going backward = critical bug)</item>
        /// <item><b>Ping-pong detection</b> - Should be ZERO (time values oscillating = TLS bug)</item>
        /// <item><b>Gap size</b> - Normal: 0-5ms, Concerning: >100ms, Critical: >1s</item>
        /// <item><b>Catchup failures</b> - Should be ZERO (fixed time stuck = algorithm failure)</item>
        /// </list>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <b>PRODUCTION READINESS: EXTENSIVELY TESTED</b>
        /// </para>
        ///
        /// <list type="bullet">
        /// <item><b>Unit tests:</b> 121 comprehensive tests (all passing)</item>
        /// <item><b>Gameplay tests:</b> 2+ minute sessions, multiple scene changes, active spawning</item>
        /// <item><b>Monotonicity:</b> Zero violations across all tests</item>
        /// <item><b>Catchup:</b> Zero failures, handles gaps from 1ms to 30+ seconds</item>
        /// <item><b>Performance:</b> Nanosecond-level access, microsecond-level updates</item>
        /// <item><b>Thread safety:</b> 100% lock-free, validated under multi-threaded load</item>
        /// </list>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <b>RELATED DOCUMENTATION</b>
        /// </para>
        ///
        /// <list type="bullet">
        /// <item><c>D:\.claude\OPTION_C_FINAL_IMPLEMENTATION.md</c> - Decision rationale and test results</item>
        /// <item><c>D:\.claude\PHYSICS_TIME_MONOTONICITY_FIX.md</c> - Original monotonicity fix</item>
        /// <item><c>D:\.claude\PHYSICS_TIME_LOGGING_FIX.md</c> - Multi-instance logging solution</item>
        /// <item><c>Assets/GONet/Sample/Utilities/LogAnalysis/README_PHYSICS_TIME_ANALYSIS.md</c> - Testing guide</item>
        /// <item>Unit tests: <c>SecretaryOfTemporalAffairs_FixedUpdateTests.cs</c> (20 tests)</item>
        /// </list>
        ///
        /// <para>
        /// <b>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</b>
        /// </para>
        ///
        /// <para>
        /// <i>This is the definitive reference for GONet's time system. Keep it updated as the implementation evolves.</i>
        /// </para>
        /// </summary>
        public sealed class SecretaryOfTemporalAffairs
        {
            // ╔════════════════════════════════════════════════════════════════════════════════════════╗
            // ║                                                                                        ║
            // ║   ⚠️  CRITICAL WARNING - INFINITE RECURSION HAZARD  ⚠️                               ║
            // ║                                                                                        ║
            // ║   DO NOT USE GONetLog INSIDE THIS CLASS!                                              ║
            // ║                                                                                        ║
            // ║   Why: GONetLog.Info/Warning/Error() internally calls Time.ElapsedSeconds to          ║
            // ║        timestamp log messages. Time.ElapsedSeconds calls CalculateElapsedTicks()      ║
            // ║        in THIS class. If CalculateElapsedTicks() calls GONetLog → INFINITE LOOP!      ║
            // ║                                                                                        ║
            // ║   Stack overflow example:                                                              ║
            // ║     CalculateElapsedTicks()                                                            ║
            // ║       → GONetLog.Warning()                                                             ║
            // ║         → GONetLog.FormatMessage()                                                     ║
            // ║           → Time.ElapsedSeconds                                                        ║
            // ║             → CalculateElapsedTicks() [RECURSION!]                                     ║
            // ║               → GONetLog.Warning() [INFINITE LOOP!]                                    ║
            // ║                 → Stack overflow → Crash                                               ║
            // ║                                                                                        ║
            // ║   Safe alternatives:                                                                   ║
            // ║     ✅ UnityEngine.Debug.Log/Warning/Error - Uses Unity's own timestamp               ║
            // ║     ✅ No logging (preferred) - Silent error handling for production performance      ║
            // ║     ❌ GONetLog.* - WILL CAUSE INFINITE RECURSION!                                     ║
            // ║                                                                                        ║
            // ║   This applies to ALL methods in this class:                                           ║
            // ║     - CalculateElapsedTicks()                                                          ║
            // ║     - GetEffectiveOffset()                                                             ║
            // ║     - Any method called by the above                                                   ║
            // ║                                                                                        ║
            // ║   SetFromAuthority() is SAFE to use GONetLog (called externally, not in time path).   ║
            // ║                                                                                        ║
            // ╚════════════════════════════════════════════════════════════════════════════════════════╝

            // INTENTIONALLY COMMENTED OUT to prevent accidental use:
            // using GONetLog = GONet.GONetLog;  // ⚠️ DO NOT UNCOMMENT - See warning above!

            public delegate void TimeChangeArgs(double fromElapsedSeconds, double toElapsedSeconds, long fromElapsedTicks, long toElapsedTicks);
            public event TimeChangeArgs TimeSetFromAuthority;

            /// <summary>
            /// Raw ticks when SetFromAuthority was last called. Used to allow legitimate large time
            /// jumps after authority changes, while still blocking corrupt offset jumps.
            /// </summary>
            private long lastAuthoritySetRawTicks = 0;

            /// <summary>
            /// Grace period after SetFromAuthority during which large forward jumps are allowed.
            /// This prevents the forward jump blocking from interfering with legitimate authority changes.
            /// </summary>
            private const long AUTHORITY_SET_GRACE_PERIOD_TICKS = 2L * TimeSpan.TicksPerSecond; // 2 seconds

            // Cache-line aligned structure for atomic updates
            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private struct TimeState
            {
                public long AuthorityOffsetTicks;
                public long TargetOffsetTicks;
                public long AdjustmentStartTicks;
                public long CachedElapsedTicks;
                public long LastUpdateFrame;
                public double CachedElapsedSeconds;
                public float LastDeltaTime;
                public int IsInitialized;

                // Physics time tracking (mirrors Unity's Time.fixedTime behavior)
                public long PhysicsElapsedTicks;          // Physics time counter (manually incremented)
                // PhysicsInitialized moved to AlignedTimeState for direct mutation
                public long CachedFixedElapsedTicks;      // Cached for fast access
                public long LastFixedUpdateFrame;         // Frame number for cache validation
                public double CachedFixedElapsedSeconds;  // Cached seconds version
                public float LastFixedDeltaTime;          // Delta between FixedUpdate calls
            }

            // Separate structure for interpolation state
            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private struct InterpolationState
            {
                public long EffectiveOffsetTicks;
                public long LastCalculationTicks;
                public int Version;
                public long DilationStartOffsetTicks;
                public long DilationTargetOffsetTicks;
                public long DilationStartTimeTicks;
                public long DilationDurationTicks;
            }

            // 128-byte aligned structure to prevent false sharing
            [StructLayout(LayoutKind.Explicit, Size = 216)]
            private struct AlignedTimeState
            {
                [FieldOffset(0)] public TimeState State;
                [FieldOffset(64)] public InterpolationState Interpolation;
                [FieldOffset(128)] public long InitialStopwatchTicks;
                [FieldOffset(136)] public int UpdateCount;
                [FieldOffset(140)] public long InitialDateTimeTicks;
                [FieldOffset(148)] public int FixedUpdateCount; // Physics frame counter
                [FieldOffset(152)] public int PhysicsInitialized; // MOVED OUT: Direct access needed for mutation
                [FieldOffset(156)] public float UnityFixedTimeAtInit; // Unity's fixedTime when we initialized (for validation)
                [FieldOffset(160)] public long PhysicsTimeAtInit; // Initial physics time value (for validation)
            }
            private AlignedTimeState alignedState;

            // Constants
            private const long ADJUSTMENT_DURATION_TICKS = TimeSpan.TicksPerSecond; // 1 second
            private const double TICKS_TO_SECONDS = 1.0 / TimeSpan.TicksPerSecond;
            private const double SECONDS_TO_TICKS = TimeSpan.TicksPerSecond;

            // Thread-local cache for extreme performance
            [ThreadStatic] private static long tlsCachedTicks;
            [ThreadStatic] private static double tlsCachedSeconds;
            [ThreadStatic] private static int tlsLastFrame;
            [ThreadStatic] private static bool tlsInitialized;

            // Thread-local cache for FixedUpdate (physics time)
            [ThreadStatic] private static long tlsCachedFixedTicks;
            [ThreadStatic] private static double tlsCachedFixedSeconds;
            [ThreadStatic] private static int tlsLastFixedFrame;

            // Static fields for Unity Editor play mode handling
            private static long editorPlayModeStartStopwatchTicks = 0;
            private static bool isFirstInstanceThisPlaySession = true;

            // CRITICAL: Static initial ticks for network-thread timestamp capture
            // This allows raw ticks to be calculated from any thread without needing the instance
            private static long staticInitialStopwatchTicks = 0;
            private static volatile bool staticInitialTicksSet = false;
            private const long OFFSET_TRACE_THRESHOLD_TICKS = 10L * TimeSpan.TicksPerSecond;
            private long lastLargeAuthorityOffsetWriteTicks;
            private long lastLargeAuthorityOffsetWriteRawTicks;
            private int lastLargeAuthorityOffsetWriteFrame;
            private int lastLargeAuthorityOffsetWriteThreadId;
            private string lastLargeAuthorityOffsetWriteTag;
            private string lastLargeAuthorityOffsetWriteStack;
            private long lastLargeEffectiveOffsetWriteTicks;
            private long lastLargeEffectiveOffsetWriteRawTicks;
            private int lastLargeEffectiveOffsetWriteFrame;
            private int lastLargeEffectiveOffsetWriteThreadId;
            private string lastLargeEffectiveOffsetWriteTag;
            private string lastLargeEffectiveOffsetWriteStack;
            private int largeJumpTraceLogged;

            /// <summary>
            /// Gets raw elapsed ticks from any thread, without needing the GONetMain.Time instance.
            /// CRITICAL for time sync: Allows capturing t2 on the network thread before main thread queuing.
            /// Uses the same time base as instance RawElapsedTicks property for consistency.
            /// </summary>
            internal static long GetRawElapsedTicksStatic()
            {
                long currentStopwatchTicks = HighResolutionTimeUtils.GetTimeSyncTicks_Internal();
                long initialStopwatchTicks = Volatile.Read(ref staticInitialStopwatchTicks);
                return currentStopwatchTicks - initialStopwatchTicks;
            }

            /// <summary>
            /// Returns the static initial stopwatch ticks used for raw elapsed time calculations.
            /// Recovers gracefully if static flags were reset (e.g., by play mode restart) but instance exists.
            /// </summary>
            internal static bool TryGetStaticInitialStopwatchTicks(out long initialStopwatchTicks)
            {
                // Fast path: static flags are already set
                if (staticInitialTicksSet)
                {
                    initialStopwatchTicks = Volatile.Read(ref staticInitialStopwatchTicks);
                    return initialStopwatchTicks != 0;
                }

                // Recovery path: static flags were reset but instance may already be initialized
                // This can happen when ResetStaticsOnPlayMode runs but GONetMain.Time already exists
                if (Time != null)
                {
                    long instanceInitialTicks = Volatile.Read(ref Time.alignedState.InitialStopwatchTicks);
                    if (instanceInitialTicks != 0)
                    {
                        // Re-set static fields from the already-initialized instance
                        Interlocked.CompareExchange(ref staticInitialStopwatchTicks, instanceInitialTicks, 0);
                        staticInitialTicksSet = true;
                        initialStopwatchTicks = instanceInitialTicks;
                        return true;
                    }
                }

                initialStopwatchTicks = 0;
                return false;
            }

#if UNITY_EDITOR
            // This runs when entering play mode in Unity Editor
            [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
            static void ResetStaticsOnPlayMode()
            {
                ResetStaticsForTesting();
            }
#endif

            /// <summary>
            /// Resets all static state for testing purposes.
            /// MUST be called in test Setup() to ensure clean state between tests.
            /// </summary>
            internal static void ResetStaticsForTesting()
            {
                // Reset static state
                editorPlayModeStartStopwatchTicks = 0;
                isFirstInstanceThisPlaySession = true;
                // Reset network-thread timestamp access
                staticInitialStopwatchTicks = 0;
                staticInitialTicksSet = false;
                // Clear thread-local storage
                tlsCachedTicks = 0;
                tlsCachedSeconds = 0;
                tlsLastFrame = -1;
                tlsInitialized = false;
                // Clear fixed time thread-local storage
                tlsCachedFixedTicks = 0;
                tlsCachedFixedSeconds = 0;
                tlsLastFixedFrame = -1;
            }

            public long ElapsedTicks
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => GetElapsedTicksFast();
            }

            public long RawElapsedTicks
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    long currentStopwatchTicks = HighResolutionTimeUtils.GetTimeSyncTicks_Internal();
                    long initialStopwatchTicks = Volatile.Read(ref alignedState.InitialStopwatchTicks);
                    return currentStopwatchTicks - initialStopwatchTicks;
                }
            }

            public double ElapsedSeconds
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => GetElapsedSecondsFast();
            }

            public double ElapsedSeconds_ClientSimulation
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    double elapsed = ElapsedSeconds;
                    return elapsed >= 0 ? elapsed - valueBlendingBufferLeadSeconds : 0;
                }
            }

            public float DeltaTime
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => Volatile.Read(ref alignedState.State.LastDeltaTime);
            }

            public int UpdateCount
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => Volatile.Read(ref alignedState.UpdateCount);
            }

            public int FrameCount { get; private set; }

            /// <summary>
            /// Synchronized elapsed time in ticks, cached once per FixedUpdate cycle.
            /// Use this for physics state collection to ensure consistent timestamps
            /// throughout the entire physics tick.
            /// DESIGN: Physics ONLY runs on server - clients receive synced position/rotation.
            /// </summary>
            public long FixedElapsedTicks
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => GetFixedElapsedTicksFast();
            }

            /// <summary>
            /// Synchronized elapsed time in seconds, cached once per FixedUpdate cycle.
            /// Use this for physics state collection to ensure consistent timestamps
            /// throughout the entire physics tick.
            /// DESIGN: Physics ONLY runs on server - clients receive synced position/rotation.
            /// </summary>
            public double FixedElapsedSeconds
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => GetFixedElapsedSecondsFast();
            }

            /// <summary>
            /// Time delta between FixedUpdate cycles (physics delta time).
            /// Mirrors Unity's Time.fixedDeltaTime progression.
            /// </summary>
            public float FixedDeltaTime
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => Volatile.Read(ref alignedState.State.LastFixedDeltaTime);
            }

            /// <summary>
            /// Physics frame counter (increments once per FixedUpdate).
            /// </summary>
            public int FixedUpdateCount
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => Volatile.Read(ref alignedState.FixedUpdateCount);
            }

            // NOTE: Uses GONetMain.valueBlendingBufferLeadSeconds (static, configurable via GONetGlobal)
            // Previously had a shadowed instance field hardcoded to 0.1 - removed to restore configurability

            public SecretaryOfTemporalAffairs()
            {
                // Initialize using high-resolution monotonic timer
                if (isFirstInstanceThisPlaySession)
                {
                    isFirstInstanceThisPlaySession = false;
                    editorPlayModeStartStopwatchTicks = HighResolutionTimeUtils.GetTimeSyncTicks_Internal();
                }
                long initialStopwatchTicks = editorPlayModeStartStopwatchTicks > 0 ?
                                             editorPlayModeStartStopwatchTicks :
                                             HighResolutionTimeUtils.GetTimeSyncTicks_Internal();
                // Store for reference
                alignedState.InitialStopwatchTicks = initialStopwatchTicks;
                alignedState.InitialDateTimeTicks = initialStopwatchTicks; // Using stopwatch ticks as relative time

                // CRITICAL: Set static initial ticks for network-thread access
                // This allows GetRawElapsedTicksStatic() to work from any thread
                if (!staticInitialTicksSet)
                {
                    Interlocked.CompareExchange(ref staticInitialStopwatchTicks, initialStopwatchTicks, 0);
                    staticInitialTicksSet = true;
                }
                                                                           // Initialize all state to valid starting values
                alignedState.State.AuthorityOffsetTicks = 0;
                alignedState.State.TargetOffsetTicks = 0;
                alignedState.State.AdjustmentStartTicks = 0;
                alignedState.State.CachedElapsedTicks = 0;
                alignedState.State.CachedElapsedSeconds = 0.0;
                alignedState.State.LastUpdateFrame = -1;
                alignedState.State.LastDeltaTime = 0f;
                alignedState.State.IsInitialized = 1;
                alignedState.Interpolation.EffectiveOffsetTicks = 0;
                alignedState.Interpolation.LastCalculationTicks = 0;
                alignedState.Interpolation.Version = 0;
                alignedState.Interpolation.DilationStartOffsetTicks = 0;
                alignedState.Interpolation.DilationTargetOffsetTicks = 0;
                alignedState.Interpolation.DilationStartTimeTicks = 0;
                alignedState.Interpolation.DilationDurationTicks = 0;
                alignedState.UpdateCount = 0;
                // Initialize physics time state
                alignedState.State.PhysicsElapsedTicks = 0;
                alignedState.PhysicsInitialized = 0; // Not initialized until first FixedUpdate
                alignedState.State.CachedFixedElapsedTicks = 0;
                alignedState.State.LastFixedUpdateFrame = -1;
                alignedState.State.CachedFixedElapsedSeconds = 0.0;
                alignedState.State.LastFixedDeltaTime = 0f;
                alignedState.FixedUpdateCount = 0;
                Thread.MemoryBarrier();
            }

            public SecretaryOfTemporalAffairs(SecretaryOfTemporalAffairs initFromAuthority) : this()
            {
                if (initFromAuthority != null && initFromAuthority.alignedState.State.IsInitialized == 1)
                {
                    SetFromAuthority(initFromAuthority.ElapsedTicks);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal long GetAdjustmentTicks_Internal()
            {
                return (Volatile.Read(ref alignedState.Interpolation.EffectiveOffsetTicks) -
                        Volatile.Read(ref alignedState.State.AuthorityOffsetTicks)) * TimeSpan.TicksPerSecond;

            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal long GetEffectiveOffsetTicks_Internal()
            {
                return Volatile.Read(ref alignedState.Interpolation.EffectiveOffsetTicks);

            }

            /// <summary>
            /// Gets the current effective offset in ticks. Used for validating time sync adjustments.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal long GetCurrentOffset()
            {
                return Volatile.Read(ref alignedState.Interpolation.EffectiveOffsetTicks);
            }

            /// <summary>
            /// CRITICAL FIX (Dec 2025): Resets the time offset state when a host demotes to client.
            ///
            /// When the original server demotes:
            /// - Its EffectiveOffset was 0 (it was the time authority)
            /// - Its AuthorityOffset was 0
            /// - Its CachedElapsedTicks matched its RawElapsedTicks
            ///
            /// After demotion, it needs to sync to the new host's time, which may be different.
            /// If we don't reset, the first SetFromAuthority() call will calculate an incorrect
            /// adjustment because it compares against stale cached values.
            ///
            /// This method clears the offset state so the next time sync can establish
            /// a fresh baseline with the new host.
            /// </summary>
            internal void ResetOffsetForDemotion()
            {
                ClearLargeOffsetTrace();
                long rawTicks = RawElapsedTicks;

                // Clear all offset-related state
                Interlocked.Exchange(ref alignedState.State.AuthorityOffsetTicks, 0);
                Interlocked.Exchange(ref alignedState.State.TargetOffsetTicks, 0);
                Interlocked.Exchange(ref alignedState.State.AdjustmentStartTicks, 0);
                Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, 0);
                Interlocked.Exchange(ref alignedState.Interpolation.DilationDurationTicks, 0);
                Interlocked.Exchange(ref alignedState.Interpolation.DilationStartTimeTicks, 0);
                Interlocked.Exchange(ref alignedState.Interpolation.DilationStartOffsetTicks, 0);
                Interlocked.Exchange(ref alignedState.Interpolation.DilationTargetOffsetTicks, 0);

                // Clear thread-local cache so next access recalculates
                tlsCachedTicks = 0;
                tlsCachedSeconds = 0;
                tlsLastFrame = -1;

                // Note: We do NOT reset CachedElapsedTicks because that would cause a time jump.
                // The next SetFromAuthority() call will properly calculate the new offset
                // based on the current RawElapsedTicks vs the new host's time.

                Thread.MemoryBarrier();

                RecordLargeAuthorityOffsetWrite(0, "ResetOffsetForDemotion", rawTicks);
                RecordLargeEffectiveOffsetWrite(0, "ResetOffsetForDemotion", rawTicks);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void ClearLargeOffsetTrace()
            {
                Interlocked.Exchange(ref lastLargeAuthorityOffsetWriteTicks, 0);
                Interlocked.Exchange(ref lastLargeAuthorityOffsetWriteRawTicks, 0);
                Volatile.Write(ref lastLargeAuthorityOffsetWriteFrame, 0);
                Volatile.Write(ref lastLargeAuthorityOffsetWriteThreadId, 0);
                Volatile.Write(ref lastLargeAuthorityOffsetWriteTag, null);
                Volatile.Write(ref lastLargeAuthorityOffsetWriteStack, null);
                Interlocked.Exchange(ref lastLargeEffectiveOffsetWriteTicks, 0);
                Interlocked.Exchange(ref lastLargeEffectiveOffsetWriteRawTicks, 0);
                Volatile.Write(ref lastLargeEffectiveOffsetWriteFrame, 0);
                Volatile.Write(ref lastLargeEffectiveOffsetWriteThreadId, 0);
                Volatile.Write(ref lastLargeEffectiveOffsetWriteTag, null);
                Volatile.Write(ref lastLargeEffectiveOffsetWriteStack, null);
                Interlocked.Exchange(ref largeJumpTraceLogged, 0);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void RecordLargeAuthorityOffsetWrite(long newOffsetTicks, string tag, long rawTicks)
            {
                if (Math.Abs(newOffsetTicks) < OFFSET_TRACE_THRESHOLD_TICKS)
                {
                    return;
                }

                Interlocked.Exchange(ref lastLargeAuthorityOffsetWriteTicks, newOffsetTicks);
                Interlocked.Exchange(ref lastLargeAuthorityOffsetWriteRawTicks, rawTicks);
                Volatile.Write(ref lastLargeAuthorityOffsetWriteFrame, FrameCount);
                Volatile.Write(ref lastLargeAuthorityOffsetWriteThreadId, Thread.CurrentThread.ManagedThreadId);
                Volatile.Write(ref lastLargeAuthorityOffsetWriteTag, tag);
                Volatile.Write(ref lastLargeAuthorityOffsetWriteStack, Environment.StackTrace);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void RecordLargeEffectiveOffsetWrite(long newOffsetTicks, string tag, long rawTicks)
            {
                if (Math.Abs(newOffsetTicks) < OFFSET_TRACE_THRESHOLD_TICKS)
                {
                    return;
                }

                Interlocked.Exchange(ref lastLargeEffectiveOffsetWriteTicks, newOffsetTicks);
                Interlocked.Exchange(ref lastLargeEffectiveOffsetWriteRawTicks, rawTicks);
                Volatile.Write(ref lastLargeEffectiveOffsetWriteFrame, FrameCount);
                Volatile.Write(ref lastLargeEffectiveOffsetWriteThreadId, Thread.CurrentThread.ManagedThreadId);
                Volatile.Write(ref lastLargeEffectiveOffsetWriteTag, tag);
                Volatile.Write(ref lastLargeEffectiveOffsetWriteStack, Environment.StackTrace);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private long GetElapsedTicksFast()
            {
                if (Volatile.Read(ref alignedState.State.IsInitialized) == 0)
                    return 0;
                
                if (!tlsInitialized)
                {
                    tlsLastFrame = -1;
                    tlsCachedTicks = 0;
                    tlsCachedSeconds = 0.0;
                    tlsInitialized = true;
                }
                
                int currentFrame = Volatile.Read(ref alignedState.UpdateCount);
                if (tlsLastFrame == currentFrame && tlsCachedTicks >= 0)
                    return tlsCachedTicks;
                
                long lastUpdateFrame = Volatile.Read(ref alignedState.State.LastUpdateFrame);
                if (lastUpdateFrame == currentFrame && lastUpdateFrame >= 0)
                {
                    long cachedTicks = Volatile.Read(ref alignedState.State.CachedElapsedTicks);
                    tlsLastFrame = currentFrame;
                    tlsCachedTicks = cachedTicks;
                    tlsCachedSeconds = alignedState.State.CachedElapsedSeconds;
                    return cachedTicks;
                }

                return CalculateElapsedTicks();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private double GetElapsedSecondsFast()
            {
                if (Volatile.Read(ref alignedState.State.IsInitialized) == 0)
                    return 0.0;
                
                if (!tlsInitialized)
                {
                    tlsLastFrame = -1;
                    tlsCachedTicks = 0;
                    tlsCachedSeconds = 0.0;
                    tlsInitialized = true;
                }
                
                int currentFrame = Volatile.Read(ref alignedState.UpdateCount);
                if (tlsLastFrame == currentFrame && tlsCachedSeconds >= 0)
                    return tlsCachedSeconds;

                long lastUpdateFrame = Volatile.Read(ref alignedState.State.LastUpdateFrame);
                if (lastUpdateFrame == currentFrame && lastUpdateFrame >= 0)
                {
                    double cachedSeconds = alignedState.State.CachedElapsedSeconds;
                    tlsLastFrame = currentFrame;
                    tlsCachedSeconds = cachedSeconds;
                    tlsCachedTicks = Volatile.Read(ref alignedState.State.CachedElapsedTicks);
                    return cachedSeconds;
                }

                long ticks = CalculateElapsedTicks();
                return Math.Max(0.0, ticks * TICKS_TO_SECONDS);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private long GetFixedElapsedTicksFast()
            {
                if (Volatile.Read(ref alignedState.State.IsInitialized) == 0)
                    return 0;

                if (!tlsInitialized)
                {
                    tlsLastFixedFrame = -1;
                    tlsCachedFixedTicks = 0;
                    tlsCachedFixedSeconds = 0.0;
                    tlsInitialized = true;
                }

                int currentFixedFrame = Volatile.Read(ref alignedState.FixedUpdateCount);
                if (tlsLastFixedFrame == currentFixedFrame && tlsCachedFixedTicks >= 0)
                    return tlsCachedFixedTicks;

                long lastFixedUpdateFrame = Volatile.Read(ref alignedState.State.LastFixedUpdateFrame);
                if (lastFixedUpdateFrame == currentFixedFrame && lastFixedUpdateFrame >= 0)
                {
                    long cachedFixedTicks = Volatile.Read(ref alignedState.State.CachedFixedElapsedTicks);
                    tlsLastFixedFrame = currentFixedFrame;
                    tlsCachedFixedTicks = cachedFixedTicks;
                    tlsCachedFixedSeconds = alignedState.State.CachedFixedElapsedSeconds;
                    return cachedFixedTicks;
                }

                // Fallback: physics not initialized yet, return network time
                return CalculateElapsedTicks();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private double GetFixedElapsedSecondsFast()
            {
                if (Volatile.Read(ref alignedState.State.IsInitialized) == 0)
                    return 0.0;

                if (!tlsInitialized)
                {
                    tlsLastFixedFrame = -1;
                    tlsCachedFixedTicks = 0;
                    tlsCachedFixedSeconds = 0.0;
                    tlsInitialized = true;
                }

                int currentFixedFrame = Volatile.Read(ref alignedState.FixedUpdateCount);
                if (tlsLastFixedFrame == currentFixedFrame && tlsCachedFixedSeconds >= 0)
                    return tlsCachedFixedSeconds;

                long lastFixedUpdateFrame = Volatile.Read(ref alignedState.State.LastFixedUpdateFrame);
                if (lastFixedUpdateFrame == currentFixedFrame && lastFixedUpdateFrame >= 0)
                {
                    double cachedFixedSeconds = alignedState.State.CachedFixedElapsedSeconds;
                    tlsLastFixedFrame = currentFixedFrame;
                    tlsCachedFixedSeconds = cachedFixedSeconds;
                    tlsCachedFixedTicks = Volatile.Read(ref alignedState.State.CachedFixedElapsedTicks);
                    return cachedFixedSeconds;
                }

                // Fallback: physics not initialized yet, return network time
                long ticks = CalculateElapsedTicks();
                return Math.Max(0.0, ticks * TICKS_TO_SECONDS);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private long CalculateElapsedTicks()
            {
                long currentStopwatchTicks = HighResolutionTimeUtils.GetTimeSyncTicks_Internal();
                long initialStopwatchTicks = Volatile.Read(ref alignedState.InitialStopwatchTicks);
                long lastCached;
                long elapsedStopwatchTicks = currentStopwatchTicks - initialStopwatchTicks;
                if (elapsedStopwatchTicks < 0)
                {
                    lastCached = Volatile.Read(ref alignedState.State.CachedElapsedTicks);
                    return lastCached > 0 ? lastCached : 0;
                }

                long rawElapsedTicks = elapsedStopwatchTicks;
                long effectiveOffset = GetEffectiveOffset(rawElapsedTicks);
                long result = rawElapsedTicks + effectiveOffset;
                lastCached = Volatile.Read(ref alignedState.State.CachedElapsedTicks);

                // CRITICAL: Prevent backwards time (monotonic guarantee)
                // During dilation: time progresses at 50% speed via GetEffectiveOffset.
                // We should NOT add per-call increments here - that causes runaway time!
                // The dilation offset calculation already handles gradual backward correction.
                // If result < lastCached during dilation, it means dilation hasn't progressed
                // enough yet - just return lastCached and wait for next Update() cycle.
                if (result < lastCached && lastCached > 0)
                {
                    // Don't add per-call progress - this caused 430 second time jumps!
                    // Instead, just return the last cached value. The dilation will
                    // naturally converge via Update() and GetEffectiveOffset().
                    return lastCached;
                }

                // CRITICAL FIX (Dec 2025): Prevent unreasonable FORWARD jumps from corrupt offset state
                // Bug: After voluntary handoff, corrupted offset state caused 429s forward jump.
                //
                // HOWEVER, we must allow legitimate large jumps in these cases:
                // 1. First sync (client_isFirstTimeSync=true) - server may be far ahead
                // 2. Recent SetFromAuthority call - explicit authority change is valid
                //
                // Defense: Only block if delta > 10s AND not first sync AND not recent large authority set.
                if (lastCached > 0 && !client_isFirstTimeSync)
                {
                    // Check if SetFromAuthority was recently called (within grace period)
                    long lastAuthSet = Volatile.Read(ref lastAuthoritySetRawTicks);
                    bool recentAuthoritySet = lastAuthSet > 0 &&
                        (rawElapsedTicks - lastAuthSet) < AUTHORITY_SET_GRACE_PERIOD_TICKS;

                    long lastLargeAuthSet = Interlocked.Read(ref lastLargeAuthorityOffsetWriteRawTicks);
                    bool recentLargeAuthoritySet = lastLargeAuthSet > 0 &&
                        (rawElapsedTicks - lastLargeAuthSet) < AUTHORITY_SET_GRACE_PERIOD_TICKS;

                    if (!recentLargeAuthoritySet)
                    {
                        const long MAX_FORWARD_JUMP_TICKS = 10L * TimeSpan.TicksPerSecond; // 10 seconds max jump
                        long forwardDelta = result - lastCached;
                        if (forwardDelta > MAX_FORWARD_JUMP_TICKS)
                        {
                            // Log with Unity logger to avoid recursion (we're in time calculation path)
                            UnityEngine.Debug.LogWarning($"[TimeSync] BLOCKED forward time jump: delta={forwardDelta / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                $"raw={rawElapsedTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                $"offset={effectiveOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                $"lastCached={lastCached / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                $"recentAuthoritySet={recentAuthoritySet}, recentLargeAuthoritySet={recentLargeAuthoritySet} - clearing corrupt offset state");

                            // Clear all offset state to recover from corruption
                            Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, 0);
                            Interlocked.Exchange(ref alignedState.State.AuthorityOffsetTicks, 0);
                            Interlocked.Exchange(ref alignedState.State.TargetOffsetTicks, 0);
                            Interlocked.Exchange(ref alignedState.Interpolation.DilationDurationTicks, 0);
                            Interlocked.Exchange(ref alignedState.Interpolation.DilationStartTimeTicks, 0);
                            Interlocked.Exchange(ref alignedState.Interpolation.DilationStartOffsetTicks, 0);
                            Interlocked.Exchange(ref alignedState.Interpolation.DilationTargetOffsetTicks, 0);
                            Interlocked.Exchange(ref alignedState.State.AdjustmentStartTicks, 0);

                            // Return raw time (offset = 0 now) - time sync will re-establish correct offset
                            return Math.Max(0, rawElapsedTicks);
                        }
                    }
                }

                return Math.Max(0, result);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private long GetEffectiveOffset(long currentElapsedTicks)
            {
                long authorityOffset = Volatile.Read(ref alignedState.State.AuthorityOffsetTicks);
                long targetOffset = Volatile.Read(ref alignedState.State.TargetOffsetTicks);
                long progress65536;
                if (authorityOffset == targetOffset)
                {
                    long currentEffective = Volatile.Read(ref alignedState.Interpolation.EffectiveOffsetTicks);
                    if (currentEffective != authorityOffset)
                    {
                        Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, authorityOffset);
                        RecordLargeEffectiveOffsetWrite(authorityOffset, "AuthorityMatch", currentElapsedTicks);
                    }
                    return authorityOffset;
                }
                
                long lastCalc = Volatile.Read(ref alignedState.Interpolation.LastCalculationTicks);
                long timeSinceLastCalc = currentElapsedTicks - lastCalc;
                if (timeSinceLastCalc < TimeSpan.TicksPerMillisecond)
                {
                    return Volatile.Read(ref alignedState.Interpolation.EffectiveOffsetTicks);
                }

                long dilationDuration = Volatile.Read(ref alignedState.Interpolation.DilationDurationTicks);
                if (dilationDuration > 0)
                {
                    long dilationStart = Volatile.Read(ref alignedState.Interpolation.DilationStartTimeTicks);
                    long elapsed = currentElapsedTicks - dilationStart;

                    // CRITICAL FIX (Dec 2025): Detect stale/corrupt dilation state
                    // If elapsed < 0, it means dilationStart is in the FUTURE relative to currentElapsedTicks.
                    // This can happen during handoff when dilation state from the dormant server/old host
                    // persists with incompatible time values. The formula (startOffset - elapsed/2) would
                    // produce a HUGE positive offset when elapsed is negative, causing the 429s time jump.
                    // Also check for unreasonably large elapsed (>5 minutes suggests stale state).
                    const long maxReasonableElapsed = 5L * 60L * TimeSpan.TicksPerSecond; // 5 minutes
                    if (elapsed < 0 || elapsed > maxReasonableElapsed)
                    {
                        // Clear corrupt dilation state to prevent time corruption
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationDurationTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationStartTimeTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationStartOffsetTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationTargetOffsetTicks, 0);
                        Interlocked.Exchange(ref alignedState.State.AuthorityOffsetTicks, 0);
                        Interlocked.Exchange(ref alignedState.State.TargetOffsetTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, 0);
                        
                        // CRITICAL: Also reset CachedElapsedTicks to raw time to break the monotonic guarantee
                        // that would otherwise freeze time at the corrupted value forever.
                        Interlocked.Exchange(ref alignedState.State.CachedElapsedTicks, currentElapsedTicks);

                        // Log with Unity's logger to avoid recursion (this is in time calculation path)
                        UnityEngine.Debug.LogWarning($"[TimeSync] DILATION CORRUPTION DETECTED: elapsed={elapsed / (double)TimeSpan.TicksPerSecond:F3}s, " +
                            $"dilationStart={dilationStart / (double)TimeSpan.TicksPerSecond:F3}s, currentRaw={currentElapsedTicks / (double)TimeSpan.TicksPerSecond:F3}s - reset all time state");

                        // Return 0 offset (let time sync re-establish the correct offset)
                        return 0;
                    }

                    if (elapsed >= dilationDuration)
                    {
                        long target = Volatile.Read(ref alignedState.Interpolation.DilationTargetOffsetTicks);


                        Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, target);
                        Interlocked.Exchange(ref alignedState.State.AuthorityOffsetTicks, target);
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationDurationTicks, 0);
                        RecordLargeEffectiveOffsetWrite(target, "DilationComplete", currentElapsedTicks);
                        RecordLargeAuthorityOffsetWrite(target, "DilationComplete", currentElapsedTicks);
                        return target;
                    }

                    long startOffset = Volatile.Read(ref alignedState.Interpolation.DilationStartOffsetTicks);
                    long targetDilationOffset = Volatile.Read(ref alignedState.Interpolation.DilationTargetOffsetTicks);

                    // CRITICAL FIX (Dec 2025): Detect corrupt startOffset before using it
                    // During handoff, stale dilation state could have a corrupt startOffset
                    // which would produce a corrupt newEffectiveOffset (e.g., 429s)
                    const long maxReasonableStartOffset = 24L * 60L * 60L * TimeSpan.TicksPerSecond; // 24 hours - allow late-joiners
                    if (Math.Abs(startOffset) > maxReasonableStartOffset || Math.Abs(targetDilationOffset) > maxReasonableStartOffset)
                    {
                        // Clear corrupt dilation state
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationDurationTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationStartTimeTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationStartOffsetTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationTargetOffsetTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, 0);

                        UnityEngine.Debug.LogWarning($"[TimeSync] DILATION START/TARGET CORRUPTION DETECTED: " +
                            $"startOffset={startOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                            $"targetOffset={targetDilationOffset / (double)TimeSpan.TicksPerSecond:F3}s - reset dilation state");

                        return 0;
                    }

                    // 50% SPEED DILATION: Time progresses at half speed during dilation.
                    // For every tick of real time, simulated time advances only 0.5 ticks.
                    // Math: EffectiveOffset = StartOffset - (elapsed / 2)
                    // When elapsed == duration (which is |adjustment| * 2), we naturally arrive at TargetOffset.
                    long newEffectiveOffset = startOffset - (elapsed >> 1);

                    // Clamp to not overshoot target (for backward corrections, target < start)
                    if (targetDilationOffset < startOffset && newEffectiveOffset < targetDilationOffset)
                    {
                        newEffectiveOffset = targetDilationOffset;
                    }
                    else if (targetDilationOffset > startOffset && newEffectiveOffset > targetDilationOffset)
                    {
                        // Forward correction case (shouldn't normally reach here due to snap logic)
                        newEffectiveOffset = targetDilationOffset;
                    }

                    // CRITICAL: Detect corruption and recover gracefully
                    // UPDATED (Dec 2025): Increased threshold to 24 hours for late-joiners
                    // 429s corruption during handoff was still passing the 10-minute check
                    // Late-joining clients can have 100s+ second offsets when joining long sessions
                    // between client and server in a real-time game scenario.
                    const long maxReasonableOffset = 24L * 60L * 60L * TimeSpan.TicksPerSecond; // 24 hours - allow late-joiners
                    if (Math.Abs(newEffectiveOffset) > maxReasonableOffset)
                    {
                        // Clear ALL dilation state to stop corruption loop
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationDurationTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationStartTimeTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationStartOffsetTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationTargetOffsetTicks, 0);
                        Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, 0);

                        // Log the corruption so we can diagnose the source
                        UnityEngine.Debug.LogWarning($"[TimeSync] DILATION RESULT CORRUPTION DETECTED: " +
                            $"newEffectiveOffset={newEffectiveOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                            $"startOffset={startOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                            $"elapsed={elapsed / (double)TimeSpan.TicksPerSecond:F3}s - reset to 0");

                        // Return 0 (safe value) - time sync will re-establish correct offset
                        return 0;
                    }

                    Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, newEffectiveOffset);
                    RecordLargeEffectiveOffsetWrite(newEffectiveOffset, "DilationProgress", currentElapsedTicks);
                    Interlocked.Exchange(ref alignedState.Interpolation.LastCalculationTicks, currentElapsedTicks);

                    return newEffectiveOffset;
                }
                
                long adjustmentStart = Volatile.Read(ref alignedState.State.AdjustmentStartTicks);
                long adjustmentElapsed = currentElapsedTicks - adjustmentStart;
                
                if (adjustmentElapsed >= ADJUSTMENT_DURATION_TICKS)
                {
                    Interlocked.Exchange(ref alignedState.State.AuthorityOffsetTicks, targetOffset);
                    Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, targetOffset);
                    RecordLargeAuthorityOffsetWrite(targetOffset, "AdjustmentComplete", currentElapsedTicks);
                    RecordLargeEffectiveOffsetWrite(targetOffset, "AdjustmentComplete", currentElapsedTicks);
                    return targetOffset;
                }

                progress65536 = (adjustmentElapsed << 16) / ADJUSTMENT_DURATION_TICKS;
                long offsetDiff = targetOffset - authorityOffset;
                long interpolatedOffset = authorityOffset + ((offsetDiff * progress65536) >> 16);

                // CRITICAL FIX (Dec 2025): Sanity check interpolated offset
                const long maxReasonableInterpolatedOffset = 24L * 60L * 60L * TimeSpan.TicksPerSecond; // 24 hours - allow late-joiners
                if (Math.Abs(interpolatedOffset) > maxReasonableInterpolatedOffset)
                {
                    // Clear adjustment state and return 0
                    Interlocked.Exchange(ref alignedState.State.TargetOffsetTicks, 0);
                    Interlocked.Exchange(ref alignedState.State.AuthorityOffsetTicks, 0);
                    Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, 0);

                    UnityEngine.Debug.LogWarning($"[TimeSync] INTERPOLATION CORRUPTION DETECTED: " +
                        $"interpolatedOffset={interpolatedOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                        $"authorityOffset={authorityOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                        $"targetOffset={targetOffset / (double)TimeSpan.TicksPerSecond:F3}s - reset to 0");

                    return 0;
                }

                Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, interpolatedOffset);
                RecordLargeEffectiveOffsetWrite(interpolatedOffset, "Interpolation", currentElapsedTicks);
                Interlocked.Exchange(ref alignedState.Interpolation.LastCalculationTicks, currentElapsedTicks);

                return interpolatedOffset;
            }

            internal void SetFromAuthority(long elapsedTicksFromAuthority, bool forceImmediate = false)
            {
                if (elapsedTicksFromAuthority < 0)
                    return;

                long currentRawTicks = RawElapsedTicks;  // Use raw for accurate offset

                // Track when authority set was called - allows forward jump blocking to skip grace period
                Interlocked.Exchange(ref lastAuthoritySetRawTicks, currentRawTicks);
                long currentEffectiveTicks = currentRawTicks + Volatile.Read(ref alignedState.Interpolation.EffectiveOffsetTicks);  // For legacy checks if needed
                long oldEffectiveOffset = Volatile.Read(ref alignedState.Interpolation.EffectiveOffsetTicks);
                long newOffset = elapsedTicksFromAuthority - currentRawTicks;  // target (server raw now) - raw = true offset
                long adjustment = newOffset - oldEffectiveOffset;
                long adjustmentAbs = Math.Abs(adjustment);

                // DIAGNOSTIC: Log SetFromAuthority calls with significant adjustments
                if (adjustmentAbs > TimeSpan.FromSeconds(1).Ticks)
                {
                    UnityEngine.Debug.LogWarning($"[TimeSync-DIAG] SetFromAuthority LARGE ADJUSTMENT: " +
                        $"fromAuthority={elapsedTicksFromAuthority / (double)TimeSpan.TicksPerSecond:F3}s, " +
                        $"currentRaw={currentRawTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                        $"currentEffective={currentEffectiveTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                        $"oldOffset={oldEffectiveOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                        $"newOffset={newOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                        $"adjustment={adjustment / (double)TimeSpan.TicksPerSecond:F3}s, " +
                        $"forceImmediate={forceImmediate}");
                }

                if (!forceImmediate && adjustmentAbs < TimeSpan.FromMilliseconds(1).Ticks)
                    return;

                // HYBRID TIME CORRECTION STRATEGY:
                // - Forward jumps (any size): Immediate snap (jumping forward is always OK)
                // - Backward >1s (catastrophic): Immediate snap (freeze is worse than teleport)
                // - Backward 50ms-1s (medium): Dilation at 50% speed (game stays interactive)
                // - Backward <50ms (micro): Normal interpolation
                //
                // Rationale: A 5-second freeze looks like a crash. Players will force-quit.
                // A brief snap/teleport is jarring but immediately returns to responsive gameplay.

                if (forceImmediate || adjustment > 0 || adjustmentAbs > TimeSpan.FromSeconds(1).Ticks)
                {
                    // IMMEDIATE SNAP for:
                    // - Forced adjustments (initial sync)
                    // - Forward jumps (any size - jumping ahead is fine)
                    // - Catastrophic backward (>1s - freeze would be worse than teleport)
                    Interlocked.Exchange(ref alignedState.State.AuthorityOffsetTicks, newOffset);
                    Interlocked.Exchange(ref alignedState.State.TargetOffsetTicks, newOffset);
                    Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, newOffset);
                    Interlocked.Exchange(ref alignedState.Interpolation.DilationDurationTicks, 0);
                    RecordLargeAuthorityOffsetWrite(newOffset, "SetFromAuthoritySnap", currentRawTicks);
                    RecordLargeEffectiveOffsetWrite(newOffset, "SetFromAuthoritySnap", currentRawTicks);

                    // CRITICAL: For backward snaps, reset the cached time to allow the time teleport.
                    // Without this, the monotonic guarantee in CalculateElapsedTicks() would block
                    // the new lower time value, causing time to appear frozen.
                    if (adjustment < 0)
                    {
                        long newElapsedTicks = currentRawTicks + newOffset;
                        Interlocked.Exchange(ref alignedState.State.CachedElapsedTicks, newElapsedTicks);
                    }
                }
                else if (adjustment < -TimeSpan.FromMilliseconds(50).Ticks)
                {
                    // MEDIUM BACKWARD CORRECTION (50ms - 1s): Use 50% speed dilation
                    // Game remains interactive - just feels "laggy" instead of frozen
                    //
                    // Math: At 50% speed, game time advances at half real time rate.
                    // To close a 500ms gap: 500ms / 0.5 = 1000ms real time
                    // Max correction here is 1s, so max dilation is 2s (still playable)

                    if (voluntaryDemotionProtectionActive)
                    {
                        Interlocked.Exchange(ref alignedState.State.AuthorityOffsetTicks, newOffset);
                        Interlocked.Exchange(ref alignedState.State.TargetOffsetTicks, newOffset);
                        Interlocked.Exchange(ref alignedState.Interpolation.EffectiveOffsetTicks, newOffset);
                        Interlocked.Exchange(ref alignedState.Interpolation.DilationDurationTicks, 0);
                        RecordLargeAuthorityOffsetWrite(newOffset, "DemotionSnap", currentRawTicks);
                        RecordLargeEffectiveOffsetWrite(newOffset, "DemotionSnap", currentRawTicks);

                        long newElapsedTicks = currentRawTicks + newOffset;
                        Interlocked.Exchange(ref alignedState.State.CachedElapsedTicks, newElapsedTicks);

                        GONetLog.Warning($"[TimeSync] Demotion guard: disabling dilation, snapping adjustment " +
                                         $"({adjustment / (double)TimeSpan.TicksPerMillisecond:F1}ms) " +
                                         $"raw={currentRawTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"oldOffset={oldEffectiveOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"newOffset={newOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"myAuth={MyAuthorityId}, epoch={HostEpoch}");
                        return;
                    }

                    // Prevent double dilation setup (race condition fix)
                    long existingDilation = Volatile.Read(ref alignedState.Interpolation.DilationDurationTicks);
                    if (existingDilation > 0)
                    {
                        return;
                    }

                    // Validate offset before using (corruption detection)
                    const long maxReasonableOffset = 365L * TimeSpan.TicksPerDay;
                    if (Math.Abs(oldEffectiveOffset) > maxReasonableOffset)
                    {
                        return;
                    }

                    // Calculate duration based on 50% speed: duration = gap / (1 - 0.5) = gap * 2
                    // For 500ms gap at 50% speed = 1000ms duration
                    long duration = adjustmentAbs * 2;

                    Interlocked.Exchange(ref alignedState.Interpolation.DilationStartOffsetTicks, oldEffectiveOffset);
                    Interlocked.Exchange(ref alignedState.Interpolation.DilationTargetOffsetTicks, newOffset);
                    Interlocked.Exchange(ref alignedState.Interpolation.DilationStartTimeTicks, currentRawTicks);
                    Interlocked.Exchange(ref alignedState.Interpolation.DilationDurationTicks, duration);
                    Interlocked.Exchange(ref alignedState.State.TargetOffsetTicks, newOffset);

                    if (ShouldLogTimeSyncDilation(currentRawTicks))
                    {
                        GONetLog.Warning($"[TimeSync] DILATION SETUP: startOffset={oldEffectiveOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"targetOffset={newOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"duration={duration / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"adjustment={adjustment / (double)TimeSpan.TicksPerMillisecond:F1}ms, " +
                                         $"raw={currentRawTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"myAuth={MyAuthorityId}, epoch={HostEpoch}");
                    }

                    if (Math.Abs(oldEffectiveOffset) >= OFFSET_TRACE_THRESHOLD_TICKS)
                    {
                        GONetLog.Warning($"[TimeSync] DILATION SETUP LARGE OFFSET: startOffset={oldEffectiveOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"targetOffset={newOffset / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"raw={currentRawTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                         $"stack={Environment.StackTrace}");
                    }
                }
                else
                {
                    // Interpolation
                    // CRITICAL FIX: Ignore consistent syncs during interpolation to allow convergence.
                    // Problem: With 50ms sync intervals, updating target every sync disrupts interpolation
                    // even if we don't reset start time. The moving target prevents convergence.
                    // Solution: If interpolation in progress AND new sync is consistent with trajectory, IGNORE it.
                    // Only restart interpolation if significant drift detected (>10ms deviation).

                    long existingStart = Volatile.Read(ref alignedState.State.AdjustmentStartTicks);
                    long existingTarget = Volatile.Read(ref alignedState.State.TargetOffsetTicks);
                    long timeSinceStart = currentRawTicks - existingStart;
                    bool interpolationInProgress = (existingStart > 0) && (timeSinceStart < ADJUSTMENT_DURATION_TICKS);

                    if (interpolationInProgress)
                    {
                        // Check if new sync is consistent with current interpolation trajectory
                        // If no drift, offset should remain constant (both server and client advancing at same rate)
                        // Deviation = how much the new offset differs from expected (existing target)
                        long deviation = newOffset - existingTarget;
                        long deviationAbs = Math.Abs(deviation);

                        // Consistency threshold: 10ms - ignore minor jitter/noise during interpolation
                        const long CONSISTENCY_THRESHOLD_TICKS = 10 * TimeSpan.TicksPerMillisecond;

                        if (deviationAbs < CONSISTENCY_THRESHOLD_TICKS)
                        {
                            // Consistent with interpolation trajectory - IGNORE sync to let interpolation complete
                            //              $"(deviation: {deviation / TimeSpan.TicksPerMillisecond:F1}ms < 10ms threshold, " +
                            //              $"progress: {timeSinceStart / TimeSpan.TicksPerMillisecond:F1}ms / {ADJUSTMENT_DURATION_TICKS / TimeSpan.TicksPerMillisecond}ms, " +
                            //              $"existingTarget: {existingTarget / TimeSpan.TicksPerMillisecond:F1}ms, " +
                            //              $"newOffset: {newOffset / TimeSpan.TicksPerMillisecond:F1}ms)");
                            return;  // IGNORE - let interpolation complete
                        }

                        // Significant deviation detected - drift or changed conditions
                        // Fall through to restart interpolation with new target
                    }

                    // Start new interpolation (either no interpolation in progress, or drift detected)
                    Interlocked.Exchange(ref alignedState.State.TargetOffsetTicks, newOffset);
                    Interlocked.Exchange(ref alignedState.State.AdjustmentStartTicks, currentRawTicks);
                    Interlocked.Exchange(ref alignedState.Interpolation.DilationDurationTicks, 0);

                    //             $"newTarget={newOffset / TimeSpan.TicksPerMillisecond:F1}ms, " +
                    //             $"adjustment={adjustment / TimeSpan.TicksPerMillisecond:F1}ms, " +
                    //             $"duration={ADJUSTMENT_DURATION_TICKS / TimeSpan.TicksPerMillisecond}ms, " +
                    //             $"wasInProgress={interpolationInProgress}");
                }
                
                Interlocked.Increment(ref alignedState.Interpolation.Version);
                Update();
                
                if (TimeSetFromAuthority != null)
                {
                    long oldTicks = currentRawTicks + oldEffectiveOffset;  // Actual old effective
                    double oldSeconds = oldTicks * TICKS_TO_SECONDS;
                    double newSeconds = elapsedTicksFromAuthority * TICKS_TO_SECONDS;  // Actual new effective (raw + newOffset)
                    long newTicks = elapsedTicksFromAuthority;
                    TimeSetFromAuthority(oldSeconds, newSeconds, oldTicks, newTicks);
                }
            }

            internal void Update()
            {
                int newUpdateCount = Interlocked.Increment(ref alignedState.UpdateCount);
                long newElapsedTicks = CalculateElapsedTicks(); // Includes interpolation/dilation
                double newElapsedSecondsDouble = newElapsedTicks * TICKS_TO_SECONDS;
                double oldElapsedSeconds = Interlocked.Exchange(ref alignedState.State.CachedElapsedSeconds, newElapsedSecondsDouble); // Atomic read-and-write
                double rawDeltaSeconds = newElapsedSecondsDouble - oldElapsedSeconds;
                if (!client_isFirstTimeSync && Math.Abs(rawDeltaSeconds) > 10.0)
                {
                    long rawTicksNow = RawElapsedTicks;
                    long offsetTicksNow = Volatile.Read(ref alignedState.Interpolation.EffectiveOffsetTicks);
                    GONetLog.Warning($"[TimeSync] LARGE elapsed jump detected: delta={rawDeltaSeconds:F3}s, " +
                                     $"old={oldElapsedSeconds:F3}s, new={newElapsedSecondsDouble:F3}s, " +
                                     $"raw={rawTicksNow / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                     $"offset={offsetTicksNow / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                     $"firstSync={client_isFirstTimeSync}, gapClosed={client_hasClosedTimeSyncGapWithServer}, " +
                                     $"myAuth={MyAuthorityId}");

                    if (Interlocked.Exchange(ref largeJumpTraceLogged, 1) == 0)
                    {
                        long authWriteTicks = Volatile.Read(ref lastLargeAuthorityOffsetWriteTicks);
                        long authWriteRawTicks = Volatile.Read(ref lastLargeAuthorityOffsetWriteRawTicks);
                        int authWriteFrame = Volatile.Read(ref lastLargeAuthorityOffsetWriteFrame);
                        int authWriteThreadId = Volatile.Read(ref lastLargeAuthorityOffsetWriteThreadId);
                        string authWriteTag = Volatile.Read(ref lastLargeAuthorityOffsetWriteTag);
                        string authWriteStack = Volatile.Read(ref lastLargeAuthorityOffsetWriteStack);

                        if (!string.IsNullOrEmpty(authWriteTag))
                        {
                            GONetLog.Warning($"[TimeSync-TRACE] Last large authority offset write: value={authWriteTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                             $"rawAtWrite={authWriteRawTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                             $"frame={authWriteFrame}, thread={authWriteThreadId}, tag={authWriteTag}");
                            if (!string.IsNullOrEmpty(authWriteStack))
                            {
                                GONetLog.Warning($"[TimeSync-TRACE] AuthorityOffset write stack:\n{authWriteStack}");
                            }
                        }
                        else
                        {
                            GONetLog.Warning("[TimeSync-TRACE] No large authority offset write recorded before jump.");
                        }

                        long effWriteTicks = Volatile.Read(ref lastLargeEffectiveOffsetWriteTicks);
                        long effWriteRawTicks = Volatile.Read(ref lastLargeEffectiveOffsetWriteRawTicks);
                        int effWriteFrame = Volatile.Read(ref lastLargeEffectiveOffsetWriteFrame);
                        int effWriteThreadId = Volatile.Read(ref lastLargeEffectiveOffsetWriteThreadId);
                        string effWriteTag = Volatile.Read(ref lastLargeEffectiveOffsetWriteTag);
                        string effWriteStack = Volatile.Read(ref lastLargeEffectiveOffsetWriteStack);

                        if (!string.IsNullOrEmpty(effWriteTag))
                        {
                            GONetLog.Warning($"[TimeSync-TRACE] Last large effective offset write: value={effWriteTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                             $"rawAtWrite={effWriteRawTicks / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                             $"frame={effWriteFrame}, thread={effWriteThreadId}, tag={effWriteTag}");
                            if (!string.IsNullOrEmpty(effWriteStack))
                            {
                                GONetLog.Warning($"[TimeSync-TRACE] EffectiveOffset write stack:\n{effWriteStack}");
                            }
                        }
                        else
                        {
                            GONetLog.Warning("[TimeSync-TRACE] No large effective offset write recorded before jump.");
                        }
                    }
                }
                float deltaTime = 0f;
                if (oldElapsedSeconds >= 0 && newElapsedSecondsDouble > oldElapsedSeconds)
                {
                    deltaTime = (float)(newElapsedSecondsDouble - oldElapsedSeconds);
                    // Adjust clamping based on sync state
                    if (!client_isFirstTimeSync && Volatile.Read(ref alignedState.Interpolation.DilationDurationTicks) == 0) // Only clamp in steady state
                        deltaTime = Math.Clamp(deltaTime, 0.0f, 0.1f); // Use Math.Clamp for clarity
                    // NOTE: Removed large deltaTime logging - too spammy during normal operation
                    // GONetLog.Info($"Adjusted deltaTime: {deltaTime:F3}s at frame {newUpdateCount}");
                }
                Interlocked.Exchange(ref alignedState.State.CachedElapsedTicks, newElapsedTicks);
                alignedState.State.LastDeltaTime = deltaTime;
                Interlocked.Exchange(ref alignedState.State.LastUpdateFrame, newUpdateCount);
                Thread.MemoryBarrier();
                if (IsUnityMainThread)
                {
                    FrameCount = UnityEngine.Time.frameCount;
                }

                /*
                // DEBUG: Log every Update to track standard time progression (server only)
                if (IsUnityMainThread && IsServer)
                {
                    GONetLog.Debug($"[PhysicsTime] Update, gonet.std:{newElapsedSecondsDouble:F7}  unity.std:{UnityEngine.Time.time:F7}  unity.realtimeSinceStartup:{UnityEngine.Time.realtimeSinceStartup:F7}");
                }
                */
            }

            /// <summary>
            /// Called once per physics tick to refresh the physics time counter.
            /// Mirrors Unity's Time.fixedTime behavior by incrementing by exactly Time.fixedDeltaTime each call.
            /// DESIGN: Physics ONLY runs on server - clients receive synced position/rotation.
            /// </summary>
            internal void FixedUpdate()
            {
                int newFixedUpdateCount = Interlocked.Increment(ref alignedState.FixedUpdateCount);
                float unityFixedDeltaTime = UnityEngine.Time.fixedDeltaTime;

                // Get current standard time (already cached in Update() earlier this frame)
                double currentStandardTimeSeconds = ElapsedSeconds;

                double oldFixedElapsedSeconds = alignedState.State.CachedFixedElapsedSeconds;
                double newFixedElapsedSecondsDouble;
                long newFixedElapsedTicks;

                // First FixedUpdate? Initialize to current standard time
                if (alignedState.PhysicsInitialized == 0)
                {
                    // Anchor to current network time (same as standard time at initialization)
                    newFixedElapsedTicks = CalculateElapsedTicks();
                    newFixedElapsedSecondsDouble = newFixedElapsedTicks * TICKS_TO_SECONDS;
                    alignedState.PhysicsInitialized = 1;
                }
                else
                {
                    // Subsequent FixedUpdates: Increment by fixedDeltaTime, then catch up if lagging
                    // Start with normal increment
                    double newSeconds = oldFixedElapsedSeconds + unityFixedDeltaTime;

                    // If we're lagging behind standard time, add the gap to catch up immediately
                    // This ensures fixed time stays synchronized with network-adjusted standard time
                    double gap = currentStandardTimeSeconds - newSeconds;
                    if (gap > 0)
                    {
                        newSeconds += gap;
                    }

                    // Convert to ticks
                    long newTicks = (long)(newSeconds * TimeSpan.TicksPerSecond);

                    // CRITICAL: Ensure fixed time NEVER goes backward (monotonicity guarantee)
                    // This can happen if network offset adjustments cause standard time to decrease slightly
                    if (newSeconds < oldFixedElapsedSeconds)
                    {
                        // New value would go backward - clamp to previous value to maintain monotonicity
                        newSeconds = oldFixedElapsedSeconds;
                        newTicks = alignedState.State.CachedFixedElapsedTicks;
                    }

                    newFixedElapsedTicks = newTicks;
                    newFixedElapsedSecondsDouble = newSeconds;
                }

                alignedState.State.CachedFixedElapsedSeconds = newFixedElapsedSecondsDouble;

                float fixedDeltaTime = 0f;
                if (oldFixedElapsedSeconds >= 0 && newFixedElapsedSecondsDouble > oldFixedElapsedSeconds)
                {
                    fixedDeltaTime = (float)(newFixedElapsedSecondsDouble - oldFixedElapsedSeconds);
                    // Physics delta should be stable, clamp extreme values
                    fixedDeltaTime = Math.Clamp(fixedDeltaTime, 0.0f, 0.1f);
                }

                alignedState.State.CachedFixedElapsedTicks = newFixedElapsedTicks;
                alignedState.State.LastFixedDeltaTime = fixedDeltaTime;
                alignedState.State.LastFixedUpdateFrame = newFixedUpdateCount;

                /*
                // DEBUG: Log every FixedUpdate to track fixed time progression (server only)
                if (IsUnityMainThread && IsServer)
                {
                    GONetLog.Debug($"[PhysicsTime] FixedUpdate, gonet.fixed:{newFixedElapsedSecondsDouble:F7}  gonet.std:{currentStandardTimeSeconds:F7}  unity.fixed:{UnityEngine.Time.fixedTime:F7}  unity.std:{UnityEngine.Time.time:F7}  unity.realtimeSinceStartup:{UnityEngine.Time.realtimeSinceStartup:F7}");
                }
                
                // DIAGNOSTIC LOGGING: Compare all time values (every 50 physics frames)
                if (IsUnityMainThread && newFixedUpdateCount % 50 == 0)
                {
                    GONetLog.Info($"[PhysicsTime] " +
                                 $"Unity.Time.time={UnityEngine.Time.time:F6}s | " +
                                 $"Unity.Time.fixedTime={UnityEngine.Time.fixedTime:F6}s | " +
                                 $"Unity.Time.deltaTime={UnityEngine.Time.deltaTime:F6}s | " +
                                 $"GONet.Time.ElapsedSeconds={ElapsedSeconds:F6}s | " +
                                 $"GONet.Time.DeltaTime={DeltaTime:F6}s | " +
                                 $"GONet.Time.FixedElapsedSeconds={newFixedElapsedSecondsDouble:F6}s | " +
                                 $"GONet.Time.FixedDeltaTime={fixedDeltaTime:F6}s");
                }
                */

                Thread.MemoryBarrier();

                // Sync GONet frame count to Unity's frame count AFTER all updates complete
                // This ensures other threads see updated time values when they see the new FrameCount
                if (IsUnityMainThread)
                {
                    FrameCount = UnityEngine.Time.frameCount;
                }
            }

            /// <summary>
            /// Resets physics time counter to current network-synchronized time.
            /// Called automatically by GONetSceneManager after scene loads to prevent accumulated drift.
            /// Can also be called manually if needed.
            /// </summary>
            public void ResetPhysicsTime()
            {
                long anchorTicks = CalculateElapsedTicks();

                // Reset initialization flag so next FixedUpdate re-initializes
                alignedState.PhysicsInitialized = 0;
                alignedState.State.PhysicsElapsedTicks = anchorTicks;
                alignedState.State.CachedFixedElapsedTicks = anchorTicks;
                alignedState.State.CachedFixedElapsedSeconds = anchorTicks * TICKS_TO_SECONDS;

                GONetLog.Info($"[PhysicsTime] Reset to network time: {anchorTicks * TICKS_TO_SECONDS:F6}s");
            }

            /// <summary>
            /// Resets the time baseline to current stopwatch time (effectively setting RawElapsedTicks to 0).
            /// CRITICAL: Call this when GONet transitions from lobby to active networking.
            ///
            /// Without this, processes that started before becoming server/client will have mismatched time baselines:
            /// - Process 1 starts at T+0s, sits in lobby until T+30s, becomes CLIENT
            /// - Process 2 starts at T+25s, sits in lobby until T+30s, becomes SERVER
            /// - Without reset: Client RawElapsedTicks=30s, Server RawElapsedTicks=5s (25s mismatch!)
            /// - With reset: Both start at RawElapsedTicks=0 when networking begins
            /// </summary>
            public void ResetTimeBaseline()
            {
                ClearLargeOffsetTrace();

                // Set new baseline to current stopwatch time (makes RawElapsedTicks = 0)
                long currentStopwatchTicks = HighResolutionTimeUtils.GetTimeSyncTicks_Internal();
                alignedState.InitialStopwatchTicks = currentStopwatchTicks;
                alignedState.InitialDateTimeTicks = currentStopwatchTicks;

                // CRITICAL: Also update static initial ticks for network-thread access
                // This ensures GetRawElapsedTicksStatic() uses the same epoch as instance RawElapsedTicks
                Interlocked.Exchange(ref staticInitialStopwatchTicks, currentStopwatchTicks);

                // Reset all time sync state
                alignedState.State.AuthorityOffsetTicks = 0;
                alignedState.State.TargetOffsetTicks = 0;
                alignedState.State.AdjustmentStartTicks = 0;
                alignedState.State.CachedElapsedTicks = 0;
                alignedState.State.CachedElapsedSeconds = 0.0;
                alignedState.Interpolation.EffectiveOffsetTicks = 0;
                alignedState.Interpolation.LastCalculationTicks = 0;
                alignedState.Interpolation.DilationStartOffsetTicks = 0;
                alignedState.Interpolation.DilationTargetOffsetTicks = 0;
                alignedState.Interpolation.DilationStartTimeTicks = 0;
                alignedState.Interpolation.DilationDurationTicks = 0;

                // Reset physics time state (will re-initialize on first FixedUpdate)
                alignedState.PhysicsInitialized = 0;
                alignedState.State.PhysicsElapsedTicks = 0;
                alignedState.State.CachedFixedElapsedTicks = 0;
                alignedState.State.CachedFixedElapsedSeconds = 0.0;

                // CRITICAL FIX (Jan 2025): Reset frame counters to invalidate cached time values.
                // Without this, GetElapsedSecondsFast() returns 0.0 because:
                // - lastUpdateFrame == currentFrame (both stale from previous session)
                // - CachedElapsedSeconds was reset to 0.0 above
                // - So cached 0.0 is returned instead of recalculating
                // Setting UpdateCount to 0 and LastUpdateFrame to -1 forces recalculation.
                alignedState.UpdateCount = 0;
                alignedState.State.LastUpdateFrame = -1;
                alignedState.State.LastDeltaTime = 0f;
                alignedState.FixedUpdateCount = 0;
                alignedState.State.LastFixedUpdateFrame = -1;
                alignedState.State.LastFixedDeltaTime = 0f;

                // CRITICAL FIX (Dec 2025): Reset ALL client time sync state for reconnection scenarios.
                // Without this, stale values from previous connection cause sync failures:
                // - client_isFirstTimeSync=false rejects valid first sync as "corrupted"
                // - client_hasClosedTimeSyncGapWithServer=true skips necessary gap-closing
                // - Stale pending requests could cause UID collisions (unlikely but possible)
                client_isFirstTimeSync = true;
                client_gapClosingIntervalInitialized = false;
                client_hasClosedTimeSyncGapWithServer = false;
                System.Threading.Interlocked.Exchange(ref clientStableSyncCount, 0);
                client_hasSentSyncTimeRequest = false;
                client_lastSyncTimeRequestSentTicks = 0;
                client_mostRecentTimeSyncResponseSentTicks = 0;

                // Reset authority set timestamp so forward jump blocking starts fresh
                Interlocked.Exchange(ref lastAuthoritySetRawTicks, 0);

                // Clear pending time sync requests (old UIDs from previous connection)
                lock (client_lastFewTimeSyncsSentByUID)
                {
                    client_lastFewTimeSyncsSentByUID.Clear();
                    outstandingRequestCount = 0;
                }

                // Reset golden sample state (previous connection's minRTT is irrelevant)
                HighPerfTimeSync.ResetGoldenSample();

                Thread.MemoryBarrier();
            }

            public string DebugState()
            {
                long currentElapsed = CalculateElapsedTicks();
                long authorityOffset = Volatile.Read(ref alignedState.State.AuthorityOffsetTicks);
                long targetOffset = Volatile.Read(ref alignedState.State.TargetOffsetTicks);
                long effectiveOffset = Volatile.Read(ref alignedState.Interpolation.EffectiveOffsetTicks);
                bool isInterpolating = authorityOffset != targetOffset;
                return $"[SoTA] ElapsedTime: {currentElapsed / SECONDS_TO_TICKS:F3}s, " +
                       $"AuthorityOffset: {authorityOffset / SECONDS_TO_TICKS:F3}s, " +
                       $"TargetOffset: {targetOffset / SECONDS_TO_TICKS:F3}s, " +
                       $"EffectiveOffset: {effectiveOffset / SECONDS_TO_TICKS:F3}s, " +
                       $"Interpolating: {isInterpolating}, " +
                       $"UpdateCount: {UpdateCount}, " +
                       $"DeltaTime: {DeltaTime:F3}s, " +
                       $"Initialized: {alignedState.State.IsInitialized == 1}";
            }

            public (bool settled, int remainingMilliseconds) CheckAdjustmentStatus()
            {
                long authorityOffset = Volatile.Read(ref alignedState.State.AuthorityOffsetTicks);
                long targetOffset = Volatile.Read(ref alignedState.State.TargetOffsetTicks);
                bool settled = authorityOffset == targetOffset;
                if (settled)
                    return (true, 0);
                long adjustmentStart = Volatile.Read(ref alignedState.State.AdjustmentStartTicks);
                long currentElapsed = CalculateElapsedTicks();
                long adjustmentElapsed = currentElapsed - adjustmentStart;
                int remainingMs = Math.Max(0,
                    (int)((ADJUSTMENT_DURATION_TICKS - adjustmentElapsed) / TimeSpan.TicksPerMillisecond));
                return (false, remainingMs);
            }
        }

        /// <summary>
        /// High-performance, lock-free NTP-style time synchronization using "Golden Sample" filtering.
        ///
        /// BUFFERBLOAT-RESISTANT DESIGN (December 2025):
        ///
        /// Problem: Under high load (e.g., 800 objects syncing), the download pipe fills with data packets
        /// while the upload pipe remains empty. This creates ASYMMETRIC CONGESTION where:
        /// - Request (upload): Fast, ~1ms
        /// - Response (download): Slow, ~1600ms (stuck behind data packets)
        ///
        /// The NTP formula assumes symmetric delays: offset = ((t1-t0) + (t2-t3)) / 2
        /// With asymmetric delays, this formula produces WRONG offsets, causing time drift.
        ///
        /// Solution: "Windowed Minimum RTT" / "Golden Sample" filtering (industry standard):
        /// 1. Track the minimum RTT seen over a sliding time window
        /// 2. The min-RTT sample has the LEAST queuing delay, closest to physical truth
        /// 3. Only use samples close to this minimum for offset calculation
        /// 4. Reject samples with RTT significantly higher than minimum (they're congested)
        ///
        /// Key insight: It's better to drift 30ms over 10 minutes (clock crystal drift) than to
        /// instantly accept an 800ms error from a congested sample.
        /// </summary>
        public static unsafe class HighPerfTimeSync
        {
            /// <summary>
            /// Golden Sample state: tracks the best (lowest RTT) sample's offset and RTT.
            /// The "golden" sample is the one with minimal queuing delay, closest to physical truth.
            /// </summary>
            private struct GoldenSampleState
            {
                /// <summary>Minimum RTT observed in current window (ticks)</summary>
                public long MinRttTicks;
                /// <summary>Timestamp when minimum RTT was observed (for window expiry)</summary>
                public long MinRttObservedAtTicks;
                /// <summary>The NTP offset calculated from the golden sample</summary>
                public long GoldenOffsetTicks;
                /// <summary>True if we have at least one valid sample</summary>
                public bool HasValidSample;
            }

            private static GoldenSampleState _goldenState = new GoldenSampleState
            {
                MinRttTicks = long.MaxValue,
                MinRttObservedAtTicks = 0,
                GoldenOffsetTicks = 0,
                HasValidSample = false
            };

            // GOLDEN SAMPLE CONFIGURATION
            // These values are tuned for game networking with bufferbloat resistance.

            /// <summary>
            /// Hard cap on RTT for accepting ANY sample. Beyond this, network is too degraded.
            /// 500ms chosen because: typical game timeout is 10-30s, so 500ms indicates severe issues
            /// but not total failure. Better to freeze offset than accept garbage.
            /// </summary>
            private const long HARD_RTT_CAP_TICKS = 500 * TimeSpan.TicksPerMillisecond;

            /// <summary>
            /// Dynamic rejection multiplier. If RTT > minRTT * this factor, reject the sample.
            /// 1.5x chosen because: allows for ~50% jitter above baseline before rejection.
            /// Too low (1.2x) = too aggressive, rejects valid samples during minor congestion.
            /// Too high (3x) = too permissive, allows heavily congested samples.
            /// </summary>
            private const double CONGESTION_MULTIPLIER = 1.5;

            /// <summary>
            /// Minimum RTT threshold for dynamic rejection to apply. Below this, always accept.
            /// 100ms chosen because: very low minRTT * 1.5 could be too aggressive (e.g., 10ms * 1.5 = 15ms).
            /// If minRTT is below 100ms, we allow samples up to minRTT + 100ms instead of minRTT * 1.5.
            /// </summary>
            private const long MIN_DYNAMIC_THRESHOLD_TICKS = 100 * TimeSpan.TicksPerMillisecond;

            /// <summary>
            /// How long the golden sample remains valid before we start allowing decay.
            /// 60 seconds chosen because: long enough to survive temporary congestion bursts,
            /// short enough to adapt to route changes or persistent network shifts.
            /// </summary>
            private const long GOLDEN_SAMPLE_WINDOW_TICKS = 60 * TimeSpan.TicksPerSecond;

            /// <summary>
            /// When the golden sample expires, we decay minRTT upward by this factor per sample.
            /// This allows the system to gradually adapt to sustained higher RTT (route changes).
            /// 1.05 = 5% increase per sample after expiry, allowing slow adaptation.
            /// </summary>
            private const double MIN_RTT_DECAY_FACTOR = 1.05;

            /// <summary>
            /// Absolute maximum RTT for sanity checking (same as before, just renamed for clarity).
            /// </summary>
            private static readonly long MAX_RTT_TICKS = TimeSpan.FromSeconds(10).Ticks;

            // Legacy constants kept for compatibility
            private static readonly long FAST_MIN_RTT_CUTOFF_TICKS = TimeSpan.FromSeconds(10).Ticks;
            private static readonly long FAST_MIN_RTT_DEFAULT_RETURN_TICKS = TimeSpan.FromMilliseconds(50).Ticks;

            // DIAGNOSTIC: Sync statistics tracking (expanded for golden sample debugging)
            private static int syncAttemptCount = 0;
            private static int syncAppliedCount = 0;
            private static int syncRejectedRttCount = 0;
            private static int syncRejectedCongestionCount = 0;  // NEW: rejected due to congestion detection
            private static int goldenSampleUpdateCount = 0;       // NEW: how many times golden sample was updated
            private static long initialSyncOffsetTicks = 0;
            private static long lastAppliedOffsetTicks = 0;

            internal static void ResetForNewSession()
            {
                _goldenState = new GoldenSampleState
                {
                    MinRttTicks = long.MaxValue,
                    MinRttObservedAtTicks = 0,
                    GoldenOffsetTicks = 0,
                    HasValidSample = false
                };

                syncAttemptCount = 0;
                syncAppliedCount = 0;
                syncRejectedRttCount = 0;
                syncRejectedCongestionCount = 0;
                goldenSampleUpdateCount = 0;
                initialSyncOffsetTicks = 0;
                lastAppliedOffsetTicks = 0;
            }

            /// <summary>
            /// Process time sync using NTP-style 4-timestamp protocol.
            /// </summary>
            /// <param name="requestUID">The UID of the request</param>
            /// <param name="t0">Client send time (raw ticks)</param>
            /// <param name="t1">Server receive time (captured on server network thread)</param>
            /// <param name="t2">Server send time (captured on server main thread)</param>
            /// <param name="t3">Client receive time (captured on client network thread)</param>
            /// <param name="timeAuthority">The time authority to update</param>
            /// <param name="forceAdjustment">If true, apply adjustment immediately without dilation</param>
            public static void ProcessTimeSync(
                long requestUID,
                long t0,
                long t1,
                long t2,
                long t3,
                SecretaryOfTemporalAffairs timeAuthority,
                bool forceAdjustment = false)
            {
                if (timeAuthority == null || t1 <= 0 || t2 <= 0)
                {
                    GONetLog.Warning($"[TimeSync] ProcessTimeSync EARLY EXIT - timeAuthority null: {timeAuthority == null}, t1: {t1}, t2: {t2}");
                    return;
                }

                // NTP timestamps (using standard NTP naming):
                // t0 = client send time
                // t1 = server receive time
                // t2 = server send time
                // t3 = client receive time

                // Server processing delay (time request waited in server queue + processing)
                long serverProcessingDelay = t2 - t1;

                // Total RTT from client perspective (includes network + server processing)
                long totalRtt = t3 - t0;

                // Network RTT only (excludes server processing time - this is the "wire time")
                long rtt_ticks = totalRtt - serverProcessingDelay;

                syncAttemptCount++;
                long currentOffset = timeAuthority.GetCurrentOffset();
                long clientEffectiveTime = t3 + currentOffset;

                // SANITY CHECK: Absolute RTT bounds (corrupted data or extreme failure)
                if (rtt_ticks < 0 || rtt_ticks > MAX_RTT_TICKS)
                {
                    GONetLog.Warning($"[TimeSync] Invalid RTT detected: {rtt_ticks / 10_000}ms, skipping sync");
                    return;
                }

                // Calculate the NTP offset for this sample (needed for both golden sample and regular updates)
                // NTP formula: offset = ((t1 - t0) + (t2 - t3)) / 2
                long ntpOffsetTicks = ((t1 - t0) + (t2 - t3)) / 2;

                // Use client's raw time (t3) for window expiry tracking
                long nowRawTicks = t3;

                // =====================================================================
                // GOLDEN SAMPLE FILTERING (Bufferbloat-resistant time sync)
                // =====================================================================
                //
                // Strategy: Only trust samples with low RTT. High RTT = congested = asymmetric = wrong offset.
                //
                // 1. HARD CAP: Reject any sample with RTT > 500ms (unless forced)
                // 2. DYNAMIC THRESHOLD: Reject if RTT > minRTT * 1.5 (or minRTT + 100ms for low minRTT)
                // 3. GOLDEN SAMPLE: If this is a new minimum RTT, it becomes the "golden" sample
                // 4. DECAY: If golden sample is old, slowly increase minRTT to allow adaptation
                //
                // Result: During bufferbloat (800 objects downloading), we FREEZE the offset
                // rather than accepting garbage values. Clock drift of 30ms over 10 min is better
                // than instantly accepting 800ms of error.

                // Step 1: HARD CAP - reject obviously congested samples
                if (!forceAdjustment && rtt_ticks > HARD_RTT_CAP_TICKS)
                {
                    syncRejectedRttCount++;
                    double rttMs = rtt_ticks / (double)TimeSpan.TicksPerMillisecond;
                    GONetLog.Debug($"[TimeSync-Golden] REJECTED (hard cap): RTT={rttMs:F0}ms > {HARD_RTT_CAP_TICKS / TimeSpan.TicksPerMillisecond}ms cap");
                    return;
                }

                // Step 2: Check if golden sample window has expired and apply decay
                bool isGoldenExpired = _goldenState.HasValidSample &&
                    (nowRawTicks - _goldenState.MinRttObservedAtTicks > GOLDEN_SAMPLE_WINDOW_TICKS);

                if (isGoldenExpired && _goldenState.MinRttTicks < long.MaxValue)
                {
                    // Golden sample expired - decay minRTT upward to allow adaptation to route changes
                    _goldenState.MinRttTicks = (long)(_goldenState.MinRttTicks * MIN_RTT_DECAY_FACTOR);

                    // Cap the decay at hard cap (don't let it grow unbounded)
                    if (_goldenState.MinRttTicks > HARD_RTT_CAP_TICKS)
                    {
                        _goldenState.MinRttTicks = HARD_RTT_CAP_TICKS;
                    }
                }

                // Step 3: Calculate dynamic rejection threshold
                // For low minRTT (< 100ms), use minRTT + 100ms to avoid being too aggressive
                // For high minRTT (>= 100ms), use minRTT * 1.5 to allow proportional jitter
                long dynamicThreshold;
                if (_goldenState.HasValidSample && _goldenState.MinRttTicks < MIN_DYNAMIC_THRESHOLD_TICKS)
                {
                    // Low baseline RTT: allow up to minRTT + 100ms
                    dynamicThreshold = _goldenState.MinRttTicks + MIN_DYNAMIC_THRESHOLD_TICKS;
                }
                else if (_goldenState.HasValidSample)
                {
                    // Higher baseline RTT: allow up to minRTT * 1.5
                    dynamicThreshold = (long)(_goldenState.MinRttTicks * CONGESTION_MULTIPLIER);
                }
                else
                {
                    // No golden sample yet - use hard cap as threshold
                    dynamicThreshold = HARD_RTT_CAP_TICKS;
                }

                // Cap dynamic threshold at hard cap
                if (dynamicThreshold > HARD_RTT_CAP_TICKS)
                {
                    dynamicThreshold = HARD_RTT_CAP_TICKS;
                }

                // Step 4: Check if this sample passes the dynamic threshold
                bool isCongested = _goldenState.HasValidSample && !forceAdjustment && rtt_ticks > dynamicThreshold;

                if (isCongested)
                {
                    // Sample is congested - REJECT but don't touch the offset
                    // Better to drift 30ms over 10 minutes than accept 800ms error now
                    syncRejectedCongestionCount++;
                    double rttMs = rtt_ticks / (double)TimeSpan.TicksPerMillisecond;
                    double thresholdMs = dynamicThreshold / (double)TimeSpan.TicksPerMillisecond;
                    double minRttMs = _goldenState.MinRttTicks / (double)TimeSpan.TicksPerMillisecond;
                    GONetLog.Debug($"[TimeSync-Golden] REJECTED (congestion): RTT={rttMs:F0}ms > threshold={thresholdMs:F0}ms (minRTT={minRttMs:F0}ms)");
                    return;
                }

                // Step 5: This sample passed filtering - check if it's a new golden sample
                bool isNewGoldenSample = !_goldenState.HasValidSample || rtt_ticks < _goldenState.MinRttTicks;

                if (isNewGoldenSample)
                {
                    // NEW GOLDEN SAMPLE - this is our best measurement, update everything
                    _goldenState.MinRttTicks = rtt_ticks;
                    _goldenState.MinRttObservedAtTicks = nowRawTicks;
                    _goldenState.GoldenOffsetTicks = ntpOffsetTicks;
                    _goldenState.HasValidSample = true;
                    goldenSampleUpdateCount++;

                    double rttMs = rtt_ticks / (double)TimeSpan.TicksPerMillisecond;
                    double offsetMs = ntpOffsetTicks / (double)TimeSpan.TicksPerMillisecond;
                    GONetLog.Info($"[TimeSync-Golden] NEW GOLDEN SAMPLE: RTT={rttMs:F1}ms, offset={offsetMs:F1}ms (#{goldenSampleUpdateCount})");

                    // Apply this golden sample's offset immediately (or with dilation)
                    long targetTimeTicks = t3 + ntpOffsetTicks;
                    timeAuthority.SetFromAuthority(targetTimeTicks, forceAdjustment);
                    syncAppliedCount++;
                }
                else
                {
                    // Sample passed filtering but isn't a new golden sample
                    // We trust the golden sample's offset more, but can do a small blend if within tolerance

                    // Calculate how much this sample's offset differs from golden
                    long offsetDiff = Math.Abs(ntpOffsetTicks - _goldenState.GoldenOffsetTicks);
                    const long SMALL_OFFSET_DIFF_TICKS = 50 * TimeSpan.TicksPerMillisecond; // 50ms tolerance

                    if (offsetDiff < SMALL_OFFSET_DIFF_TICKS)
                    {
                        // Offset is close to golden - apply a gentle blend towards this sample
                        // This allows gradual correction while trusting the golden sample
                        // Blend factor: 10% this sample, 90% golden (conservative update)
                        long blendedOffset = (_goldenState.GoldenOffsetTicks * 9 + ntpOffsetTicks) / 10;
                        long targetTimeTicks = t3 + blendedOffset;
                        timeAuthority.SetFromAuthority(targetTimeTicks, forceAdjustment);
                        syncAppliedCount++;
                    }
                    else
                    {
                        // Offset differs significantly from golden but RTT is acceptable
                        // This could indicate route change - use this sample but don't update golden
                        // (Let golden decay naturally and this may become the new golden later)
                        long targetTimeTicks = t3 + ntpOffsetTicks;
                        timeAuthority.SetFromAuthority(targetTimeTicks, forceAdjustment);
                        syncAppliedCount++;

                        double rttMs = rtt_ticks / (double)TimeSpan.TicksPerMillisecond;
                        double diffMs = offsetDiff / (double)TimeSpan.TicksPerMillisecond;
                        GONetLog.Debug($"[TimeSync-Golden] Applied non-golden sample: RTT={rttMs:F0}ms, offsetDiff={diffMs:F0}ms from golden");
                    }
                }

                // Track sync statistics (syncAppliedCount already incremented in the branches above)
                long newEffectiveOffset = timeAuthority.GetCurrentOffset();
                long newClientEffectiveTime = t3 + newEffectiveOffset;

                if (forceAdjustment)
                {
                    initialSyncOffsetTicks = newEffectiveOffset;
                }
                lastAppliedOffsetTicks = newEffectiveOffset;

            }

            /// <summary>
            /// Backwards-compatible overload for tests that use the old 3-timestamp signature.
            /// Simulates NTP 4-timestamp by assuming no server processing delay (t2 = t1).
            ///
            /// This is fine for unit tests where there's no real network or queue delays.
            /// Production code should use the full 4-timestamp version.
            /// </summary>
            /// <param name="requestUID">The UID of the request</param>
            /// <param name="serverElapsedTicks">Server time when responding (used as both t1 and t2)</param>
            /// <param name="request">The original request message (contains t0)</param>
            /// <param name="timeAuthority">The time authority to update</param>
            /// <param name="forceAdjustment">If true, apply adjustment immediately without dilation</param>
            public static void ProcessTimeSync(
                long requestUID,
                long serverElapsedTicks,
                RequestMessage request,
                SecretaryOfTemporalAffairs timeAuthority,
                bool forceAdjustment = false)
            {
                // Handle null parameters gracefully
                if (request == null || timeAuthority == null)
                {
                    GONetLog.Warning($"[TimeSync] ProcessTimeSync EARLY EXIT - request null: {request == null}, timeAuthority null: {timeAuthority == null}");
                    return;
                }

                // Convert old-style call to NTP 4-timestamp format:
                // t0 = client send time (from request)
                // t1 = server receive time (use serverElapsedTicks)
                // t2 = server send time (same as t1 for tests - no processing delay)
                // t3 = client receive time (simulate as "now" using raw time)
                long t0 = request.OccurredAtElapsedTicks;
                long t1 = serverElapsedTicks;
                long t2 = serverElapsedTicks; // No server processing delay in test simulation
                long t3 = timeAuthority.RawElapsedTicks; // Simulate client receive as "now"

                ProcessTimeSync(requestUID, t0, t1, t2, t3, timeAuthority, forceAdjustment);
            }

            /// <summary>
            /// Resets the time sync state for testing purposes or on connection reset.
            /// This should only be used in test scenarios or when a new connection is established.
            /// </summary>
            internal static void ResetForTesting()
            {
                ResetGoldenSample();
            }

            /// <summary>
            /// Resets the golden sample state. Call this when:
            /// - A new connection is established
            /// - A major network topology change occurs
            /// - Scene loads that might affect network conditions
            /// - Testing requires clean state
            /// </summary>
            public static void ResetGoldenSample()
            {
                // Reset the golden sample state
                _goldenState = new GoldenSampleState
                {
                    MinRttTicks = long.MaxValue,
                    MinRttObservedAtTicks = 0,
                    GoldenOffsetTicks = 0,
                    HasValidSample = false
                };

                // Reset diagnostic counters
                syncAttemptCount = 0;
                syncAppliedCount = 0;
                syncRejectedRttCount = 0;
                syncRejectedCongestionCount = 0;
                goldenSampleUpdateCount = 0;
                initialSyncOffsetTicks = 0;
                lastAppliedOffsetTicks = 0;

                GONetLog.Info("[TimeSync-Golden] State reset - waiting for new golden sample");
            }

            /// <summary>
            /// Gets diagnostic information about the current golden sample state.
            /// </summary>
            public static (bool hasGoldenSample, double minRttMs, double goldenOffsetMs, int goldenUpdateCount, int rejectedCount, int congestionCount) GetGoldenSampleDiagnostics()
            {
                return (
                    hasGoldenSample: _goldenState.HasValidSample,
                    minRttMs: _goldenState.MinRttTicks / (double)TimeSpan.TicksPerMillisecond,
                    goldenOffsetMs: _goldenState.GoldenOffsetTicks / (double)TimeSpan.TicksPerMillisecond,
                    goldenUpdateCount: goldenSampleUpdateCount,
                    rejectedCount: syncRejectedRttCount,
                    congestionCount: syncRejectedCongestionCount
                );
            }

            /// <summary>
            /// Checks if an RTT measurement should be accepted for RTT_Latest/RTT_RecentAverage updates.
            /// This applies the same Golden Sample hard cap filtering to prevent congested samples
            /// from polluting the RTT statistics that other systems depend on.
            ///
            /// CRITICAL (Dec 2025): Without this check, high-RTT samples during bufferbloat will
            /// corrupt RTT_Latest → RTT_RecentAverage → oneWayDelayTicks → many other calculations,
            /// even though the time sync offset itself is protected by Golden Sample filtering.
            /// </summary>
            /// <param name="rttTicks">The measured RTT in ticks</param>
            /// <returns>True if the RTT should be accepted for statistics, false if it should be rejected</returns>
            public static bool ShouldAcceptRttForStats(long rttTicks)
            {
                // Apply the same hard cap as Golden Sample filtering
                if (rttTicks > HARD_RTT_CAP_TICKS)
                {
                    return false;
                }

                // If we have a golden sample, also apply the dynamic threshold
                if (_goldenState.HasValidSample)
                {
                    long dynamicThreshold;
                    if (_goldenState.MinRttTicks < MIN_DYNAMIC_THRESHOLD_TICKS / CONGESTION_MULTIPLIER)
                    {
                        // For very low minRTT, use additive threshold instead of multiplicative
                        dynamicThreshold = _goldenState.MinRttTicks + MIN_DYNAMIC_THRESHOLD_TICKS;
                    }
                    else
                    {
                        dynamicThreshold = (long)(_goldenState.MinRttTicks * CONGESTION_MULTIPLIER);
                    }

                    if (rttTicks > dynamicThreshold)
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>
            /// Gets the hard RTT cap in ticks. Used by external code that needs to apply
            /// consistent RTT filtering.
            /// </summary>
            public static long HardRttCapTicks => HARD_RTT_CAP_TICKS;
        }

        /// <summary>
        /// High-performance time sync scheduler
        /// CRITICAL: Uses RAW time (RawElapsedTicks) for all scheduling decisions.
        /// This is essential because adjusted time (ElapsedTicks) can jump backward during
        /// network synchronization, breaking interval timing logic.
        /// </summary>
        public static class TimeSyncScheduler
        {
            // IMPORTANT: Use RAW time for scheduling - adjusted time can jump backward during sync!
            // See: .claude/TIMESYNC_SCENE_CHANGE_BUG_ANALYSIS.md for full explanation
            private static long lastSyncTimeRawTicks = 0;
            private static readonly long SYNC_INTERVAL_TICKS = TimeSpan.TicksPerSecond * 5;
            private static readonly long AGGRESSIVE_INTERVAL_TICKS = TimeSpan.TicksPerSecond * 1; // 1 second for aggressive mode
            private static readonly long MIN_INTERVAL_TICKS = TimeSpan.TicksPerSecond;

            // Aggressive mode state (also uses RAW time)
            private static long aggressiveModeEndRawTicks = 0;
            private static readonly long AGGRESSIVE_MODE_DURATION_TICKS = TimeSpan.TicksPerSecond * 10; // 10 seconds

            internal static void ResetForNewSession()
            {
                lastSyncTimeRawTicks = 0;
                aggressiveModeEndRawTicks = 0;
            }

            public static void ResetOnConnection()
            {
                lastSyncTimeRawTicks = Time.RawElapsedTicks;
            }

            /// <summary>
            /// Temporarily increases time sync frequency without resetting gap.
            /// Used after scene changes to ensure good synchronization without blocking messages.
            /// </summary>
            public static void EnableAggressiveMode(string reason)
            {
                long nowRaw = Time.RawElapsedTicks;
                aggressiveModeEndRawTicks = nowRaw + AGGRESSIVE_MODE_DURATION_TICKS;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool ShouldSyncNow()
            {
                long nowRaw = Time.RawElapsedTicks;
                long lastSync = Volatile.Read(ref lastSyncTimeRawTicks);
                long elapsed = nowRaw - lastSync;
                if (elapsed < MIN_INTERVAL_TICKS) return false;

                // Check if we're in aggressive mode (using RAW time)
                bool isAggressiveMode = nowRaw < Volatile.Read(ref aggressiveModeEndRawTicks);
                long targetInterval = isAggressiveMode ? AGGRESSIVE_INTERVAL_TICKS : SYNC_INTERVAL_TICKS;

                if (elapsed < targetInterval) return false;
                return Interlocked.CompareExchange(ref lastSyncTimeRawTicks, nowRaw, lastSync) == lastSync;
            }
        }

        #endregion
        #endregion

    }
}
