using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using UnityEngine.UI;
using System;

public class LobbyUI : MonoBehaviour, IOrderedScript
{
    public Button InviteBtn;
    public Button StartBtn;
    public Button LeaveBtn;
    [Space]
    public Transform LobbyPlayerParent;
    public LobbyPlayer PlayerPrefab;
    [Space]
    [Header("Auto Set")]
    public List<LobbyPlayer> SpawnedPlayers = new();

    public int CallOrder => 0;

    void Awake()
    {
        InviteBtn.onClick.AddListener(OnInviteClick);
        StartBtn.onClick.AddListener(OnStartClick);
        LeaveBtn.onClick.AddListener(OnLeaveClick);
    }

    public void OrderedAwake()
    {
        SteamManager.OnLobbyEntered += SteamManager_OnLobbyEntered;
        SteamManager.OnLobbyPlayerJoined += SteamManager_OnLobbyPlayerJoined;
        SteamManager.OnLobbyDisconnected += SteamManager_OnLobbyDisconnected;
        
        SteamManager.OnPersonaStateChange += SteamManager_OnPersonaStateChange;
        SteamManager.OnAvatarLoaded += SteamManager_OnAvatarLoaded;
    }

    public void OrderedStart()
    {
    }

    void OnDestroy()
    {
        SteamManager.OnLobbyEntered -= SteamManager_OnLobbyEntered;
        SteamManager.OnLobbyPlayerJoined -= SteamManager_OnLobbyPlayerJoined;
        SteamManager.OnLobbyDisconnected -= SteamManager_OnLobbyDisconnected;
        
        SteamManager.OnPersonaStateChange -= SteamManager_OnPersonaStateChange;
        SteamManager.OnAvatarLoaded -= SteamManager_OnAvatarLoaded;
    }

    void SteamManager_OnLobbyEntered(LobbyEnter_t info)
    {
        MainMenuManager.Instance.ShowPanel(gameObject, true);
        UpdateUI();

        StartBtn.gameObject.SetActive(SteamManager.IsLobbyOwner);
    }
    void SteamManager_OnLobbyPlayerJoined(PersonaStateChange_t info, bool joined)
    {
        //May or may not be in lobby, client player disconnect happens first.
        if (SteamManager.InSteamLobby)
            UpdateUI();

        StartBtn.gameObject.SetActive(SteamManager.IsLobbyOwner);
    }
    void SteamManager_OnLobbyDisconnected(ulong lobbyId)
    {
        UpdateUI();
        MainMenuManager.Instance.ShowPanel(gameObject, false);
    }
    
    void SteamManager_OnPersonaStateChange(PersonaStateChange_t info)
    {
        //Update their LobbyPlayer's info (like name and avatar).
        if (SpawnedPlayers.Find(x => x.SteamId.m_SteamID == info.m_ulSteamID) is LobbyPlayer spawned)
            spawned.UpdateInfo();
    }
    void SteamManager_OnAvatarLoaded(CSteamID steamId, Sprite sprite)
    {
        if (SpawnedPlayers.Find(x => x.SteamId.m_SteamID == steamId.m_SteamID) is LobbyPlayer spawned)
            spawned.UpdateInfo();
    }

    public void UpdateUI()
    {
        SpawnPlayers();
    }

    public void SpawnPlayers()
    {
        //Destroy old players.
        SpawnedPlayers.ForEach(x => Destroy(x.gameObject));
        SpawnedPlayers.Clear();

        //Spawn players.
        foreach (var steamId in SteamManager.GetLobbyPlayerIds())
        {
            var player = Instantiate(PlayerPrefab, LobbyPlayerParent);
            player.SetPlayer(steamId);

            SpawnedPlayers.Add(player);
        }
    }

    void OnInviteClick()
    {
        SteamFriends.ActivateGameOverlayInviteDialog(SteamManager.LobbyId.Value);
    }
    void OnStartClick()
    {
    }
    void OnLeaveClick()
    {
        SteamManager.LeaveLobby();
    }
}
