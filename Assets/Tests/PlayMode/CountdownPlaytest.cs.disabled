using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using TMPro;

public class CountdownPlaymodeTest
{
    [UnityTest]
    public IEnumerator Countdown_EnablesPlayerDriving()
    {
        // ----- UI (TMP) -----
        var canvasGO = new GameObject("Canvas");
        canvasGO.AddComponent<Canvas>();

        var textGO = new GameObject("CountdownText");
        textGO.transform.SetParent(canvasGO.transform);
        var countdownText = textGO.AddComponent<TextMeshProUGUI>();

        // ----- Player Kart (must exist before Start) -----
        var playerGO = new GameObject("PlayerKart");
        playerGO.AddComponent<BoxCollider>();   // required by controller
        var playerKart = playerGO.AddComponent<PlayerKartController>();
        playerKart.canDrive = false;

        // ----- RaceTimer -----
        var timerGO = new GameObject("RaceTimer");
        var raceTimer = timerGO.AddComponent<RaceTimer>();

        // ----- Countdown Manager -----
        var cmGO = new GameObject("CountdownManager");
        var cm = cmGO.AddComponent<CountdownManager>();
        cm.countdownText = countdownText;
        cm.raceTimer = raceTimer;

        // Wait for Start() + full countdown (~3 seconds)
        yield return new WaitForSeconds(3.6f);

        Assert.IsTrue(playerKart.canDrive, "Player should be able to drive after countdown finishes.");
    }
}