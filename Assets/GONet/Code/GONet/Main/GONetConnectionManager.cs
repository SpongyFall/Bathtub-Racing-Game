/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 * This file provides the GONetConnectionManager for managing network initialization and connection workflow.
 */
using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using GONet.Transport;

namespace GONet
{
    /// <summary>
    /// Manages GONet network initialization, connection workflow, and configuration.
    ///
    /// This component coordinates between:
    /// - Command-line arguments (for automated testing/builds)
    /// - Connection wizard UI (for manual setup)
    /// - GONetGlobal (for actual network initialization)
    ///
    /// Usage:
    /// 1. Add this component to a persistent GameObject in the lobby scene
    /// 2. Configure via UI wizard or command-line args
    /// 3. Call Connect() to initialize GONet with selected configuration
    ///
    /// IMPORTANT: This is lobby infrastructure and does NOT require GONetParticipant.
    /// It extends MonoBehaviour directly to avoid circular dependency (it creates GONetGlobal).
    /// </summary>
    [DisallowMultipleComponent]
    public class GONetConnectionManager : MonoBehaviour
    {
        private static readonly string LOGCODE = $"[{nameof(GONetConnectionManager)}] ";

        #region Serialized Fields

        [Header("GONet Setup")]
        [Tooltip("GONetGlobal prefab to instantiate (should be lobby-specific - no sample spawner or scene-specific components)")]
        [SerializeField] private GameObject gonetGlobalPrefab;

        [Header("Configuration")]
        [Tooltip("Current connection configuration (runtime only, not serialized)")]
        [SerializeField] private GONetConnectionPreset currentPreset;

        [Tooltip("Path to NetworkLobbyConfig.json (defaults to StreamingAssets/GONet/)")]
        [SerializeField] private string configPath = "GONet/NetworkLobbyConfig.json";

        [Header("Scene Management")]
        [Tooltip("Scene to load after successful connection (leave empty to stay in current scene)")]
        [SerializeField] private string gameSceneName = "GONetSample";

        [Header("Status")]
        [Tooltip("Is GONet currently connected?")]
        [SerializeField] private bool isConnected = false;

        [Tooltip("Current connection status message")]
        [SerializeField] private string statusMessage = "Not connected";

        #endregion

        #region Events

        /// <summary>
        /// Fired when connection attempt starts
        /// </summary>
        public event Action<GONetConnectionPreset> OnConnectionStarted;

        /// <summary>
        /// Fired when connection succeeds
        /// </summary>
        public event Action OnConnectionSuccess;

        /// <summary>
        /// Fired when connection fails
        /// </summary>
        public event Action<string> OnConnectionFailed;

        /// <summary>
        /// Fired when disconnected
        /// </summary>
        public event Action OnDisconnected;

        /// <summary>
        /// Fired when status message changes (for UI updates)
        /// </summary>
        public event Action<string> OnStatusChanged;

        #endregion

        #region Properties

        /// <summary>
        /// Current connection preset (can be set via UI wizard or command-line)
        /// </summary>
        public GONetConnectionPreset CurrentPreset
        {
            get => currentPreset;
            set => currentPreset = value;
        }

        /// <summary>
        /// Is GONet currently connected?
        /// </summary>
        public bool IsConnected => isConnected;

        /// <summary>
        /// Current connection status message
        /// </summary>
        public string StatusMessage => statusMessage;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // Check for command-line arguments
            GONetConnectionPreset cmdLinePreset = GONetCommandLineParser.ParseCommandLineArgs();
            if (cmdLinePreset != null)
            {
                currentPreset = cmdLinePreset;
                GONetLog.Info($"{LOGCODE}Command-line configuration detected");

                // Auto-connect if specified
                if (cmdLinePreset.autoConnect)
                {
                    GONetLog.Info($"{LOGCODE}Auto-connect enabled, connecting in 1 second...");
                    StartCoroutine(AutoConnectCoroutine());
                }
            }
        }

        private void Start()
        {
            // Defer event subscription until GONet is initialized
            // This handles both cases:
            // 1. Lobby: GONetGlobal created later during Connect()
            // 2. Scene-defined: GONetGlobal exists but SceneManager might not be initialized yet
            StartCoroutine(DeferredGONetEventSubscription());
        }

        private void OnDestroy()
        {
            // Unsubscribe from GONet events
            UnsubscribeFromGONetEvents();
        }

        #endregion

        #region Connection Management

