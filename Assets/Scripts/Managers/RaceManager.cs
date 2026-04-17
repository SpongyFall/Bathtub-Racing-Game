using GONet;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Splines;

public class RaceManager : GONetParticipantCompanionBehaviour
{
    public static event Action<RaceManager> OnRaceStart;

    public static RaceManager Instance { get; private set; }

    public WaypointContainer WaypointContainer;
    public SplineContainer TrackSpline;
    public CountdownManager CountdownManager;
    public KartSpawnManager KartSpawnManager;
    public GameUI GameUI;
    [Space]
    [Header("Runtime Set Properties")]
    public bool RaceStarted = false;
    public bool RaceActive = false;
    public int TotalLaps = 0;
    [Space]
    public NetworkedKart ClientKart;
    [Tooltip("Represents only player, non-AI karts for now.")]
    public List<NetworkedKart> NetworkedKarts = new();
    [Tooltip("Sorted by place in Update.")]
    public List<RacerInfo> RacerInfos = new();
    public List<OpponentKartAI> KartAIs = new();

    public Camera MainCamera => ClientKart != null && ClientKart.Camera.gameObject.activeSelf 
        ? ClientKart.Camera : Camera.main;

    public Dictionary<ushort, CSteamID> AuthorityToSteamId = new();
    public Dictionary<CSteamID, ushort> SteamIdToAuthority = new();

    List<NetworkedKart> readyKarts = new();

    protected override void Awake()
    {
        base.Awake();

        Instance = this;
        Debug.Log($"Race Manager Awake!");
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

        Debug.Log($"Is Server: {GONetMain.IsServer}, Is Host: {GONetMain.IsHost}, Is Client: {GONetMain.IsClient}");
        StartCoroutine(SpawnKartNextFrame());
    }

    void Update()
    {
        SortRacers();
    }

