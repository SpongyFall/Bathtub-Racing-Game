using GONet;
using UnityEngine;

/// <summary>
/// Attached to the PlayerKart prefab alongside GONetParticipant.
/// Disables camera, audio listener, and local-only components on remote karts.
/// </summary>
[RequireComponent(typeof(GONetParticipant))]
public class KartNetworkSetup : MonoBehaviourGONetCallbacks
{
    public override void OnGONetParticipantEnabled(GONetParticipant gonetParticipant)
    {
        base.OnGONetParticipantEnabled(gonetParticipant);

        if (gonetParticipant != GetComponent<GONetParticipant>())
            return;

        if (!gonetParticipant.IsLocallyControlled)
        {
            // Disable camera so remote kart doesn't render from its POV
            foreach (var cam in GetComponentsInChildren<Camera>())
                cam.enabled = false;

            // Disable audio listener to avoid conflicts
            foreach (var al in GetComponentsInChildren<AudioListener>())
                al.enabled = false;
        }
    }
}
