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

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
#if ADDRESSABLES_AVAILABLE
using UnityEditor.AddressableAssets;
#endif

namespace GONet.Editor
{
    [CustomEditor(typeof(GONetPooledObject))]
    public class GONetPooledObjectCustomInspector : UnityEditor.Editor
    {
        private GONetPooledObject targetPooledObject;

        private SerializedProperty initializeOnlyForScenesProp;

        private static List<string> cachedSceneNames;
        private static double cacheTimestamp;
        private const double CACHE_LIFETIME_SECONDS = 30.0;

        private string manualSceneInput = string.Empty;

        private void OnEnable()
        {
            targetPooledObject = (GONetPooledObject)target;
            initializeOnlyForScenesProp = serializedObject.FindProperty("initializeOnlyForScenes");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawPropertiesExcluding(serializedObject, "m_Script", "initializeOnlyForScenes");

            EditorGUILayout.Space(4);
            DrawSceneAllowlistSection();

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(10);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Pool Statistics:\n" +
                    "During Play Mode, select the pool root GameObject in the hierarchy\n" +
                    "(named \"[GONetPool] PrefabName (SceneName)\") to view real-time pool statistics.",
                    MessageType.Info);
                return;
            }

            DrawRuntimeInfo();
        }

        private void DrawSceneAllowlistSection()
        {
            EditorGUILayout.LabelField("Deferred Initialization", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "If populated, the pool will only be initialized when one of these scenes is loaded. " +
                "If empty, the pool initializes as soon as any scene loads.",
                MessageType.None);

            // Draw existing entries
            for (int i = 0; i < initializeOnlyForScenesProp.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();

                SerializedProperty element = initializeOnlyForScenesProp.GetArrayElementAtIndex(i);
                EditorGUILayout.LabelField(element.stringValue, EditorStyles.textField);

                if (GUILayout.Button("X", GUILayout.Width(22)))
                {
                    initializeOnlyForScenesProp.DeleteArrayElementAtIndex(i);
                    break;
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(2);

            // Add-from-dropdown
            EnsureSceneListCached();

            if (cachedSceneNames != null && cachedSceneNames.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel("Add from known scenes");

                if (EditorGUILayout.DropdownButton(new GUIContent("Select Scene..."), FocusType.Keyboard))
                {
                    GenericMenu menu = new GenericMenu();

                    // Build Settings header
                    menu.AddDisabledItem(new GUIContent("Build Settings"));
                    foreach (string sceneName in cachedSceneNames)
                    {
                        if (!sceneName.StartsWith("[Addressable]"))
                        {
                            bool alreadyAdded = IsSceneInList(sceneName);
                            if (alreadyAdded)
                            {
                                menu.AddDisabledItem(new GUIContent(sceneName + " (added)"));
                            }
                            else
                            {
                                string captured = sceneName;
                                menu.AddItem(new GUIContent(sceneName), false, () => AddSceneToList(captured));
                            }
                        }
                    }

                    // Addressable scenes
                    bool hasAddressable = false;
                    foreach (string sceneName in cachedSceneNames)
                    {
                        if (sceneName.StartsWith("[Addressable]"))
                        {
                            if (!hasAddressable)
                            {
                                menu.AddSeparator("");
                                menu.AddDisabledItem(new GUIContent("Addressable Scenes"));
                                hasAddressable = true;
                            }

                            string displayName = sceneName.Substring("[Addressable] ".Length);
                            bool alreadyAdded = IsSceneInList(displayName);
                            if (alreadyAdded)
                            {
                                menu.AddDisabledItem(new GUIContent(displayName + " (added)"));
                            }
                            else
                            {
                                string captured = displayName;
                                menu.AddItem(new GUIContent(displayName), false, () => AddSceneToList(captured));
                            }
                        }
                    }

                    menu.ShowAsContext();
                }

                if (GUILayout.Button("Refresh", GUILayout.Width(60)))
                {
                    cachedSceneNames = null;
                }

                EditorGUILayout.EndHorizontal();
            }

            // Manual text input
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Add manually");
            manualSceneInput = EditorGUILayout.TextField(manualSceneInput);

            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(manualSceneInput));
            if (GUILayout.Button("+", GUILayout.Width(22)))
            {
                string trimmed = manualSceneInput.Trim();
                if (!string.IsNullOrEmpty(trimmed) && !IsSceneInList(trimmed))
                {
                    AddSceneToList(trimmed);
                }
                manualSceneInput = string.Empty;
                GUI.FocusControl(null);
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
        }

        private bool IsSceneInList(string sceneName)
        {
            for (int i = 0; i < initializeOnlyForScenesProp.arraySize; i++)
            {
                if (initializeOnlyForScenesProp.GetArrayElementAtIndex(i).stringValue == sceneName)
                {
                    return true;
                }
            }
            return false;
        }

        private void AddSceneToList(string sceneName)
        {
            serializedObject.Update();
            int newIndex = initializeOnlyForScenesProp.arraySize;
            initializeOnlyForScenesProp.InsertArrayElementAtIndex(newIndex);
            initializeOnlyForScenesProp.GetArrayElementAtIndex(newIndex).stringValue = sceneName;
            serializedObject.ApplyModifiedProperties();
        }

        private static void EnsureSceneListCached()
        {
            if (cachedSceneNames != null && (EditorApplication.timeSinceStartup - cacheTimestamp) < CACHE_LIFETIME_SECONDS)
            {
                return;
            }

            cachedSceneNames = new List<string>(16);
            cacheTimestamp = EditorApplication.timeSinceStartup;

            // Build Settings scenes
            foreach (EditorBuildSettingsScene buildScene in EditorBuildSettings.scenes)
            {
                if (buildScene == null || string.IsNullOrEmpty(buildScene.path))
                {
                    continue;
                }

                string sceneName = Path.GetFileNameWithoutExtension(buildScene.path);
                if (!string.IsNullOrEmpty(sceneName))
                {
                    cachedSceneNames.Add(sceneName);
                }
            }

            // Addressable scenes
#if ADDRESSABLES_AVAILABLE
            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings != null)
                {
                    foreach (var group in settings.groups)
                    {
                        if (group == null) continue;
                        foreach (var entry in group.entries)
                        {
                            if (entry == null) continue;
                            string assetPath = AssetDatabase.GUIDToAssetPath(entry.guid);
                            if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".unity"))
                            {
                                string sceneName = Path.GetFileNameWithoutExtension(assetPath);
                                // Prefix to categorize, stripped when adding to the actual list
                                if (!cachedSceneNames.Contains("[Addressable] " + sceneName))
                                {
                                    cachedSceneNames.Add("[Addressable] " + sceneName);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Addressables not properly configured; silently skip
            }
#endif
        }

        private void DrawRuntimeInfo()
        {
            GONetParticipant participant = targetPooledObject.GetComponent<GONetParticipant>();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Pool Status", EditorStyles.boldLabel);

            if (participant == null)
            {
                EditorGUILayout.HelpBox("Missing GONetParticipant component.", MessageType.Error);
                EditorGUILayout.EndVertical();
                return;
            }

            // Show instance status
            EditorGUI.indentLevel++;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Is Pooled:", GUILayout.Width(120));
            Color prevColor = GUI.contentColor;
            GUI.contentColor = participant.isPooled ? Color.green : Color.yellow;
            EditorGUILayout.LabelField(participant.isPooled ? "Yes" : "No", EditorStyles.boldLabel);
            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();

            if (participant.isPooled)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Pool State:", GUILayout.Width(120));
                string state = participant.IsPooledInactive ? "Available (Inactive)" : "Borrowed (Active)";
                prevColor = GUI.contentColor;
                GUI.contentColor = participant.IsPooledInactive ? Color.green : new Color(1f, 0.7f, 0f);
                EditorGUILayout.LabelField(state, EditorStyles.boldLabel);
                GUI.contentColor = prevColor;
                EditorGUILayout.EndHorizontal();

                if (!participant.IsPooledInactive)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Borrower:", GUILayout.Width(120));
                    string borrower = participant.RemotelyControlledByAuthorityId != GONetMain.OwnerAuthorityId_Unset
                        ? $"Authority {participant.RemotelyControlledByAuthorityId}"
                        : "Unknown";
                    EditorGUILayout.LabelField(borrower);
                    EditorGUILayout.EndHorizontal();
                }

                // Button to select the pool root
                EditorGUILayout.Space(5);
                if (GUILayout.Button("Select Pool Root (View Full Statistics)"))
                {
                    SelectPoolRoot();
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "This instance is not currently managed by the pool system.\n" +
                    "It may be a prefab or spawned outside the pooling system.",
                    MessageType.Info);
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }

        private void SelectPoolRoot()
        {
            // Try to find the pool root by looking for GONetPoolMonitor components
            GONetPoolMonitor[] monitors = Object.FindObjectsByType<GONetPoolMonitor>(FindObjectsSortMode.None);

            GONetParticipant participant = targetPooledObject.GetComponent<GONetParticipant>();
            if (participant == null)
            {
                return;
            }

            string designTimeLocation = participant.DesignTimeLocation;
            if (string.IsNullOrWhiteSpace(designTimeLocation))
            {
                Debug.LogWarning("[GONet] Could not determine design time location for this pooled object.");
                return;
            }

            ushort designTimeIndex = GONetSpawnSupport_Runtime.GetDesignTimeLocationIndex(designTimeLocation);

            foreach (GONetPoolMonitor monitor in monitors)
            {
                if (monitor.DesignTimeLocationIndex == designTimeIndex)
                {
                    Selection.activeGameObject = monitor.gameObject;
                    EditorGUIUtility.PingObject(monitor.gameObject);
                    return;
                }
            }

            Debug.LogWarning("[GONet] Could not find pool root. The pool may not be initialized yet.");
        }
    }
}
