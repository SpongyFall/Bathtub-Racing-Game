/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GONet.Core
{
    /// <summary>
    /// Active health monitoring for SoA blending objects.
    ///
    /// PURPOSE: Instead of passive logging that requires manual analysis,
    /// this system actively detects anomalies (stuck objects) and logs
    /// comprehensive diagnostic information with full context.
    ///
    /// KEY FEATURES:
    /// - Tracks per-object lifecycle (registration → data_in → apply)
    /// - Detects stuck objects automatically (no data in, apply skipped, etc.)
    /// - Logs consolidated health reports (one line summary)
    /// - Logs detailed stuck object reports with ROOT CAUSE analysis
    ///
    /// OUTPUT FORMAT (designed for easy grep/analysis):
    /// - [SoA-HEALTH] summary line every N seconds
    /// - [STUCK-OBJECT] detailed line per stuck object with all context
    /// </summary>
    public static class SoA_ObjectHealthMonitor
    {
        /// <summary>
        /// Per-object tracking state.
        /// </summary>
        public struct ObjectState
        {
            public uint gonetId;
            public string objectName;
            public bool isPhysics;
            public bool isMine;
            public bool wasRegisteredByServer; // True if registered when GONetMain.IsServer was true

            // Timing
            public float registrationTime;
            public float firstDataInTime;
            public float lastDataInTime;
            public float lastApplyTime;

            // Counts
            public int dataInCount;
            public int applyCount;
            public int skipCount;

            // Last known state
            public int lastValidCount;
            public Vector3 lastPosition;
            public Vector3 registrationPosition;

            // Skip reason tracking
            public string lastSkipReason;
            public int consecutiveSkips;

            // Stuck detection
            public bool wasEverHealthy;     // Did it ever have validCount > 2?
            public float stuckDetectedTime; // When we first detected it was stuck
        }

        // Skip reason codes (for easy categorization)
        public const string SKIP_NOT_IN_MAP = "NOT_IN_MAP";
        public const string SKIP_NULL_GNP = "NULL_GNP";
        public const string SKIP_IS_MINE = "IS_MINE";
        public const string SKIP_STALE_DATA = "STALE_DATA";
        public const string SKIP_NULL_TRANSFORM = "NULL_TRANSFORM";
        public const string SKIP_INVALID_HANDLE = "INVALID_HANDLE";
        public const string SKIP_NAN_POSITION = "NAN_POSITION";
        public const string SKIP_SYNC_DISABLED = "SYNC_DISABLED";

        private static Dictionary<uint, ObjectState> s_TrackedObjects = new Dictionary<uint, ObjectState>(512);
        private static float s_LastHealthReportTime;
        private static float s_LastDetailedReportTime;
        private static bool s_IsEnabled;

        /// <summary>
        /// Whether the health monitor is enabled (for debug tracing).
        /// </summary>
        public static bool IsEnabled => s_IsEnabled;

        // Configuration
        private const float HEALTH_REPORT_INTERVAL = 5.0f;      // Summary every 5 seconds
        private const float DETAILED_REPORT_INTERVAL = 10.0f;   // Detailed stuck report every 10 seconds
        private const float STUCK_THRESHOLD_SECONDS = 3.0f;     // Consider stuck if no data for 3 seconds after registration
        private const int MAX_STUCK_OBJECTS_TO_LOG = 10;        // Don't spam with too many stuck objects

        /// <summary>
        /// Initialize the health monitor.
        /// </summary>
        public static void Initialize()
        {
            s_TrackedObjects.Clear();
            s_LastHealthReportTime = 0;
            s_LastDetailedReportTime = 0;
            s_IsEnabled = true;

#if GONet_SOA_TRACE
            GONetLog.Info("[SoA-HEALTH] Object health monitor initialized");
#endif
        }

        /// <summary>
        /// Shutdown the health monitor.
        /// </summary>
        public static void Shutdown()
        {
            // Final report before shutdown
            if (s_IsEnabled && s_TrackedObjects.Count > 0)
            {
                LogHealthReport(force: true);
            }

            s_TrackedObjects.Clear();
            s_IsEnabled = false;
        }

        /// <summary>
        /// Called when an object is registered in SoA.
        /// </summary>
        public static void OnRegistered(uint gonetId, string objectName, Vector3 position, bool isPhysics)
        {
            if (!s_IsEnabled) return;

            float currentTime = Time.realtimeSinceStartup;

            var state = new ObjectState
            {
                gonetId = gonetId,
                objectName = objectName ?? "Unknown",
                isPhysics = isPhysics,
                registrationTime = currentTime,
                registrationPosition = position,
                lastPosition = position,
                lastValidCount = 2, // Seeded with 2 samples
                wasEverHealthy = false,
                stuckDetectedTime = 0,
                wasRegisteredByServer = GONetMain.IsServer // Track which role registered this object
            };

            // Check IsMine
            if (GONetMain.gonetParticipantByGONetIdMap.TryGetValue(gonetId, out var gnp) && gnp != null)
            {
                state.isMine = gnp.IsMine;
            }

            s_TrackedObjects[gonetId] = state;

#if GONet_SOA_TRACE
            // DIAGNOSTIC: Log every registration with current count
            string role = GONetMain.IsServer ? "SVR" : "CLI";
            GONetLog.Debug($"[SoA-HEALTH-REG] {role}|GONetId={gonetId}|name={objectName}|newCount={s_TrackedObjects.Count}");
#endif
        }

        /// <summary>
        /// Called when DATA_IN is received for an object.
        /// </summary>
        public static void OnDataIn(uint gonetId, Vector3 position, int validCount)
        {
            if (!s_IsEnabled) return;

            if (s_TrackedObjects.TryGetValue(gonetId, out var state))
            {
                float currentTime = Time.realtimeSinceStartup;

                if (state.dataInCount == 0)
                {
                    state.firstDataInTime = currentTime;
                }

                state.lastDataInTime = currentTime;
                state.dataInCount++;
                state.lastValidCount = validCount;
                state.lastPosition = position;

                // Mark as healthy if we have real data
                if (validCount > 2)
                {
                    state.wasEverHealthy = true;
                    state.stuckDetectedTime = 0; // Reset stuck detection
                }

                s_TrackedObjects[gonetId] = state;
            }
        }

        /// <summary>
        /// Called when Apply processes an object (either applies or skips).
        /// </summary>
        public static void OnApply(uint gonetId, bool wasApplied, Vector3 appliedPosition, string skipReason = null)
        {
            if (!s_IsEnabled) return;

            if (s_TrackedObjects.TryGetValue(gonetId, out var state))
            {
                float currentTime = Time.realtimeSinceStartup;

                if (wasApplied)
                {
                    state.lastApplyTime = currentTime;
                    state.applyCount++;
                    state.lastPosition = appliedPosition;
                    state.consecutiveSkips = 0;
                    state.lastSkipReason = null;
                }
                else
                {
                    state.skipCount++;
                    state.consecutiveSkips++;
                    state.lastSkipReason = skipReason ?? "UNKNOWN";
                }

                s_TrackedObjects[gonetId] = state;
            }
        }

        /// <summary>
        /// Called when an object is unregistered from SoA.
        /// </summary>
        public static void OnUnregistered(uint gonetId)
        {
            if (!s_IsEnabled) return;

            s_TrackedObjects.Remove(gonetId);
        }

        /// <summary>
        /// Update health monitoring - call once per frame from GONetMain.
        /// </summary>
        public static void Update()
        {
            if (!s_IsEnabled || s_TrackedObjects.Count == 0) return;

            float currentTime = Time.realtimeSinceStartup;

            // Periodic health report
            if (currentTime - s_LastHealthReportTime >= HEALTH_REPORT_INTERVAL)
            {
                LogHealthReport(force: false);
                s_LastHealthReportTime = currentTime;
            }

            // Periodic detailed stuck object report
            if (currentTime - s_LastDetailedReportTime >= DETAILED_REPORT_INTERVAL)
            {
                LogDetailedStuckReport();
                s_LastDetailedReportTime = currentTime;
            }
        }

        /// <summary>
        /// Log a consolidated health summary.
        /// Format: [SoA-HEALTH] role|total|healthy|stuck|noDataIn|staleOnly|recentlyRegistered
        /// </summary>
        private static void LogHealthReport(bool force)
        {
            float currentTime = Time.realtimeSinceStartup;
            string role = GONetMain.IsServer ? "SVR" : "CLI";

            int total = 0;
            int healthy = 0;
            int stuck = 0;
            int noDataIn = 0;
            int staleOnly = 0;
            int recentlyRegistered = 0;
            int isMineCount = 0;

            foreach (var kvp in s_TrackedObjects)
            {
                var state = kvp.Value;
                total++;

                if (state.isMine)
                {
                    isMineCount++;
                    continue; // Don't count IsMine objects in health metrics
                }

                float age = currentTime - state.registrationTime;

                if (age < STUCK_THRESHOLD_SECONDS)
                {
                    recentlyRegistered++;
                }
                else if (state.dataInCount == 0)
                {
                    noDataIn++;
                    stuck++;
                }
                else if (state.lastValidCount <= 2)
                {
                    staleOnly++;
                    stuck++;
                }
                else if (state.wasEverHealthy && state.applyCount > 0)
                {
                    healthy++;
                }
                else
                {
                    // Has data but never became "healthy" - edge case
                    stuck++;
                }
            }

#if GONet_SOA_TRACE
            // Single-line summary optimized for grep
            GONetLog.Info($"[SoA-HEALTH] {role}|total={total}|healthy={healthy}|stuck={stuck}|noDataIn={noDataIn}|staleOnly={staleOnly}|recent={recentlyRegistered}|isMine={isMineCount}");

            // DIAGNOSTIC: Dump all tracked GONetIds to identify phantom objects
            if (total > 0)
            {
                var sb = new StringBuilder();
                sb.Append($"[SoA-HEALTH-DUMP] {role}|count={s_TrackedObjects.Count}|ids=");
                bool first = true;
                foreach (var kvp in s_TrackedObjects)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    var st = kvp.Value;
                    // Format: GONetId(name,dataIns,applies)
                    sb.Append($"{kvp.Key}({st.objectName.Replace(",", "_")},{st.dataInCount},{st.applyCount})");
                }
                GONetLog.Debug(sb.ToString());
            }
#endif
        }

        /// <summary>
        /// Log detailed information about stuck objects.
        /// Each stuck object gets one comprehensive log line with all diagnostic context.
        /// </summary>
        private static void LogDetailedStuckReport()
        {
            float currentTime = Time.realtimeSinceStartup;
            string role = GONetMain.IsServer ? "SVR" : "CLI";

            var stuckObjects = new List<(uint gonetId, ObjectState state, string reason)>();

            foreach (var kvp in s_TrackedObjects)
            {
                var state = kvp.Value;

                if (state.isMine) continue; // Skip IsMine objects

                float age = currentTime - state.registrationTime;
                if (age < STUCK_THRESHOLD_SECONDS) continue; // Too young to be stuck

                string stuckReason = DetermineStuckReason(state, currentTime);
                if (stuckReason != null)
                {
                    stuckObjects.Add((kvp.Key, state, stuckReason));
                }
            }

            if (stuckObjects.Count == 0) return;

            // Sort by age (oldest first)
            stuckObjects.Sort((a, b) => a.state.registrationTime.CompareTo(b.state.registrationTime));

            // Log summary (commented out - development diagnostic)
            // GONetLog.Warning($"[STUCK-SUMMARY] {role}|count={stuckObjects.Count}|oldest_age={(currentTime - stuckObjects[0].state.registrationTime):F1}s");

            // Log details for top N stuck objects (commented out - development diagnostic)
            // int logCount = Math.Min(stuckObjects.Count, MAX_STUCK_OBJECTS_TO_LOG);
            // for (int i = 0; i < logCount; i++)
            // {
            //     var (gonetId, state, reason) = stuckObjects[i];
            //     LogStuckObjectDetails(gonetId, state, reason, currentTime, role);
            // }
        }

        /// <summary>
        /// Determine why an object is stuck.
        /// Returns null if object is not stuck.
        ///
        /// IMPORTANT: "Stuck" means the object is NOT receiving proper sync data.
        /// Objects at rest that successfully synced are NOT stuck - they're just stationary.
        /// The key criteria is: did the object ever successfully receive and apply data?
        /// </summary>
        private static string DetermineStuckReason(ObjectState state, float currentTime)
        {
            // No DATA_IN ever received - definitely stuck
            if (state.dataInCount == 0)
            {
                return "NO_DATA_IN";
            }

            // Had data but validCount never exceeded seed value - stuck at initialization
            if (state.lastValidCount <= 2)
            {
                return "VALIDCOUNT_STUCK_AT_2";
            }

            // Data received but Apply always skipped - something blocking application
            if (state.applyCount == 0 && state.skipCount > 0)
            {
                return $"ALWAYS_SKIPPED:{state.lastSkipReason}";
            }

            // REMOVED: STALE_NO_RECENT_DATA check
            // Objects at rest (physics objects that stopped moving, stationary objects)
            // won't receive new data because there's nothing new to sync. This is correct
            // behavior, not a stuck condition. A truly stuck object would have applyCount=0
            // or validCount<=2, which are already caught above.

            // REMOVED: POSITION_NOT_MOVING check
            // Stationary objects that haven't moved are fine - they're just not moving.
            // This was causing false positives for any object that stayed near spawn.

            return null; // Not stuck - object successfully received and applied data
        }

        /// <summary>
        /// Log comprehensive details for a single stuck object.
        /// Format designed for easy parsing and correlation.
        /// </summary>
        private static void LogStuckObjectDetails(uint gonetId, ObjectState state, string reason, float currentTime, string role)
        {
            float age = currentTime - state.registrationTime;
            float timeSinceFirstData = state.firstDataInTime > 0 ? currentTime - state.firstDataInTime : -1;
            float timeSinceLastData = state.lastDataInTime > 0 ? currentTime - state.lastDataInTime : -1;
            float timeSinceLastApply = state.lastApplyTime > 0 ? currentTime - state.lastApplyTime : -1;

            // Decode GONetId
            uint raw = gonetId >> 10;
            uint owner = gonetId & 1023;
            string ownerStr = owner == 1023 ? "SVR" : $"CLI{owner}";

            // Calculate position delta from spawn
            float distFromSpawn = Vector3.Distance(state.lastPosition, state.registrationPosition);

            // Format: [STUCK-OBJECT] role|gonetId|raw|owner|reason|age|dataIns|applies|skips|validCount|distFromSpawn|lastSkip|name
            var sb = new StringBuilder();
            sb.Append($"[STUCK-OBJECT] {role}");
            sb.Append($"|gid={gonetId}");
            sb.Append($"|raw={raw}");
            sb.Append($"|owner={ownerStr}");
            sb.Append($"|reason={reason}");
            sb.Append($"|age={age:F1}s");
            sb.Append($"|dataIns={state.dataInCount}");
            sb.Append($"|applies={state.applyCount}");
            sb.Append($"|skips={state.skipCount}");
            sb.Append($"|validCnt={state.lastValidCount}");
            sb.Append($"|distSpawn={distFromSpawn:F2}");
            sb.Append($"|physics={state.isPhysics}");
            sb.Append($"|lastSkip={state.lastSkipReason ?? "none"}");
            sb.Append($"|sinceLast={timeSinceLastData:F1}s");
            sb.Append($"|name={state.objectName}");

            GONetLog.Warning(sb.ToString());
        }

        /// <summary>
        /// Get a summary string for debugging.
        /// </summary>
        public static string GetSummary()
        {
            if (!s_IsEnabled) return "Health monitor disabled";

            float currentTime = Time.realtimeSinceStartup;
            int total = s_TrackedObjects.Count;
            int stuck = 0;

            foreach (var state in s_TrackedObjects.Values)
            {
                if (state.isMine) continue;
                float age = currentTime - state.registrationTime;
                if (age >= STUCK_THRESHOLD_SECONDS && DetermineStuckReason(state, currentTime) != null)
                {
                    stuck++;
                }
            }

            return $"Tracked: {total}, Stuck: {stuck}";
        }
    }
}
