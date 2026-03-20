using System;
using UnityEngine;

[System.Serializable]
public class CustomKart
{
    public RollCageType RollCage;
    public WheelType Wheel;
    public ExtraDetailType ExtraDetail;
    public DecalType Decal;
    public string KartName;
    public string DriverName;
    public Color MainColor;
    public Color TrimColor;
    public Color DecalColor;

    public CustomKart() { }
}

// Enums for customization
[Serializable]
public enum WheelType
{
    Small = 0, Large = 1, Combo = 2
}

[Serializable]
public enum RollCageType
{
    Round = 0, Box = 1, Slim = 2
}

[Serializable]
public enum ExtraDetailType
{
    None = 0, FrontWing = 1
}

[Serializable]
public enum DecalType
{
    None = 0, Stripes = 1, Flames = 2
}
