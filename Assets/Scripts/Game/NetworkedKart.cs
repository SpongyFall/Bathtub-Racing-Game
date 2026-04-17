using GONet;
using Steamworks;
using System;
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
    public KartModel KartModel;
    [Space]
    [Header("Runtime Set Properties")]
    //Meant to be synced but GONet was getting compile errors during code generation using this property.
    public ulong OwnerSteamId;
    public bool SetupComplete = false;

    public Rigidbody Rigid => Controller.rb;

    protected override void Awake()
    {
        base.Awake();

        if (RaceManager.Instance)
        {
            //Not in multiplayer, setup right away.
            //if (!NetworkManager.IsConnectedGONet)
            //    Setup();
        
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

        if (IsMine)
        {
            //Load kart saved kart skin, will be RPCed in the Ready func.
            var loadedData = KartSaveManager.LoadKartData();
            KartModel.ApplyKartData(loadedData);
        }
        //Disable view blocking objs when being controlled.
        KartModel.EnableViewBlockingObjs(!IsMine);

        SetupComplete = true;
    }

    public void RPCReady()
    {
        var kartDataBytes = HelperClass.Serialize(KartModel.KartData);
        CallRpc(nameof(Ready), OwnerSteamId, kartDataBytes);
    }
    [TargetRpc(RpcTarget.All)]
    public void Ready(ulong ownerId, byte[] customKartDataBytes)
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
        //Load skin.
        var kartData = HelperClass.Deserialize<CustomKartData>(customKartDataBytes);
        if (kartData != null)
            KartModel.ApplyKartData(kartData);
        else
            Debug.LogError($"Failed to deserialize {nameof(CustomKartData)} for kart: '{name}'!", gameObject);

        //Tell manager we are ready.
        RaceManager.Instance.KartReady(this);
    }
}
