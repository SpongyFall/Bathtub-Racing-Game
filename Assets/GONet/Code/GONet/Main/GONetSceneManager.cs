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
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using GONet.Utils;
using System;



#if ADDRESSABLES_AVAILABLE
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
#endif

namespace GONet
{
    /// <summary>
    /// Manages networked scene loading and unloading for GONet.
    /// Server-authoritative: only server can initiate scene changes.
    /// Clients receive scene change events and load accordingly.
    /// Access via GONetMain.SceneManager or from GONetBehaviour.SceneManager.
    /// </summary>
    public class GONetSceneManager
    {
        private readonly GONetGlobal global;
        private readonly HashSet<string> loadedAdditiveScenes = new HashSet<string>();

        // State tracking
        private readonly Dictionary<string, AsyncOperation> scenesLoading = new Dictionary<string, AsyncOperation>();
        private readonly HashSet<string> scenesUnloading = new HashSet<string>();
        private readonly HashSet<string> scenesLoadedHistory = new HashSet<string>();

#if ADDRESSABLES_AVAILABLE
        private readonly Dictionary<string, AsyncOperationHandle<SceneInstance>> addressableSceneHandles =
            new Dictionary<string, AsyncOperationHandle<SceneInstance>>();
#endif

        // ========================================
        // EXTENSIBILITY HOOKS
        // ========================================

        /// <summary>
        /// Validation hook - return false to deny scene load request.
        /// Parameters: (sceneName, mode, requestingAuthority) → bool allowed
        /// </summary>
        public delegate bool SceneLoadValidationDelegate(string sceneName, LoadSceneMode mode, ushort requestingAuthority);

        /// <summary>
        /// Server-side validation for scene load requests.
        /// Subscribe to this event to add custom validation logic.
        /// </summary>
        public event SceneLoadValidationDelegate OnValidateSceneLoad;

        /// <summary>
        /// Set to true if scene load requests require async approval (e.g., server UI confirmation).
        /// When true, validation will pass but signal ExpectFollowOnResponse, and the server
        /// must send an explicit response RPC after approval/denial.
        /// </summary>
        public bool RequiresAsyncApproval { get; set; } = false;

        /// <summary>
        /// Internal method to invoke validation. Used by RPC validation in GONetGlobal.
        /// Returns true if validation passes or no validators are registered.
        /// </summary>
        internal bool InvokeValidation(string sceneName, LoadSceneMode mode, ushort requestingAuthority)
        {
            if (OnValidateSceneLoad == null)
                return true;

            return OnValidateSceneLoad(sceneName, mode, requestingAuthority);
        }

        /// <summary>
        /// Called when scene load begins (after validation, before actual load)
        /// </summary>
        public delegate void SceneLoadDelegate(string sceneName, LoadSceneMode mode);

        /// <summary>
        /// Invoked when scene loading starts on this machine
        /// </summary>
        public event SceneLoadDelegate OnSceneLoadStarted;

        /// <summary>
        /// Invoked when scene loading completes on this machine
        /// </summary>
        public event SceneLoadDelegate OnSceneLoadCompleted;

        /// <summary>
        /// Extensibility hook for custom late-joiner scene synchronization logic.
        /// Called when client receives a scene load event before processing it.
        /// Return false to skip the scene load (e.g., if already in the target scene).
        /// Parameters: (sceneName, mode) → bool shouldLoad
        /// </summary>
        public delegate bool ShouldProcessSceneLoadDelegate(string sceneName, LoadSceneMode mode);

        /// <summary>
        /// Client-side hook to customize scene load processing for late-joiners.
        /// Use this to implement custom logic such as:
        /// - Skipping scene load if already in target scene
        /// - Filtering out stale scene changes
        /// - Custom scene transition validation
        /// Return true to proceed with scene load, false to skip.
        /// </summary>
        public event ShouldProcessSceneLoadDelegate OnShouldProcessSceneLoad;

        /// <summary>
        /// Called when scene unload begins
        /// </summary>
        public event SceneLoadDelegate OnSceneUnloadStarted;

        /// <summary>
        /// Called when async scene request response is received (approval/denial).
        /// Parameters: (approved, sceneName, denialReason)
        /// </summary>
        public delegate void SceneRequestResponseDelegate(bool approved, string sceneName, string denialReason);

        /// <summary>
        /// Invoked on client when server sends response to scene load request
        /// </summary>
        public event SceneRequestResponseDelegate OnSceneRequestResponse;

        /// <summary>
        /// Internal method to invoke scene request response event.
        /// Called by GONetGlobal when RPC_SceneRequestResponse is received.
        /// </summary>
        internal void InvokeSceneRequestResponse(bool approved, string sceneName, string denialReason)
        {
            OnSceneRequestResponse?.Invoke(approved, sceneName, denialReason);
        }

        // ========================================
        // SCENE STATE PROPERTIES
        // ========================================

        /// <summary>
        /// Gets the currently active scene. Wraps Unity's SceneManager.GetActiveScene()
        /// to centralize all scene access through GONetSceneManager.
        /// </summary>
        public Scene ActiveScene => SceneManager.GetActiveScene();

        /// <summary>
        /// Gets the name of the currently active scene, or null if no valid scene.
        /// </summary>
        public string ActiveSceneName
        {
            get
            {
                Scene active = SceneManager.GetActiveScene();
                return active.IsValid() ? active.name : null;
            }
        }

        /// <summary>
        /// Returns true if client is currently loading any scene (async operation in progress).
        /// Used to defer network message processing until scene is ready.
        /// </summary>
        public bool IsCurrentlyLoadingScene => scenesLoading.Count > 0;

        /// <summary>
        /// Name of the scene currently being loaded (first one if multiple), or null if not loading.
        /// </summary>
        public string CurrentlyLoadingSceneName
        {
            get
            {
                if (scenesLoading.Count == 0) return null;
                foreach (var kvp in scenesLoading)
                    return kvp.Key; // Return first one
                return null;
            }
        }

        // ========================================
        // INITIALIZATION
        // ========================================

        // ========================================
        // COLLECTION GROWTH MONITORING (Automatic)
        // ========================================

        private static bool enableAutomaticCollectionMonitoring = true;
        private static GONetMain.CollectionGrowthSnapshot? beforeSceneLoadSnapshot = null;
        private static string monitoredSceneName = null;
        private static float monitoringDelaySeconds = 60f;
        private static System.Collections.IEnumerator delayedMonitoringCoroutine = null;

        /// <summary>
        /// Enable/disable automatic collection growth monitoring during scene loads.
        /// When enabled, GONet will automatically capture snapshots before/after scene loads
        /// and log collection growth to diagnose performance issues.
        /// Default: enabled
        /// </summary>
        public static bool EnableAutomaticCollectionMonitoring
        {
            get { return enableAutomaticCollectionMonitoring; }
            set
            {
                enableAutomaticCollectionMonitoring = value;
                GONetLog.Info($"[GONetSceneManager] Automatic collection monitoring: {(value ? "ENABLED" : "DISABLED")}");
            }
        }

        /// <summary>
        /// Seconds to wait after scene load before capturing final snapshot.
        /// Default: 60 seconds (allows collections to settle)
        /// </summary>
        public static float MonitoringDelaySeconds
        {
            get { return monitoringDelaySeconds; }
            set { monitoringDelaySeconds = Mathf.Max(1f, value); }
        }

        private void StartAutomaticCollectionMonitoring(string sceneName, LoadSceneMode mode)
        {
            if (!enableAutomaticCollectionMonitoring)
                return;

            // Capture "before" snapshot
            beforeSceneLoadSnapshot = GONetMain.CaptureCollectionGrowthSnapshot($"Before Scene Load: {sceneName}");
            monitoredSceneName = sceneName;

            GONetLog.Info($"[COLLECTION-MONITOR] Automatic monitoring started for scene '{sceneName}' (Mode: {mode})");
            GONetMain.LogCollectionGrowthSnapshot(beforeSceneLoadSnapshot.Value);
        }

