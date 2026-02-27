using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Steamworks;
using TMPro;
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
    [Header("Chat")]
    public ScrollRect ChatScroll;
    public Transform ChatContent;
    public TextMeshProUGUI ChatMessagePrefab;
    public TMP_InputField ChatInput;
    public Button SendBtn;
    [Space]
    [Header("Auto Set")]
    public List<LobbyPlayer> SpawnedPlayers = new();
    List<TextMeshProUGUI> spawnedMessages = new();

    public int CallOrder => 0;

    void Awake()
    {
        InviteBtn.onClick.AddListener(OnInviteClick);
        StartBtn.onClick.AddListener(OnStartClick);
        LeaveBtn.onClick.AddListener(OnLeaveClick);
        SendBtn.onClick.AddListener(OnSendClick);
        ChatInput.onSubmit.AddListener(_ => OnSendClick());
    }

    public void OrderedAwake()
    {
        SteamManager.OnLobbyEntered += SteamManager_OnLobbyEntered;
        SteamManager.OnLobbyPlayerJoined += SteamManager_OnLobbyPlayerJoined;
        SteamManager.OnLobbyDisconnected += SteamManager_OnLobbyDisconnected;
        
        SteamManager.OnPersonaStateChange += SteamManager_OnPersonaStateChange;
        SteamManager.OnAvatarLoaded += SteamManager_OnAvatarLoaded;
        SteamManager.OnLobbyChatMessage += SteamManager_OnLobbyChatMessage;
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
        SteamManager.OnLobbyChatMessage -= SteamManager_OnLobbyChatMessage;
    }

    void SteamManager_OnLobbyEntered(LobbyEnter_t info)
    {
        MainMenuManager.Instance.ShowPanel(gameObject, true);
        UpdateUI();

        StartBtn.gameObject.SetActive(SteamManager.IsLobbyOwner);
    }
    void SteamManager_OnLobbyPlayerJoined(LobbyChatUpdate_t info, bool joined)
    {
        //May or may not be in lobby, client player disconnect happens first.
        if (SteamManager.InSteamLobby)
            UpdateUI();

        StartBtn.gameObject.SetActive(SteamManager.IsLobbyOwner);
    }
    void SteamManager_OnLobbyDisconnected(ulong lobbyId)
    {
        UpdateUI();
        ClearChat();
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
    void SteamManager_OnLobbyChatMessage(CSteamID senderId, string message)
    {
        string senderName = SteamFriends.GetFriendPersonaName(senderId);
        AppendChatMessage($"{senderName}: {message}");
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
        NetworkManager.Instance.StartGame();
    }
    void OnLeaveClick()
    {
        SteamManager.LeaveLobby();
    }
    void OnSendClick()
    {
        string text = ChatInput.text.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        SteamManager.SendChatMessage(text);
        ChatInput.text = string.Empty;
        ChatInput.ActivateInputField();
    }

    void AppendChatMessage(string text)
    {
        var msg = Instantiate(ChatMessagePrefab, ChatContent);
        msg.text = text;
        spawnedMessages.Add(msg);

        LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)ChatContent);
        StartCoroutine(ScrollToBottom());
    }
    IEnumerator ScrollToBottom()
    {
        yield return null; // wait one frame for layout to update
        ChatScroll.verticalNormalizedPosition = 0f;
    }

    void ClearChat()
    {
        spawnedMessages.ForEach(x => Destroy(x.gameObject));
        spawnedMessages.Clear();
    }
}