        /// <summary>
        /// Initiates connection with current preset configuration.
        /// This is the main entry point called by UI wizard or auto-connect.
        /// </summary>
        public void Connect()
        {
            if (currentPreset == null)
            {
                SetStatus("Error: No connection configuration set");
                OnConnectionFailed?.Invoke("No connection configuration set");
                GONetLog.Error($"{LOGCODE}Cannot connect: currentPreset is null");
                return;
            }

            if (isConnected)
            {
                SetStatus("Already connected");
                GONetLog.Warning($"{LOGCODE}Already connected, ignoring Connect() call");
                return;
            }

            // CRITICAL: Instantiate GONetGlobal if it doesn't exist yet
            // This allows lobby scene to start without a scene-defined GONetGlobal
            // and creates it dynamically with proper configuration
            GONetLog.Info($"{LOGCODE}[DEBUG] Connect() - GONetMain.Global null check: {GONetMain.Global == null}");

            if (GONetMain.Global == null)
            {
                SetStatus("Initializing GONet...");
                GONetLog.Info($"{LOGCODE}GONetGlobal not found - instantiating from prefab");
                GONetLog.Info($"{LOGCODE}[DEBUG] About to call InstantiateGONetGlobal()");

                if (!InstantiateGONetGlobal())
                {
                    GONetLog.Error($"{LOGCODE}[DEBUG] InstantiateGONetGlobal() returned false - aborting");
                    SetStatus("Error: Failed to instantiate GONetGlobal");
                    OnConnectionFailed?.Invoke("Failed to instantiate GONetGlobal");
                    return;
                }

                GONetLog.Info($"{LOGCODE}[DEBUG] InstantiateGONetGlobal() returned true - starting WaitForGONetInitThenConnect coroutine");

                // Wait for GONetGlobal to initialize (Awake calls GONetMain.InitOnUnityMainThread)
                StartCoroutine(WaitForGONetInitThenConnect());
                return;
            }

            GONetLog.Info($"{LOGCODE}[DEBUG] GONetMain.Global already exists - skipping instantiation, starting ConnectCoroutine");
            StartCoroutine(ConnectCoroutine());
        }

        /// <summary>
        /// Instantiates GONetGlobal from the assigned prefab at runtime.
        /// Returns true if successful, false if prefab not assigned or instantiation fails.
        ///
        /// NEW ARCHITECTURE: Adds GONetSampleClientOrServer component to handle transport initialization.
        /// This unifies the lobby flow with the GONetSample scene flow - both use identical code paths.
        ///
        /// CRITICAL: This is safe to call even with GONet's automatic spawn networking because
        /// it only runs when GONetMain.Global == null (before GONet initializes).
        /// At this point, GONet's networking hooks haven't been registered yet.
        /// </summary>
        private bool InstantiateGONetGlobal()
        {
            GONetLog.Info($"{LOGCODE}Instantiating GONetGlobal from prefab...");

            // Validate prefab is assigned
            if (gonetGlobalPrefab == null)
            {
                GONetLog.Error($"{LOGCODE}GONetGlobal prefab not assigned! Please assign the lobby-specific GONetGlobal prefab in the Inspector.");
                return false;
            }

            // Validate prefab has GONetGlobal component
            GONetGlobal prefabGlobal = gonetGlobalPrefab.GetComponent<GONetGlobal>();
            if (prefabGlobal == null)
            {
                GONetLog.Error($"{LOGCODE}Assigned prefab '{gonetGlobalPrefab.name}' does not have a GONetGlobal component!");
                return false;
            }

            // CRITICAL: Instantiate with prefab temporarily inactive to prevent Awake() during Instantiate()
            // We need to configure the instance BEFORE Awake runs
            bool prefabWasActive = gonetGlobalPrefab.activeSelf;
            if (prefabWasActive)
            {
                gonetGlobalPrefab.SetActive(false);  // Temporarily disable prefab for instantiation
            }

            GameObject gonetGlobalInstance = Instantiate(gonetGlobalPrefab);
            gonetGlobalInstance.name = "GONetGlobal"; // Remove "(Clone)" suffix

            // Restore prefab's original active state immediately
            if (prefabWasActive)
            {
                gonetGlobalPrefab.SetActive(true);
            }

            // Apply configuration from current preset BEFORE Awake runs
            GONetGlobal global = gonetGlobalInstance.GetComponent<GONetGlobal>();
            if (global == null)
            {
                GONetLog.Error($"{LOGCODE}Failed to get GONetGlobal component from instantiated object!");
                Destroy(gonetGlobalInstance);
                return false;
            }

            ApplyPresetToGONetGlobal(global);

            // CRITICAL: Add GONetSteamManager FIRST if using Steamworks (BEFORE activation)
            // This ensures Steam API initializes during GONetSteamManager.Awake() before GONetSampleClientOrServer.Awake()
            if (currentPreset.usePluggableTransport && currentPreset.transportType == GONetTransportType.Steamworks)
            {
                if (GONetSteamManager.HasInstance)
                {
                    GONetLog.Debug($"{LOGCODE}GONetSteamManager already exists, skipping addition to GONetGlobal");
                }
                else if (gonetGlobalInstance.GetComponent<GONetSteamManager>() == null)
                {
                    gonetGlobalInstance.AddComponent<GONetSteamManager>();
                    GONetLog.Info($"{LOGCODE}Added GONetSteamManager component (will initialize during Awake)");
                }
            }

            // CRITICAL: Add GONetSampleClientOrServer component to handle transport initialization
            // This delegates all transport/server/client logic to proven, battle-tested code
            var clientOrServer = gonetGlobalInstance.AddComponent<GONetSampleClientOrServer>();

            // Configure based on role
            clientOrServer.isServer = (currentPreset.role == GONetConnectionRole.DedicatedServer);
            clientOrServer.isHost = (currentPreset.role == GONetConnectionRole.Host);

            GONetLog.Info($"{LOGCODE}Added GONetSampleClientOrServer component (isHost={clientOrServer.isHost}, isServer={clientOrServer.isServer})");

            // Activate the instance - this will trigger Awake() cascade:
            // 1. GONetGlobal.Awake() (execution order -32000)
            // 2. GONetSteamManager.Awake() (execution order -31000) - Initializes Steam API
            // 3. GONetSampleClientOrServer.Awake() (default 0) - Creates transport, SDR should be ready
            gonetGlobalInstance.SetActive(true);

            GONetLog.Info($"{LOGCODE}GONetGlobal instantiated successfully - transport initialization delegated to GONetSampleClientOrServer");
            return true;
        }

