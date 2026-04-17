using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

public class SelectCustomizations : MonoBehaviour
{
    public KartModel KartModel;

    public TMP_Dropdown rollCageInput;
    public TMP_Dropdown wheelInput;
    public TMP_Dropdown extraDetailInput;
    public TMP_Dropdown decalInput;

    //public TMP_InputField kartNameInput;
    //public TMP_InputField driverNameInput;

    void Awake()
    {
        SetDropdowns();
    }

    void OnEnable()
    {
        LoadData();
    }
    void OnDisable()
    {
        SaveData();
    }

    void SetDropdowns()
    {
        SetDropdown(rollCageInput, typeof(RollCageType), KartModel.SetRollCage);
        SetDropdown(wheelInput, typeof(WheelType), KartModel.SetWheels);
        SetDropdown(extraDetailInput, typeof(ExtraDetailType), KartModel.SetExtraDetail);
        SetDropdown(decalInput, typeof(DecalType), KartModel.SetDecal);
    }

    public void SetDropdown(TMP_Dropdown dropdown, Type enumType, UnityAction<int> onValueChanged)
    {
        dropdown.ClearOptions();
        //Add options (directly ordered by value).
        var optionData = new List<TMP_Dropdown.OptionData>();
        foreach (var type in Enum.GetValues(enumType))
            optionData.Add(new(type.ToString().SpaceUppercases()));
        dropdown.AddOptions(optionData);

        //Register update model func.
        dropdown.onValueChanged.RemoveAllListeners();
        dropdown.onValueChanged.AddListener(onValueChanged);
    }
    void LoadDropdowns(CustomKartData data)
    {
        rollCageInput.SetValueWithoutNotify((int)data.RollCage);
        wheelInput.SetValueWithoutNotify((int)data.Wheel);
        extraDetailInput.SetValueWithoutNotify((int)data.ExtraDetail);
        decalInput.SetValueWithoutNotify((int)data.Decal);
    }

    //Color btns
    public void SelectMainColor(string colorStr)
    {
        var col = ParseInputColor(colorStr);
        KartModel.SetMainColor(col);
    }

    public void SelectTrimColor(string colorStr)
    {
        var col = ParseInputColor(colorStr);
        KartModel.SetTrimColor(col);

        //if (ColorUtility.TryParseHtmlString("#" + color, out Color newColor))
        //{
        //    trimMaterial.material.color = newColor;
        //    kartData.TrimColor = newColor;
        //    if (extraDetail != null && extraDetail.GetComponent<MeshRenderer>())
        //        extraDetail.GetComponent<MeshRenderer>().material.color = kartData.TrimColor;
        //}
    }

    public void SelectDecalColor(string colorStr)
    {
        var col = ParseInputColor(colorStr);
        KartModel.SetDecalColor(col);

        //if (ColorUtility.TryParseHtmlString("#" + color, out Color newColor))
        //{
        //    for (int i = 0; i < decal.transform.childCount; i++)
        //        if (decal.transform.GetChild(i).GetComponent<MeshRenderer>())
        //        {
        //            decal.transform.GetChild(i).GetComponent<MeshRenderer>().material.color = newColor;
        //        }
        //    kartData.DecalColor = newColor;
        //}
    }


    public void LoadData()
    {
        var loadedData = KartSaveManager.LoadKartData();
        //Apply saved data.
        KartModel.ApplyKartData(loadedData);
        //Set initial dropdowns.
        LoadDropdowns(loadedData);
    }
    public void SaveData()
    {
        KartSaveManager.SaveKartData(KartModel.KartData);
        Debug.Log("Kart data saved!");
    }

    public static Color ParseInputColor(string colorStr)
    {
        ColorUtility.TryParseHtmlString("#" + colorStr, out Color newColor);
        return newColor;
    }

    //// Methods for setting parts
    //public void SetWheels()
    //{
    //    if (wheels != null) Destroy(wheels.gameObject);
    //    int wheelType = wheelInput.value;
    //    wheels = Instantiate(wheelOptions[wheelType], kart.transform);
    //    kartData.Wheel = (WheelType)wheelType;
    //}

