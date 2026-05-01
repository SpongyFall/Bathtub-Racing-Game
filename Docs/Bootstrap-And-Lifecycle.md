# Bootstrap And Lifecycle

## Purpose

The game starts in `Assets/Scenes/Bootstrap.unity`. Bootstrap exists to create persistent managers once, then immediately load `MainMenu`.

The main persistent object is `Static Scripts` in `Bootstrap`. It contains:

- `OrderedScriptManager`
- `DontDestroyOnLoad`
- `GONetSteamManager`
- `SteamManager`
- `KartSaveManager`
- `NetworkManager`
- `InputManager`
- `SceneLoader`
- `GONetConnectionManager`

Because of `DontDestroyOnLoad`, these managers survive scene changes.

## Ordered initialization

Project startup order is not plain Unity `Awake`/`Start`. `OrderedScriptManager` manually calls `OrderedAwake()` and `OrderedStart()` on every `IOrderedScript` it finds.

Implementation details:

- It scans itself and `OrderedObjs`.
- It sorts by `CallOrder` descending.
- Scripts with higher `CallOrder` run first.

Current implications:

- `NetworkManager` and `KartSaveManager` use `CallOrder = 1`.
- `SteamManager`, `InputManager`, and `SceneLoader` use `CallOrder = 0`.
- So network/save initialization happens before input/scene-loader initialization.

If a new persistent manager depends on another one being ready, set `CallOrder` deliberately. Do not assume Unity component order is enough.

## Scene flow

`SceneLoader.OrderedStart()` immediately loads `SceneType.MainMenu`.

`SceneLoader` also:

- Converts loaded scene names into the `SceneType` enum.
- Stores the result in `SceneLoader.CurrentSceneType`.
- Raises `SceneLoader.OnSceneLoaded`.

Several systems depend on that event, especially `InputManager` and `NetworkManager`.

## Session reset behavior

Leaving a GONet game does not just disconnect sockets. `NetworkManager.DisconnectGONet()` also:

- Marks `pendingSessionReset = true`
- Disconnects/stops GONet client/server
- Destroys `GONetMain.Global`
- Destroys `GONetMain.MyLocal`
- Destroys `GONetStatusUI` if present
- Returns to `MainMenu` if currently in `Racetrack`

After the main menu loads, `NetworkManager.OnSceneLoaded()` calls `GONetMain.ResetForNewSession()` if `pendingSessionReset` is set.

That reset step is important. If it is skipped, stale GONet state can leak into the next session.
