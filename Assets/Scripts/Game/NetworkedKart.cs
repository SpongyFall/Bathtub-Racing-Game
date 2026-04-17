using GONet;
using Steamworks;
using System.Linq;
using TMPro;
using UnityEngine;

/// <summary>
/// Attached to the PlayerKart prefab alongside GONetParticipant.
/// Disables camera, audio listener, and local-only components on remote karts.
/// </summary>
[RequireComponent(typeof(GONetParticipant))]
public class NetworkedKart : GONetParticipantCompanionBehaviour
{
    public PlayerKartController Controller;
    public RacerInfo RacerInfo;
    public Camera Camera;
    public Canvas WorldCanvas;
    public TextMeshProUGUI RacerName;
    public QuickOutline Outline;
    [Space]
    [Header("Runtime Set Properties")]
    //Meant to be synced but GONet was getting compile errors during code generation using this property.
    public ulong OwnerSteamId;

    public Rigidbody Rigid => Controller.rb;

    protected override void Awake()
    {
        base.Awake();

        if (RaceManager.Instance)
        {
            //Not in multiplayer, setup right away.
            if (!NetworkManager.IsConnectedGONet)
                Setup();
        
            RaceManager.Instance.AddNetworkedKart(this, true);
        }
    }

    void Update()
    {
        if (RaceManager.Instance == null)
            return;

        if (WorldCanvas.gameObject.activeSelf && RaceManager.Instance.MainCamera)
            WorldCanvas.transform.rotation = Quaternion.LookRotation(WorldCanvas.transform.position - RaceManager.Instance.MainCamera.transform.position);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();

        if (RaceManager.Instance)
            RaceManager.Instance.AddNetworkedKart(this, false);
    }

    public override void OnGONetReady()
    {
        base.OnGONetReady();
        Debug.Log($"On GONet ready for Kart index: {RaceManager.Instance.NetworkedKarts.IndexOf(this)}");

        //Called when this participant as well as GONet is ready and networked.
        Setup();
    }
    public override void OnGONetParticipantStarted()
    {
        base.OnGONetParticipantStarted();
    }

    public void Setup()
    {
        bool inMultiplayer = NetworkManager.IsConnectedGONet;
        int kartIndex = RaceManager.Instance.NetworkedKarts.IndexOf(this);
        Debug.Log($"NetworkedKart index {kartIndex} SetUp, IsMine: {IsMine}, in multiplayer: {inMultiplayer}", gameObject);

        if (!inMultiplayer || IsMine)
        {
            OwnerSteamId = SteamManager.LocalSteamId.m_SteamID;
            RaceManager.Instance.ClientKart = this;
        }

        //Enable camera.
        Camera.gameObject.SetActiveSafe(!inMultiplayer || gonetParticipant.IsLocallyControlled);
        RacerInfo.IsClientPlayer = !inMultiplayer || IsMine;
        Controller.rb.isKinematic = inMultiplayer && !IsMine;
        WorldCanvas.gameObject.SetActiveSafe(!IsMine);
        Outline.enabled = !IsMine;

        if (!inMultiplayer || IsMine)
        {
            //Load kart skin.
            //RPCLoadCustomKart();
        }

        //RPC to all the owner's Steam ID.
        if (IsMine)
            CallRpc(nameof(RPCSetup), OwnerSteamId);
    }
    [TargetRpc(RpcTarget.All)]
    public void RPCSetup(ulong ownerId)
    {
        //Called to all from owner.
        OwnerSteamId = ownerId;
        var steamId = new CSteamID(ownerId);

        //Link auth and Steam ID.
        RaceManager.Instance.LinkAuthToSteamId(GONetParticipant.OwnerAuthorityId, steamId);

        //Set name.
        string playerName = SteamFriends.GetFriendPersonaName(steamId);
        RacerName.text = playerName;
        name = playerName;

        //Tell manager we are ready.
        RaceManager.Instance.KartReady(this);
    }

    /// <summary>
    /// Loads the client's selected kart skins and RPCs to all players to display it.
    /// </summary>
    public void RPCLoadCustomKart()
    {
        var kartName = PlayerPrefs.GetString(SelectCustomizations.SelectedKartNameKey, "");
        var savedKarts = KartSaveManager.LoadKarts().ToList();
        CustomKart customKart = savedKarts.Find(x => x.KartName == kartName);

        if (customKart == null)
        {
            Debug.Log("No custom kart to load found!");
            return;
        }
        var serializable = new CustomKartSerializable(customKart);

        //If multiplayer, RPC.
        if (NetworkManager.IsConnectedGONet)
            CallRpc(nameof(LoadCustomKart), serializable);
        else
            LoadCustomKart(serializable);
    }
    //[TargetRpc(RpcTarget.All)]
    void LoadCustomKart(CustomKartSerializable kartSerializable)
    {
        //Called to all players.
        var customKart = kartSerializable.ToCustomKart();

        //Apply colors.
        foreach (MeshRenderer renderer in GetComponentsInChildren<MeshRenderer>())
        {
            if (renderer.gameObject.name.Contains("Body"))
                renderer.material.color = customKart.MainColor;

            if (renderer.gameObject.name.Contains("TubCap"))
                renderer.material.color = customKart.TrimColor;

            if (renderer.gameObject.name.Contains("Decal"))
                renderer.material.color = customKart.DecalColor;
        }

        Debug.Log($"Loaded custom kart: '{customKart.KartName}' for player ID: {OwnerSteamId}", this);
    }
}
