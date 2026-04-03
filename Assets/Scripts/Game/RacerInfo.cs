using GONet;
using UnityEngine;

// Stores all race-related state for a racer (player or AI).
// Also connects to the assigned WaypointContainer for track logic.
public class RacerInfo : MonoBehaviour
{
    [Header("Identity")]
    public string racerName = "Racer";
    public bool isClientPlayer = false;

    [Header("Race Progress")]
    [Tooltip("Current completed laps. 0-based index.")]
    public int currentLap = 0;

    [Tooltip("Total number of laps in this race.")]
    public int totalLaps = 3;

    [Tooltip("Current active waypoint index.")]
    public int currentWaypoint = 0;

    [Tooltip("Distance to the upcoming waypoint.")]
    public float distanceToNext = 0f;

    public bool hasFinished = false;


    // Waypoint container controlling track layout. (AI overrides this if needed.)
    public WaypointContainer waypointContainer { get; private set; }

    private GONetParticipant gonetParticipant;
    private OpponentKartAI ai;
    private Transform tr;

    void Awake()
    {
        gonetParticipant = GetComponent<GONetParticipant>();
        tr = transform;
        ai = GetComponent<OpponentKartAI>();

        // Auto-grab existing waypoint container in scene
        waypointContainer = FindFirstObjectByType<WaypointContainer>();

        // Load lap count chosen at track selection menu
        totalLaps = PlayerPrefs.GetInt("SelectedLapCount", totalLaps);

        // AI overrides waypointContainer if it uses a specific path
        if (ai != null)
            waypointContainer = ai.waypointContainer;

        if (waypointContainer == null)
            Debug.LogError("RacerInfo: No WaypointContainer found in the scene.");
    }

    void Update()
    {
        // Only the owner writes waypoint progress; remote clients receive it via sync.
        if (gonetParticipant != null && !gonetParticipant.IsLocallyControlled) return;

        if (!HasValidWaypoints())
            return;

        UpdateWaypointProgress();
    }


    // Ensures the track waypoint list is valid before processing.
    private bool HasValidWaypoints()
    {
        return waypointContainer != null &&
               waypointContainer.waypoints != null &&
               waypointContainer.waypoints.Count > 0;
    }


    // Updates the current waypoint index and distance.
    // AI uses its own internal waypoint index; player uses nearest waypoint detection.

    private void UpdateWaypointProgress()
    {
        var list = waypointContainer.waypoints;

        // AI progression index
        if (ai != null)
        {
            currentWaypoint = Mathf.Clamp(ai.currentWaypoint, 0, list.Count - 1);
            distanceToNext = Vector3.Distance(tr.position, list[currentWaypoint].position);
            return;
        }

        // Player: find nearest waypoint
        int bestIndex = 0;
        float closestDist = float.MaxValue;

        for (int i = 0; i < list.Count; i++)
        {
            float sqrDist = (tr.position - list[i].position).sqrMagnitude;

            if (sqrDist < closestDist)
            {
                closestDist = sqrDist;
                bestIndex = i;
            }
        }

        currentWaypoint = bestIndex;
        distanceToNext = Mathf.Sqrt(closestDist);
    }
}