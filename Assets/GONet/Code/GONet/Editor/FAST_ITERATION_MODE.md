# Fast Iteration Mode

## Overview

Fast Iteration Mode is an **EXPERIMENTAL** feature that optimizes the edit-play cycle by changing when GONet generates and deletes runtime code.

**Normal behavior:** Code generates when entering Play Mode, deletes when exiting.

**Fast Iteration Mode:** Code generates once on editor startup, persists through play sessions, and deletes when the editor quits.

This can save 5-15 seconds per Play Mode cycle when making changes unrelated to GONet.

## One-Time Cost

Enabling this feature requires a full code regeneration that may take **1-3 minutes** depending on project size. This is a one-time cost that saves time on subsequent Play Mode cycles.

## Behavior Comparison

| Event | Normal Mode | Fast Iteration Mode |
|-------|-------------|---------------------|
| Editor Open | - | Generate code |
| Enter Play Mode | Generate code | Skip (use existing) |
| Exit Play Mode | Delete code | Skip deletion |
| Build Pre-process | Generate code | Generate code |
| Build Post-process | Delete code | Skip deletion |
| Build with errors | Delete code | Delete code (safety) |
| Editor Quit | - | Delete code |

## Risks

- **Compilation Deadlock:** Editing/removing GONet sync fields while enabled can cause stale generated code that blocks Play Mode entry.
- **Silent Data Corruption:** Stale union IDs can cause network data to deserialize to wrong fields with no runtime errors.
- **Team Issues:** Different developers may have different cached states.
- **Static State Persistence:** Because there is no domain reload between Play sessions, **static variables persist**. This affects singleton patterns, static flags, and other static state.

## Static State Persistence (Important!)

In Normal Mode, deleting and regenerating code causes a **domain reload** that resets all static variables. In Fast Iteration Mode, there is **no domain reload**, so static variables persist between Play sessions.

**Common issue:** Singleton patterns with `isQuitting` flags:
```csharp
public class MySingleton : MonoBehaviour
{
    private static bool _isQuitting = false;

    void OnApplicationQuit()
    {
        _isQuitting = true;  // Set during first play session exit
    }

    public static MySingleton Instance
    {
        get
        {
            if (_isQuitting) return null;  // Returns null in second play session!
            // ...
        }
    }
}
```

**Solution:** Reset static flags when play mode starts:
```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
static void ResetStaticState()
{
    _isQuitting = false;
}
```

This is the same issue that occurs with Unity's built-in "Enter Play Mode Options > Disable Domain Reload" feature.

## Recovery

If you encounter issues:

1. Use **"Fix GONet Generated Code"** button in GONet Editor Support window, OR
2. Manually delete all files in `Assets/GONet/Code/GONet/Generated/` folder, OR
3. Disable Fast Iteration Mode (triggers fresh regeneration)

## Recommendation

Only enable when making frequent non-GONet changes. Disable before making ANY changes to `[GONetAutoMagicalSync]` fields or GONetParticipant prefabs.
