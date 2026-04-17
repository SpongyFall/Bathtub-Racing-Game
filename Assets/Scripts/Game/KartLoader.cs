using UnityEngine;

public class KartLoader : MonoBehaviour
{
    public GameObject kartBasePrefab;
    public string selectedKartName;

    void Start()
    {
        LoadKart();
    }

    void LoadKart()
    {
        selectedKartName = PlayerPrefs.GetString(SelectCustomizations.SelectedKartNameKey, "");
        GameObject kart;

        // Load saved karts
        CustomKart[] saved = KartSaveManager.LoadKarts();
        CustomKart target = null;

        foreach (var k in saved)
            if (k.KartName == selectedKartName)
                target = k;

        // Spawn kart (only ONCE)
        kart = Instantiate(kartBasePrefab);

        // Apply colors if saved exists
        if (target != null)
        {
            var renderers = kart.GetComponentsInChildren<MeshRenderer>();

            foreach (MeshRenderer r in renderers)
            {
                if (r.gameObject.name.Contains("Body"))
                    r.material.color = target.MainColor;

                if (r.gameObject.name.Contains("TubCap"))
                    r.material.color = target.TrimColor;

                if (r.gameObject.name.Contains("Decal"))
                    r.material.color = target.DecalColor;
            }

            Debug.Log("Loaded kart: " + target.KartName);
        }
        else
        {
            Debug.LogWarning("No saved kart found — using default.");
        }
    }
}


