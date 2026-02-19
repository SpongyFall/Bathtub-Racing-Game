using Steamworks;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class LobbyPlayer : MonoBehaviour
{
    public TextMeshProUGUI PlayerName;
    public Image PlayerAvatar;

    [HideInInspector] public CSteamID SteamId;

    void Awake()
    {

    }

    public void SetPlayer(CSteamID steamId)
    {
        SteamId = steamId;

        //Update if possible. Otherwise, will update later when things load.
        UpdateInfo();
    }

    public void UpdateInfo()
    {
        //Update name.
        PlayerName.text = SteamFriends.GetFriendPersonaName(SteamId);

        //Set avatar.
        if (SteamManager.TryGetAvatar(SteamId, out Sprite sprite))
            PlayerAvatar.sprite = sprite;
    }
}