        /// <summary>
        /// Applies connection preset configuration to GONetGlobal before it initializes.
        /// This must be called BEFORE GONetGlobal.Awake() runs.
        /// </summary>
        private void ApplyPresetToGONetGlobal(GONetGlobal global)
        {
            // Set server connection info based on role using internal setters
            // Host/Server: Listen on all interfaces (0.0.0.0)
            // Client: Connect to specified IP
            if (currentPreset.role == GONetConnectionRole.Host || currentPreset.role == GONetConnectionRole.DedicatedServer)
            {
                GONetGlobal.ServerIPAddress_Actual = "0.0.0.0"; // Listen on all interfaces
            }
            else
            {
                GONetGlobal.ServerIPAddress_Actual = currentPreset.ipAddress; // Connect to this IP
            }

            GONetGlobal.ServerPort_Actual = currentPreset.port;

            // Set GONetId batch size
            global.client_GONetIdBatchSize = currentPreset.gonetIdBatchSize;

            // Apply transport configuration
            global.usePluggableTransport = currentPreset.usePluggableTransport;
            global.transportType = currentPreset.transportType;

            // Set max connections (for server/host) - CRITICAL: Must be set BEFORE Awake() creates GONetServer
            if (currentPreset.role == GONetConnectionRole.Host || currentPreset.role == GONetConnectionRole.DedicatedServer)
            {
                global.maxConnections = currentPreset.maxConnections;
                GONetLog.Info($"{LOGCODE}Set maxConnections to {currentPreset.maxConnections} BEFORE GONetSampleClientOrServer.Awake()");
            }

            // Note: Network send rate configuration would require additional GONetGlobal API exposure
            // For now, omitting this setting - can be added later if GONetGlobal exposes it

            GONetLog.Info($"{LOGCODE}Applied preset configuration to GONetGlobal - Role: {currentPreset.role}, Server: {GONetGlobal.ServerIPAddress_Actual}:{GONetGlobal.ServerPort_Actual}, BatchSize: {global.client_GONetIdBatchSize}, MaxConnections: {global.maxConnections}, UsePluggableTransport: {global.usePluggableTransport}, TransportType: {global.transportType}");
        }

