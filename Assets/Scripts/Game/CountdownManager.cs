using GONet;
using UnityEngine;
using TMPro;
using System.Collections;

public class CountdownManager : MonoBehaviour
{
    public GameObject WaitingForPlayers;
    public TMP_Text countdownText;
    public RaceTimer raceTimer;
    public AudioSource CountdownBeepSource;

    void Awake()
    {
        WaitingForPlayers.SetActive(true);
        countdownText.text = "";
    }

    public void StartCountdown()
    {
        StartCoroutine(CountdownSequence());
    }

    IEnumerator CountdownSequence()
    {
        WaitingForPlayers.SetActive(false);
        CountdownBeepSource.Play();

        countdownText.text = "3";
        yield return new WaitForSeconds(1f);

        countdownText.text = "2";
        yield return new WaitForSeconds(1f);

        countdownText.text = "1";
        yield return new WaitForSeconds(1f);

        countdownText.text = "GO!";
        raceTimer.StartRace();

        RaceManager.Instance.OnCountdownFinished(this);

        yield return new WaitForSeconds(1f);
        countdownText.text = "";
    }
}
