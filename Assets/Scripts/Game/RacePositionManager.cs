using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TMPro;
using System;


[Obsolete("Replaced by RaceManager, and GameUI.")]
// Determines and displays race position based on lap count, waypoint index, and remaining distance to next waypoint.
public class RacePositionManager : MonoBehaviour
{
    //[Space]
    //[Header("UI")]
    //[Tooltip("UI text displaying the player's current place.")]
    //public TMP_Text positionText;

    //// List of all racers currently participating
    //private List<RacerInfo> racers = new List<RacerInfo>();

    //void Start()
    //{
    //    DiscoverRacers();
    //}

    //void Update()
    //{
    //    SortRacers();
    //    UpdatePositionDisplay();
    //}

    //// Builds the racer list by pulling all RacerInfo components in the scene.
    //private void DiscoverRacers()
    //{
    //    racers = FindObjectsOfType<RacerInfo>().ToList();

    //    if (racers.Count == 0)
    //        Debug.LogError("RacePositionManager: No racers found! Ensure each kart has a RacerInfo component.");
    //}

    //public void RefreshRacers()
    //{
    //    DiscoverRacers();
    //    Debug.Log($"Racers refreshed. Count = {racers.Count}");
    //}


    //// Sorts racers from leading → last place based on race progression.
    //// Order: highest lap → highest waypoint → shortest distance to next waypoint.
    //private void SortRacers()
    //{
    //    racers = racers
    //        .OrderByDescending(r => r.CurrentLap)
    //        .ThenByDescending(r => r.CurrentWaypoint)
    //        .ThenBy(r => r.DistanceToNext)
    //        .ToList();
    //}

    ///// Updates the player’s on-screen race position text.
    //private void UpdatePositionDisplay()
    //{
    //    RacerInfo player = racers.FirstOrDefault(r => r.isClientPlayer);

    //    if (player == null)
    //    {
    //        positionText.text = $"Place: ? / {racers.Count}";
    //        //Debug.LogError("RacePositionManager: Player not found in racer list.");
    //        return;
    //    }

    //    int position = racers.IndexOf(player) + 1;
    //    positionText.text = $"Place: {position} / {racers.Count}";
    //}
}