        private void CompleteAutomaticCollectionMonitoring(string sceneName, LoadSceneMode mode)
        {
            if (!enableAutomaticCollectionMonitoring || !beforeSceneLoadSnapshot.HasValue)
                return;

            // Capture "immediately after load" snapshot
            var afterLoadSnapshot = GONetMain.CaptureCollectionGrowthSnapshot($"After Scene Load: {sceneName}");
            GONetMain.LogCollectionGrowthSnapshot(afterLoadSnapshot);
            GONetMain.LogCollectionGrowthComparison(beforeSceneLoadSnapshot.Value, afterLoadSnapshot);

            // Start periodic monitoring coroutine (samples every 2s for first 30s, then final at 60s)
            if (global != null)
            {
                if (delayedMonitoringCoroutine != null)
                {
                    global.StopCoroutine(delayedMonitoringCoroutine);
                }

                delayedMonitoringCoroutine = PeriodicMonitoringCoroutine(sceneName, afterLoadSnapshot);
                global.StartCoroutine(delayedMonitoringCoroutine);
            }
        }

        private System.Collections.IEnumerator PeriodicMonitoringCoroutine(string sceneName, GONetMain.CollectionGrowthSnapshot afterLoadSnapshot)
        {
            var previousSnapshot = afterLoadSnapshot;
            float elapsedTime = 0f;
            float sampleInterval = 2f; // Sample every 2 seconds
            float detailedMonitoringDuration = 30f; // Detailed monitoring for first 30 seconds

            GONetLog.Info($"[COLLECTION-MONITOR] Starting periodic monitoring (every {sampleInterval}s for {detailedMonitoringDuration}s, then final at {monitoringDelaySeconds}s)");

            // Periodic samples for first 30 seconds (during stabilization period)
            while (elapsedTime < detailedMonitoringDuration)
            {
                yield return new WaitForSeconds(sampleInterval);
                elapsedTime += sampleInterval;

                if (!enableAutomaticCollectionMonitoring)
                {
                    GONetLog.Info($"[COLLECTION-MONITOR] Monitoring cancelled (monitoring disabled)");
                    yield break;
                }

                // Capture snapshot with persistence queue breakdown
                var snapshot = GONetMain.CaptureCollectionGrowthSnapshot($"+{elapsedTime:F0}s: {sceneName}");
                GONetMain.LogCollectionGrowthSnapshot(snapshot);
                GONetMain.LogCollectionGrowthComparison(previousSnapshot, snapshot);

                // Log persistence queue details
                GONetMain.LogPersistenceQueueBreakdown();

                previousSnapshot = snapshot;
            }

            // Wait for final snapshot at 60 seconds
            float remainingTime = monitoringDelaySeconds - elapsedTime;
            if (remainingTime > 0)
            {
                yield return new WaitForSeconds(remainingTime);

                if (!enableAutomaticCollectionMonitoring)
                {
                    GONetLog.Info($"[COLLECTION-MONITOR] Final monitoring cancelled (monitoring disabled)");
                    yield break;
                }

                // Final snapshot at 60 seconds
                var finalSnapshot = GONetMain.CaptureCollectionGrowthSnapshot($"After {monitoringDelaySeconds}s: {sceneName}");
                GONetMain.LogCollectionGrowthSnapshot(finalSnapshot);
                GONetMain.LogCollectionGrowthComparison(previousSnapshot, finalSnapshot);
                GONetMain.LogPersistenceQueueBreakdown();
            }

            GONetLog.Info($"[COLLECTION-MONITOR] Automatic monitoring complete for scene '{sceneName}'");

            // Clear state
            beforeSceneLoadSnapshot = null;
            monitoredSceneName = null;
            delayedMonitoringCoroutine = null;
        }

        // ========================================
        // INITIALIZATION
        // ========================================

        internal GONetSceneManager(GONetGlobal global)
        {
            this.global = global;

            // Subscribe to scene events
            GONetMain.EventBus.Subscribe<SceneLoadEvent>(OnSceneLoadEvent);
            GONetMain.EventBus.Subscribe<SceneUnloadEvent>(OnSceneUnloadEvent);

            // Subscribe to Unity's scene loaded/unloaded events for state tracking
            SceneManager.sceneLoaded += OnUnitySceneLoaded;
            SceneManager.sceneUnloaded += OnUnitySceneUnloaded;

            // Hook into scene load events for automatic collection monitoring
            OnSceneLoadStarted += StartAutomaticCollectionMonitoring;
            OnSceneLoadCompleted += CompleteAutomaticCollectionMonitoring;

            GONetLog.Debug("[GONetSceneManager] Initialized (automatic collection monitoring enabled)");
        }

        // ========================================
        // PUBLIC API - Scene Loading
        // ========================================

        /// <summary>
        /// Loads a scene from Build Settings.
        /// SERVER ONLY - clients should not call this directly.
        /// </summary>
        /// <param name="sceneName">Name of the scene in Build Settings</param>
        /// <param name="mode">Single or Additive loading mode</param>
        public void LoadSceneFromBuildSettings(string sceneName, LoadSceneMode mode = LoadSceneMode.Single)
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[GONetSceneManager] Only server can load scenes. Use RequestLoadScene() from clients.");
                return;
            }

            LoadScene_Internal(sceneName, -1, SceneLoadType.BuildSettings, mode, true, 100, GONetMain.MyAuthorityId);
        }

        /// <summary>
        /// Loads a scene from Build Settings by build index.
        /// SERVER ONLY - clients should not call this directly.
        /// </summary>
        /// <param name="buildIndex">Build index of the scene</param>
        /// <param name="mode">Single or Additive loading mode</param>
        public void LoadSceneFromBuildSettings(int buildIndex, LoadSceneMode mode = LoadSceneMode.Single)
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[GONetSceneManager] Only server can load scenes. Use RequestLoadScene() from clients.");
                return;
            }

            string sceneName = GetSceneNameFromBuildIndex(buildIndex);
            if (string.IsNullOrEmpty(sceneName))
            {
                GONetLog.Error($"[GONetSceneManager] Invalid build index: {buildIndex}");
                return;
            }

            LoadScene_Internal(sceneName, buildIndex, SceneLoadType.BuildSettings, mode, true, 100, GONetMain.MyAuthorityId);
        }

        /// <summary>
        /// Unloads an additively loaded scene.
        /// SERVER ONLY - clients should not call this directly.
        /// </summary>
        /// <param name="sceneName">Name of the scene to unload</param>
        public void UnloadScene(string sceneName)
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[GONetSceneManager] Only server can unload scenes.");
                return;
            }

            if (!loadedAdditiveScenes.Contains(sceneName))
            {
                GONetLog.Warning($"[GONetSceneManager] Scene '{sceneName}' is not loaded additively, cannot unload");
                return;
            }

            UnloadScene_Internal(sceneName, -1, SceneLoadType.BuildSettings);
        }

