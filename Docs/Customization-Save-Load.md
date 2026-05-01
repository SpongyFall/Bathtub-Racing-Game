# Customization Save And Load

## Scope

Kart customization is a local save system plus a network broadcast during race join.

Main files:

- `Assets/Scripts/Game/CustomKartData.cs`
- `Assets/Scripts/Game/KartSaveManager.cs`
- `Assets/Scripts/Game/KartModel.cs`
- `Assets/Scripts/Menus & Transition Screens/SelectCustomizations.cs`
- `Assets/Scripts/Game/NetworkedKart.cs`

## Data model

`CustomKartData` stores:

- `RollCage`
- `Wheel`
- `ExtraDetail`
- `Decal`
- `MainColor`
- `TrimColor`
- `DecalColor`

It is a single-cart payload. The project does not currently support multiple saved builds or cloud sync.

## Local persistence

`KartSaveManager` writes one JSON file:

- `Path.Combine(Application.persistentDataPath, "CustomKart.json")`

Behavior:

- on bootstrap init, if the file does not exist, it writes a default `CustomKartData`
- `SaveKartData()` serializes with `JsonUtility.ToJson(..., true)`
- `LoadKartData()` returns a new default object if file read/deserialization fails

This system is intentionally simple. There is no schema migration layer.

## Customization scene flow

`SelectCustomizations` is the UI-side controller in `KartCustomization`.

On `Awake()`:

- dropdowns are rebuilt from enum values
- each dropdown is wired directly to a `KartModel` setter

On `OnEnable()`:

- local JSON is loaded
- `KartModel.ApplyKartData()` applies it
- dropdown values are updated without firing listeners

On `OnDisable()`:

- current `KartModel.KartData` is saved back to disk

So the editor scene is not using an explicit Save button for the main persistence path. Leaving/disabling the customization UI saves.

Color selection is scene-wired through UI button `OnClick` events on the customization controller object.

Each color button calls one of:

- `SelectMainColor`
- `SelectTrimColor`
- `SelectDecalColor`

The button passes a hex color string argument, which `SelectCustomizations.ParseInputColor(...)` converts into a `Color` before applying it to the `KartModel`.

## Runtime application

`KartModel` is the actual renderer-side application layer.

It applies customization by:

- enabling exactly one roll cage object
- enabling exactly one wheel set
- enabling exactly one extra detail object
- swapping decal materials
- recoloring body, trim, extra-detail renderers, and decals

`KartData` is kept as mutable runtime state on the component.

## Network handoff into race

In multiplayer, cosmetics are not loaded from remote disk.

Current race flow:

1. local owner `NetworkedKart.Setup()` loads `KartSaveManager.LoadKartData()`
2. local owner applies it to their kart
3. owner calls `RPCReady()`
4. `RPCReady()` serializes `KartModel.KartData`
5. `Ready()` runs on all machines
6. every machine deserializes that `CustomKartData`
7. every machine applies it to the matching kart

That means the cosmetic state used in a race comes from the owner machine at join time, not from any shared profile store.

## Related non-cosmetic saved settings

Track setup uses `PlayerPrefs`, not the JSON file:

- lap count key: `LapCount`
- AI count key: `AICount`

Those values are read by the host when starting the race.

Maintenance note:

- `TrackSelectionManager.MinAICount` allows `0` in some multiplayer cases
- but `OnStartClick()` currently clamps the saved AI count to at least `1`

If zero-AI multiplayer should be supported end-to-end, that clamp is one of the first places to inspect.
