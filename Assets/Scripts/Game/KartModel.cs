using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class KartModel : MonoBehaviour
{
    public List<GameObject> RollCageObjs;
    [Space]
    public List<GameObject> WheelObjs;
    [Space]
    public List<GameObject> ExtraDetailObjs;
    [Space]
    public Transform DecalParent;
    [Tooltip("Should be ordered according to the enum.")]
    public List<Material> DecalMaterialChoices;
    [Space]
    public MeshRenderer BodyRenderer;
    public MeshRenderer TrimRenderer;
    [Space]
    [Tooltip("Objects that obstruct the camera's view, and are disabled when this kart is being viewed.")]
    public List<GameObject> ViewBlockingObjs;

    [NonSerialized] public CustomKartData KartData = new();

    public void ApplyKartData(CustomKartData data)
    {
        KartData = data;

        SetRollCage(KartData.RollCage);
        SetWheels(KartData.Wheel);
        SetExtraDetail(KartData.ExtraDetail);
        SetDecal(KartData.Decal);

        SetMainColor(KartData.MainColor);
        SetTrimColor(KartData.TrimColor);
        SetDecalColor(KartData.DecalColor);

        Debug.Log("Applying kart data!", gameObject);
    }

    public void SetRollCage(int typeInt) => SetRollCage((RollCageType)typeInt);
    public void SetRollCage(RollCageType type)
    {
        int index = (int)type;
        for (int i = 0; i < WheelObjs.Count; i++)
            RollCageObjs[i].SetActive(i == index);

        KartData.RollCage = type;

        //if (rollCage != null) Destroy(rollCage.gameObject);
        //int rollCageType = rollCageInput.value;
        //rollCage = Instantiate(rollCageOptions[rollCageType], kart.transform);
        //kartData.RollCage = (RollCageType)rollCageType;
    }

    public void SetWheels(int typeInt) => SetWheels((WheelType)typeInt);
    public void SetWheels(WheelType type)
    {
        int index = (int)type;
        for (int i = 0; i < WheelObjs.Count; i++)
            WheelObjs[i].SetActive(i == index);

        KartData.Wheel = type;
    }

    public void SetExtraDetail(int typeInt) => SetExtraDetail((ExtraDetailType)typeInt);
    public void SetExtraDetail(ExtraDetailType type)
    {
        int index = (int)type;
        for (int i = 0; i < ExtraDetailObjs.Count; i++)
            ExtraDetailObjs[i].SetActive(i == index);

        KartData.ExtraDetail = type;

        //if (extraDetail != null) Destroy(extraDetail.gameObject);
        //int extraDetailType = extraDetailInput.value;
        //extraDetail = Instantiate(extraDetailOptions[extraDetailType], kart.transform);
        //if (extraDetail.GetComponent<MeshRenderer>())
        //    extraDetail.GetComponent<MeshRenderer>().material.color = kartData.TrimColor;
        //kartData.ExtraDetail = (ExtraDetailType)extraDetailType;
    }

    public void SetDecal(int typeInt) => SetDecal((DecalType)typeInt);
    public void SetDecal(DecalType type)
    {
        int index = (int)type;
        var decalMat = DecalMaterialChoices[index];
        //Set the mats of the decal, and use the same saved color.
        foreach (var renderer in DecalParent.GetComponentsInChildren<MeshRenderer>())
        {
            renderer.material = decalMat;
            renderer.material.color = KartData.DecalColor;
        }

        KartData.Decal = type;

        //int decalType = decalInput.value;
        //for (int i = 0; i < decal.transform.childCount; i++)
        //    if (decal.transform.GetChild(i).GetComponent<MeshRenderer>())
        //    {
        //        decal.transform.GetChild(i).GetComponent<MeshRenderer>().material = decalMaterials[decalType];
        //        decal.transform.GetChild(i).GetComponent<MeshRenderer>().material.color = kartData.DecalColor;
        //    }
        //kartData.Decal = (DecalType)decalType;
    }

    public void SetDecalColor(Color color)
    {
        foreach (var renderer in DecalParent.GetComponentsInChildren<MeshRenderer>())
            renderer.material.color = color;

        KartData.DecalColor = color;
    }

    public void SetMainColor(Color color)
    {
        BodyRenderer.material.color = color;
        KartData.MainColor = color;
    }

    public void SetTrimColor(Color color)
    {
        TrimRenderer.material.color = color;
        foreach (var obj in ExtraDetailObjs)
        {
            foreach (var renderer in obj.GetComponentsInChildren<MeshRenderer>())
                renderer.material.color = color;
        }

        KartData.TrimColor = color;

        //trimMaterial.material.color = newColor;
        //kartData.TrimColor = newColor;
        //if (extraDetail != null && extraDetail.GetComponent<MeshRenderer>()) 
        //    extraDetail.GetComponent<MeshRenderer>().material.color = kartData.TrimColor;
    }

    public void EnableViewBlockingObjs(bool enable)
    {
        foreach (var obj in ViewBlockingObjs)
        {
            if (obj)
                obj.SetActive(enable);
        }
    }
}
