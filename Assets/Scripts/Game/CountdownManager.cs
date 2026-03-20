using GONet;
using UnityEngine;
using TMPro;
using System.Collections;

public class CountdownManager : MonoBehaviour
{
    public TMP_Text countdownText;
    public RaceTimer raceTimer;

    public void StartCountdown()
    {
        StartCoroutine(CountdownSequence());
    }

    IEnumerator CountdownSequence()
    {
        countdownText.text = "3";
        yield return new WaitForSeconds(1f);

        countdownText.text = "2";
        yield return new WaitForSeconds(1f);

        countdownText.text = "1";
        yield return new WaitForSeconds(1f);

        countdownText.text = "GO!";
        raceTimer.StartRace();

        RaceManager.Instance.ClientKart.Controller.canDrive = true;
        
        OpponentKartAI[] ais = Object.FindObjectsOfType<OpponentKartAI>();
        foreach (var ai in ais)
            ai.canDrive = true;

        yield return new WaitForSeconds(1f);
        countdownText.text = "";
    }
}
