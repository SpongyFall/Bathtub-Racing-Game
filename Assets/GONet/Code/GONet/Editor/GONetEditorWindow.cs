using GONet.Generation;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace GONet.Editor
{
    public class GONetEditorWindow : EditorWindow
    {
        [MenuItem("GONet/GONet Editor Support")]
        public static void ShowWindow()
        {
            GONetEditorWindow editorWindow = EditorWindow.GetWindow<GONetEditorWindow>();
            const string GONET_EDITOR_SUPPORT = "GONet Editor Support";
            GUIContent titleContent = new GUIContent(GONET_EDITOR_SUPPORT);
            editorWindow.titleContent = titleContent;

            InitializeGUIStyles();
        }

        internal const string ASSETS_SYNC_SETTINGS_PROFILES_FOLDER_PATH = "Assets/GONet/Resources/GONet/SyncSettingsProfiles/";
        internal const string ASSET_FILE_EXTENSION = ".asset";

        string NAMEO_DEFAULT = "<Enter Name>";
        string nameo;

        string gonetIdText;
        string gonetIdText_raw;

        private static GUIStyle sectionHeaderGUIStyle = null;

        /// <summary>
        /// Tracks whether the Advanced Users section is expanded.
        /// Uses EditorPrefs to persist across sessions.
        /// </summary>
        private const string ADVANCED_SECTION_FOLDOUT_KEY = "GONet.EditorWindow.AdvancedSectionExpanded";
        private bool isAdvancedSectionExpanded
        {
            get => EditorPrefs.GetBool(ADVANCED_SECTION_FOLDOUT_KEY, false);
            set => EditorPrefs.SetBool(ADVANCED_SECTION_FOLDOUT_KEY, value);
        }

        /// <summary>
        /// Scroll position for the entire window content.
        /// </summary>
        private Vector2 windowScrollPosition;

        /// <summary>
        /// Cached GUIStyle for rich text help boxes.
        /// </summary>
        private static GUIStyle richTextHelpBoxStyle;

        /// <summary>
        /// Draws a help box that supports rich text (bold, color, etc.).
        /// Use HTML-style tags: &lt;b&gt;bold&lt;/b&gt;, &lt;color=#hex&gt;colored&lt;/color&gt;
        /// </summary>
        private static void DrawRichTextHelpBox(string message, MessageType messageType)
        {
            if (richTextHelpBoxStyle == null)
            {
                richTextHelpBoxStyle = new GUIStyle(EditorStyles.helpBox);
                richTextHelpBoxStyle.richText = true;
                richTextHelpBoxStyle.fontSize = 11;
                richTextHelpBoxStyle.padding = new RectOffset(8, 8, 8, 8);
            }

            // Get the icon based on message type
            GUIContent content;
            switch (messageType)
            {
                case MessageType.Info:
                    content = EditorGUIUtility.IconContent("console.infoicon.sml");
                    break;
                case MessageType.Warning:
                    content = EditorGUIUtility.IconContent("console.warnicon.sml");
                    break;
                case MessageType.Error:
                    content = EditorGUIUtility.IconContent("console.erroricon.sml");
                    break;
                default:
                    content = GUIContent.none;
                    break;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            if (content != GUIContent.none && content.image != null)
            {
                GUILayout.Label(content, GUILayout.Width(20), GUILayout.Height(20));
            }

            EditorGUILayout.LabelField(message, richTextHelpBoxStyle);

            EditorGUILayout.EndHorizontal();
        }

        private static void InitializeGUIStyles()
        {
            sectionHeaderGUIStyle = new GUIStyle();
            sectionHeaderGUIStyle.alignment = TextAnchor.MiddleCenter;
            sectionHeaderGUIStyle.fontStyle = FontStyle.Normal;
            sectionHeaderGUIStyle.normal.textColor = Color.white;
            sectionHeaderGUIStyle.fontSize = 18;
            sectionHeaderGUIStyle.fontStyle = FontStyle.Bold;
        }

        private void OnGUI()
        {
            // Ensure GUI styles are initialized (may be null after domain reload)
            if (sectionHeaderGUIStyle == null)
            {
                InitializeGUIStyles();
            }

            if (!Application.isPlaying)
            {
                OnGUI_IsNotPlaying();
            }
            else // since GONetId is not assigned nor visible in inspector until playing, only show this then
            {
                OnGUI_IsPlaying();
            }
        }

        private void OnGUI_IsPlaying()
        {
            { // GONetId
                EditorGUILayout.Separator();

                EditorGUILayout.BeginHorizontal();
                const string GNId = "GONetId";
                EditorGUILayout.LabelField(GNId, GUILayout.MaxWidth(60));
                gonetIdText = EditorGUILayout.TextField(gonetIdText);
                const string NUMS = @"[^a-zA-Z0-9 ]";
                gonetIdText = gonetIdText == null ? gonetIdText : Regex.Replace(gonetIdText, NUMS, string.Empty);
                const string SEL = "Select in Hierarchy";
                const string TOOLIOUL = "Select the GameObject in the Hierarchy with GONetParticipant installed with GONetId value equal to input field value.";
                GUIContent buttonTextWithTooltip = new GUIContent(SEL, TOOLIOUL);
                if (GUILayout.Button(buttonTextWithTooltip))
                {
                    uint gonetIdSearch;
                    if (uint.TryParse(gonetIdText, out gonetIdSearch))
                    {
                        Component component = FindObjectsOfType<GONetParticipant>().FirstOrDefault(gnp => gnp.GONetId == gonetIdSearch);
                        if (component != null)
                        {
                            Selection.activeGameObject = component.gameObject;
                        }
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            { // GONetId raw
                EditorGUILayout.Separator();

                EditorGUILayout.BeginHorizontal();
                const string GNId = "My GONetId (RAW)";
                EditorGUILayout.LabelField(GNId, GUILayout.MaxWidth(120));
                gonetIdText_raw = EditorGUILayout.TextField(gonetIdText_raw);
                const string NUMS = @"[^a-zA-Z0-9 ]";
                gonetIdText_raw = gonetIdText_raw == null ? gonetIdText_raw : Regex.Replace(gonetIdText_raw, NUMS, string.Empty);
                const string SEL = "Select in Hierarchy";
                const string TOOLIOUL = "Select the GameObject in the Hierarchy with GONetParticipant installed with GONetId (RAW) value equal to input field value -AND- Owner Authority Id value that matches \"mine.\"";
                GUIContent buttonTextWithTooltip = new GUIContent(SEL, TOOLIOUL);
                if (GUILayout.Button(buttonTextWithTooltip))
                {
                    uint gonetIdSearch;
                    if (uint.TryParse(gonetIdText_raw, out gonetIdSearch))
                    {
                        Component component = FindObjectsOfType<GONetParticipant>().FirstOrDefault(gnp => gnp.gonetId_raw == gonetIdSearch && gnp.OwnerAuthorityId == GONetMain.MyAuthorityId);
                        if (component != null)
                        {
                            Selection.activeGameObject = component.gameObject;
                        }
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void OnGUI_IsNotPlaying()
        {
            // Wrap entire content in scroll view for when content exceeds window size
            windowScrollPosition = EditorGUILayout.BeginScrollView(windowScrollPosition);

            EditorGUILayout.Separator();

            EditorGUILayout.BeginHorizontal();
            nameo = EditorGUILayout.TextField("New Sync Settings Profile", nameo);
            if (GUILayout.Button("Create"))
            {
                CreateSyncSettingsProfileAsset<GONetAutoMagicalSyncSettings_ProfileTemplate>(nameo);
                nameo = NAMEO_DEFAULT;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Separator();
            EditorGUILayout.Separator();
            EditorGUILayout.Separator();
            EditorGUILayout.LabelField("Refresh GONet's code generation", sectionHeaderGUIStyle);
            EditorGUILayout.Separator();

            const string REFRESH_TEXT = "GONet's code generation process is mostly automatic, triggered by specific Unity actions to ensure that GONet remains synchronized with the user's " +
                                        "networked code changes. However, there are situations where manual intervention is necessary to initiate this process.\nThere are two primary use " +
                                        "cases when users will need to manually refresh GONet's code generation:\n\n1. Before Entering Play Mode: It's essential to refresh GONet's code " +
                                        "generation manually if any changes (creation, modification, or deletion) related to GONet have been made since last manual refresh. This step " +
                                        "guarantees that the networked code is up-to-date and accurately reflects recent changes.\n\n2. When changing 'GONetAutoMagicalSync' fields: This " +
                                        "manual action is required when creating, modifying, or deleting a public field with the 'GONetAutoMagicalSync' attribute attached. Specially, when " +
                                        "the GameObject of that component also contains a 'GONetParticipant' component. By doing so, you ensure that synchronization is correctly established " +
                                        "between the components and their networked behavior. Also, by refreshing code generation, the user will have access to the related SyncEvent_GeneratedType " +
                                        "value in case the user wants to subscribe using GONetEventBus.Subscribe method.\n\nThese manual interventions ensure the integrity of your networked code in GONet, guaranteeing " +
                                        "that it remains synchronized with your Unity project's changes";
            EditorGUILayout.HelpBox(REFRESH_TEXT, MessageType.None);

            if (GUILayout.Button("Refresh GONet code generation"))
            {
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.UpdateAllUniqueSnaps();
                CloseWindowSafely(); // close safely to avoid GUI layout errors
                return;
            }

            EditorGUILayout.Separator();
            EditorGUILayout.Separator();
            EditorGUILayout.Separator();
            EditorGUILayout.LabelField("Fix GONet's code generation", sectionHeaderGUIStyle);
            EditorGUILayout.Separator();

            EditorStyles.label.wordWrap = true;
            const string FIX_TEXT = "Sometimes, code generation can get out of whack (i.e., when deleting/removing things in scene/project that are related to GONet) and the generated code will have " +
                                    "compilation errors as a result.  If you go to manually edit the generated code to fix the compilation errors, you will quickly see that the code generation " +
                                    "routine will come back and generate the code again and the compilation errors will return just as before.  The solution is to click this button to fix the issue " +
                                    "to cause GONet code generation to start all over again, forgetting what it had cached previously to aid in code generation and redo it all fresh. " +
                                    "This should fix things.  If not, please feel free to contact customer support by emailing contactus@galoreinteractive.com. " +
                                    "NOTE: After clicking this button you will have to focus away from the Unity Editor window and then bring focus back so Unity will recognize the changes and " +
                                    "recompile etc...";
            EditorGUILayout.HelpBox(FIX_TEXT, MessageType.Warning);
            if (GUILayout.Button("Fix GONet Generated Code"))
            {
                FixGONetGeneratedCode();
                CloseWindowSafely(); // close safely to avoid GUI layout errors
                return;
            }

            if (GUILayout.Button("Generate Runtime only scripts"))
            {
                GenerateRuntimeOnlyScripts();
                CloseWindowSafely(); // close safely to avoid GUI layout errors
                return;
            }
            if (GUILayout.Button("Delete Runtime only scripts"))
            {
                DeleteRuntimeOnlyScripts();
                CloseWindowSafely(); // close safely to avoid GUI layout errors
                return;
            }

            // ==================== ADVANCED USERS SECTION ====================
            EditorGUILayout.Separator();
            EditorGUILayout.Separator();
            EditorGUILayout.Separator();

            // Draw the entire advanced section in one unified container
            DrawAdvancedUsersSectionContainer();

            // End scroll view
            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Draws the entire Advanced Users section in a unified outer container with foldout.
        /// </summary>
        private void DrawAdvancedUsersSectionContainer()
        {
            var originalColor = GUI.backgroundColor;
            bool isEnabled = GONetProjectSettings.IsFastIterationModeEnabled;

            // Auto-expand when enabled so users see the active status
            if (isEnabled && !isAdvancedSectionExpanded)
            {
                isAdvancedSectionExpanded = true;
            }

            // Outer container with colored border effect
            // Use a distinct color to make the whole section stand out
            GUI.backgroundColor = isEnabled
                ? new Color(1f, 0.4f, 0.4f, 0.5f)  // Reddish when enabled (danger!)
                : new Color(0.7f, 0.7f, 0.7f, 0.5f); // Gray when disabled

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalColor;

            // Foldout header with warning styling
            GUIStyle foldoutHeaderStyle = new GUIStyle(EditorStyles.foldout);
            foldoutHeaderStyle.fontStyle = FontStyle.Bold;
            foldoutHeaderStyle.fontSize = 13;

            // Build the foldout label with status indicator
            string foldoutLabel = isEnabled
                ? "🧪⚠️ EXPERIMENTAL - Advanced Options (ACTIVE!) ⚠️🧪"
                : "🧪 EXPERIMENTAL - Advanced Options (click to expand)";

            // Color the foldout text based on status
            if (isEnabled)
            {
                foldoutHeaderStyle.normal.textColor = new Color(0.9f, 0.2f, 0.2f);
                foldoutHeaderStyle.onNormal.textColor = new Color(0.9f, 0.2f, 0.2f);
            }
            else
            {
                foldoutHeaderStyle.normal.textColor = new Color(0.6f, 0.4f, 0f);
                foldoutHeaderStyle.onNormal.textColor = new Color(0.6f, 0.4f, 0f);
            }

            isAdvancedSectionExpanded = EditorGUILayout.Foldout(isAdvancedSectionExpanded, foldoutLabel, true, foldoutHeaderStyle);

            if (isAdvancedSectionExpanded)
            {
                EditorGUILayout.Separator();

                // Draw header warnings
                DrawAdvancedUsersHeader();

                EditorGUILayout.Separator();

                // Draw the Fast Iteration Mode content
                DrawFastIterationModeContent();
            }
            else if (isEnabled)
            {
                // Show a compact warning when collapsed but enabled
                EditorGUILayout.HelpBox(
                    "⚠️ Fast Iteration Mode is ACTIVE! Click above to expand and see options.",
                    MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Draws the "Advanced Users Only" header with warning styling.
        /// </summary>
        private void DrawAdvancedUsersHeader()
        {
            // Header with warning symbols - no inner box needed, we're inside the outer container
            GUIStyle dangerHeaderStyle = new GUIStyle(EditorStyles.boldLabel);
            dangerHeaderStyle.alignment = TextAnchor.MiddleCenter;
            dangerHeaderStyle.fontSize = 14;
            dangerHeaderStyle.normal.textColor = new Color(1f, 0.4f, 0f); // Orange-red

            EditorGUILayout.LabelField("🧪 EXPERIMENTAL 🧪", dangerHeaderStyle);
            EditorGUILayout.LabelField("⚠️ ⚠️ ⚠️  ADVANCED USERS ONLY  ⚠️ ⚠️ ⚠️", dangerHeaderStyle);
            EditorGUILayout.LabelField("USE AT YOUR OWN RISK", dangerHeaderStyle);
        }

        /// <summary>
        /// Draws the Fast Iteration Mode content (without outer container - called from parent container).
        /// </summary>
        private void DrawFastIterationModeContent()
        {
            var originalColor = GUI.backgroundColor;
            bool isCurrentlyEnabled = GONetProjectSettings.IsFastIterationModeEnabled;

            // Section title
            GUIStyle sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel);
            sectionTitleStyle.fontSize = 12;
            sectionTitleStyle.alignment = TextAnchor.MiddleCenter;
            EditorGUILayout.LabelField("⚡ Fast Iteration Mode ⚡", sectionTitleStyle);

            EditorGUILayout.Separator();

            // Status indicator
            string statusText = isCurrentlyEnabled
                ? "🔴 ENABLED - Code generation skipped on Play Mode entry/exit"
                : "🟢 DISABLED - Normal code generation behavior (recommended)";
            GUIStyle statusStyle = new GUIStyle(EditorStyles.label);
            statusStyle.fontStyle = FontStyle.Bold;
            statusStyle.normal.textColor = isCurrentlyEnabled ? new Color(0.8f, 0f, 0f) : new Color(0f, 0.5f, 0f);
            EditorGUILayout.LabelField(statusText, statusStyle);

            EditorGUILayout.Separator();

            // What this does - concise version
            const string WHAT_IT_DOES =
                "<b>WHAT THIS DOES:</b> Skips GONet code generation on Play Mode entry/exit.\n" +
                "Code generates once on project open, deletes on Unity close.\n" +
                "<b><color=#00AA00>BENEFIT:</color></b> Significant time savings per Play Mode cycle (varies by project and hardware).";
            DrawRichTextHelpBox(WHAT_IT_DOES, MessageType.Info);

            // BEFORE YOU ENABLE - critical pre-requisites
            if (!isCurrentlyEnabled)
            {
                const string BEFORE_ENABLING =
                    "📋 <b>BEFORE ENABLING - READ THIS FIRST:</b>\n\n" +
                    "<b>1. CREATE A BUILD FIRST</b> if you plan to test multiplayer with build(s) + Editor:\n" +
                    "   Generated code must match between Editor and builds. Create your build\n" +
                    "   <b>before</b> enabling this mode, then iterate in Editor without rebuilding.\n\n" +
                    "<b>2. CONSIDER DISABLING DOMAIN RELOAD</b> for maximum speed:\n" +
                    "   <b>Edit > Project Settings > Editor > Enter Play Mode Settings</b>\n" +
                    "   ☑ Enable, then uncheck <b>'Reload Domain'</b>\n" +
                    "   GONet experimentally supports this - static state resets automatically.\n" +
                    "   Combined with Fast Iteration Mode, Play Mode entry becomes near-instant.\n\n" +
                    "<b>3. UNDERSTAND THE BEHAVIOR CHANGE:</b>\n" +
                    "   When enabled, code generates when the <b>editor opens</b> and deletes when the <b>editor closes</b>\n" +
                    "   (instead of on every Play Mode entry/exit).\n\n" +
                    "<b>4. NO GONET CHANGES</b> while iterating:\n" +
                    "   Do not modify <b><color=#FFAA00>[GONetAutoMagicalSync]</color></b> fields or GONetParticipant prefabs.\n" +
                    "   If you must, click <b><color=#4488FF>'Generate Runtime only scripts'</color></b> above afterward.";
                DrawRichTextHelpBox(BEFORE_ENABLING, MessageType.Warning);
            }

            // Critical risks - condensed
            const string CRITICAL_RISKS =
                "⚠️ <b><color=#FF4444>RISKS</color></b>\n" +
                "• <b>Compile errors</b> if you modify sync fields without regenerating\n" +
                "• <b>Silent data corruption</b> from stale serialization IDs\n" +
                "• <b>Build/Editor mismatch</b> if builds made with different generated code\n" +
                "• <b>Domain Reload disabled:</b> If you disable Domain Reload, your code must also reset\n" +
                "   static state. Use <b>[InitializeOnEnterPlayMode]</b> on a static method.\n" +
                "   GONet handles its own statics - you'll need to do the same for all your use cases.";
            DrawRichTextHelpBox(CRITICAL_RISKS, MessageType.Error);

            // Link to open GONet.cs example
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUIStyle linkStyle = new GUIStyle(EditorStyles.linkLabel);
            if (GUILayout.Button("See example: GONet.cs → ResetStaticsOnPlayMode()", linkStyle))
            {
                OpenGONetStaticResetExample();
            }
            EditorGUILayout.EndHorizontal();

            // Manual regeneration - condensed
            const string MANUAL_REGEN =
                "💡 <b>IF YOU MAKE GONET CHANGES:</b>\n" +
                "Click <b><color=#4488FF>'Generate Runtime only scripts'</color></b> above to manually regenerate.\n" +
                "Do this after modifying sync fields, prefabs, or if sync behavior seems wrong.";
            DrawRichTextHelpBox(MANUAL_REGEN, MessageType.Info);

            // Recovery - condensed
            const string RECOVERY =
                "🔧 <b>RECOVERY:</b> If broken, click <b><color=#4488FF>'Fix GONet Generated Code'</color></b> above,\n" +
                "or simply <b>DISABLE</b> this mode to trigger fresh regeneration.";
            DrawRichTextHelpBox(RECOVERY, MessageType.None);

            EditorGUILayout.Separator();

            // Show one-time cost warning if not enabled
            if (!isCurrentlyEnabled)
            {
                EditorGUILayout.HelpBox(
                    "⏱️ ONE-TIME COST: Enabling requires full code regeneration (time varies by project and hardware).",
                    MessageType.Info);
            }

            // The toggle button
            EditorGUILayout.BeginHorizontal();

            string buttonText = isCurrentlyEnabled
                ? "🛑 DISABLE Fast Iteration Mode (Recommended)"
                : "⚡ ENABLE Fast Iteration Mode (Use at your own risk!)";

            GUIStyle buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.fontStyle = FontStyle.Bold;
            buttonStyle.fontSize = 12;

            if (isCurrentlyEnabled)
            {
                // Green button to disable (safe action)
                GUI.backgroundColor = new Color(0.5f, 0.9f, 0.5f);
            }
            else
            {
                // Red button to enable (risky action)
                GUI.backgroundColor = new Color(1f, 0.6f, 0.4f);
            }

            if (GUILayout.Button(buttonText, buttonStyle, GUILayout.Height(30)))
            {
                if (isCurrentlyEnabled)
                {
                    // Disabling - safe, just do it
                    DisableFastIterationMode();
                }
                else
                {
                    // Enabling - show confirmation dialog
                    if (EditorUtility.DisplayDialog(
                        "🧪⚠️ Enable EXPERIMENTAL Fast Iteration Mode?",
                        "READ CAREFULLY BEFORE PROCEEDING:\n\n" +
                        "⏱️ ONE-TIME COST: Code regeneration now (time varies by project and hardware).\n\n" +
                        "BEFORE ENABLING:\n" +
                        "• CREATE A BUILD FIRST if testing multiplayer with build(s) + Editor\n" +
                        "• Consider disabling Domain Reload for maximum speed:\n" +
                        "  Edit > Project Settings > Editor > Enter Play Mode Settings\n\n" +
                        "WHILE ENABLED:\n" +
                        "• Do NOT modify [GONetAutoMagicalSync] fields or GONetParticipant prefabs\n" +
                        "• If you must, click 'Generate Runtime only scripts' afterward\n\n" +
                        "RISKS:\n" +
                        "• Compile errors if sync fields modified without regenerating\n" +
                        "• Silent data corruption from stale code\n" +
                        "• Build/Editor mismatch issues\n\n" +
                        "This feature is EXPERIMENTAL. Proceed?",
                        "Yes, I understand",
                        "Cancel"))
                    {
                        EnableFastIterationMode();
                    }
                }
            }

            GUI.backgroundColor = originalColor;
            EditorGUILayout.EndHorizontal();

            // Show domain reload tip when enabled
            if (isCurrentlyEnabled)
            {
                EditorGUILayout.Separator();
                const string DOMAIN_RELOAD_TIP =
                    "💡 <b>TIP:</b> For even faster iteration, disable Domain Reload:\n" +
                    "<b>Edit > Project Settings > Editor > Enter Play Mode Settings</b>\n" +
                    "☑ Enable, then uncheck 'Reload Domain'. GONet handles static state resets.";
                DrawRichTextHelpBox(DOMAIN_RELOAD_TIP, MessageType.Info);
            }
        }

        /// <summary>
        /// Enables Fast Iteration Mode and forces code regeneration.
        /// </summary>
        private void EnableFastIterationMode()
        {
            GONetLog.Warning("[GONet] 🧪⚠️ Enabling EXPERIMENTAL Fast Iteration Mode - forcing code regeneration...");

            // Enable the setting
            GONetProjectSettings.IsFastIterationModeEnabled = true;

            // Force regeneration to ensure code is fresh
            GONetProjectSettingsInitializer.ForceRegenerateForFastIterationMode();

            GONetLog.Warning("[GONet] 🧪⚠️ EXPERIMENTAL Fast Iteration Mode ENABLED. " +
                "Remember to DISABLE before modifying GONetAutoMagicalSync fields!");

            // Force repaint to update UI
            Repaint();
        }

        /// <summary>
        /// Disables Fast Iteration Mode and triggers normal behavior.
        /// </summary>
        private void DisableFastIterationMode()
        {
            GONetLog.Info("[GONet] Disabling Fast Iteration Mode - returning to normal code generation behavior.");

            // Disable the setting
            GONetProjectSettings.IsFastIterationModeEnabled = false;

            // Clear the startup flag so normal behavior resumes
            GONetProjectSettingsInitializer.ClearFastIterationStartupFlag();

            // Delete the cached generated files so they regenerate normally next time
            GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.DeleteGeneratedFiles();

            GONetLog.Info("[GONet] Fast Iteration Mode DISABLED. Normal Play Mode code generation restored.");

            // Force repaint to update UI
            Repaint();
        }

        /// <summary>
        /// Opens GONet.cs to the ResetStaticsOnPlayMode example method.
        /// </summary>
        private void OpenGONetStaticResetExample()
        {
            const string GONET_CS_PATH = "Assets/GONet/Code/GONet/Main/GONet.cs";
            const int RESET_STATICS_LINE = 388; // Line number of ResetStaticsOnPlayMode method

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(GONET_CS_PATH);
            if (script != null)
            {
                AssetDatabase.OpenAsset(script, RESET_STATICS_LINE);
            }
            else
            {
                GONetLog.Warning($"[GONet] Could not find {GONET_CS_PATH}");
            }
        }

        /// <summary>
        /// Safely closes the editor window by deferring the close operation.
        /// This prevents "GUI Error: Invalid GUILayout state" errors that occur when
        /// Close() is called in the middle of OnGUI while inside Begin/End layout blocks.
        /// </summary>
        private void CloseWindowSafely()
        {
            // Defer the close to after OnGUI completes to avoid GUI layout errors
            EditorApplication.delayCall += () =>
            {
                if (this != null) // Window might already be destroyed
                {
                    Close();
                }
            };

            // Exit the current GUI loop immediately to prevent further drawing
            GUIUtility.ExitGUI();
        }

        private void FixGONetGeneratedCode()
        {
            GONetLog.Debug($"FRAME: {Time.frameCount} .... FixGONetGeneratedCode (1)");


            if (File.Exists(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_ALL_UNIQUE_SNAPS_FILE_PATH))
            {
                File.Delete(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_ALL_UNIQUE_SNAPS_FILE_PATH);
            }

            if (File.Exists(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_IN_SCENE_UNIQUE_SNAPS_FILE_PATH))
            {
                File.Delete(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_IN_SCENE_UNIQUE_SNAPS_FILE_PATH);
            }

            if (File.Exists(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.ASSET_FOLDER_SNAPS_FILE))
            {
                File.Delete(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.ASSET_FOLDER_SNAPS_FILE);
            }

            /* This causes all to recompile and I believe it is unnecessary and it certain takes a long time!
            const string UNITY_LIBRARY_SCRIPT_ASSEMBLIES = "Library/ScriptAssemblies";
            if (Directory.Exists(UNITY_LIBRARY_SCRIPT_ASSEMBLIES))
            {
                foreach (string filePath in Directory.GetFiles(UNITY_LIBRARY_SCRIPT_ASSEMBLIES))
                {
                    File.Delete(filePath);
                }
            }
            */

            if (Directory.Exists(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_FILE_PATH))
            {
                // Delete generated files but preserve important ones:
                // - SyncEvent_GeneratedTypes.cs: Regenerated by UpdateAllUniqueSnaps() below
                // - GONet_SoA_Descriptor.cs: Design-time SoA configuration, preserved to avoid unnecessary regeneration
                // - AnimatorTriggerHashes.cs: Regenerated by UpdateAllUniqueSnaps() below
                foreach (string filePath in Directory.GetFiles(GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_FILE_PATH))
                {
                    string fileName = Path.GetFileName(filePath);
                    // Skip files that should be preserved (matching DeleteGeneratedFiles() behavior)
                    if (fileName.Contains(nameof(SyncEvent_GeneratedTypes)) ||
                        fileName.Contains("GONet_SoA_Descriptor") ||
                        fileName.Contains("AnimatorTriggerHashes"))
                    {
                        continue;
                    }
                    File.Delete(filePath);
                }
            }

            bool wasNewDtmForced = GONetSpawnSupport_Runtime.IsNewDtmForced;
            try
            {
                GONetLog.Debug($"FRAME: {Time.frameCount} .... FixGONetGeneratedCode (2)");
                GONetSpawnSupport_Runtime.IsNewDtmForced = true;

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                GONetLog.Debug($"FRAME: {Time.frameCount} .... FixGONetGeneratedCode (3)");
                GONetSpawnSupport_DesignTime.EnsureDesignTimeLocationsCurrent_ProjectOnly(); // TODO FIXME where does this belong to get all project:// to save to DTM.txt?!?!?!?

                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.UpdateAllUniqueSnaps();
                // NOTE: UpdateAllUniqueSnaps() regenerates SyncEvent_GeneratedTypes.cs
                // Runtime companion code (*_Generated.cs) will be regenerated when entering play mode via GenerateFiles()
            }
            finally
            {
                GONetSpawnSupport_Runtime.IsNewDtmForced = wasNewDtmForced;
                GONetLog.Debug($"FRAME: {Time.frameCount} .... FixGONetGeneratedCode (4)");
            }
        }

        internal static T CreateSyncSettingsProfileAsset<T>(string assetName) where T : ScriptableObject
        {
            T asset = ScriptableObject.CreateInstance<T>();

            string desiredPath = string.Concat(ASSETS_SYNC_SETTINGS_PROFILES_FOLDER_PATH, assetName, ASSET_FILE_EXTENSION);
            string assetPathAndName = AssetDatabase.GenerateUniqueAssetPath(desiredPath);

            //AssetDatabase.CreateAsset(asset, desiredPath);
            AssetDatabase.CreateAsset(asset, assetPathAndName);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;

            return asset;
        }

        private void GenerateRuntimeOnlyScripts()
        {
            GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GenerateFiles();
        }

        private void DeleteRuntimeOnlyScripts()
        {
            GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.DeleteGeneratedFiles();
        }
    }
}
