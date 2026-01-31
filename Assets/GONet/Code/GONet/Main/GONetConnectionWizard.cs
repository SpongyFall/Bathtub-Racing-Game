/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 * This file provides the GONetConnectionWizard UI component for network setup.
 */
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace GONet
{
    /// <summary>
    /// Persistent UI wizard for GONet connection setup.
    /// Provides simple UI for Host/Client/Dedicated Server selection with optional advanced settings.
    /// Uses DontDestroyOnLoad to persist across scene changes until connected.
    /// </summary>
    [RequireComponent(typeof(GONetConnectionManager))]
    public class GONetConnectionWizard : MonoBehaviour
    {
        private static GONetConnectionWizard instance;
        private static readonly string LOGCODE = $"[{nameof(GONetConnectionWizard)}] ";

        #region UI Components

        private Canvas canvas;
        private GameObject mainPanel;
        private GameObject serverConfigOverlay;  // Full-screen overlay background
        private GameObject serverConfigPopup;    // Centered popup panel

        // Title
        private TextMeshProUGUI titleText;

        // Player Name
        private TMP_InputField playerNameInput;

        // Role Buttons
        private Button hostButton;
        private Button dedicatedServerButton;

        // Join Section
        private GameObject joinSection;
        private TMP_InputField ipAddressInput;
        private TMP_InputField portInput;
        private Button connectButton;

        // Server Configuration (shown for Host/Dedicated Server)
        private Toggle serverUseCustomTransportToggle;
        private TMP_Dropdown serverTransportDropdown;
        private TMP_InputField maxPlayersInput;
        private GONetConnectionRole? pendingRole;  // Role selected before showing config (nullable)

        // Client Configuration (shown in Join Section)
        private Toggle clientUseCustomTransportToggle;
        private TMP_Dropdown clientTransportDropdown;

        // Steamworks P2P Configuration (client-side, shown when Steamworks transport selected)
        private GameObject connectionMethodSection;      // Container for P2P vs Direct IP selection
        private Toggle useSteamP2PToggle;               // Radio button for Steam P2P
        private Toggle useDirectIPToggle;               // Radio button for Direct IP
        private GameObject steamP2PInputSection;        // Container for Host Steam ID input
        private TMP_InputField hostSteamIDInput;        // Host Steam ID input field
        private TextMeshProUGUI yourSteamIDLabel;       // Display user's own Steam ID
        private GameObject directIPInputSection;         // Container for IP+Port (existing inputs will be moved here visually)

        // Server Steamworks Info (shown when hosting with Steamworks)
        private GameObject serverSteamInfoSection;       // Container for server Steam ID display
        private TextMeshProUGUI serverSteamIDLabel;      // Display server's Steam ID for sharing

        // Server config popup buttons
        private Button startServerButton;                // Start Server button (for loading state)

        // Presets
        private TMP_Dropdown presetDropdown;
        private Button savePresetButton;
        private Button loadPresetButton;

        // LAN Discovery
        private Button scanLANButton;
        private GameObject lanServerList;
        private List<GameObject> lanServerEntries = new List<GameObject>();

        // Status
        private TextMeshProUGUI statusText;

        #endregion

        #region Configuration

        // Layout configuration (non-serialized for programmatic control during development)
        private int panelWidth = 1300;  // ~68% of 1920 reference width
        private int panelHeight = 1000;  // ~92% of 1080 reference height (increased to fit Steamworks P2P UI)
        private int padding = 25;

        [Header("Presets")]
        [SerializeField] private List<GONetConnectionPreset> builtInPresets = new List<GONetConnectionPreset>();

        #endregion

        private GONetConnectionManager connectionManager;
        private bool isConnected = false;
        private bool lastSteamSdrReadyResult = false;

        #region Unity Lifecycle

        private void Awake()
        {
            // Singleton pattern
            if (instance != null && instance != this)
            {
                GONetLog.Debug($"{LOGCODE}Duplicate instance detected - destroying self");
                Destroy(gameObject);
                return;
            }

            instance = this;

            // CRITICAL: Only call DontDestroyOnLoad during play mode (not during editor build process)
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }

            // Get connection manager
            connectionManager = GetComponent<GONetConnectionManager>();
            if (connectionManager == null)
            {
                GONetLog.Error($"{LOGCODE}GONetConnectionManager component not found!");
                return;
            }

            // Subscribe to connection events
            connectionManager.OnConnectionStarted += HandleConnectionStarted;
            connectionManager.OnConnectionSuccess += HandleConnectionSuccess;
            connectionManager.OnConnectionFailed += HandleConnectionFailed;
            connectionManager.OnDisconnected += HandleDisconnected;
            connectionManager.OnStatusChanged += HandleStatusChanged;

            BuildUI();
            EnsureEventSystemExists();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            EnsureStatusUIExists();
#endif
            EnsureHostMigrationPromptUIExists();

            LoadBuiltInPresets();
            // NOTE: Steam initialization is now triggered on-demand when Steamworks is selected
            // in the transport dropdown (see UpdateTransportSpecificUI and UpdateServerTransportUI).
            // This avoids initializing Steam unnecessarily when using NetcodeIO transport.
        }

        /// <summary>
        /// Initialize Steam API early so SDR has time to become ready before the user clicks Host.
        /// This prevents the timing issue where GONetSteamManager only gets created when GONetGlobal is instantiated.
        /// </summary>
        private void EnsureEarlySteamInitialization()
        {
            // Skip if Steam is already initialized (e.g., GONetSteamManager already exists)
            if (GONetSteamManager.IsInitialized)
            {
                GONetLog.Debug($"{LOGCODE}Steam already initialized, skipping early init");
                return;
            }

            try
            {
                // Ensure Steam manager exists so SteamAPI.RunCallbacks() is processed every frame.
                // This is REQUIRED for SDR to reach the ready state in a timely manner.
                GONetSteamManager.EnsureInstanceExists();

                if (!GONetSteamManager.IsInitialized)
                {
                    GONetLog.Warning($"{LOGCODE}Early Steam initialization failed - Steam client may not be running");
                    return;
                }

                GONetLog.Info($"{LOGCODE}Early Steam initialization successful");
                GONetLog.Info($"{LOGCODE}Initiated Steam Datagram Relay (SDR) early - should be ready by time user clicks Host");
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"{LOGCODE}Early Steam initialization exception: {ex.Message}");
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void EnsureStatusUIExists()
        {
            // Check if GONetStatusUI already exists (from sample scene or previous initialization)
            GONet.Sample.GONetStatusUI existingUI = FindObjectOfType<GONet.Sample.GONetStatusUI>();
            if (existingUI != null)
            {
                GONetLog.Debug($"{LOGCODE}GONetStatusUI already exists");
                return;
            }

            // Create status UI as a persistent GameObject
            GameObject statusUIObject = new GameObject("GONetStatusUI");
            statusUIObject.AddComponent<GONet.Sample.GONetStatusUI>();

            // CRITICAL: Only call DontDestroyOnLoad during play mode (not during editor build process)
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(statusUIObject);
            }

            GONetLog.Info($"{LOGCODE}Created GONetStatusUI");
        }
#endif

        private void EnsureHostMigrationPromptUIExists()
        {
            // Check if VoluntaryHostMigrationPromptUI already exists (from sample scene or previous initialization)
            GONet.Sample.VoluntaryHostMigrationPromptUI existingUI = FindObjectOfType<GONet.Sample.VoluntaryHostMigrationPromptUI>();
            if (existingUI != null)
            {
                GONetLog.Debug($"{LOGCODE}VoluntaryHostMigrationPromptUI already exists");
                return;
            }

            // Create host migration prompt UI as a persistent GameObject
            GameObject promptUIObject = new GameObject("VoluntaryHostMigrationPromptUI");
            promptUIObject.AddComponent<GONet.Sample.VoluntaryHostMigrationPromptUI>();

            // CRITICAL: Only call DontDestroyOnLoad during play mode (not during editor build process)
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(promptUIObject);
            }

            GONetLog.Info($"{LOGCODE}Created VoluntaryHostMigrationPromptUI");
        }

        private void OnDestroy()
        {
            if (connectionManager != null)
            {
                connectionManager.OnConnectionStarted -= HandleConnectionStarted;
                connectionManager.OnConnectionSuccess -= HandleConnectionSuccess;
                connectionManager.OnConnectionFailed -= HandleConnectionFailed;
                connectionManager.OnDisconnected -= HandleDisconnected;
                connectionManager.OnStatusChanged -= HandleStatusChanged;
            }
        }

        #endregion

        #region UI Building

        private void BuildUI()
        {
            // Get or create canvas
            canvas = gameObject.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = gameObject.AddComponent<Canvas>();
            }
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 300; // Ensure above status UI (sortingOrder = 100)

            // Get or create CanvasScaler
            CanvasScaler scaler = gameObject.GetComponent<CanvasScaler>();
            if (scaler == null)
            {
                scaler = gameObject.AddComponent<CanvasScaler>();
            }
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            // Get or create GraphicRaycaster
            if (gameObject.GetComponent<GraphicRaycaster>() == null)
            {
                gameObject.AddComponent<GraphicRaycaster>();
            }

            // Create main panel (centered with center pivot so it's truly centered on screen)
            mainPanel = CreatePanelWithPivot("MainPanel", new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(panelWidth, panelHeight), new Vector2(0.5f, 0.5f));

            // Elements inside panel use top-center anchor (0.5, 1), which anchors to PANEL'S TOP EDGE
            // Panel's top edge is at Y=0 relative to elements (not panelHeight/2!)
            float yPos = -padding;  // Start from top edge with padding (negative Y goes down)
            float contentWidth = panelWidth - (padding * 2);

            // Title
            titleText = CreateText(mainPanel.transform, "TitleText", "GONet Connection", 48,
                new Vector2(0, yPos), new Vector2(contentWidth, 60), FontStyles.Bold, TextAlignmentOptions.Center);
            yPos -= 80;

            // Player Name (label left, input right, both on same row)
            float labelWidth = 150;
            float inputWidth = contentWidth - labelWidth - padding;
            CreateLabel(mainPanel.transform, "PlayerNameLabel", "Player Name:",
                new Vector2(-contentWidth / 2 + labelWidth / 2, yPos), new Vector2(labelWidth, 40));
            playerNameInput = CreateInputField(mainPanel.transform, "PlayerNameInput", "Player",
                new Vector2(contentWidth / 2 - inputWidth / 2, yPos), new Vector2(inputWidth, 40));
            yPos -= 60;

            // Host Button
            hostButton = CreateButton(mainPanel.transform, "HostButton", "Host (Server + Client)",
                new Vector2(0, yPos), new Vector2(contentWidth, 50), OnHostButtonClicked);
            yPos -= 70;

            // Dedicated Server Button
            dedicatedServerButton = CreateButton(mainPanel.transform, "DedicatedServerButton", "Dedicated Server",
                new Vector2(0, yPos), new Vector2(contentWidth, 50), OnDedicatedServerButtonClicked);
            yPos -= 80;

            // Join Section
            yPos = BuildJoinSection(yPos);
            yPos -= 20;

            // Build Server Configuration popup (shown when Host/Dedicated Server clicked)
            BuildServerConfigPopup();

            // Presets Section
            yPos = BuildPresetsSection(yPos);
            yPos -= 20;

            // LAN Discovery Section
            yPos = BuildLANDiscoverySection(yPos);
            yPos -= 20;

            // Status Text (bottom of panel - anchored to top, so use negative Y offset)
            statusText = CreateText(mainPanel.transform, "StatusText", "Ready to connect", 36,
                new Vector2(0, -panelHeight + 40), new Vector2(contentWidth, 30), FontStyles.Normal, TextAlignmentOptions.Center);
            statusText.color = Color.green;
        }

        private float BuildJoinSection(float startY)
        {
            float sectionWidth = panelWidth - (padding * 2);
            float sectionPadding = 12;
            float sectionHeight = 450;  // Increased to accommodate Steamworks P2P UI elements

            // Join Game section with border (anchor to top-center of parent)
            GameObject sectionGO = new GameObject("JoinSection");
            sectionGO.transform.SetParent(mainPanel.transform, false);
            RectTransform sectionRect = sectionGO.AddComponent<RectTransform>();
            sectionRect.anchorMin = new Vector2(0.5f, 1);
            sectionRect.anchorMax = new Vector2(0.5f, 1);
            sectionRect.pivot = new Vector2(0.5f, 1);
            sectionRect.anchoredPosition = new Vector2(0, startY);
            sectionRect.sizeDelta = new Vector2(sectionWidth, sectionHeight);

            Image sectionImage = sectionGO.AddComponent<Image>();
            sectionImage.color = new Color(0.15f, 0.15f, 0.15f, 0.9f);
            joinSection = sectionGO;

            float yPosSection = -sectionPadding;

            // Title
            CreateLabel(joinSection.transform, "JoinLabel", "Join Game",
                new Vector2(0, yPosSection), new Vector2(200, 38));
            yPosSection -= 48;

            // Transport Configuration (client-side)
            // Use Custom Transport Toggle
            GameObject clientTransportToggleGO = new GameObject("ClientUseCustomTransportToggle");
            clientTransportToggleGO.transform.SetParent(joinSection.transform, false);

            RectTransform clientToggleRect = clientTransportToggleGO.AddComponent<RectTransform>();
            clientToggleRect.anchorMin = new Vector2(0.5f, 1);
            clientToggleRect.anchorMax = new Vector2(0.5f, 1);
            clientToggleRect.pivot = new Vector2(0.5f, 1);
            clientToggleRect.anchoredPosition = new Vector2(0, yPosSection);
            clientToggleRect.sizeDelta = new Vector2(350, 30);

            Toggle clientTransportToggle = clientTransportToggleGO.AddComponent<Toggle>();
            clientTransportToggle.isOn = true;  // Default: on (pluggable transport)

            // Toggle background
            GameObject clientToggleBgGO = new GameObject("Background");
            clientToggleBgGO.transform.SetParent(clientTransportToggleGO.transform, false);
            RectTransform clientToggleBgRect = clientToggleBgGO.AddComponent<RectTransform>();
            clientToggleBgRect.anchorMin = new Vector2(0, 0.5f);
            clientToggleBgRect.anchorMax = new Vector2(0, 0.5f);
            clientToggleBgRect.pivot = new Vector2(0, 0.5f);
            clientToggleBgRect.anchoredPosition = new Vector2(5, 0);
            clientToggleBgRect.sizeDelta = new Vector2(22, 22);
            Image clientToggleBgImage = clientToggleBgGO.AddComponent<Image>();
            clientToggleBgImage.color = Color.white;

            // Toggle checkmark
            GameObject clientCheckmarkGO = new GameObject("Checkmark");
            clientCheckmarkGO.transform.SetParent(clientToggleBgGO.transform, false);
            RectTransform clientCheckmarkRect = clientCheckmarkGO.AddComponent<RectTransform>();
            clientCheckmarkRect.anchorMin = Vector2.zero;
            clientCheckmarkRect.anchorMax = Vector2.one;
            clientCheckmarkRect.sizeDelta = new Vector2(-5, -5);
            Image clientCheckmarkImage = clientCheckmarkGO.AddComponent<Image>();
            clientCheckmarkImage.color = Color.green;

            clientTransportToggle.graphic = clientCheckmarkImage;

            // Toggle label
            GameObject clientToggleLabelGO = new GameObject("ClientTransportLabel");
            clientToggleLabelGO.transform.SetParent(clientTransportToggleGO.transform, false);
            RectTransform clientToggleLabelRect = clientToggleLabelGO.AddComponent<RectTransform>();
            clientToggleLabelRect.anchorMin = new Vector2(0, 0);
            clientToggleLabelRect.anchorMax = new Vector2(1, 1);
            clientToggleLabelRect.pivot = new Vector2(0, 0.5f);
            clientToggleLabelRect.anchoredPosition = new Vector2(30, 0);
            clientToggleLabelRect.sizeDelta = new Vector2(-30, 0);

            TextMeshProUGUI clientToggleLabelTMP = clientToggleLabelGO.AddComponent<TextMeshProUGUI>();
            clientToggleLabelTMP.text = "Use Custom Transport";
            clientToggleLabelTMP.fontSize = 20;
            clientToggleLabelTMP.color = Color.white;
            clientToggleLabelTMP.alignment = TextAlignmentOptions.MidlineLeft;

            yPosSection -= 42;

            // Transport dropdown
            CreateLabel(joinSection.transform, "ClientTransportLabel", "Transport:",
                new Vector2(-400, yPosSection), new Vector2(150, 35));

            TMP_Dropdown clientTransportDropdown = CreateDropdown(joinSection.transform, "ClientTransportDropdown",
                new Vector2(-100, yPosSection), new Vector2(300, 35));
            clientTransportDropdown.options.Clear();
            clientTransportDropdown.options.Add(new TMP_Dropdown.OptionData("NetcodeIO"));
            clientTransportDropdown.options.Add(new TMP_Dropdown.OptionData("Steamworks"));
            clientTransportDropdown.value = 0;
            clientTransportDropdown.RefreshShownValue();
            clientTransportDropdown.interactable = false;  // Disabled until toggle is checked

            // Wire up toggle to enable/disable dropdown
            clientTransportToggle.onValueChanged.AddListener((isOn) =>
            {
                if (clientTransportDropdown != null)
                {
                    clientTransportDropdown.interactable = isOn;
                }
            });

            // Store references for later use (client-specific fields)
            clientUseCustomTransportToggle = clientTransportToggle;
            this.clientTransportDropdown = clientTransportDropdown;

            // Wire up transport dropdown to show/hide Steamworks UI
            clientTransportDropdown.onValueChanged.AddListener((value) =>
            {
                // Only update if custom transport is enabled
                if (clientUseCustomTransportToggle.isOn)
                {
                    UpdateTransportSpecificUI(value);
                }
            });

            // Also wire up the toggle to show/hide transport-specific UI
            clientTransportToggle.onValueChanged.AddListener((isOn) =>
            {
                if (clientTransportDropdown != null)
                {
                    clientTransportDropdown.interactable = isOn;

                    // Show/hide Steamworks UI based on both toggle AND transport selection
                    if (isOn && clientTransportDropdown.value == 1) // 1 = Steamworks
                    {
                        UpdateTransportSpecificUI(clientTransportDropdown.value);
                    }
                    else
                    {
                        // Hide all Steamworks UI when toggle is off or NetcodeIO selected
                        if (connectionMethodSection != null)
                        {
                            connectionMethodSection.SetActive(false);
                        }
                        if (steamP2PInputSection != null)
                        {
                            steamP2PInputSection.SetActive(false);
                        }
                    }
                }
            });

            yPosSection -= 48;

            // Connection Method Section (shown only when Steamworks selected)
            GameObject connectionMethodGO = new GameObject("ConnectionMethodSection");
            connectionMethodGO.transform.SetParent(joinSection.transform, false);
            RectTransform connectionMethodRect = connectionMethodGO.AddComponent<RectTransform>();
            connectionMethodRect.anchorMin = new Vector2(0.5f, 1);
            connectionMethodRect.anchorMax = new Vector2(0.5f, 1);
            connectionMethodRect.pivot = new Vector2(0.5f, 1);
            connectionMethodRect.anchoredPosition = new Vector2(0, yPosSection);
            connectionMethodRect.sizeDelta = new Vector2(sectionWidth - 30, 80);
            connectionMethodSection = connectionMethodGO;
            connectionMethodSection.SetActive(false);  // Hidden by default

            float methodYPos = -5;

            // Connection Method label
            CreateLabel(connectionMethodSection.transform, "ConnectionMethodLabel", "Connection Method:",
                new Vector2(0, methodYPos), new Vector2(200, 30));
            methodYPos -= 38;

            // Radio button layout (horizontal)
            float radioSpacing = 250;
            float radioStartX = -radioSpacing / 2;

            // Steam P2P Radio Button
            GameObject steamP2PToggleGO = new GameObject("SteamP2PToggle");
            steamP2PToggleGO.transform.SetParent(connectionMethodSection.transform, false);
            RectTransform steamP2PRect = steamP2PToggleGO.AddComponent<RectTransform>();
            steamP2PRect.anchorMin = new Vector2(0.5f, 1);
            steamP2PRect.anchorMax = new Vector2(0.5f, 1);
            steamP2PRect.pivot = new Vector2(0, 0.5f);
            steamP2PRect.anchoredPosition = new Vector2(radioStartX, methodYPos);
            steamP2PRect.sizeDelta = new Vector2(230, 30);

            useSteamP2PToggle = steamP2PToggleGO.AddComponent<Toggle>();
            useSteamP2PToggle.isOn = false;

            // Toggle background
            GameObject steamP2PBgGO = new GameObject("Background");
            steamP2PBgGO.transform.SetParent(steamP2PToggleGO.transform, false);
            RectTransform steamP2PBgRect = steamP2PBgGO.AddComponent<RectTransform>();
            steamP2PBgRect.anchorMin = new Vector2(0, 0.5f);
            steamP2PBgRect.anchorMax = new Vector2(0, 0.5f);
            steamP2PBgRect.pivot = new Vector2(0, 0.5f);
            steamP2PBgRect.anchoredPosition = new Vector2(5, 0);
            steamP2PBgRect.sizeDelta = new Vector2(22, 22);
            Image steamP2PBgImage = steamP2PBgGO.AddComponent<Image>();
            steamP2PBgImage.color = Color.white;

            // Toggle checkmark
            GameObject steamP2PCheckGO = new GameObject("Checkmark");
            steamP2PCheckGO.transform.SetParent(steamP2PBgGO.transform, false);
            RectTransform steamP2PCheckRect = steamP2PCheckGO.AddComponent<RectTransform>();
            steamP2PCheckRect.anchorMin = Vector2.zero;
            steamP2PCheckRect.anchorMax = Vector2.one;
            steamP2PCheckRect.sizeDelta = new Vector2(-5, -5);
            Image steamP2PCheckImage = steamP2PCheckGO.AddComponent<Image>();
            steamP2PCheckImage.color = Color.green;
            useSteamP2PToggle.graphic = steamP2PCheckImage;

            // Label
            GameObject steamP2PLabelGO = new GameObject("Label");
            steamP2PLabelGO.transform.SetParent(steamP2PToggleGO.transform, false);
            RectTransform steamP2PLabelRect = steamP2PLabelGO.AddComponent<RectTransform>();
            steamP2PLabelRect.anchorMin = new Vector2(0, 0);
            steamP2PLabelRect.anchorMax = new Vector2(1, 1);
            steamP2PLabelRect.anchoredPosition = new Vector2(30, 0);
            steamP2PLabelRect.sizeDelta = new Vector2(-30, 0);
            TextMeshProUGUI steamP2PLabelText = steamP2PLabelGO.AddComponent<TextMeshProUGUI>();
            steamP2PLabelText.text = "Steam P2P";
            steamP2PLabelText.fontSize = 20;
            steamP2PLabelText.color = Color.white;
            steamP2PLabelText.alignment = TextAlignmentOptions.MidlineLeft;

            // Direct IP Radio Button
            GameObject directIPToggleGO = new GameObject("DirectIPToggle");
            directIPToggleGO.transform.SetParent(connectionMethodSection.transform, false);
            RectTransform directIPRect = directIPToggleGO.AddComponent<RectTransform>();
            directIPRect.anchorMin = new Vector2(0.5f, 1);
            directIPRect.anchorMax = new Vector2(0.5f, 1);
            directIPRect.pivot = new Vector2(0, 0.5f);
            directIPRect.anchoredPosition = new Vector2(radioStartX + radioSpacing, methodYPos);
            directIPRect.sizeDelta = new Vector2(280, 30);

            useDirectIPToggle = directIPToggleGO.AddComponent<Toggle>();
            useDirectIPToggle.isOn = true;  // Default selection

            // Toggle background
            GameObject directIPBgGO = new GameObject("Background");
            directIPBgGO.transform.SetParent(directIPToggleGO.transform, false);
            RectTransform directIPBgRect = directIPBgGO.AddComponent<RectTransform>();
            directIPBgRect.anchorMin = new Vector2(0, 0.5f);
            directIPBgRect.anchorMax = new Vector2(0, 0.5f);
            directIPBgRect.pivot = new Vector2(0, 0.5f);
            directIPBgRect.anchoredPosition = new Vector2(5, 0);
            directIPBgRect.sizeDelta = new Vector2(22, 22);
            Image directIPBgImage = directIPBgGO.AddComponent<Image>();
            directIPBgImage.color = Color.white;

            // Toggle checkmark
            GameObject directIPCheckGO = new GameObject("Checkmark");
            directIPCheckGO.transform.SetParent(directIPBgGO.transform, false);
            RectTransform directIPCheckRect = directIPCheckGO.AddComponent<RectTransform>();
            directIPCheckRect.anchorMin = Vector2.zero;
            directIPCheckRect.anchorMax = Vector2.one;
            directIPCheckRect.sizeDelta = new Vector2(-5, -5);
            Image directIPCheckImage = directIPCheckGO.AddComponent<Image>();
            directIPCheckImage.color = Color.green;
            useDirectIPToggle.graphic = directIPCheckImage;

            // Label
            GameObject directIPLabelGO = new GameObject("Label");
            directIPLabelGO.transform.SetParent(directIPToggleGO.transform, false);
            RectTransform directIPLabelRect = directIPLabelGO.AddComponent<RectTransform>();
            directIPLabelRect.anchorMin = new Vector2(0, 0);
            directIPLabelRect.anchorMax = new Vector2(1, 1);
            directIPLabelRect.anchoredPosition = new Vector2(30, 0);
            directIPLabelRect.sizeDelta = new Vector2(-30, 0);
            TextMeshProUGUI directIPLabelText = directIPLabelGO.AddComponent<TextMeshProUGUI>();
            directIPLabelText.text = "Direct IP (Local Testing)";
            directIPLabelText.fontSize = 20;
            directIPLabelText.color = Color.white;
            directIPLabelText.alignment = TextAlignmentOptions.MidlineLeft;

            // Make toggles exclusive (radio button behavior)
            useSteamP2PToggle.onValueChanged.AddListener((isOn) =>
            {
                if (isOn && useDirectIPToggle.isOn)
                {
                    useDirectIPToggle.isOn = false;
                }
                UpdateConnectionMethodUI();
            });

            useDirectIPToggle.onValueChanged.AddListener((isOn) =>
            {
                if (isOn && useSteamP2PToggle.isOn)
                {
                    useSteamP2PToggle.isOn = false;
                }
                UpdateConnectionMethodUI();
            });

            yPosSection -= 85;

            // Steam P2P Input Section (Host Steam ID + Your Steam ID)
            GameObject steamP2PInputGO = new GameObject("SteamP2PInputSection");
            steamP2PInputGO.transform.SetParent(joinSection.transform, false);
            RectTransform steamP2PInputRect = steamP2PInputGO.AddComponent<RectTransform>();
            steamP2PInputRect.anchorMin = new Vector2(0.5f, 1);
            steamP2PInputRect.anchorMax = new Vector2(0.5f, 1);
            steamP2PInputRect.pivot = new Vector2(0.5f, 1);
            steamP2PInputRect.anchoredPosition = new Vector2(0, yPosSection);
            steamP2PInputRect.sizeDelta = new Vector2(sectionWidth - 30, 90);
            steamP2PInputSection = steamP2PInputGO;
            steamP2PInputSection.SetActive(false);  // Hidden by default

            float steamInputYPos = -5;

            // Host Steam ID input (label + input field)
            float steamLabelWidth = 180;
            float steamInputWidth = sectionWidth - 230 - steamLabelWidth;
            CreateLabel(steamP2PInputSection.transform, "HostSteamIDLabel", "Host Steam ID or Share Code:",
                new Vector2(-sectionWidth / 2 + 30 + steamLabelWidth / 2, steamInputYPos), new Vector2(steamLabelWidth, 38));
            hostSteamIDInput = CreateInputField(steamP2PInputSection.transform, "HostSteamIDInput", "Enter Steam ID or Share Code",
                new Vector2(sectionWidth / 2 - 30 - steamInputWidth / 2, steamInputYPos), new Vector2(steamInputWidth, 38));
            hostSteamIDInput.text = "";  // Clear default placeholder text
            steamInputYPos -= 48;

            // Your Steam ID display (read-only label)
            yourSteamIDLabel = CreateLabel(steamP2PInputSection.transform, "YourSteamIDLabel", "Your Steam ID: (Retrieving...)",
                new Vector2(0, steamInputYPos), new Vector2(sectionWidth - 60, 30));
            yourSteamIDLabel.alignment = TextAlignmentOptions.MidlineLeft;
            yourSteamIDLabel.fontSize = 18;

            // Direct IP Input Section (IP address only - port is separate below)
            GameObject directIPInputGO = new GameObject("DirectIPInputSection");
            directIPInputGO.transform.SetParent(joinSection.transform, false);
            RectTransform directIPInputRect = directIPInputGO.AddComponent<RectTransform>();
            directIPInputRect.anchorMin = new Vector2(0.5f, 1);
            directIPInputRect.anchorMax = new Vector2(0.5f, 1);
            directIPInputRect.pivot = new Vector2(0.5f, 1);
            directIPInputRect.anchoredPosition = new Vector2(0, yPosSection);
            directIPInputRect.sizeDelta = new Vector2(sectionWidth - 30, 50);
            directIPInputSection = directIPInputGO;
            directIPInputSection.SetActive(true);  // Visible by default (Direct IP is default)

            // IP address input (full width)
            float ipLabelWidth = 50;
            float ipInputWidth = sectionWidth - 90;
            float ipYPos = -5;
            float ipLabelX = -(sectionWidth - 30) / 2 + ipLabelWidth / 2;
            float ipInputX = ipLabelX + ipLabelWidth / 2 + ipInputWidth / 2;

            CreateLabel(directIPInputSection.transform, "IPLabel", "IP:",
                new Vector2(ipLabelX, ipYPos), new Vector2(ipLabelWidth, 38));
            ipAddressInput = CreateInputField(directIPInputSection.transform, "IPInput", "127.0.0.1",
                new Vector2(ipInputX, ipYPos), new Vector2(ipInputWidth, 38));

            // Adjust spacing to accommodate Steam P2P warning label (which extends ~90px down)
            // Steam P2P section height: 90px (Host Steam ID input + Your Steam ID warning label)
            // Direct IP section height: 50px (just IP input)
            // Use max height to ensure port section is below both
            yPosSection -= 100;

            // Port Input Section (ALWAYS VISIBLE - shared by Steam P2P and Direct IP)
            GameObject portInputGO = new GameObject("PortInputSection");
            portInputGO.transform.SetParent(joinSection.transform, false);
            RectTransform portInputRect = portInputGO.AddComponent<RectTransform>();
            portInputRect.anchorMin = new Vector2(0.5f, 1);
            portInputRect.anchorMax = new Vector2(0.5f, 1);
            portInputRect.pivot = new Vector2(0.5f, 1);
            portInputRect.anchoredPosition = new Vector2(0, yPosSection);
            portInputRect.sizeDelta = new Vector2(sectionWidth - 30, 50);
            // Always visible - no SetActive(false)

            // Port input (centered, smaller width)
            float portLabelWidth = 60;
            float portInputWidth = 120;
            float portYPos = -5;
            float portLabelX = -portLabelWidth / 2 - portInputWidth / 2 - 5;
            float portInputX = portInputWidth / 2 + 5;

            CreateLabel(portInputGO.transform, "PortLabel", "Port:",
                new Vector2(portLabelX, portYPos), new Vector2(portLabelWidth, 38));
            portInput = CreateInputField(portInputGO.transform, "PortInput", "7777",
                new Vector2(portInputX, portYPos), new Vector2(portInputWidth, 38));

            yPosSection -= 60;

            // Connect button
            connectButton = CreateButton(joinSection.transform, "ConnectButton", "Connect",
                new Vector2(0, yPosSection), new Vector2(sectionWidth - 30, 42), OnConnectButtonClicked);

            return startY - (sectionHeight + 10);
        }


        private float BuildPresetsSection(float startY)
        {
            float contentWidth = panelWidth - (padding * 2);
            float labelWidth = 200;
            float dropdownWidth = 250;
            float buttonWidth = 80;
            float spacing = 10;

            // Horizontal layout: [Label] [Dropdown] [Save] [Load]
            float totalWidth = labelWidth + dropdownWidth + (buttonWidth * 2) + (spacing * 3);
            float startX = -totalWidth / 2;

            // Label
            CreateLabel(mainPanel.transform, "PresetsLabel", "Connection Presets:",
                new Vector2(startX + labelWidth / 2, startY), new Vector2(labelWidth, 40));

            // Dropdown (manually created to match other elements)
            GameObject dropdownGO = new GameObject("PresetDropdown");
            dropdownGO.transform.SetParent(mainPanel.transform, false);

            RectTransform dropdownRect = dropdownGO.AddComponent<RectTransform>();
            dropdownRect.anchorMin = new Vector2(0.5f, 1);  // Top-center anchor
            dropdownRect.anchorMax = new Vector2(0.5f, 1);
            dropdownRect.pivot = new Vector2(0.5f, 1);  // Top-center pivot
            dropdownRect.anchoredPosition = new Vector2(startX + labelWidth + spacing + dropdownWidth / 2, startY);
            dropdownRect.sizeDelta = new Vector2(dropdownWidth, 40);

            Image dropdownImage = dropdownGO.AddComponent<Image>();
            dropdownImage.color = Color.white;

            presetDropdown = dropdownGO.AddComponent<TMP_Dropdown>();
            presetDropdown.options.Add(new TMP_Dropdown.OptionData("None"));

            // Save/Load buttons (proper centering)
            float buttonsX = startX + labelWidth + spacing + dropdownWidth + spacing;
            savePresetButton = CreateButton(mainPanel.transform, "SavePresetButton", "Save",
                new Vector2(buttonsX + buttonWidth / 2, startY), new Vector2(buttonWidth, 40), OnSavePresetClicked);

            loadPresetButton = CreateButton(mainPanel.transform, "LoadPresetButton", "Load",
                new Vector2(buttonsX + buttonWidth + spacing + buttonWidth / 2, startY), new Vector2(buttonWidth, 40), OnLoadPresetClicked);

            return startY - 55;
        }

        private float BuildLANDiscoverySection(float startY)
        {
            float contentWidth = panelWidth - (padding * 2);

            scanLANButton = CreateButton(mainPanel.transform, "ScanLANButton", "🔍 Scan LAN Servers",
                new Vector2(0, startY), new Vector2(contentWidth, 40), OnScanLANClicked);

            // LAN server list container (scrollable) - position below button
            GameObject listGO = new GameObject("LANServerList");
            listGO.transform.SetParent(mainPanel.transform, false);
            RectTransform listRect = listGO.AddComponent<RectTransform>();
            listRect.anchorMin = new Vector2(0.5f, 1);
            listRect.anchorMax = new Vector2(0.5f, 1);
            listRect.pivot = new Vector2(0.5f, 1);
            listRect.anchoredPosition = new Vector2(0, startY - 50);
            listRect.sizeDelta = new Vector2(contentWidth, 100);

            Image listImage = listGO.AddComponent<Image>();
            listImage.color = new Color(0.05f, 0.05f, 0.05f, 0.9f);
            lanServerList = listGO;

            return startY - 160;
        }

        #endregion

        #region UI Helper Methods

        private void BuildServerConfigPopup()
        {
            // Create full-screen overlay background
            GameObject overlayGO = new GameObject("ServerConfigOverlay");
            overlayGO.transform.SetParent(canvas.transform, false);

            RectTransform overlayRect = overlayGO.AddComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.sizeDelta = Vector2.zero;

            Image overlayImage = overlayGO.AddComponent<Image>();
            overlayImage.color = new Color(0, 0, 0, 0.8f);  // Semi-transparent black

            serverConfigOverlay = overlayGO;
            serverConfigOverlay.SetActive(false);  // Hidden by default

            // Create centered popup panel (800x500 - smaller since less content)
            GameObject popupGO = new GameObject("ServerConfigPopup");
            popupGO.transform.SetParent(overlayGO.transform, false);

            RectTransform popupRect = popupGO.AddComponent<RectTransform>();
            popupRect.anchorMin = new Vector2(0.5f, 0.5f);
            popupRect.anchorMax = new Vector2(0.5f, 0.5f);
            popupRect.pivot = new Vector2(0.5f, 0.5f);
            popupRect.anchoredPosition = Vector2.zero;
            popupRect.sizeDelta = new Vector2(800, 500);

            Image popupImage = popupGO.AddComponent<Image>();
            popupImage.color = new Color(0.15f, 0.15f, 0.15f, 1f);  // Dark gray background

            serverConfigPopup = popupGO;

            // Title
            CreateText(popupGO.transform, "ServerConfigTitle", "Server Configuration", 36,
                new Vector2(0, -30), new Vector2(700, 50), FontStyles.Bold, TextAlignmentOptions.Center);

            // Cancel button (top-right corner)
            GameObject cancelButtonGO = new GameObject("CancelButton");
            cancelButtonGO.transform.SetParent(popupGO.transform, false);

            RectTransform cancelRect = cancelButtonGO.AddComponent<RectTransform>();
            cancelRect.anchorMin = new Vector2(1, 1);
            cancelRect.anchorMax = new Vector2(1, 1);
            cancelRect.pivot = new Vector2(1, 1);
            cancelRect.anchoredPosition = new Vector2(-20, -20);
            cancelRect.sizeDelta = new Vector2(40, 40);

            Image cancelImage = cancelButtonGO.AddComponent<Image>();
            cancelImage.color = new Color(0.8f, 0.2f, 0.2f, 1f);  // Red background

            Button cancelButton = cancelButtonGO.AddComponent<Button>();
            cancelButton.onClick.AddListener(OnCancelServerConfig);

            // Cancel button text (X)
            CreateText(cancelButtonGO.transform, "CancelText", "X", 24,
                Vector2.zero, new Vector2(40, 40), FontStyles.Bold, TextAlignmentOptions.Center);

            // Server configuration content
            float yPos = -100;

            // Server Steam Information Section (shown when Steamworks selected)
            GameObject serverSteamInfoGO = new GameObject("ServerSteamInfoSection");
            serverSteamInfoGO.transform.SetParent(popupGO.transform, false);
            RectTransform serverSteamInfoRect = serverSteamInfoGO.AddComponent<RectTransform>();
            serverSteamInfoRect.anchorMin = new Vector2(0.5f, 1);
            serverSteamInfoRect.anchorMax = new Vector2(0.5f, 1);
            serverSteamInfoRect.pivot = new Vector2(0.5f, 1);
            serverSteamInfoRect.anchoredPosition = new Vector2(0, yPos);
            serverSteamInfoRect.sizeDelta = new Vector2(700, 100);
            serverSteamInfoSection = serverSteamInfoGO;
            serverSteamInfoSection.SetActive(false);  // Hidden by default, not used in pre-connection popup

            yPos -= 110;

            // Use Custom Transport Toggle
            GameObject useTransportToggleGO = new GameObject("UseCustomTransportToggle");
            useTransportToggleGO.transform.SetParent(popupGO.transform, false);

            RectTransform useTransportRect = useTransportToggleGO.AddComponent<RectTransform>();
            useTransportRect.anchorMin = new Vector2(0.5f, 1);
            useTransportRect.anchorMax = new Vector2(0.5f, 1);
            useTransportRect.pivot = new Vector2(0.5f, 1);
            useTransportRect.anchoredPosition = new Vector2(0, yPos);
            useTransportRect.sizeDelta = new Vector2(400, 35);

            serverUseCustomTransportToggle = useTransportToggleGO.AddComponent<Toggle>();
            serverUseCustomTransportToggle.isOn = true;  // Default: on (pluggable transport)
            serverUseCustomTransportToggle.onValueChanged.AddListener(OnUseCustomTransportChanged);

            // Toggle background
            GameObject toggleBgGO = new GameObject("Background");
            toggleBgGO.transform.SetParent(useTransportToggleGO.transform, false);
            RectTransform toggleBgRect = toggleBgGO.AddComponent<RectTransform>();
            toggleBgRect.anchorMin = new Vector2(0, 0.5f);
            toggleBgRect.anchorMax = new Vector2(0, 0.5f);
            toggleBgRect.pivot = new Vector2(0, 0.5f);
            toggleBgRect.anchoredPosition = new Vector2(5, 0);
            toggleBgRect.sizeDelta = new Vector2(25, 25);
            Image toggleBgImage = toggleBgGO.AddComponent<Image>();
            toggleBgImage.color = Color.white;

            // Toggle checkmark
            GameObject checkmarkGO = new GameObject("Checkmark");
            checkmarkGO.transform.SetParent(toggleBgGO.transform, false);
            RectTransform checkmarkRect = checkmarkGO.AddComponent<RectTransform>();
            checkmarkRect.anchorMin = Vector2.zero;
            checkmarkRect.anchorMax = Vector2.one;
            checkmarkRect.sizeDelta = new Vector2(-6, -6);
            Image checkmarkImage = checkmarkGO.AddComponent<Image>();
            checkmarkImage.color = Color.green;

            serverUseCustomTransportToggle.graphic = checkmarkImage;

            // Toggle label
            GameObject toggleLabelGO = new GameObject("UseTransportLabel");
            toggleLabelGO.transform.SetParent(useTransportToggleGO.transform, false);
            RectTransform toggleLabelRect = toggleLabelGO.AddComponent<RectTransform>();
            toggleLabelRect.anchorMin = new Vector2(0, 0);
            toggleLabelRect.anchorMax = new Vector2(1, 1);
            toggleLabelRect.pivot = new Vector2(0, 0.5f);
            toggleLabelRect.anchoredPosition = new Vector2(35, 0);
            toggleLabelRect.sizeDelta = new Vector2(-35, 0);

            TextMeshProUGUI toggleLabelTMP = toggleLabelGO.AddComponent<TextMeshProUGUI>();
            toggleLabelTMP.text = "Use Custom Transport";
            toggleLabelTMP.fontSize = 24;
            toggleLabelTMP.color = Color.white;
            toggleLabelTMP.alignment = TextAlignmentOptions.MidlineLeft;

            yPos -= 70;

            // Transport Selection Dropdown
            CreateText(popupGO.transform, "TransportLabel", "Transport Type:", 24,
                new Vector2(-300, yPos), new Vector2(200, 40));

            serverTransportDropdown = CreateDropdown(popupGO.transform, "TransportDropdown",
                new Vector2(50, yPos), new Vector2(300, 40));
            serverTransportDropdown.options.Clear();
            // Populate from GONetTransportType enum
            serverTransportDropdown.options.Add(new TMP_Dropdown.OptionData("NetcodeIO"));
            serverTransportDropdown.options.Add(new TMP_Dropdown.OptionData("Steamworks"));
            serverTransportDropdown.value = 0;
            serverTransportDropdown.RefreshShownValue();
            serverTransportDropdown.interactable = false;  // Disabled until toggle is checked

            // Wire up transport dropdown to show/hide server Steam info section
            serverTransportDropdown.onValueChanged.AddListener((value) =>
            {
                // Only update if custom transport is enabled
                if (serverUseCustomTransportToggle.isOn)
                {
                    UpdateServerTransportUI(value);
                }
            });

            yPos -= 80;

            // Max Players
            CreateText(popupGO.transform, "MaxPlayersLabel", "Max Players:", 24,
                new Vector2(-300, yPos), new Vector2(200, 40));
            maxPlayersInput = CreateInputField(popupGO.transform, "MaxPlayersInput", "32",
                new Vector2(50, yPos), new Vector2(150, 40));
            maxPlayersInput.text = "32";

            yPos -= 100;

            // Start Server Button (green, prominent, wider to fit "Starting Server..." with animated dots)
            startServerButton = CreateButton(popupGO.transform, "StartServerButton", "Start Server",
                new Vector2(-140, yPos), new Vector2(280, 50), OnStartServerButtonClicked);
            startServerButton.GetComponent<Image>().color = new Color(0.2f, 0.7f, 0.2f);  // Green

            // Cancel Button (gray)
            Button cancelBtn = CreateButton(popupGO.transform, "CancelServerButton", "Cancel",
                new Vector2(140, yPos), new Vector2(200, 50), OnCancelServerConfig);
            cancelBtn.GetComponent<Image>().color = new Color(0.4f, 0.4f, 0.4f);  // Gray
        }

        private void OnCancelServerConfig()
        {
            if (serverConfigOverlay != null)
            {
                serverConfigOverlay.SetActive(false);
            }

            pendingRole = null;  // Clear pending role
        }

        private void OnUseCustomTransportChanged(bool isOn)
        {
            if (serverTransportDropdown != null)
            {
                serverTransportDropdown.interactable = isOn;  // Enable dropdown only when toggle is checked

                // Show/hide server Steam info based on both toggle AND transport selection
                if (isOn && serverTransportDropdown.value == 1) // 1 = Steamworks
                {
                    UpdateServerTransportUI(serverTransportDropdown.value);
                }
                else
                {
                    // Hide server Steam info when toggle is off or NetcodeIO selected
                    if (serverSteamInfoSection != null)
                    {
                        serverSteamInfoSection.SetActive(false);
                    }
                }
            }
        }

        private void OnStartServerButtonClicked()
        {
            // Start coroutine to allow UI to update before blocking operations
            StartCoroutine(StartServerWithLoadingState());
        }

        private System.Collections.IEnumerator StartServerWithLoadingState()
        {
            // Start loading state immediately (visual feedback before blocking operations)
            StartServerLoadingState();

            // CRITICAL: Yield one frame to allow UI updates to render
            yield return null;

            // If using Steamworks transport, wait for Steam Datagram Relay (SDR) to initialize
            // This prevents blocking the main thread with Thread.Sleep() calls
            GONetTransportType transportType = (GONetTransportType)serverTransportDropdown.value;
            if (transportType == GONetTransportType.Steamworks)
            {
                // Update status to show we're waiting for Steam
                if (statusText != null)
                {
                    statusText.text = "Waiting for Steam Datagram Relay...";
                }

                // Wait for SDR to be ready (non-blocking, allows coroutines to animate)
                yield return StartCoroutine(WaitForSteamSDRReady());
            }

            // Keep popup visible during loading (so user sees the "Starting Server..." feedback)
            // Popup will hide when HandleConnectionSuccess is called after scene transition

            // Apply UI configuration to preset (includes transport settings)
            // This will be applied to GONetGlobal BEFORE instantiation in GONetConnectionManager
            if (pendingRole == GONetConnectionRole.Host)
            {
                ApplyUIConfigurationToPreset(GONetConnectionRole.Host);
                connectionManager.Connect();  // ApplyPresetToGONetGlobal happens BEFORE instantiation
            }
            else if (pendingRole == GONetConnectionRole.DedicatedServer)
            {
                ApplyUIConfigurationToPreset(GONetConnectionRole.DedicatedServer);
                connectionManager.Connect();  // ApplyPresetToGONetGlobal happens BEFORE instantiation
            }

            pendingRole = null;
        }

        private System.Collections.IEnumerator WaitForSteamSDRReady()
        {
            const float CHECK_INTERVAL = 0.1f; // Check every 100ms (same as SteamworksTransport)
            const int MAX_ATTEMPTS = 100; // Maximum 100 attempts (10 seconds total)
            int attempts = 0;

            lastSteamSdrReadyResult = false;
            GONetLog.Info($"{LOGCODE}Waiting for Steam Datagram Relay (SDR) to initialize...");

            // First, ensure Steam is initialized (early init should have happened in Awake, but verify)
            if (!GONetSteamManager.IsInitialized)
            {
                // Force creation of the manager so callbacks are processed and Init() can be attempted.
                GONetSteamManager.EnsureInstanceExists();

                if (!GONetSteamManager.IsInitialized)
                {
                    GONetLog.Info($"{LOGCODE}Steam not yet initialized, waiting...");
                    int steamInitAttempts = 0;
                    while (!GONetSteamManager.IsInitialized && steamInitAttempts < 50) // Wait up to 5 seconds
                    {
                        steamInitAttempts++;
                        yield return new WaitForSeconds(CHECK_INTERVAL);
                    }
                }

                if (!GONetSteamManager.IsInitialized)
                {
                    GONetLog.Warning($"{LOGCODE}Steam API did not initialize in time. SDR will not be available.");
                    if (statusText != null)
                    {
                        statusText.text = "Steam not initialized, falling back to IP...";
                    }
                    yield break;
                }
                GONetLog.Info($"{LOGCODE}Steam initialized, now checking SDR status...");
            }


            while (attempts < MAX_ATTEMPTS)
            {
                // Ensure Steam callbacks are processed while we poll SDR readiness.
                // Without this, SDR may never progress beyond "Attempting"/"Waiting".
                try
                {
                    Steamworks.SteamAPI.RunCallbacks();
                }
                catch
                {
                    // Ignore - Steam may still be initializing or platform not supported.
                }

                // Check SDR status (non-blocking)
                Steamworks.SteamRelayNetworkStatus_t status;
                Steamworks.ESteamNetworkingAvailability sdrAvailability =
                    Steamworks.SteamNetworkingUtils.GetRelayNetworkStatus(out status);

                if (sdrAvailability == Steamworks.ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current)
                {
                    GONetLog.Info($"{LOGCODE}SDR is ready! (checked {attempts} times, ~{attempts * CHECK_INTERVAL * 1000}ms)");
                    if (statusText != null)
                    {
                        statusText.text = "Steam Datagram Relay ready!";
                    }
                    lastSteamSdrReadyResult = true;
                    yield break; // SDR ready, exit coroutine
                }

                // Log every 10 attempts (~1 second)
                if (attempts % 10 == 0)
                {
                    GONetLog.Info($"{LOGCODE}SDR Status: {sdrAvailability} (Avail={status.m_eAvail}, NetworkConfig={status.m_eAvailNetworkConfig}, AnyRelay={status.m_eAvailAnyRelay}) - waiting... (attempt {attempts}/{MAX_ATTEMPTS})");
                }

                attempts++;

                // CRITICAL: Use WaitForSeconds instead of Thread.Sleep to avoid blocking main thread
                // This allows Unity's coroutine system and UI to continue updating
                yield return new WaitForSeconds(CHECK_INTERVAL);
            }

            // Timeout - log warning but continue anyway (will fall back to IP socket)
            Steamworks.SteamRelayNetworkStatus_t finalStatus;
            Steamworks.ESteamNetworkingAvailability finalAvailability =
                Steamworks.SteamNetworkingUtils.GetRelayNetworkStatus(out finalStatus);
            GONetLog.Warning($"{LOGCODE}SDR did not reach ready state after {attempts} attempts (~{attempts * CHECK_INTERVAL}s). P2P socket may fail, but will attempt anyway and fall back to IP if needed.");

            if (statusText != null)
            {
                statusText.text = "SDR timeout, falling back to IP...";
            }
        }

        private void StartServerLoadingState()
        {
            // Disable button to prevent double-clicks
            if (startServerButton != null)
            {
                startServerButton.interactable = false;
            }

            // Update status text
            if (statusText != null)
            {
                statusText.text = "Initializing server...";
                statusText.color = Color.yellow;
            }

            // Start animated dots coroutine for button text
            if (startServerButton != null)
            {
                StartCoroutine(AnimateButtonLoadingDots());
            }
        }

        private System.Collections.IEnumerator AnimateButtonLoadingDots()
        {
            if (startServerButton == null) yield break;

            const string BASE_TEXT = "Starting Server";
            int dotCount = 0;
            TextMeshProUGUI buttonText = startServerButton.GetComponentInChildren<TextMeshProUGUI>();

            if (buttonText == null) yield break;

            // Animate until button becomes interactable again (or is destroyed)
            while (startServerButton != null && !startServerButton.interactable)
            {
                // Cycle dots: . → .. → ... → (empty) → repeat
                string dots = new string('.', dotCount);
                buttonText.text = $"{BASE_TEXT}{dots}";

                dotCount = (dotCount + 1) % 4; // 0, 1, 2, 3, then back to 0

                yield return new WaitForSeconds(0.5f); // Update every 500ms
            }

            // Restore original text when done
            if (buttonText != null)
            {
                buttonText.text = "Start Server";
            }
        }

        private GameObject CreatePanel(string name, Vector2 anchor, Vector2 position, Vector2 size, Color? color = null)
        {
            return CreatePanelWithPivot(name, anchor, position, size, new Vector2(0.5f, 1), color);
        }

        private GameObject CreatePanelWithPivot(string name, Vector2 anchor, Vector2 position, Vector2 size, Vector2 pivot, Color? color = null)
        {
            GameObject panelGO = new GameObject(name);
            panelGO.transform.SetParent(canvas.transform, false);

            RectTransform rect = panelGO.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Image image = panelGO.AddComponent<Image>();
            image.color = color ?? new Color(0, 0, 0, 0.85f);

            return panelGO;
        }

        private TextMeshProUGUI CreateText(Transform parent, string name, string text, int fontSize,
            Vector2 position, Vector2 size, FontStyles fontStyle = FontStyles.Normal,
            TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft)
        {
            GameObject textGO = new GameObject(name);
            textGO.transform.SetParent(parent, false);

            RectTransform rect = textGO.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1);
            rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = fontStyle;
            tmp.alignment = alignment;
            tmp.color = Color.white;

            return tmp;
        }

        private TextMeshProUGUI CreateLabel(Transform parent, string name, string text, Vector2 position, Vector2 size)
        {
            TextMeshProUGUI label = CreateText(parent, name, text, 30, position, size, FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            label.fontSizeMin = 18;
            label.fontSizeMax = 30;
            label.enableAutoSizing = true;
            return label;
        }

        private Button CreateButton(Transform parent, string name, string text, Vector2 position, Vector2 size, UnityEngine.Events.UnityAction onClick)
        {
            GameObject buttonGO = new GameObject(name);
            buttonGO.transform.SetParent(parent, false);

            RectTransform rect = buttonGO.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1);  // Top-center anchor
            rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 0.5f);  // Center pivot
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Image image = buttonGO.AddComponent<Image>();
            image.color = new Color(0.2f, 0.4f, 0.8f, 1f);

            Button button = buttonGO.AddComponent<Button>();
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
            tmp.fontSize = 32;
            tmp.fontSizeMin = 18;
            tmp.fontSizeMax = 32;
            tmp.enableAutoSizing = true;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;

            return button;
        }

        private TMP_InputField CreateInputField(Transform parent, string name, string placeholder, Vector2 position, Vector2 size)
        {
            GameObject inputGO = new GameObject(name);
            inputGO.transform.SetParent(parent, false);

            RectTransform rect = inputGO.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1);  // Top-center anchor (consistent with other elements)
            rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 1);  // Top-center pivot
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Image image = inputGO.AddComponent<Image>();
            image.color = Color.white;

            TMP_InputField inputField = inputGO.AddComponent<TMP_InputField>();

            // Text Area
            GameObject textAreaGO = new GameObject("TextArea");
            textAreaGO.transform.SetParent(inputGO.transform, false);
            RectTransform textAreaRect = textAreaGO.AddComponent<RectTransform>();
            textAreaRect.anchorMin = Vector2.zero;
            textAreaRect.anchorMax = Vector2.one;
            textAreaRect.sizeDelta = new Vector2(-10, -10);

            // Placeholder
            GameObject placeholderGO = new GameObject("Placeholder");
            placeholderGO.transform.SetParent(textAreaGO.transform, false);
            TextMeshProUGUI placeholderText = placeholderGO.AddComponent<TextMeshProUGUI>();
            placeholderText.text = placeholder;
            placeholderText.fontSize = 30;
            placeholderText.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);

            RectTransform placeholderRect = placeholderGO.GetComponent<RectTransform>();
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.sizeDelta = Vector2.zero;

            // Text
            GameObject textGO = new GameObject("Text");
            textGO.transform.SetParent(textAreaGO.transform, false);
            TextMeshProUGUI inputText = textGO.AddComponent<TextMeshProUGUI>();
            inputText.fontSize = 30;
            inputText.color = Color.black;

            RectTransform inputTextRect = textGO.GetComponent<RectTransform>();
            inputTextRect.anchorMin = Vector2.zero;
            inputTextRect.anchorMax = Vector2.one;
            inputTextRect.sizeDelta = Vector2.zero;

            inputField.textViewport = textAreaRect;
            inputField.textComponent = inputText;
            inputField.placeholder = placeholderText;
            inputField.text = placeholder;  // Set the actual text value, not just placeholder

            return inputField;
        }

        private TMP_Dropdown CreateDropdown(Transform parent, string name, Vector2 position, Vector2 size)
        {
            GameObject dropdownGO = new GameObject(name);
            dropdownGO.transform.SetParent(parent, false);

            RectTransform rect = dropdownGO.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1);
            rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Image bgImage = dropdownGO.AddComponent<Image>();
            bgImage.color = Color.white;

            TMP_Dropdown dropdown = dropdownGO.AddComponent<TMP_Dropdown>();

            // Create Label (displays selected value)
            GameObject labelGO = new GameObject("Label");
            labelGO.transform.SetParent(dropdownGO.transform, false);
            RectTransform labelRect = labelGO.AddComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0, 0);
            labelRect.anchorMax = new Vector2(1, 1);
            labelRect.offsetMin = new Vector2(10, 2);
            labelRect.offsetMax = new Vector2(-25, -2);

            TextMeshProUGUI labelText = labelGO.AddComponent<TextMeshProUGUI>();
            labelText.text = "Option";
            labelText.fontSize = 20;
            labelText.color = Color.black;
            labelText.alignment = TextAlignmentOptions.MidlineLeft;

            dropdown.captionText = labelText;

            // Create Arrow
            GameObject arrowGO = new GameObject("Arrow");
            arrowGO.transform.SetParent(dropdownGO.transform, false);
            RectTransform arrowRect = arrowGO.AddComponent<RectTransform>();
            arrowRect.anchorMin = new Vector2(1, 0.5f);
            arrowRect.anchorMax = new Vector2(1, 0.5f);
            arrowRect.pivot = new Vector2(0.5f, 0.5f);
            arrowRect.anchoredPosition = new Vector2(-15, 0);
            arrowRect.sizeDelta = new Vector2(20, 20);

            TextMeshProUGUI arrowText = arrowGO.AddComponent<TextMeshProUGUI>();
            arrowText.text = "▼";
            arrowText.fontSize = 16;
            arrowText.color = Color.black;
            arrowText.alignment = TextAlignmentOptions.Center;

            // Create Template (dropdown list)
            GameObject templateGO = new GameObject("Template");
            templateGO.transform.SetParent(dropdownGO.transform, false);
            RectTransform templateRect = templateGO.AddComponent<RectTransform>();
            templateRect.anchorMin = new Vector2(0, 0);
            templateRect.anchorMax = new Vector2(1, 0);
            templateRect.pivot = new Vector2(0.5f, 1);
            templateRect.anchoredPosition = new Vector2(0, 2);
            templateRect.sizeDelta = new Vector2(0, 150);

            Image templateImage = templateGO.AddComponent<Image>();
            templateImage.color = Color.white;

            ScrollRect scrollRect = templateGO.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            // Create Viewport
            GameObject viewportGO = new GameObject("Viewport");
            viewportGO.transform.SetParent(templateGO.transform, false);
            RectTransform viewportRect = viewportGO.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.sizeDelta = Vector2.zero;
            viewportRect.pivot = new Vector2(0, 1);

            Image viewportMask = viewportGO.AddComponent<Image>();
            viewportMask.color = Color.white;
            Mask mask = viewportGO.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            scrollRect.viewport = viewportRect;

            // Create Content
            GameObject contentGO = new GameObject("Content");
            contentGO.transform.SetParent(viewportGO.transform, false);
            RectTransform contentRect = contentGO.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0, 28);

            scrollRect.content = contentRect;

            // Create Item (template for each option)
            GameObject itemGO = new GameObject("Item");
            itemGO.transform.SetParent(contentGO.transform, false);
            RectTransform itemRect = itemGO.AddComponent<RectTransform>();
            itemRect.anchorMin = new Vector2(0, 0.5f);
            itemRect.anchorMax = new Vector2(1, 0.5f);
            itemRect.sizeDelta = new Vector2(0, 28);

            Toggle itemToggle = itemGO.AddComponent<Toggle>();

            // Item Background
            GameObject itemBgGO = new GameObject("Item Background");
            itemBgGO.transform.SetParent(itemGO.transform, false);
            RectTransform itemBgRect = itemBgGO.AddComponent<RectTransform>();
            itemBgRect.anchorMin = Vector2.zero;
            itemBgRect.anchorMax = Vector2.one;
            itemBgRect.sizeDelta = Vector2.zero;

            Image itemBgImage = itemBgGO.AddComponent<Image>();
            itemBgImage.color = new Color(0.95f, 0.95f, 0.95f);

            itemToggle.targetGraphic = itemBgImage;

            // Item Checkmark
            GameObject checkmarkGO = new GameObject("Item Checkmark");
            checkmarkGO.transform.SetParent(itemGO.transform, false);
            RectTransform checkmarkRect = checkmarkGO.AddComponent<RectTransform>();
            checkmarkRect.anchorMin = new Vector2(0, 0.5f);
            checkmarkRect.anchorMax = new Vector2(0, 0.5f);
            checkmarkRect.pivot = new Vector2(0.5f, 0.5f);
            checkmarkRect.anchoredPosition = new Vector2(10, 0);
            checkmarkRect.sizeDelta = new Vector2(20, 20);

            TextMeshProUGUI checkmarkText = checkmarkGO.AddComponent<TextMeshProUGUI>();
            checkmarkText.text = "✓";
            checkmarkText.fontSize = 18;
            checkmarkText.color = Color.black;
            checkmarkText.alignment = TextAlignmentOptions.Center;

            itemToggle.graphic = checkmarkText;

            // Item Label
            GameObject itemLabelGO = new GameObject("Item Label");
            itemLabelGO.transform.SetParent(itemGO.transform, false);
            RectTransform itemLabelRect = itemLabelGO.AddComponent<RectTransform>();
            itemLabelRect.anchorMin = Vector2.zero;
            itemLabelRect.anchorMax = Vector2.one;
            itemLabelRect.offsetMin = new Vector2(20, 1);
            itemLabelRect.offsetMax = new Vector2(-5, -2);

            TextMeshProUGUI itemLabelText = itemLabelGO.AddComponent<TextMeshProUGUI>();
            itemLabelText.text = "Option";
            itemLabelText.fontSize = 18;
            itemLabelText.color = Color.black;
            itemLabelText.alignment = TextAlignmentOptions.MidlineLeft;

            dropdown.itemText = itemLabelText;

            // Assign template and hide it
            dropdown.template = templateRect;
            templateGO.SetActive(false);

            return dropdown;
        }

        private void EnsureEventSystemExists()
        {
            EventSystem[] allEventSystems = FindObjectsOfType<EventSystem>();

            if (allEventSystems.Length == 0)
            {
                GONetLog.Debug($"{LOGCODE}No EventSystem found - creating persistent one");
                GameObject eventSystemGO = new GameObject("EventSystem_Persistent");
                eventSystemGO.AddComponent<EventSystem>();
                eventSystemGO.AddComponent<StandaloneInputModule>();

                // CRITICAL: Only call DontDestroyOnLoad during play mode (not during editor build process)
                if (Application.isPlaying)
                {
                    DontDestroyOnLoad(eventSystemGO);
                }
            }
        }

        #endregion

        #region Steam ID Encoding Utilities

        /// <summary>
        /// Converts a Steam ID (ulong) to a human-friendly Base-36 code with smart grouping and color coding.
        /// Example: 76561197960287930 -> "DXE-5C7J-KL2" (with colors: letters=cyan, numbers=orange)
        /// Groups: 3-5 characters, as uniform as possible
        /// </summary>
        private static string SteamIDToFriendlyCode(ulong steamID, bool includeColorCoding = false)
        {
            const string BASE36_CHARS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

            if (steamID == 0)
            {
                return "0";
            }

            System.Text.StringBuilder result = new System.Text.StringBuilder();
            ulong value = steamID;

            while (value > 0)
            {
                int remainder = (int)(value % 36);
                result.Insert(0, BASE36_CHARS[remainder]);
                value /= 36;
            }

            string encoded = result.ToString();

            // Smart grouping: groups of 3-5 characters, as uniform as possible
            int[] groupSizes = CalculateOptimalGroupSizes(encoded.Length);

            System.Text.StringBuilder formatted = new System.Text.StringBuilder();
            int charIndex = 0;

            for (int groupIndex = 0; groupIndex < groupSizes.Length; groupIndex++)
            {
                if (groupIndex > 0)
                {
                    formatted.Append('-');
                }

                int groupSize = groupSizes[groupIndex];
                for (int i = 0; i < groupSize; i++)
                {
                    char c = encoded[charIndex++];

                    if (includeColorCoding)
                    {
                        // Color coding: digits = orange, letters = cyan
                        if (char.IsDigit(c))
                        {
                            formatted.Append("<color=#FFA500>"); // Orange
                            formatted.Append(c);
                            formatted.Append("</color>");
                        }
                        else
                        {
                            formatted.Append("<color=#00FFFF>"); // Cyan
                            formatted.Append(c);
                            formatted.Append("</color>");
                        }
                    }
                    else
                    {
                        formatted.Append(c);
                    }
                }
            }

            return formatted.ToString();
        }

        /// <summary>
        /// Calculates optimal group sizes for a given length.
        /// Groups should be 3-5 characters, as uniform as possible.
        /// </summary>
        private static int[] CalculateOptimalGroupSizes(int totalLength)
        {
            if (totalLength <= 5)
            {
                return new int[] { totalLength };
            }

            // Try to create groups of size 4 (most uniform for typical lengths)
            int numGroups = (totalLength + 3) / 4; // Round up division by 4

            // Calculate how many characters per group
            int baseSize = totalLength / numGroups;
            int remainder = totalLength % numGroups;

            // Create groups: some will be baseSize+1, others baseSize
            int[] groups = new int[numGroups];
            for (int i = 0; i < numGroups; i++)
            {
                groups[i] = baseSize + (i < remainder ? 1 : 0);
            }

            // Ensure all groups are within 3-5 range
            // If baseSize < 3, consolidate groups
            if (baseSize < 3)
            {
                // Recalculate with larger groups
                numGroups = (totalLength + 4) / 5; // Try groups of ~5
                baseSize = totalLength / numGroups;
                remainder = totalLength % numGroups;

                groups = new int[numGroups];
                for (int i = 0; i < numGroups; i++)
                {
                    groups[i] = baseSize + (i < remainder ? 1 : 0);
                }
            }

            return groups;
        }

        /// <summary>
        /// Converts a Base-36 friendly code back to a Steam ID (ulong).
        /// Example: "DXE5C-7JKL2" -> 76561197960287930
        /// </summary>
        private static bool TryFriendlyCodeToSteamID(string friendlyCode, out ulong steamID)
        {
            const string BASE36_CHARS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            steamID = 0;

            if (string.IsNullOrWhiteSpace(friendlyCode))
            {
                return false;
            }

            // Remove dashes and whitespace, convert to uppercase
            string cleaned = friendlyCode.Replace("-", "").Replace(" ", "").ToUpperInvariant();

            try
            {
                ulong result = 0;
                foreach (char c in cleaned)
                {
                    int digitValue = BASE36_CHARS.IndexOf(c);
                    if (digitValue < 0)
                    {
                        // Invalid character
                        return false;
                    }

                    result = result * 36 + (ulong)digitValue;
                }

                steamID = result;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Copies text to system clipboard.
        /// </summary>
        private static void CopyToClipboard(string text)
        {
            GUIUtility.systemCopyBuffer = text;
            GONetLog.Info($"[GONetConnectionWizard] Copied to clipboard: {text}");
        }

        #endregion

        #region Steamworks UI Control

        private void UpdateTransportSpecificUI(int transportIndex)
        {
            const string TRANSPORT_STEAMWORKS = "Steamworks";
            const string TRANSPORT_NETCODEIO = "NetcodeIO";

            bool isSteamworks = (transportIndex == 1);  // 0 = NetcodeIO, 1 = Steamworks

            GONetLog.Info($"{LOGCODE}Transport changed to: {(isSteamworks ? TRANSPORT_STEAMWORKS : TRANSPORT_NETCODEIO)}");

            if (isSteamworks)
            {
                // Initialize Steam early when Steamworks is selected (gives SDR time to warm up)
                EnsureEarlySteamInitialization();

                // Show Steamworks-specific UI
                GONetLog.Info($"{LOGCODE}Showing Steamworks UI - connectionMethodSection null? {connectionMethodSection == null}");
                if (connectionMethodSection != null)
                {
                    connectionMethodSection.SetActive(true);
                    GONetLog.Info($"{LOGCODE}Connection method section set to active");
                }

                // Retrieve and display user's Steam ID
                UpdateYourSteamID();

                // Update connection method UI based on current selection
                UpdateConnectionMethodUI();
            }
            else
            {
                // Hide Steamworks-specific UI
                if (connectionMethodSection != null)
                {
                    connectionMethodSection.SetActive(false);
                }

                if (steamP2PInputSection != null)
                {
                    steamP2PInputSection.SetActive(false);
                }

                // Direct IP section should remain visible for NetcodeIO
                if (directIPInputSection != null)
                {
                    directIPInputSection.SetActive(true);
                }
            }
        }

        private void UpdateConnectionMethodUI()
        {
            bool useSteamP2P = useSteamP2PToggle != null && useSteamP2PToggle.isOn;

            // Show/hide appropriate input sections
            // Port field is ALWAYS visible (separate section, not toggled)
            if (steamP2PInputSection != null)
            {
                steamP2PInputSection.SetActive(useSteamP2P);
            }

            if (directIPInputSection != null)
            {
                directIPInputSection.SetActive(!useSteamP2P);
            }

            // Update status text to guide user
            if (statusText != null)
            {
                if (useSteamP2P)
                {
                    statusText.text = "Enter host's Steam ID and port to connect via Steam P2P";
                    statusText.color = Color.cyan;
                }
                else
                {
                    statusText.text = "Enter host's IP address and port for direct connection";
                    statusText.color = Color.cyan;
                }
            }
        }

        private void UpdateYourSteamID()
        {
            if (yourSteamIDLabel == null)
            {
                return;
            }

            // Check if GONetSteamManager is initialized
            if (!GONetSteamManager.IsInitialized)
            {
                // Instead of showing error, provide helpful guidance
                yourSteamIDLabel.text = "Note: Ensure Steam client is running before connecting via Steam P2P";
                yourSteamIDLabel.color = new Color(1f, 0.8f, 0.4f); // Orange-ish warning color
                return;
            }

            try
            {
                // Retrieve local Steam ID
                Steamworks.CSteamID localSteamID = Steamworks.SteamUser.GetSteamID();
                string steamIDString = localSteamID.ToString();

                yourSteamIDLabel.text = $"Your Steam ID: {steamIDString}";
                yourSteamIDLabel.color = new Color(0.5f, 1f, 0.5f); // Light green - success
                yourSteamIDLabel.fontSize = 20;

                GONetLog.Info($"{LOGCODE}Retrieved local Steam ID: {steamIDString}");
            }
            catch (System.Exception ex)
            {
                yourSteamIDLabel.text = $"Error retrieving Steam ID: {ex.Message}";
                yourSteamIDLabel.color = Color.red;
                GONetLog.Error($"{LOGCODE}Failed to retrieve Steam ID: {ex.Message}");
            }
        }

        private void UpdateServerTransportUI(int transportIndex)
        {
            const string TRANSPORT_STEAMWORKS = "Steamworks";
            const string TRANSPORT_NETCODEIO = "NetcodeIO";

            bool isSteamworks = (transportIndex == 1);  // 0 = NetcodeIO, 1 = Steamworks

            GONetLog.Debug($"{LOGCODE}Server transport changed to: {(isSteamworks ? TRANSPORT_STEAMWORKS : TRANSPORT_NETCODEIO)}");

            if (isSteamworks)
            {
                // Initialize Steam early when Steamworks is selected (gives SDR time to warm up)
                EnsureEarlySteamInitialization();
            }

            // Note: Server Steam info (P2P Code) is displayed in GONetStatusUI after server starts,
            // not in this pre-connection popup (Steam may not be initialized yet)
        }

        // REMOVED: UpdateServerSteamID() and CopyServerP2PCode() methods
        // Server Steam P2P code is now displayed only in GONetStatusUI (after server starts)

        #endregion

        #region Button Handlers

        private void OnHostButtonClicked()
        {
            GONetLog.Info($"{LOGCODE}Host button clicked - showing server configuration");
            pendingRole = GONetConnectionRole.Host;

            // Show Server Configuration popup
            if (serverConfigOverlay != null)
            {
                serverConfigOverlay.SetActive(true);
            }
        }

        private void OnDedicatedServerButtonClicked()
        {
            GONetLog.Info($"{LOGCODE}Dedicated Server button clicked - showing server configuration");
            pendingRole = GONetConnectionRole.DedicatedServer;

            // Show Server Configuration popup
            if (serverConfigOverlay != null)
            {
                serverConfigOverlay.SetActive(true);
            }
        }

        private void OnConnectButtonClicked()
        {
            const string EMPTY_STEAMID = "Please enter a Steam ID.";

            GONetLog.Info($"{LOGCODE}Connect button clicked");

            // Check if using Steamworks transport with Steam P2P connection
            bool isSteamworks = clientUseCustomTransportToggle != null && clientUseCustomTransportToggle.isOn &&
                               clientTransportDropdown != null && clientTransportDropdown.value == 1;

            bool useSteamP2P = isSteamworks && useSteamP2PToggle != null && useSteamP2PToggle.isOn;

            if (isSteamworks && useSteamP2P)
            {
                // Steam P2P connection - validate Steam ID format
                // NOTE: We no longer check GONetSteamManager.IsInitialized here because Steam may still be initializing
                // when the user clicks Connect. The transport layer will handle the Steam initialization check
                // at connection time, giving Steam more time to initialize. Early validation was too strict.
                /* COMMENTED OUT - Allow connection even if Steam not initialized yet
                if (!GONetSteamManager.IsInitialized)
                {
                    HandleStatusChanged(STEAM_NOT_INITIALIZED_ERROR);
                    GONetLog.Error($"{LOGCODE}{STEAM_NOT_INITIALIZED_ERROR}");
                    return;
                }
                */

                string hostSteamIDOrCode = hostSteamIDInput.text.Trim();

                if (string.IsNullOrEmpty(hostSteamIDOrCode))
                {
                    HandleStatusChanged(EMPTY_STEAMID);
                    GONetLog.Warning($"{LOGCODE}{EMPTY_STEAMID}");
                    return;
                }

                // Try parsing as numeric Steam ID first
                ulong steamIDValue;
                if (ulong.TryParse(hostSteamIDOrCode, out steamIDValue))
                {
                    // Valid numeric Steam ID
                    GONetLog.Info($"{LOGCODE}Parsed as numeric Steam ID: {steamIDValue}");
                }
                // Try parsing as Share Code (Base-36 encoded)
                else if (TryFriendlyCodeToSteamID(hostSteamIDOrCode, out steamIDValue))
                {
                    // Valid Share Code - converted to Steam ID
                    GONetLog.Info($"{LOGCODE}Converted Share Code '{hostSteamIDOrCode}' to Steam ID: {steamIDValue}");
                }
                else
                {
                    // Invalid format - not numeric and not valid Share Code
                    HandleStatusChanged("Invalid Steam ID or Share Code format.");
                    GONetLog.Warning($"{LOGCODE}Invalid format (not numeric Steam ID or Base-36 Share Code). Input: {hostSteamIDOrCode}");
                    return;
                }

                // Update the input field to show the numeric Steam ID (for clarity)
                hostSteamIDInput.text = steamIDValue.ToString();

                // Valid Steam ID - pass it as the address
                // SteamworksTransport will detect it's not an IP and use ConnectP2P()
                GONetLog.Info($"{LOGCODE}Connecting via Steam P2P to Steam ID: {steamIDValue}");
                SetButtonsEnabled(false);
                StartCoroutine(ConnectClientViaSteamP2PWithLoadingState());
            }
            else
            {
                // Direct IP connection (NetcodeIO or Steamworks Direct IP)
                GONetLog.Info($"{LOGCODE}Connecting via Direct IP to: {ipAddressInput.text}:{portInput.text}");

                // Apply UI configuration to preset (includes transport settings)
                // This will be applied to GONetGlobal BEFORE instantiation in GONetConnectionManager
                ApplyUIConfigurationToPreset(GONetConnectionRole.Client);
                connectionManager.Connect();  // ApplyPresetToGONetGlobal happens BEFORE instantiation
            }
        }

        private System.Collections.IEnumerator ConnectClientViaSteamP2PWithLoadingState()
        {
            if (statusText != null)
            {
                statusText.text = "Waiting for Steam Datagram Relay...";
                statusText.color = Color.yellow;
            }

            // Let UI update before starting polling/waits
            yield return null;

            yield return StartCoroutine(WaitForSteamSDRReady());

            if (!lastSteamSdrReadyResult)
            {
                const string ERROR_MSG = "Error: Steam Datagram Relay not ready. Try again in a moment.";
                GONetLog.Warning($"{LOGCODE}{ERROR_MSG}");

                if (statusText != null)
                {
                    statusText.text = ERROR_MSG;
                    statusText.color = Color.red;
                }

                SetButtonsEnabled(true);
                yield break;
            }

            ApplyUIConfigurationToPreset(GONetConnectionRole.Client);
            connectionManager.Connect();  // ApplyPresetToGONetGlobal happens BEFORE instantiation
        }


        private void OnSavePresetClicked()
        {
            GONetLog.Info($"{LOGCODE}Save preset clicked");
            // TODO: Implement preset saving
        }

        private void OnLoadPresetClicked()
        {
            GONetLog.Info($"{LOGCODE}Load preset clicked");
            // TODO: Implement preset loading
        }

        private void OnScanLANClicked()
        {
            GONetLog.Info($"{LOGCODE}Scan LAN clicked");
            // TODO: Implement LAN discovery
        }

        #endregion

        #region Configuration Management

        private void ApplyUIConfigurationToPreset(GONetConnectionRole role)
        {
            GONetConnectionPreset preset = ScriptableObject.CreateInstance<GONetConnectionPreset>();
            preset.role = role;
            preset.playerName = playerNameInput.text;

            // Transport configuration (use server or client fields based on role)
            if (role == GONetConnectionRole.Host || role == GONetConnectionRole.DedicatedServer)
            {
                // Server config
                if (serverUseCustomTransportToggle != null && serverTransportDropdown != null)
                {
                    preset.usePluggableTransport = serverUseCustomTransportToggle.isOn;
                    preset.transportType = (GONetTransportType)serverTransportDropdown.value;
                    GONetLog.Info($"{LOGCODE}SERVER Preset transport config - UsePluggable: {preset.usePluggableTransport}, Type: {preset.transportType}");
                }

                // Server always uses IP address and port (no Steam P2P for server hosting)
                preset.ipAddress = ipAddressInput.text;
                if (ushort.TryParse(portInput.text, out ushort port))
                {
                    preset.port = port;
                }
            }
            else
            {
                // Client config
                if (clientUseCustomTransportToggle != null && clientTransportDropdown != null)
                {
                    preset.usePluggableTransport = clientUseCustomTransportToggle.isOn;
                    preset.transportType = (GONetTransportType)clientTransportDropdown.value;
                    GONetLog.Info($"{LOGCODE}CLIENT Preset transport config - UsePluggable: {preset.usePluggableTransport}, Type: {preset.transportType}");
                }

                // IP and Port configuration (applies to ALL transports, not just custom)
                // Port is ALWAYS read from UI (needed for Steam P2P virtual port and Direct IP)
                if (ushort.TryParse(portInput.text, out ushort clientPort))
                {
                    preset.port = clientPort;
                    GONetLog.Info($"{LOGCODE}CLIENT - Successfully parsed port from UI: {clientPort}");
                }
                else
                {
                    // Failed to parse port - use default
                    preset.port = 7777;
                    GONetLog.Warning($"{LOGCODE}CLIENT - Failed to parse port from UI (value: '{portInput.text}'), using default: 7777");
                }

                // Address configuration (Steam ID vs IP Address)
                bool isSteamworks = preset.usePluggableTransport && preset.transportType == GONetTransportType.Steamworks;
                bool useSteamP2P = isSteamworks && useSteamP2PToggle != null && useSteamP2PToggle.isOn;

                if (useSteamP2P)
                {
                    // Steam P2P connection - use Steam ID as address, port for virtual port
                    preset.ipAddress = hostSteamIDInput.text.Trim();
                    GONetLog.Info($"{LOGCODE}CLIENT using Steam P2P - Address: {preset.ipAddress} (Steam ID), Port: {preset.port}");
                }
                else
                {
                    // Direct IP connection (applies to NetcodeIO, Steamworks Direct IP, or any other transport)
                    preset.ipAddress = ipAddressInput.text;
                    GONetLog.Info($"{LOGCODE}CLIENT using Direct IP - Address: {preset.ipAddress}:{preset.port}");
                }
            }

            // Server configuration settings (only set if maxPlayersInput exists)
            if (maxPlayersInput != null && int.TryParse(maxPlayersInput.text, out int maxPlayers))
            {
                preset.maxConnections = maxPlayers;
            }

            connectionManager.CurrentPreset = preset;
        }

        private void LoadBuiltInPresets()
        {
            // TODO: Load presets from NetworkLobbyConfig.json
            GONetLog.Debug($"{LOGCODE}Loading built-in presets...");
        }

        #endregion

        #region Connection Event Handlers

        private void HandleConnectionStarted(GONetConnectionPreset preset)
        {
            GONetLog.Info($"{LOGCODE}Connection started");
            SetButtonsEnabled(false);
        }

        private void HandleConnectionSuccess()
        {
            GONetLog.Info($"{LOGCODE}Connection successful");
            isConnected = true;

            // Hide wizard UI (including server config popup if it's still visible)
            mainPanel.SetActive(false);
            if (serverConfigOverlay != null)
            {
                serverConfigOverlay.SetActive(false);
            }

            // Re-enable start server button (in case user returns to lobby)
            if (startServerButton != null)
            {
                startServerButton.interactable = true;
            }
        }

        private void HandleConnectionFailed(string error)
        {
            GONetLog.Error($"{LOGCODE}Connection failed: {error}");
            SetButtonsEnabled(true);

            // Re-enable start server button
            if (startServerButton != null)
            {
                startServerButton.interactable = true;
            }

            // Show server config popup again if it was hidden
            if (serverConfigOverlay != null && pendingRole != null)
            {
                serverConfigOverlay.SetActive(true);
            }
        }

        private void HandleDisconnected()
        {
            GONetLog.Info($"{LOGCODE}Disconnected");
            isConnected = false;

            // Show wizard UI again
            mainPanel.SetActive(true);
            SetButtonsEnabled(true);
        }

        private void HandleStatusChanged(string status)
        {
            statusText.text = status;

            // Color status text based on content
            if (status.Contains("Error") || status.Contains("Failed"))
            {
                statusText.color = Color.red;
            }
            else if (status.Contains("Connect"))
            {
                statusText.color = Color.yellow;
            }
            else
            {
                statusText.color = Color.green;
            }
        }

        private void SetButtonsEnabled(bool enabled)
        {
            hostButton.interactable = enabled;
            dedicatedServerButton.interactable = enabled;
            connectButton.interactable = enabled;
            scanLANButton.interactable = enabled;
            savePresetButton.interactable = enabled;
            loadPresetButton.interactable = enabled;
        }

        #endregion
    }
}