#if ADDRESSABLES_AVAILABLE
        /// <summary>
        /// Loads a scene from Addressables system.
        /// SERVER ONLY - clients should not call this directly.
        /// </summary>
        /// <param name="sceneName">Addressable name/key of the scene</param>
        /// <param name="mode">Single or Additive loading mode</param>
        /// <param name="activateOnLoad">Whether to activate the scene immediately when loaded</param>
        /// <param name="priority">Loading priority (0-100, higher = higher priority)</param>
        public void LoadSceneFromAddressables(
            string sceneName,
            LoadSceneMode mode = LoadSceneMode.Single,
            bool activateOnLoad = true,
            int priority = 100)
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[GONetSceneManager] Only server can load scenes. Use RequestLoadScene() from clients.");
                return;
            }

            LoadScene_Internal(sceneName, -1, SceneLoadType.Addressables, mode, activateOnLoad, priority, GONetMain.MyAuthorityId);
        }

        /// <summary>
        /// Unloads an Addressables scene.
        /// SERVER ONLY - clients should not call this directly.
        /// </summary>
        /// <param name="sceneName">Name of the Addressables scene to unload</param>
        public void UnloadAddressablesScene(string sceneName)
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[GONetSceneManager] Only server can unload scenes.");
                return;
            }

            if (!loadedAdditiveScenes.Contains(sceneName))
            {
                GONetLog.Warning($"[GONetSceneManager] Scene '{sceneName}' is not loaded additively, cannot unload");
                return;
            }

            UnloadScene_Internal(sceneName, -1, SceneLoadType.Addressables);
        }
#endif

        // ========================================
        // CLIENT REQUEST APIS (Phase 5)
        // ========================================

        /// <summary>
        /// CLIENT ONLY: Request the server to load a scene from Build Settings.
        /// Server will validate the request via OnValidateSceneLoad hook before loading.
        /// </summary>
        /// <param name="sceneName">Name of the scene to request loading</param>
        /// <param name="mode">Loading mode (Single or Additive)</param>
        public void RequestLoadScene(string sceneName, LoadSceneMode mode = LoadSceneMode.Single)
        {
            if (GONetMain.IsServer)
            {
                GONetLog.Warning("[GONetSceneManager] Server should use LoadSceneFromBuildSettings() directly, not RequestLoadScene()");
                return;
            }

            if (!GONetMain.IsClient)
            {
                GONetLog.Warning("[GONetSceneManager] RequestLoadScene() can only be called by clients");
                return;
            }

            GONetLog.Info($"[GONetSceneManager] Client requesting scene load: '{sceneName}' (Mode: {mode})");

            // Send RPC to server via internal helper (which calls CallRpc)
            global.SendSceneLoadRequest(sceneName, (byte)mode, (byte)SceneLoadType.BuildSettings);
        }

#if ADDRESSABLES_AVAILABLE
        /// <summary>
        /// CLIENT ONLY: Request the server to load a scene from Addressables.
        /// Server will validate the request via OnValidateSceneLoad hook before loading.
        /// </summary>
        /// <param name="sceneName">Addressable key/name of the scene to request loading</param>
        /// <param name="mode">Loading mode (Single or Additive)</param>
        public void RequestLoadAddressablesScene(string sceneName, LoadSceneMode mode = LoadSceneMode.Single)
        {
            if (GONetMain.IsServer)
            {
                GONetLog.Warning("[GONetSceneManager] Server should use LoadSceneFromAddressables() directly, not RequestLoadAddressablesScene()");
                return;
            }

            if (!GONetMain.IsClient)
            {
                GONetLog.Warning("[GONetSceneManager] RequestLoadAddressablesScene() can only be called by clients");
                return;
            }

            GONetLog.Info($"[GONetSceneManager] Client requesting Addressables scene load: '{sceneName}' (Mode: {mode})");

            // Send RPC to server via internal helper (which calls CallRpc)
            global.SendSceneLoadRequest(sceneName, (byte)mode, (byte)SceneLoadType.Addressables);
        }
