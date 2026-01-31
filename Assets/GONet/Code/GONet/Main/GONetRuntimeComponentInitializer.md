# GONetRuntimeComponentInitializer

## Quick Start Guide

**GONetRuntimeComponentInitializer** allows you to dynamically add `GONetParticipantCompanionBehaviour` components (like chat systems, custom RPC handlers, etc.) to the persistent GONetGlobal singleton when your scene loads.

---

## Why Do I Need This?

GONet uses a singleton pattern for `GONetGlobal` - the first instance persists across all scenes via DontDestroyOnLoad, and duplicate instances in other scenes are automatically destroyed. This means you **CANNOT** add scene-specific components directly to GONetGlobal in the scene hierarchy (they'll be destroyed with the duplicate).

`GONetRuntimeComponentInitializer` solves this by adding components to the persistent GONetGlobal at runtime, allowing each scene to have its own initialization logic without conflicts.

---

## Common Use Cases

✅ Adding RPC-based systems (like `GONetSampleChatSystem`) that need RPCs
✅ Scene-specific GONet functionality that should be added/removed with scene lifecycle
✅ Testing scenes standalone (each scene can have its own initializer)
✅ Modular feature management across multiple scenes

---

## How To Use

### 1. Create GameObject
Create an empty GameObject in your scene
*Example name:* `"ChatInitializer"` or `"ComponentInitializer"`

### 2. Add Component
Add the `GONetRuntimeComponentInitializer` component to it
*Component Menu:* `GONet > GONetRuntimeComponentInitializer`

### 3. Select Type
In the Inspector, use the **"Component Type"** dropdown to select which component to add
*Example:* `"GONet.Sample.GONetSampleChatSystem"`

### 4. Configure Lifecycle
Configure the **"Remove On Scene Unload"** option:
- ❌ **UNCHECKED (default)** = Component persists across all scenes
- ✅ **CHECKED** = Component is removed when this scene unloads

### 5. Done!
The component will be automatically added to GONetGlobal when the scene loads

---

## Example Scenarios

### Scenario 1: Persistent Chat System

**Goal:** Chat functionality available in all scenes after it's first loaded.

**Setup:**
- **Scene A (GONetSample):**
  - Add `GONetRuntimeComponentInitializer`
  - Select `"GONetSampleChatSystem"`
  - Remove On Scene Unload = `FALSE` ← Chat persists

- **Scene B (JustAnotherScene):**
  - Add `GONetRuntimeComponentInitializer`
  - Select `"GONetSampleChatSystem"`
  - Remove On Scene Unload = `FALSE` ← Reuses existing chat

**Result:** Chat added by Scene A, Scene B doesn't re-add it (already exists)

---

### Scenario 2: Scene-Specific Feature

**Goal:** Custom RPC system only in the battle arena scene.

**Setup:**
- **Battle Arena Scene:**
  - Add `GONetRuntimeComponentInitializer`
  - Select `"MyGame.BattleArenaRPCSystem"`
  - Remove On Scene Unload = `TRUE` ← Only exists in this scene

**Result:** System added when entering arena, removed when leaving

---

### Scenario 3: Multiple Scenes, Same Feature

**Goal:** Feature spans multiple gameplay scenes, but removed between scene groups.

**Setup:**
- **Menu Scenes (MainMenu, Options, etc.):**
  - No initializer (feature not needed)

- **Gameplay Scenes (Level1, Level2, Boss):**
  - **Level1:**
    - Add `GONetRuntimeComponentInitializer`
    - Select `"MyGame.GameplayRPCSystem"`
    - Remove On Scene Unload = `FALSE` ← Added here
  - **Level2:**
    - Add `GONetRuntimeComponentInitializer`
    - Select `"MyGame.GameplayRPCSystem"`
    - Remove On Scene Unload = `FALSE` ← Reused
  - **Boss:**
    - Add `GONetRuntimeComponentInitializer`
    - Select `"MyGame.GameplayRPCSystem"`
    - Remove On Scene Unload = `TRUE` ← Removed after boss fight

**Result:** Feature spans levels 1-2 and boss, removed when returning to menu

---

## Important Notes

⚠️ Only types extending `GONetParticipantCompanionBehaviour` are supported (required for RPC functionality)

⚠️ If a component already exists on GONetGlobal, the initializer will NOT claim ownership for removal (prevents scenes from interfering with each other)

⚠️ Multiple initializers across different scenes can safely target the same component type - only the first one to run will add it

⚠️ The selected component type must have a parameterless constructor (standard Unity requirement for AddComponent)

⚠️ This is a runtime system - components are added in `Start()`, not in the editor

---

## Troubleshooting

### Q: The dropdown shows "No types found"

**A:** You need to create custom classes that extend `GONetParticipantCompanionBehaviour`.

Example:
```csharp
public class MyCustomSystem : GONetParticipantCompanionBehaviour
{
    // Your RPC methods and logic here
}
```

---

### Q: My component isn't being added

**A:** Check the Unity Console for error messages from `GONetRuntimeComponentInitializer`.

Common issues:
- Component type no longer exists (renamed/deleted)
- Component type doesn't extend `GONetParticipantCompanionBehaviour`
- GONetGlobal doesn't exist in the scene

---

### Q: My component is being removed when I don't want it to be

**A:** Ensure "Remove On Scene Unload" is **UNCHECKED** (false). Also verify that another initializer in a different scene isn't removing it.

---

### Q: I see warnings about "already exists on GONetGlobal"

**A:** This is **normal**! It means another scene (or the GONetGlobal prefab) already added this component. The initializer will skip adding it again.

---

## Technical Details

- **Component type storage:** Stored as `AssemblyQualifiedName` for reliability
- **Type discovery:** Uses reflection at edit-time (no runtime cost)
- **Ownership tracking:** Only the initializer that added a component can remove it
- **Thread-safe:** Runs on Unity main thread during scene lifecycle

---

## For Developers

### Source Files

- **Runtime:** `Assets/GONet/Code/GONet/Main/GONetRuntimeComponentInitializer.cs`
- **Editor:** `Assets/GONet/Code/GONet/Editor/GONetRuntimeComponentInitializerEditor.cs`

Both files are heavily commented for easy understanding and modification.

---

## Need Help?

Refer to the extensive XML documentation in the source code for detailed technical explanations of every method and property.
