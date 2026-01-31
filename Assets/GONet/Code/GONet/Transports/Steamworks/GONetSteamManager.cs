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

using Steamworks;
using System;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace GONet
{
    /// <summary>
    /// Manages Steamworks API initialization and shutdown for GONet.
    ///
    /// <para><b>⚡ HIGH-LOAD OPTIMIZATIONS (December 2025):</b></para>
    /// <list type="bullet">
    ///   <item><description><b>PlayerLoop Injection</b>: SteamAPI.RunCallbacks() runs BEFORE Update via Unity's PlayerLoop system</description></item>
    ///   <item><description><b>Multi-Point Polling</b>: Callbacks processed in Update, FixedUpdate, and LateUpdate</description></item>
    ///   <item><description><b>Manual API</b>: <see cref="ProcessNetworkEvents"/> for user-controlled polling during heavy loops</description></item>
    /// </list>
    ///
    /// <para><b>⚠️ IMPORTANT - MAIN THREAD LIMITATION:</b></para>
    /// <para>
    /// Steamworks is main-thread bound. During blocking operations (scene loads, heavy instantiation),
    /// Steam's internal buffers back up regardless of callback frequency. For optimal performance:
    /// </para>
    /// <list type="number">
    ///   <item><description>Use async scene loading (<c>SceneManager.LoadSceneAsync</c>)</description></item>
    ///   <item><description>Time-slice heavy instantiation (spread across frames)</description></item>
    ///   <item><description>Call <see cref="ProcessNetworkEvents"/> inside custom loading loops</description></item>
    ///   <item><description>Consider NetcodeIO transport for latency-critical scenarios with high object counts</description></item>
    /// </list>
    ///
    /// <para><b>REQUIREMENTS:</b></para>
    /// <list type="bullet">
    ///   <item><description>Steam client must be running</description></item>
    ///   <item><description>steam_appid.txt in project root with GONet App ID 4168160</description></item>
    /// </list>
    ///
    /// <para><b>EXECUTION ORDER:</b></para>
    /// <list type="bullet">
    ///   <item><description>PlayerLoop: EarlyUpdate.SteamCallbacks (BEFORE all other systems)</description></item>
    ///   <item><description>MonoBehaviour: -31000 (after GONetGlobal at -32000)</description></item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(-31000)]
    public class GONetSteamManager : MonoBehaviour
    {
        private static GONetSteamManager instance;
        private static bool isSteamInitialized = false;
        private static bool isRelayNetworkAccessRequested = false;
        private static bool isPlayerLoopInjected = false;

        // Throttle logging to prevent spam during heavy polling
        private static int callbackCallCount = 0;
        private static float lastCallbackLogTime = 0f;

        private static bool IsSteamApiContextValid()
        {
            try
            {
                // Steamworks.NET does not expose an "IsInitialized" flag, but an initialized context has valid user/pipe handles.
                // NOTE: IsSteamRunning() only indicates the Steam client is running, NOT that SteamAPI.Init() succeeded.
                return SteamAPI.GetHSteamPipe().m_HSteamPipe != 0 && SteamAPI.GetHSteamUser().m_HSteamUser != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// True if SteamAPI.Init() succeeded and Steam is ready to use.
        /// </summary>
        public static bool IsInitialized => IsSteamApiContextValid();

        public static bool HasInstance => instance != null;

        /// <summary>
        /// <para><b>🔥 CRITICAL API FOR HIGH-LOAD SCENARIOS</b></para>
        /// <para>
        /// Process Steam network events immediately. Call this during heavy operations
        /// to prevent RTT inflation and message queue backup.
        /// </para>
        ///
        /// <para><b>WHEN TO USE:</b></para>
        /// <list type="bullet">
        ///   <item><description>Inside async scene loading loops (<c>while (!asyncOp.isDone)</c>)</description></item>
        ///   <item><description>During time-sliced object instantiation</description></item>
        ///   <item><description>Any loop that blocks the main thread for &gt;50ms</description></item>
        /// </list>
        ///
        /// <para><b>EXAMPLE - Async Scene Loading:</b></para>
        /// <code>
        /// IEnumerator LoadLevel() {
        ///     // Use GONetSceneManager for networked scene loading
        ///     var asyncOp = GONetSceneManager.LoadSceneAsync("Game");
        ///     while (!asyncOp.isDone) {
        ///         GONetSteamManager.ProcessNetworkEvents(); // Keep Steam alive!
        ///         yield return null;
        ///     }
        /// }
        /// </code>
        ///
        /// <para><b>EXAMPLE - Time-Sliced Instantiation:</b></para>
        /// <code>
        /// IEnumerator SpawnObjects(List&lt;GameObject&gt; prefabs) {
        ///     // Use GONet's high-resolution time for accurate frame budgeting
        ///     long startTicks = GONetMain.Time.ElapsedTicks;
        ///     long frameBudgetTicks = 8 * TimeSpan.TicksPerMillisecond; // 8ms
        ///     foreach (var prefab in prefabs) {
        ///         Instantiate(prefab);
        ///         // Yield every 8ms to keep main thread responsive
        ///         if (GONetMain.Time.ElapsedTicks - startTicks > frameBudgetTicks) {
        ///             GONetSteamManager.ProcessNetworkEvents();
        ///             yield return null;
        ///             startTicks = GONetMain.Time.ElapsedTicks;
        ///         }
        ///     }
        /// }
        /// </code>
        ///
        /// <para><b>THREAD SAFETY:</b> Must be called from main thread only.</para>
        /// </summary>
        public static void ProcessNetworkEvents()
        {
            if (!IsInitialized)
            {
                return;
            }

            try
            {
                SteamAPI.RunCallbacks();
                callbackCallCount++;
            }
            catch (Exception ex)
            {
                GONetLog.Error($"[GONetSteamManager] ProcessNetworkEvents exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures a <see cref="GONetSteamManager"/> exists so Steam callbacks are processed via Update().
        /// Safe to call multiple times.
        /// </summary>
        public static GONetSteamManager EnsureInstanceExists()
        {
            if (instance != null)
            {
                return instance;
            }

            GONetSteamManager existing = UnityEngine.Object.FindObjectOfType<GONetSteamManager>();
            if (existing != null)
            {
                instance = existing;
                return existing;
            }

            GameObject go = new GameObject("GONetSteamManager");
            GONetSteamManager created = go.AddComponent<GONetSteamManager>();

            if (Application.isPlaying)
            {
                DontDestroyOnLoad(go);
            }

            return created;
        }

        /// <summary>
        /// App ID used for Steam initialization.
        /// GONet production App ID: 4168160
        /// </summary>
        [Tooltip("Steam App ID for GONet (4168160).")]
        public uint steamAppId = 4168160; // GONet production App ID

        [Tooltip("If true, creates steam_appid.txt automatically with configured App ID.")]
        public bool autoCreateAppIdFile = true;

        [Header("High-Load Optimizations")]
        [Tooltip("Inject SteamAPI.RunCallbacks into Unity's PlayerLoop to run BEFORE Update.\n\n" +
                "This ensures Steam messages are processed as early as possible each frame,\n" +
                "reducing RTT inflation during low-FPS scenarios.\n\n" +
                "Default: TRUE (recommended)")]
        public bool usePlayerLoopInjection = true;

        private void Awake()
        {
            // Singleton pattern
            if (instance != null && instance != this)
            {
                GONetLog.Warning("[GONetSteamManager] Duplicate GONetSteamManager detected, destroying duplicate component.");
                Destroy(this);
                return;
            }
            instance = this;

            if (Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }

            // Create steam_appid.txt if requested
            if (autoCreateAppIdFile)
            {
                CreateSteamAppIdFile();
            }

            // Initialize Steamworks API
            InitializeSteam();

            // Inject into PlayerLoop for earliest possible callback processing
            if (usePlayerLoopInjection)
            {
                InjectIntoPlayerLoop();
            }
        }

        private void CreateSteamAppIdFile()
        {
            try
            {
                string appIdFilePath = System.IO.Path.Combine(Application.dataPath, "..", "steam_appid.txt");

                // Only create if it doesn't exist
                if (!System.IO.File.Exists(appIdFilePath))
                {
                    System.IO.File.WriteAllText(appIdFilePath, steamAppId.ToString());
                    GONetLog.Info($"[GONetSteamManager] Created steam_appid.txt with App ID: {steamAppId}");
                }
                else
                {
                    string existingAppId = System.IO.File.ReadAllText(appIdFilePath).Trim();
                    if (existingAppId != steamAppId.ToString())
                    {
                        GONetLog.Warning($"[GONetSteamManager] steam_appid.txt exists with different App ID ({existingAppId}), keeping existing file.");
                    }
                }
            }
            catch (System.Exception ex)
            {
                GONetLog.Error($"[GONetSteamManager] Failed to create steam_appid.txt: {ex.Message}");
            }
        }

        private void InitializeSteam()
        {
            if (isSteamInitialized)
            {
                if (IsSteamApiContextValid())
                {
                    GONetLog.Debug("[GONetSteamManager] Steam already initialized, skipping.");
                    EnsureRelayNetworkAccessRequested();
                    return;
                }

                // Fast play mode / domain-reload disabled can leave stale static flags between sessions.
                // If our flag says initialized but the Steam context is invalid, force a re-init attempt.
                GONetLog.Warning("[GONetSteamManager] Steam init flag set but Steam context is invalid. Re-initializing Steamworks.");
                isSteamInitialized = false;
                isRelayNetworkAccessRequested = false;
            }

            try
            {
                // If SteamAPI.Init() was already called elsewhere (e.g., early init in lobby), adopt that state.
                if (IsSteamApiContextValid())
                {
                    isSteamInitialized = true;
                    GONetLog.Info($"[GONetSteamManager] Steamworks API already initialized (App ID: {SteamUtils.GetAppID()})");
                    GONetLog.Info($"[GONetSteamManager] Steam User: {SteamFriends.GetPersonaName()} (SteamID: {SteamUser.GetSteamID()})");
                    EnsureRelayNetworkAccessRequested();
                    return;
                }

                // Initialize Steamworks API
                bool success = SteamAPI.Init();

                if (success)
                {
                    isSteamInitialized = true;
                    GONetLog.Info($"[GONetSteamManager] Steamworks API initialized successfully (App ID: {SteamUtils.GetAppID()})");
                    GONetLog.Info($"[GONetSteamManager] Steam User: {SteamFriends.GetPersonaName()} (SteamID: {SteamUser.GetSteamID()})");

                    EnsureRelayNetworkAccessRequested();

                    // CRITICAL: Initialize Steam→GONet clock synchronization for accurate RTT
                    // This allows us to use Steam's m_usecTimeReceived for accurate network timing
                    SteamTimeSync.Initialize();
                }
                else
                {
                    GONetLog.Error("[GONetSteamManager] SteamAPI.Init() failed. Ensure Steam client is running and steam_appid.txt exists.");
                    GONetLog.Error("[GONetSteamManager] GONet uses App ID 4168160. Verify steam_appid.txt contains this App ID.");
                }
            }
            catch (System.Exception ex)
            {
                GONetLog.Error($"[GONetSteamManager] Exception during Steam initialization: {ex.Message}");
            }
        }

        private static void EnsureRelayNetworkAccessRequested()
        {
            if (isRelayNetworkAccessRequested)
            {
                return;
            }

            try
            {
                // CRITICAL: Kick off Steam Datagram Relay (SDR) initialization early.
                // This must be called BEFORE CreateListenSocketP2P or ConnectP2P for P2P to work reliably.
                // SDR takes a few seconds to fetch relay network config from Steam backend.
                SteamNetworkingUtils.InitRelayNetworkAccess();
                isRelayNetworkAccessRequested = true;
                GONetLog.Info("[GONetSteamManager] Initiated Steam Datagram Relay (SDR) network access request.");
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"[GONetSteamManager] Failed to initiate SDR network access request: {ex.Message}");
            }
        }

        #region PlayerLoop Injection

        /// <summary>
        /// Custom PlayerLoop update system that runs BEFORE Unity's Update.
        /// This ensures Steam callbacks are processed as early as possible each frame.
        /// </summary>
        private struct GONetSteamEarlyUpdate { }

        private static void InjectIntoPlayerLoop()
        {
            if (isPlayerLoopInjected)
            {
                return;
            }

            try
            {
                PlayerLoopSystem currentLoop = PlayerLoop.GetCurrentPlayerLoop();
                PlayerLoopSystem modifiedLoop = InsertSteamCallbackSystem(currentLoop);
                PlayerLoop.SetPlayerLoop(modifiedLoop);
                isPlayerLoopInjected = true;
                GONetLog.Info("[GONetSteamManager] Injected SteamAPI.RunCallbacks into PlayerLoop (EarlyUpdate phase)");
            }
            catch (Exception ex)
            {
                GONetLog.Warning($"[GONetSteamManager] PlayerLoop injection failed: {ex.Message}. Falling back to Update() polling.");
            }
        }

        private static PlayerLoopSystem InsertSteamCallbackSystem(PlayerLoopSystem loop)
        {
            // Create our custom update system
            PlayerLoopSystem steamCallbackSystem = new PlayerLoopSystem
            {
                type = typeof(GONetSteamEarlyUpdate),
                updateDelegate = OnPlayerLoopSteamCallbacks
            };

            // Find the EarlyUpdate subsystem and insert our callback at the beginning
            var subsystems = loop.subSystemList;
            if (subsystems == null)
            {
                return loop;
            }

            for (int i = 0; i < subsystems.Length; i++)
            {
                if (subsystems[i].type == typeof(EarlyUpdate))
                {
                    // Insert at the beginning of EarlyUpdate
                    var earlyUpdateSubsystems = subsystems[i].subSystemList;
                    if (earlyUpdateSubsystems != null)
                    {
                        var newSubsystems = new PlayerLoopSystem[earlyUpdateSubsystems.Length + 1];
                        newSubsystems[0] = steamCallbackSystem;
                        Array.Copy(earlyUpdateSubsystems, 0, newSubsystems, 1, earlyUpdateSubsystems.Length);
                        subsystems[i].subSystemList = newSubsystems;
                    }
                    else
                    {
                        subsystems[i].subSystemList = new PlayerLoopSystem[] { steamCallbackSystem };
                    }
                    break;
                }
            }

            loop.subSystemList = subsystems;
            return loop;
        }

        private static void OnPlayerLoopSteamCallbacks()
        {
            // EARLIEST POSSIBLE CALLBACK PROCESSING
            // Runs before all MonoBehaviour.Update() methods
            ProcessNetworkEvents();
        }

        private static void RemoveFromPlayerLoop()
        {
            if (!isPlayerLoopInjected)
            {
                return;
            }

            try
            {
                PlayerLoopSystem currentLoop = PlayerLoop.GetCurrentPlayerLoop();
                PlayerLoopSystem cleanedLoop = RemoveSteamCallbackSystem(currentLoop);
                PlayerLoop.SetPlayerLoop(cleanedLoop);
                isPlayerLoopInjected = false;
                GONetLog.Info("[GONetSteamManager] Removed SteamAPI.RunCallbacks from PlayerLoop");
            }
            catch (Exception ex)
            {
                GONetLog.Warning($"[GONetSteamManager] PlayerLoop cleanup failed: {ex.Message}");
            }
        }

        private static PlayerLoopSystem RemoveSteamCallbackSystem(PlayerLoopSystem loop)
        {
            var subsystems = loop.subSystemList;
            if (subsystems == null)
            {
                return loop;
            }

            for (int i = 0; i < subsystems.Length; i++)
            {
                if (subsystems[i].type == typeof(EarlyUpdate))
                {
                    var earlyUpdateSubsystems = subsystems[i].subSystemList;
                    if (earlyUpdateSubsystems != null)
                    {
                        // Remove our custom system
                        var filteredSubsystems = Array.FindAll(earlyUpdateSubsystems,
                            s => s.type != typeof(GONetSteamEarlyUpdate));
                        subsystems[i].subSystemList = filteredSubsystems;
                    }
                    break;
                }
            }

            loop.subSystemList = subsystems;
            return loop;
        }

        #endregion

        #region MonoBehaviour Callbacks (Redundant Polling)

        // These provide additional callback opportunities beyond PlayerLoop injection.
        // While PlayerLoop runs earliest, these catch any messages that arrive mid-frame.

        private void Update()
        {
            // Standard per-frame callback processing
            // Redundant with PlayerLoop but ensures callbacks are processed
            // even if PlayerLoop injection failed
            ProcessNetworkEvents();
        }

        private void FixedUpdate()
        {
            // HIGH-LOAD FIX: Process callbacks in FixedUpdate for consistent timing.
            // During low FPS (e.g., 20 FPS), Update runs infrequently but FixedUpdate
            // still fires at 50Hz. This catches callbacks between Update frames.
            //
            // NOTE: FixedUpdate does NOT run during blocking operations (scene load).
            // After a 200ms block, Unity runs FixedUpdate 10x in a tight loop to "catch up"
            // AFTER the block ends. This doesn't prevent the RTT spike, but it does
            // help process the backed-up messages quickly once the block ends.
            ProcessNetworkEvents();
        }

        private void LateUpdate()
        {
            // Process callbacks at end of frame.
            // Catches any messages that arrived during Update processing.
            ProcessNetworkEvents();
        }

        #endregion

        #region Cleanup

        private void OnApplicationQuit()
        {
            RemoveFromPlayerLoop();

            if (isSteamInitialized)
            {
                GONetLog.Info("[GONetSteamManager] Shutting down Steamworks API.");
                SteamAPI.Shutdown();
                isSteamInitialized = false;
                isRelayNetworkAccessRequested = false;
            }
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                RemoveFromPlayerLoop();

                if (isSteamInitialized)
                {
                    // Unity Editor exiting Play Mode does not always invoke OnApplicationQuit().
                    // Ensure we shutdown Steamworks cleanly so the next play session can re-init reliably.
                    GONetLog.Info("[GONetSteamManager] Shutting down Steamworks API (OnDestroy).");
                    SteamAPI.Shutdown();
                    isSteamInitialized = false;
                    isRelayNetworkAccessRequested = false;
                }

                instance = null;
            }
        }

        #endregion
    }
}
