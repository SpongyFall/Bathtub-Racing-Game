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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

#if ADDRESSABLES_AVAILABLE
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Build;
#endif

namespace GONet.Editor
{
    /// <summary>
    /// sister class of <see cref="GONetSpawnSupport_Runtime"/>.
    /// </summary>
    [InitializeOnLoad]
    public static class GONetSpawnSupport_DesignTime
    {
        private const string BUILD_SETTINGS_DIRTY_REASON_PREFIX = "[BUILD_SETTINGS] ";
        private const string SNAP_DIRTY_REASON_PREFIX = "[SNAP] ";

        private static double lastPrefabStageClosedTime = -1;
        private static readonly double PREFAB_STAGE_TRANSITION_GRACE_PERIOD = 2.0; // seconds

        #region Deferred Change Detection Flags

        /// <summary>
        /// Session-persistent flag: hierarchy change occurred, prefab/addressables scan needed.
        /// Uses SessionState (survives domain reloads, cleared on Unity restart).
        /// </summary>
        private const string SESSION_KEY_PREFAB_SCAN_NEEDED = "GONet.PrefabScanNeeded";

        /// <summary>
        /// Session-persistent flag: project assets changed, project scan needed.
        /// </summary>
        private const string SESSION_KEY_PROJECT_SCAN_NEEDED = "GONet.ProjectScanNeeded";

        /// <summary>
        /// Session-persistent flag: scene hierarchy changed, scene scan needed.
        /// </summary>
        private const string SESSION_KEY_SCENE_SCAN_NEEDED = "GONet.SceneScanNeeded";

        /// <summary>
        /// Marks that prefab/addressables scan is needed. Instant O(1) operation.
        /// </summary>
        internal static void SetPrefabScanNeeded(bool needed)
        {
            SessionState.SetBool(SESSION_KEY_PREFAB_SCAN_NEEDED, needed);
        }

        /// <summary>
        /// Checks if prefab scan is needed. Defaults to TRUE for safety.
        /// </summary>
        internal static bool IsPrefabScanNeeded()
        {
            return SessionState.GetBool(SESSION_KEY_PREFAB_SCAN_NEEDED, true);
        }

        /// <summary>
        /// Marks that project scan is needed. Instant O(1) operation.
        /// </summary>
        internal static void SetProjectScanNeeded(bool needed)
        {
            SessionState.SetBool(SESSION_KEY_PROJECT_SCAN_NEEDED, needed);
        }

        /// <summary>
        /// Checks if project scan is needed. Defaults to TRUE for safety.
        /// </summary>
        internal static bool IsProjectScanNeeded()
        {
            return SessionState.GetBool(SESSION_KEY_PROJECT_SCAN_NEEDED, true);
        }

        /// <summary>
        /// Marks that scene scan is needed. Instant O(1) operation.
        /// </summary>
        internal static void SetSceneScanNeeded(bool needed)
        {
            SessionState.SetBool(SESSION_KEY_SCENE_SCAN_NEEDED, needed);
        }

        /// <summary>
        /// Checks if scene scan is needed. Defaults to TRUE for safety.
        /// </summary>
        internal static bool IsSceneScanNeeded()
        {
            return SessionState.GetBool(SESSION_KEY_SCENE_SCAN_NEEDED, true);
        }

        /// <summary>
        /// Clears all deferred scan flags. Called at build start or on manual reset.
        /// </summary>
        internal static void ClearAllDeferredScanFlags()
        {
            SetPrefabScanNeeded(false);
            SetProjectScanNeeded(false);
            SetSceneScanNeeded(false);
        }

        #endregion

        #region Team-Aware Dirty Check Gate

        private const double TEAM_DIRTY_CHECK_TIMEOUT_SECONDS = 30.0;
        private static Task<List<string>> pendingTeamDirtyCheckTask;
        private static double teamDirtyCheckStartTime = -1;
        private static bool teamDirtyCheckTimeoutLogged;
        private static bool bypassTeamDirtyCheckOnce;

        private static void ClearTeamDirtyCheckState()
        {
            pendingTeamDirtyCheckTask = null;
            teamDirtyCheckStartTime = -1;
            teamDirtyCheckTimeoutLogged = false;
        }

        private static bool TryDeferPlayModeForTeamAwareDirtyCheck()
        {
            if (!IsTeamAwareDirtyCheckingEnabled())
            {
                return false;
            }

            if (bypassTeamDirtyCheckOnce)
            {
                bypassTeamDirtyCheckOnce = false;
                return false;
            }

            if (pendingTeamDirtyCheckTask == null)
            {
                pendingTeamDirtyCheckTask = PerformContentBasedDirtyCheckingAsync();
                teamDirtyCheckStartTime = EditorApplication.timeSinceStartup;
                teamDirtyCheckTimeoutLogged = false;
                GONetLog.Warning("[GONet] Play mode blocked: running team-aware dirty check...");
            }

            return true;
        }

