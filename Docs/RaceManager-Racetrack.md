# RaceManager In Racetrack

## Scope

`Assets/Scripts/Managers/RaceManager.cs` is the race orchestrator for `Assets/Scenes/Racetrack.unity`.

It coordinates:

- player kart spawn timing
- ready handshake
- authority-to-Steam mapping
- race start
- AI spawn
- racer sorting
- end conditions
- leave-game behavior

It works closely with:

- `KartSpawnManager`
- `NetworkedKart`
- `RacerInfo`
- `CountdownManager`
- `GameUI`

## Startup sequence

When GONet marks the `RaceManager` participant ready:

1. `RaceManager.OnGONetReady()` starts `SpawnKartNextFrame()`
2. one frame later `KartSpawnManager.SpawnKart()` runs
3. each player spawns exactly one `PlayerKartPrefab`
4. `WaitForKartSpawns()` waits until every human kart has `SetupComplete == true`
5. local `ClientKart` sends `RPCReady()`
6. every machine receives `NetworkedKart.Ready(...)`
7. host waits for all players or a timeout
8. host starts the race with `StartRace(laps, aiCount)`

The one-frame delay before spawning is intentional. It gives the scene/GONet setup one frame to settle before the kart objects appear.

## Human spawn slots

`KartSpawnManager` derives human spawn slots from `SteamManager.GetLobbyPlayerIds()`.

Rules:

- offline: local player always spawns in slot 0
- multiplayer: local player spawns at its index in the sorted Steam ID list
- AI fills remaining slots later

That means slot assignment is deterministic across all machines as long as `GetLobbyPlayerIds()` remains sorted the same way.

## Ready handshake

This is the critical anti-race-condition system for player karts.

Why it exists:

- If a kart sends RPCs before peers have spawned that kart, the RPC can be dropped.

Current handshake:

1. each `NetworkedKart` runs `Setup()` in `OnGONetReady()`
2. owner marks `SetupComplete = true`
3. `RaceManager` waits until the count of setup-complete human karts reaches lobby player count
4. owner sends `RPCReady()`
5. `Ready()` broadcasts:
   - owner Steam ID
   - serialized `CustomKartData`
6. `RaceManager.KartReady()` records that kart in `readyKarts`

The host waits up to 10 seconds for all players to appear in `readyKarts`.

If a player is not ready in time:

- host tries to map their Steam ID to GONet authority ID
- if mapping exists, `NetworkManager.KickGONetPlayer()` is used
- otherwise Steam-only kick signaling is used

## Authority mapping

The project needs both Steam IDs and GONet authority IDs.

`NetworkedKart.Ready()` links them by calling:

- `RaceManager.LinkAuthToSteamId(GONetParticipant.OwnerAuthorityId, steamId)`

Those dictionaries are later used for:

- kick/disconnect handling
- winner name lookup

## Race start

Only the host starts the race.

Host behavior:

- reads lap count and AI count from `TrackSelectionManager` static getters
- sends `StartRace` RPC to everyone

`StartRace()` on all machines:

- sets `RaceStarted = true`
- sets `RaceActive = true`
- stores `TotalLaps`
- calls `KartSpawnManager.SpawnAIKarts(aiCount)`
- starts countdown

Only the server actually spawns AI because `SpawnAIKarts()` returns immediately on non-server machines.

## Racer sorting and win detection

`RaceManager.Update()` continuously sorts `RacerInfos` by `RaceProgress`.

`RaceProgress` is computed by `RacerInfo` from:

- completed laps
- current waypoint index
- distance to next waypoint

End conditions are checked only on server and only while `RaceActive`:

- any racer reaches `CompletedLaps >= TotalLaps`
- or racer count drops to 1 or 0

The end result is broadcast with `EndRace(winnerGONetId)`.

## Waypoint system note

`RacerInfo` owns waypoint progression. A few maintenance notes matter:

- starting waypoint is assigned only when `RaceManager.OnRaceStart` fires
- lap completion happens when progression returns to the starting waypoint
- the code only tests the next waypoint, which prevents skipping backwards through the course

Non-obvious current state:

- `RacerInfo.IsControlledByMe` is hardcoded to `true`

So waypoint progression is currently computed locally for every racer on every machine, not just for the owning player. If race-position bugs appear in multiplayer, inspect that first.

## Leave behavior

`RaceManager.LeaveGame(bool stayInSteamLobby)` splits leaving into two modes:

- `stayInSteamLobby = true`: disconnect GONet only, used by the race-end main-menu button
- `stayInSteamLobby = false`: disconnect GONet and leave the Steam lobby
