using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class KartSaveManager : MonoBehaviour, IOrderedScript
{
    public static string SavePath => Path.Combine(Application.persistentDataPath, "CustomKart.json");

    public static KartSaveManager Instance = null;

    public int CallOrder => 1;
    
    public void OrderedAwake()
    {
        //Save a new default kart data if we don't have one.
        if (!File.Exists(SavePath))
            SaveKartData(new());
    }
    public void OrderedStart()
    {

    }

    public static void SaveKartData(CustomKartData kart)
    {
        string json = JsonUtility.ToJson(kart, true);
        File.WriteAllText(SavePath, json);
    }

    // Load all karts
    public static CustomKartData LoadKartData()
    {
        //No file, new.
        if (!File.Exists(SavePath)) 
            return new CustomKartData();

        string json = File.ReadAllText(SavePath);
        CustomKartData savedKart = JsonUtility.FromJson<CustomKartData>(json);
        //Null file deserialize, new.
        if (savedKart == null) 
            return new CustomKartData();

        return savedKart;
    }
}
