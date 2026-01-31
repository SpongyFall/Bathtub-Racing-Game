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

using GONet;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace GONet.Sample
{
    /// <summary>
    /// Persistent UI overlay showing GONet status information.
    /// Displays server/client role and synchronized time.
    /// This component is designed to persist across scene changes (via DontDestroyOnLoad on parent).
    /// </summary>
    public class GONetStatusUI : MonoBehaviour
    {
        #region String Constants - Eliminates runtime string allocations

        // UI Element Names
        private const string NAME_CANVAS = "GONetStatusCanvas";
        private const string NAME_PANEL = "StatusPanel";
        private const string NAME_HEADER = "HeaderText";
        private const string NAME_TIME = "TimeText";
        private const string NAME_ROLE = "RoleText";
        private const string NAME_CONNECTION = "ConnectionStatusText";
        private const string NAME_FPS = "FpsText";
        private const string NAME_RTT = "RttText";
        private const string NAME_NETSIM = "NetSimText";
        private const string NAME_STEAM_SECTION = "SteamInfoSection";
        private const string NAME_STEAM_ID = "SteamIDText";
        private const string NAME_STEAM_ID_COPY = "SteamIDCopyButton";
        private const string NAME_FRIENDLY_CODE = "FriendlyCodeText";
        private const string NAME_FRIENDLY_CODE_COPY = "FriendlyCodeCopyButton";
        private const string NAME_STEAM_HELP = "SteamInfoHelp";
        private const string NAME_TEXT = "Text";
        private const string NAME_MESH = "MeshText";

        // Display Text Labels
        private const string LABEL_HEADER = "GONet Info";
        private const string LABEL_TIME_PREFIX = "Sync'd Time: ";
        private const string LABEL_TIME_SUFFIX = "s";
        private const string LABEL_ROLE_PREFIX = "Role: ";
        private const string LABEL_AUTHORITY_PREFIX = " (Authority:";
        private const string LABEL_AUTHORITY_SUFFIX = ")";
        private const string LABEL_AUTHORITY_PENDING = "--)";
        private const string LABEL_PORT_PREFIX = " Port:";
        private const string LABEL_CONNECTION_PREFIX = "Connection: ";
        private const string LABEL_FPS_PREFIX = "FPS: ";
        private const string LABEL_RTT_PREFIX = "RTT: ";
        private const string LABEL_RTT_MS = "ms (avg:";
        private const string LABEL_RTT_SUFFIX = ")";
        private const string LABEL_RTT_NONE = "";
        private const string LABEL_NETSIM_PREFIX = "NetSim: ";
        private const string LABEL_NETSIM_MS = "ms ±";
        private const string LABEL_NETSIM_LOSS = " | ";
        private const string LABEL_NETSIM_LOSS_SUFFIX = "% loss";
        private const string LABEL_STEAM_ID_PREFIX = "Steam ID: ";
        private const string LABEL_FRIENDLY_CODE_PREFIX = "P2P Share Code: ";
        private const string LABEL_STEAM_HELP = "(Share with players for P2P connection)";
        private const string LABEL_COPY_BUTTON = "[Copy]";
        private const string LABEL_CLIENTS_SEPARATOR = " / ";
        private const string LABEL_CLIENTS_SUFFIX = " Clients";
        private const string LABEL_MESH_PREFIX = "Mesh: ";
        private const string LABEL_MESH_SEPARATOR = "/";
        private const string LABEL_MESH_PEERS = " peers";
        private const string LABEL_MESH_DISABLED = "Mesh: (disabled)";
        private const string LABEL_MESH_NONE = "Mesh: 0 peers";

        // Role Text
        private const string ROLE_UNKNOWN = "Unknown";
        private const string ROLE_SERVER_CLIENT = "SERVER + CLIENT";
        private const string ROLE_SERVER = "SERVER";
        private const string ROLE_SERVER_STARTING = "SERVER (starting...)";
        private const string ROLE_SERVER_FAILED = "SERVER (FAILED - check logs)";
        private const string ROLE_CLIENT = "CLIENT";

        // Connection Status Text
        private const string STATUS_NOT_CONNECTED = "Not Connected";
        private const string STATUS_CONNECTED = "Connected";
        private const string STATUS_CONNECTING = "Connecting";
        private const string STATUS_DISCONNECTED = "Disconnected";
        private const string STATUS_ERROR = "Connection Error";
        private const string STATUS_DENIED = "Connection Denied";
        private const string STATUS_TIMEOUT = "Connection Timeout";
        private const string STATUS_INVALID_TOKEN = "Invalid Token";
        private const string STATUS_TOKEN_EXPIRED = "Token Expired";
        private const string STATUS_UNKNOWN = "Unknown";
        private const string STATUS_INITIALIZED = "Connected (Initialized)";
        private const string STATUS_INITIALIZING = "Connected (Initializing...)";

        // Default Display Text
        private const string DEFAULT_TIME = "Sync'd Time: 0.00";
        private const string DEFAULT_ROLE = "Role: Unknown";
        private const string DEFAULT_CONNECTION = "Connection: Not Connected";
        private const string DEFAULT_FPS = "FPS: --";
        private const string DEFAULT_RTT = "RTT: --";
        private const string DEFAULT_MESH = "Mesh: --";
        private const string DEFAULT_STEAM_ID = "Steam ID: ";
        private const string DEFAULT_FRIENDLY_CODE = "P2P Share Code: ";

        // GameObject.Find constant
        private const string SERVER_GO_NAME = "GONetSampleServer(Clone)";

        // Base36 encoding
        private const string BASE36_CHARS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private const string BASE36_ZERO = "0";

        // Rich text color tags
        private const string COLOR_TAG_ORANGE_START = "<color=#FFA500>";
        private const string COLOR_TAG_CYAN_START = "<color=#00FFFF>";
        private const string COLOR_TAG_END = "</color>";

        // Logging prefix
        private const string LOG_PREFIX = "[GONetStatusUI] ";
        private const string LOG_COPIED_STEAM_ID = "Copied Steam ID to clipboard: ";
        private const string LOG_COPIED_FRIENDLY = "Copied friendly code to clipboard: ";
        private const string LOG_FAILED_COPY_STEAM = "Failed to copy Steam ID: ";
        private const string LOG_FAILED_COPY_FRIENDLY = "Failed to copy friendly code: ";
        private const string LOG_STEAM_ERROR_PREFIX = "Steam ID: Error - ";

        // Minimize/Maximize UI
        private const string NAME_EXPANDED_CONTENT = "ExpandedContent";
        private const string NAME_MINIMIZED_CONTENT = "MinimizedContent";
        private const string NAME_MINIMIZE_BUTTON = "MinimizeButton";
        private const string NAME_MAXIMIZE_BUTTON = "MaximizeButton";
        private const string NAME_MINIMIZED_STATUS = "MinimizedStatusText";
        private const string LABEL_MINIMIZE = "−";
        private const string LABEL_MAXIMIZE = "+";
        private const string LABEL_MINIMIZED_PREFIX = "GONet: ";
        private const string LABEL_MINIMIZED_SERVER_CLIENTS_PREFIX = " (";
        private const string LABEL_MINIMIZED_SERVER_CLIENTS_SEPARATOR = "/";
        private const string LABEL_MINIMIZED_SERVER_CLIENTS_SUFFIX = ")";
        private const string LABEL_MINIMIZED_CLIENT_AUTHORITY_PREFIX = ":";

        // Default hotkey
        private const KeyCode DEFAULT_TOGGLE_KEY = KeyCode.F10;

        #endregion

        #region Numeric Constants

        // Canvas settings
        private const int CANVAS_SORT_ORDER = 100;
        private const float REFERENCE_WIDTH = 1920f;
        private const float REFERENCE_HEIGHT = 1080f;

        // Panel layout - expanded
        private const float PANEL_X = 10f;
        private const float PANEL_Y = -10f;
        private const float PANEL_WIDTH_EXPANDED = 1400f;
        private const float PANEL_HEIGHT_EXPANDED = 380f;

        // Panel layout - minimized (wider to accommodate "GONet: SERVER + CLIENT (0/32)")
        private const float PANEL_WIDTH_MINIMIZED = 380f;
        private const float PANEL_HEIGHT_MINIMIZED = 40f;

        private const float PANEL_ALPHA = 0.7f;

        // Minimize/Maximize button
        private const float TOGGLE_BUTTON_SIZE = 32f;
        private const float TOGGLE_BUTTON_X = 10f;
        private const float TOGGLE_BUTTON_Y = -10f;
        private const int FONT_SIZE_TOGGLE = 20;

        // Text layout - left column
        private const float LEFT_COL_X = 10f;
        private const float HEADER_Y = -10f;
        private const float TIME_Y = -70f;
        private const float ROLE_Y = -125f;
        private const float CONNECTION_Y = -180f;

        // Text layout - right column
        private const float RIGHT_COL_X = 660f;
        private const float FPS_Y = -70f;
        private const float RTT_Y = -125f;
        private const float NETSIM_Y = -180f;

        // Text layout - far right column (mesh info)
        private const float FAR_RIGHT_COL_X = 1000f;
        private const float MESH_Y = -70f;

        // Text sizes
        private const float HEADER_WIDTH = 500f;
        private const float HEADER_HEIGHT = 50f;
        private const float TEXT_WIDTH = 680f;
        private const float TEXT_HEIGHT = 45f;
        private const float RIGHT_COL_WIDTH = 480f;

        // Font sizes
        private const int FONT_SIZE_HEADER = 56;
        private const int FONT_SIZE_MAIN = 48;
        private const int FONT_SIZE_NETSIM = 42;
        private const int FONT_SIZE_STEAM = 40;
        private const int FONT_SIZE_HELP = 32;
        private const int FONT_SIZE_BUTTON = 32;
        private const int FONT_SIZE_MIN = 10;

        // Steam section layout
        private const float STEAM_SECTION_Y = -235f;
        private const float STEAM_SECTION_HEIGHT = 130f;
        private const float STEAM_ID_Y = 0f;
        private const float FRIENDLY_CODE_Y = -50f;
        private const float STEAM_HELP_Y = -95f;
        private const float STEAM_BUTTON_X = 490f;
        private const float STEAM_BUTTON_WIDTH = 90f;
        private const float STEAM_BUTTON_HEIGHT = 40f;
        private const float STEAM_TEXT_WIDTH = 480f;
        private const float STEAM_TEXT_HEIGHT = 40f;
        private const float STEAM_HELP_WIDTH = 680f;
        private const float STEAM_HELP_HEIGHT = 35f;

        // FPS thresholds
        private const float FPS_GOOD_THRESHOLD = 55f;
        private const float FPS_WARN_THRESHOLD = 30f;
        private const int FPS_BUFFER_SIZE = 30;

        // RTT thresholds (ms)
        private const float RTT_GOOD_THRESHOLD = 50f;
        private const float RTT_WARN_THRESHOLD = 100f;
        private const float MS_CONVERSION = 1000f;

        // Time comparison epsilon
        private const float TIME_EPSILON = 0.001f;

        #endregion

        #region Color Constants - Avoids repeated Color struct instantiation

        private static readonly Color COLOR_PANEL_BG = new Color(0f, 0f, 0f, PANEL_ALPHA);
        private static readonly Color COLOR_BUTTON_BG = new Color(0.3f, 0.6f, 0.9f, 0.8f);
        private static readonly Color COLOR_TOGGLE_BG = new Color(0.4f, 0.4f, 0.4f, 0.9f);
        private static readonly Color COLOR_STEAM_TEXT = new Color(0.5f, 1f, 0.5f);
        private static readonly Color COLOR_NETSIM = new Color(1f, 0.8f, 0.4f);
        private static readonly Color COLOR_HIGH_LATENCY = new Color(1f, 0.5f, 0f);

        #endregion

        #region Update Interval Constants

        private const float TIME_UPDATE_INTERVAL = 0.1f;
        private const float STATUS_UPDATE_INTERVAL = 0.5f;
        private const float SERVER_GO_CHECK_INTERVAL = 1.0f;

        #endregion

        #region Fields

        // Hotkey configuration
        [Header("Hotkey Settings")]
        [Tooltip("The key to toggle the GONet Status UI expand/collapse state")]
        [SerializeField]
        private KeyCode toggleKey = DEFAULT_TOGGLE_KEY;

        // Minimize/Maximize state
        private bool isMinimized = true;
        private RectTransform panelRect;
        private GameObject expandedContent;
        private GameObject minimizedContent;
        private Button minimizeButton;
        private Button maximizeButton;
        private TextMeshProUGUI minimizedStatusText;

        private TextMeshProUGUI headerText;
        private TextMeshProUGUI timeText;
        private TextMeshProUGUI roleText;
        private TextMeshProUGUI connectionStatusText;
        private TextMeshProUGUI steamIDText;
        private TextMeshProUGUI fpsText;
        private TextMeshProUGUI rttText;
        private TextMeshProUGUI netSimText;
        private TextMeshProUGUI meshText;
        private TextMeshProUGUI friendlyCodeText;
        private Button steamIDCopyButton;
        private Button friendlyCodeCopyButton;
        private GameObject steamInfoSection;
        private bool wasServerSpawned;

        // PERFORMANCE: Cache previous values to avoid updating text every frame
        private float lastTimeUpdate = -1f;
        private string lastRoleText;
        private string lastConnectionStatus;
        private string lastFpsText;
        private string lastRttText;
        private string lastNetSimText;
        private string lastMeshText;

        // FPS calculation
        private float[] fpsBuffer = new float[FPS_BUFFER_SIZE];
        private int fpsBufferIndex = 0;
        private float fpsSum = 0f;
        private Color lastRoleColor = Color.white;
        private Color lastConnectionColor = Color.white;
        private bool lastSteamInfoVisibility = false;
        private string lastSteamIDText;
        private string lastFriendlyCodeText;

        // PERFORMANCE: Throttle timers
        private float nextTimeUpdate = 0f;
        private float nextStatusUpdate = 0f;

        // PERFORMANCE: Cache GameObject.Find result
        private GameObject cachedServerGO = null;
        private float nextServerGOCheck = 0f;

        // PERFORMANCE: Cached StringBuilders to avoid allocations
        private readonly System.Text.StringBuilder sbTime = new System.Text.StringBuilder(32);
        private readonly System.Text.StringBuilder sbRole = new System.Text.StringBuilder(64);
        private readonly System.Text.StringBuilder sbConnection = new System.Text.StringBuilder(48);
        private readonly System.Text.StringBuilder sbFps = new System.Text.StringBuilder(16);
        private readonly System.Text.StringBuilder sbRtt = new System.Text.StringBuilder(32);
        private readonly System.Text.StringBuilder sbNetSim = new System.Text.StringBuilder(48);
        private readonly System.Text.StringBuilder sbMesh = new System.Text.StringBuilder(48);
        private readonly System.Text.StringBuilder sbSteamId = new System.Text.StringBuilder(32);
        private readonly System.Text.StringBuilder sbFriendlyCode = new System.Text.StringBuilder(64);

        // PERFORMANCE: Static StringBuilder for Base36 conversion (thread-safe via instance method)
        private readonly System.Text.StringBuilder sbBase36 = new System.Text.StringBuilder(16);
        private readonly System.Text.StringBuilder sbBase36Formatted = new System.Text.StringBuilder(64);

        // PERFORMANCE: StringBuilder for minimized status
        private readonly System.Text.StringBuilder sbMinimizedStatus = new System.Text.StringBuilder(48);

        #endregion

        private void Awake()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            // Create canvas for status UI
            GameObject canvasGO = new GameObject(NAME_CANVAS);
            canvasGO.transform.SetParent(transform, false);

            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CANVAS_SORT_ORDER;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(REFERENCE_WIDTH, REFERENCE_HEIGHT);

            canvasGO.AddComponent<GraphicRaycaster>();

            // Create panel for status info (top-left corner) - starts minimized
            GameObject panelGO = new GameObject(NAME_PANEL);
            panelGO.transform.SetParent(canvasGO.transform, false);

            panelRect = panelGO.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0, 1);
            panelRect.anchorMax = new Vector2(0, 1);
            panelRect.pivot = new Vector2(0, 1);
            panelRect.anchoredPosition = new Vector2(PANEL_X, PANEL_Y);
            panelRect.sizeDelta = new Vector2(PANEL_WIDTH_MINIMIZED, PANEL_HEIGHT_MINIMIZED);

            Image panelImage = panelGO.AddComponent<Image>();
            panelImage.color = COLOR_PANEL_BG;

            // Build expanded content (all the detailed status info)
            BuildExpandedContent(panelGO.transform);

            // Build minimized content (compact status)
            BuildMinimizedContent(panelGO.transform);

            // Set initial visibility state
            UpdatePanelSize();
        }

        private void BuildExpandedContent(Transform panelTransform)
        {
            // Container for all expanded content
            expandedContent = new GameObject(NAME_EXPANDED_CONTENT);
            expandedContent.transform.SetParent(panelTransform, false);
            RectTransform expandedRect = expandedContent.AddComponent<RectTransform>();
            expandedRect.anchorMin = Vector2.zero;
            expandedRect.anchorMax = Vector2.one;
            expandedRect.offsetMin = Vector2.zero;
            expandedRect.offsetMax = Vector2.zero;

            // Create minimize button (positioned in top-right of header area)
            minimizeButton = CreateToggleButton(expandedContent.transform, NAME_MINIMIZE_BUTTON, LABEL_MINIMIZE,
                new Vector2(PANEL_WIDTH_EXPANDED - TOGGLE_BUTTON_SIZE - 10f, TOGGLE_BUTTON_Y),
                new Vector2(TOGGLE_BUTTON_SIZE, TOGGLE_BUTTON_SIZE), OnMinimizeClicked);

            // Create header text - larger and bold to distinguish it
            headerText = CreateText(expandedContent.transform, NAME_HEADER, LABEL_HEADER, FONT_SIZE_HEADER, new Vector2(LEFT_COL_X, HEADER_Y), new Vector2(HEADER_WIDTH, HEADER_HEIGHT), FontStyles.Bold);

            // Create time text - consistent spacing
            timeText = CreateText(expandedContent.transform, NAME_TIME, DEFAULT_TIME, FONT_SIZE_MAIN, new Vector2(LEFT_COL_X, TIME_Y), new Vector2(TEXT_WIDTH, TEXT_HEIGHT));

            // Create role text - consistent spacing (includes Authority ID on same line)
            roleText = CreateText(expandedContent.transform, NAME_ROLE, DEFAULT_ROLE, FONT_SIZE_MAIN, new Vector2(LEFT_COL_X, ROLE_Y), new Vector2(TEXT_WIDTH, TEXT_HEIGHT));

            // Create connection status text - consistent spacing
            connectionStatusText = CreateText(expandedContent.transform, NAME_CONNECTION, DEFAULT_CONNECTION, FONT_SIZE_MAIN, new Vector2(LEFT_COL_X, CONNECTION_Y), new Vector2(TEXT_WIDTH, TEXT_HEIGHT));

            // Create FPS text - right column
            fpsText = CreateText(expandedContent.transform, NAME_FPS, DEFAULT_FPS, FONT_SIZE_MAIN, new Vector2(RIGHT_COL_X, FPS_Y), new Vector2(RIGHT_COL_WIDTH, TEXT_HEIGHT));

            // Create RTT text - right column (clients only)
            rttText = CreateText(expandedContent.transform, NAME_RTT, DEFAULT_RTT, FONT_SIZE_MAIN, new Vector2(RIGHT_COL_X, RTT_Y), new Vector2(RIGHT_COL_WIDTH, TEXT_HEIGHT));

            // Create Network Simulation text - right column (dev builds only)
            netSimText = CreateText(expandedContent.transform, NAME_NETSIM, string.Empty, FONT_SIZE_NETSIM, new Vector2(RIGHT_COL_X, NETSIM_Y), new Vector2(RIGHT_COL_WIDTH, TEXT_HEIGHT));

            // Create Mesh status text - far right column (distributed host only)
            meshText = CreateText(expandedContent.transform, NAME_MESH, DEFAULT_MESH, FONT_SIZE_MAIN, new Vector2(FAR_RIGHT_COL_X, MESH_Y), new Vector2(RIGHT_COL_WIDTH, TEXT_HEIGHT));

            // Create Steam Information section (hidden by default, shown when using Steamworks)
            steamInfoSection = new GameObject(NAME_STEAM_SECTION);
            steamInfoSection.transform.SetParent(expandedContent.transform, false);
            RectTransform steamSectionRect = steamInfoSection.AddComponent<RectTransform>();
            steamSectionRect.anchorMin = new Vector2(0, 1);
            steamSectionRect.anchorMax = new Vector2(0, 1);
            steamSectionRect.pivot = new Vector2(0, 1);
            steamSectionRect.anchoredPosition = new Vector2(LEFT_COL_X, STEAM_SECTION_Y);
            steamSectionRect.sizeDelta = new Vector2(TEXT_WIDTH, STEAM_SECTION_HEIGHT);

            // Steam ID text and copy button
            steamIDText = CreateText(steamInfoSection.transform, NAME_STEAM_ID, DEFAULT_STEAM_ID, FONT_SIZE_STEAM, new Vector2(0, STEAM_ID_Y), new Vector2(STEAM_TEXT_WIDTH, STEAM_TEXT_HEIGHT));
            steamIDCopyButton = CreateButton(steamInfoSection.transform, NAME_STEAM_ID_COPY, LABEL_COPY_BUTTON, new Vector2(STEAM_BUTTON_X, STEAM_ID_Y), new Vector2(STEAM_BUTTON_WIDTH, STEAM_BUTTON_HEIGHT), CopySteamID);

            // Friendly code text and copy button
            friendlyCodeText = CreateText(steamInfoSection.transform, NAME_FRIENDLY_CODE, DEFAULT_FRIENDLY_CODE, FONT_SIZE_STEAM, new Vector2(0, FRIENDLY_CODE_Y), new Vector2(STEAM_TEXT_WIDTH, STEAM_TEXT_HEIGHT));
            friendlyCodeCopyButton = CreateButton(steamInfoSection.transform, NAME_FRIENDLY_CODE_COPY, LABEL_COPY_BUTTON, new Vector2(STEAM_BUTTON_X, FRIENDLY_CODE_Y), new Vector2(STEAM_BUTTON_WIDTH, STEAM_BUTTON_HEIGHT), CopyFriendlyCode);

            // Info text explaining what these are
            CreateText(steamInfoSection.transform, NAME_STEAM_HELP, LABEL_STEAM_HELP, FONT_SIZE_HELP, new Vector2(0, STEAM_HELP_Y), new Vector2(STEAM_HELP_WIDTH, STEAM_HELP_HEIGHT));

            steamInfoSection.SetActive(false);
        }

        private void BuildMinimizedContent(Transform panelTransform)
        {
            // Container for minimized content
            minimizedContent = new GameObject(NAME_MINIMIZED_CONTENT);
            minimizedContent.transform.SetParent(panelTransform, false);
            RectTransform minimizedRect = minimizedContent.AddComponent<RectTransform>();
            minimizedRect.anchorMin = Vector2.zero;
            minimizedRect.anchorMax = Vector2.one;
            minimizedRect.offsetMin = Vector2.zero;
            minimizedRect.offsetMax = Vector2.zero;

            // Create maximize button (positioned at left)
            maximizeButton = CreateToggleButton(minimizedContent.transform, NAME_MAXIMIZE_BUTTON, LABEL_MAXIMIZE,
                new Vector2(TOGGLE_BUTTON_X, -4f),
                new Vector2(TOGGLE_BUTTON_SIZE, TOGGLE_BUTTON_SIZE), OnMaximizeClicked);

            // Create compact status text (positioned after button)
            minimizedStatusText = CreateText(minimizedContent.transform, NAME_MINIMIZED_STATUS, LABEL_MINIMIZED_PREFIX + ROLE_UNKNOWN,
                FONT_SIZE_MAIN, new Vector2(TOGGLE_BUTTON_X + TOGGLE_BUTTON_SIZE + 8f, -4f),
                new Vector2(PANEL_WIDTH_MINIMIZED - TOGGLE_BUTTON_SIZE - 30f, TOGGLE_BUTTON_SIZE));
        }

        private TextMeshProUGUI CreateText(Transform parent, string name, string initialText, int fontSize, Vector2 position, Vector2 size, FontStyles fontStyle = FontStyles.Normal)
        {
            GameObject textGO = new GameObject(name);
            textGO.transform.SetParent(parent, false);

            RectTransform rect = textGO.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            TextMeshProUGUI text = textGO.AddComponent<TextMeshProUGUI>();
            text.text = initialText;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.color = Color.white;
            text.enableAutoSizing = true;
            text.fontSizeMin = FONT_SIZE_MIN;
            text.fontSizeMax = fontSize;

            return text;
        }

        private Button CreateButton(Transform parent, string name, string text, Vector2 position, Vector2 size, UnityEngine.Events.UnityAction onClick)
        {
            GameObject buttonGO = new GameObject(name);
            buttonGO.transform.SetParent(parent, false);

            RectTransform rect = buttonGO.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Image image = buttonGO.AddComponent<Image>();
            image.color = COLOR_BUTTON_BG;

            Button button = buttonGO.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);

            // Button text
            GameObject textGO = new GameObject(NAME_TEXT);
            textGO.transform.SetParent(buttonGO.transform, false);

            RectTransform textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = FONT_SIZE_BUTTON;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;

            return button;
        }

        private Button CreateToggleButton(Transform parent, string name, string label, Vector2 position, Vector2 size, UnityEngine.Events.UnityAction onClick)
        {
            GameObject buttonGO = new GameObject(name);
            buttonGO.transform.SetParent(parent, false);

            RectTransform rect = buttonGO.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Image image = buttonGO.AddComponent<Image>();
            image.color = COLOR_TOGGLE_BG;

            Button button = buttonGO.AddComponent<Button>();
            button.targetGraphic = image;

            ColorBlock colors = button.colors;
            colors.normalColor = COLOR_TOGGLE_BG;
            colors.highlightedColor = COLOR_TOGGLE_BG * 1.3f;
            colors.pressedColor = COLOR_TOGGLE_BG * 0.7f;
            colors.selectedColor = COLOR_TOGGLE_BG;
            button.colors = colors;

            button.onClick.AddListener(onClick);

            // Button label
            GameObject textGO = new GameObject(NAME_TEXT);
            textGO.transform.SetParent(buttonGO.transform, false);

            RectTransform textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = FONT_SIZE_TOGGLE;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.fontStyle = FontStyles.Bold;

            return button;
        }

        #region Minimize/Maximize

        private void OnMinimizeClicked()
        {
            isMinimized = true;
            UpdatePanelSize();
        }

        private void OnMaximizeClicked()
        {
            isMinimized = false;
            UpdatePanelSize();
        }

        /// <summary>
        /// Toggles between minimized and expanded states.
        /// Can be called externally or via the configured hotkey (default: F10).
        /// </summary>
        public void ToggleMinimized()
        {
            isMinimized = !isMinimized;
            UpdatePanelSize();
        }

        private void UpdatePanelSize()
        {
            if (isMinimized)
            {
                panelRect.sizeDelta = new Vector2(PANEL_WIDTH_MINIMIZED, PANEL_HEIGHT_MINIMIZED);
                expandedContent.SetActive(false);
                minimizedContent.SetActive(true);
            }
            else
            {
                panelRect.sizeDelta = new Vector2(PANEL_WIDTH_EXPANDED, PANEL_HEIGHT_EXPANDED);
                expandedContent.SetActive(true);
                minimizedContent.SetActive(false);
            }
        }

        #endregion

        private void Update()
        {
            // Handle hotkey toggle (check before UI disabled check so hotkey can re-enable)
            if (Input.GetKeyDown(toggleKey))
            {
                ToggleMinimized();
            }

            // PERFORMANCE: Early exit if UI is globally disabled
            if (!GONetGlobal.IsUIEnabled)
            {
                return;
            }

            float currentTime = Time.time;

            // PERFORMANCE: Throttle time text updates to 100ms intervals (was every frame)
            if (timeText != null && currentTime >= nextTimeUpdate)
            {
                nextTimeUpdate = currentTime + TIME_UPDATE_INTERVAL;

                float elapsedSeconds = (float)GONetMain.Time.ElapsedSeconds;
                if (Mathf.Abs(elapsedSeconds - lastTimeUpdate) > TIME_EPSILON)
                {
                    // PERFORMANCE: Use StringBuilder instead of string interpolation
                    sbTime.Clear();
                    sbTime.Append(LABEL_TIME_PREFIX);
                    sbTime.AppendFormat("{0:F3}", elapsedSeconds);
                    sbTime.Append(LABEL_TIME_SUFFIX);
                    timeText.SetText(sbTime);
                    lastTimeUpdate = elapsedSeconds;
                }
            }

            // Update FPS - every frame for smooth readings
            UpdateFPS();

            // Update RTT - can be throttled with status updates
            UpdateRTT();

            // Update NetSim display - only in dev builds
            UpdateNetSimDisplay();

            // Update Mesh status display
            UpdateMeshDisplay();

            // PERFORMANCE: Throttle status updates to 500ms intervals (was every frame)
            if (currentTime < nextStatusUpdate)
            {
                return;
            }
            nextStatusUpdate = currentTime + STATUS_UPDATE_INTERVAL;

            if (roleText != null)
            {
                string role = ROLE_UNKNOWN;
                Color roleColor = Color.white;

                if (GONetMain.IsServer && GONetMain.IsClient)
                {
                    role = ROLE_SERVER_CLIENT;
                }
                else if (GONetMain.IsServer)
                {
                    // PERFORMANCE: Cache GameObject.Find result and only check once per second
                    bool sampleServerExists = false;
                    if (currentTime >= nextServerGOCheck)
                    {
                        nextServerGOCheck = currentTime + SERVER_GO_CHECK_INTERVAL;
                        cachedServerGO = GameObject.Find(SERVER_GO_NAME);
                    }
                    sampleServerExists = cachedServerGO != null;

                    bool serverRunning = GONetMain.gonetServer != null && GONetMain.gonetServer.IsRunning;
                    bool serverFailed = GONetMain.gonetServer != null && !GONetMain.gonetServer.IsRunning;

                    if (serverFailed)
                    {
                        role = ROLE_SERVER_FAILED;
                        roleColor = Color.red;
                    }
                    else
                    {
                        wasServerSpawned |= (sampleServerExists || serverRunning);
                        role = wasServerSpawned ? ROLE_SERVER : ROLE_SERVER_STARTING;
                    }
                }
                else if (GONetMain.IsClient)
                {
                    role = ROLE_CLIENT;
                }

                ushort authorityId = GONetMain.MyAuthorityId;

                // PERFORMANCE: Use StringBuilder instead of string interpolation
                sbRole.Clear();
                sbRole.Append(LABEL_ROLE_PREFIX);
                sbRole.Append(role);
                sbRole.Append(LABEL_AUTHORITY_PREFIX);
                if (authorityId == 0 && !GONetMain.IsServer)
                {
                    sbRole.Append(LABEL_AUTHORITY_PENDING);
                }
                else
                {
                    sbRole.Append(authorityId);
                    sbRole.Append(LABEL_AUTHORITY_SUFFIX);
                }

                // Show port when acting as server (host)
                if (GONetMain.IsServer)
                {
                    // Use the main server's actual listening port (handles both original hosts and promoted client-hosts)
                    // GONetMain.gonetServer.ListeningPort is the definitive source for the main server's port
                    int port = GONetGlobal.ServerPort_Actual; // Fallback to config
                    if (GONetMain.gonetServer != null && GONetMain.gonetServer.ListeningPort > 0)
                    {
                        port = GONetMain.gonetServer.ListeningPort;
                    }
                    if (port > 0)
                    {
                        sbRole.Append(LABEL_PORT_PREFIX);
                        sbRole.Append(port);
                    }
                }

                string newRoleText = sbRole.ToString();

                if (newRoleText != lastRoleText || roleColor != lastRoleColor)
                {
                    roleText.SetText(sbRole);
                    roleText.color = roleColor;
                    lastRoleText = newRoleText;
                    lastRoleColor = roleColor;
                }

                // Update minimized status text with compact role display
                // NOTE: For clients, the color will be updated below based on connection status
                // NOTE: For servers, the client count will be appended below
                if (minimizedStatusText != null)
                {
                    sbMinimizedStatus.Clear();
                    sbMinimizedStatus.Append(LABEL_MINIMIZED_PREFIX);
                    sbMinimizedStatus.Append(role);
                    // For servers, client count will be appended in the connection status section below
                    // For pure clients, append authority ID for identification (e.g., "CLIENT: 5")
                    if (!GONetMain.IsServer && GONetMain.IsClient)
                    {
                        sbMinimizedStatus.Append(LABEL_MINIMIZED_CLIENT_AUTHORITY_PREFIX);
                        sbMinimizedStatus.Append(authorityId);
                        minimizedStatusText.text = sbMinimizedStatus.ToString();
                    }
                    minimizedStatusText.color = roleColor;
                }
            }

            if (connectionStatusText != null)
            {
                string connectionStatus = STATUS_NOT_CONNECTED;
                Color statusColor = Color.red;

                if (GONetMain.IsServer)
                {
                    uint clientCount = GONetMain.gonetServer != null ? GONetMain.gonetServer.numConnections : 0;
                    int maxClients = GONetMain.gonetServer != null ? GONetMain.gonetServer.MaxClientCount : 0;

                    // PERFORMANCE: Use StringBuilder for client count display
                    sbConnection.Clear();
                    sbConnection.Append(clientCount);
                    sbConnection.Append(LABEL_CLIENTS_SEPARATOR);
                    sbConnection.Append(maxClients);
                    sbConnection.Append(LABEL_CLIENTS_SUFFIX);
                    connectionStatus = sbConnection.ToString();

                    statusColor = clientCount == 0 ? Color.yellow : Color.white;

                    // Update minimized status with client count for server
                    if (minimizedStatusText != null)
                    {
                        sbMinimizedStatus.Append(LABEL_MINIMIZED_SERVER_CLIENTS_PREFIX);
                        sbMinimizedStatus.Append(clientCount);
                        sbMinimizedStatus.Append(LABEL_MINIMIZED_SERVER_CLIENTS_SEPARATOR);
                        sbMinimizedStatus.Append(maxClients);
                        sbMinimizedStatus.Append(LABEL_MINIMIZED_SERVER_CLIENTS_SUFFIX);
                        minimizedStatusText.text = sbMinimizedStatus.ToString();
                        minimizedStatusText.color = statusColor;
                    }
                }
                else if (GONetMain.IsClient && GONetMain.GONetClient != null)
                {
                    var transport = GONetMain.GONetClient.Transport;
                    if (transport != null && transport is GONet.Transport.SteamworksTransport steamTransport)
                    {
                        var transportState = steamTransport.ClientState;
                        connectionStatus = transportState switch
                        {
                            GONet.Transport.GONetTransportClientState.Connected => STATUS_CONNECTED,
                            GONet.Transport.GONetTransportClientState.Connecting => STATUS_CONNECTING,
                            GONet.Transport.GONetTransportClientState.Disconnected => STATUS_DISCONNECTED,
                            GONet.Transport.GONetTransportClientState.Error => STATUS_ERROR,
                            _ => STATUS_UNKNOWN
                        };

                        if (transportState == GONet.Transport.GONetTransportClientState.Connected)
                        {
                            bool isInitialized = GONetMain.GONetClient.IsInitializedWithServer;
                            if (isInitialized)
                            {
                                connectionStatus = STATUS_INITIALIZED;
                                statusColor = Color.green;
                            }
                            else
                            {
                                connectionStatus = STATUS_INITIALIZING;
                                statusColor = Color.yellow;
                            }
                        }
                        else if (transportState == GONet.Transport.GONetTransportClientState.Connecting)
                        {
                            statusColor = Color.yellow;
                        }
                        else
                        {
                            statusColor = Color.red;
                        }
                    }
                    else
                    {
                        var state = GONetMain.GONetClient.ConnectionState;
                        connectionStatus = state switch
                        {
                            NetcodeIO.NET.ClientState.Connected => STATUS_CONNECTED,
                            NetcodeIO.NET.ClientState.SendingConnectionRequest => STATUS_CONNECTING,
                            NetcodeIO.NET.ClientState.SendingChallengeResponse => STATUS_CONNECTING,
                            NetcodeIO.NET.ClientState.Disconnected => STATUS_DISCONNECTED,
                            NetcodeIO.NET.ClientState.ConnectionDenied => STATUS_DENIED,
                            NetcodeIO.NET.ClientState.ConnectionRequestTimedOut => STATUS_TIMEOUT,
                            NetcodeIO.NET.ClientState.ChallengeResponseTimedOut => STATUS_TIMEOUT,
                            NetcodeIO.NET.ClientState.ConnectionTimedOut => STATUS_TIMEOUT,
                            NetcodeIO.NET.ClientState.InvalidConnectToken => STATUS_INVALID_TOKEN,
                            NetcodeIO.NET.ClientState.ConnectTokenExpired => STATUS_TOKEN_EXPIRED,
                            _ => STATUS_UNKNOWN
                        };

                        if (state == NetcodeIO.NET.ClientState.Connected)
                        {
                            bool isInitialized = GONetMain.GONetClient.IsInitializedWithServer;
                            if (isInitialized)
                            {
                                connectionStatus = STATUS_INITIALIZED;
                                statusColor = Color.green;
                            }
                            else
                            {
                                connectionStatus = STATUS_INITIALIZING;
                                statusColor = Color.yellow;
                            }
                        }
                        else if (state == NetcodeIO.NET.ClientState.SendingConnectionRequest ||
                                 state == NetcodeIO.NET.ClientState.SendingChallengeResponse)
                        {
                            statusColor = Color.yellow;
                        }
                        else
                        {
                            statusColor = Color.red;
                        }
                    }

                    // Update minimized status color for clients to match connection status
                    if (minimizedStatusText != null)
                    {
                        minimizedStatusText.color = statusColor;
                    }
                }

                // PERFORMANCE: Use StringBuilder for final connection text
                sbConnection.Clear();
                sbConnection.Append(LABEL_CONNECTION_PREFIX);
                sbConnection.Append(connectionStatus);
                string newConnectionText = sbConnection.ToString();

                if (newConnectionText != lastConnectionStatus || statusColor != lastConnectionColor)
                {
                    connectionStatusText.SetText(sbConnection);
                    connectionStatusText.color = statusColor;
                    lastConnectionStatus = newConnectionText;
                    lastConnectionColor = statusColor;
                }
            }

            UpdateSteamInfo();
        }

        private void UpdateSteamInfo()
        {
            bool showSteamInfo = false;

            GONetGlobal gonetGlobal = GONetGlobal.Instance;
            if (gonetGlobal != null)
            {
                showSteamInfo = gonetGlobal.transportType == GONetTransportType.Steamworks;
            }

            if (steamInfoSection != null && showSteamInfo != lastSteamInfoVisibility)
            {
                steamInfoSection.SetActive(showSteamInfo);
                lastSteamInfoVisibility = showSteamInfo;
            }

            if (showSteamInfo && GONetSteamManager.IsInitialized)
            {
                try
                {
                    Steamworks.CSteamID localSteamID = Steamworks.SteamUser.GetSteamID();
                    ulong steamIDValue = localSteamID.m_SteamID;

                    // PERFORMANCE: Use StringBuilder for Steam ID text
                    sbSteamId.Clear();
                    sbSteamId.Append(LABEL_STEAM_ID_PREFIX);
                    sbSteamId.Append(steamIDValue);
                    string newSteamIDText = sbSteamId.ToString();

                    // PERFORMANCE: Use StringBuilder for friendly code
                    string friendlyCodeColored = SteamIDToFriendlyCode(steamIDValue, includeColorCoding: true);
                    sbFriendlyCode.Clear();
                    sbFriendlyCode.Append(LABEL_FRIENDLY_CODE_PREFIX);
                    sbFriendlyCode.Append(friendlyCodeColored);
                    string newFriendlyCodeText = sbFriendlyCode.ToString();

                    if (steamIDText != null && newSteamIDText != lastSteamIDText)
                    {
                        steamIDText.SetText(sbSteamId);
                        steamIDText.color = COLOR_STEAM_TEXT;
                        lastSteamIDText = newSteamIDText;
                    }

                    if (friendlyCodeText != null && newFriendlyCodeText != lastFriendlyCodeText)
                    {
                        friendlyCodeText.SetText(sbFriendlyCode);
                        friendlyCodeText.color = Color.white;
                        friendlyCodeText.richText = true;
                        lastFriendlyCodeText = newFriendlyCodeText;
                    }
                }
                catch (System.Exception ex)
                {
                    sbSteamId.Clear();
                    sbSteamId.Append(LOG_STEAM_ERROR_PREFIX);
                    sbSteamId.Append(ex.Message);
                    string errorText = sbSteamId.ToString();

                    if (steamIDText != null && errorText != lastSteamIDText)
                    {
                        steamIDText.SetText(sbSteamId);
                        steamIDText.color = Color.red;
                        lastSteamIDText = errorText;
                    }
                }
            }
        }

        private void CopySteamID()
        {
            if (GONetSteamManager.IsInitialized)
            {
                try
                {
                    Steamworks.CSteamID localSteamID = Steamworks.SteamUser.GetSteamID();
                    string steamIDString = localSteamID.m_SteamID.ToString();
                    GUIUtility.systemCopyBuffer = steamIDString;
                    GONetLog.Info(LOG_PREFIX + LOG_COPIED_STEAM_ID + steamIDString);
                }
                catch (System.Exception ex)
                {
                    GONetLog.Error(LOG_PREFIX + LOG_FAILED_COPY_STEAM + ex.Message);
                }
            }
        }

        private void CopyFriendlyCode()
        {
            if (GONetSteamManager.IsInitialized)
            {
                try
                {
                    Steamworks.CSteamID localSteamID = Steamworks.SteamUser.GetSteamID();
                    ulong steamIDValue = localSteamID.m_SteamID;
                    string friendlyCode = SteamIDToFriendlyCode(steamIDValue, includeColorCoding: false);
                    GUIUtility.systemCopyBuffer = friendlyCode;
                    GONetLog.Info(LOG_PREFIX + LOG_COPIED_FRIENDLY + friendlyCode);
                }
                catch (System.Exception ex)
                {
                    GONetLog.Error(LOG_PREFIX + LOG_FAILED_COPY_FRIENDLY + ex.Message);
                }
            }
        }

        /// <summary>
        /// Converts a Steam ID (ulong) to a human-friendly Base-36 code with smart grouping and color coding.
        /// Example: 76561197960287930 -> "DXE-5C7J-KL2" (with colors: letters=cyan, numbers=orange)
        /// Groups: 3-5 characters, as uniform as possible
        /// </summary>
        private string SteamIDToFriendlyCode(ulong steamID, bool includeColorCoding = false)
        {
            if (steamID == 0)
            {
                return BASE36_ZERO;
            }

            // PERFORMANCE: Use instance StringBuilder to avoid allocation
            sbBase36.Clear();
            ulong value = steamID;

            while (value > 0)
            {
                int remainder = (int)(value % 36);
                sbBase36.Insert(0, BASE36_CHARS[remainder]);
                value /= 36;
            }

            int encodedLength = sbBase36.Length;
            int[] groupSizes = CalculateOptimalGroupSizes(encodedLength);

            // PERFORMANCE: Use instance StringBuilder for formatted output
            sbBase36Formatted.Clear();
            int charIndex = 0;

            for (int groupIndex = 0; groupIndex < groupSizes.Length; groupIndex++)
            {
                if (groupIndex > 0)
                {
                    sbBase36Formatted.Append('-');
                }

                int groupSize = groupSizes[groupIndex];
                for (int i = 0; i < groupSize; i++)
                {
                    char c = sbBase36[charIndex++];

                    if (includeColorCoding)
                    {
                        if (char.IsDigit(c))
                        {
                            sbBase36Formatted.Append(COLOR_TAG_ORANGE_START);
                            sbBase36Formatted.Append(c);
                            sbBase36Formatted.Append(COLOR_TAG_END);
                        }
                        else
                        {
                            sbBase36Formatted.Append(COLOR_TAG_CYAN_START);
                            sbBase36Formatted.Append(c);
                            sbBase36Formatted.Append(COLOR_TAG_END);
                        }
                    }
                    else
                    {
                        sbBase36Formatted.Append(c);
                    }
                }
            }

            return sbBase36Formatted.ToString();
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

        private void UpdateFPS()
        {
            if (fpsText == null) return;

            float currentFps = 1f / Time.unscaledDeltaTime;

            // Rolling average for smooth FPS display
            fpsSum -= fpsBuffer[fpsBufferIndex];
            fpsBuffer[fpsBufferIndex] = currentFps;
            fpsSum += currentFps;
            fpsBufferIndex = (fpsBufferIndex + 1) % fpsBuffer.Length;

            float avgFps = fpsSum / fpsBuffer.Length;

            // PERFORMANCE: Use StringBuilder
            sbFps.Clear();
            sbFps.Append(LABEL_FPS_PREFIX);
            sbFps.AppendFormat("{0:F0}", avgFps);
            string newFpsText = sbFps.ToString();

            if (newFpsText != lastFpsText)
            {
                fpsText.SetText(sbFps);
                fpsText.color = avgFps >= FPS_GOOD_THRESHOLD ? Color.green : (avgFps >= FPS_WARN_THRESHOLD ? Color.yellow : Color.red);
                lastFpsText = newFpsText;
            }
        }

        private void UpdateRTT()
        {
            if (rttText == null) return;

            string newRttText = DEFAULT_RTT;
            Color rttColor = Color.white;

            if (GONetMain.IsClient && !GONetMain.IsServer && GONetMain.GONetClient?.connectionToServer != null)
            {
                var conn = GONetMain.GONetClient.connectionToServer;
                float rttMs = conn.RTT_Latest * MS_CONVERSION;
                float avgRttMs = conn.RTT_RecentAverage * MS_CONVERSION;

                // PERFORMANCE: Use StringBuilder
                sbRtt.Clear();
                sbRtt.Append(LABEL_RTT_PREFIX);
                sbRtt.AppendFormat("{0:F0}", rttMs);
                sbRtt.Append(LABEL_RTT_MS);
                sbRtt.AppendFormat("{0:F0}", avgRttMs);
                sbRtt.Append(LABEL_RTT_SUFFIX);

                // DIAGNOSTIC: Show timestamp ratio (1.0 = good, 0.0 = bad)
                #if UNITY_EDITOR || DEVELOPMENT_BUILD
                float tsRatio = GONetMain.TimestampProvidedRatio;
                sbRtt.Append(" ts:");
                sbRtt.AppendFormat("{0:P0}", tsRatio);
                #endif

                newRttText = sbRtt.ToString();

                if (avgRttMs < RTT_GOOD_THRESHOLD) rttColor = Color.green;
                else if (avgRttMs < RTT_WARN_THRESHOLD) rttColor = Color.yellow;
                else rttColor = COLOR_HIGH_LATENCY;
            }
            else if (GONetMain.IsServer)
            {
                newRttText = LABEL_RTT_NONE;
            }

            if (newRttText != lastRttText)
            {
                if (newRttText == DEFAULT_RTT || newRttText == LABEL_RTT_NONE)
                {
                    rttText.text = newRttText;
                }
                else
                {
                    rttText.SetText(sbRtt);
                }
                rttText.color = rttColor;
                lastRttText = newRttText;
            }
        }

        private void UpdateNetSimDisplay()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (netSimText == null) return;

            string newNetSimText = string.Empty;

            GONetGlobal gonetGlobal = GONetGlobal.Instance;
            if (gonetGlobal != null && gonetGlobal.networkSimulation_Enabled)
            {
                int latency = gonetGlobal.networkSimulation_LatencyMs;
                int jitter = gonetGlobal.networkSimulation_JitterMs;
                float loss = gonetGlobal.networkSimulation_PacketLossPercent;

                // PERFORMANCE: Use StringBuilder
                sbNetSim.Clear();
                sbNetSim.Append(LABEL_NETSIM_PREFIX);
                sbNetSim.Append(latency);
                sbNetSim.Append(LABEL_NETSIM_MS);
                sbNetSim.Append(jitter);
                sbNetSim.Append(LABEL_NETSIM_LOSS);
                sbNetSim.AppendFormat("{0:F1}", loss);
                sbNetSim.Append(LABEL_NETSIM_LOSS_SUFFIX);
                newNetSimText = sbNetSim.ToString();
            }

            if (newNetSimText != lastNetSimText)
            {
                if (string.IsNullOrEmpty(newNetSimText))
                {
                    netSimText.text = string.Empty;
                }
                else
                {
                    netSimText.SetText(sbNetSim);
                }
                netSimText.color = COLOR_NETSIM;
                lastNetSimText = newNetSimText;
            }
#endif
        }

        private void UpdateMeshDisplay()
        {
            if (meshText == null) return;

            string newMeshText = DEFAULT_MESH;
            Color meshColor = Color.white;

            GONetGlobal gonetGlobal = GONetGlobal.Instance;
            if (gonetGlobal == null || !gonetGlobal.enableDistributedHostAuthority)
            {
                newMeshText = LABEL_MESH_DISABLED;
                meshColor = Color.gray;
            }
            else if (DistributedHost.GONetHotStandbyManager.Instance.IsInitialized)
            {
                int connectedPeers = DistributedHost.GONetHotStandbyManager.Instance.ActiveStandbyConnectionCount;
                int dormantClients = DistributedHost.GONetHotStandbyManager.Instance.DormantServerConnectionCount;

                // PERFORMANCE: Use StringBuilder
                sbMesh.Clear();
                sbMesh.Append(LABEL_MESH_PREFIX);
                sbMesh.Append(connectedPeers);
                sbMesh.Append(LABEL_MESH_SEPARATOR);
                sbMesh.Append(dormantClients);
                sbMesh.Append(LABEL_MESH_PEERS);

                // Show the hot standby port this process is listening on
                int hotStandbyPort = DistributedHost.GONetHotStandbyManager.Instance.DormantServerPort;
                if (hotStandbyPort > 0)
                {
                    sbMesh.Append(':');
                    sbMesh.Append(hotStandbyPort);
                }
                newMeshText = sbMesh.ToString();

                // Color coding: green if we have both outgoing and incoming connections, yellow if partial, red if none
                if (connectedPeers > 0 && dormantClients > 0)
                {
                    meshColor = Color.green;
                }
                else if (connectedPeers > 0 || dormantClients > 0)
                {
                    meshColor = Color.yellow;
                }
                else
                {
                    meshColor = Color.red;
                }
            }

            if (newMeshText != lastMeshText)
            {
                if (newMeshText == DEFAULT_MESH || newMeshText == LABEL_MESH_DISABLED)
                {
                    meshText.text = newMeshText;
                }
                else
                {
                    meshText.SetText(sbMesh);
                }
                meshText.color = meshColor;
                lastMeshText = newMeshText;
            }
        }
    }
}
