using GONet;
using Steamworks;
using System.Linq;
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
    public GameObject CameraObj;
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

    protected override void OnDestroy()
    {
        base.OnDestroy();

        if (RaceManager.Instance)
            RaceManager.Instance.AddNetworkedKart(this, false);
    }

    public override void OnGONetParticipantStarted(GONetParticipant gonetParticipant)
    {
        base.OnGONetParticipantStarted(gonetParticipant);
        Debug.Log("On GONet started ");
    }

    public override void OnGONetReady()
    {
        base.OnGONetReady();
        Debug.Log("On GONet ready");

        //Called when this participant as well as GONet is ready and networked.
        Setup();
    }

    public void Setup()
    {
        Debug.Log($"Setting up networked cart", gameObject);
        bool inMultiplayer = NetworkManager.IsConnectedGONet;
     
        if (!inMultiplayer || IsMine)
        {
            OwnerSteamId = SteamManager.LocalSteamId.m_SteamID;
            RaceManager.Instance.ClientKart = this;
        }

        //Enable camera.
        CameraObj.SetActiveSafe(!inMultiplayer || gonetParticipant.IsLocallyControlled);
        RacerInfo.isClientPlayer = !inMultiplayer || IsMine;
        Controller.rb.isKinematic = inMultiplayer && !IsMine;

        if (!inMultiplayer || IsMine)
        {
            //Load kart skin.
            RPCLoadCustomKart();
        }

        //Refresh racers.
        var rpm = FindFirstObjectByType<RacePositionManager>();
        if (rpm != null)
        {
            rpm.RefreshRacers();
            Debug.Log("REFRESH after player kart spawn");
        }
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
    [TargetRpc]
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
