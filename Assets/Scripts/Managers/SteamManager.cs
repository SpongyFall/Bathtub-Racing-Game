using GONet;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class SteamManager : MonoBehaviour, IOrderedScript
{
    public static SteamManager Instance { get; private set; }

    public static CSteamID LocalSteamId => SteamUser.GetSteamID();

    public static CSteamID? LobbyId = null;
    public static bool InSteamLobby => LobbyId.HasValue;
    public static bool IsLobbyOwner => InSteamLobby && LocalSteamId == GetLobbyOwnerId();
    public static bool IsJoiningRandom => Instance.joiningRandomCor != null;

    public static LobbyMatchList_t LastMatchListInfo;
    public static List<CSteamID> LastMatchListLobbyIds = new();

    public static event Action<LobbyCreated_t> OnLobbyCreated;
    public static event Action<GameLobbyJoinRequested_t> OnLobbyJoinRequested;
    public static event Action<LobbyEnter_t> OnLobbyEntered;
    public static event Action<ulong> OnLobbyDisconnected;
    public static event Action<LobbyChatUpdate_t> OnLobbyChatUpdate;

    public static event Action<PersonaStateChange_t> OnPersonaStateChange;
    public static event Action<LobbyChatUpdate_t, bool> OnLobbyPlayerJoined;
    public static event Action<CSteamID, Sprite> OnAvatarLoaded;

    // Fired for every chat message received in the lobby (including your own).
    public static event Action<CSteamID, string> OnLobbyChatMessage;

    // Fired when lobby metadata changes (e.g. host signals game start).
    public static event Action<LobbyDataUpdate_t> OnLobbyDataUpdate;

    static Callback<LobbyCreated_t> cbLobbyCreated;
    static Callback<GameLobbyJoinRequested_t> cbLobbyJoinRequested;
    static Callback<LobbyEnter_t> cbLobbyEnter;
    static Callback<LobbyKicked_t> cbLobbyKicked;
    static Callback<LobbyChatUpdate_t> cbLobbyChatUpdate;
    static CallResult<LobbyMatchList_t> cbLobbyMatchList;

    static Callback<PersonaStateChange_t> cbPersonaStateChange;
    static Callback<AvatarImageLoaded_t> cbAvatarImageLoaded;
    static Callback<LobbyChatMsg_t> cbLobbyChatMsg;
    static Callback<LobbyDataUpdate_t> cbLobbyDataUpdate;

    static Dictionary<ulong, Sprite> avatarCache { get; } = new();

    Coroutine joiningRandomCor = null;

    public GONetSteamManager GONetSteamManager;

    public int CallOrder => 0;

    public void OrderedAwake()
    {
        Instance = this;

        //Subscribe to Steam events.
        cbLobbyCreated = Callback<LobbyCreated_t>.Create(LobbyCreated);
        cbLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(JoinRequested);
        cbLobbyEnter = Callback<LobbyEnter_t>.Create(LobbyEntered);
        cbLobbyKicked = Callback<LobbyKicked_t>.Create(LobbyDisconnected);
        cbLobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(LobbyChatUpdate);
        cbLobbyMatchList = CallResult<LobbyMatchList_t>.Create(LobbyMatchList);

        cbPersonaStateChange = Callback<PersonaStateChange_t>.Create(PersonaStateChange);
        cbAvatarImageLoaded = Callback<AvatarImageLoaded_t>.Create(AvatarImageLoaded);
        cbLobbyChatMsg = Callback<LobbyChatMsg_t>.Create(LobbyChatMsg);
        cbLobbyDataUpdate = Callback<LobbyDataUpdate_t>.Create(LobbyDataUpdate);
    }

    public void OrderedStart()
    {

    }

    void Update()
    {
    }

    void OnDestroy()
    {
        cbLobbyCreated?.Dispose();
        cbLobbyJoinRequested?.Dispose();
        cbLobbyEnter?.Dispose();
        cbLobbyKicked?.Dispose();
        cbLobbyChatUpdate?.Dispose();
        cbLobbyMatchList?.Dispose();

        cbPersonaStateChange?.Dispose();
        cbAvatarImageLoaded?.Dispose();
        cbLobbyChatMsg?.Dispose();
        cbLobbyDataUpdate?.Dispose();
    }

    public static void CreateLobby()
    {
        SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, RaceManager.MaxPlayers);
    }
    static void LobbyCreated(LobbyCreated_t info)
    {
        if (info.m_eResult != EResult.k_EResultOK)
        {
            Debug.LogError($"Failed to create Steam lobby: {info.m_eResult}");
            return;
        }

        Debug.Log($"Created Steam lobby ID: {info.m_ulSteamIDLobby}");

        // Publish the host's SteamID so clients can connect to us via P2P.
        SteamMatchmaking.SetLobbyData(new CSteamID(info.m_ulSteamIDLobby), "hostSteamId", LocalSteamId.m_SteamID.ToString());

        OnLobbyCreated.InvokeSafe(nameof(OnLobbyCreated), info);
    }

    static void JoinRequested(GameLobbyJoinRequested_t request)
    {
        if (InSteamLobby)
            return;

        Debug.Log($"Join requested for lobby ID: {request.m_steamIDLobby}");

        JoinLobby(request.m_steamIDLobby);
        OnLobbyJoinRequested.InvokeSafe(nameof(OnLobbyJoinRequested), request);
    }
    public static void JoinLobby(CSteamID lobbyId)
    {
        SteamMatchmaking.JoinLobby(lobbyId);
    }
    static void LobbyEntered(LobbyEnter_t info)
    {
        //Stop joining random if we are.
        if (IsJoiningRandom)
            StopJoiningRandomLobby();

        Debug.Log($"Entered Steam lobby ID: {info.m_ulSteamIDLobby}");
        LobbyId = new CSteamID(info.m_ulSteamIDLobby);

        //Get user info of all players.
        RequestPlayerInfo();

        OnLobbyEntered.InvokeSafe(nameof(OnLobbyEntered), info);
    }

    public static void LeaveLobby()
    {
        if (!InSteamLobby)
            return;

        SteamMatchmaking.LeaveLobby(LobbyId.Value);
        LobbyDisconnected(LobbyId.Value.m_SteamID);
    }
    static void LobbyDisconnected(LobbyKicked_t info)
    {
        //Called when kicked or lost connection.
        LobbyDisconnected(info.m_ulSteamIDLobby);
    }
    static void LobbyDisconnected(ulong lobbyId)
    {
        LobbyId = null;
        Debug.Log("Disconnected from Steam lobby!");
        OnLobbyDisconnected.InvokeSafe(nameof(OnLobbyDisconnected), lobbyId);
    }

    static void LobbyChatUpdate(LobbyChatUpdate_t info)
    {
        var lobbyId = new CSteamID(info.m_ulSteamIDLobby);
        var userId = new CSteamID(info.m_ulSteamIDUserChanged);
        //Name could be null here (I think?)
        var playerName = SteamFriends.GetFriendPersonaName(userId);
        var state = (EChatMemberStateChange)info.m_rgfChatMemberStateChange;

        OnLobbyChatUpdate.InvokeSafe(nameof(OnLobbyChatUpdate), info);

        if (state.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeEntered))
        {
            Debug.Log($"Player '{playerName}' with Id: {userId} joined Steam lobby {lobbyId}");

            //Someone joined, request non-cached player info.
            RequestPlayerInfo();

            OnLobbyPlayerJoined.InvokeSafe(nameof(OnLobbyPlayerJoined), info, true);
        }
        else if (state.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeLeft)
            || state.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeDisconnected)
            || state.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeKicked)
            || state.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeBanned))
        {
            if (state.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeLeft))
                Debug.Log($"Player '{playerName}' with Id: {userId} left Steam lobby {lobbyId}");
            if (state.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeDisconnected))
                Debug.Log($"Player '{playerName}' with Id: {userId} disconnected from Steam lobby {lobbyId}");
            if (state.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeKicked))
                Debug.Log($"Player '{playerName}' with Id: {userId} kicked from Steam lobby {lobbyId}");
            if (state.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeKicked))
                Debug.Log($"Player '{playerName}' with Id: {userId} banned from Steam lobby {lobbyId}");

            OnLobbyPlayerJoined.InvokeSafe(nameof(OnLobbyPlayerJoined), info, false);
        }
    }

    public static void RequestPlayerInfo()
    {
        if (!InSteamLobby)
            return;

        foreach (var steamId in GetLobbyPlayerIds())
        {
            //Request user info to trigger user information loading if not already cached.
            //Triggers PersonaStateChange.
            SteamFriends.RequestUserInformation(steamId, true);
        }
    }
    static void PersonaStateChange(PersonaStateChange_t info)
    {
        var steamId = new CSteamID(info.m_ulSteamID);
        var playerName = SteamFriends.GetFriendPersonaName(steamId);
        var changeFlags = info.m_nChangeFlags;

        var lobbyIds = GetLobbyPlayerIds();
        bool inLobby = lobbyIds.Contains(steamId);

        if (inLobby)
            Debug.Log($"{nameof(PersonaStateChange)} for lobby player Id: {info.m_ulSteamID}, name: '{playerName}', changeFlags: {changeFlags}");

        /*
        //Means player either left or joined.
        if (changeFlags.HasFlag(EPersonaChange.k_EPersonaChangeGameServer))
        {
            Debug.Log($"Player {(inLobby ? "joined" : "left")} with Id: {info.m_ulSteamID}, name: '{playerName}'");
            OnLobbyPlayerJoined.InvokeSafe(nameof(OnLobbyPlayerJoined), info, inLobby);
        }
        */

        //We'll get persona changes for friends not in our lobby, so ignore those for lobby stuff.
        if (inLobby)
        {
            //Load their avatar. Will not trigger a callback if already cached (like for local player).
            SteamFriends.GetLargeFriendAvatar(steamId);
            OnPersonaStateChange.InvokeSafe(nameof(OnPersonaStateChange), info);
        }
    }

    static void AvatarImageLoaded(AvatarImageLoaded_t info)
    {
        if (info.m_iImage <= 0)
            return;
        var playerName = SteamFriends.GetFriendPersonaName(info.m_steamID);

        Debug.Log($"{nameof(AvatarImageLoaded)} for player: '{playerName}' with Id: {info.m_steamID}");

        TryGetAvatar(info.m_steamID, out Sprite sprite);

        OnAvatarLoaded.InvokeSafe(nameof(OnAvatarLoaded), info.m_steamID, sprite);
    }

    static void LobbyChatMsg(LobbyChatMsg_t info)
    {
        if ((EChatEntryType)info.m_eChatEntryType != EChatEntryType.k_EChatEntryTypeChatMsg)
            return;

        var lobbyId = new CSteamID(info.m_ulSteamIDLobby);
        var senderId = new CSteamID(info.m_ulSteamIDUser);

        byte[] data = new byte[4096];
        int len = SteamMatchmaking.GetLobbyChatEntry(lobbyId, (int)info.m_iChatID, out CSteamID _, data, data.Length, out EChatEntryType _);

        string message = Encoding.UTF8.GetString(data, 0, len);
        OnLobbyChatMessage.InvokeSafe(nameof(OnLobbyChatMessage), senderId, message);
    }

    static void LobbyDataUpdate(LobbyDataUpdate_t info)
    {
        OnLobbyDataUpdate.InvokeSafe(nameof(OnLobbyDataUpdate), info);
    }

    /// <summary>
    /// Sends a chat message to everyone in the current lobby (including yourself — you will
    /// receive your own message back via <see cref="OnLobbyChatMessage"/>).
    /// </summary>
    public static void SendChatMessage(string message)
    {
        if (!InSteamLobby || string.IsNullOrWhiteSpace(message))
            return;

        byte[] data = Encoding.UTF8.GetBytes(message);
        SteamMatchmaking.SendLobbyChatMsg(LobbyId.Value, data, data.Length);
    }

    /// <summary>
    /// Attempts to get the avatar for the given Steam ID. If the avatar is not cached, it will return false and make the API
    /// call to get the avatar, once the avatar is loaded it will then return true..
    /// </summary>
    public static bool TryGetAvatar(CSteamID steamID, out Sprite sprite)
    {
        var imageid = SteamFriends.GetLargeFriendAvatar(steamID);
        //Not loaded yet (-1), no avatar, or invalid (0).
        if (imageid <= 0)
        {
            sprite = null;
            return false;
        }

        //Try create a texture 2d.
        if (!TryCreateAvatarTexture(imageid, out Texture2D texture))
        {
            sprite = null;
            return false;
        }

        sprite = HelperClass.ToSprite(texture);
        //Cache sprite.
        SetAvatarCache(steamID.m_SteamID, sprite);
        return true;
    }
    static bool TryCreateAvatarTexture(int imageId, out Texture2D texture)
    {
        texture = null;

        if (!SteamUtils.GetImageSize(imageId, out uint width, out uint height))
            return false;

        //RGBA32 = 4 bytes per pixel.
        int size = (int)(width * height * 4);
        byte[] data = new byte[size];
        if (!SteamUtils.GetImageRGBA(imageId, data, data.Length))
            return false;

        //Flip vertically.
        int rows = (int)width * 4;
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * rows;
            int bot = ((int)height - 1 - y) * rows;
            for (int x = 0; x < rows; x++)
            {
                byte temp = data[top + x];
                data[top + x] = data[bot + x];
                data[bot + x] = temp;
            }
        }

        //Make a texture 2d from the raw data.
        texture = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false, false);
        texture.LoadRawTextureData(data);
        texture.Apply();
        return true;
    }
    static void SetAvatarCache(ulong steamid, Sprite sprite)
    {
        //If we already have a cached texture.
        if (avatarCache.TryGetValue(steamid, out Sprite existing) && existing != null)
        {
            //Destroy it to free memory before caching the new one.
            if (!ReferenceEquals(existing, sprite))
            {
                //Both the text and the sprite.
                Destroy(existing.texture);
                Destroy(existing);
            }
        }

        avatarCache[steamid] = sprite;
    }

    //public static Sprite GetAvatar(CSteamID steamId)
    //{
    //    ulong id = steamId.m_SteamID;
    //    if (AvatarCache.TryGetValue(id, out var cached) && cached != null)
    //        return cached;

    //    int imageId = SteamFriends.GetLargeFriendAvatar(steamId);
    //    if (imageId <= 0)
    //        return null;

    //    if (TryCreateAvatarTexture(imageId, out Texture2D texture))
    //    {
    //        SetAvatarCache(id, texture);
    //        return texture;
    //    }

    //    return null;
    //}

    public static void StartJoiningRandomLobby()
    {
        StopJoiningRandomLobby();

        Instance.joiningRandomCor = Instance.StartCoroutine(Instance.JoinRandomLobbyCor());
    }
    public static void StopJoiningRandomLobby()
    {
        if (Instance.joiningRandomCor != null)
        {
            Instance.StopCoroutine(Instance.joiningRandomCor);
            Instance.joiningRandomCor = null;
        }
    }
    IEnumerator JoinRandomLobbyCor()
    {
        //Tries to join a random lobby every 4 seconds until successful. Will stop if we join a lobby or if manually stopped.
        while (!InSteamLobby)
        {
            RequestLobbyList();
            yield return new WaitForSeconds(4f);
        }

        Debug.Log("Finished join rand cor");
        joiningRandomCor = null;
    }

    public static void RequestLobbyList()
    {
        SteamMatchmaking.AddRequestLobbyListNumericalFilter("players", RaceManager.MaxPlayers, ELobbyComparison.k_ELobbyComparisonLessThan);
        var call = SteamMatchmaking.RequestLobbyList();
        cbLobbyMatchList.Set(call);
    }
    static void LobbyMatchList(LobbyMatchList_t info, bool failure)
    {
        if (failure || info.m_nLobbiesMatching == 0)
        {
            Debug.Log("No lobbies found to join.");
            return;
        }

        //Collect lobby Ids.
        LastMatchListInfo = info;
        LastMatchListLobbyIds.Clear();
        for (int i = 0; i < info.m_nLobbiesMatching; i++)
        {
            var lobbyId = SteamMatchmaking.GetLobbyByIndex(i);

            LastMatchListLobbyIds.Add(lobbyId);
            //Get lobby data (would need another callback).
            //SteamMatchmaking.RequestLobbyData(lobbyId);
            //string name = SteamMatchmaking.GetLobbyData(lobbyId, "name");
            //int playerCount = SteamMatchmaking.GetNumLobbyMembers(lobbyId);

            //Debug.Log($"Found lobby ID: {lobbyId}, name: '{name}', player count: {playerCount}");
            //if (playerCount < RaceManager.MaxPlayers)
            //    joinableIds.Add(lobbyId);
        }

        Debug.Log($"Found {LastMatchListLobbyIds.Count} Steam lobbies.");

        if (IsJoiningRandom)
        {
            if (LastMatchListLobbyIds.Count == 0)
            {
                Debug.Log("No joinable lobbies found.");
                return;
            }

            //Join random lobby.
            Debug.Log($"Joining a random lobby...");
            var randomLobbyId = LastMatchListLobbyIds[UnityEngine.Random.Range(0, LastMatchListLobbyIds.Count)];
            JoinLobby(randomLobbyId);
        }
    }

    /// <returns>Empty list if not in a lobby.</returns>
    public static List<CSteamID> GetLobbyPlayerIds()
    {
        List<CSteamID> playerIds = new();
        if (!InSteamLobby)
            return playerIds;

        for (int i = 0; i < SteamMatchmaking.GetNumLobbyMembers(LobbyId.Value); i++)
            playerIds.Add(SteamMatchmaking.GetLobbyMemberByIndex(LobbyId.Value, i));
        return playerIds;
    }

    public static CSteamID GetLobbyOwnerId()
    {
        return InSteamLobby ? SteamMatchmaking.GetLobbyOwner(LobbyId.Value) : CSteamID.Nil;
    }
}
