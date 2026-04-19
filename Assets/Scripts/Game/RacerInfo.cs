using GONet;
using System;
using UnityEngine;

// Stores all race-related state for a racer (player or AI).
// Also connects to the assigned WaypointContainer for track logic.
public class RacerInfo : MonoBehaviour
{
    [Tooltip("Shall we log when our current waypoint progresses?")]
    public bool LogWaypointUpdates = false;
    [Space]
    [Header("Runtime Set Properties")]
    public bool IsClientPlayer = false;
    [Tooltip("Current completed laps. Increases when the player reaches the starting lap, after coming from another one.")]
    public int CompletedLaps = 0;
    [Tooltip("The current closest waypoint. Is progressive, and will only update when the player passes the next waypoint.")]
    public Waypoint CurrentWaypoint;
    [Tooltip("Kart owners update their current waypoint.")]
    //[GONetAutoMagicalSync] public int SyncedWaypointIndex = 0;
    public float DistanceToNextWaypoint = 0f;
    [Tooltip("The total dist from the current to the next waypoint.")]
    public float CurrentWaypointLength;

    [NonSerialized] public GONetParticipant Participant;

    [Tooltip("A value representing how far ahead we are in the race. Basically represents how many waypoints you've passed.")]
    public float RaceProgress => CompletedLaps * WaypointContainer.waypoints.Count + WaypointIndex + (1f - DistanceToNextWaypoint / CurrentWaypointLength);
    public int RacerPlace => RaceManager.Instance.GetRacerPlace(this);
    public int WaypointIndex => WaypointContainer != null ? WaypointContainer.waypoints.IndexOf(CurrentWaypoint) : -1;
    public WaypointContainer WaypointContainer => RaceManager.Instance ? RaceManager.Instance.WaypointContainer : null;
    //Set to true currently to allow all players to calcuate Waypoint updates, until kart owners are made to sync them.
    public bool IsControlledByMe => true;// Participant.IsLocallyControlled;

    public event Action<Waypoint, Waypoint> OnWaypointReached;

    void OnDrawGizmosSelected()
    {
        if (CurrentWaypoint != null && WaypointContainer)
        {
            var nextWaypoint = WaypointContainer.GetNextWaypoint(CurrentWaypoint);

            Gizmos.color = Color.green;
            Gizmos.DrawLine(transform.position, CurrentWaypoint.position);
            //Shows the next waypoint achieve radius.
            nextWaypoint.OnDrawGizmosSelected();
        }
    }

    void Awake()
    {
        Participant = GetComponent<GONetParticipant>();

        //// Load lap count chosen at track selection menu
        //totalLaps = PlayerPrefs.GetInt("SelectedLapCount", totalLaps);
    }

    void OnEnable()
    {
        RaceManager.OnRaceStart += RaceManager_OnRaceStart;

        RaceManager.Instance.AddRacerInfo(this, true);
    }
    void OnDisable()
    {
        RaceManager.OnRaceStart -= RaceManager_OnRaceStart;
        
        RaceManager.Instance.AddRacerInfo(this, false);
    }

    void Update()
    {
        if (WaypointContainer != null)
            UpdateCurrentWaypoint();
    }

    void RaceManager_OnRaceStart(RaceManager manager)
    {
        //Set our current to the starting waypoint.
        CurrentWaypoint = WaypointContainer.GetStartingWaypoint();
    }