        /// <summary>
        /// Waits for GONet to be initialized, then starts connection.
        /// Also ensures event subscriptions are set up once SceneManager is ready.
        /// </summary>
        private IEnumerator WaitForGONetInitThenConnect()
        {
            float timeout = 10f;
            float elapsed = 0f;

            // Wait for GONetGlobal to be instantiated
            while (GONetMain.Global == null && elapsed < timeout)
            {
                yield return null;
                elapsed += Time.deltaTime;
            }

            if (GONetMain.Global == null)
            {
                string errorMsg = "GONet initialization timeout after 10 seconds";
                SetStatus($"Error: {errorMsg}");
                OnConnectionFailed?.Invoke(errorMsg);
                GONetLog.Error($"{LOGCODE}{errorMsg}");
                yield break;
            }

            GONetLog.Info($"{LOGCODE}GONetGlobal initialized after {elapsed:F2}s, waiting for SceneManager...");

            // Wait for SceneManager to be created (happens during InitOnUnityMainThread)
            // This is critical for event subscription to work
            elapsed = 0f;
            while (GONetMain.SceneManager == null && elapsed < timeout)
            {
                yield return null;
                elapsed += Time.deltaTime;
            }

            if (GONetMain.SceneManager == null)
            {
                string errorMsg = "GONetMain.SceneManager initialization timeout after 10 seconds";
                SetStatus($"Error: {errorMsg}");
                OnConnectionFailed?.Invoke(errorMsg);
                GONetLog.Error($"{LOGCODE}{errorMsg}");
                yield break;
            }

            GONetLog.Info($"{LOGCODE}SceneManager initialized after {elapsed:F2}s, subscribing to events...");

            // Subscribe to events now that SceneManager exists
            SubscribeToGONetEvents();

            GONetLog.Info($"{LOGCODE}GONet fully initialized, starting connection...");
            StartCoroutine(ConnectCoroutine());
        }

        /// <summary>
        /// Disconnects from network and returns to lobby
        /// </summary>
        public void Disconnect()
        {
            if (!isConnected)
            {
                GONetLog.Warning($"{LOGCODE}Not connected, ignoring Disconnect() call");
                return;
            }

            SetStatus("Disconnecting...");
            GONetLog.Info($"{LOGCODE}Disconnecting from network");

            // TODO: Implement GONet shutdown logic
            // For now, just reset state
            isConnected = false;
            SetStatus("Disconnected");
            OnDisconnected?.Invoke();

            // TODO: Optionally return to lobby scene
        }

        /// <summary>
        /// Coroutine that handles connection workflow.
        ///
        /// NEW ARCHITECTURE: GONetSampleClientOrServer handles transport initialization automatically.
        /// This coroutine now just waits for connection status and loads the game scene.
        ///
        /// This code path is only used when GONetGlobal already exists in scene (GONetSample flow).
        /// For lobby flow, GONetSampleClientOrServer is added during InstantiateGONetGlobal().
        /// </summary>
        private IEnumerator ConnectCoroutine()
        {
            GONetLog.Info($"{LOGCODE}Starting connection workflow (transport handled by GONetSampleClientOrServer)");
            SetStatus($"Connecting as {currentPreset.role}...");
            OnConnectionStarted?.Invoke(currentPreset);

            // Apply configuration to GONetGlobal (for GONetSample scene flow)
            ApplyConfigurationToGONet();

            // Wait for GONetSampleClientOrServer to complete connection
            // GONetSampleClientOrServer.Awake() handles all transport initialization
            float timeout = 30f;
            float elapsed = 0f;

            while (elapsed < timeout)
            {
                // Check if connection completed
                bool connected = false;
                bool failed = false;

                if (currentPreset.role == GONetConnectionRole.Client)
                {
                    connected = GONetMain.IsClient && GONetMain.GONetClient != null && GONetMain.GONetClient.IsConnectedToServer;
                }
                else
                {
                    // Server: Check if server exists AND is running successfully
                    // IsRunning checks if StartServer() succeeded (not just if server object exists)
                    if (GONetMain.gonetServer != null)
                    {
                        connected = GONetMain.gonetServer.IsRunning;
                        // If server exists but not running, it failed to start
                        if (!connected)
                        {
                            failed = true;
                        }
                    }
                }

                if (connected)
                {
                    isConnected = true;
                    SetStatus($"Connected as {currentPreset.role}");
                    OnConnectionSuccess?.Invoke();
                    GONetLog.Info($"{LOGCODE}Connection successful!");

                    // Load game scene if specified
                    if (!string.IsNullOrEmpty(gameSceneName))
                    {
                        yield return new WaitForSeconds(0.5f); // Brief delay for network stabilization
                        LoadGameScene();
                    }

                    yield break; // Success!
                }

                // If we detected failure, exit immediately instead of waiting for timeout
                if (failed)
                {
                    string failureMsg = $"Server failed to start (check logs for details - likely SteamAPI initialization failure)";
                    SetStatus(failureMsg);
                    OnConnectionFailed?.Invoke(failureMsg);
                    GONetLog.Error($"{LOGCODE}{failureMsg}");
                    yield break;
                }

                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }

            // Timeout
            string errorMsg = $"Connection timed out after {timeout}s";
            SetStatus(errorMsg);
            OnConnectionFailed?.Invoke(errorMsg);
            GONetLog.Error($"{LOGCODE}{errorMsg}");
        }

