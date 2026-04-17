using UnityEngine;

public class Billboard : MonoBehaviour
{
    void OnEnable()
    {
        RaceManager.Billboards.Add(this);
    }
    void OnDisable()
    {
        RaceManager.Billboards.Remove(this);
    }
}