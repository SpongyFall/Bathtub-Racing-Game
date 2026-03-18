using GONet;
using UnityEngine;
using TMPro;
using System.Collections;

public class CountdownManager : MonoBehaviour
{
    public TMP_Text countdownText;
    public RaceTimer raceTimer;

    private PlayerKartController playerKart;

    IEnumerator Start()
    {
        // Wait 1 frame so KartLoader can spawn the kart first
        yield return null;

        // Find the locally controlled kart (not a remote player's kart).
        foreach (var kart in Object.FindObjectsByType<PlayerKartController>(FindObjectsSortMode.None))
        {
            var gnp = kart.GetComponent<GONet.GONetParticipant>();
            if (gnp == null || gnp.IsLocallyControlled)
            {
                playerKart = kart;
                break;
            }
        }

        if (playerKart == null)
        {
            Debug.LogWarning("CountdownManager: No local PlayerKartController found in scene!");
        }

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

        if (playerKart != null)
        {
            playerKart.canDrive = true;
        }
        
        OpponentKartAI[] ais = Object.FindObjectsOfType<OpponentKartAI>();
        foreach (var ai in ais)
            ai.canDrive = true;

        yield return new WaitForSeconds(1f);
        countdownText.text = "";
    }
}
