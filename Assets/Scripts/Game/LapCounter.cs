using GONet;
using System;
using System.Collections;
using TMPro;
using UnityEngine;

[Obsolete]
public class LapCounter : MonoBehaviour
{
    //private RacerInfo playerRacerInfo;

    //public int totalLaps = 3;
    //public int currentLap = 1;   // 1-based for UI

    //public TMP_Text lapText;
    //private bool canTriggerLap = true;
    //private bool raceStarted = false;

    //[Header("Player Racer Reference")]
    //public RacerInfo playerRacer;   // assign in inspector OR auto-find

    //void Start()
    //{
    //    totalLaps = PlayerPrefs.GetInt(TrackSelectionManager.SelectedLapCountKey, 3);

    //    currentLap = 1;
    //    raceStarted = false;

    //    if (playerRacer != null)
    //    {
    //        playerRacer.CurrentLap = 0;      // 0-based internal
    //        playerRacer.totalLaps = totalLaps;
    //    }

    //    UpdateLapDisplay();

    //    playerRacerInfo = FindFirstObjectByType<RacerInfo>();
    //    playerRacerInfo.CurrentLap = 1;
    //    playerRacerInfo.totalLaps = totalLaps;
    //}

    //private void OnTriggerEnter(Collider other)
    //{
    //    // PLAYER — only count laps for the locally-owned kart (not remote players' karts).
    //    if (other.CompareTag("Player") && canTriggerLap)
    //    {
    //        var gnp = other.GetComponent<GONetParticipant>();
    //        if (gnp != null && !gnp.IsLocallyControlled) return;
    //        AdvanceLap();
    //    }
    //    // AI � update their RacerInfo only (no UI changes)
    //    else if (other.CompareTag("AI"))
    //    {
    //        var aiRacer = other.GetComponent<RacerInfo>();
    //        if (aiRacer != null && !aiRacer.hasFinished)
    //        {
    //            aiRacer.CurrentLap++;
    //            if (aiRacer.CurrentLap >= aiRacer.totalLaps)
    //                aiRacer.hasFinished = true;
    //        }
    //    }
    //}

    //void AdvanceLap()
    //{
    //    if (!raceStarted)
    //    {
    //        raceStarted = true;
    //        playerRacerInfo.CurrentLap = currentLap;

    //        StartCoroutine(LapCooldown());
    //        return;
    //    }

    //    currentLap++;
    //    playerRacerInfo.CurrentLap = currentLap;

    //    // Update player RacerInfo
    //    if (playerRacer != null)
    //    {
    //        playerRacer.CurrentLap = currentLap - 1; // internal 0-based
    //        if (currentLap > totalLaps)
    //            playerRacer.hasFinished = true;
    //    }

    //    if (currentLap > totalLaps)
    //    {
    //        lapText.text = "FINISHED!";
    //        Object.FindFirstObjectByType<RaceTimer>().StopRace();
    //        playerRacerInfo.hasFinished = true;

    //        return;
    //    }

    //    UpdateLapDisplay();
    //    StartCoroutine(LapCooldown());
    //}

    //IEnumerator LapCooldown()
    //{
    //    canTriggerLap = false;
    //    yield return new WaitForSeconds(1f);
    //    canTriggerLap = true;
    //}

    //void UpdateLapDisplay()
    //{
    //    lapText.text = "Lap " + currentLap + " / " + totalLaps;
    //}
}