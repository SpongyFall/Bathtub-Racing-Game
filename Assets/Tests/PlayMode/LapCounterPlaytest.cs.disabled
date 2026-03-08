using NUnit.Framework;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;

public class LapCounterPlaymodeTest
{
    [UnityTest]
    public IEnumerator LapCounter_IncrementsAfterSecondTrigger()
    {
        // --- REQUIRED STUBS ---
        var wcGO = new GameObject("WaypointContainer");
        wcGO.AddComponent<WaypointContainer>().waypoints = new List<Transform>();

        var canvas = new GameObject("Canvas").AddComponent<Canvas>();
        var tmpGO = new GameObject("LapText");
        tmpGO.transform.SetParent(canvas.transform);
        var tmp = tmpGO.AddComponent<TextMeshProUGUI>();

        var racerGO = new GameObject("PlayerRacer");
        racerGO.tag = "Player";
        var racer = racerGO.AddComponent<RacerInfo>();
        racer.isPlayer = true;

        var lapGO = new GameObject("LapCounter");
        var lap = lapGO.AddComponent<LapCounter>();
        lap.lapText = tmp;
        lap.playerRacer = racer;

        var playerGO = new GameObject("Player");
        playerGO.tag = "Player";
        playerGO.AddComponent<BoxCollider>();

        yield return null; // allow Start()

        // First trigger: starts race
        lap.SendMessage("OnTriggerEnter", playerGO.GetComponent<Collider>());
        yield return new WaitForSeconds(1.1f);

        // Second trigger: increments lap
        lap.SendMessage("OnTriggerEnter", playerGO.GetComponent<Collider>());

        Assert.AreEqual(2, lap.currentLap);
    }
}