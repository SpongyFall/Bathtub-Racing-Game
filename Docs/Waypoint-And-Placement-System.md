# Waypoint And Placement System

## Scope

Race placement logic is split across:

- `Assets/Scripts/Game/RacerInfo.cs`
- `Assets/Scripts/Game/WaypointContainer.cs`
- `Assets/Scripts/Managers/RaceManager.cs`

This is one of the more important gameplay systems to understand because placement, lap completion, and race end all depend on it.

## Core model

Each racer tracks:

- completed laps
- current waypoint
- distance to next waypoint
- current segment length

From that, `RacerInfo` computes `RaceProgress`, which is used by `RaceManager` to sort all racers.

Current formula:

- completed laps contribute the largest chunk
- current waypoint index refines position within the lap
- distance-to-next-waypoint resolves who is further ahead between racers on the same segment

So the system is not ranking racers by world position. It ranks them by ordered progression along the intended track path.

## Waypoint progression model

The track logic is intentionally one-directional.

`RacerInfo` only considers movement from:

- current waypoint
- to the next waypoint in sequence

It does not search globally for the nearest waypoint to determine progress. That design prevents a racer from cutting across the track or driving backwards and accidentally skipping progression.

The waypoint only advances when the racer is close enough to the next waypoint and closer to it than to the current waypoint.

## Lap completion

The starting waypoint is assigned when the race starts, not before.

That matters because lap completion happens when a racer progresses back onto the starting waypoint after having moved through the course.

This avoids the common bug where a racer spawns on the start and is mistakenly credited with a lap immediately.

## Placement updates

`RaceManager.Update()` continuously re-sorts `RacerInfos` by `RaceProgress`.

UI placement comes from this sorted list:

- `RaceManager.GetRacerPlace(...)`
- `GameUI` reads the local player's `RacerInfo.RacerPlace`

So if placement looks wrong, the first things to inspect are:

- waypoint order
- starting waypoint assignment
- achieve-distance tuning
- current waypoint progression rules

## End-of-race dependency

Lap progression is also how race completion is detected.

When a racer rolls back onto the start waypoint:

- `CompletedLaps` increments
- `RaceManager.CheckEndConditions()` can end the race if lap target is reached

This means waypoint bugs can look like win-condition bugs even when the end-race code itself is fine.

## Multiplayer caveat

There is one especially important current implementation detail:

- `RacerInfo.IsControlledByMe` is hardcoded to `true`

The original apparent intent was owner-driven progression with synced waypoint state, but that sync path is disabled/commented out. As written now, every machine computes waypoint progression for every racer locally.

That can still appear to work, but it is a maintenance risk because:

- placement can diverge across machines
- race-end timing can diverge if transforms drift
- it hides ownership assumptions that the networking layer would normally enforce

If multiplayer race-state inconsistencies appear, this is one of the first systems to audit.