        private static void TeamAwareDirtyCheckUpdate()
        {
            if (pendingTeamDirtyCheckTask == null)
            {
                return;
            }

            if (!pendingTeamDirtyCheckTask.IsCompleted)
            {
                if (!teamDirtyCheckTimeoutLogged &&
                    EditorApplication.timeSinceStartup - teamDirtyCheckStartTime >= TEAM_DIRTY_CHECK_TIMEOUT_SECONDS)
                {
                    teamDirtyCheckTimeoutLogged = true;
                    GONetLog.Error(
                        $"[GONet] Play mode still blocked after {TEAM_DIRTY_CHECK_TIMEOUT_SECONDS:0} seconds " +
                        "while running the team-aware dirty check. " +
                        "This check must complete before play mode can start.");
                }

                return;
            }

            try
            {
                if (pendingTeamDirtyCheckTask.IsFaulted)
                {
                    GONetLog.Warning($"Error in content-based dirty checking: {pendingTeamDirtyCheckTask.Exception?.GetBaseException().Message}");
#if ADDRESSABLES_AVAILABLE
                    CheckForAddressablesChangesBeforePlayMode_Traditional();
#endif
                }
                else if (pendingTeamDirtyCheckTask.IsCanceled)
                {
                    GONetLog.Warning("Team-aware dirty checking was canceled. Proceeding without content-based verification.");
#if ADDRESSABLES_AVAILABLE
                    CheckForAddressablesChangesBeforePlayMode_Traditional();
#endif
                }
                else
                {
                    List<string> changes = pendingTeamDirtyCheckTask.Result;
                    ApplyContentBasedDirtyChanges(changes);
                }
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"Error finalizing team-aware dirty checking: {ex.Message}");
#if ADDRESSABLES_AVAILABLE
                CheckForAddressablesChangesBeforePlayMode_Traditional();
#endif
            }
            finally
            {
                ClearTeamDirtyCheckState();
                bypassTeamDirtyCheckOnce = true;
            }

            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                StartPlayModeRequestWatchdog("team-aware dirty check complete");
                EditorApplication.isPlaying = true;
            }
        }

        #endregion

        #region Play Mode Request Watchdog

        private const double PLAY_MODE_REQUEST_TIMEOUT_SECONDS = 30.0;
        private static double playModeRequestStartTime = -1;
        private static bool playModeRequestTimeoutLogged;
        private static bool playModeRequestPending;
        private static string playModeRequestReason;
        private static bool playModeFocusLossLogged;
        private static bool playUnfocusedOverrideAttempted;
        private static bool playUnfocusedOverrideEnabled;
        private static string playUnfocusedOverrideDetails;

        internal static void StartPlayModeRequestWatchdog(string reason)
        {
            if (!playModeRequestPending)
            {
                playModeRequestStartTime = EditorApplication.timeSinceStartup;
                playModeRequestTimeoutLogged = false;
                playModeFocusLossLogged = false;
            }

            playModeRequestPending = true;
            playModeRequestReason = string.IsNullOrEmpty(reason) ? "unknown" : reason;
        }

        private static void StopPlayModeRequestWatchdog()
        {
            playModeRequestPending = false;
            playModeRequestStartTime = -1;
            playModeRequestTimeoutLogged = false;
            playModeRequestReason = null;
            playModeFocusLossLogged = false;
        }

        private static void PlayModeRequestWatchdogUpdate()
        {
            if (!playModeRequestPending)
            {
                return;
            }

            if (EditorApplication.isPlaying)
            {
                StopPlayModeRequestWatchdog();
                return;
            }

            if (playModeRequestStartTime < 0)
            {
                playModeRequestStartTime = EditorApplication.timeSinceStartup;
            }

            if (!playModeRequestTimeoutLogged &&
                EditorApplication.timeSinceStartup - playModeRequestStartTime >= PLAY_MODE_REQUEST_TIMEOUT_SECONDS)
            {
                playModeRequestTimeoutLogged = true;
                GONetLog.Error(
                    $"[GONet] Play mode still not entered after {PLAY_MODE_REQUEST_TIMEOUT_SECONDS:0} seconds (reason: {playModeRequestReason}). " +
                    $"isPlayingOrWillChangePlaymode={EditorApplication.isPlayingOrWillChangePlaymode}, " +
                    $"isCompiling={EditorApplication.isCompiling}, editorFocused={IsEditorActive()}. " +
                    "Unity may pause play mode transitions while unfocused. Refocus the editor to continue.");
            }
        }

        internal static bool TryEnablePlayModeWhenUnfocused(out string details)
        {
            if (playUnfocusedOverrideAttempted)
            {
                details = playUnfocusedOverrideDetails;
                return playUnfocusedOverrideEnabled;
            }

            playUnfocusedOverrideAttempted = true;
            playUnfocusedOverrideEnabled = TryEnablePlayModeWhenUnfocusedInternal(out playUnfocusedOverrideDetails);
            details = playUnfocusedOverrideDetails;

            if (playUnfocusedOverrideEnabled)
            {
                GONetLog.Info($"[GONet] Enabled play-unfocused override ({details}).");
            }
            else if (!string.IsNullOrEmpty(details))
            {
                GONetLog.Warning(
                    $"[GONet] Play-unfocused hint applied ({details}) but no verified override is available. " +
                    "Unity may still defer play mode while unfocused.");
            }
            else
            {
                GONetLog.Warning("[GONet] Unable to enable play-unfocused override; Unity may defer play mode while unfocused.");
            }

            return playUnfocusedOverrideEnabled;
        }

        private static bool TryEnablePlayModeWhenUnfocusedInternal(out string details)
        {
            details = null;

            if (TrySetPlayModeViewBehaviorUnfocused(out details))
            {
                return true;
            }

            if (TrySetInternalPlayModeFocused(false, out details))
            {
                return true;
            }

            if (TrySetEditorPrefPlayUnfocused(out details))
            {
                return false;
            }

            return false;
        }

        private static bool TrySetPlayModeViewBehaviorUnfocused(out string details)
        {
            details = null;
            Type playModeViewType = Type.GetType("UnityEditor.PlayModeView, UnityEditor");
            if (playModeViewType == null)
            {
                return false;
            }

            PropertyInfo behaviorProp = playModeViewType.GetProperty(
                "enterPlayModeBehavior",
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (behaviorProp == null || !behaviorProp.PropertyType.IsEnum)
            {
                return false;
            }

            string unfocusedName = null;
            foreach (string name in Enum.GetNames(behaviorProp.PropertyType))
            {
                if (string.Equals(name, "PlayUnfocused", StringComparison.OrdinalIgnoreCase))
                {
                    unfocusedName = name;
                    break;
                }
            }

            if (string.IsNullOrEmpty(unfocusedName))
            {
                return false;
            }

            object target = null;
            MethodInfo getter = behaviorProp.GetGetMethod(true);
            if (getter != null && !getter.IsStatic)
            {
                MethodInfo getMain = playModeViewType.GetMethod(
                    "GetMainPlayModeView",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                target = getMain?.Invoke(null, null);
                if (target == null)
                {
                    return false;
                }
            }

            object enumValue = Enum.Parse(behaviorProp.PropertyType, unfocusedName);
            behaviorProp.SetValue(target, enumValue);
            details = "PlayModeView.enterPlayModeBehavior=PlayUnfocused";
            return true;
        }

        private static bool TrySetInternalPlayModeFocused(bool playFocused, out string details)
        {
            details = null;
            Type internalType = Type.GetType("UnityEditorInternal.InternalEditorUtility, UnityEditor");
            if (internalType == null)
            {
                return false;
            }

            MethodInfo setter = internalType.GetMethod(
                "SetPlayModeFocused",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (setter == null)
            {
                setter = internalType.GetMethod(
                    "SetPlayModeFocus",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }

            if (setter == null)
            {
                return false;
            }

            setter.Invoke(null, new object[] { playFocused });
            details = "InternalEditorUtility.SetPlayModeFocused(false)";
            return true;
        }

        private static bool TrySetEditorPrefPlayUnfocused(out string details)
        {
            const string key = "PlayUnfocused";
            details = null;
            try
            {
                EditorPrefs.SetBool(key, true);
                details = "EditorPrefs.PlayUnfocused=true (unverified)";
                return true;
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"[GONet] Failed to set EditorPrefs '{key}': {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Deferred Dirty Warning Dialog

        private const string SESSION_KEY_DIRTY_WARNING_PENDING = "GONet.DirtyWarningPending";
        private const double DIRTY_WARNING_PENDING_TIMEOUT_SECONDS = 30.0;
        private static double dirtyWarningPendingStartTime = -1;
        private static bool dirtyWarningTimeoutLogged;

        private static bool IsDirtyWarningDialogPending()
        {
            return SessionState.GetBool(SESSION_KEY_DIRTY_WARNING_PENDING, false);
        }

        private static void SetDirtyWarningDialogPending(bool pending)
        {
            SessionState.SetBool(SESSION_KEY_DIRTY_WARNING_PENDING, pending);
        }

        private static void ClearDirtyWarningDialogPending()
        {
            SetDirtyWarningDialogPending(false);
            dirtyWarningPendingStartTime = -1;
            dirtyWarningTimeoutLogged = false;
        }

        internal static bool IsEditorActive()
        {
            PropertyInfo isFocusedProp = typeof(EditorApplication).GetProperty(
                "isFocused",
                BindingFlags.Public | BindingFlags.Static);
            if (isFocusedProp != null && isFocusedProp.PropertyType == typeof(bool))
            {
                return (bool)isFocusedProp.GetValue(null);
            }

            Type internalUtilityType = Type.GetType("UnityEditorInternal.InternalEditorUtility, UnityEditor");
            PropertyInfo isActiveProp = internalUtilityType?.GetProperty(
                "isApplicationActive",
                BindingFlags.Public | BindingFlags.Static);
            if (isActiveProp != null && isActiveProp.PropertyType == typeof(bool))
            {
                return (bool)isActiveProp.GetValue(null);
            }

            return true;
        }

        private static void DeferDirtyWarningDialog()
        {
            if (IsDirtyWarningDialogPending())
            {
                return;
            }

            SetDirtyWarningDialogPending(true);
            dirtyWarningPendingStartTime = EditorApplication.timeSinceStartup;
            dirtyWarningTimeoutLogged = false;
            GONetLog.Warning("[GONet] Play mode blocked: dirty warning dialog deferred until the editor regains focus.");
            EditorApplication.delayCall += TryShowDeferredDirtyWarningDialog;
        }

        private static void DeferredDirtyWarningDialogUpdate()
        {
            if (!IsDirtyWarningDialogPending())
            {
                return;
            }

            if (dirtyWarningPendingStartTime < 0)
            {
                dirtyWarningPendingStartTime = EditorApplication.timeSinceStartup;
            }

            if (IsEditorActive())
            {
                TryShowDeferredDirtyWarningDialog();
                return;
            }

            if (!dirtyWarningTimeoutLogged &&
                EditorApplication.timeSinceStartup - dirtyWarningPendingStartTime >= DIRTY_WARNING_PENDING_TIMEOUT_SECONDS)
            {
                dirtyWarningTimeoutLogged = true;
                GONetLog.Error(
                    $"[GONet] Play mode still blocked after {DIRTY_WARNING_PENDING_TIMEOUT_SECONDS:0} seconds " +
                    "because the dirty warning dialog could not be shown (editor not focused). " +
                    "Return focus to review the warning.");
            }
        }

        private static void TryShowDeferredDirtyWarningDialog()
        {
            if (!IsDirtyWarningDialogPending())
            {
                return;
            }

            if (!IsEditorActive())
            {
                return;
            }

            string filePath = GetDesignTimeDirtyReasonsFilePath();
            if (!File.Exists(filePath))
            {
                ClearDirtyWarningDialogPending();
                return;
            }

            if (GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.WasDirtyWarningAcknowledged)
            {
                ClearDirtyWarningDialogPending();
                return;
            }

            string fileContents = GetLimitedFilePreview(filePath, 10);
            bool didPreventEnteringPlaymode = ShowGONetWarning_ShouldPreventEnteringPlaymode(fileContents);

            ClearDirtyWarningDialogPending();

            if (!didPreventEnteringPlaymode)
            {
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.MarkDirtyWarningAcknowledged();
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    StartPlayModeRequestWatchdog("dirty warning proceed");
                    EditorApplication.isPlaying = true;
                }
            }
        }

        private static void OnEditorFocusChanged(bool hasFocus)
        {
            if (hasFocus)
            {
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator
                    .TryResumePendingPlayModeAfterGenerationOnFocus();
                TryShowDeferredDirtyWarningDialog();
                if (playModeRequestPending && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    GONetLog.Warning(
                        $"[GONet] Editor regained focus while play mode request is pending (reason: {playModeRequestReason}). Retrying play mode entry.");
                    StartPlayModeRequestWatchdog("focus regained resume");
                    EditorApplication.isPlaying = true;
                }
                return;
            }

            if (playModeRequestPending && !EditorApplication.isPlaying && !playModeFocusLossLogged)
            {
                playModeFocusLossLogged = true;
                GONetLog.Warning(
                    $"[GONet] Editor lost focus while entering play mode (reason: {playModeRequestReason}). " +
                    "Unity may pause play mode transitions while unfocused; refocus the editor to continue.");
            }
        }

        #endregion
        private static bool IsCompiling
        {
            get => EditorPrefs.GetBool(IsCompilingKey, false);
            set { EditorPrefs.SetBool(IsCompilingKey, value); /*GONetLog.Debug($"Setting IsCompiling to: {value}");*/ }
        }

        private static void OnCompilationStarted(object obj)
        {
            IsCompiling = true;
            //GONetLog.Debug("......................................................COMPILE start");
        }

        private static void OnCompilationFinished(object obj)
        {
            //GONetLog.Debug("COMPILE end - setting up delay");

            // Use delay call to ensure this runs after Unity settles post-compilation
            EditorApplication.delayCall += () =>
            {
                //GONetLog.Debug("......................................................COMPILE end (after delay)");
                //IsCompiling = false;
            };
        }
        private static void OnBeforeAssemblyReload()
        {
            //GONetLog.Debug("Before assembly reload - still compiling...");
            IsCompiling = true;
        }

        private static void OnAfterAssemblyReload()
        {
            //GONetLog.Debug("After assembly reload - now it's safe to reset flags.");
            // Use delay call to ensure this runs after Unity settles post-compilation
            EditorApplication.delayCall += () =>
            {
                //GONetLog.Debug("......................................................COMPILE end (after delay)");
                IsCompiling = false;
            };
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            // Handle actions post-reload (useful for checking compilation state)
            if (EditorPrefs.HasKey(IsCompilingKey) && EditorPrefs.GetBool(IsCompilingKey, false))
            {
                //GONetLog.Debug("Scripts reloaded while compiling; performing post-compilation cleanup.");
                OnCompilationFinished(null); // Ensure post-compilation cleanup runs
            }
        }

        private static void CompilationRecoveryCheck()//
        {
            /*
            // If IsCompiling is true but Unity is not actually compiling, reset the state
            if (IsCompiling && !EditorApplication.isCompiling)
            {
                GONetLog.Debug("Recovery mechanism: resetting IsCompiling as Unity is not compiling.");
                EditorApplication.delayCall += () =>
                {
                    GONetLog.Debug("......................................................COMPILE end (after delay)");
                    IsCompiling = false;
                };
            }
            */
        }
        internal static bool IsInitialEditorLoad { get; private set; } = true;
        internal static bool IsQuitting { get; set; }

        private const string IsCompilingKey = "GONet_IsCompiling"; // Key for EditorPrefs

        static GONetSpawnSupport_DesignTime()
        {
            /* GONet v1.4 only does stuff in the build....not "in real time" like this:
            EditorApplication.hierarchyChanged += OnHierarchyChanged_EnsureDesignTimeLocationsCurrent_SceneOnly;

#if UNITY_2018_1_OR_NEWER
            EditorApplication.projectChanged += OnProjectChanged_EnsureDesignTimeLocationsCurrent_ProjectOnly;
#else
            EditorApplication.projectWindowChanged += OnProjectChanged;
#endif
            */

            // IMPORTANT: Unregister event handlers first to prevent accumulation during domain reloads
            // This prevents the warning accumulation issue where 1 warning becomes 3
            EditorApplication.hierarchyChanged -= OnHierarchyChanged_TakeNoteOfAnyGONetChanges_SceneOnly;
            EditorApplication.projectChanged -= OnProjectChanged_TakeNoteOfAnyGONetChanges_ProjectOnly;

            GONetParticipant.OnDestroyCalled -= GONetParticipant_OnDestroyCalled;
            GONetParticipant.OnAwakeEditor -= GONetParticipant_OnAwakeEditor;
            GONetParticipant.OnEnableEditor -= GONetParticipant_OnEnableEditor;
            GONetParticipant.OnDisableEditor -= GONetParticipant_OnDisableEditor;
            GONetParticipant.OnValidateEditor -= GONetParticipant_OnValidateEditor;

            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;

            EditorApplication.update -= CompilationRecoveryCheck;
            EditorApplication.update -= ResetInitialEditorLoadFlag;
            EditorApplication.update -= TeamAwareDirtyCheckUpdate;
            EditorApplication.update -= DeferredDirtyWarningDialogUpdate;
            EditorApplication.update -= PlayModeRequestWatchdogUpdate;

            SceneManager.sceneLoaded -= OnSceneLoaded;
            EditorSceneManager.sceneOpened -= EditorSceneManager_sceneOpened;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.focusChanged -= OnEditorFocusChanged;

            // Now register the event handlers
            // Instead, in v1.4+, we just monitor GONet related changes and take note that there are changes from last build to warn users later
            EditorApplication.hierarchyChanged += OnHierarchyChanged_TakeNoteOfAnyGONetChanges_SceneOnly;
            EditorApplication.projectChanged += OnProjectChanged_TakeNoteOfAnyGONetChanges_ProjectOnly;

#if ADDRESSABLES_AVAILABLE
            // Hook into Addressables build completion events for more robust change detection
            RegisterAddressableBuildCallbacks();
#endif

            GONetParticipant.OnDestroyCalled += GONetParticipant_OnDestroyCalled;
            GONetParticipant.OnAwakeEditor += GONetParticipant_OnAwakeEditor;
            GONetParticipant.OnEnableEditor += GONetParticipant_OnEnableEditor;
            GONetParticipant.OnDisableEditor += GONetParticipant_OnDisableEditor;
            GONetParticipant.OnValidateEditor += GONetParticipant_OnValidateEditor;

            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;

            // Hook into prefab stage events to track when prefab stage closes
            UnityEditor.SceneManagement.PrefabStage.prefabStageClosing += OnPrefabStageClosing;

            // Recover the IsCompiling state from EditorPrefs (in case of domain reload)
            if (EditorPrefs.HasKey(IsCompilingKey) && EditorPrefs.GetBool(IsCompilingKey, false))
            {
                //GONetLog.Debug("Recovered from domain reload after compilation. Performing post-compilation actions.");
                //OnCompilationFinished(null); // Run the post-compilation logic
            }

            // Periodic recovery check to handle edge cases like crashes
            EditorApplication.update += CompilationRecoveryCheck;
            EditorApplication.update += ResetInitialEditorLoadFlag;
            EditorApplication.update += TeamAwareDirtyCheckUpdate;
            EditorApplication.update += DeferredDirtyWarningDialogUpdate;
            EditorApplication.update += PlayModeRequestWatchdogUpdate;

            //SceneManager.sceneLoading += OnSceneLoading;
            SceneManager.sceneLoaded += OnSceneLoaded;
            EditorSceneManager.sceneOpened += EditorSceneManager_sceneOpened;

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.focusChanged += OnEditorFocusChanged;
        }

        /// <summary>
        /// Performs immediate change detection before play mode transition.
        /// Uses either traditional timestamp-based checking or advanced content-based checking
        /// depending on the team-aware dirty checking configuration.
        /// </summary>
        private static bool CheckForChangesBeforePlayMode()
        {
            try
            {
                if (TryDeferPlayModeForTeamAwareDirtyCheck())
                {
                    return true;
                }

                // Use traditional addressables checking for solo development
#if ADDRESSABLES_AVAILABLE
                CheckForAddressablesChangesBeforePlayMode_Traditional();
#endif

                return false;
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"Error in pre-play mode change detection: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if team-aware dirty checking is enabled by looking at GONetProjectSettings configuration.
        /// </summary>
        internal static bool IsTeamAwareDirtyCheckingEnabled()
        {
            try
            {
                var projectSettings = GONetProjectSettings.Instance;
                bool isEnabled = projectSettings != null && projectSettings.enableTeamAwareDirtyChecking;
                GONetLog.Debug($"IsTeamAwareDirtyCheckingEnabled - projectSettings: {(projectSettings != null ? "found" : "null")}, enableTeamAwareDirtyChecking: {(projectSettings?.enableTeamAwareDirtyChecking ?? false)}, result: {isEnabled}");
                return isEnabled;
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"Error checking team-aware dirty checking setting: {ex.Message}");
                return false; // Default to false if we can't determine
            }
        }

        /// <summary>
        /// Performs advanced content-based dirty checking for team environments.
        /// Uses multi-threaded content hashing to detect actual GONet-relevant changes.
        /// </summary>
        private static async Task<List<string>> PerformContentBasedDirtyCheckingAsync()
        {
            //GONetLog.Debug("Starting team-aware content-based dirty checking..."); // COMMENTED - spammy log (log cleanup)

            // Create current content snapshot
            var currentSnapshot = await GONetContentSnapshot.CreateSnapshotAsync();

            // Load previous snapshot from last build
            string snapshotPath = GetContentSnapshotFilePath();
            var previousSnapshot = GONetContentSnapshot.LoadSnapshot(snapshotPath);

            // Compare snapshots to find changes
            return GONetContentSnapshot.CompareSnapshots(currentSnapshot, previousSnapshot);
        }

        private static void ApplyContentBasedDirtyChanges(List<string> changes)
        {
            if (changes == null)
            {
                return;
            }

            foreach (var change in changes)
            {
                AddGONetDesignTimeDirtyReason($"Team-aware detection: {change}");
                //GONetLog.Debug($"Content-based detection: {change}"); // COMMENTED - spammy log (log cleanup)
            }
        }

        /// <summary>
        /// Traditional addressables change detection for solo development (faster, timestamp-based).
        /// </summary>
#if ADDRESSABLES_AVAILABLE
        private static void CheckForAddressablesChangesBeforePlayMode_Traditional()
        {
            try
            {
                // Get current addressables GONetParticipants
                var currentAddressableGNPs = GatherAddressableGONetParticipants();

                // Get last build metadata for addressables prefabs
                var lastBuildMetadata = GONetSpawnSupport_Runtime.LoadDesignTimeMetadataFromPersistence()
                    .Where(m => m.Location.StartsWith(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX))
                    .ToList();

                // Convert current addressables to location format for comparison
                var currentAddressableLocations = new HashSet<string>();
                foreach (var gnp in currentAddressableGNPs)
                {
                    string assetPath = AssetDatabase.GetAssetPath(gnp);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        string location = $"{GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX}{assetPath}";
                        currentAddressableLocations.Add(location);
                    }
                }

                // Check for new addressable prefabs
                foreach (var currentLocation in currentAddressableLocations)
                {
                    if (!lastBuildMetadata.Any(m => m.Location == currentLocation))
                    {
                        string dirtyReason = $"GONetParticipant at {currentLocation} was added or modified after the last build.";
                        AddGONetDesignTimeDirtyReason(dirtyReason);
                        //GONetLog.Debug($"Traditional detection: {dirtyReason}"); // COMMENTED - spammy log (log cleanup)
                    }
                }

                // Check for removed addressable prefabs
                foreach (var lastBuildMeta in lastBuildMetadata)
                {
                    if (!currentAddressableLocations.Contains(lastBuildMeta.Location))
                    {
                        string dirtyReason = $"GONetParticipant prefab removed from addressables: {lastBuildMeta.Location}";
                        AddGONetDesignTimeDirtyReason(dirtyReason);
                        //GONetLog.Debug($"Traditional detection: {dirtyReason}"); // COMMENTED - spammy log (log cleanup)
                    }
                }
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"Error in traditional addressables check: {ex.Message}");
            }
        }
#endif

        /// <summary>
        /// Gets the file path for storing content snapshots.
        /// </summary>
        private static string GetContentSnapshotFilePath()
        {
            string folderPath = GetDesignTimeDirtyReasonFolder();
            return Path.Combine(folderPath, "GONetTeamAwareDirtyCheckSnapshot_MemoryPack.bin");
        }

        private static void OnPrefabStageClosing(UnityEditor.SceneManagement.PrefabStage stage)
        {
            // Track when the prefab stage is closing
            lastPrefabStageClosedTime = EditorApplication.timeSinceStartup;
            //GONetLog.Debug($"Prefab stage closing - setting grace period timestamp: {lastPrefabStageClosedTime}"); // COMMENTED - spammy log (log cleanup)
        }

        /// <summary>
        /// Rock-solid solution for Unity 6.2: Check if a successful build was completed after the dirty file was created.
        /// This is called on play mode entry and is guaranteed to work regardless of callback timing issues.
        /// </summary>
        private static bool WasSuccessfulBuildCompletedSinceDirtyFile(string dirtyFilePath)
        {
            try
            {
                if (!File.Exists(dirtyFilePath))
                {
                    return false;
                }

                DateTime dirtyFileTime = File.GetLastWriteTimeUtc(dirtyFilePath);

                if (TryGetGONetMostRecentSuccessfulBuild(out GONetMostRecentSuccessfulBuild buildRecord))
                {
                    DateTime buildTime = buildRecord.DateTimeBuildSucceeded.ToUniversalTime();

                    // If build happened after dirty file was created, we should clean it up
                    bool buildIsNewer = buildTime > dirtyFileTime;
                    return buildIsNewer;
                }
                else
                {
                    return false;
                }
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"Error checking if build completed since dirty file: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Performs all deferred change detection. Called at strategic validation points.
        /// </summary>
        internal static void PerformDeferredChangeDetection()
        {
            try
            {
                if (IsPrefabScanNeeded())
                {
                    GONetLog.Debug("[Deferred Detection] Performing deferred prefab scan");
                    HandlePotentialChangeInPrefabPreviewMode_ProcessAnyDesignTimeDirty_IfAppropriate_Original();
                    SetPrefabScanNeeded(false);
                }

                if (IsProjectScanNeeded())
                {
                    GONetLog.Debug("[Deferred Detection] Performing deferred project scan");
                    OnProjectChanged_TakeNoteOfAnyGONetChanges_ProjectOnly_Original();
                    SetProjectScanNeeded(false);
                }

                if (IsSceneScanNeeded())
                {
                    GONetLog.Debug("[Deferred Detection] Performing deferred scene scan");
                    PerformSceneChangeDetection_Original();
                    SetSceneScanNeeded(false);
                }
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"[Deferred Detection] Error: {ex.Message}");
                ClearAllDeferredScanFlags(); // Prevent infinite retry loops
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                StopPlayModeRequestWatchdog();
            }

            // Check when Unity is about to enter play mode (ExitingEditMode)
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                // CRITICAL: Check if a successful build happened since dirty file was created
                // This handles Unity 6.2 where OnPostprocessBuild is unreliable
                string filePath = GetDesignTimeDirtyReasonsFilePath();
                bool buildJustCompleted = false;
                if (File.Exists(filePath))
                {
                    if (WasSuccessfulBuildCompletedSinceDirtyFile(filePath))
                    {
                        File.Delete(filePath);
                        // Also clear deferred scan flags to prevent re-adding dirty reasons
                        ClearAllDeferredScanFlags();
                        buildJustCompleted = true;
                    }
                }

                // Only run deferred detection if a build didn't just complete.
                // If a build just completed, the project state is known-good and
                // running deferred detection could produce false positives.
                if (!buildJustCompleted && GONetProjectSettings.IsDeferredChangeDetectionEnabled())
                {
                    PerformDeferredChangeDetection();
                }

                // IMPORTANT: Check for changes BEFORE checking for dirty file existence
                // This ensures changes are detected and recorded before the play mode warning check
                // Uses either traditional or content-based checking depending on configuration
                bool didPreventEnteringPlaymode = false;
                if (CheckForChangesBeforePlayMode())
                {
                    didPreventEnteringPlaymode = true;
                    EditorApplication.isPlaying = false;
                    return;
                }

                AddDirtyReasonIfScenesInBuildDiffer(filePath);
                AddDirtyReasonIfSnapsDifferSinceLastBuild();

                // Check for the existence of the "is dirty" file
                if (File.Exists(filePath))
                {
                    // Check if warning was already acknowledged this session (prevents double dialog when compilation gate defers play mode)
                    if (GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.WasDirtyWarningAcknowledged)
                    {
                        // User already acknowledged the warning, don't show it again
                        didPreventEnteringPlaymode = false;
                    }
                    else
                    {
                        if (!IsEditorActive())
                        {
                            DeferDirtyWarningDialog();
                            didPreventEnteringPlaymode = true;
                            EditorApplication.isPlaying = false;
                            return;
                        }

                        if (IsDirtyWarningDialogPending())
                        {
                            ClearDirtyWarningDialogPending();
                        }

                        // Read the contents of the file
                        string fileContents = GetLimitedFilePreview(filePath, 10);

                        // Show a warning to the user
                        didPreventEnteringPlaymode = ShowGONetWarning_ShouldPreventEnteringPlaymode(fileContents);

                        // If user clicked "Proceed Anyway", mark it so we don't show the dialog again if compilation gate defers play mode
                        if (!didPreventEnteringPlaymode)
                        {
                            GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.MarkDirtyWarningAcknowledged();
                        }
                    }

                    EditorApplication.isPlaying = !didPreventEnteringPlaymode;
                }

                if (!didPreventEnteringPlaymode)
                {
                    GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.
                        OnEditorPlayModeStateChanged_BlockEnteringPlaymodeIfUniqueSnapsChanged(state, out didPreventEnteringPlaymode);
                }

                /* if needed as security, double safe cleanup operation:
                if (didPreventEnteringPlaymode)
                {
                    // double check we always delete these files
                    GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.DeleteGeneratedFiles();
                }
                */
            }
            else
            {
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.
                    OnEditorPlayModeStateChanged_BlockEnteringPlaymodeIfUniqueSnapsChanged(state, out bool didPreventEnteringPlaymode);
            }

            static void AddDirtyReasonIfScenesInBuildDiffer(string dirtyReasonFilePath)
            {
                // First, clear any existing build settings dirty reasons so they can "heal" if fixed
                RemoveGONetDesignTimeDirtyReasonsByPrefix(BUILD_SETTINGS_DIRTY_REASON_PREFIX);

                if (TryGetGONetMostRecentSuccessfulBuild(out GONetMostRecentSuccessfulBuild record))
                {
                    List<string> currentScenePaths = EditorBuildSettings.scenes
                        .Where(scene => scene.enabled)
                        .Select(scene => scene.path)
                        .ToList();

                    bool areAnyDeviataions = !currentScenePaths.SequenceEqual(record.ScenePathsIncluded);
                    if (areAnyDeviataions)
                    {
                        string errorMessage = BUILD_SETTINGS_DIRTY_REASON_PREFIX + "The scene paths listed in the last successful build do not match the current list of scene paths in the build settings.";
                        AddGONetDesignTimeDirtyReason(errorMessage);
                    }

                    // Find the index of the first enabled scene in current build settings
                    int currentFirstSceneIndex = -1;
                    for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
                    {
                        if (EditorBuildSettings.scenes[i].enabled)
                        {
                            currentFirstSceneIndex = i;
                            break;
                        }
                    }

                    // Check if the first scene to be loaded has changed
                    if (currentFirstSceneIndex != record.FirstSceneIndex)
                    {
                        string previousFirstScene = record.FirstSceneIndex >= 0 && record.FirstSceneIndex < EditorBuildSettings.scenes.Length
                            ? EditorBuildSettings.scenes[record.FirstSceneIndex].path
                            : "none";
                        string currentFirstScene = currentFirstSceneIndex >= 0 && currentFirstSceneIndex < EditorBuildSettings.scenes.Length
                            ? EditorBuildSettings.scenes[currentFirstSceneIndex].path
                            : "none";

                        string errorMessage = BUILD_SETTINGS_DIRTY_REASON_PREFIX + $"The first scene to be loaded has changed since the last successful build. Previous: '{previousFirstScene}' (index {record.FirstSceneIndex}), Current: '{currentFirstScene}' (index {currentFirstSceneIndex}).";
                        AddGONetDesignTimeDirtyReason(errorMessage);
                    }

                    // Check if the user is trying to play from a scene that is not the first scene in the build
                    Scene activeScene = SceneManager.GetActiveScene();
                    if (!string.IsNullOrEmpty(activeScene.path) && currentFirstSceneIndex >= 0)
                    {
                        string firstSceneInBuild = EditorBuildSettings.scenes[currentFirstSceneIndex].path;

                        // Compare the active scene path with the first scene in build settings
                        if (!activeScene.path.Equals(firstSceneInBuild, StringComparison.OrdinalIgnoreCase))
                        {
                            string errorMessage = BUILD_SETTINGS_DIRTY_REASON_PREFIX + $"You are trying to enter play mode from scene '{activeScene.path}', but the first scene in build settings is '{firstSceneInBuild}'. GONet requires you to play from the first scene in the build.";
                            AddGONetDesignTimeDirtyReason(errorMessage);
                        }
                    }
                }
            }
        }

        private static void AddDirtyReasonIfSnapsDifferSinceLastBuild()
        {
            RemoveGONetDesignTimeDirtyReasonsByPrefix(SNAP_DIRTY_REASON_PREFIX);

            if (GONetProjectSettings.IsFastIterationModeEnabled)
            {
                return;
            }

            string lastBuildPath = GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_ALL_UNIQUE_SNAPS_FILE_PATH_LAST_BUILD;
            string currentSnapsPath = GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GENERATED_ALL_UNIQUE_SNAPS_FILE_PATH;

            if (!File.Exists(lastBuildPath) || !File.Exists(currentSnapsPath))
            {
                return;
            }

            if (!FileUtils.DoFilesHaveSameContents(currentSnapsPath, lastBuildPath))
            {
                string attributeName = nameof(GONetAutoMagicalSyncAttribute).Replace("Attribute", string.Empty);
                string dirtyReason = SNAP_DIRTY_REASON_PREFIX +
                                     "GONet detected one or more changes in the unique configurations of 'components with auto sync members' in the project (i.e., known internally as 'SNAPs').  " +
                                     $"One likely reason for this is the addition/removal of [{attributeName}] from fields/properties.";
                AddGONetDesignTimeDirtyReason(dirtyReason);
            }
        }

        private static string GetLimitedFilePreview(string filePath, int maxLines)
        {
            string[] lines = File.ReadAllLines(filePath);
            if (lines.Length <= maxLines)
            {
                return string.Join("\n", lines);
            }

            // Return only the first few lines with an indication of truncation
            return string.Join("\n", lines, 0, maxLines) + $"\n\n...and {lines.Length - maxLines} more lines.";
        }

        private static bool ShowGONetWarning_ShouldPreventEnteringPlaymode(string fileContents)
        {
            // Create the warning message
            string warningMessage = "WARNING: GONet will not function properly until you create another build, because the server and all clients are required to have the same information as it pertains to all the things that are going to be networked.\n\n" +
                                    "Please review the following reasons (i.e., things that changed during design-time since the last build):\n\n" +
                                    fileContents;

            // Show a dialog with the warning and file contents
            // NOTE: optOutKey must be a valid XML tag (no spaces, 1-127 chars, alphanumeric with underscores/hyphens)
            bool didProceedAnyway =
                EditorUtility.DisplayDialog(
                    "GONet Warning", warningMessage,
                    "Proceed Anyway (*NOT* Recommended)",
                    "Cancel, I'll Rebuild First (Please and Thank You!)",
                    DialogOptOutDecisionType.ForThisSession, "GONet_PlayMode_Build_Warning");

            return !didProceedAnyway;
        }

        private static void EditorSceneManager_sceneOpened(Scene scene, OpenSceneMode mode)
        {
            //GONetLog.Debug($" %& %^$B& ^$%#YMB$^Y ^$%BMKBYL ^%MUYK ^MV&UKY&^ MVUKY ^MBV UKY^MBUKY^ BVMUK^");
        }

        private static void OnSceneLoaded(Scene arg0, LoadSceneMode arg1)
        {
            //GONetLog.Debug($" %& %^$B& ^$%#YMB$^Y ^$%BMKBYL ^%MUYK ^MV&UKY&^ MVUKY ^MBV UKY^MBUKY^ BVMUK^");
        }

        private static void ResetInitialEditorLoadFlag()
        {
            // Skip first frame after editor loads to avoid initial noise
            if (Time.frameCount > 0)
            {
                IsInitialEditorLoad = false;
                EditorApplication.update -= ResetInitialEditorLoadFlag; // Unsubscribe after first frame
            }
        }

        private static void GONetParticipant_OnAwakeEditor(GONetParticipant gonetParticipant)
        {
            bool isHappeningDueToChangingPlayModeOrInitialOpenInEditor =
                (!GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange.HasValue ||
                 GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.EnteredEditMode ||
                 GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.ExitingPlayMode) &&
                (GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange_frameCount == Time.frameCount ||
                 Time.frameCount == 0);

            bool isTargetedDesignTimeOnlyAction =
                !EditorApplication.isPlaying &&
                !EditorApplication.isPlayingOrWillChangePlaymode &&
                !isHappeningDueToChangingPlayModeOrInitialOpenInEditor &&
                !EditorApplication.isUpdating && // handle scene loading or editor updates
                !Application.isBatchMode && // Avoid triggering in CI/CD build pipelines
                !IsQuitting;

            // IMPORTANT: Skip dirty detection if we're just opening a prefab for editing
            // OnAwake is called naturally when double-clicking a prefab, which shouldn't count as a change
            bool isInPrefabEditingMode = IsInPrefabEditingMode(gonetParticipant);
            if (isInPrefabEditingMode)
            {
                // Skipping dirty detection - in prefab editing mode
                return;
            }

            if (isTargetedDesignTimeOnlyAction &&
                (IsInSceneIncludedInBuild(gonetParticipant) || DesignTimeMetadata.TryGetFullPathInProject(gonetParticipant, out string fullPathInProject)))
            {
                // if in here, we know this is a new GNP being added into scene in editor edit mode (i.e., design time add)
                //string dirtyReason = $"GONetParticipant was awakened on GameObject: {DesignTimeMetadata.GetFullPath(gonetParticipant)} (Design-time only). {GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange}:{GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange_frameCount}:{Time.frameCount}";
                string dirtyReason = $"GONetParticipant was awakened on GameObject: {DesignTimeMetadata.GetFullPath(gonetParticipant)}";
                AddGONetDesignTimeDirtyReason(dirtyReason);
            }
            /* troubleshoot assistance when above not working:
            else
            {
                // Use StringBuilder to log all relevant values
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"Entered the else block for GONetParticipant Awake on GameObject: {gonetParticipant.gameObject.name}.");
                sb.AppendLine($"EditorApplication.isPlaying: {EditorApplication.isPlaying}");
                sb.AppendLine($"EditorApplication.isPlayingOrWillChangePlaymode: {EditorApplication.isPlayingOrWillChangePlaymode}");
                sb.AppendLine($"EditorApplication.isUpdating (scene loading): {EditorApplication.isUpdating}");
                sb.AppendLine($"isHappeningDueToChangingPlayModeOrInitialOpenInEditor: {isHappeningDueToChangingPlayModeOrInitialOpenInEditor}");
                sb.AppendLine($"LastPlayModeStateChange: {GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange}");
                sb.AppendLine($"LastPlayModeStateChange_frameCount: {GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange_frameCount}");
                sb.AppendLine($"Current Time.frameCount: {Time.frameCount}");

                // Log the accumulated information
                GONetLog.Debug(sb.ToString());
            }
            */
        }

        internal static void IndicateGONetDesignTimeNoLongerDirty()
        {
            string filePath = GetDesignTimeDirtyReasonsFilePath();

            if (File.Exists(filePath))
            {
                try
                {
                    long fileSize = new FileInfo(filePath).Length;
                    File.Delete(filePath);
                }
                catch (System.Exception ex)
                {
                    GONetLog.Error($"EXCEPTION during File.Delete(): {ex.GetType().Name}: {ex.Message}");
                    GONetLog.Error($"Stack trace: {ex.StackTrace}");
                }
            }

            // CRITICAL: Clear deferred scan flags after a successful build.
            // Without this, deferred detection might run when entering play mode
            // and re-add dirty reasons even though the build just completed successfully.
            // The flags default to true, so they must be explicitly cleared.
            ClearAllDeferredScanFlags();

#if ADDRESSABLES_AVAILABLE
            // Also clear addressables session tracking when builds succeed
            ClearAddressablesSessionTracking();
#endif

            // Save content snapshot if team-aware dirty checking is enabled
            if (IsTeamAwareDirtyCheckingEnabled())
            {
                GONetLog.Debug("Team-aware dirty checking is enabled, saving content snapshot...");
                SaveContentSnapshotAfterBuild();
            }
            else
            {
                GONetLog.Debug("Team-aware dirty checking is disabled, skipping content snapshot...");
            }
        }

        private static string GetDesignTimeDirtyReasonsFilePath()
        {
            string folderPath = GetDesignTimeDirtyReasonFolder();
            string filePath = Path.Combine(folderPath, "GONetDesignTimeDirtyReasons.log");
            return filePath;
        }

        internal static void RecordScenesInSuccessfulBuild()
        {
            HashSet<string> scenePathsIncluded = new();
            foreach (EditorBuildSettingsScene buildScene in EditorBuildSettings.scenes)
            {
                scenePathsIncluded.Add(buildScene.path);
            }

            // Find the index of the first enabled scene in build settings
            int firstSceneIndex = -1;
            for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
            {
                if (EditorBuildSettings.scenes[i].enabled)
                {
                    firstSceneIndex = i;
                    break;
                }
            }

            GONetMostRecentSuccessfulBuild record = new()
            {
                ScenePathsIncluded = scenePathsIncluded.ToArray(),
                FirstSceneIndex = firstSceneIndex,
                DateTimeBuildSucceeded = DateTime.UtcNow,
            };

            string folderPath = GetDesignTimeDirtyReasonFolder();
            string filePath = Path.Combine(folderPath, string.Concat(nameof(GONetMostRecentSuccessfulBuild), ".json"));
            File.WriteAllText(filePath, JsonUtility.ToJson(record));
        }

        internal static bool TryGetGONetMostRecentSuccessfulBuild(out GONetMostRecentSuccessfulBuild record)
        {
            record = null;
            string folderPath = GetDesignTimeDirtyReasonFolder();
            string filePath = Path.Combine(folderPath, string.Concat(nameof(GONetMostRecentSuccessfulBuild), ".json"));
            if (!File.Exists(filePath)) return false;

            try
            {
                record = JsonUtility.FromJson<GONetMostRecentSuccessfulBuild>(File.ReadAllText(filePath));
            }
            catch (Exception ex) { }
            return record != null;
        }

        [Serializable]
        public class GONetMostRecentSuccessfulBuild
        {
            public string[] ScenePathsIncluded;
            public int FirstSceneIndex; // Index of the first scene to be loaded

            [SerializeField] private string dateTimeBuildSucceeded;
            public DateTime DateTimeBuildSucceeded
            {
                get => string.IsNullOrEmpty(dateTimeBuildSucceeded) ? default : DateTime.Parse(dateTimeBuildSucceeded);
                set => dateTimeBuildSucceeded = value.ToLocalTime().ToString("o"); // "o" for round-trip (ISO 8601) format
            }
        }

        private static string GetDesignTimeDirtyReasonFolder()
        {
            return Path.Combine(Application.dataPath, "GONet", "Code", "GONet", "Editor", "Generation");
        }

        internal static void AddGONetDesignTimeDirtyReason(string dirtyReason)
        {
            // Central safeguard: Skip project:// prefabs - they are "dormant" and don't need dirty tracking.
            // Only scene://, resources://, and addressables:// paths should trigger dirty detection.
            if (dirtyReason.Contains(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX))
            {
                GONetLog.Debug($"[GONet] Skipping dirty reason for project:// prefab (dormant, not spawnable): {dirtyReason}");
                return;
            }

            string folderPath = GetDesignTimeDirtyReasonFolder();
            string filePath = GetDesignTimeDirtyReasonsFilePath();

            // Create the directory if it doesn't exist
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            try
            {
                // Get the current date and time in a readable format
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                // Create the log entry with the reason and timestamp
                string logEntry = $"{timestamp}: {dirtyReason}\n";

                // Append to the file, creating it if it doesn't exist
                File.AppendAllText(filePath, logEntry);

                // Force immediate disk flush to prevent race conditions with play mode transition
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Flush(true); // Force OS to flush to disk
                }

                // Additional safety: Brief pause to ensure file system operations complete
                // before any potential play mode transition that might check for file existence
                System.Threading.Thread.Sleep(10);

                // Optionally, log confirmation to Unity console
                //GONetLog.Debug($"Logged design-time dirty reason: {dirtyReason}"); // COMMENTED - spammy log (log cleanup)
            }
            catch (Exception ex)
            {
                // Handle any file writing errors
                GONetLog.Debug($"Error writing to log file: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes all dirty reasons from the file that start with the specified prefix.
        /// This allows specific categories of dirty reasons to "heal" when the issue is resolved.
        /// </summary>
        internal static void RemoveGONetDesignTimeDirtyReasonsByPrefix(string prefix)
        {
            string filePath = GetDesignTimeDirtyReasonsFilePath();

            if (!File.Exists(filePath))
            {
                return; // Nothing to remove
            }

            try
            {
                // Read all lines from the file
                string[] allLines = File.ReadAllLines(filePath);

                // Filter out lines that contain the prefix (checking after the timestamp)
                List<string> filteredLines = new List<string>();
                foreach (string line in allLines)
                {
                    // Each line format is: "timestamp: [PREFIX] reason"
                    // We need to check if the reason part (after ": ") starts with the prefix
                    int colonIndex = line.IndexOf(": ");
                    if (colonIndex >= 0 && colonIndex + 2 < line.Length)
                    {
                        string reasonPart = line.Substring(colonIndex + 2);
                        if (!reasonPart.StartsWith(prefix))
                        {
                            filteredLines.Add(line);
                        }
                    }
                    else
                    {
                        // Malformed line, keep it just in case
                        filteredLines.Add(line);
                    }
                }

                // Write the filtered lines back to the file (or delete if empty)
                if (filteredLines.Count > 0)
                {
                    File.WriteAllLines(filePath, filteredLines);
                }
                else
                {
                    // If no lines remain, delete the file
                    File.Delete(filePath);
                }

                //GONetLog.Debug($"Removed dirty reasons with prefix: {prefix}"); // COMMENTED - spammy log (log cleanup)
            }
            catch (Exception ex)
            {
                GONetLog.Debug($"Error removing dirty reasons by prefix: {ex.Message}");
            }
        }

        private static void GONetParticipant_OnDestroyCalled(GONetParticipant gonetParticipant)
        {
            bool isHappeningDueToChangingPlayModeOrInitialOpenInEditor =
                (!GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange.HasValue ||
                 GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.EnteredEditMode ||
                 GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.ExitingPlayMode) &&
                (GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange_frameCount == Time.frameCount ||
                 Time.frameCount == 0);

            bool isTargetedDesignTimeOnlyAction =
                !EditorApplication.isPlaying &&
                !EditorApplication.isPlayingOrWillChangePlaymode &&
                !isHappeningDueToChangingPlayModeOrInitialOpenInEditor &&
                !EditorApplication.isUpdating && // handle scene loading or editor updates
                !Application.isBatchMode && // Avoid triggering in CI/CD build pipelines
                !IsQuitting;

            if (isTargetedDesignTimeOnlyAction)
            {
                bool isInScene = IsInSceneIncludedInBuild(gonetParticipant);
                bool hasProjectPath = DesignTimeMetadata.TryGetFullPathInProject(gonetParticipant, out string fullPathInProject);

                // Check if we're in prefab editing mode - OnDestroy gets called when exiting prefab stage
                bool isInPrefabMode = IsInPrefabEditingMode(gonetParticipant);
                if (isInPrefabMode)
                {
                    // Skip - this is happening inside prefab editing mode
                    return;
                }

                // Check if this is happening shortly after prefab stage closed
                double timeSincePrefabStageClosed = EditorApplication.timeSinceStartup - lastPrefabStageClosedTime;
                if (lastPrefabStageClosedTime > 0 && timeSincePrefabStageClosed < PREFAB_STAGE_TRANSITION_GRACE_PERIOD)
                {
                    // We're within the grace period after prefab stage closed - skip
                    GONetLog.Debug($"Skipping OnDestroy for {gonetParticipant.gameObject.name} - within grace period after prefab stage close ({timeSincePrefabStageClosed:F2}s)");
                    return;
                }

                if (isInScene || hasProjectPath)
                {
                    string dirtyReason = $"GONetParticipant was removed from GameObject: {DesignTimeMetadata.GetFullPath(gonetParticipant)} (Design-time only).";
                    AddGONetDesignTimeDirtyReason(dirtyReason);
                }
            }
        }

        private static void GONetParticipant_OnEnableEditor(GONetParticipant gonetParticipant)
        {
            string goName = gonetParticipant?.gameObject?.name ?? "null";

            bool isHappeningDueToChangingPlayModeOrInitialOpenInEditor =
                (!GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange.HasValue ||
                 GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.EnteredEditMode ||
                 GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.ExitingPlayMode) &&
                (GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange_frameCount == Time.frameCount ||
                 Time.frameCount == 0);

            // Checked play mode transition state

            bool isTargetedDesignTimeOnlyAction =
                !EditorApplication.isPlaying &&
                !EditorApplication.isPlayingOrWillChangePlaymode &&
                !isHappeningDueToChangingPlayModeOrInitialOpenInEditor &&
                !EditorApplication.isUpdating && // handle scene loading or editor updates
                !Application.isBatchMode && // Avoid triggering in CI/CD build pipelines
                !IsQuitting;

            // Verified design-time only action conditions

            // Skip if not a targeted design-time action

            bool isInScene = IsInSceneIncludedInBuild(gonetParticipant);
            bool hasProjectPath = DesignTimeMetadata.TryGetFullPathInProject(gonetParticipant, out string fullPathInProject);
            bool isInPrefabMode = IsInPrefabEditingMode(gonetParticipant);

            // Checked scene and project path status

            // IMPORTANT: Skip dirty detection if we're just opening a prefab for editing
            // OnEnable is called naturally when double-clicking a prefab, which shouldn't count as a change
            if (isInPrefabMode)
            {
                // Skipping dirty detection - in prefab editing mode
                return;
            }

            // IMPORTANT: Skip if this is happening shortly after prefab stage closed
            // When exiting prefab stage, Unity calls OnDisable/OnEnable on the prefab asset
            // This is NOT a user toggling the component, just Unity's internal behavior
            double timeSincePrefabStageClosed = EditorApplication.timeSinceStartup - lastPrefabStageClosedTime;
            if (lastPrefabStageClosedTime > 0 && timeSincePrefabStageClosed < PREFAB_STAGE_TRANSITION_GRACE_PERIOD)
            {
                // We're within the grace period after prefab stage closed - skip logging
                GONetLog.Debug($"Skipping event for {gonetParticipant.gameObject.name} - within grace period after prefab stage close ({timeSincePrefabStageClosed:F2}s)");
                return;
            }

            if (isTargetedDesignTimeOnlyAction &&
                (isInScene || hasProjectPath))
            {
                // NEW: Use prefab save detector to filter out Unity's internal save behavior
                if (hasProjectPath && GONetPrefabSaveDetector.ShouldSkipPrefabEvent(fullPathInProject, "OnEnable"))
                {
                    // This is part of Unity's internal prefab save behavior - skip it
                    return;
                }

                string dirtyReason = $"GONetParticipant was enabled on GameObject: {DesignTimeMetadata.GetFullPath(gonetParticipant)} (Design-time only).";
                // Adding dirty reason for enabled GONetParticipant
                AddGONetDesignTimeDirtyReason(dirtyReason);
            }
            else
            {
                // Not adding dirty reason - conditions not met
            }
        }

        private static void GONetParticipant_OnDisableEditor(GONetParticipant gonetParticipant)
        {
            string goName = gonetParticipant?.gameObject?.name ?? "null";
            // OnDisableEditor processing

            bool isHappeningDueToChangingPlayModeOrInitialOpenInEditor =
                (!GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange.HasValue ||
                 GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.EnteredEditMode ||
                 GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.ExitingPlayMode) &&
                (GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange_frameCount == Time.frameCount ||
                 Time.frameCount == 0);

            // Checked play mode transition state

            bool isTargetedDesignTimeOnlyAction =
                !EditorApplication.isPlaying &&
                !EditorApplication.isPlayingOrWillChangePlaymode &&
                !isHappeningDueToChangingPlayModeOrInitialOpenInEditor &&
                !EditorApplication.isUpdating && // handle scene loading or editor updates
                !Application.isBatchMode && // Avoid triggering in CI/CD build pipelines
                !IsQuitting;

            // Verified design-time only action conditions

            bool isInScene = IsInSceneIncludedInBuild(gonetParticipant);
            bool hasProjectPath = DesignTimeMetadata.TryGetFullPathInProject(gonetParticipant, out string fullPathInProject);
            bool isInPrefabMode = IsInPrefabEditingMode(gonetParticipant);

            // OnDisable is called naturally when entering/exiting prefab mode, which shouldn't count as a change
            if (isInPrefabMode)
            {
                // Skipping dirty detection - in prefab editing mode
                return;
            }

            // IMPORTANT: Skip if this is happening shortly after prefab stage closed
            // When exiting prefab stage, Unity calls OnDisable/OnEnable on the prefab asset
            // This is NOT a user toggling the component, just Unity's internal behavior
            double timeSincePrefabStageClosed = EditorApplication.timeSinceStartup - lastPrefabStageClosedTime;
            if (lastPrefabStageClosedTime > 0 && timeSincePrefabStageClosed < PREFAB_STAGE_TRANSITION_GRACE_PERIOD)
            {
                // We're within the grace period after prefab stage closed - skip logging
                GONetLog.Debug($"Skipping event for {gonetParticipant.gameObject.name} - within grace period after prefab stage close ({timeSincePrefabStageClosed:F2}s)");
                return;
            }

            if (isTargetedDesignTimeOnlyAction &&
                (isInScene || hasProjectPath))
            {
                // NEW: Use prefab save detector to filter out Unity's internal save behavior
                if (hasProjectPath && GONetPrefabSaveDetector.ShouldSkipPrefabEvent(fullPathInProject, "OnDisable"))
                {
                    // This is part of Unity's internal prefab save behavior - skip it
                    return;
                }

                string dirtyReason = $"GONetParticipant was disabled on GameObject: {DesignTimeMetadata.GetFullPath(gonetParticipant)} (Design-time only).";
                // Adding dirty reason for disabled GONetParticipant
                AddGONetDesignTimeDirtyReason(dirtyReason);
            }
            else
            {
                // Not adding dirty reason - conditions not met
            }
        }

        private static void GONetParticipant_OnValidateEditor(GONetParticipant gonetParticipant)
        {
            string goName = gonetParticipant?.gameObject?.name ?? "null";
            // OnValidateEditor processing

            bool isHappeningDueToChangingPlayModeOrInitialOpenInEditor =
                (!GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange.HasValue ||
                 GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.EnteredEditMode ||
                 GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.ExitingPlayMode) &&
                (GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange_frameCount == Time.frameCount ||
                 Time.frameCount == 0);

            // Checked play mode transition state

            bool isTargetedDesignTimeOnlyAction =
                !EditorApplication.isPlaying &&
                !EditorApplication.isPlayingOrWillChangePlaymode &&
                !isHappeningDueToChangingPlayModeOrInitialOpenInEditor &&
                !EditorApplication.isUpdating && // handle scene loading or editor updates
                !Application.isBatchMode && // Avoid triggering in CI/CD build pipelines
                !IsQuitting;

            // Verified design-time only action conditions

            bool isInScene = IsInSceneIncludedInBuild(gonetParticipant);
            bool hasProjectPath = DesignTimeMetadata.TryGetFullPathInProject(gonetParticipant, out string fullPathInProject);
            bool isInPrefabMode = IsInPrefabEditingMode(gonetParticipant);

            // Checked scene and project path status

            // IMPORTANT: For OnValidate, we should NOT skip if it's a genuine user interaction
            // OnValidate can be called both during prefab loading AND when user changes properties
            // We should only record dirty reasons for actual user property changes, not automatic loading
            // Note: OnAwake/OnEnable should always be skipped in prefab mode, but OnValidate needs this check

            if (isTargetedDesignTimeOnlyAction &&
                (isInScene || hasProjectPath))
            {
                // Check if this is a prefab in an "active" context (resources://, addressables://)
                // Note: project:// prefabs are filtered by the central check in AddGONetDesignTimeDirtyReason
                string fullPath = DesignTimeMetadata.GetFullPath(gonetParticipant);

                bool isPrefab = fullPath.StartsWith(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX) ||
                               fullPath.StartsWith(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX) ||
                               fullPath.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX);

                string dirtyReason = $"GONetParticipant properties changed on GameObject: {fullPath} (Design-time only).";

                if (isPrefab)
                {
                    // For prefabs, we need to be selective about OnValidate
                    // We want to allow it when user is actually editing the prefab
                    // But block it when Unity is just revalidating all prefabs

                    // Check if we're in prefab editing mode AND this is the prefab being edited
                    if (isInPrefabMode)
                    {
                        var currentPrefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
                        if (currentPrefabStage != null)
                        {
                            string editingPath = currentPrefabStage.assetPath;
                            // Check if this is the prefab currently being edited
                            if (fullPath.Contains(editingPath) || editingPath.Contains(gonetParticipant.gameObject.name))
                            {
                                // User is actively editing THIS prefab - allow OnValidate
                                GONetLog.Debug($"[GONetSpawnSupport_DesignTime] Allowing OnValidate for actively edited prefab {fullPath}");
                                AddGONetDesignTimeDirtyReason(dirtyReason);
                                return;
                            }
                        }
                    }

                    // Check if this prefab was selected in the last few seconds (single-click editing)
                    if (UnityEditor.Selection.activeGameObject == gonetParticipant.gameObject)
                    {
                        // User has this prefab selected - likely editing in Inspector
                        // NOTE: Debug log removed during performance optimization (log cleanup)
                        AddGONetDesignTimeDirtyReason(dirtyReason);
                        return;
                    }

                    // This is a prefab NOT being edited - skip OnValidate
                    // NOTE: Debug log removed - was called for every prefab during generation (log cleanup)
                    return;
                }

                // For scene objects, OnValidate is reliable, so we'll allow it through
                // Adding dirty reason for property change
                AddGONetDesignTimeDirtyReason(dirtyReason);
            }
            else
            {
                // Not adding dirty reason - conditions not met
            }
        }

        /// <summary>
        /// Detects if a GONetParticipant is being edited in any prefab editing context.
        /// Handles both double-click prefab editing (prefab stage mode) and single-click prefab editing (inspector mode).
        /// Uses Unity 2022.3+ PrefabStageUtility APIs for robust detection across all Unity versions.
        /// </summary>
        private static bool IsInPrefabEditingMode(GONetParticipant gonetParticipant)
        {
            if (gonetParticipant?.gameObject == null)
            {
                // GameObject is null
                return false;
            }

            string goName = gonetParticipant.gameObject.name;

            // Method 1: Check if we're currently in a PrefabStage (double-click prefab editing)
            var currentPrefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            // Check for active prefab stage

            if (currentPrefabStage != null && currentPrefabStage.stageHandle.IsValid())
            {
                // Valid prefab stage found, check if GameObject is part of contents
                // We're in prefab stage mode - check if this GameObject is part of the prefab being edited
                try
                {
                    if (currentPrefabStage.IsPartOfPrefabContents(gonetParticipant.gameObject))
                    {
                        // GameObject is part of prefab stage contents
                        return true; // Confirmed: editing in prefab stage mode
                    }
                    else
                    {
                        // GameObject is NOT part of prefab stage contents
                    }
                }
                catch (System.InvalidOperationException ex)
                {
                    // Unity doesn't allow accessing prefabContentsRoot during Awake/OnEnable
                    // In this case, we're likely in prefab stage mode but can't confirm yet
                    // Cannot check prefab contents during Awake/OnEnable - assume prefab mode
                    return true; // Conservative approach: assume we're in prefab editing mode
                }
            }

            // Method 2: Check if this is a prefab asset being edited directly (single-click inspector editing)
            bool isPartOfAnyPrefab = UnityEditor.PrefabUtility.IsPartOfAnyPrefab(gonetParticipant.gameObject);
            // Check if part of any prefab

            if (isPartOfAnyPrefab)
            {
                // Get the asset path - if this GameObject has a direct asset path, it's likely a prefab asset
                string assetPath = UnityEditor.AssetDatabase.GetAssetPath(gonetParticipant.gameObject);
                // Get asset path

                // Key insight: During single-click editing, AssetPath can be empty!
                // We rely on AssetType and Scene info instead
                var assetType = UnityEditor.PrefabUtility.GetPrefabAssetType(gonetParticipant.gameObject);
                // Check asset type

                if (assetType == UnityEditor.PrefabAssetType.Regular || assetType == UnityEditor.PrefabAssetType.Variant)
                {
                    // Check scene info
                    var scene = gonetParticipant.gameObject.scene;
                    // Check scene info

                    // Key check: prefab assets have empty scene path (whether AssetPath is empty or not)
                    if (string.IsNullOrEmpty(gonetParticipant.gameObject.scene.path))
                    {
                        // Prefab asset being edited directly in inspector
                        return true; // Confirmed: editing prefab asset directly in inspector
                    }
                    else
                    {
                        // Has scene path, not direct asset editing
                    }
                }
                else
                {
                    // Asset type is not Regular/Variant prefab
                }
            }

            // Not in prefab editing mode
            return false; // Not in any prefab editing mode
        }

        private static void OnProjectChanged_TakeNoteOfAnyGONetChanges_ProjectOnly()
        {
            if (GONetProjectSettings.IsDeferredChangeDetectionEnabled())
            {
                SetProjectScanNeeded(true);
                return;
            }

            OnProjectChanged_TakeNoteOfAnyGONetChanges_ProjectOnly_Original();
        }

        /// <summary>
        /// ORIGINAL IMPLEMENTATION - Preserved exactly for fallback.
        /// </summary>
        private static void OnProjectChanged_TakeNoteOfAnyGONetChanges_ProjectOnly_Original()
        {
            HashSet<GONetParticipant> projectGnps = new();

            // IMPORTANT: have to load them all up for else the following call will not "find" them all and only the ones that happened to be loaded already would be found/processed
            Resources.LoadAll<GONetParticipant>(string.Empty);
            foreach (var gonetParticipant in Resources.FindObjectsOfTypeAll<GONetParticipant>())
            {
                AddIfAppropriate(projectGnps, gonetParticipant);
            }

            // IMPORTANT: have to do this because the above call to Resources.FindObjectsOfTypeAll<GONetParticipant>() does NOT identify a prefab that just had GNP added to it this frame!!!
            foreach (GONetParticipant gonetParticipant in GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GetGNPsAddedToPrefabThisFrame())
            {
                AddIfAppropriate(projectGnps, gonetParticipant);
            }

#if ADDRESSABLES_AVAILABLE
            // TODO: Re-enable once we figure out the right time to scan Addressables scenes
            // The issue is that loading scenes during projectChanged can block/hang during builds
            // Also scan all Addressables scenes for GONetParticipants
            //LoadAndScanAddressablesScenes(projectGnps);
#endif

            // Collect the design time locations for each GONetParticipant (includes addressables paths)
            HashSet<string> allPathsToGnpsInProject = new();
            foreach (var gonetParticipant in projectGnps)
            {
                string designTimeLocation = GetDesignTimeLocationForProjectScan(gonetParticipant);
                if (!string.IsNullOrEmpty(designTimeLocation))
                {
                    allPathsToGnpsInProject.Add(designTimeLocation);
                }
            }

            GONetLog.Debug($"Here are all {allPathsToGnpsInProject.Count} GNPs in project:\n{string.Join("\n", allPathsToGnpsInProject)}");
            ProcessAnyDesignTimeDirty_IfAppropriate(allPathsToGnpsInProject);

            static void AddIfAppropriate(HashSet<GONetParticipant> projectGnps, GONetParticipant gonetParticipant)
            {
                // Check if the GONetParticipant is part of a scene
                Scene scene = gonetParticipant.gameObject.scene;
                bool isPresumedInProject = scene == null || string.IsNullOrEmpty(scene.path);

                // Check if the scene is included in the build settings
                bool isInSceneInBuild = !isPresumedInProject && IsSceneIncludedInBuild(scene.path);
                if (isPresumedInProject || isInSceneInBuild)
                {
                    projectGnps.Add(gonetParticipant);
                }
                else
                {
                    GONetLog.Debug($"GONetParticipant found in excluded scene (i.e., not in build, so GONet does not care now): {DesignTimeMetadata.GetFullPath(gonetParticipant)}");
                }

                //GONetLog.Debug($"SLEEPS RESOURCES gnp:{DesignTimeMetadata.GetFullPath(gonetParticipant)}");
            }
        }

        private static string GetDesignTimeLocationForProjectScan(GONetParticipant gonetParticipant)
        {
            if (gonetParticipant == null || gonetParticipant.gameObject == null)
            {
                return string.Empty;
            }

            string designTimeLocation = gonetParticipant.DesignTimeLocation;
            if (!string.IsNullOrWhiteSpace(designTimeLocation))
            {
                return designTimeLocation;
            }

            string assetPath = AssetDatabase.GetAssetPath(gonetParticipant);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
                if (prefabStage != null && prefabStage.scene == gonetParticipant.gameObject.scene)
                {
                    assetPath = prefabStage.assetPath;
                }
                else
                {
                    GameObject prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(gonetParticipant.gameObject);
                    if (prefabAsset != null)
                    {
                        assetPath = AssetDatabase.GetAssetPath(prefabAsset);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                if (assetPath.Contains("/Resources/"))
                {
                    return string.Concat(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX, assetPath);
                }

#if ADDRESSABLES_AVAILABLE
                if (IsAddressableAsset(assetPath))
                {
                    return string.Concat(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX, assetPath);
                }
#endif

                // INTENTIONALLY return empty for pure project:// prefabs - they are "dormant" and don't need dirty tracking.
                // These prefabs aren't spawnable at runtime (not in Resources, not Addressable, not in a scene).
                // When/if they get placed in a scene, the scene:// path will be tracked instead.
                // When/if they get moved to Resources or added to Addressables, the appropriate prefix will be used.
                // This reduces false positive dirty warnings and unnecessary processing.
                return string.Empty;
            }

            return DesignTimeMetadata.GetFullUniquePathInScene(gonetParticipant);
        }

        private static bool IsInSceneIncludedInBuild(GONetParticipant gonetParticipant)
        {
            // Check if the GONetParticipant is part of a scene
            Scene scene = gonetParticipant.gameObject.scene;
            bool isPresumedInProject = scene == null || string.IsNullOrEmpty(scene.path);
            if (isPresumedInProject) return false;

            return IsSceneIncludedInBuild(scene.path);
        }

        private static bool IsSceneIncludedInBuild(string scenePath)
        {
            // Check Build Settings first
            foreach (var buildScene in EditorBuildSettings.scenes)
            {
                if (buildScene.path == scenePath && buildScene.enabled)
                {
                    return true; // Scene is included in the build settings
                }
            }

#if ADDRESSABLES_AVAILABLE
            // Also check if scene is in Addressables
            if (IsSceneInAddressables(scenePath))
            {
                return true;
            }
#endif

            return false;
        }

#if ADDRESSABLES_AVAILABLE
        private static bool IsSceneInAddressables(string scenePath)
        {
            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    return false;
                }

                // Get the asset GUID from the scene path
                string guid = AssetDatabase.AssetPathToGUID(scenePath);
                if (string.IsNullOrEmpty(guid))
                {
                    return false;
                }

                // Check if this GUID exists in any Addressables group
                var entry = settings.FindAssetEntry(guid);
                return entry != null;
            }
            catch
            {
                return false;
            }
        }

        private static void LoadAndScanAddressablesScenes(HashSet<GONetParticipant> projectGnps)
        {
            try
            {
                // IMPORTANT: Skip scene loading during build or play mode - it can hang/block
                if (BuildPipeline.isBuildingPlayer || EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    GONetLog.Debug("[GONetSpawnSupport] Skipping Addressables scene scan during build/play mode");
                    return;
                }

                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    GONetLog.Debug("[GONetSpawnSupport] No Addressables settings found - skipping Addressables scene scan");
                    return;
                }

                // Collect all scene asset entries from Addressables
                List<string> addressableScenesPath = new List<string>();
                foreach (var group in settings.groups)
                {
                    if (group == null) continue;

                    foreach (var entry in group.entries)
                    {
                        if (entry == null) continue;

                        // Check if this is a scene asset
                        string assetPath = AssetDatabase.GUIDToAssetPath(entry.guid);
                        if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".unity"))
                        {
                            addressableScenesPath.Add(assetPath);
                        }
                    }
                }

                if (addressableScenesPath.Count == 0)
                {
                    GONetLog.Debug("[GONetSpawnSupport] No scenes found in Addressables");
                    return;
                }

                GONetLog.Debug($"[GONetSpawnSupport] Found {addressableScenesPath.Count} scene(s) in Addressables: {string.Join(", ", addressableScenesPath)}");

                // Remember currently open scenes
                List<Scene> originalScenes = new List<Scene>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    originalScenes.Add(SceneManager.GetSceneAt(i));
                }

                // Load each Addressables scene additively, scan it, then unload it
                foreach (string scenePath in addressableScenesPath)
                {
                    try
                    {
                        GONetLog.Debug($"[GONetSpawnSupport] Loading Addressables scene for scanning: {scenePath}");
                        Scene loadedScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);

                        // Scan for GONetParticipants in this scene
                        foreach (GameObject rootGameObject in loadedScene.GetRootGameObjects())
                        {
                            foreach (GONetParticipant gnp in rootGameObject.GetComponentsInChildren<GONetParticipant>(true))
                            {
                                projectGnps.Add(gnp);
                                GONetLog.Debug($"[GONetSpawnSupport] Found GNP in Addressables scene '{loadedScene.name}': {gnp.gameObject.name}");
                            }
                        }

                        // Unload the scene after scanning
                        EditorSceneManager.CloseScene(loadedScene, true);
                    }
                    catch (System.Exception ex)
                    {
                        GONetLog.Warning($"[GONetSpawnSupport] Failed to load/scan Addressables scene '{scenePath}': {ex.Message}");
                    }
                }

                GONetLog.Debug($"[GONetSpawnSupport] Finished scanning Addressables scenes");
            }
            catch (System.Exception ex)
            {
                GONetLog.Error($"[GONetSpawnSupport] Error while scanning Addressables scenes: {ex.Message}");
            }
        }