    //public void SetRollCage()
    //{
    //    if (rollCage != null) Destroy(rollCage.gameObject);
    //    int rollCageType = rollCageInput.value;
    //    rollCage = Instantiate(rollCageOptions[rollCageType], kart.transform);
    //    kartData.RollCage = (RollCageType)rollCageType;
    //}

    //public void SetExtraDetail()
    //{
    //    if (extraDetail != null) Destroy(extraDetail.gameObject);
    //    int extraDetailType = extraDetailInput.value;
    //    extraDetail = Instantiate(extraDetailOptions[extraDetailType], kart.transform);
    //    if (extraDetail.GetComponent<MeshRenderer>())
    //        extraDetail.GetComponent<MeshRenderer>().material.color = kartData.TrimColor;
    //    kartData.ExtraDetail = (ExtraDetailType)extraDetailType;
    //}

    //public void SetDecal()
    //{
    //    int decalType = decalInput.value;
    //    for(int i = 0; i < decal.transform.childCount; i++)
    //        if (decal.transform.GetChild(i).GetComponent<MeshRenderer>())
    //        {
    //            decal.transform.GetChild(i).GetComponent<MeshRenderer>().material = decalMaterials[decalType];
    //            decal.transform.GetChild(i).GetComponent<MeshRenderer>().material.color = kartData.DecalColor;
    //        }
    //    kartData.Decal = (DecalType)decalType;
    //}

    //// Methods for setting colors
    //public void SelectMainColor(string color)
    //{
    //    if (ColorUtility.TryParseHtmlString("#" + color, out Color newColor))
    //    {
    //        bodyMaterial.material.color = newColor;
    //        kartData.MainColor = newColor;
    //    }
    //}

    //public void SelectTrimColor(string color)
    //{
    //    if (ColorUtility.TryParseHtmlString("#" + color, out Color newColor))
    //    {
    //        trimMaterial.material.color = newColor;
    //        kartData.TrimColor = newColor;
    //        if (extraDetail != null && extraDetail.GetComponent<MeshRenderer>()) 
    //            extraDetail.GetComponent<MeshRenderer>().material.color = kartData.TrimColor;
    //    }
    //}

    //public void SelectDecalColor(string color)
    //{
    //    if (ColorUtility.TryParseHtmlString("#" + color, out Color newColor))
    //    {
    //        for(int i = 0; i < decal.transform.childCount; i++)
    //            if (decal.transform.GetChild(i).GetComponent<MeshRenderer>())
    //            {
    //                decal.transform.GetChild(i).GetComponent<MeshRenderer>().material.color = newColor;
    //            }
    //        kartData.DecalColor = newColor;
    //    }
    //}

    // Methods for names
    //public void SetKartName() => kartData.KartName = kartNameInput.text;
    //public void SetDriverName() => kartData.DriverName = driverNameInput.text;

    /*
    public void LoadSelectedKart()
    {
        if (PlayerPrefs.HasKey(SelectedKartNameKey))
        {
            var selectedName = PlayerPrefs.GetString(SelectedKartNameKey);
            var karts = KartSaveManager.LoadKartData().ToList();
            var selected = karts.Find(x => x.KartName == selectedName);

            if (selected != null)
                kartData = selected;
        }

        LoadKart(kartData);
    }
    public void LoadKart(CustomKartData kartData)
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
                decal.transform.GetChild(i).GetComponent<MeshRenderer>().material.color = this.kartData.DecalColor;
            }

        // Update dropdowns and input fields
        wheelInput.value = (int)kartData.Wheel;
        rollCageInput.value = (int)kartData.RollCage;
        extraDetailInput.value = (int)kartData.ExtraDetail;
        decalInput.value = (int)kartData.Decal;
        kartNameInput.text = kartData.KartName;
        driverNameInput.text = kartData.DriverName;

        this.kartData = kartData;
    }

    // Initial defaults
    void UpdateLapDisplayDefaults()
    {
        SetWheels();
        SetRollCage();
        SetExtraDetail();
        SetDecal();
        SelectMainColor("FFFFFF");
        SelectTrimColor("000000");
        SelectDecalColor("FFFFFF");
    }
    */
}