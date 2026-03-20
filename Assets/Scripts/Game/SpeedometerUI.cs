using UnityEngine;
using TMPro;


// Displays the player's kart speed in MPH.

public class SpeedometerUI : MonoBehaviour
{
    //[Header("References")]

    [Tooltip("UI text field where speed will be displayed.")]
    public TMP_Text speedText;

    NetworkedKart clientKart => RaceManager.Instance.ClientKart;

    const float ToMPH = 2.23694f;

    void Update()
    {
        if (clientKart == null || speedText == null)
            return;

        UpdateSpeedometer();
    }

    /// Computes and updates the speed text display.
    private void UpdateSpeedometer()
    {
        float speedMPH = clientKart.Rigid.velocity.magnitude * ToMPH;
        speedText.text = speedMPH.ToString("000");
    }
    public void UpdateSpeedometerFromVelocity(Vector3 velocity)
    {
        float speedMPH = velocity.magnitude * ToMPH;
        speedText.text = speedMPH.ToString("000");
    }
}


