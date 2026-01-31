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

using UnityEditor;
using UnityEngine;

namespace GONet.Editor
{
    [CustomEditor(typeof(GONetPoolMonitor))]
    public class GONetPoolMonitorCustomInspector : UnityEditor.Editor
    {
        private GONetPoolMonitor targetMonitor;

        private void OnEnable()
        {
            targetMonitor = (GONetPoolMonitor)target;
        }

        public override void OnInspectorGUI()
        {
            // Don't draw default inspector - we'll draw everything custom

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "This component monitors pool statistics at runtime.\n" +
                    "Pool statistics will appear during Play Mode.",
                    MessageType.Info);
                return;
            }

            DrawPoolHeader();
            EditorGUILayout.Space(5);
            DrawPoolStatistics();

            // Force continuous repaint during play mode
            Repaint();
        }

        private void DrawPoolHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Pool Configuration", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            DrawStatRow("Prefab:", targetMonitor.PrefabName);
            DrawStatRow("Scene:", string.IsNullOrEmpty(targetMonitor.SceneIdentifier) ? "(Global)" : targetMonitor.SceneIdentifier);
            DrawStatRow("Persistent:", targetMonitor.PersistAcrossScenes ? "Yes (DontDestroyOnLoad)" : "No");
            DrawStatRow("Design Time Index:", targetMonitor.DesignTimeLocationIndex.ToString());

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }

        private void DrawPoolStatistics()
        {
            PoolStats stats = GONetMain.GetPoolStatsByKey(targetMonitor.DesignTimeLocationIndex, targetMonitor.SceneIdentifier);

            if (!stats.IsValid)
            {
                EditorGUILayout.HelpBox(
                    "Pool statistics not available.\n" +
                    "The pool may still be initializing.",
                    MessageType.Warning);
                return;
            }

            // Capacity Section
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Pool Capacity", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            DrawStatRow("Total Instances:", stats.TotalInstances.ToString());
            DrawStatRow("Max Pool Size:", stats.MaxPoolSize == 0 ? "Unlimited" : stats.MaxPoolSize.ToString());
            DrawStatRow("Grow By Count:", stats.GrowByCount.ToString());
            DrawStatRow("ID Ranges:", stats.RangeCount.ToString());

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();

            // Current State Section
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Current State", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            DrawStatRowWithColor("Available:", stats.AvailableCount.ToString(), Color.green);
            DrawStatRowWithColor("Borrowed (In Use):", stats.BorrowedCount.ToString(),
                stats.BorrowedCount > 0 ? new Color(1f, 0.7f, 0f) : Color.green);
            DrawStatRowWithColor("Destroyed:", stats.DestroyedCount.ToString(),
                stats.DestroyedCount > 0 ? Color.red : Color.green);
            DrawStatRowWithColor("Pending Requests:", stats.PendingBorrowRequests.ToString(),
                stats.PendingBorrowRequests > 0 ? Color.yellow : Color.green);

            // Utilization bar
            if (stats.TotalInstances > 0)
            {
                EditorGUILayout.Space(5);
                float utilization = (float)stats.BorrowedCount / stats.TotalInstances;
                Color barColor = utilization < 0.7f ? Color.green : (utilization < 0.9f ? Color.yellow : Color.red);
                Color prevBgColor = GUI.backgroundColor;
                GUI.backgroundColor = barColor;

                Rect rect = EditorGUILayout.GetControlRect(GUILayout.Height(20));
                EditorGUI.ProgressBar(rect, utilization, $"Utilization: {utilization:P0} ({stats.BorrowedCount}/{stats.TotalInstances})");

                GUI.backgroundColor = prevBgColor;
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();

            // Lifetime Statistics Section
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Lifetime Statistics", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            DrawStatRow("Total Borrow Events:", stats.TotalBorrowEvents.ToString("N0"));
            DrawStatRow("Unique IDs Ever Borrowed:", stats.UniqueBorrowedCount.ToString());
            DrawStatRow("Reused Borrow Events:", stats.ReusedBorrowEvents.ToString("N0"));
            DrawStatRow("Peak Concurrent Borrowed:", stats.PeakBorrowed.ToString());

            // Reuse efficiency bar
            if (stats.TotalBorrowEvents > 0)
            {
                EditorGUILayout.Space(5);
                float reuseRatio = (float)stats.ReusedBorrowEvents / stats.TotalBorrowEvents;
                Color barColor = reuseRatio > 0.5f ? Color.green : (reuseRatio > 0.2f ? Color.yellow : Color.red);
                Color prevBgColor = GUI.backgroundColor;
                GUI.backgroundColor = barColor;

                Rect rect = EditorGUILayout.GetControlRect(GUILayout.Height(20));
                EditorGUI.ProgressBar(rect, reuseRatio, $"Reuse Efficiency: {reuseRatio:P0}");

                GUI.backgroundColor = prevBgColor;

                // Help text for reuse efficiency
                EditorGUILayout.Space(3);
                if (reuseRatio >= 0.5f)
                {
                    EditorGUILayout.HelpBox(
                        "Good reuse efficiency! Objects are being properly returned to the pool.",
                        MessageType.Info);
                }
                else if (reuseRatio >= 0.2f && stats.TotalBorrowEvents > 10)
                {
                    EditorGUILayout.HelpBox(
                        "Moderate reuse. Some objects may be getting destroyed instead of returned.",
                        MessageType.Warning);
                }
                else if (stats.TotalBorrowEvents > 10)
                {
                    EditorGUILayout.HelpBox(
                        "Low reuse efficiency! Objects are being destroyed rather than returned.\n" +
                        "Check that pooled objects call ReturnToPool() instead of Destroy().",
                        MessageType.Error);
                }
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();

            // Child Objects Section
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Pool Contents", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            int childCount = targetMonitor.transform.childCount;
            DrawStatRow("Child Objects:", childCount.ToString());

            if (childCount != stats.TotalInstances)
            {
                EditorGUILayout.HelpBox(
                    $"Child count ({childCount}) differs from total instances ({stats.TotalInstances}).\n" +
                    "Some pooled objects may be reparented while borrowed.",
                    MessageType.Info);
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();

            // Global Stats Section
            DrawGlobalStats();
        }

        private void DrawGlobalStats()
        {
            var globalStats = GONetMain.GetPoolManagerGlobalStats();

            if (globalStats.TotalPools <= 1)
            {
                return;
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Global Pool Manager (All Pools)", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            DrawStatRow("Total Active Pools:", globalStats.TotalPools.ToString());
            DrawStatRow("Total Borrow Requests:", globalStats.TotalBorrowRequests.ToString("N0"));
            DrawStatRow("Total Return Requests:", globalStats.TotalReturnRequests.ToString("N0"));
            DrawStatRow("Borrow Events Published:", globalStats.TotalBorrowEvents.ToString("N0"));
            DrawStatRow("Return Events Published:", globalStats.TotalReturnEvents.ToString("N0"));

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }

        private void DrawStatRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(160));
            EditorGUILayout.LabelField(value);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatRowWithColor(string label, string value, Color color)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(160));
            Color prevColor = GUI.contentColor;
            GUI.contentColor = color;
            EditorGUILayout.LabelField(value, EditorStyles.boldLabel);
            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();
        }
    }
}