    IEnumerator SpawnKartNextFrame()
    {
        yield return null;

        KartSpawnManager.SpawnKart();
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
    public void AddRacerInfo(RacerInfo info, bool add)
    {
        //Occurs on enable/disable of a RacerInfo. Some AIs are the only disabled RacerInfos rn.
        if (add && !RacerInfos.Contains(info))
            RacerInfos.Add(info);
        else if (!add)
            RacerInfos.Remove(info);
    }
    public void AddKartAI(OpponentKartAI kart, bool add)
    {
        //Occurs on enable/disable.
        if (add && !KartAIs.Contains(kart))
        {
            KartAIs.Add(kart);
            kart.name = $"AI Kart {KartAIs.Count}";
        }
        else if (!add)
            KartAIs.Remove(kart);
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

        foreach (var steamId in playerIds)
        {
            if (!readyPlayers.Contains(steamId))
            {
                string playerName = SteamFriends.GetFriendPersonaName(steamId);
                Debug.Log($"Player {playerName} did not ready their kart in time!");
                if (SteamIdToAuthority.TryGetValue(steamId, out ushort authorityId))
                    NetworkManager.KickGONetPlayer(authorityId, steamId);
                else
                    SteamManager.KickPlayer(steamId);
            }
        }

        //Host RPC to all.
        //Load laps and opponent count from player prefs.
        int laps = TrackSelectionManager.GetSavedLapCount();
        int aiCount = TrackSelectionManager.GetSavedAICount();
        CallRpc(nameof(StartRace), laps, aiCount);
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
    public void StartRace(int laps, int aiCount)
    {
        //Called to all.
        Debug.Log("Starting race!");
        RaceStarted = true;
        RaceActive = true;

        TotalLaps = laps;
        //Master spawns AI karts.
        KartSpawnManager.SpawnAIKarts(aiCount);

        CountdownManager.StartCountdown();

        OnRaceStart.InvokeSafe(nameof(OnRaceStart), this);
    }
    public void OnCountdownFinished(CountdownManager countdownManager)
    {
        //Allow driving of all karts.
        EnableAllKartDriving(true);
    }

    public void SortRacers()
    {
        //Sort the racers by their progress in the race.
        RacerInfos = RacerInfos.OrderByDescending(x => x.RaceProgress).ToList();

        /*
        //Sort the racers by their position in the race.
        RacerInfos = RacerInfos
            //Higher lap count means further ahead (descending).
            .OrderByDescending(x => x.CompletedLaps)
            //Higher waypoint index means further ahead (descending).
            .ThenByDescending(x => x.WaypointIndex)
            //Lower distance to next waypoint means further ahead (ascending).
            .ThenBy(x => x.DistanceToNextWaypoint)
            .ToList();
        */
    }
    public int GetRacerPlace(RacerInfo info)
    {
        return RacerInfos.IndexOf(info) + 1;
    }

    public void CheckEndConditions()
    {
        //Only check to end if race is active, and we are the server.
        if (!RaceActive || !GONetMain.IsServer)
            return;

        RacerInfo winner = null;
        foreach (var racerInfo in RacerInfos)
        {
            //Check for lap winner.
            if (racerInfo.CompletedLaps >= TotalLaps)
            {
                winner = racerInfo;
                break;
            }
        }

        if (winner)
        {
            RPCEndRace(winner.Participant.GONetId);
            return;
        }
        //No winner yet, check for player count.
        else if (RacerInfos.Count <= 1)
        {
            Debug.Log("Only one player left! Ending game...");

            //If we have one or less racers left, end.
            if (RacerInfos.Count == 1)
                RPCEndRace(RacerInfos[0].Participant.GONetId);
            else
                RPCEndRace(0);
        }
    }

    public void RPCEndRace(uint winnerGONetId)
    {
        //Can't end race if not server.
        if (!GONetMain.IsServer)
            return;

        CallRpc(nameof(EndRace), winnerGONetId);
    }
    [TargetRpc]
    public void EndRace(uint winnerGONetId)
    {
        if (!RaceActive)
            return;
        RaceActive = false;

        string winnerName = "Unknown";
        ushort winnerAuthId = 0;
        CSteamID winnerSteamId = CSteamID.Nil;

        var winnerInfo = RacerInfos.Find(x => x.Participant.GONetId == winnerGONetId);
        if (winnerInfo == null)
            Debug.LogError($"{nameof(EndRace)}: Failed to find winner RacerInfo for GONet Id: '{winnerGONetId}'!");
        //See if it's a player by checking for NetworkedKart.
        else if (winnerInfo.TryGetComponent<NetworkedKart>(out var winnerPlayerKart))
        {
            //Get auth Id.
            winnerAuthId = winnerInfo.Participant.OwnerAuthorityId;
            //Get SteamID.
            if (AuthorityToSteamId.TryGetValue(winnerAuthId, out winnerSteamId))
            {
                //Get Steam name.
                winnerName = SteamFriends.GetFriendPersonaName(winnerSteamId);
            }
            else
                Debug.LogError($"{nameof(EndRace)}: Failed to find SteamID for winner GONet AuthId: '{winnerAuthId}'!");
        }
        //Otherwise if it's an AI, use it's obj name.
        else
            winnerName = winnerInfo.name;


        Debug.Log($"{nameof(EndRace)}! Winner name: '{winnerName}', GONet AuthId: {winnerAuthId}, SteamID: {winnerSteamId}");
        GameUI.ShowEndRaceScreen(winnerName);
    }
    public void OnEndRaceScreenShown()
    {
        //Stop all kart movement when screen is shown.
        EnableAllKartDriving(false);
    }

    public void EnableAllKartDriving(bool enable)
    {
        ClientKart.Controller.canDrive = enable;
        KartAIs.ForEach(x => x.canDrive = enable);
    }

    public static bool TryGetClientKart(out NetworkedKart kart)
    {
        kart = null;

        if (Instance == null)
            return false;
        
        kart = Instance.ClientKart;
        return kart != null;
    }

    public void LeaveGame(bool stayInSteamLobby)
    {
        NetworkManager.DisconnectGONet();
        if (!stayInSteamLobby)
            SteamManager.LeaveLobby();

        SceneLoader.LoadScene(SceneType.MainMenu);
    }
}

public class EndGameInfo
{
    public bool HasWinner = false;
    public ushort WinnerAuthId;

    public EndGameInfo(ushort winnerAuthId)
    {
        WinnerAuthId = winnerAuthId;
        HasWinner = true;
    }
}