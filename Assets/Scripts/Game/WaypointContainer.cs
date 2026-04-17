using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

public class WaypointContainer : MonoBehaviour
{
    [Tooltip("How close does a Kart need to be to complete a waypoint.")]
    public float DefaultAchieveWaypointDistance = 7f;
    [Space]
    [Header("Runtime Set")]
    [Tooltip("Waypoints are dependent on their child ordering, and progression must be correlated with the ordering (1 can not come after 2).")]
    public List<Waypoint> waypoints;

    // Start is called before the first frame update
    void Awake()
    {
        waypoints = GetComponentsInChildren<Waypoint>().ToList();
        waypoints.ForEach(x => x.Set(this));
    }

    public Waypoint GetStartingWaypoint()
    {
        return waypoints[0];
    }

    public Waypoint GetClosestWaypoint(Vector3 fromPos)
    {
        Waypoint closestPoint = null;
        float minSqrDist = float.MaxValue;

        foreach (var point in waypoints)
        {
            float sqrDist = (point.position - fromPos).sqrMagnitude;
            if (sqrDist < minSqrDist)
            {
                closestPoint = point;
                minSqrDist = sqrDist;
            }
        }

        return closestPoint;
    }

    public Waypoint GetNextWaypoint(Waypoint current)
    {
        int currentIndex = waypoints.IndexOf(current);
        return GetNextWaypoint(currentIndex);
    }
    public Waypoint GetNextWaypoint(int currentIndex)
    {
        //Progressive, will only return the next waypoint (loop from end to start).
        //Is depedant on waypoint ordering, especially children ordering.
        return waypoints[(currentIndex + 1) % waypoints.Count];
    }
}
