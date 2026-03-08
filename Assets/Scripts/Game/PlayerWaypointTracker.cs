using GONet;
using UnityEngine;

[RequireComponent(typeof(RacerInfo))]
public class PlayerWaypointTracker : MonoBehaviour
{
    private GONetParticipant gonetParticipant;
    private RacerInfo racerInfo;
    private WaypointContainer waypointContainer;

    void Start()
    {
        gonetParticipant = GetComponent<GONetParticipant>();
        racerInfo = GetComponent<RacerInfo>();
        waypointContainer = FindFirstObjectByType<WaypointContainer>();

        racerInfo.totalLaps = PlayerPrefs.GetInt("SelectedLapCount", 3);
    }

    void FixedUpdate()
    {
        // Only the owning client should write waypoint data; synced values handle remote karts.
        if (gonetParticipant != null && !gonetParticipant.IsLocallyControlled) return;

        if (waypointContainer == null || racerInfo == null)
            return;

        var waypoints = waypointContainer.waypoints;

        float minDist = float.MaxValue;
        int closest = 0;

        // Find closest waypoint
        for (int i = 0; i < waypoints.Count; i++)
        {
            float d = Vector3.Distance(transform.position, waypoints[i].position);
            if (d < minDist)
            {
                minDist = d;
                closest = i;
            }
        }

        racerInfo.currentWaypoint = closest;
        racerInfo.distanceToNext = minDist;
    }
}