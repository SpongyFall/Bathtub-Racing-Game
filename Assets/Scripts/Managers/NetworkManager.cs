using GONet;
using GONet.Transport;
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
    public static bool IsConnectedGONet => (GONetMain.GONetClient != null && GONetMain.GONetClient.IsConnectedToServer) 
        || (GONetMain.gonetServer != null && GONetMain.gonetServer.IsRunning);

    public GONetConnectionManager GONetConnectionManager;

    // Run after SteamManager (CallOrder 0) so lobby state is ready.
    public int CallOrder => 1;

    public void OrderedAwake()
    {
        Instance = this;

        SteamManager.OnLobbyDataUpdate += OnLobbyDataUpdate;
        GONetConnectionManager.OnConnectionSuccess += OnGONetConnected;
    }

    public void OrderedStart() { }

    void OnDestroy()
    {
        SteamManager.OnLobbyDataUpdate -= OnLobbyDataUpdate;
        if (GONetConnectionManager != null)
            GONetConnectionManager.OnConnectionSuccess -= OnGONetConnected;
    }

    /// <summary>
    /// Called by the host when the Start button is pressed.
    /// Signals all clients via lobby metadata then initializes GONet as host.
    /// </summary>
    public void StartGame()
    {
        bool inSteamLobby = SteamManager.InSteamLobby;

        if (inSteamLobby)
        {
            // Broadcast host SteamID and state so clients can connect.
            SteamMatchmaking.SetLobbyData(SteamManager.LobbyId.Value, "hostSteamId", SteamManager.LocalSteamId.ToString());
            SteamMatchmaking.SetLobbyData(SteamManager.LobbyId.Value, "state", "starting");
        }

        //If we're not in a steam lobby, start game offline.
        ConnectGONet(offline: !inSteamLobby, isHost: true, hostSteamId: null);
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
            ConnectGONet(offline: false, isHost: false, hostSteamId: hostSteamId);
        }
    }

    void ConnectGONet(bool offline, bool isHost, string hostSteamId)
    {
        // GONetConnectionPreset is a ScriptableObject — use CreateInstance at runtime.
        var preset = ScriptableObject.CreateInstance<GONetConnectionPreset>();

        //If offline, we host.
        preset.role = offline || isHost ? GONetConnectionRole.Host : GONetConnectionRole.Client;
        // For clients, ipAddress is the host's SteamID string — SteamworksTransport
        // converts this to a SteamNetworkingIdentity for the P2P connection.
        if (!isHost)
            preset.ipAddress = hostSteamId;

        //Offline mode can't use Steam transport.
        bool useSteamTransport = !offline;
        preset.usePluggableTransport = useSteamTransport;
        if (useSteamTransport)
            preset.transportType = GONetTransportType.Steamworks;
        
        preset.maxConnections = RaceManager.MaxPlayers;

        GONetConnectionManager.CurrentPreset = preset;
        GONetConnectionManager.Connect();
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

    /// <summary>
    /// Kicks the GONet player as well as calls the SteamManager's KickPlayer.
    /// </summary>
    public static void KickGONetPlayer(ushort authorityId, CSteamID steamId)
    {
        if (!IsConnectedGONet || !GONetMain.IsServer)
            return;

        string playerName = SteamFriends.GetFriendPersonaName(steamId);
        Debug.Log($"Kicking GONet player '{playerName}' with authority ID {authorityId}");

        var server = GONetMain.gonetServer;
        if (server.TryGetRemoteClientByAuthorityId(authorityId, out var remote))
        {
            var uid = remote.ConnectionToClient.InitiatingClientConnectionUID;

            //Disconnect via GONet transport.
            server.TryDisconnectClientByConnectionUID(uid, GONetTransportDisconnectReason.Kicked, "host-kick");
        }

        SteamManager.KickPlayer(steamId);
    }

    public static void DisconnectGONet()
    {
        Instance.GONetConnectionManager.Disconnect();

        GONetMain.GONetClient?.Disconnect();
        GONetMain.gonetServer?.Stop();

        if (GONetMain.Global != null)
        {
            var go = GONetMain.Global.gameObject;
            go.SetActive(false);
            Destroy(go);
        }

        if (GONetMain.MyLocal != null)
        {
            var go = GONetMain.MyLocal.gameObject;
            go.SetActive(false);
            Destroy(go);
        }

        GONetMain.ResetForNewSession();
    }
}
