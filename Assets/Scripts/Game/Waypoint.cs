using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Waypoint : MonoBehaviour
{
    [Tooltip("How close does a Kart need to be to complete this waypoint. Shown by the green circle around the waypoint.")]
    public float AchieveDistance = 7f;
    public WaypointContainer Container;

    public Vector3 position => transform.position;
    public Quaternion rotation => transform.rotation;

    void Reset()
    {
        //When script is added, try to find a parent container.
        Container = GetComponentInParent<WaypointContainer>();
        if (Container)
            AchieveDistance = Container.DefaultAchieveWaypointDistance;
    }

    public void OnDrawGizmosSelected()
    {
        if (!Container)
            return;

        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, AchieveDistance);
    }

    public void Set(WaypointContainer container)
    {
        Container = container;
    }
}
