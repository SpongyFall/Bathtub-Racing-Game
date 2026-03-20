using GONet;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RaceManager : MonoBehaviour
{
    public const int MaxPlayers = 10;

    public static RaceManager Instance { get; private set; }

    public RacePositionManager RacePositionManager;
    public CountdownManager CountdownManager;
    public KartSpawnManager KartSpawnManager;

    [Header("Runtime Set Props")]
    public NetworkedKart ClientKart;
    public List<NetworkedKart> NetworkedKarts = new();

    bool raceStarted = false;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        KartSpawnManager.SpawnKart();

        StartRace();
    }

    public void AddNetworkedKart(NetworkedKart kart, bool add)
    {
        if (add && !NetworkedKarts.Contains(kart))
            NetworkedKarts.Add(kart);
        else if (!add)
            NetworkedKarts.Remove(kart);

        //if (!raceStarted)
        //{
        //    if (NetworkedKarts.Count == SteamManager.GetLobbyPlayerIds().Count)
        //        StartRace();
        //}
    }

    public void StartRace()
    {
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