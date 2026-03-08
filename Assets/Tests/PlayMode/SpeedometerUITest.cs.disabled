using UnityEngine;
using UnityEngine.TestTools;
using NUnit.Framework;
using TMPro;
using System.Collections;

public class SpeedometerUITest
{
    [UnityTest]
    public IEnumerator Speedometer_DisplaysCorrectMPH()
    {
        // Canvas
        var canvasGO = new GameObject("Canvas");
        canvasGO.AddComponent<Canvas>();

        // Text
        var textGO = new GameObject("SpeedText");
        textGO.transform.SetParent(canvasGO.transform);
        var speedText = textGO.AddComponent<TextMeshProUGUI>();

        // Speedometer
        var speedGO = new GameObject("Speedometer");
        var speedometer = speedGO.AddComponent<SpeedometerUI>();
        speedometer.speedText = speedText;

        yield return null;

        // Inject velocity directly for testing
        speedometer.UpdateSpeedometerFromVelocity(new Vector3(10f, 0, 0));

        Assert.AreEqual("022", speedText.text);
    }
}
