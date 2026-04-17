using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MapManager : MonoBehaviour
{
    public SpriteRenderer MapSprite;
    public Transform BlipParent;
    public MapBlip BlipPrefab;
    [Space]
    [Header("Runtime Set Props")]
    public List<MapBlip> ActiveBlips = new();

    void Update()
    {
        if (!RaceManager.Instance.RaceActive)
            return;
    }

    /// <summary>
    /// Adds a blip to the map for the given world object. The blip will automatically update its position to match the 
    /// world object's.
    /// </summary>
    public void AddBlip(Transform worldObj, Color color)
    {
        var blip = Instantiate(BlipPrefab, BlipParent);

        blip.Set(worldObj, color, this);
        ActiveBlips.Add(blip);
    }

    public Vector3 ClosestPointOnMapSprite(Vector3 worldPoint)
    {
        var spriteTrans = MapSprite.transform;

        Vector3 localPos = spriteTrans.InverseTransformPoint(worldPoint);
        var bounds = MapSprite.sprite.bounds;

        localPos.x = Mathf.Clamp(localPos.x, bounds.min.x, bounds.max.x);
        localPos.y = Mathf.Clamp(localPos.y, bounds.min.y, bounds.max.y);

        return spriteTrans.TransformPoint(localPos);
    }
}
