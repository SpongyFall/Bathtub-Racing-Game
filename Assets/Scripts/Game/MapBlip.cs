using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MapBlip : MonoBehaviour
{
    public int SortingPrio = 0;
    public MeshRenderer Renderer;
    [Space]
    [Header("Runtime Set Props")]
    public Transform TrackedWorldObj;

    MapManager manager;

    void Update()
    {
        transform.position = manager.ClosestPointOnMapSprite(TrackedWorldObj.position);
    }

    public void Set(Transform worldObj, Color color, MapManager manager)
    {
        this.manager = manager;

        SetColor(color);
    }

    public void SetColor(Color color)
    {
        Renderer.material.SetColor("_Color", color);
    }
}
