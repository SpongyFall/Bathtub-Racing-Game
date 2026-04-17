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
    public GONetParticipant PlayerKartPrefab;
    public GONetParticipant AIKartPrefab;
    public Transform SpawnPointParent;

    //[Tooltip("Pre-placed AI kart GameObjects in the scene, one per spawn slot in the same order as spawnPoints.")]
    //[SerializeField] List<OpponentKartAI> aiKarts;

    List<Transform> spawnPoints = new();

    void Awake()
    {
        FindSpawnPoints();
    }
    void FindSpawnPoints()
    {
        spawnPoints.Clear();
        for (int i = 0; i < SpawnPointParent.childCount; i++)
        {
            var child = SpawnPointParent.GetChild(i);
            spawnPoints.Add(child);
        }
    }

    public void SpawnKart()
    {
        if (spawnPoints.Count == 0)
        {
            Debug.LogError($"Trying to {nameof(SpawnKart)} before spawn points are set!");
            FindSpawnPoints();
        }

        List<CSteamID> lobbyPlayers = SteamManager.GetLobbyPlayerIds();
        int playerCount = lobbyPlayers.Count > 0 ? lobbyPlayers.Count : 1;

        // Disable AI karts that will be replaced by human players.
        //for (int i = 0; i < playerCount && i < aiKarts.Count; i++)
        //    aiKarts[i].gameObject.SetActive(false);
            //Destroy(aiKarts[i].gameObject);

        if (!SteamManager.InSteamLobby)
        {
            // Solo / offline: spawn the local player kart at slot 0.
            SpawnPlayerKart(0);
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
        SpawnPlayerKart(myIndex);

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
    void SpawnPlayerKart(int slotIndex)
    {
        //if (slotIndex >= spawnPoints.Count)
        //{
        //    Debug.LogError($"[KartSpawnManager] No spawn point at index {slotIndex}.");
        //    return;
        //}
        //Just loop slot index, should never really happen though.
        slotIndex %= spawnPoints.Count;

        Transform sp = spawnPoints[slotIndex];
        Instantiate(PlayerKartPrefab, sp.position, sp.rotation);
    }

    public void SpawnAIKarts(int count)
    {
        //Fills remaining slots with AI karts. Server controls the AI karts by spawning them himself.
        if (!GONetMain.IsServer)
            return;

        int spawnedPlayerCount = RaceManager.Instance.NetworkedKarts.Count;
        for (int i = 0; i < count; i++)
        {
            int slotIndex = (spawnedPlayerCount + i) % spawnPoints.Count;
            Transform sp = spawnPoints[slotIndex];
            Instantiate(AIKartPrefab, sp.position, sp.rotation);
        }

        Debug.Log($"Server spawned {count} AI karts.");
    }
}
