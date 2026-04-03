using GONet;
using Steamworks;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RaceManager : GONetParticipantCompanionBehaviour
{
    public const int MaxPlayers = 10;

    public static RaceManager Instance { get; private set; }

    public WaypointContainer WaypointContainer;
    public RacePositionManager RacePositionManager;
    public CountdownManager CountdownManager;
    public KartSpawnManager KartSpawnManager;

    [Header("Runtime Set Props")]
    public NetworkedKart ClientKart;
    public List<NetworkedKart> NetworkedKarts = new();

    public Camera MainCamera => ClientKart != null && ClientKart.Camera.gameObject.activeSelf 
        ? ClientKart.Camera : Camera.main;

    public Dictionary<ushort, CSteamID> AuthorityToSteamId = new();
    public Dictionary<CSteamID, ushort> SteamIdToAuthority = new();

    List<NetworkedKart> readyKarts = new();
    bool raceStarted = false;

    protected override void Awake()
    {
        base.Awake();

        Instance = this;
    }

    protected override void Start()
    {
        base.Start();
     
        //KartSpawnManager.SpawnKart();

        ////StartRace();
        //StartCoroutine(WaitForReadyKarts());
    }

    public override void OnGONetReady()
    {
        base.OnGONetReady();

        KartSpawnManager.SpawnKart();
        Debug.Log($"Is Server: {GONetMain.IsServer}, Is Host: {GONetMain.IsHost}, Is Client: {GONetMain.IsClient}");
        if (GONetMain.IsHost)
            StartCoroutine(WaitForReadyKarts());
    }

    public void AddNetworkedKart(NetworkedKart kart, bool add)
    {
        if (add && !NetworkedKarts.Contains(kart))
            NetworkedKarts.Add(kart);
        else if (!add)
            NetworkedKarts.Remove(kart);
    }

    IEnumerator WaitForReadyKarts()
    {
        //Called by host.
        readyKarts.Clear();

        //Wait a max time cumulitive for all players to ready their karts.
        float maxWaitTime = 10f;
        float currentWait = 0f;

        var playerIds = SteamManager.GetLobbyPlayerIds();
        List<CSteamID> readyPlayers = new();
        foreach (var steamId in playerIds)
        {
            string playerName = SteamFriends.GetFriendPersonaName(steamId);
            Debug.Log($"Waiting for player {playerName} to ready their kart...");

            while (true)
            {
                //Once we find their kart as ready, break and move on to the next player.
                if (readyKarts.Exists(x => x.OwnerSteamId == steamId.m_SteamID))
                {
                    //Debug.Log($"Player {playerName}'s kart is ready!");
                    readyPlayers.Add(steamId);
                    break;
                }
                else if (currentWait >= maxWaitTime)
                {
                    //Waited too long.
                    break;
                }

                currentWait += Time.deltaTime;
                yield return null;
            }

            if (currentWait >= maxWaitTime)
                break;
        }

        //Kick non-ready.
        foreach (var steamId in playerIds)
        {
            if (!readyPlayers.Contains(steamId))
            {
                string playerName = SteamFriends.GetFriendPersonaName(steamId);
                Debug.Log($"Player {playerName} did not ready their kart in time!");
                //Doesn't actually kick from GONet, not sure how to get their authority ID from here.
                SteamManager.KickPlayer(steamId);
            }
        }

        //Host RPC to all.
        CallRpc(nameof(StartRace));
    }
    public void KartReady(NetworkedKart kart)
    {
        //This is reliable because it is called from the kart's RPCSetup, which is only RPCed from the kart's owner.
        Debug.Log($"Kart ready: '{kart}'!");

        readyKarts.Add(kart);
    }

    public void LinkAuthToSteamId(ushort authId, CSteamID steamID)
    {
        SteamIdToAuthority[steamID] = authId;
        AuthorityToSteamId[authId] = steamID;
    }

    [TargetRpc]
    public void StartRace()
    {
        //Called to all.
        Debug.Log("Starting race!");
        raceStarted = true;

        CountdownManager.StartCountdown();
    }

    public void LeaveGame()
    {
        NetworkManager.DisconnectGONet();
        SteamManager.LeaveLobby();

        SceneLoader.LoadScene(SceneType.MainMenu);
    }
}