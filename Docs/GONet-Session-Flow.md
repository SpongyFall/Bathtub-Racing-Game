# GONet Session Flow

## Scope

`Assets/Scripts/Managers/NetworkManager.cs` is the bridge between Steam lobby state and the actual in-race GONet session.

Steam gets players into the same lobby.
GONet gets them into the same race scene.

## Host start flow

When the host presses Start:

1. `NetworkManager.StartGame()` runs.
2. If in a Steam lobby, it writes:
   - `hostSteamId = local Steam ID`
   - `state = "starting"`
3. It builds a runtime `GONetConnectionPreset`.
4. It connects as host.

Offline singleplayer uses the same path, but with `offline = true`, which forces host mode and disables Steam transport.

## Client join flow

Clients do not join GONet until Steam lobby metadata changes.

Flow:

1. Steam callback raises `SteamManager.OnLobbyDataUpdate`
2. `NetworkManager.OnLobbyDataUpdate()` reads the lobby `state`
3. If `state == "starting"`, it reads `hostSteamId`
4. It builds a runtime `GONetConnectionPreset`
5. It connects as a client

Important implementation detail:

- For Steam transport, the preset's `ipAddress` is not an IP address.
- It stores the host Steam ID string.
- The Steamworks transport interprets that as the remote peer identity.

## Scene loading

After GONet reports connection success:

- only the server loads `Racetrack`
- it does so through `GONetMain.SceneManager.LoadSceneFromBuildSettings`
- GONet then propagates the scene load to clients

Do not replace that with local `SceneManager.LoadScene()` for multiplayer flow.

## Transport selection

`ConnectGONet()` decides transport at runtime:

- offline: no pluggable transport
- multiplayer: `GONetTransportType.Steamworks`

The project is therefore built around Steam P2P transport for multiplayer.

## Steam initialization dependency

Bootstrap includes a `GONetSteamManager`, and `SteamManager` also holds a reference to it. That manager is responsible for:

- `SteamAPI.Init()`
- `SteamAPI.RunCallbacks()`
- relay network access initialization
- Steam callback processing during heavy load

GONetSteamManager also injects callback pumping into Unity's PlayerLoop and additionally polls in `Update`, `FixedUpdate`, and `LateUpdate`.

This matters because scene loads and large instantiation spikes can otherwise delay Steam callback processing and inflate perceived network latency.

## Disconnect/reset path

`DisconnectGONet()` is the only safe general exit path from a live race session.

It does more than close sockets:

- tears down GONet client/server
- destroys GONet global objects
- returns to main menu if needed
- schedules `GONetMain.ResetForNewSession()` on the next `MainMenu` load

If a future system adds a custom "return to menu" flow, it should still funnel through this method.
