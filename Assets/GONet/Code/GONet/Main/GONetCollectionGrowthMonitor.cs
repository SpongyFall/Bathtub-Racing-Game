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

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using GONet.Utils;

namespace GONet
{
    /// <summary>
    /// Monitors GONet collection growth over time to diagnose performance issues.
    ///
    /// USAGE:
    ///   1. Attach this component to any GameObject (or the GONetGlobal object)
    ///   2. Configure sampling interval and duration in inspector
    ///   3. Enable monitoringEnabled to start tracking
    ///   4. Check logs for [COLLECTION-GROWTH] entries
    ///
    /// PURPOSE:
    ///   Diagnoses slow scene loading caused by dynamic collection resizing.
    ///   With 810+ participants, collections grow multiple times (1000→2048→4096),
    ///   causing rehashing and GC pressure that blocks frames.
    ///
    /// OUTPUT:
    ///   Logs collection sizes, capacities, and GC stats at regular intervals.
    ///   Shows growth patterns and correlates with GC collection spikes.
    /// </summary>
    public class GONetCollectionGrowthMonitor : MonoBehaviour
    {
        [Header("Monitoring Configuration")]
        [Tooltip("Enable periodic collection growth monitoring")]
        public bool monitoringEnabled = false;

        [Tooltip("Interval between snapshots (seconds)")]
        [Range(0.5f, 10f)]
        public float sampleIntervalSeconds = 2f;

        [Tooltip("Duration to monitor (seconds). 0 = infinite")]
        [Range(0f, 300f)]
        public float monitorDurationSeconds = 60f;

        [Tooltip("Also log scene load events")]
        public bool logSceneLoadEvents = true;

        [Header("Comparison Mode")]
        [Tooltip("If enabled, only captures two snapshots (before/after) instead of continuous monitoring")]
        public bool comparisonMode = false;

        [Tooltip("Seconds to wait before capturing 'after' snapshot in comparison mode")]
        [Range(1f, 120f)]
        public float comparisonDelaySeconds = 60f;

        private Coroutine monitoringCoroutine;
        private float monitoringStartTime;
        private GONetMain.CollectionGrowthSnapshot? previousSnapshot;
        private GONetMain.CollectionGrowthSnapshot? beforeSnapshot;

        private void OnEnable()
        {
            if (monitoringEnabled)
            {
                StartMonitoring();
            }

            if (logSceneLoadEvents)
            {
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            }
        }

        private void OnDisable()
        {
            StopMonitoring();

            if (logSceneLoadEvents)
            {
                UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            }
        }

        private void OnValidate()
        {
            // Start/stop monitoring when enabled/disabled in inspector
            if (Application.isPlaying)
            {
                if (monitoringEnabled && monitoringCoroutine == null)
                {
                    StartMonitoring();
                }
                else if (!monitoringEnabled && monitoringCoroutine != null)
                {
                    StopMonitoring();
                }
            }
        }

        public void StartMonitoring()
        {
            if (monitoringCoroutine != null)
            {
                StopCoroutine(monitoringCoroutine);
            }

            monitoringStartTime = Time.time;
            previousSnapshot = null;
            beforeSnapshot = null;

            if (comparisonMode)
            {
                monitoringCoroutine = StartCoroutine(ComparisonModeCoroutine());
            }
            else
            {
                monitoringCoroutine = StartCoroutine(ContinuousMonitoringCoroutine());
            }

            GONetLog.Info($"[COLLECTION-GROWTH-MONITOR] Started monitoring (mode: {(comparisonMode ? "comparison" : "continuous")}, interval: {sampleIntervalSeconds}s, duration: {(monitorDurationSeconds > 0 ? monitorDurationSeconds + "s" : "infinite")})");
        }

        public void StopMonitoring()
        {
            if (monitoringCoroutine != null)
            {
                StopCoroutine(monitoringCoroutine);
                monitoringCoroutine = null;
                GONetLog.Info("[COLLECTION-GROWTH-MONITOR] Stopped monitoring");
            }
        }

        private IEnumerator ContinuousMonitoringCoroutine()
        {
            while (true)
            {
                // Capture snapshot
                var snapshot = GONetMain.CaptureCollectionGrowthSnapshot($"Continuous @ {Time.time - monitoringStartTime:F1}s");

                // Log snapshot
                GONetMain.LogCollectionGrowthSnapshot(snapshot);

                // If we have a previous snapshot, log comparison
                if (previousSnapshot.HasValue)
                {
                    GONetMain.LogCollectionGrowthComparison(previousSnapshot.Value, snapshot);
                }

                previousSnapshot = snapshot;

                // Check if duration exceeded
                if (monitorDurationSeconds > 0 && (Time.time - monitoringStartTime) >= monitorDurationSeconds)
                {
                    GONetLog.Info($"[COLLECTION-GROWTH-MONITOR] Monitoring duration reached ({monitorDurationSeconds}s) - stopping");
                    monitoringEnabled = false;
                    yield break;
                }

                // Wait for next sample
                yield return new WaitForSeconds(sampleIntervalSeconds);
            }
        }

        private IEnumerator ComparisonModeCoroutine()
        {
            // Capture "before" snapshot
            beforeSnapshot = GONetMain.CaptureCollectionGrowthSnapshot("Before (Comparison Mode)");
            GONetMain.LogCollectionGrowthSnapshot(beforeSnapshot.Value);

            GONetLog.Info($"[COLLECTION-GROWTH-MONITOR] Waiting {comparisonDelaySeconds}s before capturing 'after' snapshot...");

            // Wait specified duration
            yield return new WaitForSeconds(comparisonDelaySeconds);

            // Capture "after" snapshot
            var afterSnapshot = GONetMain.CaptureCollectionGrowthSnapshot("After (Comparison Mode)");
            GONetMain.LogCollectionGrowthSnapshot(afterSnapshot);

            // Log comparison
            GONetMain.LogCollectionGrowthComparison(beforeSnapshot.Value, afterSnapshot);

            GONetLog.Info("[COLLECTION-GROWTH-MONITOR] Comparison complete - stopping");
            monitoringEnabled = false;
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            if (logSceneLoadEvents)
            {
                var snapshot = GONetMain.CaptureCollectionGrowthSnapshot($"Scene Loaded: {scene.name}");
                GONetMain.LogCollectionGrowthSnapshot(snapshot);

                if (previousSnapshot.HasValue)
                {
                    GONetMain.LogCollectionGrowthComparison(previousSnapshot.Value, snapshot);
                }

                previousSnapshot = snapshot;
            }
        }

        /// <summary>
        /// Manually trigger a single snapshot (useful for testing).
        /// Call from code or inspector button.
        /// </summary>
        [ContextMenu("Capture Snapshot Now")]
        public void CaptureSnapshotNow()
        {
            var snapshot = GONetMain.CaptureCollectionGrowthSnapshot($"Manual @ {Time.time:F1}s");
            GONetMain.LogCollectionGrowthSnapshot(snapshot);

            if (previousSnapshot.HasValue)
            {
                GONetMain.LogCollectionGrowthComparison(previousSnapshot.Value, snapshot);
            }

            previousSnapshot = snapshot;
        }
    }
}
