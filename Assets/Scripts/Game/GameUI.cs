using GONet;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class GameUI : MonoBehaviour
{
    public GameObject LapParent;
    public TextMeshProUGUI LapCounter;
    public GameObject PlaceParent;
    public TextMeshProUGUI PlaceCounter;
    [Space]
    [Header("Menu")]
    public Button QuitBtn;
    [Space]
    [Header("Race End")]
    public GameObject WinnerPanel;
    public TextMeshProUGUI WinnerText;
    public Transform RaceEndPanel;
    public TextMeshProUGUI EndPanelWinText;
    public Button MainMenuBtn;

    void Awake()
    {
        QuitBtn.onClick.AddListener(OnQuitClick);

        MainMenuBtn.onClick.AddListener(OnMainMenuClick);
    }

    void Update()
    {
        if (RaceManager.Instance == null)
            return;

        bool activeClient = RaceManager.TryGetClientKart(out NetworkedKart clientKart);
        if (activeClient)
        {
            var racerInfo = clientKart.RacerInfo;

            int totalLaps = RaceManager.Instance.TotalLaps;
            int displayLap = Mathf.Clamp(racerInfo.CompletedLaps + 1, 0, totalLaps);
            LapCounter.text = $"Lap: {displayLap} / {totalLaps}";
            PlaceCounter.text = $"Place: {racerInfo.RacerPlace} / {RaceManager.Instance.RacerInfos.Count}";
        }
        LapParent.SetActiveSafe(activeClient);
        PlaceParent.SetActiveSafe(activeClient);
    }

    void OnQuitClick()
    {
        RaceManager.Instance.LeaveGame(false);
    }

    public void ShowEndRaceScreen(string winnerName)
    {
        WinnerText.text = $"WINNER: '{winnerName}'!";
        EndPanelWinText.text = $"WINNER:\n'{winnerName}'!";
        WinnerPanel.SetActive(true);

        //Delay showing the screen.
        StartCoroutine(ShowRaceEndPanelCor());
    }
    IEnumerator ShowRaceEndPanelCor()
    {
        yield return new WaitForSeconds(5f);

        //Unlock cursor.
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;

        //Disable winner panel.
        WinnerPanel.SetActive(false);
        //Show end screen.
        RaceEndPanel.gameObject.SetActive(true);
        RaceManager.Instance.OnEndRaceScreenShown();
    }
    void OnMainMenuClick()
    {
        RaceManager.Instance.LeaveGame(true);
    }
}
