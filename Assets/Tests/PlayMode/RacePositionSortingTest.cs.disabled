using UnityEngine;
using UnityEngine.TestTools;
using NUnit.Framework;
using TMPro;
using System.Collections;

public class RacePositionPlaymodeTest
{
    [UnityTest]
    public IEnumerator RacePosition_DisplaysCorrectLeader()
    {
        // UI
        var canvas = new GameObject("Canvas", typeof(Canvas));
        var textObj = new GameObject("PositionText");
        textObj.transform.SetParent(canvas.transform);
        var positionText = textObj.AddComponent<TextMeshProUGUI>();

        // Manager
        var rpm = new GameObject("RPM").AddComponent<RacePositionManager>();
        rpm.positionText = positionText;

        // Player racer
        var player = new GameObject("Player");
        var playerRacer = player.AddComponent<RacerInfo>();
        playerRacer.isPlayer = true;
        playerRacer.currentLap = 2;
        playerRacer.currentWaypoint = 5;
        playerRacer.distanceToNext = 2f;

        // AI racer (behind)
        var ai = new GameObject("AI");
        var aiRacer = ai.AddComponent<RacerInfo>();
        aiRacer.currentLap = 1;
        aiRacer.currentWaypoint = 3;
        aiRacer.distanceToNext = 10f;

        yield return null; // allow Start()

        Assert.IsTrue(positionText.text.Contains("Place: 1"),
            "Player should be shown in first place.");
    }
}