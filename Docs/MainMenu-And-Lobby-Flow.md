# Main Menu And Lobby Flow

## Scope

These UI flows are mostly controlled by:

- `Assets/Scripts/Managers/MainMenuManager.cs`
- `Assets/Scripts/Menus & Transition Screens/LobbyUI.cs`
- `Assets/Scripts/Managers/SteamManager.cs`
- `Assets/Scripts/Menus & Transition Screens/TrackSelection.cs`

This is worth documenting because the UI is likely to change, but the current flow has non-obvious coupling to input mode, Steam lobby state, and panel stack behavior.

## Main menu structure

`MainMenuManager` behaves like a lightweight panel-state controller rather than a formal state machine.

The main panels are:

- main menu panel
- multiplayer panel
- lobby UI
- track selection
- joining-random overlay

It keeps an `ActivePanels` stack and uses `ShowPanel(...)` to:

- show a new panel
- hide the previously active one unless it is acting as the parent panel
- restore the previous panel when a child closes

This is effectively a manual UI navigation stack.

## High-level user flows

Current intended flows:

- Singleplayer:
  main menu -> track selection -> start game -> GONet offline host -> `Racetrack`
- Host multiplayer:
  main menu -> multiplayer panel -> create Steam lobby -> lobby UI -> track selection -> host starts game
- Join by code:
  main menu -> multiplayer panel -> join Steam lobby -> lobby UI -> wait for host start
- Join random:
  main menu -> start random search -> Steam lobby join when one is found -> lobby UI
- Customization:
  main menu -> `KartCustomization` scene

## Lobby UI ownership

The lobby screen is event-driven off `SteamManager`.

`LobbyUI` reacts to:

- lobby entered
- player joined/left
- lobby disconnected
- persona updates
- avatar loaded
- lobby chat messages

The UI itself does not own lobby truth. It rebuilds itself from Steam state whenever those events arrive.

That means if the next team redesigns the lobby screen, the safest approach is to preserve the event-driven model and avoid duplicating Steam state in UI-local variables.

## Host start path

The Start button in `LobbyUI` does not start networking directly.

Current flow:

1. host enters Steam lobby
2. lobby UI shows Start button only for the owner
3. Start opens track selection
4. track selection writes lap count and AI count to `PlayerPrefs`
5. `NetworkManager.StartGame()` publishes Steam lobby metadata and starts GONet

The important split is:

- Steam lobby decides who is grouped together
- track selection stores host-selected race settings locally
- `NetworkManager` converts lobby state into the actual game session

## Join and random-join flow

Join by code:

- user enters lobby ID
- `SteamManager.JoinLobby(...)` is called
- once Steam confirms entry, `LobbyUI.ShowCurrentLobby()` rebuilds the screen

Random join:

- `SteamManager.StartJoiningRandomLobby()` starts polling for lobbies
- `MainMenuManager` shows a searching overlay while that coroutine exists
- if a lobby is found, one is chosen randomly and joined

## Controller/mouse interaction

The main menu uses both EventSystem selection and manual graphic raycasts.

Important coupling:

- `MainMenuManager` raycasts using `InputManager.MousePosition`
- `InputManager.MousePosition` can be either real mouse position or selected UI object position, depending on control mode

This means controller-driven UI still participates in logic that was originally written around pointer raycasts.

If the UI is redesigned, preserve this relationship or replace it deliberately. Otherwise controller behavior can degrade in subtle ways even if the buttons still look correct.

## Back/cancel behavior

`MainMenuManager` implements `ICancelHandler`.

Current back behavior:

- if multiplayer panel is open, close it
- else if lobby UI is open, trigger Leave Lobby

That behavior is part of controller usability and should be re-evaluated if panel hierarchy changes.

## Copy code behavior

The lobby code button has a custom hover-sensitive display behavior:

- clicking copies the lobby ID
- while the pointer remains over the button, the text changes to show the copied code
- when hover ends, text resets

This is minor, but it is one of the spots where menu behavior depends on the custom pointer/controller hybrid input system.

## Chat behavior

Lobby chat is thin by design:

- local send goes through Steam lobby chat
- received messages, including your own, are appended from callback events
- join/leave messages are synthetic UI messages, not Steam-authored chat content

If chat is redesigned, keep in mind that the visible chat feed currently contains both real chat and local system messages.