#endif

        /// <summary>
        /// Scene-specific version that only compares against objects from the same scenes being scanned.
        /// This prevents false positives when scanning only currently loaded scenes.
        /// </summary>
        private static void ProcessAnyDesignTimeDirty_IfAppropriate_SceneSpecific(HashSet<string> fullPathsToDesignTimeGnps, HashSet<string> sceneNames)
        {
            // Skip change detection only if we're actively building
            // During builds, metadata caching may not be complete, leading to false positives
            if (BuildPipeline.isBuildingPlayer)
            {
                GONetLog.Debug($"Skipping design-time dirty detection during build: isBuildingPlayer={BuildPipeline.isBuildingPlayer}");
                return;
            }

            // Skip if we have zero current paths but metadata caching isn't complete yet
            // This prevents false "everything was removed" during editor startup/domain reload
            if (fullPathsToDesignTimeGnps.Count == 0 && !GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached)
            {
                GONetLog.Debug($"Skipping design-time dirty detection: Found 0 current paths but metadata caching isn't complete yet");
                return;
            }

            // compare this list and the current metadata associated with all these gnps to that stored in the last build's metadata..if different TAKE NOTE and refer to this when entering play mode and show warning!
            IEnumerable<DesignTimeMetadata> designTimeMetadatasFromLastBuild =
                GONetSpawnSupport_Runtime.LoadDesignTimeMetadataFromPersistence();

            // Filter to only include scene paths from the specific scenes we're scanning
            HashSet<string> pathsFromLastBuildInTheseScenes = new HashSet<string>();
            foreach (var metadata in designTimeMetadatasFromLastBuild)
            {
                if (metadata.Location.StartsWith(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX))
                {
                    // Extract scene name from path like "scene://SceneName/GameObject/Path"
                    string locationAfterPrefix = metadata.Location.Substring(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX.Length);
                    int firstSlashIndex = locationAfterPrefix.IndexOf('/');
                    if (firstSlashIndex > 0)
                    {
                        string sceneNameFromPath = locationAfterPrefix.Substring(0, firstSlashIndex);
                        if (sceneNames.Contains(sceneNameFromPath))
                        {
                            pathsFromLastBuildInTheseScenes.Add(metadata.Location);
                        }
                    }
                }
            }

            GONetLog.Debug($"Filtered last build paths to {pathsFromLastBuildInTheseScenes.Count} paths from scenes: {string.Join(", ", sceneNames)}");

            // Compare the current GNP paths to the previous build's metadata (only from the same scenes)
            foreach (var currentPath in fullPathsToDesignTimeGnps)
            {
                if (!pathsFromLastBuildInTheseScenes.Contains(currentPath))
                {
                    AddGONetDesignTimeDirtyReason($"GONetParticipant at {currentPath} was added or modified after the last build.");
                }
            }

            // Check for prefabs that were removed from the current scanning (only within the same scenes)
            foreach (var lastBuildPath in pathsFromLastBuildInTheseScenes)
            {
                if (!fullPathsToDesignTimeGnps.Contains(lastBuildPath))
                {
                    GONetLog.Debug($"Confirmed removal for scene path {lastBuildPath}");
                    AddGONetDesignTimeDirtyReason($"GONetParticipant prefab removed from scene: {lastBuildPath}");
                }
            }
        }

        private static void ProcessAnyDesignTimeDirty_IfAppropriate(HashSet<string> fullPathsToDesignTimeGnps)
        {
            // Skip change detection only if we're actively building
            // During builds, metadata caching may not be complete, leading to false positives
            if (BuildPipeline.isBuildingPlayer)
            {
                GONetLog.Debug($"Skipping design-time dirty detection during build: isBuildingPlayer={BuildPipeline.isBuildingPlayer}");
                return;
            }

            // Skip if we have zero current paths but metadata caching isn't complete yet
            // This prevents false "everything was removed" during editor startup/domain reload
            // Note: During normal addressables operations, we now wait for caching to complete before calling this method
            if (fullPathsToDesignTimeGnps.Count == 0 && !GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached)
            {
                GONetLog.Debug($"Skipping design-time dirty detection: Found 0 current paths but metadata caching isn't complete yet");
                return;
            }

            // compare this list and the current metadata associated with all these gnps to that stored in the last build's metadata..if different TAKE NOTE and refer to this when entering play mode and show warning!
            IEnumerable<DesignTimeMetadata> designTimeMetadatasFromLastBuild =
                GONetSpawnSupport_Runtime.LoadDesignTimeMetadataFromPersistence();
            // HashSet for fast lookup of the GNP paths from the last build's metadata
            HashSet<string> pathsFromLastBuild = new HashSet<string>(
                designTimeMetadatasFromLastBuild.Select(metadata => metadata.Location));

            // Compare the current GNP paths to the previous build's metadata
            foreach (var currentPath in fullPathsToDesignTimeGnps)
            {
                bool foundMatch = pathsFromLastBuild.Contains(currentPath);

                // Check for backwards compatibility: resources:// vs project:// prefixes should be treated as equivalent
                if (!foundMatch && currentPath.StartsWith(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX))
                {
                    // Try to find equivalent project:// path from last build
                    string equivalentProjectPath = currentPath.Replace(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX, GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX);
                    foundMatch = pathsFromLastBuild.Contains(equivalentProjectPath);
                }
                else if (!foundMatch && currentPath.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX))
                {
                    // Try to find equivalent resources:// path from last build (edge case)
                    string equivalentResourcesPath = currentPath.Replace(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX, GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX);
                    foundMatch = pathsFromLastBuild.Contains(equivalentResourcesPath);
                }

                if (!foundMatch)
                {
                    AddGONetDesignTimeDirtyReason($"GONetParticipant at {currentPath} was added or modified after the last build.");
                }
            }

            // Determine what type of scan this is based on the current paths
            bool isSceneScan = fullPathsToDesignTimeGnps.Any(p => p.StartsWith(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX));
            bool isProjectScan = fullPathsToDesignTimeGnps.Any(p =>
                p.StartsWith(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX) ||
                p.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX) ||
                p.StartsWith(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX));

            // Check for prefabs that were removed from the current scanning (only within the same category)
            foreach (var lastBuildPath in pathsFromLastBuild)
            {
                // Skip checking removals for categories not being scanned in this pass
                bool isLastBuildScene = lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX);
                bool isLastBuildProject = lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX) ||
                                         lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX) ||
                                         lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX);

                // Only check for removals within the same scan type
                if ((isSceneScan && !isLastBuildScene) || (isProjectScan && !isLastBuildProject))
                {
                    continue; // Skip this path - wrong category for this scan
                }

                bool foundInCurrent = fullPathsToDesignTimeGnps.Contains(lastBuildPath);

                // Check for backwards compatibility when checking removals too
                if (!foundInCurrent && lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX))
                {
                    // Try to find equivalent project:// path in current scan
                    string equivalentProjectPath = lastBuildPath.Replace(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX, GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX);
                    foundInCurrent = fullPathsToDesignTimeGnps.Contains(equivalentProjectPath);
                }
                else if (!foundInCurrent && lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX))
                {
                    // Try to find equivalent resources:// path in current scan
                    string equivalentResourcesPath = lastBuildPath.Replace(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX, GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX);
                    foundInCurrent = fullPathsToDesignTimeGnps.Contains(equivalentResourcesPath);
                }

                if (!foundInCurrent)
                {
                    // Additional safeguard: For non-scene prefabs, check if this is a false positive by verifying
                    // the asset actually no longer exists in the project/addressables
                    if (!lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX))
                    {
                        // Extract the asset path from the prefixed path
                        string assetPath = "";
                        if (lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX))
                        {
                            assetPath = lastBuildPath.Substring(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX.Length);
                        }
                        else if (lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX))
                        {
                            assetPath = lastBuildPath.Substring(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX.Length);
                        }
                        else if (lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX))
                        {
                            assetPath = lastBuildPath.Substring(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX.Length);
                        }

                        // If we extracted an asset path, verify the asset doesn't actually still exist
                        if (!string.IsNullOrEmpty(assetPath))
                        {
                            // Check if the asset still exists and has a GONetParticipant
                            GONetParticipant stillExists = AssetDatabase.LoadAssetAtPath<GONetParticipant>(assetPath);
                            if (stillExists != null)
                            {
                                GONetLog.Debug($"Skipping false positive removal for prefixed path {lastBuildPath}: asset still exists at {assetPath}");
                                continue; // Skip this false positive
                            }
                        }
                    }

                    // Determine the type of removal based on the prefix
                    string removalType = "project resources";
                    if (lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX))
                    {
                        removalType = "addressables";
                    }
                    else if (lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX) ||
                             lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX))
                    {
                        removalType = "project resources";
                    }
                    else if (lastBuildPath.StartsWith(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX))
                    {
                        removalType = "scene";
                    }

                    GONetLog.Debug($"Confirmed removal for prefixed path {lastBuildPath}: type={removalType}");
                    AddGONetDesignTimeDirtyReason($"GONetParticipant prefab removed from {removalType}: {lastBuildPath}");
                }
            }
        }

        private static void OnHierarchyChanged_TakeNoteOfAnyGONetChanges_SceneOnly()
        {
            // Skip if compiling, updating, in play mode, or during asset import/refresh
            if (EditorApplication.isCompiling ||
                EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode ||
                AssetDatabase.IsAssetImportWorkerProcess())
            {
                //GONetLog.Debug("Skipping hierarchy check - editor is busy compiling, updating, or importing.");
                return;
            }

            //////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            // SKIP first/oth frame due to this method being called when coming out of other GONet generation stuff (e.g., editor support: "Fix GONet Generated Code")
            if (Time.frameCount == 0) return;
            //////////////////////////////////////////////////////////////////////////////////////////////////////////////////

            bool isHierarchyChangingDueToExitingPlayModeInEditor =
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange.HasValue &&
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.EnteredEditMode &&
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange_frameCount == Time.frameCount; // IMPORTANT: this is how we know it "just" changed from play to edit mode...otherwise we could never run the logic we want after exiting the play mode and we start messing around with the hierarchy

            if (!Application.isPlaying &&
                !isHierarchyChangingDueToExitingPlayModeInEditor && // it would not be design time if we are playing (in editor) now would it?
                !IsCompiling &&
                !IsInitialEditorLoad)
            {
                if (GONetProjectSettings.IsDeferredChangeDetectionEnabled())
                {
                    SetSceneScanNeeded(true);
                    HandlePotentialChangeInPrefabPreviewMode_ProcessAnyDesignTimeDirty_IfAppropriate();
                    return;
                }

                PerformSceneChangeDetection_Original();
            }
        }

        private static void PerformSceneChangeDetection_Original()
        {
            HashSet<string> pathsToGnpsInScene = new();
            HashSet<string> loadedSceneNames = new(); // Track which scenes we're scanning
            int count = SceneManager.loadedSceneCount;
            for (int i = 0; i < count; ++i)
            { //
                Scene loadedScene = EditorSceneManager.GetSceneAt(i);
                if (!IsSceneIncludedInBuild(loadedScene.path)) continue; // only consider scene changes when scene is included in the build since GONet does not care otherwise

                const string SLASHY_LITTLE_WALLACE_PREVENTS_DELETING_SIMILARLY_NAMED_SCENES = "/";
                string scenePrefix = string.Concat(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX, loadedScene.name, SLASHY_LITTLE_WALLACE_PREVENTS_DELETING_SIMILARLY_NAMED_SCENES);

                loadedSceneNames.Add(loadedScene.name); // Track this scene name

                foreach (var rootGO in loadedScene.GetRootGameObjects())
                {
                    foreach (var gonetParticipant in rootGO.GetComponentsInChildren<GONetParticipant>())
                    {
                        string fullUniquePath = DesignTimeMetadata.GetFullUniquePathInScene(gonetParticipant);
                        // TODO check if this exists in the metadata from last build and if not take note of the change/addition!
                        pathsToGnpsInScene.Add(fullUniquePath);
                    }
                }
            }

            GONetLog.Debug($"Here are all {pathsToGnpsInScene.Count} scene GNPs in loaded scenes ({string.Join(", ", loadedSceneNames)}):\n{string.Join("\n", pathsToGnpsInScene)}");
            ProcessAnyDesignTimeDirty_IfAppropriate_SceneSpecific(pathsToGnpsInScene, loadedSceneNames);
            if (!GONetProjectSettings.IsDeferredChangeDetectionEnabled())
            {
                HandlePotentialChangeInPrefabPreviewMode_ProcessAnyDesignTimeDirty_IfAppropriate();
            }
        }

        private static void HandlePotentialChangeInPrefabPreviewMode_ProcessAnyDesignTimeDirty_IfAppropriate()
        {
            if (GONetProjectSettings.IsDeferredChangeDetectionEnabled())
            {
                SetPrefabScanNeeded(true);
                return;
            }

            HandlePotentialChangeInPrefabPreviewMode_ProcessAnyDesignTimeDirty_IfAppropriate_Original();
        }

        /// <summary>
        /// ORIGINAL IMPLEMENTATION - Preserved exactly for fallback.
        /// </summary>
        private static void HandlePotentialChangeInPrefabPreviewMode_ProcessAnyDesignTimeDirty_IfAppropriate_Original()
        {
#if ADDRESSABLES_AVAILABLE
            // Update the addressable asset paths cache before processing changes
            UpdateAddressableAssetPathsCache();
#endif

            IEnumerable<DesignTimeMetadata> designTimeLocations_gonetParticipants_lastBuild =
                GONetSpawnSupport_Runtime.LoadDesignTimeMetadataFromPersistence();

            // Get paths from last build for project://, resources://, and addressables:// prefixes
            IEnumerable<string> gnpPrefabAssetPaths_lastBuild =
                designTimeLocations_gonetParticipants_lastBuild
                    .Where(x => x.Location.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX) ||
                               x.Location.StartsWith(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX) ||
                               x.Location.StartsWith(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX))
                    .Select(x => {
                        if (x.Location.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX))
                            return x.Location.Substring(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX.Length);
                        else if (x.Location.StartsWith(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX))
                            return x.Location.Substring(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX.Length);
                        else if (x.Location.StartsWith(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX))
                            return x.Location.Substring(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX.Length);
                        else
                            return x.Location; // Fallback for unknown prefixes
                    });

            // TODO FIXME doing this every time the hiearchy changes is crazy....mainy due to high processing time....need to attempt to move this entire method logic to be called in the other option: AssetPostprocessor.OnPostprocessAllAssets, where we hope we can more narrowly focus in on the specific data that is changing instead of searching the entire project essentially!
            //   --- UPDATE to above TODO FIXME: there is an implementation of this inside AssetPostprocessor/Magoo.OnPostprocessAllAssets (find OnPostprocessAllAssets_TakeNoteOfAnyGONetChanges), so this here can/should probably be removed as redundant and this is less performant for sure.
            List<GONetParticipant> gnpsInProjectResources =
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GatherGONetParticipantsInAllResourcesFolders();

            // Also gather addressable GONetParticipants for comprehensive change detection
            List<GONetParticipant> gnpsInAddressables = GatherAddressableGONetParticipants();

            // Combine both resources and addressables paths for comprehensive comparison
            HashSet<string> allGnpPaths = new();
            allGnpPaths.UnionWith(gnpsInProjectResources.Select(g => AssetDatabase.GetAssetPath(g)));
            allGnpPaths.UnionWith(gnpsInAddressables.Select(g => AssetDatabase.GetAssetPath(g)));

            // Debug logging to understand the comparison issue
            GONetLog.Debug($"Last build paths ({gnpPrefabAssetPaths_lastBuild.Count()}): {string.Join(", ", gnpPrefabAssetPaths_lastBuild.Take(10))}");
            GONetLog.Debug($"Current paths ({allGnpPaths.Count}): {string.Join(", ", allGnpPaths.Take(10))}");

            {// Check for GNP deletes: was previously in gnpPrefabAssetPaths_lastBuild, but NOT in the updated list of gnp prefabs
             // Check for GNP deletes: previously in gnpPrefabAssetPaths_lastBuild, but NOT in currentGnpAssetPaths
                IEnumerable<string> deletedGnpPaths = gnpPrefabAssetPaths_lastBuild
                    .Where(path => !allGnpPaths.Contains(path));

                foreach (string deletedPath in deletedGnpPaths)
                {
                    // Safeguard: Check if this is a false positive - if the asset still exists in either collection,
                    // then it wasn't actually deleted, likely due to timing issues during addressables modifications
                    bool stillExistsInResources = gnpsInProjectResources.Any(g => AssetDatabase.GetAssetPath(g) == deletedPath);
                    bool stillExistsInAddressables = gnpsInAddressables.Any(g => AssetDatabase.GetAssetPath(g) == deletedPath);

                    if (stillExistsInResources || stillExistsInAddressables)
                    {
                        GONetLog.Debug($"Skipping false positive deletion for {deletedPath}: stillInResources={stillExistsInResources}, stillInAddressables={stillExistsInAddressables}");
                        continue; // Skip this false positive
                    }

                    // Check if this was an addressable asset in the last build to provide the correct message
                    bool wasAddressableAsset =
#if ADDRESSABLES_AVAILABLE
                        WasAddressableInLastBuild(deletedPath, designTimeLocations_gonetParticipants_lastBuild);
#else
                        false;
#endif

                    string messageType = wasAddressableAsset ? "addressable" : "project resources";

                    GONetLog.Debug($"Confirmed deletion for {deletedPath}: wasAddressable={wasAddressableAsset}");
                    AddGONetDesignTimeDirtyReason($"GONetParticipant prefab deleted from {messageType}: {deletedPath}");
                }
            }

            {// Check for GNP adds: was previously NOT in gnpPrefabAssetPaths_lastBuild, but is now in the updated list of gnp prefabs
             // Check for GNP adds: previously NOT in gnpPrefabAssetPaths_lastBuild, but is now in currentGnpAssetPaths
                IEnumerable<string> addedGnpPaths = allGnpPaths
                    .Where(path => !gnpPrefabAssetPaths_lastBuild.Contains(path));

                foreach (string addedPath in addedGnpPaths)
                {
                    // Check if this is an addressable asset to provide the correct message
                    bool isAddressableAsset = false;
#if ADDRESSABLES_AVAILABLE
                    isAddressableAsset = IsAddressableAsset(addedPath);
#endif
                    string messageType = isAddressableAsset ? "addressable" : "project resources";
                    AddGONetDesignTimeDirtyReason($"GONetParticipant prefab added to {messageType}: {addedPath}");
                }
            }
        }

        private static void OnProjectChanged_EnsureDesignTimeLocationsCurrent_ProjectOnly()
        {
            // GONet 1.4 stops doing this unless we are building or manually calling 'fix': EnsureDesignTimeLocationsCurrent_ProjectOnly();
        }

        internal static void EnsureDesignTimeLocationsCurrent_ProjectOnly()
        {
            // clear it now as it will be built back up below
            RemoveFromPersistence_WherePrefixMatches(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX);
            RemoveFromPersistence_WherePrefixMatches(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX);

            // IMPORTANT: have to load them all up for else the following call will not "find" them all and only the ones that happened to be loaded already would be found/processed
            Resources.LoadAll<GONetParticipant>(string.Empty);
            foreach (var gonetParticipant in Resources.FindObjectsOfTypeAll<GONetParticipant>())
            {
                OnProjectChanged_EnsureDesignTimeLocationsCurrent_ProjectOnly_Single(gonetParticipant);
            }

            // IMPORTANT: have to do this because the above call to Resources.FindObjectsOfTypeAll<GONetParticipant>() does NOT identify a prefab that just had GNP added to it this frame!!!
            foreach (GONetParticipant gonetParticipant in GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GetGNPsAddedToPrefabThisFrame())
            {
                OnProjectChanged_EnsureDesignTimeLocationsCurrent_ProjectOnly_Single(gonetParticipant);
            }

            // Scan for addressable GONetParticipant prefabs
            EnsureDesignTimeLocationsCurrent_AddressablesOnly();
        }

        internal static void OnProjectChanged_EnsureDesignTimeLocationsCurrent_ProjectOnly_Single(GONetParticipant gonetParticipant)
        {
            if (gonetParticipant != null)
            {
                string projectPath = AssetDatabase.GetAssetPath(gonetParticipant);
                bool isProjectAsset = !string.IsNullOrWhiteSpace(projectPath);
                if (isProjectAsset)
                {
#if ADDRESSABLES_AVAILABLE
                    // Check if this is an addressable asset first
                    if (IsAddressableAsset(projectPath))
                    {
                        return; // Don't create project:// entry for addressable assets
                    }
#endif

                    // Only process prefabs in Resources folders - project:// prefabs are "dormant" and don't need tracking.
                    // When they're placed in a scene, the scene:// path will be tracked.
                    // When they're moved to Resources or added to Addressables, they'll be processed then.
                    if (projectPath.Contains("/Resources/"))
                    {
                        string currentLocation = string.Concat(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX, projectPath);
                        EnsureDesignTimeLocationCurrent(gonetParticipant, currentLocation); // have to do proper unity serialization stuff for this to stick!
                    }
                    // else: Skip project:// prefabs - they're not spawnable at runtime and don't need design-time tracking

                    // Check and update addressables information
                    UpdateAddressablesMetadata(gonetParticipant, projectPath);

                    //gonetParticipant.DesignTimeLocation = currentLocation; // so, set it  directly and it seems to stick/save/persist just fine
                }
            }
            else if ((object)gonetParticipant != null && !string.IsNullOrWhiteSpace(gonetParticipant.DesignTimeLocation))
            {
                EnsureExistsInPersistence_WithTheseValues(gonetParticipant.DesignTimeLocation);
            }
        }

        private static void OnHierarchyChanged_EnsureDesignTimeLocationsCurrent_SceneOnly()
        {
            //GONetLog.Debug($"FRAME: {Time.frameCount} .... OnHierarchyChanged_EnsureDesignTimeLocationsCurrent_SceneOnly"); // COMMENTED - spammy log (log cleanup)

            //////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            // SKIP code gen on first/oth frame due to this method being called when coming out of other GONet generation stuff (e.g., editor support: "Fix GONet Generated Code")
            if (Time.frameCount == 0) return;
            //////////////////////////////////////////////////////////////////////////////////////////////////////////////////

            bool isHierarchyChangingDueToExitingPlayModeInEditor = 
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange.HasValue && 
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange == PlayModeStateChange.EnteredEditMode &&
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.LastPlayModeStateChange_frameCount == Time.frameCount; // IMPORTANT: this is how we know it "just" changed from play to edit mode...otherwise we could never run the logic we want after exiting the play mode and we start messing around with the hierarchy

            if (!Application.isPlaying && !isHierarchyChangingDueToExitingPlayModeInEditor) // it would not be design time if we are playing (in editor) now would it?
            {
                bool somethingChanged = false;
                int count = SceneManager.loadedSceneCount;
                for (int i = 0; i < count; ++i)
                {
                    Scene loadedScene = EditorSceneManager.GetSceneAt(i);

                    const string SLASHY_LITTLE_WALLACE_PREVENTS_DELETING_SIMILARLY_NAMED_SCENES = "/";
                    string scenePrefix = string.Concat(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX, loadedScene.name, SLASHY_LITTLE_WALLACE_PREVENTS_DELETING_SIMILARLY_NAMED_SCENES);
                    RemoveFromPersistence_WherePrefixMatches(scenePrefix); // clear anything already present from these scene now as it will be built back up below

                    foreach (var rootGO in loadedScene.GetRootGameObjects())
                    {
                        foreach (var gonetParticipant in rootGO.GetComponentsInChildren<GONetParticipant>())
                        {
                            string fullUniquePath = DesignTimeMetadata.GetFullUniquePathInScene(gonetParticipant);
                            if (fullUniquePath != gonetParticipant.DesignTimeLocation)
                            {
                                somethingChanged = true;
                                EnsureDesignTimeLocationCurrent(gonetParticipant, fullUniquePath); // have to do proper unity serialization stuff for this to stick!
                            }
                            else
                            {
                                EnsureExistsInPersistence_WithTheseValues(fullUniquePath); // although this is also called inside EnsureDesignTimeLocationCurrent, we need to call it here too in case the generated file this information goes into is manually deleted on the filesystem and the information was lost...this is a failsafe method to ensure it is populated!
                            }
                        }
                    }
                }

                if (somethingChanged)
                {
                    // NOTE: there is no longer anything else to do since we save the data outside the GNP itself in the DesignTimeLocations.json
                    //EditorSceneManager.MarkAllScenesDirty();
                    //EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo(); // this may be too much....they will save when they want to...normally
                }
            }
        }

        internal static void EnsureExistsInPersistence_WithTheseValues(DesignTimeMetadata ensureExistsDtm)
        {
            // Skip project:// prefabs - they are "dormant" and don't need persistence.
            // Only persist resources://, addressables://, and scene:// paths.
            if (ensureExistsDtm.Location.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX))
            {
                return;
            }

            IEnumerable<DesignTimeMetadata> persistedDtms = GONetSpawnSupport_Runtime.LoadDesignTimeMetadataFromPersistence();

            // Check for exact match OR equivalent project:// ↔ resources:// match (backward compat migration)
            string equivalentProjectPath = null;
            if (ensureExistsDtm.Location.StartsWith(GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX))
            {
                equivalentProjectPath = ensureExistsDtm.Location.Replace(
                    GONetSpawnSupport_Runtime.RESOURCES_HIERARCHY_PREFIX,
                    GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX);
            }

            bool doesAlreadyExist = persistedDtms.Any(x => x.Location == ensureExistsDtm.Location);
            bool hasEquivalentProjectEntry = equivalentProjectPath != null &&
                persistedDtms.Any(x => x.Location == equivalentProjectPath);

            if (doesAlreadyExist || hasEquivalentProjectEntry)
            {
                if (ensureExistsDtm.CodeGenerationId != GONetParticipant.CodeGenerationId_Unset)
                {
                    var updatedList = new List<DesignTimeMetadata>();
                    bool migrated = false;

                    foreach (DesignTimeMetadata persistedDtm in persistedDtms)
                    {
                        bool isExactMatch = persistedDtm.Location == ensureExistsDtm.Location;
                        bool isEquivalentMatch = equivalentProjectPath != null && persistedDtm.Location == equivalentProjectPath;

                        if (isExactMatch || isEquivalentMatch)
                        {
                            // Migrate project:// to resources:// if needed
                            if (isEquivalentMatch && !isExactMatch)
                            {
                                persistedDtm.Location = ensureExistsDtm.Location; // Update to resources:// prefix
                                migrated = true;
                            }

                            if (ensureExistsDtm.CodeGenerationId != GONetParticipant.CodeGenerationId_Unset)
                            {
                                persistedDtm.CodeGenerationId = ensureExistsDtm.CodeGenerationId;
                            }
                            persistedDtm.UnityGuid = ensureExistsDtm.UnityGuid;
                        }
                        updatedList.Add(persistedDtm);
                    }

                    if (migrated)
                    {
                        GONetLog.Debug($"Migrated project:// to resources:// for: {ensureExistsDtm.Location}");
                    }
                    OverwritePersistenceWith(updatedList);
                }
            }
            else
            {
                var updatedListDtms = new List<DesignTimeMetadata>(persistedDtms);
                updatedListDtms.Add(ensureExistsDtm);
                OverwritePersistenceWith(updatedListDtms);
            }
        }

        static void RemoveFromPersistence_WherePrefixMatches(string prefixToMatch)
        {
            IEnumerable<DesignTimeMetadata> all = GONetSpawnSupport_Runtime.LoadDesignTimeMetadataFromPersistence();

            all = all.Where(x => !x.Location.StartsWith(prefixToMatch));

            OverwritePersistenceWith(all);
        }