#endif

        /// <summary>
        /// CLIENT ONLY: Request the server to unload a scene.
        /// Server will validate the request before unloading.
        /// </summary>
        /// <param name="sceneName">Name of the scene to request unloading</param>
        public void RequestUnloadScene(string sceneName)
        {
            if (GONetMain.IsServer)
            {
                GONetLog.Warning("[GONetSceneManager] Server should use UnloadScene() directly, not RequestUnloadScene()");
                return;
            }

            if (!GONetMain.IsClient)
            {
                GONetLog.Warning("[GONetSceneManager] RequestUnloadScene() can only be called by clients");
                return;
            }

            GONetLog.Info($"[GONetSceneManager] Client requesting scene unload: '{sceneName}'");

            // Send RPC to server via internal helper (which calls CallRpc)
            global.SendSceneUnloadRequest(sceneName);
        }

        /// <summary>
        /// SERVER ONLY: Send scene request response to client (approval or denial).
        /// Used in async approval workflows where server shows UI confirmation dialog.
        /// </summary>
        /// <param name="clientId">Authority ID of the client to receive the response</param>
        /// <param name="approved">True if request was approved, false if denied</param>
        /// <param name="sceneName">Name of the scene that was requested</param>
        /// <param name="reason">Optional reason for denial (only used when approved=false)</param>
        public void SendSceneRequestResponse(ushort clientId, bool approved, string sceneName, string reason = "")
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[GONetSceneManager] SendSceneRequestResponse() can only be called by server");
                return;
            }

            GONetLog.Info($"[GONetSceneManager] Sending scene request response to client {clientId}: {(approved ? "APPROVED" : "DENIED")} - '{sceneName}'");

            // Call internal helper on GONetGlobal which invokes CallRpc
            global.SendSceneRequestResponse(clientId, approved, sceneName, reason);
        }

        // ========================================
        // INTERNAL IMPLEMENTATION
        // ========================================

        private void LoadScene_Internal(
            string sceneName,
            int buildIndex,
            SceneLoadType loadType,
            LoadSceneMode mode,
            bool activateOnLoad,
            int priority,
            ushort requestingAuthorityId = 0)
        {
            // Validation hook - allow subscribers to deny scene load
            if (OnValidateSceneLoad != null)
            {
                bool allowed = OnValidateSceneLoad(sceneName, mode, requestingAuthorityId);
                if (!allowed)
                {
                    GONetLog.Warning($"[GONetSceneManager] Scene load denied by validation: {sceneName} (requester: {requestingAuthorityId})");
                    return;
                }
            }

            // Verify scene exists in build settings (if loading from build settings)
            if (loadType == SceneLoadType.BuildSettings)
            {
                if (buildIndex >= 0)
                {
                    if (buildIndex >= SceneManager.sceneCountInBuildSettings)
                    {
                        GONetLog.Error($"[GONetSceneManager] Invalid build index {buildIndex} - only {SceneManager.sceneCountInBuildSettings} scenes in build settings");
                        return;
                    }
                }
                else if (!string.IsNullOrEmpty(sceneName))
                {
                    // Verify scene name exists in build settings
                    bool found = false;
                    for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
                    {
                        string path = SceneUtility.GetScenePathByBuildIndex(i);
                        if (!string.IsNullOrEmpty(path))
                        {
                            string name = System.IO.Path.GetFileNameWithoutExtension(path);
                            if (name == sceneName)
                            {
                                found = true;
                                buildIndex = i; // Store build index for event
                                break;
                            }
                        }
                    }

                    if (!found)
                    {
                        GONetLog.Error($"[GONetSceneManager] Scene '{sceneName}' not found in build settings");
                        return;
                    }
                }
            }

            // CRITICAL: If loading in Single mode, Unity will automatically unload all other scenes
            // We MUST publish SceneUnloadEvent for each currently loaded scene BEFORE publishing SceneLoadEvent
            // This ensures persistent events from old scenes get cancelled (spawns, value baselines, etc.)
            if (mode == LoadSceneMode.Single)
            {
                // Get all currently loaded scenes (before the new scene loads)
                List<string> scenesToUnload = new List<string>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && scene.name != sceneName)  // Don't unload the scene we're about to load
                    {
                        scenesToUnload.Add(scene.name);
                    }
                }

                // Publish SceneUnloadEvent for each scene that will be unloaded
                foreach (string sceneToUnload in scenesToUnload)
                {
                    GONetLog.Warning($"[GONetSceneManager] Single mode load - publishing SceneUnloadEvent for '{sceneToUnload}' (will be auto-unloaded by Unity)");

                    var unloadEvt = new SceneUnloadEvent
                    {
                        SceneName = sceneToUnload,
                        SceneBuildIndex = GetSceneBuildIndex(sceneToUnload),
                        LoadType = loadType,  // Use same load type as the new scene
                        OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
                    };

                    GONetMain.EventBus.Publish(unloadEvt);
                    GONetLog.Info($"[SCENE-PUBLISH] SceneUnloadEvent published for '{sceneToUnload}'");

                    // Server must notify local subscribers directly because OnSceneUnloadEvent
                    // returns early on the server (it only processes client-side unloads).
                    // Without this, systems like GONetPoolManager never learn about the unloading
                    // scene and cannot clean up pools for re-initialization on revisit.
                    OnSceneUnloadStarted?.Invoke(sceneToUnload, mode);
                }
            }

            // Publish persistent event for all clients (including late-joiners)
            var evt = new SceneLoadEvent
            {
                SceneName = sceneName,
                SceneBuildIndex = buildIndex,
                LoadType = loadType,
                Mode = mode,
                ActivateOnLoad = activateOnLoad,
                Priority = priority,
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };

            GONetMain.EventBus.Publish(evt);
            GONetLog.Info($"[SCENE-PUBLISH] SceneLoadEvent published for '{sceneName}' (Mode: {mode}, LoadType: {loadType})");

            // SCENE LOADING SUPPRESSION: Mark all connected clients as loading this scene
            // Unreliable sync messages will be dropped until clients send SceneLoadCompleteEvent
            if (GONetMain.IsServer && GONetMain.gonetServer != null)
            {
                double loadStartTime = GONetMain.Time.ElapsedSeconds;
                foreach (var remoteClient in GONetMain.gonetServer.remoteClients)
                {
                    remoteClient.IsCurrentlyLoadingScene = true;
                    remoteClient.CurrentlyLoadingSceneName = sceneName;
                    remoteClient.LoadingStartedTime = loadStartTime;
                    remoteClient.HasSentLoadingTimeoutFallback = false;
                }
                GONetLog.Info($"[SCENE-LOADING-START] Marked {GONetMain.gonetServer.remoteClients.Count} clients as loading scene '{sceneName}' (unreliable sync suppressed)");
            }

            // Notify subscribers
            OnSceneLoadStarted?.Invoke(sceneName, mode);

            // Load locally on server
            LoadSceneLocally(evt);

            // Track additive scenes
            if (mode == LoadSceneMode.Additive)
            {
                loadedAdditiveScenes.Add(sceneName);
            }

            GONetLog.Info($"[GONetSceneManager] Server loading scene: {sceneName} ({loadType}, {mode})");
        }

        private void UnloadScene_Internal(string sceneName, int buildIndex, SceneLoadType loadType)
        {
            // Publish persistent event
            var evt = new SceneUnloadEvent
            {
                SceneName = sceneName,
                SceneBuildIndex = buildIndex,
                LoadType = loadType,
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };

            GONetMain.EventBus.Publish(evt);

            // Notify subscribers
            OnSceneUnloadStarted?.Invoke(sceneName, LoadSceneMode.Additive);

            // Unload locally on server
            UnloadSceneLocally(evt);

            // Remove from tracking
            loadedAdditiveScenes.Remove(sceneName);

            GONetLog.Info($"[GONetSceneManager] Server unloading scene: {sceneName}");
        }

        // ========================================
        // EVENT HANDLERS
        // ========================================

        private void OnSceneLoadEvent(GONetEventEnvelope<SceneLoadEvent> eventEnvelope)
        {
            SceneLoadEvent evt = eventEnvelope.Event;

            // Server already loaded it when publishing the event
            if (GONetMain.IsServer)
                return;

            GONetLog.Info($"[GONetSceneManager] Client received scene load: {evt.SceneName} ({evt.LoadType}, {evt.Mode})");

            // Check if already in target scene (skip unnecessary reload)
            bool isAlreadyLoaded =
                (evt.Mode == LoadSceneMode.Single && SceneManager.GetActiveScene().name == evt.SceneName) ||
                (evt.Mode == LoadSceneMode.Additive && SceneManager.GetSceneByName(evt.SceneName).isLoaded);
            if (isAlreadyLoaded)
            {
                // PRE-LOADED SCENE FIX: Scene was already loaded before client connected.
                // Treat this as a completed load so late-joiner init can proceed.
                GONetLog.Info($"[GONetSceneManager] Already in scene '{evt.SceneName}' - treating as load complete");

                scenesLoadedHistory.Add(evt.SceneName);
                GONetMain.RecordSceneLoad(evt.SceneName);

                if (evt.Mode == LoadSceneMode.Additive)
                {
                    loadedAdditiveScenes.Add(evt.SceneName);
                }

                if (GONetMain.IsClient)
                {
                    bool wasAlreadyInitialized = GONetMain.GONetClient != null && GONetMain.GONetClient.IsInitializedWithServer;
                    if (wasAlreadyInitialized)
                    {
                        GONetLog.Info($"[TimeSync] Skipping AFTER scene load reset - already reset BEFORE load (client initialized): {evt.SceneName}");
                    }
                    else
                    {
                        GONetMain.RequestAggressiveTimeSync($"scene_load_complete_{evt.SceneName}");
                    }

                    if (!IsSceneAwaitingGONetIdSync(evt.SceneName))
                    {
                        SendSceneLoadCompleteWithRetry(evt.SceneName, evt.Mode);
                    }
                }

                OnSceneLoadCompleted?.Invoke(evt.SceneName, evt.Mode);
                return;
            }

            // Invoke extensibility hook for custom late-joiner logic
            if (OnShouldProcessSceneLoad != null)
            {
                bool shouldProcess = OnShouldProcessSceneLoad(evt.SceneName, evt.Mode);
                if (!shouldProcess)
                {
                    GONetLog.Info($"[GONetSceneManager] Custom validation skipped scene load: {evt.SceneName}");
                    return;
                }
            }

            bool isClientInitialized = GONetMain.IsClient && GONetMain.GONetClient != null && GONetMain.GONetClient.IsInitializedWithServer;

            // IMPORTANT: Skip time sync reset BEFORE scene load if client isn't initialized yet
            // During initial connection, interrupting time sync breaks value synchronization
            // But we WILL reset after the scene finishes loading (see OnSceneLoadOperationCompleted)
            if (isClientInitialized)
            {
                //GONetLog.Info($"[TimeSync] Requesting aggressive time sync BEFORE scene load (client already initialized): {evt.SceneName}");
                GONetMain.RequestAggressiveTimeSync($"scene_load_start_{evt.SceneName}");
            }
            else
            {
                GONetLog.Info($"[TimeSync] Skipping pre-load time sync reset - client not yet initialized (will reset after load completes)");
            }

            // Notify subscribers
            OnSceneLoadStarted?.Invoke(evt.SceneName, evt.Mode);

            // NOTE: Scene loading state is tracked via scenesLoading dictionary
            // GONetSceneManager.IsCurrentlyLoadingScene returns true while any scene is loading
            // This is checked in GONet.cs to defer AllValuesBundle processing during scene load
            GONetLog.Info($"[CLIENT-SCENE-LOADING] Client starting scene load '{evt.SceneName}' - scenesLoading.Count will be >0 (network message processing will defer)");

            // DIAGNOSTIC: Track scene load start for time sync analysis (commented out for production)
            /*
            GONetLog.Warning($"[TimeSync-DIAG] *** SCENE LOAD START *** Scene='{evt.SceneName}', Mode={evt.Mode}, " +
                          $"RawTime={GONetMain.Time.RawElapsedTicks / TimeSpan.TicksPerMillisecond}ms, " +
                          $"ClientInitialized={isClientInitialized}");
            */

            // Load scene locally on client
            LoadSceneLocally(evt);

            // Track additive scenes
            if (evt.Mode == LoadSceneMode.Additive)
            {
                loadedAdditiveScenes.Add(evt.SceneName);
            }
        }

        private void OnSceneUnloadEvent(GONetEventEnvelope<SceneUnloadEvent> eventEnvelope)
        {
            SceneUnloadEvent evt = eventEnvelope.Event;

            // DIAGNOSTIC: Log ALL scene unload events, even on server
            // Include instance hash code to detect if multiple SceneManager instances exist
            // Server already unloaded it when publishing the event
            if (GONetMain.IsServer)
                return;

            GONetLog.Info($"[GONetSceneManager] Client received scene unload: {evt.SceneName}");

            // Notify subscribers
            OnSceneUnloadStarted?.Invoke(evt.SceneName, LoadSceneMode.Additive);

            // Unload scene locally on client
            UnloadSceneLocally(evt);

            // Remove from tracking
            loadedAdditiveScenes.Remove(evt.SceneName);
        }

        // ========================================
        // SCENE LOADING/UNLOADING IMPLEMENTATION
        // ========================================

        private void LoadSceneLocally(SceneLoadEvent evt)
        {
            // Track loading state
            if (!scenesLoading.ContainsKey(evt.SceneName))
            {
                scenesLoading[evt.SceneName] = null; // Will be set to AsyncOperation below
            }

            if (evt.LoadType == SceneLoadType.BuildSettings)
            {
                AsyncOperation operation = null;

                try
                {
                    // Use build index if available, otherwise use name
                    if (evt.SceneBuildIndex >= 0)
                    {
                        operation = SceneManager.LoadSceneAsync(evt.SceneBuildIndex, evt.Mode);
                    }
                    else
                    {
                        operation = SceneManager.LoadSceneAsync(evt.SceneName, evt.Mode);
                    }

                    // Store the operation for progress tracking
                    if (operation != null)
                    {
                        scenesLoading[evt.SceneName] = operation;
                        operation.allowSceneActivation = evt.ActivateOnLoad;

                        // Add completion callback
                        operation.completed += (op) => OnSceneLoadOperationCompleted(evt.SceneName, evt.Mode, op);
                    }
                    else
                    {
                        GONetLog.Error($"[GONetSceneManager] LoadSceneAsync returned null for scene '{evt.SceneName}'");
                        scenesLoading.Remove(evt.SceneName);
                    }
                }
                catch (System.Exception ex)
                {
                    GONetLog.Error($"[GONetSceneManager] Exception loading scene '{evt.SceneName}': {ex.Message}");
                    scenesLoading.Remove(evt.SceneName);
                }
            }
#if ADDRESSABLES_AVAILABLE
            else if (evt.LoadType == SceneLoadType.Addressables)
            {
                LoadSceneFromAddressablesAsync(evt);
            }
#else
            else
            {
                GONetLog.Error("[GONetSceneManager] Addressables not available but scene requested Addressables loading!");
                scenesLoading.Remove(evt.SceneName);
            }
#endif
        }

        private void OnSceneLoadOperationCompleted(string sceneName, LoadSceneMode mode, AsyncOperation operation)
        {
            if (!operation.isDone)
            {
                GONetLog.Warning($"[GONetSceneManager] Scene load operation completed callback fired but isDone is false for '{sceneName}'");
            }

            // DIAGNOSTIC: Track scene load completion for time sync analysis (commented out for production)
            /*
            GONetLog.Warning($"[TimeSync-DIAG] *** SCENE LOAD COMPLETE *** Scene='{sceneName}', Mode={mode}, " +
                          $"RawTime={GONetMain.Time.RawElapsedTicks / TimeSpan.TicksPerMillisecond}ms");
            */
        }

        private void UnloadSceneLocally(SceneUnloadEvent evt)
        {
            // Track unloading state
            scenesUnloading.Add(evt.SceneName);

            if (evt.LoadType == SceneLoadType.BuildSettings)
            {
                Scene scene = SceneManager.GetSceneByName(evt.SceneName);
                if (scene.IsValid() && scene.isLoaded)
                {
                    try
                    {
                        AsyncOperation operation = SceneManager.UnloadSceneAsync(scene);
                        if (operation != null)
                        {
                            operation.completed += (op) => OnSceneUnloadOperationCompleted(evt.SceneName, op);
                        }
                        else
                        {
                            // UnloadSceneAsync can return null if the scene was already unloaded (e.g., by Unity in Single mode)
                            // This is expected behavior, not an error
                            GONetLog.Debug($"[GONetSceneManager] UnloadSceneAsync returned null for scene '{evt.SceneName}' - likely already unloaded by Unity");
                            scenesUnloading.Remove(evt.SceneName);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        GONetLog.Error($"[GONetSceneManager] Exception unloading scene '{evt.SceneName}': {ex.Message}");
                        scenesUnloading.Remove(evt.SceneName);
                    }
                }
                else
                {
                    // Scene not found or not loaded
                    // This is EXPECTED when LoadSceneMode.Single was used - Unity auto-unloads other scenes
                    // The SceneUnloadEvent is still published for persistent event tracking and cleanup
                    GONetLog.Debug($"[GONetSceneManager] Scene '{evt.SceneName}' already unloaded (expected for Single mode loads) - completing cleanup");
                    scenesUnloading.Remove(evt.SceneName);
                }
            }
#if ADDRESSABLES_AVAILABLE
            else if (evt.LoadType == SceneLoadType.Addressables)
            {
                UnloadSceneFromAddressablesAsync(evt.SceneName);
            }
#endif
        }

        private void OnSceneUnloadOperationCompleted(string sceneName, AsyncOperation operation)
        {
            if (!operation.isDone)
            {
                GONetLog.Warning($"[GONetSceneManager] Scene unload operation completed callback fired but isDone is false for '{sceneName}'");
            }

            GONetLog.Debug($"[GONetSceneManager] Scene unload operation completed: '{sceneName}'");
        }

        // ========================================
        // HELPER METHODS
        // ========================================

        private string GetSceneNameFromBuildIndex(int buildIndex)
        {
            if (buildIndex < 0 || buildIndex >= SceneManager.sceneCountInBuildSettings)
                return null;

            string scenePath = SceneUtility.GetScenePathByBuildIndex(buildIndex);
            return System.IO.Path.GetFileNameWithoutExtension(scenePath);
        }

        private int GetSceneBuildIndex(string sceneName)
        {
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(i);
                if (!string.IsNullOrEmpty(path))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (name == sceneName)
                    {
                        return i;
                    }
                }
            }
            return -1;  // Not found in build settings
        }

        /// <summary>
        /// Checks if a scene is currently loaded (additively or as main scene)
        /// </summary>
        public bool IsSceneLoaded(string sceneName)
        {
            Scene scene = SceneManager.GetSceneByName(sceneName);
            return scene.IsValid() && scene.isLoaded;
        }

        /// <summary>
        /// Gets a Unity Scene by name.
        /// Wrapper for UnityEngine.SceneManager.GetSceneByName for GONet API consistency.
        /// </summary>
        /// <param name="sceneName">The name of the scene to get</param>
        /// <returns>The Scene if found, or an invalid Scene if not found. Check scene.IsValid() and scene.isLoaded before using.</returns>
        public Scene GetSceneByName(string sceneName)
        {
            return SceneManager.GetSceneByName(sceneName);
        }

        // ========================================
        // STATE TRACKING (from SceneManagementUtils)
        // ========================================

        /// <summary>
        /// Returns true if any scene is currently loading
        /// </summary>
        public bool IsAnySceneLoading => scenesLoading.Count > 0;

        /// <summary>
        /// Returns true if the specified scene is currently loading
        /// </summary>
        public bool IsSceneLoading(string sceneName) => scenesLoading.ContainsKey(sceneName);

        /// <summary>
        /// Returns true if any scene is currently unloading
        /// </summary>
        public bool IsAnySceneUnloading => scenesUnloading.Count > 0;

        /// <summary>
        /// Returns true if the specified scene is currently unloading
        /// </summary>
        public bool IsSceneUnloading(string sceneName) => scenesUnloading.Contains(sceneName);

        /// <summary>
        /// Returns true if the scene was loaded at some point and is now unloaded
        /// </summary>
        public bool IsSceneUnloaded(string sceneName) =>
            scenesLoadedHistory.Contains(sceneName) && !IsSceneLoaded(sceneName);

        /// <summary>
        /// Gets all scenes that are currently in the process of loading
        /// </summary>
        public IEnumerable<string> GetLoadingScenes() => scenesLoading.Keys;

        /// <summary>
        /// Gets all scenes that are currently in the process of unloading
        /// </summary>
        public IEnumerable<string> GetUnloadingScenes() => scenesUnloading;

        /// <summary>
        /// Gets the loading progress (0-1) for a specific scene.
        /// Returns -1 if scene is not currently loading.
        /// </summary>
        public float GetSceneLoadingProgress(string sceneName)
        {
            if (scenesLoading.TryGetValue(sceneName, out AsyncOperation operation) && operation != null)
            {
                return operation.progress;
            }
            return -1f;
        }

        /// <summary>
        /// Gets statistics about scene loading state (for debugging)
        /// </summary>
        public string GetSceneLoadingStats()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== GONet Scene Manager Stats ===");
            sb.AppendLine($"Currently Loading: {scenesLoading.Count}");
            foreach (var scene in scenesLoading.Keys)
                sb.AppendLine($"  - {scene}");

            sb.AppendLine($"Currently Unloading: {scenesUnloading.Count}");
            foreach (var scene in scenesUnloading)
                sb.AppendLine($"  - {scene}");

            sb.AppendLine($"Loaded Additive Scenes: {loadedAdditiveScenes.Count}");
            foreach (var scene in loadedAdditiveScenes)
                sb.AppendLine($"  - {scene}");

            sb.AppendLine($"Scene Load History: {scenesLoadedHistory.Count} scenes");