        /// <summary>
        /// Auto-connect coroutine (used for command-line auto-connect)
        /// </summary>
        private IEnumerator AutoConnectCoroutine()
        {
            yield return new WaitForSeconds(1f); // Wait for initialization
            Connect();
        }

        #endregion

        #region GONet Initialization

        /// <summary>
        /// Applies current preset configuration to GONetGlobal settings
        /// </summary>
        private void ApplyConfigurationToGONet()
        {
            if (GONetGlobal.Instance == null)
            {
                GONetLog.Error($"{LOGCODE}GONetGlobal.Instance is null! Cannot apply configuration.");
                return;
            }

            // Apply batch size
            GONetGlobal.Instance.client_GONetIdBatchSize = currentPreset.gonetIdBatchSize;
            GONetLog.Debug($"{LOGCODE}Set GONetId batch size to {currentPreset.gonetIdBatchSize}");

            // CRITICAL: Apply transport configuration
            // This ensures settings are applied even when GONetGlobal already exists in scene (GONetSample flow)
            GONetGlobal.Instance.usePluggableTransport = currentPreset.usePluggableTransport;
            GONetGlobal.Instance.transportType = currentPreset.transportType;
            GONetLog.Info($"{LOGCODE}Applied transport configuration - UsePluggableTransport: {GONetGlobal.Instance.usePluggableTransport}, TransportType: {GONetGlobal.Instance.transportType}");

            // CRITICAL: Add GONetSteamManager if using Steamworks transport (for Path B where GONetGlobal already exists)
            if (currentPreset.usePluggableTransport && currentPreset.transportType == GONetTransportType.Steamworks)
            {
                if (GONetGlobal.Instance.GetComponent<GONetSteamManager>() == null)
                {
                    GONetLog.Info($"{LOGCODE}Adding GONetSteamManager component for Steamworks transport");
                    GONetGlobal.Instance.gameObject.AddComponent<GONetSteamManager>();
                    // Wait one frame for GONetSteamManager.Awake() to run and initialize Steam
                    // Note: This is called from ConnectCoroutine which already has "yield return null" right after
                }
                else
                {
                    GONetLog.Debug($"{LOGCODE}GONetSteamManager already exists, skipping");
                }
            }

            // Apply max connections (for server/host)
            if (currentPreset.role == GONetConnectionRole.Host || currentPreset.role == GONetConnectionRole.DedicatedServer)
            {
                GONetGlobal.Instance.maxConnections = currentPreset.maxConnections;
                GONetLog.Warning($"{LOGCODE}★★★ SET MAX CONNECTIONS TO {currentPreset.maxConnections} ★★★");
            }

            // Apply network send rate
            // TODO: Map to GONetGlobal settings (if exposed)
            GONetLog.Debug($"{LOGCODE}Network send rate: {currentPreset.networkSendRate} Hz");

            // TODO: Apply other settings as GONetGlobal exposes them
        }

        #endregion

        #region Scene Management

