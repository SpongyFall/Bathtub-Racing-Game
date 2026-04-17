using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System;

public class TrackSelectionManager : MonoBehaviour
{
    public const string SavedLapCountKey = "LapCount";
    public const int DefaultLapCount = 3;
    public const int MaxLapCount = 50;

    public const string SavedAICountKey = "AICount";
    public const int DefaultAICount = 3;

    public TMP_InputField lapsInput; // For 'Number of Laps'
    public TMP_InputField opponentsInput; // For 'Number of Opponents'
    [Space]
    public Button BackBtn;
    public Button StartBtn;

    public int MaxAICount => SteamManager.InSteamLobby
        //If we're in a lobby, max AI count is max players - current players (including host).
        ? NetworkManager.MaxPlayerCount - SteamManager.GetLobbyPlayerCount() 
        : NetworkManager.MaxPlayerCount - 1;
    //If we're in a lobby and there's at least another player, allow 0 AI opponents.
    public int MinAICount => SteamManager.InSteamLobby && SteamManager.GetLobbyPlayerCount() > 1 ? 0 : 1;

    void Awake()
    {
        lapsInput.onValueChanged.AddListener(OnLapsInputChanged);
        opponentsInput.onValueChanged.AddListener(OnOpponentsInputChanged);

        BackBtn.onClick.AddListener(OnBackClick);
        StartBtn.onClick.AddListener(OnStartClick);
    }

    void OnEnable()
    {
        //Load saved values.
        lapsInput.text = GetSavedLapCount().ToString();
        opponentsInput.text = GetSavedAICount().ToString();
    }

    void OnLapsInputChanged(string input)
    {
        if (int.TryParse(input, out int parsedLaps))
        {
            parsedLaps = Mathf.Clamp(parsedLaps, 1, MaxLapCount);
            lapsInput.SetTextWithoutNotify(parsedLaps.ToString());
        }
        else
            lapsInput.SetTextWithoutNotify(DefaultLapCount.ToString());
    }
    void OnOpponentsInputChanged(string input)
    {
        if (int.TryParse(input, out int parsedAICount))
        {
            parsedAICount = Mathf.Clamp(parsedAICount, MinAICount, MaxAICount);
            opponentsInput.SetTextWithoutNotify(parsedAICount.ToString());
        }
        else
            opponentsInput.SetTextWithoutNotify(DefaultAICount.ToString());
    }

    void OnBackClick()
    {
        MainMenuManager.Instance.ShowTrackSelection(false);
    }
    void OnStartClick()
    {
        int.TryParse(lapsInput.text, out int parsedLaps);
        parsedLaps = Mathf.Clamp(parsedLaps, 1, MaxLapCount);
        int.TryParse(opponentsInput.text, out int parsedAICount);
        parsedAICount = Mathf.Clamp(parsedAICount, 1, MaxAICount);

        //int laps = 3;
        //if (!int.TryParse(lapsInput.text, out laps))
        //    laps = 3;
        //// Lap Limit
        //laps = Mathf.Clamp(laps, 1, 50);

        //Save to player prefs.
        PlayerPrefs.SetInt(SavedLapCountKey, parsedLaps);
        PlayerPrefs.SetInt(SavedAICountKey, parsedAICount);
        PlayerPrefs.Save();

        Debug.Log($"{nameof(TrackSelectionManager)}: Set lap count: {parsedLaps}, set opponent count: {parsedAICount}");

        //Start the game.
        NetworkManager.Instance.StartGame();
    }

    public static int GetSavedLapCount()
    {
        return PlayerPrefs.GetInt(SavedLapCountKey, DefaultLapCount);
    }
    public static int GetSavedAICount()
    {
        return PlayerPrefs.GetInt(SavedAICountKey, DefaultAICount);
    }
}
