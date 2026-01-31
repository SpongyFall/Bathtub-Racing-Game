/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GONet.Core
{
    /// <summary>
    /// Lightweight lifecycle tracking for network objects.
    ///
    /// PURPOSE: Capture the complete object lifecycle from spawn to blend,
    /// enabling correlation between spawn events and stuck object issues.
    ///
    /// LIFECYCLE STAGES (in order):
    /// 1. SPAWN - Object instantiated (local or remote)
    /// 2. GONETID - GONetId assigned
    /// 3. READY - OnGONetReady fired
    /// 4. SOA_REG - Registered in SoA blending (tracked by SoA_ObjectHealthMonitor)
    /// 5. DATA_IN - First network data received (tracked by SoA_ObjectHealthMonitor)
    /// 6. APPLY - First successful blend apply (tracked by SoA_ObjectHealthMonitor)
    ///
    /// OUTPUT FORMAT (single line per event, grep-friendly):
    /// [LIFECYCLE] role|stage|GONetId|raw|owner|elapsed|name|extra
    /// </summary>
    public static class SoA_LifecycleTracker
    {
        /// <summary>
        /// Lifecycle stages for tracking.
        /// </summary>
        public enum Stage
        {
            SPAWN,      // Object instantiated
            GONETID,    // GONetId assigned
            READY,      // OnGONetReady fired
            SOA_REG,    // Registered in SoA (also tracked by health monitor)
            DESPAWN     // Object destroyed
        }

        // Track which objects have reached each stage (for duplicate detection)
        private static HashSet<uint> s_SpawnLogged = new HashSet<uint>();
        private static HashSet<uint> s_GonetIdLogged = new HashSet<uint>();
        private static HashSet<uint> s_ReadyLogged = new HashSet<uint>();
        private static HashSet<uint> s_SoaRegLogged = new HashSet<uint>();

        private static bool s_IsEnabled = true;
        private static float s_StartTime;

        /// <summary>
        /// Whether the lifecycle tracker is enabled (for debug tracing).
        /// </summary>
        public static bool IsEnabled => s_IsEnabled;

        // Statistics
        private static int s_SpawnCount;
        private static int s_GonetIdCount;
        private static int s_ReadyCount;
        private static int s_SoaRegCount;
        private static int s_DespawnCount;

        /// <summary>
        /// Initialize the lifecycle tracker.
        /// </summary>
        public static void Initialize()
        {
            s_SpawnLogged.Clear();
            s_GonetIdLogged.Clear();
            s_ReadyLogged.Clear();
            s_SoaRegLogged.Clear();

            s_SpawnCount = 0;
            s_GonetIdCount = 0;
            s_ReadyCount = 0;
            s_SoaRegCount = 0;
            s_DespawnCount = 0;

            s_StartTime = Time.realtimeSinceStartup;
            s_IsEnabled = true;

#if GONet_LIFECYCLE_TRACE
            GONetLog.Info("[LIFECYCLE] Tracker initialized");
#endif
        }

        /// <summary>
        /// Shutdown and log final statistics.
        /// </summary>
        public static void Shutdown()
        {
#if GONet_LIFECYCLE_TRACE
            if (s_IsEnabled)
            {
                string role = GONetMain.IsServer ? "SVR" : "CLI";
                GONetLog.Info($"[LIFECYCLE-SUMMARY] {role}|spawn={s_SpawnCount}|gonetId={s_GonetIdCount}|ready={s_ReadyCount}|soaReg={s_SoaRegCount}|despawn={s_DespawnCount}");
            }
#endif

            s_SpawnLogged.Clear();
            s_GonetIdLogged.Clear();
            s_ReadyLogged.Clear();
            s_SoaRegLogged.Clear();
            s_IsEnabled = false;
        }

        /// <summary>
        /// Log when an object is spawned/instantiated.
        /// Call this from GONetSpawner or instantiation code.
        /// </summary>
        public static void OnSpawn(uint gonetId, string objectName, bool isLocal, string spawnSource)
        {
            if (!s_IsEnabled) return;
            if (gonetId == 0) return; // Not yet assigned

            if (s_SpawnLogged.Contains(gonetId)) return; // Already logged
            s_SpawnLogged.Add(gonetId);
            s_SpawnCount++;

            LogEvent(Stage.SPAWN, gonetId, objectName, $"local={isLocal}|src={spawnSource}");
        }

        /// <summary>
        /// Log when a GONetId is assigned to an object.
        /// Call this from GONetId assignment code.
        /// </summary>
        public static void OnGONetIdAssigned(uint gonetId, string objectName, ushort ownerAuthorityId)
        {
            if (!s_IsEnabled) return;
            if (gonetId == 0) return;

            if (s_GonetIdLogged.Contains(gonetId)) return;
            s_GonetIdLogged.Add(gonetId);
            s_GonetIdCount++;

            string ownerStr = ownerAuthorityId == GONetMain.OwnerAuthorityId_Server ? "SVR" : $"CLI{ownerAuthorityId}";
            LogEvent(Stage.GONETID, gonetId, objectName, $"owner={ownerStr}");
        }

        /// <summary>
        /// Log when OnGONetReady fires for an object.
        /// Call this from the OnGONetReady event handler.
        /// </summary>
        public static void OnGONetReady(uint gonetId, string objectName, bool isMine)
        {
            if (!s_IsEnabled) return;
            if (gonetId == 0) return;

            if (s_ReadyLogged.Contains(gonetId)) return;
            s_ReadyLogged.Add(gonetId);
            s_ReadyCount++;

            LogEvent(Stage.READY, gonetId, objectName, $"isMine={isMine}");
        }

        /// <summary>
        /// Log when an object is registered in SoA.
        /// Called from RegisterObjectInSoA.
        /// </summary>
        public static void OnSoARegistered(uint gonetId, string objectName, bool isPhysics)
        {
            if (!s_IsEnabled) return;
            if (gonetId == 0) return;

            if (s_SoaRegLogged.Contains(gonetId)) return;
            s_SoaRegLogged.Add(gonetId);
            s_SoaRegCount++;

            LogEvent(Stage.SOA_REG, gonetId, objectName, $"physics={isPhysics}");
        }

        /// <summary>
        /// Log when an object is despawned/destroyed.
        /// </summary>
        public static void OnDespawn(uint gonetId, string objectName)
        {
            if (!s_IsEnabled) return;
            if (gonetId == 0) return;

            s_DespawnCount++;

            // Clean up tracking sets
            s_SpawnLogged.Remove(gonetId);
            s_GonetIdLogged.Remove(gonetId);
            s_ReadyLogged.Remove(gonetId);
            s_SoaRegLogged.Remove(gonetId);

            LogEvent(Stage.DESPAWN, gonetId, objectName, "");
        }

        /// <summary>
        /// Check if an object has reached a specific lifecycle stage.
        /// Useful for detecting objects stuck at a particular stage.
        /// </summary>
        public static bool HasReachedStage(uint gonetId, Stage stage)
        {
            switch (stage)
            {
                case Stage.SPAWN: return s_SpawnLogged.Contains(gonetId);
                case Stage.GONETID: return s_GonetIdLogged.Contains(gonetId);
                case Stage.READY: return s_ReadyLogged.Contains(gonetId);
                case Stage.SOA_REG: return s_SoaRegLogged.Contains(gonetId);
                default: return false;
            }
        }

        /// <summary>
        /// Get statistics about lifecycle progression.
        /// </summary>
        public static string GetStatsSummary()
        {
            return $"spawn={s_SpawnCount}|gonetId={s_GonetIdCount}|ready={s_ReadyCount}|soaReg={s_SoaRegCount}|despawn={s_DespawnCount}";
        }

        /// <summary>
        /// Log a lifecycle event in grep-friendly format.
        /// Format: [LIFECYCLE] role|stage|GONetId|raw|owner|elapsed|name|extra
        /// </summary>
        private static void LogEvent(Stage stage, uint gonetId, string objectName, string extra)
        {
            string role = GONetMain.IsServer ? "SVR" : "CLI";
            float elapsed = Time.realtimeSinceStartup - s_StartTime;

            // Decode GONetId
            uint raw = gonetId >> 10;
            uint ownerBits = gonetId & 1023;
            string ownerStr = ownerBits == 1023 ? "SVR" : $"CLI{ownerBits}";

            string name = objectName ?? "Unknown";
            if (name.Length > 40) name = name.Substring(0, 40);

#if GONet_LIFECYCLE_TRACE
            GONetLog.Debug($"[LIFECYCLE] {role}|{stage}|{gonetId}|raw={raw}|owner={ownerStr}|t={elapsed:F2}|{name}|{extra}");
#endif
        }
    }
}