#if ADDRESSABLES_AVAILABLE
        /// <summary>
        /// Updates the DesignTimeMetadata for a GONetParticipant with addressables information if available.
        /// </summary>
        private static void UpdateAddressablesMetadata(GONetParticipant gonetParticipant, string assetPath)
        {
            var designTimeMetadata = GONetSpawnSupport_Runtime.GetDesignTimeMetadata(gonetParticipant);

            try
            {
                var addressableSettings = AddressableAssetSettingsDefaultObject.Settings;
                if (addressableSettings == null)
                {
                    // No addressables configured - LoadType and AddressableKey are now computed from location prefix
                    return;
                }

                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                var entry = addressableSettings.FindAssetEntry(guid);

                if (entry != null && !string.IsNullOrEmpty(entry.address))
                {
                    // Asset is addressable - LoadType and AddressableKey are now computed from location prefix
                    GONetLog.Debug($"GONetParticipant '{gonetParticipant.name}' detected as addressable with key: '{entry.address}'");
                }
                else
                {
                    // Asset is not addressable - LoadType and AddressableKey are now computed from location prefix
                }
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"Failed to check addressables status for '{assetPath}': {ex.Message}");

                // Fallback to Resources on error - LoadType and AddressableKey are now computed from location prefix
            }
        }

        private const string SESSION_STATE_ADDRESSABLES_CACHE_KEY = "GONet.AddressableAssetPaths";