        /// <summary>
        /// Loads the game scene after successful connection
        /// </summary>
        private void LoadGameScene()
        {
            GONetLog.Info($"{LOGCODE}Loading game scene: {gameSceneName}");
            SetStatus($"Loading {gameSceneName}...");

            // GONetSceneManager is server-authoritative - only server can load scenes directly
            if (GONetMain.IsServer)
            {
                // Server: Load scene directly
                if (GONetMain.SceneManager != null)
                {
                    GONetMain.SceneManager.LoadSceneFromBuildSettings(gameSceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
                }
                else
                {
                    UnityEngine.SceneManagement.SceneManager.LoadScene(gameSceneName);
                }
            }
            else if (GONetMain.IsClient)
            {
                // Client: Don't load scene - server will send scene load command
                // Or if you want client to request it: GONetMain.SceneManager.RequestSceneChange(gameSceneName);
                GONetLog.Info($"{LOGCODE}Client connected - waiting for server to load scene");
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Deferred GONet event subscription coroutine.
        /// Waits for GONetMain.SceneManager to be initialized before subscribing to events.
        /// Handles both startup cases:
        /// 1. Lobby: Waits for Connect() to instantiate GONetGlobal
        /// 2. Scene-defined: Waits for GONetGlobal.Awake() → GONetMain.InitOnUnityMainThread() → InitSceneManager()
        /// </summary>
        private IEnumerator DeferredGONetEventSubscription()
        {
            float timeout = 30f; // Generous timeout (lobby might wait indefinitely until user clicks Connect)
            float elapsed = 0f;

            // Wait for GONetMain.SceneManager to be initialized
            // SceneManager is created during GONetMain.InitOnUnityMainThread() → InitSceneManager()
            while (GONetMain.SceneManager == null && elapsed < timeout)
            {
                yield return null;
                elapsed += Time.deltaTime;
            }

            if (GONetMain.SceneManager == null)
            {
                // This is EXPECTED for lobby scenario if user hasn't clicked Connect yet
                // We'll try again when Connect() is called
                GONetLog.Debug($"{LOGCODE}GONetMain.SceneManager still null after {elapsed:F2}s - will subscribe later when GONet initializes");
                yield break;
            }

            // SceneManager exists - safe to subscribe now
            SubscribeToGONetEvents();
        }

        /// <summary>
        /// Subscribes to GONet events for connection status tracking.
        /// IMPORTANT: Only call this AFTER GONetMain.SceneManager is initialized.
        /// Use DeferredGONetEventSubscription() coroutine for automatic timing.
        /// </summary>
        private void SubscribeToGONetEvents()
        {
            // Defensive check (should never happen if called from DeferredGONetEventSubscription)
            if (GONetMain.SceneManager == null)
            {
                GONetLog.Warning($"{LOGCODE}Cannot subscribe to OnSceneLoadCompleted - GONetMain.SceneManager is null");
                return;
            }

            GONetLog.Info($"{LOGCODE}Subscribing to OnSceneLoadCompleted");
            GONetMain.SceneManager.OnSceneLoadCompleted += OnSceneLoadCompleted;
        }

        /// <summary>
        /// Unsubscribes from GONet events
        /// </summary>
        private void UnsubscribeFromGONetEvents()
        {
            if (GONetMain.SceneManager != null)
            {
                GONetMain.SceneManager.OnSceneLoadCompleted -= OnSceneLoadCompleted;
            }
        }

        /// <summary>
        /// Called when a scene finishes loading - hide approval panel if showing
        /// </summary>
        private void OnSceneLoadCompleted(string sceneName, LoadSceneMode mode)
        {
            GONetLog.Info($"{LOGCODE}OnSceneLoadCompleted: '{sceneName}' - hiding approval panel if visible");

            // Hide approval panel when scene loads (approval was processed)
            if (sceneApprovalPanel != null && sceneApprovalPanel.activeSelf)
            {
                GONetLog.Info($"{LOGCODE}Hiding scene approval panel on scene load completion");
                sceneApprovalPanel.SetActive(false);
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Sets status message and fires OnStatusChanged event
        /// </summary>
        private void SetStatus(string message)
        {
            statusMessage = message;
            OnStatusChanged?.Invoke(message);
            GONetLog.Debug($"{LOGCODE}Status: {message}");
        }

        /// <summary>
        /// Loads NetworkLobbyConfig.json from StreamingAssets
        /// </summary>
        public string LoadConfigJSON()
        {
            string path = Path.Combine(Application.streamingAssetsPath, configPath);

            if (!File.Exists(path))
            {
                GONetLog.Warning($"{LOGCODE}NetworkLobbyConfig.json not found at {path}");
                return null;
            }

            try
            {
                string json = File.ReadAllText(path);
                GONetLog.Debug($"{LOGCODE}Loaded NetworkLobbyConfig.json ({json.Length} chars)");
                return json;
            }
            catch (Exception ex)
            {
                GONetLog.Error($"{LOGCODE}Failed to load NetworkLobbyConfig.json: {ex}");
                return null;
            }
        }

        #endregion

        #region Scene Approval System

        private GameObject sceneApprovalPanel;
        private TextMeshProUGUI sceneApprovalMessageText;
        private UnityEngine.UI.Button sceneApproveButton;
        private UnityEngine.UI.Button sceneDenyButton;
        private string pendingSceneName;
        private LoadSceneMode pendingSceneMode;
        private ushort pendingRequestingAuthority;

        /// <summary>
        /// Setup scene approval system - call this after server starts
        /// </summary>
        private void SetupSceneApprovalSystem()
        {
            if (GONetMain.SceneManager == null)
            {
                GONetLog.Warning($"{LOGCODE}Cannot setup scene approval - GONetMain.SceneManager is null");
                return;
            }

            // Enable async approval mode
            GONetMain.SceneManager.RequiresAsyncApproval = true;

            // Subscribe to validation event
            GONetMain.SceneManager.OnValidateSceneLoad += OnValidateSceneLoadRequest;

            // Subscribe to scene load completed to hide approval panels
            GONetMain.SceneManager.OnSceneLoadCompleted += OnSceneLoadCompleted;

            GONetLog.Info($"{LOGCODE}Scene approval system enabled (with scene load cleanup)");
        }

        /// <summary>
        /// Validation hook - shows approval UI for client requests
        /// </summary>
        private bool OnValidateSceneLoadRequest(string sceneName, LoadSceneMode mode, ushort requestingAuthority)
        {
            GONetLog.Info($"{LOGCODE}Scene load request: '{sceneName}' from authority {requestingAuthority} (MyAuthorityId: {GONetMain.MyAuthorityId})");

            // Server's own requests are auto-approved
            if (GONetMain.IsServer && requestingAuthority == GONetMain.MyAuthorityId)
            {
                GONetLog.Info($"{LOGCODE}Server's own request - auto-approved");
                return true;
            }

            // Client request - show approval UI
            if (GONetMain.IsServer)
            {
                GONetLog.Info($"{LOGCODE}Client request - showing approval UI");
                pendingSceneName = sceneName;
                pendingSceneMode = mode;
                pendingRequestingAuthority = requestingAuthority;
                ShowSceneApprovalUI(sceneName, requestingAuthority);
                return true; // Return true to allow RPC through - async approval will send response later
            }

            return true; // Client's own requests - always allow on client side
        }

        /// <summary>
        /// Shows scene approval UI (creates it if needed)
        /// </summary>
        private void ShowSceneApprovalUI(string sceneName, ushort requestingAuthority)
        {
            // CRITICAL: Defer to SceneSelectionUI if it exists (similar to ExitButtonUI deferral pattern)
            // SceneSelectionUI provides more comprehensive scene management UI
            GONet.Sample.SceneSelectionUI sceneSelectionUI = FindObjectOfType<GONet.Sample.SceneSelectionUI>();
            if (sceneSelectionUI != null)
            {
                GONetLog.Info($"{LOGCODE}SceneSelectionUI exists - deferring to it for approval UI");
                // SceneSelectionUI will handle the approval dialog
                return;
            }

            // Fallback: Show GONetConnectionManager's basic approval UI
            if (sceneApprovalPanel == null)
            {
                BuildSceneApprovalUI();
            }

            if (sceneApprovalPanel != null && sceneApprovalMessageText != null)
            {
                sceneApprovalMessageText.text = $"Client (Authority {requestingAuthority}) requests scene:\n\n'{sceneName}'\n\nApprove this scene change?";
                sceneApprovalPanel.SetActive(true);
            }
        }

        /// <summary>
        /// Builds scene approval UI programmatically
        /// </summary>
        private void BuildSceneApprovalUI()
        {
            // Find existing canvas (from GONetConnectionWizard)
            Canvas canvas = FindObjectOfType<Canvas>();
            if (canvas == null)
            {
                GONetLog.Error($"{LOGCODE}Cannot build scene approval UI - no Canvas found");
                return;
            }

            // Create approval panel (centered, high z-order to render above everything)
            sceneApprovalPanel = new GameObject("SceneApprovalPanel");
            sceneApprovalPanel.transform.SetParent(canvas.transform, false);

            RectTransform approvalRect = sceneApprovalPanel.AddComponent<RectTransform>();
            approvalRect.anchorMin = new Vector2(0.5f, 0.5f);
            approvalRect.anchorMax = new Vector2(0.5f, 0.5f);
            approvalRect.pivot = new Vector2(0.5f, 0.5f);
            approvalRect.anchoredPosition = Vector2.zero;
            approvalRect.sizeDelta = new Vector2(700, 400);

            UnityEngine.UI.Image approvalImage = sceneApprovalPanel.AddComponent<UnityEngine.UI.Image>();
            approvalImage.color = new Color(0.15f, 0.15f, 0.2f, 0.95f);

            Canvas panelCanvas = sceneApprovalPanel.AddComponent<Canvas>();
            panelCanvas.overrideSorting = true;
            panelCanvas.sortingOrder = 500; // Above wizard (300) and status UI (100)

            // Title
            GameObject titleGO = new GameObject("Title");
            titleGO.transform.SetParent(sceneApprovalPanel.transform, false);
            RectTransform titleRect = titleGO.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.1f, 0.75f);
            titleRect.anchorMax = new Vector2(0.9f, 0.9f);
            titleRect.anchoredPosition = Vector2.zero;
            titleRect.sizeDelta = Vector2.zero;

            TextMeshProUGUI titleText = titleGO.AddComponent<TextMeshProUGUI>();
            titleText.text = "Scene Change Request";
            titleText.fontSize = 32;
            titleText.fontSizeMin = 18;
            titleText.fontSizeMax = 32;
            titleText.enableAutoSizing = true;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = Color.white;
            titleText.fontStyle = TMPro.FontStyles.Bold;

            // Message
            GameObject messageGO = new GameObject("Message");
            messageGO.transform.SetParent(sceneApprovalPanel.transform, false);
            RectTransform messageRect = messageGO.AddComponent<RectTransform>();
            messageRect.anchorMin = new Vector2(0.1f, 0.35f);
            messageRect.anchorMax = new Vector2(0.9f, 0.7f);
            messageRect.anchoredPosition = Vector2.zero;
            messageRect.sizeDelta = Vector2.zero;

            sceneApprovalMessageText = messageGO.AddComponent<TextMeshProUGUI>();
            sceneApprovalMessageText.fontSize = 24;
            sceneApprovalMessageText.fontSizeMin = 14;
            sceneApprovalMessageText.fontSizeMax = 24;
            sceneApprovalMessageText.enableAutoSizing = true;
            sceneApprovalMessageText.alignment = TextAlignmentOptions.Center;
            sceneApprovalMessageText.color = Color.white;

            // Approve button
            sceneApproveButton = CreateApprovalButton(sceneApprovalPanel.transform, "ApproveButton", "APPROVE",
                new Vector2(-180, 60), new Vector2(150, 50), new Color(0.2f, 0.7f, 0.2f, 1f), OnSceneApproved);

            // Deny button
            sceneDenyButton = CreateApprovalButton(sceneApprovalPanel.transform, "DenyButton", "DENY",
                new Vector2(30, 60), new Vector2(150, 50), new Color(0.8f, 0.2f, 0.2f, 1f), OnSceneDenied);

            // Start hidden
            sceneApprovalPanel.SetActive(false);

            GONetLog.Info($"{LOGCODE}Scene approval UI created");
        }

        /// <summary>
        /// Helper to create approval buttons
        /// </summary>
        private UnityEngine.UI.Button CreateApprovalButton(Transform parent, string name, string text, Vector2 position, Vector2 size, Color color, UnityEngine.Events.UnityAction onClick)
        {
            GameObject buttonGO = new GameObject(name);
            buttonGO.transform.SetParent(parent, false);

            RectTransform rect = buttonGO.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0);
            rect.anchorMax = new Vector2(0.5f, 0);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            UnityEngine.UI.Image image = buttonGO.AddComponent<UnityEngine.UI.Image>();
            image.color = color;

            UnityEngine.UI.Button button = buttonGO.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);

            // Button text
            GameObject textGO = new GameObject("Text");
            textGO.transform.SetParent(buttonGO.transform, false);

            RectTransform textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 24;
            tmp.fontSizeMin = 14;
            tmp.fontSizeMax = 24;
            tmp.enableAutoSizing = true;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.fontStyle = TMPro.FontStyles.Bold;

            return button;
        }

        /// <summary>
        /// Called when server approves scene change
        /// </summary>
        private void OnSceneApproved()
        {
            GONetLog.Info($"{LOGCODE}Server APPROVED scene change to '{pendingSceneName}'");

            if (sceneApprovalPanel != null)
                sceneApprovalPanel.SetActive(false);

            // Load the scene
            if (GONetMain.SceneManager != null)
            {
                GONetMain.SceneManager.LoadSceneFromBuildSettings(pendingSceneName, pendingSceneMode);
            }

            // Send approval response to client
            GONetMain.SceneManager.SendSceneRequestResponse(pendingRequestingAuthority, true, pendingSceneName, "");
        }

        /// <summary>
        /// Called when server denies scene change
        /// </summary>
        private void OnSceneDenied()
        {
            GONetLog.Info($"{LOGCODE}Server DENIED scene change to '{pendingSceneName}'");

            if (sceneApprovalPanel != null)
                sceneApprovalPanel.SetActive(false);

            // Send denial response to client
            GONetMain.SceneManager.SendSceneRequestResponse(pendingRequestingAuthority, false, pendingSceneName, "Server denied the request");
        }

        #endregion
    }
}
