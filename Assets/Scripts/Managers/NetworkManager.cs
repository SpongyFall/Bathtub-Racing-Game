using GONet;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Bridges the Steam lobby system with GONet's transport layer.
/// Handles the session handshake: signalling clients → initializing GONet → loading the race scene.
/// </summary>
public class NetworkManager : MonoBehaviour, IOrderedScript
{
    public static NetworkManager Instance { get; private set; }

    [SerializeField] GONetConnectionManager gonetConnectionManager;

    // Run after SteamManager (CallOrder 0) so lobby state is ready.
    public int CallOrder => 1;

    public void OrderedAwake()
    {
        Instance = this;

        SteamManager.OnLobbyDataUpdate += OnLobbyDataUpdate;
        gonetConnectionManager.OnConnectionSuccess += OnGONetConnected;
    }

    public void OrderedStart() { }

    void OnDestroy()
    {
        SteamManager.OnLobbyDataUpdate -= OnLobbyDataUpdate;
        if (gonetConnectionManager != null)
            gonetConnectionManager.OnConnectionSuccess -= OnGONetConnected;
    }

    /// <summary>
    /// Called by the host when the Start button is pressed.
    /// Signals all clients via lobby metadata then initializes GONet as host.
    /// </summary>
    public void StartGame()
    {
        // Broadcast to all lobby members that the game is starting.
        SteamMatchmaking.SetLobbyData(SteamManager.LobbyId.Value, "state", "starting");

        ConnectGONet(isHost: true, hostSteamId: null);
    }

    // Clients receive this when lobby metadata changes.
    void OnLobbyDataUpdate(LobbyDataUpdate_t info)
    {
        // Only clients react — host triggers this themselves.
        if (SteamManager.IsLobbyOwner)
            return;

        string state = SteamMatchmaking.GetLobbyData(SteamManager.LobbyId.Value, "state");
        if (state == "starting")
        {
            string hostSteamId = SteamMatchmaking.GetLobbyData(SteamManager.LobbyId.Value, "hostSteamId");
            ConnectGONet(isHost: false, hostSteamId: hostSteamId);
        }
    }

    void ConnectGONet(bool isHost, string hostSteamId)
    {
        // GONetConnectionPreset is a ScriptableObject — use CreateInstance at runtime.
        var preset = ScriptableObject.CreateInstance<GONetConnectionPreset>();
        preset.role = isHost ? GONetConnectionRole.Host : GONetConnectionRole.Client;
        preset.usePluggableTransport = true;
        preset.transportType = GONetTransportType.Steamworks;
        preset.maxConnections = RaceManager.MaxPlayers;

        // For clients, ipAddress is the host's SteamID string — SteamworksTransport
        // converts this to a SteamNetworkingIdentity for the P2P connection.
        if (!isHost)
            preset.ipAddress = hostSteamId;

        gonetConnectionManager.CurrentPreset = preset;
        gonetConnectionManager.Connect();
    }

    // Fires when GONet reports a successful connection.
    void OnGONetConnected()
    {
        // Only the server loads the scene — GONet's SceneManager propagates it to all clients.
        if (GONetMain.IsServer && GONetMain.SceneManager != null)
        {
            GONetMain.SceneManager.LoadSceneFromBuildSettings(
                SceneType.Racetrack.ToString(),
                LoadSceneMode.Single
            );
        }
    }
}