#if ADDRESSABLES_AVAILABLE
            sb.AppendLine($"Addressable Scene Handles: {addressableSceneHandles.Count}");
            foreach (var kvp in addressableSceneHandles)
                sb.AppendLine($"  - {kvp.Key} (Valid: {kvp.Value.IsValid()})");
#endif

            return sb.ToString();
        }

        /// <summary>
        /// Gets the total number of currently loaded scenes.
        /// Wraps Unity's SceneManager.sceneCount to centralize all scene access.
        /// </summary>
        public int LoadedSceneCount => SceneManager.sceneCount;

        /// <summary>
        /// Gets the loaded scene at the given index.
        /// Wraps Unity's SceneManager.GetSceneAt to centralize all scene access.
        /// </summary>
        public Scene GetLoadedSceneAt(int index)
        {
            return SceneManager.GetSceneAt(index);
        }

        /// <summary>
        /// Moves a root-level GameObject into the specified scene.
        /// Wraps Unity's SceneManager.MoveGameObjectToScene to centralize all scene access.
        /// </summary>
        public void MoveGameObjectToScene(GameObject go, Scene scene)
        {
            SceneManager.MoveGameObjectToScene(go, scene);
        }

        // Unity scene event handlers for state tracking
        private void OnUnitySceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scenesLoading.ContainsKey(scene.name))
                scenesLoading.Remove(scene.name);

            scenesLoadedHistory.Add(scene.name);

            // Record scene load in GONetMain's scene history tracker for logging/debugging
            GONetMain.RecordSceneLoad(scene.name);

            // NOTE: Scene loading state is automatically cleared because scenesLoading.Remove(scene.name) happened above
            // GONetSceneManager.IsCurrentlyLoadingScene will return false once all scenes finish loading
            GONetLog.Info($"[CLIENT-SCENE-LOADED] Client finished loading scene '{scene.name}' - scenesLoading cleared (normal message processing resumes)");

            // IMPORTANT: Reset time sync AFTER scene load completes (especially for large scenes)
            // This ensures time sync is accurate after potentially long load times
            // For late-joining clients during initial connection, this is their FIRST time sync reset
            if (GONetMain.IsClient)
            {
                bool wasAlreadyInitialized = GONetMain.GONetClient != null && GONetMain.GONetClient.IsInitializedWithServer;
                if (wasAlreadyInitialized)
                {
                    // IMPORTANT: Don't reset again if we already reset BEFORE scene load!
                    // Double-resetting causes the second reset to fail (wasAlreadyClosed=false from first reset)
                    // leaving client in broken time sync state permanently
                    GONetLog.Info($"[TimeSync] Skipping AFTER scene load reset - already reset BEFORE load (client initialized): {scene.name}");
                }
                else
                {
                    // Late-joining clients that weren't initialized yet: First aggressive sync happens AFTER scene loads
                    //GONetLog.Info($"[TimeSync] Requesting aggressive time sync AFTER scene load completed (late-joining client - first aggressive sync): {scene.name}");
                    GONetMain.RequestAggressiveTimeSync($"scene_load_complete_{scene.name}");
                }

                // CRITICAL: Notify server that scene load is complete
                // Server needs this to know when to send scene-defined object GONetId assignments
                // This ensures late-joining clients have fully loaded the scene before receiving GONetIds
                SendSceneLoadCompleteWithRetry(scene.name, mode);
            }

            OnSceneLoadCompleted?.Invoke(scene.name, mode);
        }

        private void OnUnitySceneUnloaded(Scene scene)
        {
            if (scenesUnloading.Contains(scene.name))
                scenesUnloading.Remove(scene.name);

            // Clear proactive GONetId tracking for this scene (supports scene reload)
            // When scene reloads, clients need to receive GONetIds again
            GONetGlobal.ClearProactiveGonetIdTrackingForScene(scene.name);

            if (scenesAwaitingGONetIdSync.Remove(scene.name))
            {
                GONetLog.Info($"[SceneLoadComplete] Cleared pending GONetId sync for unloaded scene '{scene.name}'");
                GONetMain.Client_UpdateSceneDefinedObjectIdsReadyFlag();
            }
        }

        /// <summary>
        /// Tracks scenes awaiting GONetId sync confirmation from server.
        /// Key = scene name, Value = time when SceneLoadCompleteEvent was first sent.
        /// </summary>
        private readonly Dictionary<string, float> scenesAwaitingGONetIdSync = new Dictionary<string, float>();

        internal bool HasPendingGONetIdSync => scenesAwaitingGONetIdSync.Count > 0;
        internal bool IsSceneAwaitingGONetIdSync(string sceneName) => scenesAwaitingGONetIdSync.ContainsKey(sceneName);

        /// <summary>
        /// Maximum number of retry attempts for SceneLoadCompleteEvent.
        /// </summary>
        private const int SCENE_LOAD_COMPLETE_MAX_RETRIES = 3;

        /// <summary>
        /// Time to wait before retrying SceneLoadCompleteEvent (seconds).
        /// </summary>
        private const float SCENE_LOAD_COMPLETE_RETRY_DELAY = 1.0f;

        /// <summary>
        /// Max delay between retries after the initial burst (seconds).
        /// </summary>
        private const float SCENE_LOAD_COMPLETE_RETRY_MAX_DELAY = 5.0f;

        /// <summary>
        /// Sends SceneLoadCompleteEvent with automatic retry if server doesn't respond.
        /// This fixes a race condition where reliable messages can be silently dropped during late-joiner connection.
        /// </summary>
        private void SendSceneLoadCompleteWithRetry(string sceneName, LoadSceneMode mode)
        {
            SceneLoadCompleteEvent completeEvent = new SceneLoadCompleteEvent
            {
                SceneName = sceneName,
                Mode = mode
            };
            GONetLog.Info($"[SceneLoadComplete] Publishing SceneLoadCompleteEvent for '{sceneName}' - IsClient={GONetMain.IsClient}, IsServer={GONetMain.IsServer}, MyAuthorityId={GONetMain.MyAuthorityId}, IsConnected={GONetMain.GONetClient?.IsConnectedToServer}");
            GONetMain.EventBus.Publish(completeEvent);
            GONetLog.Info($"[SceneLoadComplete] Published SceneLoadCompleteEvent for '{sceneName}' to event bus");

            // Track this scene as awaiting GONetId sync
            scenesAwaitingGONetIdSync[sceneName] = (float)GONetMain.Time.ElapsedSeconds;
            GONetLog.Info($"[SceneLoadComplete] Awaiting GONetId sync for '{sceneName}' (pending scenes: {scenesAwaitingGONetIdSync.Count})");
            GONetMain.Client_UpdateSceneDefinedObjectIdsReadyFlag();

            // Start retry coroutine (only for pure clients, not host)
            if (!GONetMain.IsServer && global != null)
            {
                global.StartCoroutine(RetrySceneLoadCompleteIfNeeded(sceneName, mode));
            }
        }

        /// <summary>
        /// Coroutine that retries SceneLoadCompleteEvent if server hasn't sent GONetId sync.
        /// </summary>
        private System.Collections.IEnumerator RetrySceneLoadCompleteIfNeeded(string sceneName, LoadSceneMode mode)
        {
            int retryCount = 0;
            float retryDelay = SCENE_LOAD_COMPLETE_RETRY_DELAY;
            bool loggedFailure = false;

            while (true)
            {
                yield return new WaitForSeconds(retryDelay);

                // Check if we've received GONetId sync for this scene (server processed our event)
                // The server marks clients as having received proactive GONetIds when it sends them
                bool receivedSync = GONetGlobal.HasClientReceivedProactiveGonetIds(sceneName, GONetMain.MyAuthorityId);

                // Also check if scene is no longer in our awaiting list (cleared by receiving sync)
                bool stillAwaiting = scenesAwaitingGONetIdSync.ContainsKey(sceneName);

                if (receivedSync || !stillAwaiting)
                {
                    // Server processed our event, no retry needed
                    scenesAwaitingGONetIdSync.Remove(sceneName);
                    GONetLog.Debug($"[SceneLoadComplete] Scene '{sceneName}' GONetId sync confirmed, no retry needed");
                    GONetMain.Client_UpdateSceneDefinedObjectIdsReadyFlag();
                    yield break;
                }

                // Check if client is still connected
                if (GONetMain.GONetClient == null || !GONetMain.GONetClient.IsConnectedToServer)
                {
                    GONetLog.Warning($"[SceneLoadComplete] Client disconnected, aborting retry for '{sceneName}'");
                    scenesAwaitingGONetIdSync.Remove(sceneName);
                    GONetMain.Client_UpdateSceneDefinedObjectIdsReadyFlag();
                    yield break;
                }

                retryCount++;
                if (retryCount <= SCENE_LOAD_COMPLETE_MAX_RETRIES)
                {
                    GONetLog.Warning($"[SceneLoadComplete] RETRY {retryCount}/{SCENE_LOAD_COMPLETE_MAX_RETRIES} - Resending SceneLoadCompleteEvent for '{sceneName}' (server may not have received initial event)");
                }
                else if (!loggedFailure)
                {
                    loggedFailure = true;
                    GONetLog.Error($"[SceneLoadComplete] FAILED after {SCENE_LOAD_COMPLETE_MAX_RETRIES} retries - Server never acknowledged SceneLoadCompleteEvent for '{sceneName}'. Continuing low-frequency retries until GONetId sync arrives.");
                }

                SceneLoadCompleteEvent retryEvent = new SceneLoadCompleteEvent
                {
                    SceneName = sceneName,
                    Mode = mode
                };
                GONetMain.EventBus.Publish(retryEvent);

                if (retryCount >= SCENE_LOAD_COMPLETE_MAX_RETRIES)
                {
                    retryDelay = Math.Min(retryDelay * 2f, SCENE_LOAD_COMPLETE_RETRY_MAX_DELAY);
                }
            }
        }

        /// <summary>
        /// Called when client receives GONetId sync from server, confirming scene load complete was processed.
        /// </summary>
        internal void OnReceivedGONetIdSyncForScene(string sceneName)
        {
            if (scenesAwaitingGONetIdSync.ContainsKey(sceneName))
            {
                float waitTime = (float)GONetMain.Time.ElapsedSeconds - scenesAwaitingGONetIdSync[sceneName];
                GONetLog.Info($"[SceneLoadComplete] Received GONetId sync for '{sceneName}' after {waitTime:F2}s");
                scenesAwaitingGONetIdSync.Remove(sceneName);
                GONetLog.Info($"[SceneLoadComplete] Pending GONetId sync cleared for '{sceneName}' (pending scenes: {scenesAwaitingGONetIdSync.Count})");
                GONetMain.Client_UpdateSceneDefinedObjectIdsReadyFlag();
            }
        }

        /// <summary>
        /// For clients already in a scene when connecting, proactively notify the server so it can send GONetIds.
        /// </summary>
        internal void NotifyServerOfLoadedScenesIfClientInitialized()
        {
            if (!GONetMain.IsClient || GONetMain.IsServer || GONetMain.GONetClient == null || !GONetMain.GONetClient.IsInitializedWithServer)
            {
                return;
            }

            Scene activeScene = SceneManager.GetActiveScene();
            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || scene.name == HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE)
                {
                    continue;
                }

                if (IsSceneAwaitingGONetIdSync(scene.name) || IsSceneLoading(scene.name))
                {
                    continue;
                }

                LoadSceneMode mode = scene.name == activeScene.name ? LoadSceneMode.Single : LoadSceneMode.Additive;
                SendSceneLoadCompleteWithRetry(scene.name, mode);
            }
        }

        /// <summary>
        /// CRITICAL FIX (January 2026): Clears all pending GONetId sync tracking for demoted hosts.
        ///
        /// After voluntary demotion, the demoted host already has all scenes loaded and GONetIds assigned.
        /// However, redundant scene load notifications from the new host can populate scenesAwaitingGONetIdSync,
        /// causing HasPendingGONetIdSync to return true AFTER the grace period ends. This blocks all sync
        /// bundles indefinitely, resulting in "No inbound sync processed" warnings and frozen objects.
        ///
        /// Call this during DemoteOriginalServerAfterHandoff to ensure clean state.
        /// </summary>
        internal void ClearPendingGONetIdSyncForDemotion()
        {
            if (scenesAwaitingGONetIdSync.Count > 0)
            {
                GONetLog.Info($"[SceneLoadComplete] Clearing {scenesAwaitingGONetIdSync.Count} pending GONetId sync entries for demoted host");
                scenesAwaitingGONetIdSync.Clear();
                GONetMain.Client_UpdateSceneDefinedObjectIdsReadyFlag();
            }
        }

        // ========================================
        // COROUTINE HELPERS
        // ========================================

        /// <summary>
        /// Coroutine that waits for a specific scene to finish loading.
        /// Use with StartCoroutine from a MonoBehaviour.
        /// Example: yield return StartCoroutine(SceneManager.WaitForSceneLoad("MyScene"));
        /// </summary>
        public System.Collections.IEnumerator WaitForSceneLoad(string sceneName)
        {
            // Wait until scene is no longer in loading state
            while (IsSceneLoading(sceneName))
            {
                yield return null;
            }

            // Verify scene actually loaded successfully
            if (!IsSceneLoaded(sceneName))
            {
                GONetLog.Error($"[GONetSceneManager] Scene '{sceneName}' failed to load");
            }
        }

        /// <summary>
        /// Coroutine that waits for a specific scene to finish unloading.
        /// </summary>
        public System.Collections.IEnumerator WaitForSceneUnload(string sceneName)
        {
            // Wait until scene is no longer in unloading state
            while (IsSceneUnloading(sceneName))
            {
                yield return null;
            }

            // Verify scene actually unloaded
            if (IsSceneLoaded(sceneName))
            {
                GONetLog.Error($"[GONetSceneManager] Scene '{sceneName}' failed to unload");
            }
        }

        /// <summary>
        /// Coroutine that waits until no scenes are loading.
        /// Useful when loading multiple scenes and need to wait for all to complete.
        /// </summary>
        public System.Collections.IEnumerator WaitForAllSceneLoads()
        {
            while (IsAnySceneLoading)
            {
                yield return null;
            }
        }

        // ========================================
        // ADDRESSABLES SUPPORT (Phase 3)
        // ========================================

