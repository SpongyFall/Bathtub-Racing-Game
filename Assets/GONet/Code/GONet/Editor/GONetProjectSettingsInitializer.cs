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
using UnityEditor;
using UnityEngine;

namespace GONet.Editor
{
    /// <summary>
    /// Ensures that GONetProjectSettings asset is created automatically when needed.
    /// Also handles Fast Iteration Mode initialization (generating code on editor startup).
    /// </summary>
    [InitializeOnLoad]
    public static class GONetProjectSettingsInitializer
    {
        /// <summary>
        /// SessionState key to track if we've already done the fast iteration startup generation this session.
        /// Prevents redundant regeneration on domain reloads within the same editor session.
        /// </summary>
        private const string SESSION_KEY_FAST_ITERATION_STARTUP_DONE = "GONet.FastIterationStartupDone";

        static GONetProjectSettingsInitializer()
        {
            EditorApplication.delayCall += EnsureProjectSettingsExist;
            EditorApplication.quitting += OnEditorQuitting;
        }

        private static void EnsureProjectSettingsExist()
        {
            // Only create if it doesn't exist
            if (GONetProjectSettings.Instance == null)
            {
                Debug.Log("GONet: Creating default project settings...");
                GONetProjectSettings.CreateProjectSettingsAsset();
            }

            EnsureRunInBackgroundSetting();
            EnsureFastIterationModeStartup();
        }

        private static void EnsureRunInBackgroundSetting()
        {
            var settings = GONetProjectSettings.Instance;
            if (settings == null || !settings.forcePlayerSettingsRunInBackground)
            {
                return;
            }

            try
            {
                if (!PlayerSettings.runInBackground)
                {
                    PlayerSettings.runInBackground = true;
                    GONet.GONetLog.Info("[GONet] Forced PlayerSettings.runInBackground=true (GONet Project Settings).");
                }
            }
            catch (System.Exception ex)
            {
                GONet.GONetLog.Warning($"[GONet] Failed to force PlayerSettings.runInBackground: {ex.Message}");
            }
        }

        /// <summary>
        /// When Fast Iteration Mode is enabled, generates runtime code on editor startup.
        /// This replaces the normal "generate on play mode entry" behavior.
        /// </summary>
        private static void EnsureFastIterationModeStartup()
        {
            if (!GONetProjectSettings.IsFastIterationModeEnabled)
            {
                return;
            }

            // Only do startup generation once per editor session (survives domain reloads)
            if (SessionState.GetBool(SESSION_KEY_FAST_ITERATION_STARTUP_DONE, false))
            {
                GONet.GONetLog.Debug("[GONet Fast Iteration] Startup generation already done this session, skipping.");
                return;
            }

            GONet.GONetLog.Warning(
                "[GONet] 🧪⚠️ EXPERIMENTAL FAST ITERATION MODE ENABLED ⚠️🧪\n" +
                "Generating runtime code on editor startup instead of play mode entry.\n" +
                "Remember: If you modify GONetAutoMagicalSync fields, DISABLE this mode or use 'Fix GONet Generated Code'.\n" +
                "This is an EXPERIMENTAL feature - behavior may change in future versions.");

            try
            {
                // First, update unique snaps to ensure everything is current
                // NOTE: We use default shouldRefreshAssetDatabase=true to ensure enum files
                // are compiled before GenerateFiles() runs. Passing false caused regression
                // from 61s to 94s because the removed refresh in GenerateSyncEventEnum()
                // was needed for proper compilation ordering.
                //
                // PERFORMANCE OPTIMIZATION: We pass shouldSkipInitialRefresh=true because:
                // 1. Unity already imported all assets during editor startup
                // 2. The ForceUpdate refresh at the START of UpdateAllUniqueSnaps is the MOST
                //    expensive operation (~30-40+ seconds), forcing full reimport of ALL assets
                // 3. The refresh at the END still happens to recognize newly generated code
                // 4. Fast Iteration Mode only runs on editor startup after a clean quit
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.UpdateAllUniqueSnaps(
                    shouldBypassChangePlaymodeCheck: false,
                    shouldRefreshAssetDatabase: true,
                    shouldSkipInitialRefresh: true);

                // Then generate the runtime files
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GenerateFiles();

                // Mark startup generation as complete for this session
                SessionState.SetBool(SESSION_KEY_FAST_ITERATION_STARTUP_DONE, true);

                GONet.GONetLog.Info("[GONet Fast Iteration] Runtime code generated successfully on editor startup.");
            }
            catch (System.Exception ex)
            {
                GONet.GONetLog.Error($"[GONet Fast Iteration] Failed to generate code on startup: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// When Fast Iteration Mode is enabled, deletes runtime code when the editor quits.
        /// This ensures a clean slate for the next session.
        /// </summary>
        private static void OnEditorQuitting()
        {
            if (!GONetProjectSettings.IsFastIterationModeEnabled)
            {
                return;
            }

            GONet.GONetLog.Info("[GONet Fast Iteration] Editor quitting - deleting generated runtime code for clean slate on next startup.");

            try
            {
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.DeleteGeneratedFiles();
            }
            catch (System.Exception ex)
            {
                GONet.GONetLog.Warning($"[GONet Fast Iteration] Failed to delete generated files on quit: {ex.Message}");
            }
        }

        /// <summary>
        /// Forces regeneration of runtime code. Called when enabling Fast Iteration Mode
        /// to ensure the generated code is fresh and up-to-date.
        /// </summary>
        internal static void ForceRegenerateForFastIterationMode()
        {
            GONet.GONetLog.Info("[GONet Fast Iteration] Forcing regeneration of runtime code...");

            try
            {
                // Clear the session flag so we regenerate
                SessionState.SetBool(SESSION_KEY_FAST_ITERATION_STARTUP_DONE, false);

                // Update snaps and regenerate
                // PERFORMANCE OPTIMIZATION: Skip the expensive initial ForceUpdate refresh.
                // The user is explicitly triggering regeneration, and the asset database should
                // already be current. The END refresh will still happen.
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.UpdateAllUniqueSnaps(
                    shouldBypassChangePlaymodeCheck: false,
                    shouldRefreshAssetDatabase: true,
                    shouldSkipInitialRefresh: true);
                GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GenerateFiles();

                // Mark as done
                SessionState.SetBool(SESSION_KEY_FAST_ITERATION_STARTUP_DONE, true);

                GONet.GONetLog.Info("[GONet Fast Iteration] Runtime code regenerated successfully.");
            }
            catch (System.Exception ex)
            {
                GONet.GONetLog.Error($"[GONet Fast Iteration] Failed to regenerate code: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Clears the fast iteration startup flag, allowing regeneration on next startup.
        /// Called when disabling Fast Iteration Mode to ensure normal behavior resumes.
        /// </summary>
        internal static void ClearFastIterationStartupFlag()
        {
            SessionState.SetBool(SESSION_KEY_FAST_ITERATION_STARTUP_DONE, false);
        }
    }
}
