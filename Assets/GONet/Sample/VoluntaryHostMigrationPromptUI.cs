using GONet;
using GONet.DistributedHost;
using GONet.Utils;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace GONet.Sample
{
    /// <summary>
    /// Diagnostic UI for voluntary host migration.
    /// Shows real-time progress toward migration eligibility with minimize/maximize support.
    /// </summary>
    public class VoluntaryHostMigrationPromptUI : MonoBehaviour
    {
        [Header("Layout")]
        [SerializeField] private float panelX = 20f;
        [SerializeField] private float panelY = 20f;
        [SerializeField] private float expandedWidth = 420f;
        [SerializeField] private float expandedHeight = 280f;
        [SerializeField] private float minimizedWidth = 180f;
        [SerializeField] private float minimizedHeight = 36f;

        [Header("Colors")]
        [SerializeField] private Color metColor = new Color(0.2f, 0.7f, 0.3f);
        [SerializeField] private Color notMetColor = new Color(0.7f, 0.3f, 0.2f);
        [SerializeField] private Color pendingColor = new Color(0.6f, 0.6f, 0.2f);
        [SerializeField] private Color buttonReadyColor = new Color(0.2f, 0.6f, 0.3f);
        [SerializeField] private Color buttonFillColor = new Color(0.15f, 0.4f, 0.2f);
        [SerializeField] private Color buttonDisabledColor = new Color(0.3f, 0.3f, 0.3f);

        private Canvas canvas;
        private GameObject panelGO;
        private RectTransform panelRect;
        private Image panelImage;

        // Expanded view components
        private GameObject expandedContent;
        private TextMeshProUGUI titleText;
        private TextMeshProUGUI candidateText;
        private TextMeshProUGUI scoresText;
        private TextMeshProUGUI differenceText;
        private TextMeshProUGUI[] thresholdTexts;
        private Image[] thresholdIcons;
        private GameObject buttonContainer;
        private Button acceptButton;
        private Image acceptButtonBg;
        private Image acceptButtonFill;
        private TextMeshProUGUI acceptButtonLabel;
        private Button dismissButton;
        private Button minimizeButton;

        // Minimized view components
        private GameObject minimizedContent;
        private TextMeshProUGUI minimizedText;
        private Button maximizeButton;

        private bool isMinimized = true;
        private bool hasEverShownPanel = false;

        private readonly string[] thresholdLabels = new string[]
        {
            "Score threshold",
            "Point minimum",
            "Uptime",
            "Sustained",
            "Cooldown"
        };

        private void Awake() { BuildUI(); }

        private void BuildUI()
        {
            // Canvas setup
            GameObject canvasGO = new GameObject("VoluntaryHostMigrationCanvas");
            canvasGO.transform.SetParent(transform);
            canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 110;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGO.AddComponent<GraphicRaycaster>();

            // Main panel
            panelGO = new GameObject("MigrationPanel");
            panelGO.transform.SetParent(canvasGO.transform, false);

            panelRect = panelGO.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0, 0);
            panelRect.anchorMax = new Vector2(0, 0);
            panelRect.pivot = new Vector2(0, 0);
            panelRect.anchoredPosition = new Vector2(panelX, panelY);
            panelRect.sizeDelta = new Vector2(minimizedWidth, minimizedHeight);

            panelImage = panelGO.AddComponent<Image>();
            panelImage.color = new Color(0.15f, 0.15f, 0.2f, 0.92f);

            BuildExpandedContent();
            BuildMinimizedContent();

            // Start hidden
            panelGO.SetActive(false);
        }

        private void BuildExpandedContent()
        {
            expandedContent = new GameObject("ExpandedContent");
            expandedContent.transform.SetParent(panelGO.transform, false);
            RectTransform expandedRect = expandedContent.AddComponent<RectTransform>();
            expandedRect.anchorMin = Vector2.zero;
            expandedRect.anchorMax = Vector2.one;
            expandedRect.offsetMin = Vector2.zero;
            expandedRect.offsetMax = Vector2.zero;

            VerticalLayoutGroup layout = expandedContent.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 10, 10);
            layout.spacing = 4f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            // Header row with title and minimize button
            GameObject headerRow = CreateHorizontalContainer(expandedContent.transform, 28);
            titleText = CreateText(headerRow.transform, "Host Migration Status", 18, new Color(1f, 0.85f, 0.2f));
            titleText.GetComponent<RectTransform>().SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 320);
            minimizeButton = CreateIconButton(headerRow.transform, "−", 24, 24, new Color(0.4f, 0.4f, 0.4f), OnMinimizeClicked);

            // Candidate info
            candidateText = CreateText(expandedContent.transform, "Candidate: --", 16, Color.white);
            scoresText = CreateText(expandedContent.transform, "Scores: -- vs --", 14, new Color(0.8f, 0.8f, 0.8f));
            differenceText = CreateText(expandedContent.transform, "Difference: --", 14, new Color(0.8f, 0.8f, 0.8f));

            // Separator
            CreateSeparator(expandedContent.transform);

            // Threshold rows
            thresholdTexts = new TextMeshProUGUI[5];
            thresholdIcons = new Image[5];
            for (int i = 0; i < 5; i++)
            {
                GameObject row = CreateHorizontalContainer(expandedContent.transform, 22);
                thresholdIcons[i] = CreateStatusIcon(row.transform);
                thresholdTexts[i] = CreateText(row.transform, thresholdLabels[i], 14, Color.white);
                // Give the text enough width to display full content
                thresholdTexts[i].GetComponent<RectTransform>().sizeDelta = new Vector2(360, 22);
            }

            // Separator
            CreateSeparator(expandedContent.transform);

            // Buttons
            buttonContainer = CreateHorizontalContainer(expandedContent.transform, 40);
            buttonContainer.GetComponent<HorizontalLayoutGroup>().spacing = 10f;

            // Accept button with fill effect
            GameObject acceptBtnGO = new GameObject("AcceptButton");
            acceptBtnGO.transform.SetParent(buttonContainer.transform, false);
            RectTransform acceptRect = acceptBtnGO.AddComponent<RectTransform>();
            acceptRect.sizeDelta = new Vector2(180, 36);

            acceptButtonBg = acceptBtnGO.AddComponent<Image>();
            acceptButtonBg.color = buttonDisabledColor;

            acceptButton = acceptBtnGO.AddComponent<Button>();
            acceptButton.targetGraphic = acceptButtonBg;
            acceptButton.interactable = false;
            acceptButton.onClick.AddListener(OnAcceptClicked);

            // Fill overlay
            GameObject fillGO = new GameObject("Fill");
            fillGO.transform.SetParent(acceptBtnGO.transform, false);
            RectTransform fillRect = fillGO.AddComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            acceptButtonFill = fillGO.AddComponent<Image>();
            acceptButtonFill.color = buttonFillColor;
            acceptButtonFill.type = Image.Type.Filled;
            acceptButtonFill.fillMethod = Image.FillMethod.Horizontal;
            acceptButtonFill.fillOrigin = 0;
            acceptButtonFill.fillAmount = 0f;

            // Button label
            GameObject labelGO = new GameObject("Label");
            labelGO.transform.SetParent(acceptBtnGO.transform, false);
            RectTransform labelRect = labelGO.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            acceptButtonLabel = labelGO.AddComponent<TextMeshProUGUI>();
            acceptButtonLabel.text = "Accept Migration";
            acceptButtonLabel.fontSize = 15;
            acceptButtonLabel.color = Color.white;
            acceptButtonLabel.alignment = TextAlignmentOptions.Center;
            acceptButtonLabel.fontStyle = FontStyles.Bold;

            // Dismiss button
            dismissButton = CreateButton(buttonContainer.transform, "Dismiss", 100, 36, new Color(0.5f, 0.3f, 0.3f), OnDismissClicked);

            expandedContent.SetActive(false);
        }

        private void BuildMinimizedContent()
        {
            minimizedContent = new GameObject("MinimizedContent");
            minimizedContent.transform.SetParent(panelGO.transform, false);
            RectTransform minimizedRect = minimizedContent.AddComponent<RectTransform>();
            minimizedRect.anchorMin = Vector2.zero;
            minimizedRect.anchorMax = Vector2.one;
            minimizedRect.offsetMin = Vector2.zero;
            minimizedRect.offsetMax = Vector2.zero;

            HorizontalLayoutGroup layout = minimizedContent.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 6, 6);
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            maximizeButton = CreateIconButton(minimizedContent.transform, "+", 24, 24, new Color(0.4f, 0.6f, 0.4f), OnMaximizeClicked);
            minimizedText = CreateText(minimizedContent.transform, "Migration: --", 14, new Color(0.9f, 0.9f, 0.9f));
            minimizedText.GetComponent<RectTransform>().sizeDelta = new Vector2(130, 24);

            minimizedContent.SetActive(true);
        }

        private GameObject CreateHorizontalContainer(Transform parent, float height)
        {
            GameObject container = new GameObject("Row");
            container.transform.SetParent(parent, false);
            RectTransform rect = container.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0, height);

            HorizontalLayoutGroup layout = container.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            return container;
        }

        private void CreateSeparator(Transform parent)
        {
            GameObject sep = new GameObject("Separator");
            sep.transform.SetParent(parent, false);
            RectTransform rect = sep.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0, 2);
            Image img = sep.AddComponent<Image>();
            img.color = new Color(0.3f, 0.3f, 0.35f, 0.8f);
        }

        private Image CreateStatusIcon(Transform parent)
        {
            GameObject iconGO = new GameObject("Icon");
            iconGO.transform.SetParent(parent, false);
            RectTransform rect = iconGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(18, 18);
            Image img = iconGO.AddComponent<Image>();
            img.color = pendingColor;
            return img;
        }

        private TextMeshProUGUI CreateText(Transform parent, string initialText, float fontSize, Color color)
        {
            GameObject textGO = new GameObject("Text");
            textGO.transform.SetParent(parent, false);
            RectTransform rect = textGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0, fontSize + 8);
            TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = initialText;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.enableAutoSizing = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return tmp;
        }

        private Button CreateButton(Transform parent, string label, float width, float height, Color bgColor, UnityEngine.Events.UnityAction onClick)
        {
            GameObject btnGO = new GameObject(label + "Button");
            btnGO.transform.SetParent(parent, false);
            RectTransform rect = btnGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, height);
            Image btnImage = btnGO.AddComponent<Image>();
            btnImage.color = bgColor;
            Button btn = btnGO.AddComponent<Button>();
            ColorBlock colors = btn.colors;
            colors.normalColor = bgColor;
            colors.highlightedColor = bgColor * 1.2f;
            colors.pressedColor = bgColor * 0.8f;
            colors.selectedColor = bgColor;
            btn.colors = colors;
            btn.onClick.AddListener(onClick);

            GameObject labelGO = new GameObject("Label");
            labelGO.transform.SetParent(btnGO.transform, false);
            RectTransform labelRect = labelGO.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            TextMeshProUGUI labelTMP = labelGO.AddComponent<TextMeshProUGUI>();
            labelTMP.text = label;
            labelTMP.fontSize = 14;
            labelTMP.color = Color.white;
            labelTMP.alignment = TextAlignmentOptions.Center;
            labelTMP.fontStyle = FontStyles.Bold;
            return btn;
        }

        private Button CreateIconButton(Transform parent, string icon, float width, float height, Color bgColor, UnityEngine.Events.UnityAction onClick)
        {
            GameObject btnGO = new GameObject(icon + "Button");
            btnGO.transform.SetParent(parent, false);
            RectTransform rect = btnGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, height);
            Image btnImage = btnGO.AddComponent<Image>();
            btnImage.color = bgColor;
            Button btn = btnGO.AddComponent<Button>();
            ColorBlock colors = btn.colors;
            colors.normalColor = bgColor;
            colors.highlightedColor = bgColor * 1.3f;
            colors.pressedColor = bgColor * 0.7f;
            colors.selectedColor = bgColor;
            btn.colors = colors;
            btn.onClick.AddListener(onClick);

            GameObject labelGO = new GameObject("Label");
            labelGO.transform.SetParent(btnGO.transform, false);
            RectTransform labelRect = labelGO.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            TextMeshProUGUI labelTMP = labelGO.AddComponent<TextMeshProUGUI>();
            labelTMP.text = icon;
            labelTMP.fontSize = 18;
            labelTMP.color = Color.white;
            labelTMP.alignment = TextAlignmentOptions.Center;
            labelTMP.fontStyle = FontStyles.Bold;
            return btn;
        }

        private void OnAcceptClicked()
        {
            var result = GONetMain.Server_InitiateVoluntaryHostMigration();
            if (result == GONetMain.VoluntaryMigrationResult.Success)
            {
                HidePanel();
            }
            else
            {
                GONetLog.Warning("[VoluntaryHostMigrationPromptUI] Migration rejected: " + result);
            }
        }

        private void OnDismissClicked() { HidePanel(); }

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

        private void UpdatePanelSize()
        {
            if (isMinimized)
            {
                panelRect.sizeDelta = new Vector2(minimizedWidth, minimizedHeight);
                expandedContent.SetActive(false);
                minimizedContent.SetActive(true);
            }
            else
            {
                panelRect.sizeDelta = new Vector2(expandedWidth, expandedHeight);
                expandedContent.SetActive(true);
                minimizedContent.SetActive(false);
            }
        }

        private void ShowPanel()
        {
            if (panelGO != null)
            {
                panelGO.SetActive(true);
                if (!hasEverShownPanel)
                {
                    hasEverShownPanel = true;
                    isMinimized = true;
                    UpdatePanelSize();
                }
            }
        }

        private void HidePanel()
        {
            if (panelGO != null) panelGO.SetActive(false);
            hasEverShownPanel = false;
        }

        private void Update()
        {
            // Only show this UI when distributed host authority is enabled
            bool distributedHostEnabled = GONetGlobal.Instance != null && GONetGlobal.Instance.enableDistributedHostAuthority;

            if (!GONetMain.IsServer || !distributedHostEnabled)
            {
                if (panelGO != null && panelGO.activeSelf) HidePanel();
                return;
            }

            // Show panel when we're the server and distributed host is enabled
            ShowPanel();

            // Poll diagnostics from vice host manager
            if (GONetViceHostManager.Instance == null) return;
            if (GONetViceHostManager.Instance.TryGetBetterHostDiagnostics(out var diag))
            {
                UpdateDisplay(diag);
            }
            else
            {
                // No diagnostics available yet - show waiting state
                UpdateDisplayNoData();
            }
        }

        private void UpdateDisplayNoData()
        {
            minimizedText.text = "Waiting...";
            minimizedText.color = new Color(0.6f, 0.6f, 0.6f);

            if (!isMinimized)
            {
                candidateText.text = "Candidate: --";
                scoresText.text = "Scores: --";
                differenceText.text = "Difference: --";
                for (int i = 0; i < 5; i++)
                {
                    thresholdIcons[i].color = new Color(0.4f, 0.4f, 0.4f);
                    thresholdTexts[i].text = $"○ {thresholdLabels[i]}: --";
                    thresholdTexts[i].color = new Color(0.6f, 0.6f, 0.6f);
                }
                acceptButton.interactable = false;
                acceptButtonBg.color = buttonDisabledColor;
                acceptButtonFill.fillAmount = 0f;
                acceptButtonLabel.text = "No data";
            }
        }

        private void UpdateDisplay(GONetViceHostManager.BetterHostDiagnostics diag)
        {
            // Update minimized text
            if (diag.IsMigrationReady)
            {
                minimizedText.text = "Migration READY";
                minimizedText.color = metColor;
            }
            else if (diag.HasPreviewCandidate)
            {
                int progress = Mathf.RoundToInt(diag.SampleProgress * 100);
                minimizedText.text = $"Warming: {progress}%";
                minimizedText.color = pendingColor;
            }
            else
            {
                minimizedText.text = "No candidate";
                minimizedText.color = new Color(0.6f, 0.6f, 0.6f);
            }

            // Update expanded content
            if (!isMinimized)
            {
                UpdateExpandedDisplay(diag);
            }
        }

        private void UpdateExpandedDisplay(GONetViceHostManager.BetterHostDiagnostics diag)
        {
            // Candidate info
            if (diag.CandidateAuthorityId > 0)
            {
                candidateText.text = $"Candidate: Authority {diag.CandidateAuthorityId} (Score: {diag.CandidateScore:F0})";
                scoresText.text = $"You: {diag.HostScore:F0}  vs  Candidate: {diag.CandidateScore:F0}";
                differenceText.text = $"Difference: +{diag.ScoreDifferenceRatio * 100:F0}% (+{diag.ScoreDifferenceAbsolute:F0} pts)";
            }
            else
            {
                candidateText.text = "Candidate: None";
                scoresText.text = $"You: {diag.HostScore:F0}";
                differenceText.text = "Difference: --";
            }

            // Threshold 0: Score threshold (20%)
            UpdateThresholdRow(0, diag.MeetsPercentThreshold,
                $"Score threshold ({diag.RequiredPercentThreshold * 100:F0}%)",
                $"{diag.ScoreDifferenceRatio * 100:F0}% / {diag.RequiredPercentThreshold * 100:F0}%");

            // Threshold 1: Point minimum (50)
            UpdateThresholdRow(1, diag.MeetsPointThreshold,
                $"Point minimum ({diag.RequiredPointThreshold:F0})",
                $"{diag.ScoreDifferenceAbsolute:F0} / {diag.RequiredPointThreshold:F0}");

            // Threshold 2: Uptime (45s)
            UpdateThresholdRow(2, diag.MeetsUptimeThreshold,
                $"Uptime ({diag.RequiredUptimeSeconds:F0}s)",
                $"{diag.CandidateUptimeSeconds:F0}s / {diag.RequiredUptimeSeconds:F0}s");

            // Threshold 3: Sustained samples (5)
            UpdateThresholdRow(3, diag.ConsecutiveSamples >= diag.RequiredSamples,
                $"Sustained ({diag.RequiredSamples} samples)",
                $"{diag.ConsecutiveSamples} / {diag.RequiredSamples}");

            // Threshold 4: Cooldown
            if (diag.CooldownExpired)
            {
                UpdateThresholdRow(4, true, "Cooldown", "Ready");
            }
            else
            {
                UpdateThresholdRow(4, false, "Cooldown", $"{diag.CooldownRemainingSeconds:F0}s remaining");
            }

            // Update accept button
            if (diag.IsMigrationReady)
            {
                acceptButton.interactable = true;
                acceptButtonBg.color = buttonReadyColor;
                acceptButtonFill.fillAmount = 1f;
                acceptButtonFill.color = buttonReadyColor;
                acceptButtonLabel.text = "Accept Migration";
            }
            else
            {
                acceptButton.interactable = false;
                acceptButtonBg.color = buttonDisabledColor;
                acceptButtonFill.fillAmount = diag.SampleProgress;
                acceptButtonFill.color = buttonFillColor;

                if (diag.ConsecutiveSamples > 0)
                {
                    acceptButtonLabel.text = $"Stabilizing... {diag.ConsecutiveSamples}/{diag.RequiredSamples}";
                }
                else if (!diag.MeetsPercentThreshold || !diag.MeetsPointThreshold)
                {
                    acceptButtonLabel.text = "Score threshold not met";
                }
                else if (!diag.MeetsUptimeThreshold)
                {
                    acceptButtonLabel.text = "Waiting for uptime...";
                }
                else if (!diag.CooldownExpired)
                {
                    acceptButtonLabel.text = "Cooldown active...";
                }
                else
                {
                    acceptButtonLabel.text = "Waiting...";
                }
            }
        }

        private void UpdateThresholdRow(int index, bool isMet, string label, string value)
        {
            thresholdIcons[index].color = isMet ? metColor : notMetColor;
            thresholdTexts[index].text = $"{(isMet ? "✓" : "✗")} {label}: {value}";
            thresholdTexts[index].color = isMet ? metColor : Color.white;
        }
    }
}