#if ADDRESSABLES_AVAILABLE
        private async void LoadSceneFromAddressablesAsync(SceneLoadEvent evt)
        {
            try
            {
                GONetLog.Debug($"[GONetSceneManager] Starting Addressables scene load: '{evt.SceneName}'");

                // Clean up any existing handle for this scene if in Single mode
                if (evt.Mode == LoadSceneMode.Single && addressableSceneHandles.Count > 0)
                {
                    GONetLog.Debug("[GONetSceneManager] Single mode load - releasing all existing Addressable scene handles");
                    foreach (var kvp in addressableSceneHandles.ToList())
                    {
                        if (kvp.Value.IsValid())
                        {
                            Addressables.Release(kvp.Value);
                        }
                        addressableSceneHandles.Remove(kvp.Key);
                    }
                }

                var handle = Addressables.LoadSceneAsync(
                    evt.SceneName,
                    evt.Mode,
                    evt.ActivateOnLoad,
                    evt.Priority
                );

                // Store handle immediately for tracking
                addressableSceneHandles[evt.SceneName] = handle;

                // Wait for completion
                await handle.Task;

                // Check result
                if (handle.Status == AsyncOperationStatus.Succeeded)
                {
                    GONetLog.Info($"[GONetSceneManager] Addressable scene loaded successfully: '{evt.SceneName}' (Mode: {evt.Mode})");

                    // OnSceneLoadCompleted will be fired by OnUnitySceneLoaded
                    // But we can add additional Addressables-specific handling here if needed
                }
                else
                {
                    GONetLog.Error($"[GONetSceneManager] Failed to load addressable scene '{evt.SceneName}': {handle.OperationException?.Message ?? "Unknown error"}");

                    // Clean up failed handle
                    addressableSceneHandles.Remove(evt.SceneName);
                    scenesLoading.Remove(evt.SceneName);

                    if (handle.IsValid())
                    {
                        Addressables.Release(handle);
                    }
                }
            }
            catch (System.Exception ex)
            {
                GONetLog.Error($"[GONetSceneManager] Exception loading addressable scene '{evt.SceneName}': {ex.Message}\n{ex.StackTrace}");

                // Clean up on exception
                addressableSceneHandles.Remove(evt.SceneName);
                scenesLoading.Remove(evt.SceneName);
            }
        }

        private async void UnloadSceneFromAddressablesAsync(string sceneName)
        {
            try
            {
                if (!addressableSceneHandles.TryGetValue(sceneName, out var handle))
                {
                    GONetLog.Warning($"[GONetSceneManager] Cannot unload addressable scene '{sceneName}' - not loaded or not tracked");
                    scenesUnloading.Remove(sceneName);
                    return;
                }

                if (!handle.IsValid())
                {
                    GONetLog.Warning($"[GONetSceneManager] Handle for addressable scene '{sceneName}' is no longer valid");
                    addressableSceneHandles.Remove(sceneName);
                    scenesUnloading.Remove(sceneName);
                    return;
                }

                GONetLog.Debug($"[GONetSceneManager] Starting Addressables scene unload: '{sceneName}'");

                var unloadHandle = Addressables.UnloadSceneAsync(handle);
                await unloadHandle.Task;

                if (unloadHandle.Status == AsyncOperationStatus.Succeeded)
                {
                    GONetLog.Info($"[GONetSceneManager] Addressable scene unloaded successfully: '{sceneName}'");
                    addressableSceneHandles.Remove(sceneName);
                }
                else
                {
                    GONetLog.Error($"[GONetSceneManager] Failed to unload addressable scene '{sceneName}': {unloadHandle.OperationException?.Message ?? "Unknown error"}");

                    // Still remove from tracking even if unload failed
                    addressableSceneHandles.Remove(sceneName);
                }
            }
            catch (System.Exception ex)
            {
                GONetLog.Error($"[GONetSceneManager] Exception unloading addressable scene '{sceneName}': {ex.Message}\n{ex.StackTrace}");

                // Clean up on exception
                addressableSceneHandles.Remove(sceneName);
            }
            finally
            {
                // Always ensure unloading state is cleaned up
                scenesUnloading.Remove(sceneName);
            }
        }

        /// <summary>
        /// Gets the Addressable scene handle for a loaded scene.
        /// Returns null if scene not loaded via Addressables or handle not found.
        /// </summary>
        public AsyncOperationHandle<SceneInstance>? GetAddressableSceneHandle(string sceneName)
        {
            if (addressableSceneHandles.TryGetValue(sceneName, out var handle))
            {
                return handle;
            }
            return null;
        }

        /// <summary>
        /// Releases all Addressable scene handles (for cleanup on shutdown).
        /// </summary>
        internal void ReleaseAllAddressableSceneHandles()
        {
            foreach (var kvp in addressableSceneHandles)
            {
                if (kvp.Value.IsValid())
                {
                    GONetLog.Debug($"[GONetSceneManager] Releasing Addressable scene handle: '{kvp.Key}'");
                    Addressables.Release(kvp.Value);
                }
            }
            addressableSceneHandles.Clear();
        }
#endif

        // ========================================
        // SCENE UTILITIES (formerly GONetSceneUtils)
        // ========================================

        /// <summary>
        /// Reliably determines if a GameObject is in DontDestroyOnLoad.
        /// Works in both Editor and Build.
        /// In Editor: scene.name == "DontDestroyOnLoad"
        /// In Build: scene is null or invalid
        /// </summary>
        public static bool IsDontDestroyOnLoad(GameObject gameObject)
        {
            if (gameObject == null) return false;

            Scene scene = gameObject.scene;

            // Fast path: Check validity first (fastest check)
            // Build: invalid scene = DDOL
            if (!scene.IsValid())
                return true;

            // Slower path: String comparison (only if scene is valid)
            // Editor: scene name is "DontDestroyOnLoad"
            return scene.name == HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE;
        }

        /// <summary>
        /// Gets scene identifier for networked scene association.
        /// Returns "DontDestroyOnLoad" for DDOL objects, scene name otherwise.
        /// </summary>
        public static string GetSceneIdentifier(GameObject gameObject)
        {
            if (IsDontDestroyOnLoad(gameObject))
                return HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE;

            Scene scene = gameObject.scene;
            return scene.IsValid() ? scene.name : string.Empty;
        }
    }
}
