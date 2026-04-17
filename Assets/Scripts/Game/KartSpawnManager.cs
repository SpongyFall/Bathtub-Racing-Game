using GONet;
using Steamworks;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Placed in the Racetrack scene. On Start, disables AI karts at player slots
/// and spawns a networked PlayerKart for each connected player.
///
/// Slot assignment:
///   Slot 0        = host (server)
///   Slots 1..N-1  = clients, in Steam lobby member order
///   Slots N..max  = AI karts (unchanged)
/// </summary>
public class KartSpawnManager : MonoBehaviour
{
    [Tooltip("PlayerKart prefab (must live in a Resources folder with GONetParticipant).")]
    [SerializeField] GONetParticipant playerKartPrefab;

    [Tooltip("Ordered spawn point transforms, matching the AI kart list below.")]
    [SerializeField] List<Transform> spawnPoints;

    [Tooltip("Pre-placed AI kart GameObjects in the scene, one per spawn slot in the same order as spawnPoints.")]
    [SerializeField] List<OpponentKartAI> aiKarts;

    void Start()
    {
    }

    public void SpawnKart()
    {
        if (NetworkManager.IsConnectedGONet && !SteamManager.InSteamLobby)
        {
            RaceManager.Instance.LeaveGame();
            return;
        }

        List<CSteamID> lobbyPlayers = SteamManager.GetLobbyPlayerIds();

        int playerCount = lobbyPlayers.Count > 0 ? lobbyPlayers.Count : 1;

        // Disable AI karts that will be replaced by human players.
        for (int i = 0; i < playerCount && i < aiKarts.Count; i++)
            aiKarts[i].gameObject.SetActive(false);
            //Destroy(aiKarts[i].gameObject);

        if (!SteamManager.InSteamLobby)
        {
            // Solo / offline: spawn the local player kart at slot 0.
            SpawnServerKart(0);
            return;
        }

        // Client: find this player's lobby index and spawn a remotely-controlled kart.
        CSteamID localId = SteamManager.LocalSteamId;
        int myIndex = lobbyPlayers.IndexOf(localId);
        if (myIndex < 0)
        {
            Debug.LogError("[KartSpawnManager] Local SteamID not found in lobby member list. Defaulting to slot 0.");
            myIndex = 0;
        }
        SpawnServerKart(myIndex);

        //if (GONetMain.IsServer)
        //{
        //    // Host: server-owned kart at slot 0. IsMine = true on this machine.
        //    SpawnServerKart(0);
        //}
        //else if (GONetMain.IsClient)
        //{
        //    // Client: find this player's lobby index and spawn a remotely-controlled kart.
        //    CSteamID localId = SteamManager.LocalSteamId;
        //    int myIndex = lobbyPlayers.IndexOf(localId);
        //    if (myIndex < 0)
        //    {
        //        Debug.LogError("[KartSpawnManager] Local SteamID not found in lobby member list. Defaulting to slot 0.");
        //        myIndex = 0;
        //    }
        //    SpawnClientKart(myIndex);
        //}
    }

    void SpawnServerKart(int slotIndex)
    {
        if (slotIndex >= spawnPoints.Count)
        {
            Debug.LogError($"[KartSpawnManager] No spawn point at index {slotIndex}.");
            return;
        }
        Transform sp = spawnPoints[slotIndex];
        Instantiate(playerKartPrefab, sp.position, sp.rotation);
    }

    void SpawnClientKart(int slotIndex)
    {
        if (slotIndex >= spawnPoints.Count)
        {
            Debug.LogError($"[KartSpawnManager] No spawn point at index {slotIndex}.");
            return;
        }
        Transform sp = spawnPoints[slotIndex];
        GONetMain.Client_InstantiateToBeRemotelyControlledByMe(playerKartPrefab, sp.position, sp.rotation);
    }
}
