using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class SelectCustomizations : MonoBehaviour
{
    public const string SelectedKartNameKey = "SelectedKartName";

    // Live update objects
    public GameObject kart;
    public GameObject wheels;
    public GameObject rollCage;
    public GameObject extraDetail;
    public GameObject decal;

    private CustomKart _customKart;

    // Customization options
    public GameObject[] wheelOptions;
    public GameObject[] rollCageOptions;
    public GameObject[] extraDetailOptions;
    public GameObject[] decalOptions;

    // Kart materials
    public MeshRenderer bodyMaterial;
    public MeshRenderer trimMaterial;
    public Material[] decalMaterials;

    // UI input fields
    public TMP_Dropdown rollCageInput;
    public TMP_Dropdown extraDetailInput;
    public TMP_Dropdown decalInput;
    public TMP_Dropdown wheelInput;
    public TMP_InputField kartNameInput;
    public TMP_InputField driverNameInput;

    void Awake()
    {
        _customKart = new CustomKart();
        UpdateLapDisplayDefaults();
    }
    void OnEnable()
    {
        LoadSelectedKart();
    }
    void Start()
    {
        //_customKart = new CustomKart();
        //UpdateLapDisplayDefaults();
    }

    // Methods for setting parts
    public void SetWheels()
    {
        if (wheels != null) Destroy(wheels.gameObject);
        int wheelType = wheelInput.value;
        wheels = Instantiate(wheelOptions[wheelType], kart.transform);
        _customKart.Wheel = (WheelType)wheelType;
    }

    public void SetRollCage()
    {
        if (rollCage != null) Destroy(rollCage.gameObject);
        int rollCageType = rollCageInput.value;
        rollCage = Instantiate(rollCageOptions[rollCageType], kart.transform);
        _customKart.RollCage = (RollCageType)rollCageType;
    }

    public void SetExtraDetail()
    {
        if (extraDetail != null) Destroy(extraDetail.gameObject);
        int extraDetailType = extraDetailInput.value;
        extraDetail = Instantiate(extraDetailOptions[extraDetailType], kart.transform);
        if(extraDetail.GetComponent<MeshRenderer>())
            extraDetail.GetComponent<MeshRenderer>().material.color = _customKart.TrimColor;
        _customKart.ExtraDetail = (ExtraDetailType)extraDetailType;
    }

    public void SetDecal()
    {
        int decalType = decalInput.value;
        for(int i = 0; i < decal.transform.childCount; i++)
            if (decal.transform.GetChild(i).GetComponent<MeshRenderer>())
            {
                decal.transform.GetChild(i).GetComponent<MeshRenderer>().material = decalMaterials[decalType];
                decal.transform.GetChild(i).GetComponent<MeshRenderer>().material.color = _customKart.DecalColor;
            }
        _customKart.Decal = (DecalType)decalType;
    }

    // Methods for setting colors
    public void SelectMainColor(string color)
    {
        if (ColorUtility.TryParseHtmlString("#" + color, out Color newColor))
        {
            bodyMaterial.material.color = newColor;
            _customKart.MainColor = newColor;
        }
    }

    public void SelectTrimColor(string color)
    {
        if (ColorUtility.TryParseHtmlString("#" + color, out Color newColor))
        {
            trimMaterial.material.color = newColor;
            _customKart.TrimColor = newColor;
            if (extraDetail != null && extraDetail.GetComponent<MeshRenderer>()) extraDetail.GetComponent<MeshRenderer>().material.color = _customKart.TrimColor;
        }
    }

    public void SelectDecalColor(string color)
    {
        if (ColorUtility.TryParseHtmlString("#" + color, out Color newColor))
        {
            for(int i = 0; i < decal.transform.childCount; i++)
                if (decal.transform.GetChild(i).GetComponent<MeshRenderer>())
                {
                    decal.transform.GetChild(i).GetComponent<MeshRenderer>().material.color = newColor;
                }
            _customKart.DecalColor = newColor;
        }
    }


    // Methods for names
    public void SetKartName() => _customKart.KartName = kartNameInput.text;
    public void SetDriverName() => _customKart.DriverName = driverNameInput.text;


    // Save and Load
    public void ConfirmChanges()
    {
        KartSaveManager.SaveKart(_customKart);

        // Tell the race scene which kart to load
        PlayerPrefs.SetString(SelectedKartNameKey, _customKart.KartName);
        PlayerPrefs.Save();

        Debug.Log("Kart saved and selected: " + _customKart.KartName);
    }


    public void LoadSelectedKart()
    {
        if (PlayerPrefs.HasKey(SelectedKartNameKey))
        {
            var selectedName = PlayerPrefs.GetString(SelectedKartNameKey);
            var karts = KartSaveManager.LoadKarts().ToList();
            var selected = karts.Find(x => x.KartName == selectedName);

            if (selected != null)
                _customKart = selected;
        }

        LoadKart(_customKart);
    }
    public void LoadKart(CustomKart kartData)
    {
        if (wheels != null) Destroy(wheels.gameObject);
        if (rollCage != null) Destroy(rollCage.gameObject);
        if (extraDetail != null) Destroy(extraDetail.gameObject);
        if (decal != null) Destroy(decal.gameObject);

        // Instantiate prefabs
        wheels = Instantiate(wheelOptions[(int)kartData.Wheel], kart.transform);
        rollCage = Instantiate(rollCageOptions[(int)kartData.RollCage], kart.transform);
        extraDetail = Instantiate(extraDetailOptions[(int)kartData.ExtraDetail], kart.transform);
        decal = Instantiate(decalOptions[(int)kartData.Decal], kart.transform);

        // Colors
        bodyMaterial.material.color = kartData.MainColor;
        trimMaterial.material.color = kartData.TrimColor;
        if(extraDetail.GetComponent<MeshRenderer>())
            extraDetail.GetComponent<MeshRenderer>().material.color = kartData.TrimColor;
        for(int i = 0; i < decal.transform.childCount; i++)
            if (decal.transform.GetChild(i).GetComponent<MeshRenderer>())
            {
                //decal.transform.GetChild(i).GetComponent<MeshRenderer>().material = decalMaterials[kartData.Decal];
                decal.transform.GetChild(i).GetComponent<MeshRenderer>().material.color = _customKart.DecalColor;
            }

        // Update dropdowns and input fields
        wheelInput.value = (int)kartData.Wheel;
        rollCageInput.value = (int)kartData.RollCage;
        extraDetailInput.value = (int)kartData.ExtraDetail;
        decalInput.value = (int)kartData.Decal;
        kartNameInput.text = kartData.KartName;
        driverNameInput.text = kartData.DriverName;

        _customKart = kartData;
    }

    // Initial defaults
    private void UpdateLapDisplayDefaults()
    {
        SetWheels();
        SetRollCage();
        SetExtraDetail();
        SetDecal();
        SelectMainColor("FFFFFF");
        SelectTrimColor("000000");
        SelectDecalColor("FFFFFF");
    }
}