    // Updates the current waypoint.
    // Works for both player and AI karts.
    void UpdateCurrentWaypoint()
    {
        //If we don't have a current waypoint, we can't check for the next, return.
        //Our starting waypoint should be manually set, if not could cause a lap increase bug.
        if (CurrentWaypoint == null)
            return;

        var waypoints = WaypointContainer.waypoints;

        //We only check the next waypoint, this way we can not go backwards and skip waypoints.
        //Check for distance, then check if we PASSED it by doing a dot product check.
        var newCurrent = CurrentWaypoint;
        var nextWaypoint = WaypointContainer.GetNextWaypoint(CurrentWaypoint);
        var racerPos = transform.position;

        //Only the controlling owner checks for waypoint progress.
        if (IsControlledByMe)
        {
            //Just check distance to the next waypoint, if we are close enough, update to it.
            var toNext = nextWaypoint.position - racerPos;
            var toCurrent = CurrentWaypoint.position - racerPos;
            //If we are within dist and also closer to the next than the current.
            if (toNext.magnitude < nextWaypoint.AchieveDistance && toNext.magnitude < toCurrent.magnitude)
                newCurrent = nextWaypoint;

            //Was using dot product to determine if we passed next waypoint, but had some bad edge cases.
            //var waypointDir = nextWaypoint.position - CurrentWaypoint.position;
            //float waypointSpacingSqrDist = waypointDir.sqrMagnitude;
            //var currentToRacer = racerPos - CurrentWaypoint.position;
            //var racerToNext = nextWaypoint.position - racerPos;

            //float racerDot = Vector3.Dot(waypointDir, currentToRacer);
            //float distToNextSqr = racerToNext.sqrMagnitude;
            //If racer dot < 0, we haven't reached passed the current waypoint yet (we probably already did).
            //If 0 < racer dot < spacing, we are between the current and next waypoint;
            //If racer dot > spacing, we are past the next waypoint, and should update to it.
            //Also, we need to be within a reasonable distance of the next waypoint, the directional checks do not account
            //for distance.
            //if (racerDot > waypointSpacingSqrDist && distToNextSqr < MaxUpdateWaypointDistance * MaxUpdateWaypointDistance)
            //    newCurrent = nextWaypoint;
            //if (IsClientPlayer)
            //{
            //    //Debug.Log($"Waypoint spacing dist: {waypointSpacingSqrDist}");
            //    //Debug.Log($"Our dot: {racerDot}");
            //}
        }
        else
        {
            //Non owners receive it via sync.
            //Sync was not working well (never has).
            //newCurrent = waypoints[SyncedWaypointIndex];
        }

        //Update current if changed.
        if (newCurrent != CurrentWaypoint)
        {
            var prev = CurrentWaypoint;
            SetCurrentWaypoint(newCurrent);
            if (LogWaypointUpdates)
                Debug.Log($"Racer: '{name}' reached next waypoint: {newCurrent}!");

            //If we reached the next waypoint, and it's the starting waypoint, we completed a lap.
            //Will not bug and increase at the start of the race, since we begin at start, and never progress to it until
            //we complete a lap.
            if (newCurrent == WaypointContainer.GetStartingWaypoint())
            {
                CompletedLaps++;
                Debug.Log($"Racer: '{name}' just completed {CompletedLaps} laps!");

                RaceManager.Instance.CheckEndConditions();
            }

            OnWaypointReached?.InvokeSafe(nameof(OnWaypointReached), prev, newCurrent);
        }

        nextWaypoint = WaypointContainer.GetNextWaypoint(CurrentWaypoint);
        DistanceToNextWaypoint = Vector3.Distance(nextWaypoint.position, racerPos);
        CurrentWaypointLength = (nextWaypoint.position - CurrentWaypoint.position).magnitude;
        if (IsClientPlayer)
        {
            //Debug.Log($"Distance to next waypoint: {DistanceToNextWaypoint}");
        }

        //Dist based search. Can allow you to go backwards and weird stuff, not good.
        //int bestIndex = -1;
        //float closestSqrDist = float.MaxValue;
        //for (int i = 0; i < waypoints.Count; i++)
        //{
        //    float sqrDist = (transform.position - waypoints[i].position).sqrMagnitude;

        //    if (sqrDist < closestSqrDist)
        //    {
        //        closestSqrDist = sqrDist;
        //        bestIndex = i;
        //    }
        //}
    }
    public void SetCurrentWaypoint(Waypoint current)
    {
        CurrentWaypoint = current;
        //If we are the owner, update the synced index so other racers can see our progress.
        //if (IsControlledByMe)
        //    SyncedWaypointIndex = WaypointContainer.waypoints.IndexOf(CurrentWaypoint);
    }
}