#if ADDRESSABLES_AVAILABLE
        /// <summary>
        /// Registers callbacks for Addressables build events and group modifications
        /// </summary>
        private static void RegisterAddressableBuildCallbacks()
        {
            var addressableSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addressableSettings != null)
            {
                // Hook into build completion events
                addressableSettings.OnDataBuilderComplete += OnAddressablesBuildComplete;

                // Hook into group/entry modification events
                AddressableAssetSettings.OnModificationGlobal += OnAddressablesModification;
            }
        }

        /// <summary>
        /// Called when an Addressables build completes
        /// </summary>
        private static void OnAddressablesBuildComplete(AddressableAssetSettings settings,
                                                       IDataBuilder builder,
                                                       IDataBuilderResult result)
        {
            GONetLog.Debug("Addressables build completed, checking for GONetParticipant changes");

            // Force update of the cache after addressables build
            UpdateAddressableAssetPathsCache();

            // Delay change detection to ensure addressables system is fully updated
            EditorApplication.delayCall += () => {
                EditorApplication.delayCall += () => {
                    GONetLog.Debug("Running addressables build change detection");

                    // Note: If metadata isn't cached, the detection will use false positive protection

                    OnHierarchyChanged_TakeNoteOfAnyGONetChanges_SceneOnly();
                };
            };
        }

        /// <summary>
        /// Called when Addressables groups or entries are modified
        /// </summary>
        private static void OnAddressablesModification(AddressableAssetSettings settings,
                                                       AddressableAssetSettings.ModificationEvent eventType,
                                                       object eventData)
        {
            // Only care about entry-related modifications that might affect GONet prefabs
            if (eventType == AddressableAssetSettings.ModificationEvent.EntryAdded ||
                eventType == AddressableAssetSettings.ModificationEvent.EntryRemoved ||
                eventType == AddressableAssetSettings.ModificationEvent.EntryModified)
            {
                GONetLog.Debug($"Addressables modification detected ({eventType}), checking for GONet changes");
                UpdateAddressableAssetPathsCache();

                // Use direct detection approach that doesn't depend on metadata caching
                // Execute immediately to ensure dirty flag is set before any potential play mode transition
                ProcessAddressablesModificationDirect(eventType, eventData);
            }
        }

        /// <summary>
        /// Directly processes addressables modifications for GONetParticipant prefabs without depending on metadata caching
        /// </summary>
        private static void ProcessAddressablesModificationDirect(AddressableAssetSettings.ModificationEvent eventType, object eventData)
        {
            try
            {
                // Extract the asset path from the modification event
                string assetPath = null;
                string address = null;

                // Debug: Log the actual type of eventData to understand what Unity passes
                GONetLog.Debug($"ProcessAddressablesModificationDirect - eventType: {eventType}, eventData type: {eventData?.GetType()?.Name ?? "null"}");

                if (eventData is AddressableAssetEntry entry)
                {
                    assetPath = AssetDatabase.GUIDToAssetPath(entry.guid);
                    address = entry.address;
                    GONetLog.Debug($"Extracted from AddressableAssetEntry - assetPath: {assetPath}, address: {address}");
                }
                else if (eventData is System.Collections.Generic.List<AddressableAssetEntry> entryList && entryList.Count > 0)
                {
                    // Unity sometimes passes a List<AddressableAssetEntry> instead of a single entry
                    GONetLog.Debug($"Processing List<AddressableAssetEntry> with {entryList.Count} entries");

                    // Process each entry in the list
                    foreach (var listEntry in entryList)
                    {
                        string entryAssetPath = AssetDatabase.GUIDToAssetPath(listEntry.guid);
                        GONetLog.Debug($"Processing list entry - assetPath: {entryAssetPath}, address: {listEntry.address}");

                        // Check if this is a GONetParticipant prefab and process it
                        if (ProcessSingleAddressableEntry(entryAssetPath, listEntry.address, eventType))
                        {
                            GONetLog.Debug($"Successfully processed addressable entry: {entryAssetPath}");
                        }
                    }
                    return; // Exit early since we processed the list
                }
                else if (eventData is string guid)
                {
                    // Sometimes Unity might pass just the GUID string for removed entries
                    assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    GONetLog.Debug($"Extracted from GUID string - assetPath: {assetPath}");
                }
                else if (eventData != null)
                {
                    // Try to get asset path from other possible object types
                    GONetLog.Debug($"Unknown eventData type, attempting reflection...");
                    var eventDataType = eventData.GetType();

                    // Try to find a "guid" field or property
                    var guidField = eventDataType.GetField("guid");
                    var guidProperty = eventDataType.GetProperty("guid");

                    if (guidField != null)
                    {
                        var guidValue = guidField.GetValue(eventData)?.ToString();
                        if (!string.IsNullOrEmpty(guidValue))
                        {
                            assetPath = AssetDatabase.GUIDToAssetPath(guidValue);
                            GONetLog.Debug($"Extracted from guid field - assetPath: {assetPath}");
                        }
                    }
                    else if (guidProperty != null)
                    {
                        var guidValue = guidProperty.GetValue(eventData)?.ToString();
                        if (!string.IsNullOrEmpty(guidValue))
                        {
                            assetPath = AssetDatabase.GUIDToAssetPath(guidValue);
                            GONetLog.Debug($"Extracted from guid property - assetPath: {assetPath}");
                        }
                    }
                }

                if (string.IsNullOrEmpty(assetPath))
                {
                    GONetLog.Debug($"Could not extract asset path from addressables modification event (eventData: {eventData})");
                    return;
                }

                // Process the single entry
                ProcessSingleAddressableEntry(assetPath, address, eventType);
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"Error processing addressables modification: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes a single addressable entry and records it if it's a GONetParticipant prefab.
        /// Returns true if the entry was processed and recorded, false otherwise.
        /// </summary>
        private static bool ProcessSingleAddressableEntry(string assetPath, string address, AddressableAssetSettings.ModificationEvent eventType)
        {
            try
            {
                // Check if this asset is a GONetParticipant prefab
                if (!assetPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                {
                    return false; // Not a prefab, ignore
                }

                GONetParticipant gnp = AssetDatabase.LoadAssetAtPath<GONetParticipant>(assetPath);
                if (gnp == null)
                {
                    return false; // Not a GONetParticipant prefab, ignore
                }

                // Now we know this is a GONetParticipant prefab that was modified in addressables
                // Determine the change type and record it if not already recorded this session
                string changeType = eventType switch
                {
                    AddressableAssetSettings.ModificationEvent.EntryAdded => "added",
                    AddressableAssetSettings.ModificationEvent.EntryRemoved => "removed",
                    AddressableAssetSettings.ModificationEvent.EntryModified => "modified",
                    _ => "changed"
                };

                // Check for session deduplication - prevent recording same change multiple times
                if (WasChangeAlreadyRecordedThisSession(assetPath, changeType))
                {
                    GONetLog.Debug($"Skipping duplicate addressables change in session: {changeType} {assetPath}");
                    return false;
                }

                // Record the change using our holistic dual persistence system
                RecordAddressablesChange(assetPath, changeType);
                GONetLog.Debug($"Direct addressables change detected and recorded: {changeType} {assetPath}");
                return true;
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"Error processing single addressable entry ({assetPath}): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Records addressables changes using dual persistence: SessionState (session) + File (cross-session)
        /// Integrates with GONet's existing dirty reason system for consistent behavior
        /// </summary>
        private static void RecordAddressablesChange(string assetPath, string changeType)
        {
            // Create the dirty reason message using GONet's standard format
            string actionText = changeType switch
            {
                "added" => "added to",
                "removed" => "removed from",
                "modified" => "modified in",
                _ => "changed in"
            };

            // Use the standard format consistent with other detection systems
            string locationPrefix = "addressables://";
            string dirtyReason = $"GONetParticipant at {locationPrefix}{assetPath} was added or modified after the last build.";

            // Use GONet's existing file persistence system - this handles:
            // - File creation/management
            // - Timestamp formatting
            // - Duplicate detection
            // - Cross-session persistence
            AddGONetDesignTimeDirtyReason(dirtyReason);

            // ALSO store in SessionState for this-session deduplication and fast access
            // This prevents us from detecting the same change multiple times within a session
            const string ADDRESSABLES_SESSION_KEY = "GONet.AddressablesSession";
            string sessionKey = $"{changeType}:{assetPath}";

            string existingSession = SessionState.GetString(ADDRESSABLES_SESSION_KEY, "");
            if (string.IsNullOrEmpty(existingSession))
            {
                SessionState.SetString(ADDRESSABLES_SESSION_KEY, sessionKey);
            }
            else if (!existingSession.Contains(sessionKey))
            {
                SessionState.SetString(ADDRESSABLES_SESSION_KEY, $"{existingSession};{sessionKey}");
            }

            GONetLog.Debug($"Recorded addressables change: {dirtyReason}");
        }

        /// <summary>
        /// Checks if this addressables change was already recorded in this session
        /// Prevents duplicate detection within the same editor session
        /// </summary>
        private static bool WasChangeAlreadyRecordedThisSession(string assetPath, string changeType)
        {
            const string ADDRESSABLES_SESSION_KEY = "GONet.AddressablesSession";
            string sessionData = SessionState.GetString(ADDRESSABLES_SESSION_KEY, "");
            string changeKey = $"{changeType}:{assetPath}";

            return !string.IsNullOrEmpty(sessionData) && sessionData.Contains(changeKey);
        }

        /// <summary>
        /// Clears session-specific addressables tracking
        /// Called after successful builds to reset session state
        /// Note: File persistence is handled by GONet's existing build completion system
        /// </summary>
        private static void ClearAddressablesSessionTracking()
        {
            const string ADDRESSABLES_SESSION_KEY = "GONet.AddressablesSession";
            SessionState.EraseString(ADDRESSABLES_SESSION_KEY);
            GONetLog.Debug("Cleared addressables session tracking");
        }
#endif

        /// <summary>
        /// Updates the cache of addressable asset paths using Unity's SessionState for domain-reload safety
        /// </summary>
        private static void UpdateAddressableAssetPathsCache()
        {
            var cachedPaths = new List<string>();

            var allGNPs = GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GatherGONetParticipantsInAllResourcesFolders();
            foreach (var gnp in allGNPs)
            {
                string assetPath = UnityEditor.AssetDatabase.GetAssetPath(gnp);
                if (!string.IsNullOrEmpty(assetPath) && !assetPath.Contains("/Resources/"))
                {
                    // This must be an addressable since it's not in Resources but was found by the gather method
                    cachedPaths.Add(assetPath);
                }
            }

            // Store in SessionState using JSON serialization for the list
            string json = JsonUtility.ToJson(new SerializableStringList { items = cachedPaths.ToArray() });
            SessionState.SetString(SESSION_STATE_ADDRESSABLES_CACHE_KEY, json);

            // GONetLog.Debug($"UpdateAddressableAssetPathsCache: Cached {cachedPaths.Count} addressable asset paths in SessionState");
        }

        [System.Serializable]
        private class SerializableStringList
        {
            public string[] items;
        }

        /// <summary>
        /// Gets the cached addressable asset paths from Unity's SessionState
        /// </summary>
        private static HashSet<string> GetCachedAddressableAssetPaths()
        {
            var result = new HashSet<string>();

            string json = SessionState.GetString(SESSION_STATE_ADDRESSABLES_CACHE_KEY, "");
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var data = JsonUtility.FromJson<SerializableStringList>(json);
                    if (data?.items != null)
                    {
                        foreach (var item in data.items)
                        {
                            result.Add(item);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    GONetLog.Warning($"Failed to deserialize addressable cache from SessionState: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Checks if the given asset path was an addressable in the last build based on stored metadata
        /// </summary>
        private static bool WasAddressableInLastBuild(string assetPath, IEnumerable<DesignTimeMetadata> lastBuildMetadata)
        {
            return lastBuildMetadata.Any(x =>
                x.Location.StartsWith(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX) &&
                x.Location.Substring(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX.Length) == assetPath);
        }

        /// <summary>
        /// Checks if the given asset path is configured as an addressable asset
        /// </summary>
        private static bool IsAddressableAsset(string assetPath)
        {
            // Check if the asset is not in a Resources folder (which would make it Resources-based)
            if (assetPath.Contains("/Resources/"))
            {
                return false;
            }

            // Check the SessionState cache that gets populated when GatherGONetParticipantsInAllResourcesFolders runs
            var cachedPaths = GetCachedAddressableAssetPaths();
            return cachedPaths.Contains(assetPath);
        }

        /// <summary>
        /// Scans for addressable GONetParticipant prefabs and creates metadata entries with ADDRESSABLES_HIERARCHY_PREFIX
        /// </summary>
        internal static void EnsureDesignTimeLocationsCurrent_AddressablesOnly()
        {
            var addressableSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addressableSettings == null)
            {
                return;
            }

            foreach (var group in addressableSettings.groups)
            {
                if (group == null) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.address)) continue;

                    // Load the asset to check if it contains a GONetParticipant
                    string assetPath = AssetDatabase.GUIDToAssetPath(entry.guid);
                    if (string.IsNullOrEmpty(assetPath)) continue;

                    // Check if it's a prefab file
                    if (assetPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                    {
                        GONetParticipant prefab = AssetDatabase.LoadAssetAtPath<GONetParticipant>(assetPath);
                        if (prefab != null)
                        {
                            // Found an addressable GONetParticipant prefab
                            string addressableLocation = string.Concat(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX, entry.address);

                            // Create or update design time metadata
                            var designTimeMetadata = GONetSpawnSupport_Runtime.GetDesignTimeMetadata(prefab);
                            if (designTimeMetadata != null)
                            {
                                designTimeMetadata.Location = addressableLocation;
                                designTimeMetadata.UnityGuid = entry.guid;

                                // Ensure it's in the persistence system
                                EnsureExistsInPersistence_WithTheseValues(addressableLocation);
                            }
                        }
                    }
                    // Check if it's a folder - scan for prefabs inside
                    else if (AssetDatabase.IsValidFolder(assetPath))
                    {
                        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { assetPath });

                        foreach (string prefabGuid in prefabGuids)
                        {
                            string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuid);
                            GONetParticipant prefab = AssetDatabase.LoadAssetAtPath<GONetParticipant>(prefabPath);
                            if (prefab != null)
                            {
                                // For folders, use the full prefab filename (including .prefab extension) as the addressable key
                                string prefabFileName = System.IO.Path.GetFileName(prefabPath);
                                string addressableKey = string.Concat(entry.address, "/", prefabFileName);
                                string addressableLocation = string.Concat(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX, addressableKey);

                                // Create or update design time metadata
                                var designTimeMetadata = GONetSpawnSupport_Runtime.GetDesignTimeMetadata(prefab);
                                if (designTimeMetadata != null)
                                {
                                    designTimeMetadata.Location = addressableLocation;
                                    designTimeMetadata.UnityGuid = prefabGuid;

                                    // Ensure it's in the persistence system
                                    EnsureExistsInPersistence_WithTheseValues(addressableLocation);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gathers all GONetParticipant prefabs that are currently marked as addressable assets.
        /// This is used for change detection to identify when prefabs are added/removed from addressables.
        /// </summary>
        /// <returns>List of GONetParticipant components from addressable prefabs</returns>
        internal static List<GONetParticipant> GatherAddressableGONetParticipants()
        {
            List<GONetParticipant> addressableGNPs = new List<GONetParticipant>();

            var addressableSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addressableSettings == null)
            {
                return addressableGNPs;
            }

            foreach (var group in addressableSettings.groups)
            {
                if (group == null) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.address)) continue;

                    // Load the asset to check if it contains a GONetParticipant
                    string assetPath = AssetDatabase.GUIDToAssetPath(entry.guid);
                    if (string.IsNullOrEmpty(assetPath)) continue;

                    // Check if it's a prefab file
                    if (assetPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                    {
                        GONetParticipant prefab = AssetDatabase.LoadAssetAtPath<GONetParticipant>(assetPath);
                        if (prefab != null)
                        {
                            addressableGNPs.Add(prefab);
                        }
                    }
                    // Check if it's a folder - scan for prefabs inside
                    else if (AssetDatabase.IsValidFolder(assetPath))
                    {
                        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { assetPath });

                        foreach (string prefabGuid in prefabGuids)
                        {
                            string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuid);
                            GONetParticipant prefab = AssetDatabase.LoadAssetAtPath<GONetParticipant>(prefabPath);
                            if (prefab != null)
                            {
                                addressableGNPs.Add(prefab);
                            }
                        }
                    }
                }
            }

            return addressableGNPs;
        }
#else
        /// <summary>
        /// Fallback when Addressables is not available - no addressables scanning needed.
        /// </summary>
        internal static void EnsureDesignTimeLocationsCurrent_AddressablesOnly()
        {
            // No addressables support, nothing to scan
        }

        /// <summary>
        /// Fallback when Addressables is not available - returns empty list of addressable GONetParticipants.
        /// </summary>
        /// <returns>Empty list since addressables is not available</returns>
        internal static List<GONetParticipant> GatherAddressableGONetParticipants()
        {
            return new List<GONetParticipant>();
        }

        /// <summary>
        /// Fallback when Addressables is not available - ensures metadata uses Resources load type.
        /// </summary>
        private static void UpdateAddressablesMetadata(GONetParticipant gonetParticipant, string assetPath)
        {
            // LoadType and AddressableKey are now computed from location prefix - no need to set them
        }
#endif

        /// <summary>
        /// Do all proper unity serialization stuff or else a change will NOT stick/save/persist.
        /// </summary>
        private static void EnsureDesignTimeLocationCurrent(GONetParticipant gonetParticipant, string currentLocation)
        {
            string goName = gonetParticipant.gameObject.name; // IMPORTANT: after a call to serializedObject.ApplyModifiedProperties(), gonetParticipant is unity "null" and this line MUst come before that!

            /*
            SerializedObject serializedObject = new SerializedObject(gonetParticipant); // use the damned unity serializtion stuff or be doomed to fail on saving stuff to scene as you hope/expect!!!
            SerializedProperty serializedProperty = serializedObject.FindProperty(nameof(GONetParticipant.DesignTimeLocation));
            serializedObject.Update();
            serializedProperty.stringValue = currentLocation; // set it this way or else it will NOT work with prefabs!
            gonetParticipant.DesignTimeLocation = currentLocation; // doubly sure
            serializedObject.ApplyModifiedProperties();
            */

            //GONetLog.Debug("set design time location for name: " + goName + " to NEW value: " + currentLocation); // COMMENTED - spammy log (log cleanup)

            DesignTimeMetadata designTimeMetadata = GONetSpawnSupport_Runtime.GetDesignTimeMetadata(gonetParticipant);
            designTimeMetadata.Location = currentLocation;
            
            string unityGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(gonetParticipant));
            designTimeMetadata.UnityGuid = unityGuid;

            {
                SerializedObject serializedObject = new SerializedObject(gonetParticipant); // use the damned unity serializtion stuff or be doomed to fail on saving stuff to scene as you hope/expect!!!
                SerializedProperty serializedProperty = serializedObject.FindProperty(nameof(GONetParticipant.UnityGuid));
                serializedObject.Update();
                serializedProperty.stringValue = unityGuid; // set it this way or else it will NOT work with prefabs!
                gonetParticipant.UnityGuid = unityGuid;
                serializedObject.ApplyModifiedProperties();
            }

            EnsureExistsInPersistence_WithTheseValues(designTimeMetadata);
        }

        internal static void ClearAllDesignTimeMetadata()
        {
            GONetSpawnSupport_Runtime.ClearAllDesignTimeMetadata();
            OverwritePersistenceWith(Enumerable.Empty<DesignTimeMetadata>());
        }

        /// <summary>
        /// POST: contents of <see cref="allDesignTimeLocationsEncountered"/> persisted.
        /// </summary>
        private static bool hasLoggedOverwritePersistenceWarning = false;
        private static void OverwritePersistenceWith(IEnumerable<DesignTimeMetadata> newCompleteDesignTimeLocations)
        {
            if (!ProcessBuildHelper.IsBuilding)
            {
                // NOTE: Only log this warning once per editor session to avoid 125+ repeated warnings during Fast Iteration Mode (log cleanup)
                if (!hasLoggedOverwritePersistenceWarning)
                {
                    hasLoggedOverwritePersistenceWarning = true;
                    GONetLog.Warning($"Oops.  Will not overwrite persistence with {nameof(newCompleteDesignTimeLocations)}, because GONet v1.4+ only does that during the time when a build is occurring.  Gotta ensure old logic does not screw things up!");
                }
                return;
            }

            string directory = Path.Combine(Application.streamingAssetsPath, GONetSpawnSupport_Runtime.GONET_STREAMING_ASSETS_FOLDER);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Filter out invalid entries:
            // 1. Empty/whitespace locations
            // 2. Scene entries with unset code gen ID
            // 3. Project:// prefabs - they are "dormant" and don't need persistence (only scene://, resources://, addressables:// are valid)
            var invalidMofosWillNotPersist = newCompleteDesignTimeLocations
                .Where(x => string.IsNullOrWhiteSpace(x.Location) ||
                    (x.Location.StartsWith(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX)
                        && x.CodeGenerationId == GONetParticipant.CodeGenerationId_Unset) ||
                    x.Location.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX));
            foreach (var invalid in invalidMofosWillNotPersist)
            {
                GONetLog.Warning($"This little piggy is not going to the market!  He has some missing data that is not cool to persist!  Most likely, this is OK to overlook based on latest implementation preference and reliance on project over scene centricity.  As json: {JsonUtility.ToJson(invalid)}");
            }

            DesignTimeMetadataLibrary designTimeMetadataLibrary = new DesignTimeMetadataLibrary()
            {
                Entries = newCompleteDesignTimeLocations
                    .Where(x => !invalidMofosWillNotPersist.Contains(x)).OrderBy(x => x.Location).ToArray(),
            };

            string fullPath = Path.Combine(Application.streamingAssetsPath, GONetSpawnSupport_Runtime.DESIGN_TIME_METADATA_FILE_POST_STREAMING_ASSETS);
            string fileContents = JsonUtility.ToJson(designTimeMetadataLibrary, prettyPrint: true);
            //GONetLog.Debug($"~~~~~~~~~~~~GEEPs isBuilding? {ProcessBuildHelper.IsBuilding} writing all text to: {fullPath}\n{fileContents}"); // COMMENTED - spammy log (log cleanup)
            WriteAllTextWithRetry(fullPath, fileContents);
        }

        /// <summary>
        /// Writes text to a file with retry logic to handle transient file locking issues.
        /// Win32 error 1224 (ERROR_USER_MAPPED_FILE) can occur when Unity or antivirus has the file locked.
        /// </summary>
        private static void WriteAllTextWithRetry(string path, string contents, int maxRetries = 3, int delayMs = 100)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    File.WriteAllText(path, contents);
                    return; // Success
                }
                catch (IOException ex) when (attempt < maxRetries - 1)
                {
                    GONetLog.Warning($"[GONetSpawnSupport] File write attempt {attempt + 1}/{maxRetries} failed for {path}: {ex.Message}. Retrying in {delayMs}ms...");
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs *= 2; // Exponential backoff
                }
            }
            // Final attempt - let exception propagate if it fails
            File.WriteAllText(path, contents);
        }

        /// <summary>
        /// Saves a content snapshot after a successful build (async operation, fire and forget)
        /// </summary>
        private static async void SaveContentSnapshotAfterBuild()
        {
            try
            {
                GONetLog.Debug("Creating content snapshot after successful build...");

                var snapshot = await GONetContentSnapshot.CreateSnapshotAsync();
                string snapshotPath = GetContentSnapshotFilePath();

                GONetContentSnapshot.SaveSnapshot(snapshot, snapshotPath);

                GONetLog.Debug($"Content snapshot saved to {snapshotPath}");
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"Failed to save content snapshot after build: {ex.Message}");
                GONetLog.Error($"Exception saving content snapshot: {ex}");
            }
        }

        #region Debug Menu Items

        [UnityEditor.MenuItem("GONet/Debug/Deferred Detection/Show Status", priority = 100)]
        public static void ShowDeferredDetectionStatus()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("=== GONet Deferred Detection Status ===");
            sb.AppendLine($"Optimization Enabled: {GONetProjectSettings.IsDeferredChangeDetectionEnabled()}");
            sb.AppendLine($"Prefab Scan Needed: {IsPrefabScanNeeded()}");
            sb.AppendLine($"Project Scan Needed: {IsProjectScanNeeded()}");
            sb.AppendLine($"Scene Scan Needed: {IsSceneScanNeeded()}");
            sb.AppendLine($"Suppress Prefab Logging: {GONetProjectSettings.ShouldSuppressPrefabDiscoveryLogging()}");

            Debug.Log(sb.ToString());
        }

        [UnityEditor.MenuItem("GONet/Debug/Deferred Detection/Force Clear All Flags", priority = 101)]
        public static void ForceClearDeferredFlags()
        {
            ClearAllDeferredScanFlags();
            Debug.Log("GONet: All deferred detection flags cleared manually.");
        }

        [UnityEditor.MenuItem("GONet/Debug/Deferred Detection/Force Set All Flags", priority = 102)]
        public static void ForceSetDeferredFlags()
        {
            SetPrefabScanNeeded(true);
            SetProjectScanNeeded(true);
            SetSceneScanNeeded(true);
            Debug.Log("GONet: All deferred detection flags set (will trigger full scan on next play mode entry).");
        }

        [UnityEditor.MenuItem("GONet/Debug/Deferred Detection/View Full Dirty Reasons File", priority = 110)]
        public static void ViewFullDirtyReasonsFile()
        {
            string filePath = GetDesignTimeDirtyReasonsFilePath();

            if (!File.Exists(filePath))
            {
                Debug.Log("GONet: No dirty reasons file exists. Project appears to be clean.");
                return;
            }

            try
            {
                string fullContents = File.ReadAllText(filePath);
                string[] lines = fullContents.Split('\n');

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("=== GONet Dirty Reasons File (FULL CONTENTS) ===");
                sb.AppendLine($"File: {filePath}");
                sb.AppendLine($"Total Lines: {lines.Length}");
                sb.AppendLine("================================================");
                sb.AppendLine(fullContents);
                sb.AppendLine("================================================");

                Debug.Log(sb.ToString());

                if (lines.Length > 50)
                {
                    if (UnityEditor.EditorUtility.DisplayDialog(
                        "GONet Dirty Reasons",
                        $"The dirty reasons file has {lines.Length} lines.\n\n" +
                        "Full contents have been logged to the Console.\n\n" +
                        "Would you like to open the file in your default text editor?",
                        "Open in Editor", "Close"))
                    {
                        UnityEditor.EditorUtility.OpenWithDefaultApp(filePath);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"GONet: Error reading dirty reasons file: {ex.Message}");
            }
        }

        [UnityEditor.MenuItem("GONet/Debug/Deferred Detection/Clear Dirty Reasons File", priority = 111)]
        public static void ClearDirtyReasonsFile()
        {
            string filePath = GetDesignTimeDirtyReasonsFilePath();

            if (!File.Exists(filePath))
            {
                Debug.Log("GONet: No dirty reasons file exists to clear.");
                return;
            }

            if (UnityEditor.EditorUtility.DisplayDialog(
                "Clear Dirty Reasons",
                "Are you sure you want to delete the dirty reasons file?\n\n" +
                "This will allow play mode to proceed without warnings, but may cause issues " +
                "if there are actual changes that require a rebuild.",
                "Delete File", "Cancel"))
            {
                try
                {
                    File.Delete(filePath);
                    Debug.Log("GONet: Dirty reasons file deleted.");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"GONet: Error deleting dirty reasons file: {ex.Message}");
                }
            }
        }

        #endregion
    }
}
