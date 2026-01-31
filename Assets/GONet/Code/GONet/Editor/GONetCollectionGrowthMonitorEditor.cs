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

using UnityEngine;
using UnityEditor;

namespace GONet
{
    /// <summary>
    /// Editor utilities for GONetCollectionGrowthMonitor.
    /// Adds menu items to quickly add monitoring to scenes.
    /// </summary>
    public static class GONetCollectionGrowthMonitorEditor
    {
        /// <summary>
        /// Adds a GONetCollectionGrowthMonitor to the active scene with smart defaults.
        /// Menu: GONet/Diagnostics/Add Collection Growth Monitor
        /// </summary>
        [MenuItem("GONet/Diagnostics/Add Collection Growth Monitor", false, 100)]
        public static void AddCollectionGrowthMonitor()
        {
            // Check if one already exists in the scene
            var existing = Object.FindObjectOfType<GONetCollectionGrowthMonitor>();
            if (existing != null)
            {
                EditorUtility.DisplayDialog(
                    "Monitor Already Exists",
                    $"A GONetCollectionGrowthMonitor already exists on GameObject '{existing.gameObject.name}'.\n\n" +
                    "Select it to configure settings.",
                    "OK"
                );
                Selection.activeGameObject = existing.gameObject;
                return;
            }

            // Create new GameObject with monitor
            GameObject monitorObj = new GameObject("GONet Collection Growth Monitor");
            var monitor = monitorObj.AddComponent<GONetCollectionGrowthMonitor>();

            // Configure with good defaults for 810-participant testing
            monitor.monitoringEnabled = false; // User must enable explicitly
            monitor.comparisonMode = true; // Comparison mode is best for scene load testing
            monitor.comparisonDelaySeconds = 60f; // 60 seconds should be enough for collections to settle
            monitor.logSceneLoadEvents = true;

            // Mark scene dirty
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene()
            );

            // Select the new object
            Selection.activeGameObject = monitorObj;

            EditorUtility.DisplayDialog(
                "Monitor Added Successfully",
                "GONetCollectionGrowthMonitor added to scene!\n\n" +
                "NEXT STEPS:\n" +
                "1. Enable 'monitoringEnabled' in the inspector\n" +
                "2. Start Play mode\n" +
                "3. Load your scene with 810 participants\n" +
                "4. Wait 60 seconds\n" +
                "5. Check the logs for [COLLECTION-GROWTH] entries\n\n" +
                "Configured with:\n" +
                "• Comparison Mode (before/after snapshot)\n" +
                "• 60-second delay\n" +
                "• Scene load event logging",
                "Got It"
            );
        }

        /// <summary>
        /// Adds a continuous monitoring version (samples every N seconds).
        /// Menu: GONet/Diagnostics/Add Continuous Growth Monitor
        /// </summary>
        [MenuItem("GONet/Diagnostics/Add Continuous Growth Monitor", false, 101)]
        public static void AddContinuousGrowthMonitor()
        {
            // Check if one already exists in the scene
            var existing = Object.FindObjectOfType<GONetCollectionGrowthMonitor>();
            if (existing != null)
            {
                EditorUtility.DisplayDialog(
                    "Monitor Already Exists",
                    $"A GONetCollectionGrowthMonitor already exists on GameObject '{existing.gameObject.name}'.\n\n" +
                    "Configure it to continuous mode by disabling 'comparisonMode'.",
                    "OK"
                );
                Selection.activeGameObject = existing.gameObject;
                return;
            }

            // Create new GameObject with monitor
            GameObject monitorObj = new GameObject("GONet Collection Growth Monitor (Continuous)");
            var monitor = monitorObj.AddComponent<GONetCollectionGrowthMonitor>();

            // Configure for continuous monitoring
            monitor.monitoringEnabled = false; // User must enable explicitly
            monitor.comparisonMode = false; // Continuous mode
            monitor.sampleIntervalSeconds = 2f; // Sample every 2 seconds
            monitor.monitorDurationSeconds = 60f; // Run for 60 seconds
            monitor.logSceneLoadEvents = true;

            // Mark scene dirty
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene()
            );

            // Select the new object
            Selection.activeGameObject = monitorObj;

            EditorUtility.DisplayDialog(
                "Continuous Monitor Added",
                "GONetCollectionGrowthMonitor added in CONTINUOUS mode!\n\n" +
                "NEXT STEPS:\n" +
                "1. Enable 'monitoringEnabled' in the inspector\n" +
                "2. Start Play mode\n" +
                "3. Monitor will sample every 2 seconds for 60 seconds\n" +
                "4. Check logs for [COLLECTION-GROWTH] entries\n\n" +
                "Configured with:\n" +
                "• Continuous Mode (periodic snapshots)\n" +
                "• 2-second sample interval\n" +
                "• 60-second total duration\n" +
                "• Scene load event logging",
                "Got It"
            );
        }
    }
}
