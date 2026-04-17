using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WaypointContainer : MonoBehaviour
{
    [Tooltip("Waypoints are dependent on their child ordering, and progression must be correlated with the ordering (1 can not come after 2).")]
    public List<Transform> waypoints;

    // Start is called before the first frame update
    void Awake()
    {
        foreach (Transform tr in gameObject.GetComponentInChildren<Transform>())
            waypoints.Add(tr);
    }

    public Transform GetStartingWaypoint()
    {
        return waypoints[0];
    }

    public Transform GetClosestWaypoint(Vector3 fromPos)
    {
        Transform closestPoint = null;
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

    public Transform GetNextWaypoint(Transform current)
    {
        int currentIndex = waypoints.IndexOf(current);
        return GetNextWaypoint(currentIndex);
    }
    public Transform GetNextWaypoint(int currentIndex)
    {
        //Progressive, will only return the next waypoint (loop from end to start).
        //Is depedant on waypoint ordering, especially children ordering.
        return waypoints[(currentIndex + 1) % waypoints.Count];
    }
}
