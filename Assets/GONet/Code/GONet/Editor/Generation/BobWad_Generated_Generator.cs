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

using Assets.GONet.Code.GONet.Editor.Generation;
using GONet.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace GONet.Generation
{
    internal static class BobWad_Generated_Generator
    {
        internal static void GenerateClass(byte[] usedCodegenIds, List<GONetParticipant_ComponentsWithAutoSyncMembers> allUniqueSnapsForPersistence, List<Type> allUniqueIGONetEventStructTypes)
        {
            var t4Template = new BobWad_GeneratedTemplate(usedCodegenIds, allUniqueSnapsForPersistence, allUniqueIGONetEventStructTypes);
            string generatedClassText = t4Template.TransformText();

            if (!Directory.Exists(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_FILE_PATH))
            {
                Directory.CreateDirectory(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_FILE_PATH);
            }
            const string GEN_D = "_Generated.cs";
            string writeToPath = string.Concat(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_FILE_PATH, nameof(BobWad), GEN_D);
            File.WriteAllText(writeToPath, generatedClassText);
        }
    }

    internal static class SyncEvent_GeneratedTypes_Generator
    {
        internal static void GenerateEnum(List<GONetParticipant_ComponentsWithAutoSyncMembers> allUniqueSnapsForPersistence)
        {
            var t4TemplateEnum = new SyncEvent_GeneratedTypes_GeneratedTemplate(allUniqueSnapsForPersistence);
            string generatedClassText = t4TemplateEnum.TransformText();

            if (!Directory.Exists(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_FILE_PATH))
            {
                Directory.CreateDirectory(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_FILE_PATH);
            }

            string writeToPath = string.Concat(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_FILE_PATH, nameof(SyncEvent_GeneratedTypes), ".cs");
            File.WriteAllText(writeToPath, generatedClassText);
        }
    }

    /// <summary>
    /// Generates a static class containing pre-computed hash constants for all Animator Trigger parameters
    /// found on GONetParticipants in the project. This allows users to call SetAnimatorTrigger(AnimatorTriggerHashes.Jump)
    /// instead of computing the hash manually with Animator.StringToHash("Jump").
    /// </summary>
    internal static class AnimatorTriggerHashes_Generator
    {
        /// <summary>
        /// Generates the AnimatorTriggerHashes.cs file containing pre-computed hash constants for all trigger parameters.
        /// SLOW VERSION - scans ALL prefabs in project. Use the overload that accepts knownGONetParticipants for better performance.
        /// </summary>
        internal static void GenerateHashes()
        {
            // Fallback to slow path - scan all prefabs
            GenerateHashes(null);
        }

        /// <summary>
        /// Generates the AnimatorTriggerHashes.cs file containing pre-computed hash constants for all trigger parameters.
        /// OPTIMIZED VERSION - uses pre-collected list of GONetParticipants instead of scanning all prefabs.
        /// </summary>
        /// <param name="knownGONetParticipants">Pre-collected list of GONetParticipants from Resources folders.
        /// If null, falls back to scanning all prefabs (slow).</param>
        internal static void GenerateHashes(List<GONetParticipant> knownGONetParticipants)
        {
            Stopwatch sw = Stopwatch.StartNew();
            HashSet<string> allTriggerNames = CollectAllTriggerParameterNames(knownGONetParticipants);
            GONetLog.Info($"[GONet Perf] AnimatorTriggerHashes: CollectAllTriggerParameterNames: {sw.ElapsedMilliseconds}ms");

            if (allTriggerNames.Count == 0)
            {
                // No triggers found - don't generate file (or generate empty file)
                // We still generate the file so it exists and can be referenced
            }

            sw.Restart();
            string generatedCode = GenerateCode(allTriggerNames);

            if (!Directory.Exists(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_FILE_PATH))
            {
                Directory.CreateDirectory(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_FILE_PATH);
            }

            string writeToPath = string.Concat(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_FILE_PATH, "AnimatorTriggerHashes.cs");
            File.WriteAllText(writeToPath, generatedCode);
            GONetLog.Info($"[GONet Perf] AnimatorTriggerHashes: GenerateCode and write file: {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// Collects all unique Animator Trigger parameter names from GONetParticipants.
        /// OPTIMIZED: Uses pre-collected list instead of scanning all prefabs.
        /// </summary>
        /// <param name="knownGONetParticipants">Pre-collected list of GONetParticipants. If null, falls back to scanning all prefabs.</param>
        private static HashSet<string> CollectAllTriggerParameterNames(List<GONetParticipant> knownGONetParticipants)
        {
            HashSet<string> triggerNames = new HashSet<string>();
            Stopwatch sw = Stopwatch.StartNew();

            if (knownGONetParticipants != null && knownGONetParticipants.Count > 0)
            {
                // FAST PATH: Use pre-collected GONetParticipants
                foreach (GONetParticipant gnp in knownGONetParticipants)
                {
                    if (gnp != null)
                    {
                        CollectTriggersFromGameObject(gnp.gameObject, triggerNames);
                    }
                }
                GONetLog.Info($"[GONet Perf] AnimatorTriggerHashes: Fast path - scanned {knownGONetParticipants.Count} known GONetParticipants: {sw.ElapsedMilliseconds}ms");
            }
            else
            {
                // SLOW PATH: Scan all prefabs (fallback for backwards compatibility)
                GONetLog.Warning("[GONet Perf] AnimatorTriggerHashes: Using SLOW path - scanning all prefabs. Consider passing knownGONetParticipants for better performance.");

                string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
                GONetLog.Info($"[GONet Perf] AnimatorTriggerHashes: FindAssets(t:Prefab) found {prefabGuids.Length} prefabs: {sw.ElapsedMilliseconds}ms");

                sw.Restart();
                int gnpPrefabCount = 0;
                foreach (string guid in prefabGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab != null)
                    {
                        GONetParticipant gnp = prefab.GetComponent<GONetParticipant>();
                        if (gnp != null)
                        {
                            gnpPrefabCount++;
                            CollectTriggersFromGameObject(prefab, triggerNames);
                        }
                    }
                }
                GONetLog.Info($"[GONet Perf] AnimatorTriggerHashes: Prefab loop ({prefabGuids.Length} total, {gnpPrefabCount} with GNP): {sw.ElapsedMilliseconds}ms");
            }

            // Also scan AnimatorControllers directly attached to known GONetParticipants
            // NOTE: We no longer scan ALL AnimatorControllers in the project - only those on GONetParticipants
            // This provides comprehensive coverage for GONet use cases while being much faster.

            return triggerNames;
        }

        /// <summary>
        /// Collects trigger parameter names from a GameObject's Animator component.
        /// </summary>
        private static void CollectTriggersFromGameObject(GameObject go, HashSet<string> triggerNames)
        {
            Animator animator = go.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                AnimatorController controller = animator.runtimeAnimatorController as AnimatorController;
                if (controller == null)
                {
                    // Handle AnimatorOverrideController
                    AnimatorOverrideController overrideController = animator.runtimeAnimatorController as AnimatorOverrideController;
                    if (overrideController != null)
                    {
                        controller = overrideController.runtimeAnimatorController as AnimatorController;
                    }
                }

                if (controller != null)
                {
                    CollectTriggersFromAnimatorController(controller, triggerNames);
                }
            }
        }

        /// <summary>
        /// Collects trigger parameter names from an AnimatorController.
        /// </summary>
        private static void CollectTriggersFromAnimatorController(AnimatorController controller, HashSet<string> triggerNames)
        {
            if (controller == null || controller.parameters == null)
                return;

            foreach (AnimatorControllerParameter param in controller.parameters)
            {
                if (param.type == AnimatorControllerParameterType.Trigger)
                {
                    // Only add valid C# identifiers
                    if (StringUtils.IsStringValidForCSharpNamingConventions(param.name))
                    {
                        triggerNames.Add(param.name);
                    }
                }
            }
        }

        /// <summary>
        /// Generates the C# code for the AnimatorTriggerHashes class.
        /// </summary>
        private static string GenerateCode(HashSet<string> triggerNames)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("// AUTO-GENERATED FILE - DO NOT EDIT MANUALLY");
            sb.AppendLine("// Generated by GONet code generation for Animator Trigger parameter hashes.");
            sb.AppendLine("// Use these constants with GONetParticipant.SetAnimatorTrigger(int) or");
            sb.AppendLine("// GONetParticipantCompanionBehaviour.SetAnimatorTrigger(int) for optimal performance.");
            sb.AppendLine("//");
            sb.AppendLine("// Example usage:");
            sb.AppendLine("//   SetAnimatorTrigger(AnimatorTriggerHashes.Jump);");
            sb.AppendLine("//");
            sb.AppendLine();
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine();
            sb.AppendLine("namespace GONet");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Pre-computed hash constants for Animator Trigger parameters found in the project.");
            sb.AppendLine("    /// Use these with <see cref=\"GONetParticipant.SetAnimatorTrigger(int)\"/> for optimal performance.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class AnimatorTriggerHashes");
            sb.AppendLine("    {");

            // Sort trigger names for consistent output
            List<string> sortedNames = triggerNames.OrderBy(n => n).ToList();

            foreach (string triggerName in sortedNames)
            {
                // Generate: public static readonly int Jump = Animator.StringToHash("Jump");
                sb.AppendLine($"        /// <summary>Hash for trigger parameter \"{triggerName}\".</summary>");
                sb.AppendLine($"        public static readonly int {triggerName} = Animator.StringToHash(\"{triggerName}\");");
                sb.AppendLine();
            }

            if (triggerNames.Count == 0)
            {
                sb.AppendLine("        // No Animator Trigger parameters found in the project.");
                sb.AppendLine("        // Add Animator Trigger parameters to AnimatorControllers on prefabs with GONetParticipant,");
                sb.AppendLine("        // then run GONet code generation to populate this class.");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }
    }
}