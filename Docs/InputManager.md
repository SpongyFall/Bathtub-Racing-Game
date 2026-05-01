# InputManager

## Purpose

`Assets/Scripts/Managers/InputManager.cs` is the single shared bridge between Unity Input System actions and gameplay/UI scripts.

It owns static state for:

- `InputActions`
- `PlayerMoveInput`
- `PlayerLookInput`
- `BoostAction`
- `DriftAction`
- `MousePosition`
- `IsUsingController`

Most gameplay code reads from these static fields rather than subscribing to input actions directly.

## Initialization

The important design choice is that input actions are initialized once in bootstrap and then exposed globally through static fields.

That gives the project one shared input state for both gameplay and UI logic, but it also means any script that reads `InputManager.InputActions` before ordered bootstrap has run is unsafe.

## Update loop

Every frame it:

- Reads `Player.Move` into `PlayerMoveInput`
- Reads `Player.Look` into `PlayerLookInput`
- Tracks the `EventSystem.currentSelectedGameObject` as `LastSelectedObj`
- Sets `MousePosition`

`MousePosition` has one non-obvious rule:

- If using controller and a UI object is selected, it uses the selected object's transform position.
- Otherwise it uses the real mouse position.

That is why menu raycasts can still work with controller navigation.

## EventSystem and controller re-entry

This system exists because Unity UI selection behaves differently for mouse and controller users.

Relevant EventSystem behavior:

- clicking UI often changes the selected object
- controller navigation depends on there being a selected object
- if selection is lost, controller users can get "stuck" until something is selected again

This project handles that by caching two references:

- `FirstSelectedObjThisScene`: the scene's intended default controller target
- `LastSelectedObj`: the last actively selected UI object

On scene load, the scene's `firstSelectedGameObject` is captured and then cleared so `InputManager` can decide when selection should actually happen.

When the player uses a controller again after using mouse/keyboard:

- controller mode is restored
- if no valid object is currently selected
- the system reselects either the last valid selected object or the scene's default first object

That is the "controller jump back in" behavior. It is not just convenience code, it is what lets controller navigation recover after mouse interaction or after selection was lost because a panel changed.

## Device mode switching

The project does not use a dedicated device manager. It flips modes heuristically:

- Pressing gamepad south button this frame sets `IsUsingController = true`
- Clicking left mouse or pressing any keyboard key sets `IsUsingController = false`

This behavior affects UI selection immediately.

## Scene load behavior

On every scene load:

- `LastSelectedObj` is cleared
- The current `EventSystem.firstSelectedGameObject` is cached into `FirstSelectedObjThisScene`
- The EventSystem's `firstSelectedGameObject` is then nulled out
- If currently in controller mode, the cached object is selected manually
- If not in controller mode, nothing is selected

This is why menu highlight behavior is scene-sensitive. If a scene is missing an `EventSystem`, or its `firstSelectedGameObject` was never configured, controller UI navigation will feel broken.

## Main consumers

- `PlayerKartController` reads `PlayerMoveInput`, `BoostAction`, and `DriftAction`
- `MainMenuManager` uses `MousePosition` for UI raycasts

If input mappings change in `Assets/InputActions.inputactions`, verify both gameplay and menu/controller navigation behavior.
