using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public class MainMenuManager : MonoBehaviour, IOrderedScript
{
    public static MainMenuManager Instance { get; private set; }

    public GameObject MainMenuPanel;
    public GameObject JoiningRandomPanel;
    [Space]
    public Button HostBtn;
    public Button JoinRandomBtn;
    public Button CancelJoinRandomBtn;
    public Button CustomizeBtn;
    public Button ScoresBtn;
    public Button AboutBtn;
    public Button QuitBtn;
    [Space]
    [Header("Auto Set")]
    public List<GameObject> ActivePanels = new();

    public int CallOrder => 0;

    const string websiteUrl = "https://team-22-bathtub-racing-game.github.io/Website/";

    void Awake()
    {
        HostBtn.onClick.AddListener(OnHostClick);
        JoinRandomBtn.onClick.AddListener(OnJoinRandomClick);
        CancelJoinRandomBtn.onClick.AddListener(OnCancelJoinRandomClick);
        CustomizeBtn.onClick.AddListener(OnCustomizeClick);
        ScoresBtn.onClick.AddListener(OnScoresClick);
        AboutBtn.onClick.AddListener(OnAboutClick);
        QuitBtn.onClick.AddListener(OnQuitClick);
    }

    public void OrderedAwake()
    {
        Instance = this;

        ShowPanel(MainMenuPanel, true);
    }
    public void OrderedStart()
    {
    }

    void Update()
    {
        if (SteamManager.Instance != null)
            JoiningRandomPanel.SetActiveSafe(SteamManager.IsJoiningRandom);
    }

    public void ShowPanel(GameObject panel, bool show)
    {
        if (panel == null)
            return;
        ActivePanels.RemoveAll(x => x == null);

        if (show && !ActivePanels.Contains(panel))
        {
            //Disable last.
            if (ActivePanels.Count > 0)
                ActivePanels[^1].SetActiveSafe(false);

            //Add and enable new.
            ActivePanels.Add(panel);
            panel.SetActiveSafe(true);
        }
        else if (!show && ActivePanels.Contains(panel))
        {
            //Disable and remove.
            ActivePanels.Remove(panel);
            panel.SetActiveSafe(false);

            //Enable next if exists.
            if (ActivePanels.Count > 0)
                ActivePanels[^1].SetActiveSafe(true);
        }
    }

    void OnHostClick()
    {
        SteamManager.CreateLobby();
    }
    void OnJoinRandomClick()
    {
        SteamManager.StartJoiningRandomLobby();
    }
    void OnCancelJoinRandomClick()
    {
        SteamManager.StopJoiningRandomLobby();
    }
    void OnCustomizeClick()
    {
        SceneLoader.LoadScene(SceneType.KartCustomization);
    }
    void OnScoresClick()
    {

    }
    void OnAboutClick()
    {
        Application.OpenURL(websiteUrl);
    }
    void OnQuitClick()
    {
#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#endif
        Application.Quit();
    }
}
