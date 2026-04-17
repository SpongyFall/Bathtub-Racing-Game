using System;
using UnityEngine;

[System.Serializable]
public class CustomKartData
{
    //public string KartName;
    //public string DriverName;

    public RollCageType RollCage;
    public WheelType Wheel;
    public ExtraDetailType ExtraDetail;
    public DecalType Decal;

    public Color MainColor = new(0.8867924f, 0.8867924f, 0.8867924f); //Default almost white.
    public Color TrimColor = new(0.5943396f, 0.5943396f, 0.5943396f); //Default gray, far right option.
    public Color DecalColor = Color.white;

    public CustomKartData() { }
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
    BathtopRacingAssociation    = 0, 
    ForTheWin                   = 1, 
    LeverSoaps                  = 2,
    Geometric                   = 3,
}
