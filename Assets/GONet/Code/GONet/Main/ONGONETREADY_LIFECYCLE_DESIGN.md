# OnGONetReady Lifecycle Design & Implementation

**Document Purpose:** Define the lifecycle guarantees, implementation strategy, and edge cases for `OnGONetReady()` - GONet's unified initialization hook.

**Last Updated:** October 9, 2025
**Status:** Design Phase - Implementing lifecycle gate system

---

## Executive Summary

### **The Problem**

OnGONetReady is currently called **before Unity's Awake()** completes for dynamically spawned objects, violating user expectations that "ready" means "fully initialized."

**Evidence from logs:**
```
Line 3808: [Server] OnGONetReady() called for 'CannonBall(Clone)' (GONetId: 4095) at 11:40:53.233
Line 3837: [Server] Awake() - 'CannonBall(Clone)' at 11:40:53.234 (1ms later)
```

### **The Solution**

Extend the existing `GONetMain.IsGONetReady()` method to check Unity lifecycle completion in addition to existing GONet prerequisites. A simple gate check function calls OnGONetReady when all conditions are met.

### **Final Guarantees (Locked In)**

When `OnGONetReady(GONetParticipant participant)` is called, the following are **guaranteed**:

1. ✅ **Awake() has completed** for the participant's GameObject
2. ✅ **OnEnable() has completed** for the participant's GameObject
3. ✅ **Start() has completed** for the participant's GameObject
4. ✅ **GONetId is assigned** (non-zero)
5. ✅ **OwnerAuthorityId is assigned** (non-zero)
6. ✅ **DeserializeInitAllCompleted has occurred** (IF the participant requires remote sync data)
7. ✅ **NOT in limbo state** (if limbo system is in use)

### **Non-Guarantees (Important)**

❌ **Update() has NOT been called** - We **cannot guarantee** this for network-dependent initialization
- Reason: Remote spawns may take multiple frames to receive sync data
- Example: Object spawns → Awake → Start → Update(frame 1) → Update(frame 2) → Network data arrives → OnGONetReady
- **User impact:** Your `Update()` logic should check `IsGONetReady` flag if it depends on networking

---

## Global Broadcast Once Guarantee

### **The Guarantee**

For every `(GONetParticipant, GONetBehaviour)` pair in the system, `OnGONetReady(GONetParticipant)` will be called **exactly once** - no more, no less.

This guarantee applies to:
- ✅ Individual participant's own behaviours (components on the same GameObject)
- ✅ ALL system behaviours (every GONetBehaviour receives OnGONetReady for every GONetParticipant)
- ✅ Runtime-added behaviours (via GONetRuntimeComponentInitializer) - they catch up on existing participants
- ✅ Scene-defined behaviours (present at design-time)
- ✅ Companion behaviours (extending GONetParticipantCompanionBehaviour)

### **How It Works: Two-Way Synchronization**

The system uses a **bidirectional broadcast mechanism** to ensure exactly-once delivery:

#### **1. Participant Becomes Ready → Broadcast to All Behaviours**

When a participant satisfies all prerequisites (Awake, Start, DeserializeInit, etc.), `CheckAndPublishOnGONetReady_IfAllConditionsMet()` broadcasts OnGONetReady to **every registered GONetBehaviour in the system**:

```csharp
// GONet.cs:8601 - CheckAndPublishOnGONetReady_IfAllConditionsMet()
internal static void CheckAndPublishOnGONetReady_IfAllConditionsMet(GONetParticipant participant)
{
    // Check all prerequisites (delegates to IsGONetReady)
    if (!IsGONetReady(participant))
    {
        return; // Not ready yet
    }

    // Prevent duplicate calls - OnGONetReady should only fire once per participant
    if (participant.didOnGONetReadyFire)
    {
        return; // Already fired
    }

    // Mark as fired BEFORE calling callbacks (prevent re-entrance)
    participant.didOnGONetReadyFire = true;

    // Broadcast OnGONetReady to ALL registered GONetBehaviours
    using (var en = allGONetBehaviours.GetEnumerator())
    {
        while (en.MoveNext())
        {
            GONetBehaviour gnBehaviour = en.Current;
            try
            {
                gnBehaviour.OnGONetReady(participant); // Broadcast to EVERY behaviour
            }
            catch (Exception ex)
            {
                GONetLog.Error($"Exception in OnGONetReady() broadcast: {ex.Message}");
            }
        }
    }
}
```

**Key insight:** This broadcasts to `allGONetBehaviours` HashSet - a system-wide registry of ALL GONetBehaviours, not just behaviours on the participant's GameObject.

#### **2. New Behaviour Added → Catch Up on All Ready Participants**

When a GONetBehaviour is added at runtime (via GONetRuntimeComponentInitializer or dynamically), its `Start()` method checks if participants are already ready and "catches up" on any missed OnGONetReady calls:

```csharp
// GONetBehaviour.cs:236-275 - GONetParticipantCompanionBehaviour.Start()
protected override void Start()
{
    base.Start();

    // PATH 6: Catch-up mechanism for behaviours that start AFTER participants are already ready
    bool shouldCatchUp = WasAddedAtRuntime || GONetMain.gonetParticipantByGONetIdMap.Count > 0;

    if (shouldCatchUp)
    {
        // Call OnGONetReady for ALL ready participants, not just this component's participant
        foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
        {
            GONetParticipant participant = kvp.Value;
            if (GONetMain.IsGONetReady(participant))
            {
                try
                {
                    OnGONetReady(participant); // Catch up on ALL ready participants
                }
                catch (Exception ex)
                {
                    GONetLog.Error($"Exception in OnGONetReady() catch-up: {ex.Message}");
                }
            }
        }
    }
}
```

**Key insight:** Runtime-added behaviours iterate ALL existing participants and call OnGONetReady for any that are already ready, ensuring they don't miss the broadcast that happened before they were added.

### **Deduplication Mechanism**

The `didOnGONetReadyFire` flag (GONetParticipant.cs) prevents duplicate calls:

```csharp
// GONetParticipant.cs:302-357 - Lifecycle tracking region
[NonSerialized] internal bool didOnGONetReadyFire = false;
```

**How it works:**
1. Before broadcasting OnGONetReady, check `didOnGONetReadyFire`
2. If true, skip broadcast (already called)
3. If false, mark as true **BEFORE** calling callbacks (prevents re-entrance)
4. This flag is per-participant, ensuring each participant only triggers one broadcast

### **Registration Mechanism**

GONetBehaviours register themselves in `allGONetBehaviours` HashSet during Awake():

```csharp
// GONetBehaviour.cs:68 - Awake()
protected virtual void Awake()
{
    GONetMain.RegisterBehaviour(this);
}
```

**Why a HashSet?**
- Fast lookup for Contains() checks
- Automatic deduplication (same behaviour can't register twice)
- Thread-safe enumeration via `GetEnumerator()`

### **Timing Guarantees**

When OnGONetReady is called for a participant, **every GONetBehaviour in the system receives the call in the same frame**:

- ✅ **Scene-defined behaviours** - Already in `allGONetBehaviours` registry, receive broadcast immediately
- ✅ **Runtime-added behaviours (before participant ready)** - In registry when broadcast happens, receive call normally
- ✅ **Runtime-added behaviours (after participant ready)** - Catch up in their Start() method by iterating ready participants

**Special case: Runtime-added behaviour on newly-ready participant's GameObject**

If a behaviour is added to a participant in the **same frame** the participant becomes ready:

1. Participant becomes ready → Broadcast to all behaviours (new behaviour may not be in registry yet)
2. New behaviour's Awake() → Registers in `allGONetBehaviours`
3. New behaviour's Start() → Catch-up mechanism calls OnGONetReady for all ready participants (including its own)

**Result:** New behaviour receives OnGONetReady exactly once (via catch-up), even if it missed the initial broadcast.

### **Example Scenarios**

#### **Scenario 1: Normal Case (Behaviour exists before Participant ready)**

```
Timeline:
1. BehaviourA.Awake() → Registers in allGONetBehaviours
2. BehaviourB.Awake() → Registers in allGONetBehaviours
3. Participant.Awake() → Marks didAwakeComplete
4. Participant.Start() → Marks didStartComplete → CheckAndPublishOnGONetReady
5. CheckAndPublishOnGONetReady → Broadcasts to BehaviourA, BehaviourB
   - BehaviourA.OnGONetReady(participant) ✅ Called once
   - BehaviourB.OnGONetReady(participant) ✅ Called once
```

#### **Scenario 2: Runtime-Added Behaviour (Added after Participant ready)**

```
Timeline:
1. Participant.Awake() → Marks didAwakeComplete
2. Participant.Start() → Marks didStartComplete → CheckAndPublishOnGONetReady
3. CheckAndPublishOnGONetReady → Broadcasts to existing behaviours (BehaviourA not created yet)
4. [User adds BehaviourA via GONetRuntimeComponentInitializer]
5. BehaviourA.Awake() → Registers in allGONetBehaviours
6. BehaviourA.Start() → Catch-up mechanism iterates ready participants
   - Finds Participant is ready (IsGONetReady = true)
   - BehaviourA.OnGONetReady(participant) ✅ Called once (catch-up)
```

#### **Scenario 3: Multiple Participants, Multiple Behaviours**

```
Timeline:
1. BehaviourA.Awake() → Registers
2. Participant1.Awake() → Marks didAwakeComplete
3. Participant1.Start() → CheckAndPublishOnGONetReady
   - BehaviourA.OnGONetReady(Participant1) ✅ Called once
4. Participant2.Awake() → Marks didAwakeComplete
5. Participant2.Start() → CheckAndPublishOnGONetReady
   - BehaviourA.OnGONetReady(Participant2) ✅ Called once
6. [User adds BehaviourB]
7. BehaviourB.Awake() → Registers
8. BehaviourB.Start() → Catch-up mechanism
   - BehaviourB.OnGONetReady(Participant1) ✅ Called once (catch-up)
   - BehaviourB.OnGONetReady(Participant2) ✅ Called once (catch-up)

Result:
- BehaviourA received OnGONetReady for Participant1 and Participant2 (2 calls total)
- BehaviourB received OnGONetReady for Participant1 and Participant2 (2 calls total)
- Each (Participant, Behaviour) pair: exactly 1 call ✅
```

### **Verification in User Code**

Users can rely on this guarantee without tracking state themselves:

```csharp
public class MyNetworkSystem : GONetBehaviour
{
    // No need to track "already processed this participant" - OnGONetReady guaranteed once per participant
    public override void OnGONetReady(GONetParticipant participant)
    {
        base.OnGONetReady(participant);

        // This will be called exactly once for every participant in the system
        Debug.Log($"Participant '{participant.name}' (GONetId: {participant.GONetId}) is ready");

        // Safe to register participant in dictionaries without duplicate checks
        RegisterParticipant(participant);
    }
}
```

**No deduplication needed in user code** - the system guarantees OnGONetReady is called exactly once per participant.

---

## Lifecycle Path Matrix

Different spawn types have different initialization paths. The gate system must handle all of them.

### **Path 1: Scene-Defined Objects (Server/Authority)**

| Event | Timing | Notes |
|-------|--------|-------|
| GameObject exists in scene | Editor time | Placed in Unity scene |
| Awake() | Scene load | Unity lifecycle |
| OnEnable() | Scene load | Unity lifecycle |
| Start() | First frame | Unity lifecycle |
| GONetId assigned | After Start | Via auto-magical sync or manual assignment |
| **OnGONetReady fires** | After GONetId | All gates met |

**Prerequisites:**
- ✅ Awake complete
- ✅ OnEnable complete
- ✅ Start complete
- ✅ GONetId assigned
- ✅ OwnerAuthorityId assigned
- ❌ DeserializeInit NOT required (local authority, no remote sync needed)

---

### **Path 2: Scene-Defined Objects (Client/Non-Authority)**

| Event | Timing | Notes |
|-------|--------|-------|
| GameObject exists in scene | Editor time | Placed in Unity scene |
| Awake() | Scene load | Unity lifecycle |
| OnEnable() | Scene load | Unity lifecycle |
| Start() | First frame | Unity lifecycle |
| First network sync received | Network delay | From server |
| GONetId assigned | After sync | Via network message |
| DeserializeInitAllCompleted | After sync | Triggered by network data |
| **OnGONetReady fires** | After DeserializeInit | All gates met |

**Prerequisites:**
- ✅ Awake complete
- ✅ OnEnable complete
- ✅ Start complete
- ✅ GONetId assigned
- ✅ OwnerAuthorityId assigned
- ✅ DeserializeInit required AND complete (receiving remote sync)

---

### **Path 3: Runtime Spawn (Local Authority - IsMine)**

| Event | Timing | Notes |
|-------|--------|-------|
| Instantiate() called | Runtime | Client/Server spawns object |
| Awake() | Same frame | Unity lifecycle |
| OnEnable() | Same frame | Unity lifecycle |
| GONetId assigned | Same frame | From batch or server assignment |
| Start() | Next frame | Unity lifecycle (delayed 1 frame) |
| **OnGONetReady fires** | After Start | All gates met |

**Current behavior (BROKEN):**
- ❌ OnGONetReady fires immediately after GONetId assigned
- ❌ Start() hasn't run yet → Race condition

**New behavior (FIXED with gate system):**
- ✅ OnGONetReady waits for Start() to complete
- ✅ No race condition

**Prerequisites:**
- ✅ Awake complete
- ✅ OnEnable complete
- ✅ Start complete
- ✅ GONetId assigned
- ✅ OwnerAuthorityId assigned
- ❌ DeserializeInit NOT required (local authority)

---

### **Path 4: Runtime Spawn (Remote - Received via Network)**

| Event | Timing | Notes |
|-------|--------|-------|
| InstantiateGONetParticipantEvent received | Network delay | From server/other client |
| Instantiate() called locally | Same frame as event | Creates local copy |
| Awake() | Same frame | Unity lifecycle |
| OnEnable() | Same frame | Unity lifecycle |
| Start() | Next frame | Unity lifecycle |
| **Update() starts running** | Frame after Start | **CRITICAL: May run before OnGONetReady!** |
| GONetId assigned | From spawn event | Already known from network message |
| DeserializeInitAllCompleted | After spawn event | Spawn event contains initial state |
| **OnGONetReady fires** | After DeserializeInit | All gates met |

**CRITICAL TIMING ISSUE:**
- ⚠️ Update() may run for several frames before OnGONetReady fires
- ⚠️ This is **unavoidable** for network-dependent initialization
- ✅ User workaround: Check `IsGONetReady` flag in Update() if needed

**Prerequisites:**
- ✅ Awake complete
- ✅ OnEnable complete
- ✅ Start complete
- ✅ GONetId assigned
- ✅ OwnerAuthorityId assigned
- ✅ DeserializeInit required AND complete (remote spawn)

---

### **Path 5: Limbo Mode 1 (InstantiateInLimboWithAutoDisableAll)**

**Behavior:** All MonoBehaviours disabled until batch arrives, then re-enabled.

| Event | Timing | Notes |
|-------|--------|-------|
| Instantiate() called | Runtime | Client spawn, batch exhausted |
| Awake() | Same frame | Unity lifecycle (runs even when disabled) |
| OnEnable() | **BLOCKED** | Component disabled during limbo |
| Start() | **BLOCKED** | Component disabled during limbo |
| Mark as in limbo | Same frame | `Client_IsInLimbo = true` |
| **Wait for batch from server** | Network delay | May be multiple frames |
| Batch arrives | Variable | Server sends new GONetId range |
| Exit limbo: Re-enable components | Same frame as batch | Components enabled |
| OnEnable() | Same frame | Now fires for re-enabled components |
| Start() | **Next frame** | Delayed 1 frame after enable |
| GONetId assigned | After batch | From new batch range |
| **OnGONetReady fires** | After Start | All gates met |

**Prerequisites:**
- ✅ Awake complete (ran during limbo)
- ✅ OnEnable complete (fired after re-enable)
- ✅ Start complete (fired after re-enable)
- ✅ GONetId assigned (from batch)
- ✅ OwnerAuthorityId assigned
- ❌ DeserializeInit NOT required (local authority)
- ✅ NOT in limbo (exited before OnGONetReady)

**Edge Case:** Start() is delayed by 1 frame after limbo exit (Unity behavior when enabling components).

---

### **Path 6: Limbo Mode 2 (InstantiateInLimboWithAutoDisableRenderingAndPhysics)**

**Behavior:** Only rendering/physics disabled, MonoBehaviours run normally.

| Event | Timing | Notes |
|-------|--------|-------|
| Instantiate() called | Runtime | Client spawn, batch exhausted |
| Awake() | Same frame | Unity lifecycle (normal) |
| OnEnable() | Same frame | Unity lifecycle (normal) |
| Start() | Next frame | Unity lifecycle (normal) |
| Mark as in limbo | After Start | `Client_IsInLimbo = true` |
| **Update() starts running** | Frame after Start | **Runs during limbo!** |
| Rendering/physics disabled | Same frame as limbo | Invisible/non-physical |
| **Wait for batch from server** | Network delay | Update() keeps running |
| Batch arrives | Variable | Server sends new GONetId range |
| Exit limbo: Re-enable rendering/physics | Same frame | Visible/physical again |
| GONetId assigned | After batch | From new batch range |
| **OnGONetReady fires** | After GONetId | All gates met |

**Prerequisites:**
- ✅ Awake complete
- ✅ OnEnable complete
- ✅ Start complete (ran during limbo)
- ✅ GONetId assigned (from batch)
- ✅ OwnerAuthorityId assigned
- ❌ DeserializeInit NOT required (local authority)
- ✅ NOT in limbo (exited before OnGONetReady)

**CRITICAL:** Update() runs **during limbo** - users must check `Client_IsInLimbo` if they need to skip logic.

---

### **Path 7: Limbo Mode 3 (InstantiateInLimbo - No Auto-Disable)**

**Behavior:** Nothing disabled, object runs completely normally during limbo.

| Event | Timing | Notes |
|-------|--------|-------|
| Instantiate() called | Runtime | Client spawn, batch exhausted |
| Awake() | Same frame | Unity lifecycle (normal) |
| OnEnable() | Same frame | Unity lifecycle (normal) |
| Start() | Next frame | Unity lifecycle (normal) |
| **Update() starts running** | Frame after Start | **Runs during limbo!** |
| Mark as in limbo | After Start | `Client_IsInLimbo = true` |
| **Wait for batch from server** | Network delay | Object fully functional, just no GONetId |
| Batch arrives | Variable | Server sends new GONetId range |
| Exit limbo | Same frame | `Client_IsInLimbo = false` |
| GONetId assigned | After batch | From new batch range |
| **OnGONetReady fires** | After GONetId | All gates met |

**Prerequisites:**
- ✅ Awake complete
- ✅ OnEnable complete
- ✅ Start complete (ran during limbo)
- ✅ GONetId assigned (from batch)
- ✅ OwnerAuthorityId assigned
- ❌ DeserializeInit NOT required (local authority)
- ✅ NOT in limbo (exited before OnGONetReady)

**CRITICAL:** User is responsible for checking `Client_IsInLimbo` in their scripts - advanced mode only.

---

## Implementation: Extending IsGONetReady()

### **Core Concept**

GONet already has `GONetMain.IsGONetReady(GONetParticipant)` that checks if a participant is ready for networking. We extend it to also check Unity lifecycle completion. A simple gate check function (`CheckAndPublishOnGONetReady_IfAllConditionsMet()`) calls `IsGONetReady()` and broadcasts OnGONetReady when it returns true.

### **Prerequisite Tracking (GONetParticipant.cs)**

```csharp
// Add these fields to GONetParticipant class

/// <summary>
/// Tracks Unity lifecycle completion state for OnGONetReady gate.
/// </summary>
[NonSerialized] internal bool didAwakeComplete = false;
[NonSerialized] internal bool didStartComplete = false;

/// <summary>
/// Tracks whether DeserializeInitAllCompleted is required for this participant.
///
/// TRUE for:
/// - Scene-defined objects with IsMine = false (clients receiving sync from server)
/// - Runtime spawns received via InstantiateGONetParticipantEvent (remote spawns)
///
/// FALSE for:
/// - Scene-defined objects with IsMine = true (server authority, no deserialization needed)
/// - Runtime spawns with IsMine = true (local authority, spawned by this machine)
/// </summary>
[NonSerialized] internal bool requiresDeserializeInit = false;

/// <summary>
/// Tracks whether DeserializeInitAllCompleted has occurred.
/// Only relevant if requiresDeserializeInit = true.
/// </summary>
[NonSerialized] internal bool didDeserializeInitComplete = false;

/// <summary>
/// Tracks whether OnGONetReady has already been called for this participant.
/// Prevents duplicate calls across multiple gate check invocations.
/// </summary>
[NonSerialized] internal bool didOnGONetReadyFire = false;
```

### **Lifecycle Milestone Markers (GONetParticipant.cs)**

```csharp
private IEnumerator AwakeCoroutine()
{
    yield return GONetMain.OnAwake_ApplyDesignTimeMetadata(this);

    if (!IsInternallyConfigured)
    {
        GONetLog.Error($"GONetParticipant on '{name}' is not internally configured...");
        enabled = false;
    }

    // MILESTONE: Awake complete
    didAwakeComplete = true;
    GONetMain.CheckAndPublishOnGONetReady_IfAllConditionsMet(this);
}

private void Start()
{
    if (Application.isPlaying)
    {
        // ... existing Start logic ...

        if (!WasInstantiated)
        {
            IsOKToStartAutoMagicalProcessing = true;
        }

        GONetMain.Start_AutoPropagateInstantiation_IfAppropriate(this);

        // ... rigidbody setup ...

        // MILESTONE: Start complete
        didStartComplete = true;
        GONetMain.CheckAndPublishOnGONetReady_IfAllConditionsMet(this);
    }
}
```

### **DeserializeInit Markers (GONetParticipant.cs)**

```csharp
/// <summary>
/// Marks this participant as requiring DeserializeInitAllCompleted before OnGONetReady.
/// Called for objects that will receive remote sync data.
/// </summary>
internal void MarkRequiresDeserializeInit()
{
    requiresDeserializeInit = true;
    // Don't check gate yet - deserialization hasn't happened
}

/// <summary>
/// Marks DeserializeInitAllCompleted as complete and checks OnGONetReady gate.
/// Called when remote sync data has been processed.
/// </summary>
internal void MarkDeserializeInitComplete()
{
    didDeserializeInitComplete = true;
    GONetMain.CheckAndPublishOnGONetReady_IfAllConditionsMet(this);
}
```

### **Extended IsGONetReady() Method (GONet.cs:8561)**

```csharp
public static bool IsGONetReady(GONetParticipant gonetParticipant)
{
    // Check basic participant initialization
    if (gonetParticipant == null ||
        gonetParticipant.OwnerAuthorityId == OwnerAuthorityId_Unset ||
        gonetParticipant.gonetId_raw == GONetParticipant.GONetIdRaw_Unset ||
        !gonetParticipant.IsInternallyConfigured)
    {
        return false;
    }

    // NEW: Check Unity lifecycle completion
    if (!gonetParticipant.didAwakeComplete)
    {
        return false; // Awake not complete
    }

    if (!gonetParticipant.didStartComplete)
    {
        return false; // Start not complete
    }

    // NEW: Check deserialization requirement (conditional)
    if (gonetParticipant.requiresDeserializeInit && !gonetParticipant.didDeserializeInitComplete)
    {
        return false; // Waiting for remote sync data
    }

    // NEW: Check not in limbo
    if (gonetParticipant.Client_IsInLimbo)
    {
        return false; // In limbo state
    }

    // Check client/server status is known
    if (!IsClientVsServerStatusKnown)
    {
        return false;
    }

    // If we're a client, ensure client instance exists and is fully initialized
    if (IsClient)
    {
        if (GONetClient == null || !GONetClient.IsInitializedWithServer)
        {
            return false;
        }
    }

    // Check GONetLocal lookup is available
    if (GONetLocal.LookupByAuthorityId == null)
    {
        return false;
    }

    GONetLocal local = GONetLocal.LookupByAuthorityId[gonetParticipant.OwnerAuthorityId];
    if (local == null)
    {
        return false;
    }

    return true;
}
```

### **Simplified Gate Check Function (GONet.cs)**

```csharp
/// <summary>
/// Checks if all conditions are met to call OnGONetReady for this participant.
/// Delegates all prerequisite checks to IsGONetReady(), then broadcasts OnGONetReady if ready.
/// This is called whenever any lifecycle milestone is reached or prerequisite changes.
/// </summary>
internal static void CheckAndPublishOnGONetReady_IfAllConditionsMet(GONetParticipant participant)
{
    // Simple check: Is participant ready?
    if (!IsGONetReady(participant))
    {
        return; // Not ready yet - IsGONetReady handles all prerequisite checks
    }

    // Deduplication: Already called?
    if (participant.didOnGONetReadyFire)
    {
        return; // Already broadcast OnGONetReady for this participant
    }

    // Mark as fired BEFORE calling callbacks (prevent re-entrance)
    participant.didOnGONetReadyFire = true;

    // ALL CONDITIONS MET - Broadcast OnGONetReady
    GONetLog.Info($"[OnGONetReady] '{participant.name}' (GONetId: {participant.GONetId}) - Broadcasting OnGONetReady");

    using (var en = allGONetBehaviours.GetEnumerator())
    {
        while (en.MoveNext())
        {
            GONetBehaviour gnBehaviour = en.Current;
            try
            {
                gnBehaviour.OnGONetReady(participant);
            }
            catch (Exception ex)
            {
                GONetLog.Error($"[OnGONetReady] Exception in OnGONetReady() for behaviour '{gnBehaviour.GetType().Name}' on '{gnBehaviour.gameObject.name}': {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
```

### **Setting requiresDeserializeInit Flag**

**Callsites where we determine if deserialization is needed:**

#### **Scene-Defined Objects (GONet.cs)**

```csharp
// When processing scene-defined participants during scene load
if (GONetMain.WasDefinedInScene(participant))
{
    if (participant.IsMine)
    {
        // Server authority - no deserialization needed
        participant.requiresDeserializeInit = false;
    }
    else
    {
        // Client will receive sync data from server
        participant.requiresDeserializeInit = true;
        participant.MarkRequiresDeserializeInit();
    }
}
```

#### **Runtime Spawns - Local Authority (GONet.cs)**

```csharp
// Client_InstantiateToBeRemotelyControlledByMe / Server spawns
// Local spawns don't need deserialization
participant.requiresDeserializeInit = false;
```

#### **Runtime Spawns - Remote (GONet.cs)**

```csharp
// Instantiate_Remote / CompleteRemoteInstantiation
// Remote spawns will receive spawn event data
participant.requiresDeserializeInit = true;
participant.MarkRequiresDeserializeInit();
```

#### **Limbo Objects (GONet.cs)**

```csharp
// Client_ExitLimbo (line ~5671)
// Limbo objects are always local authority (client spawned)
participant.requiresDeserializeInit = false;

// Mark as no longer in limbo
participant.client_isInLimbo = false;

// Check gate (will call OnGONetReady if all conditions met)
CheckAndPublishOnGONetReady_IfAllConditionsMet(participant);
```

---

## Integration with Existing Event Flow

### **OnDeserializeInitAllCompletedGNPEvent Handler (GONet.cs:1926)**

```csharp
private static void OnDeserializeInitAllCompletedGNPEvent(GONetEventEnvelope<GONetParticipantDeserializeInitAllCompletedEvent> eventEnvelope)
{
    GONetParticipant gonetParticipant = eventEnvelope.GONetParticipant;

    // Call legacy lifecycle hook (backward compatibility)
    using (var en = allGONetBehaviours.GetEnumerator())
    {
        while (en.MoveNext())
        {
            GONetBehaviour gnBehaviour = en.Current;
            gnBehaviour.OnGONetParticipantDeserializeInitAllCompleted(gonetParticipant);
        }
    }

    // Mark DeserializeInit as complete and check if OnGONetReady can fire
    gonetParticipant.MarkDeserializeInitComplete();
    // CheckAndPublishOnGONetReady_IfAllConditionsMet() called internally by MarkDeserializeInitComplete()
}
```

**Key changes:**
- ✅ Keep existing event publishing locations (no changes to when DeserializeInitAllCompleted is published)
- ✅ Keep legacy `OnGONetParticipantDeserializeInitAllCompleted()` callback (backward compatibility)
- ✅ Add gate check after marking DeserializeInit complete
- ❌ Remove direct `OnGONetReady()` broadcast from this handler (moved to gate check function)

### **GONetId Assignment Points - CRITICAL**

**CRITICAL BUG FIX (October 9, 2025):** Missing gate checks in GONetId assignment functions were causing OnGONetReady to never fire for client-spawned objects!

**The Problem:**
When GONetId was assigned via `AssignGONetIdRaw_IfAppropriate()` or `AssignGONetIdRaw_Direct()`, the gate check was NOT being called. This meant:
1. Client spawns beacon → GONetId gets assigned
2. **Gate check never fires** → OnGONetReady never called
3. User code (SpawnTestBeacon.cs) never initializes `spawnTime`
4. Beacon appears over-aged (age = Time.time - 0) and gets immediately despawned

**The Fix:**
Added gate checks to BOTH GONetId assignment functions:

```csharp
// GONet.cs:6661 - AssignGONetIdRaw_IfAppropriate()
private static void AssignGONetIdRaw_IfAppropriate(GONetParticipant gonetParticipant, bool shouldForceChangeEventIfAlreadySet = false)
{
    if (shouldForceChangeEventIfAlreadySet || gonetParticipant.gonetId_raw == GONetParticipant.GONetId_Unset)
    {
        if (lastAssignedGONetIdRaw < GONetParticipant.GONetId_Raw_MaxValue)
        {
            uint gonetId_raw = GetNextAvailableGONetIdRaw(gonetParticipant);
            gonetParticipant.GONetId = (gonetId_raw << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED) | gonetParticipant.OwnerAuthorityId;

            // LIFECYCLE GATE: GONetId assigned - check if OnGONetReady can fire
            CheckAndPublishOnGONetReady_IfAllConditionsMet(gonetParticipant);
        }
        else
        {
            throw new OverflowException("Unable to assign a new GONetId...");
        }
    }
}

// GONet.cs:6734 - AssignGONetIdRaw_Direct()
internal static void AssignGONetIdRaw_Direct(GONetParticipant gonetParticipant, uint gonetId)
{
    gonetParticipant.GONetId = gonetId;
    GONetLog.Debug($"[GONetId] Directly assigned GONetId {gonetId} to '{gonetParticipant.gameObject.name}'");

    // LIFECYCLE GATE: GONetId assigned - check if OnGONetReady can fire
    CheckAndPublishOnGONetReady_IfAllConditionsMet(gonetParticipant);
}
```

**Why This Was Critical:**
- `AssignGONetIdRaw_IfAppropriate()` is called for:
  - Client-spawned objects (via batch system)
  - Server-spawned objects
  - Scene-defined objects
- `AssignGONetIdRaw_Direct()` is called for:
  - Scene-defined objects (client receiving sync from server)
  - Late-joiner synchronization

Without these gate checks, **most spawn types would never fire OnGONetReady!**

**All GONetId Assignment Callsites:**
- ✅ `AssignGONetIdRaw_IfAppropriate()` - Scene-defined and runtime spawns **(FIXED)**
- ✅ `AssignGONetIdRaw_Direct()` - Direct assignment for scene sync **(FIXED)**
- ✅ `Client_ExitLimbo()` - Calls AssignGONetIdRaw_IfAppropriate internally
- ✅ GONetParticipant.AwakeCoroutine() - Marks Awake complete, then gate check
- ✅ GONetParticipant.Start() - Marks Start complete, then gate check
- ✅ OnDeserializeInitAllCompletedGNPEvent - Marks deserialize complete, then gate check

---

## The Parameterless OnGONetReady() Pattern

### **CRITICAL: Two-Version API Design**

GONet uses a **bridge pattern** for OnGONetReady that provides both a parameterized and parameterless version. **Both versions MUST coexist** - this is not optional!

**The Pattern (GONetBehaviour.cs:380-461):**

```csharp
// BASE CLASS (GONetBehaviour) - Parameterized version for global broadcast
public virtual void OnGONetReady(GONetParticipant gonetParticipant) { }

// DERIVED CLASS (GONetParticipantCompanionBehaviour) - Bridge + Convenience version
public override void OnGONetReady(GONetParticipant gonetParticipant)
{
    base.OnGONetReady(gonetParticipant);

    // BRIDGE: Check if this is OUR participant
    if (gonetParticipant == this.gonetParticipant)
    {
        // Call parameterless convenience version
        OnGONetReady();

        // Notify RPC system component is ready
        GONetEventBus.OnComponentReadyToReceiveRpcs(gonetParticipant);
    }
}

// CONVENIENCE VERSION - User-friendly API (no parameter needed)
public virtual void OnGONetReady() { }
```

### **Why This Pattern Exists**

**Problem:** GONet broadcasts OnGONetReady to ALL behaviours for ALL participants (global broadcast). Most user code only cares about its own participant.

**Solution:** Provide BOTH versions:

1. **Parameterized `OnGONetReady(GONetParticipant participant)`**
   - **Purpose:** Receive notifications for ALL participants in the system
   - **Use case:** System-wide tracking (e.g., chat system tracking all players)
   - **Called by:** GONet framework via global broadcast

2. **Parameterless `OnGONetReady()`**
   - **Purpose:** Convenient hook for component's own participant only
   - **Use case:** Component initialization (90% of user code)
   - **Called by:** Bridge in GONetParticipantCompanionBehaviour

### **User Code Examples**

**Example 1: Simple Case (Own Participant Only)**
```csharp
public class SpawnTestBeacon : GONetParticipantCompanionBehaviour
{
    private float spawnTime;

    // Override parameterless version - automatic filtering to own participant
    public override void OnGONetReady()
    {
        base.OnGONetReady();

        // This ONLY fires for THIS beacon's participant
        // No need to check "if (participant == this.gonetParticipant)"
        spawnTime = Time.time;
    }
}
```

**Example 2: System-Wide Tracking (All Participants)**
```csharp
public class GONetSampleChatSystem : GONetParticipantCompanionBehaviour
{
    private List<ChatParticipant> participants = new List<ChatParticipant>();

    // Override BOTH versions for different purposes

    // Parameterized: Track ALL participants system-wide
    public override void OnGONetReady(GONetParticipant gonetParticipant)
    {
        base.OnGONetReady(gonetParticipant);

        // Check if this participant has GONetLocal (represents a player)
        if (gonetParticipant.TryGetComponent(out GONetLocal gonetLocal))
        {
            // Add to chat participants list
            participants.Add(new ChatParticipant
            {
                AuthorityId = gonetLocal.OwnerAuthorityId,
                DisplayName = $"Player_{gonetLocal.OwnerAuthorityId}"
            });
        }

        // DON'T return here - let base class call parameterless version too!
    }

    // Parameterless: Initialize OWN chat system component
    public override void OnGONetReady()
    {
        base.OnGONetReady();

        // This fires for THIS chat system's participant only
        localAuthorityId = GONetMain.MyAuthorityId;
        localDisplayName = GONetMain.IsServer ? "Server" : $"Player_{localAuthorityId}";

        // Scan for existing participants (catch-up)
        ScanForExistingParticipants();
    }
}
```

### **Why User Complained About Removing Parameterless Version**

During debugging, I mistakenly tried to remove the parameterless version and replace user code with the parameterized version. The user correctly pushed back because:

1. **Pattern Consistency:** All other lifecycle hooks use this two-version bridge pattern:
   - `OnGONetParticipantEnabled(GONetParticipant)` + `OnGONetParticipantEnabled()`
   - `OnGONetParticipantStarted(GONetParticipant)` + `OnGONetParticipantStarted()`
   - `OnGONetReady(GONetParticipant)` + `OnGONetReady()`

2. **User Convenience:** 90% of user code only cares about its own participant. Forcing them to always check `if (participant == this.gonetParticipant)` is error-prone and verbose.

3. **System Design:** The global broadcast is a powerful feature for system-wide tracking, but shouldn't be the ONLY way to use OnGONetReady.

**LESSON LEARNED:** The bug was NOT in the API design (which is correct). The bug was that gate checks were missing in GONetId assignment functions, so OnGONetReady was never being called at all!

---

## User-Facing Documentation Updates

### **GONetBehaviour.cs - OnGONetReady() Documentation**

```csharp
/// <summary>
/// <para><b>THE UNIFIED INITIALIZATION HOOK</b> - Called when GONet is fully initialized and ready for use.</para>
///
/// <para><b>GUARANTEED when this is called:</b></para>
/// <list type="bullet">
///   <item><description><b>Unity Lifecycle:</b> Awake(), OnEnable(), and Start() have ALL completed</description></item>
///   <item><description><see cref="GONetParticipant.GONetId"/> is assigned (non-zero)</description></item>
///   <item><description><see cref="GONetParticipant.OwnerAuthorityId"/> is assigned (non-zero)</description></item>
///   <item><description><see cref="GONetMain.IsServer"/> and <see cref="GONetMain.MyAuthorityId"/> are valid</description></item>
///   <item><description>If client: <see cref="GONetClient.IsInitializedWithServer"/> is true</description></item>
///   <item><description><see cref="GONetLocal"/> instances are available</description></item>
///   <item><description>RPCs can be called safely</description></item>
///   <item><description>If remote object: DeserializeInitAllCompleted has occurred (initial sync data received)</description></item>
/// </list>
///
/// <para><b>NOT GUARANTEED:</b></para>
/// <list type="bullet">
///   <item><description><b>Update() may have already been called</b> - For network-dependent initialization (remote spawns receiving sync data),
///   Update() may run for several frames before OnGONetReady fires. Check <see cref="IsGONetReady"/> in Update() if needed.</description></item>
/// </list>
///
/// <para><b>Works in ALL scenarios:</b></para>
/// <list type="bullet">
///   <item><description><b>Design-time:</b> Component present in scene</description></item>
///   <item><description><b>Runtime:</b> Component added via GONetRuntimeComponentInitializer</description></item>
///   <item><description><b>Limbo:</b> Objects in limbo state (OnGONetReady deferred until limbo exit)</description></item>
/// </list>
///
/// <para><b>RECOMMENDED USAGE:</b></para>
/// <code>
/// public override void OnGONetReady(GONetParticipant participant)
/// {
///     base.OnGONetReady(participant);
///
///     if (participant == this.gonetParticipant)
///     {
///         // This participant is ready - initialize networking
///         InitializeMyNetworkState();
///     }
/// }
/// </code>
/// </summary>
public virtual void OnGONetReady(GONetParticipant participant) { }
```

### **User Workaround for Update() Timing**

```csharp
public class MyNetworkScript : GONetBehaviour
{
    private bool isNetworkReady = false;

    public override void OnGONetReady(GONetParticipant participant)
    {
        base.OnGONetReady(participant);

        if (participant == this.gonetParticipant)
        {
            isNetworkReady = true;
            // Initialize networking here
        }
    }

    void Update()
    {
        if (!isNetworkReady)
        {
            // Skip network logic until OnGONetReady fires
            return;
        }

        // Safe to use networking here
    }
}
```

---

## Edge Cases & Special Considerations

### **1. Limbo Mode 1 Component Re-Enable Timing**

**Problem:** When exiting limbo in Mode 1, components are re-enabled. Unity will call Start() on the **next frame** after enable.

**Current Implementation:**
```csharp
// Client_ExitLimbo() - Mode 1
foreach (MonoBehaviour component in participant.client_limboDisabledComponents)
{
    component.enabled = true; // Start() won't fire until next frame!
}
participant.didStartComplete = true; // Mark Start as complete immediately
CheckAndPublishOnGONetReady_IfAllConditionsMet(participant);
```

**Question:** Should we wait for Start() to actually fire, or mark it as "conceptually complete"?

**Options:**
- **Option A:** Wait for Start() (adds 1 frame delay, more accurate guarantee)
- **Option B:** Mark as complete immediately (fires OnGONetReady same frame, slightly weaker guarantee)

**Recommendation:** Option B - Mark as complete immediately
- **Reasoning:** Start() already ran during initial instantiation (before components were disabled)
- **Guarantee:** "Start() has completed for GONetParticipant" - still true, even though component Start() methods are re-running

### **2. GONetRuntimeComponentInitializer Edge Case**

**Scenario:** Component added mid-game via GONetRuntimeComponentInitializer to a GameObject that's already networked.

**Current Behavior (GONetBehaviour.cs:280-330):**
```csharp
protected virtual void Start()
{
    bool shouldCatchUp = /* component added after participants already ready */;

    if (shouldCatchUp)
    {
        // Call OnGONetReady for ALL ready participants, not just this component's participant
        foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
        {
            GONetParticipant participant = kvp.Value;
            if (GONetMain.IsGONetReady(participant))
            {
                OnGONetReady(participant); // Catch up on missed participants
            }
        }
    }
}
```

**Question:** How does `IsGONetReady()` check work with gate system?

**Solution:** The extended `IsGONetReady()` method already handles all checks. Runtime-added components call it in their Start() method to check if participants are ready and catch up on missed OnGONetReady calls.

### **3. Duplicate OnGONetReady Prevention**

**Problem:** Multiple code paths might trigger gate checks for the same participant.

**Solution:** `didOnGONetReadyFire` flag prevents duplicate broadcasts:
```csharp
// Gate 7 in CheckAndPublishOnGONetReady_IfAllConditionsMet()
if (participant.didOnGONetReadyFire)
{
    return; // Already called, skip
}

// Mark as fired BEFORE calling callbacks (prevent re-entrance)
participant.didOnGONetReadyFire = true;
```

### **4. OnDestroy / Cleanup**

**Question:** What happens if participant is destroyed before OnGONetReady fires?

**Solution:** Gate check handles null/destroyed participants:
```csharp
// At top of CheckAndPublishOnGONetReady_IfAllConditionsMet()
if (participant == null || participant.gameObject == null)
{
    GONetLog.Warning($"[OnGONetReady Gate] Participant is null or destroyed - skipping gate check");
    return;
}
```

---

## Testing Strategy

### **Test Scenarios**

1. **Scene-Defined Object (Server)**
   - ✅ Verify OnGONetReady fires after Start()
   - ✅ Verify requiresDeserializeInit = false
   - ✅ Verify no duplicate calls

2. **Scene-Defined Object (Client)**
   - ✅ Verify OnGONetReady fires after Start() AND DeserializeInit
   - ✅ Verify requiresDeserializeInit = true
   - ✅ Verify network sync received before OnGONetReady

3. **Runtime Spawn (Local Authority)**
   - ✅ Verify OnGONetReady fires after Start()
   - ✅ Verify no race condition (OnGONetReady NOT before Awake)
   - ✅ Verify requiresDeserializeInit = false

4. **Runtime Spawn (Remote)**
   - ✅ Verify OnGONetReady fires after Start() AND DeserializeInit
   - ✅ Verify Update() may run before OnGONetReady (expected behavior)
   - ✅ Verify requiresDeserializeInit = true

5. **Limbo Mode 1**
   - ✅ Verify Start() blocked until limbo exit
   - ✅ Verify OnGONetReady fires after limbo exit
   - ✅ Verify component re-enable sequence

6. **Limbo Mode 2**
   - ✅ Verify Start() runs during limbo
   - ✅ Verify Update() runs during limbo
   - ✅ Verify OnGONetReady fires after limbo exit

7. **Limbo Mode 3**
   - ✅ Verify full lifecycle runs during limbo
   - ✅ Verify User can check Client_IsInLimbo in Update()
   - ✅ Verify OnGONetReady fires after limbo exit

### **Unit Test Locations**

- `Assets/GONet/Code/GONet/Editor/UnitTests/GONet/GONetLifecycleTests.cs` (NEW)
- `Assets/GONet/Code/GONet/Editor/UnitTests/GONet/GONetLimboModeTests.cs` (EXISTING)

---

## Migration Guide

### **For Existing GONet Users**

**No breaking changes expected** - OnGONetReady will simply fire **later** (after Start instead of before Awake).

**Potential Issues:**

1. **If you were working around the race condition:**
   ```csharp
   // OLD WORKAROUND (can be removed)
   if (projectile.movementDirection != Vector3.zero)
   {
       // Move projectile
   }

   // NEW (no workaround needed)
   // movementDirection is guaranteed to be initialized in Awake/Start before OnGONetReady
   projectile.transform.position += projectile.movementDirection * Time.deltaTime;
   ```

2. **If you relied on OnGONetReady firing immediately:**
   - OnGONetReady will now fire **1 frame later** (after Start instead of same frame as GONetId assignment)
   - **Impact:** Minimal - 1 frame delay is negligible for networking initialization

### **For New GONet Users**

**Just use OnGONetReady** - it works as expected:
```csharp
public override void OnGONetReady(GONetParticipant participant)
{
    base.OnGONetReady(participant);

    if (participant == this.gonetParticipant)
    {
        // Awake, OnEnable, Start all guaranteed to have completed
        // Safe to access any component fields initialized in those methods
        InitializeNetworking();
    }
}
```

---

## Open Questions

1. **Limbo Mode 1 Start() timing:** Should we wait 1 frame for component Start() to actually fire after re-enable, or mark as "conceptually complete" immediately?
   - **Current recommendation:** Mark as complete immediately (Start ran during initial instantiation, even though component Start methods are re-running)

2. **Diagnostic logging level:** Should gate check BLOCKED messages use Debug (hidden by default) or Info (always visible)?
   - **Current recommendation:** Debug level during normal operation, only log Info when OnGONetReady actually fires

3. **requiresDeserializeInit edge cases:** Are there any spawn scenarios not covered in the matrix above?
   - **Need to verify:** Late-joiner synchronization, scene transitions, DontDestroyOnLoad objects

---

## Implementation Checklist

- [x] Add lifecycle tracking fields to GONetParticipant.cs
- [x] Mark Awake completion in GONetParticipant.AwakeCoroutine()
- [x] Mark Start completion in GONetParticipant.Start()
- [x] Add MarkRequiresDeserializeInit() and MarkDeserializeInitComplete() methods
- [x] Implement CheckAndPublishOnGONetReady_IfAllConditionsMet() in GONet.cs
- [x] Extend IsGONetReady() to check Unity lifecycle completion
- [x] Set requiresDeserializeInit flag at appropriate callsites (scene-defined, runtime spawns, limbo)
- [x] Update OnDeserializeInitAllCompletedGNPEvent handler to use gate system
- [x] Update Client_ExitLimbo() to use gate system
- [x] Document "Global Broadcast Once" guarantee in design document
- [ ] Update OnGONetReady() documentation in GONetBehaviour.cs (existing docs already comprehensive)
- [ ] Write unit tests for all lifecycle paths
- [ ] Test with existing GONet samples (ensure no regressions)
- [ ] Update CLAUDE.md with new lifecycle guarantees

---

## UpdateAfterGONetReady Pattern (Introduced October 9, 2025)

### **The Problem: Update() vs OnGONetReady Race Condition**

GONetParticipant.Awake() is an **async coroutine** that yields during metadata loading. This creates a race condition where Update() can run before OnGONetReady fires:

```
Timeline (RACE CONDITION):
1. Unity calls Awake() → starts coroutine → yields immediately
2. Unity calls Start() → didStartComplete = true
3. Unity calls Update() → YOUR CODE RUNS with uninitialized state! ❌
4. Coroutine completes (next frame) → OnGONetReady fires (TOO LATE)
```

**Real-world impact (SpawnTestBeacon bug):**
```csharp
private float spawnTime; // Initialized in OnGONetReady

void Update()
{
    float age = Time.time - spawnTime; // spawnTime = 0! Age = 35 seconds!
    if (age >= lifetime) Destroy(gameObject); // Immediate destruction ❌
}

public override void OnGONetReady()
{
    spawnTime = Time.time; // Fires AFTER Update() already ran
}
```

### **Solution 1: Defensive Check in Update() (Manual)**

Users can add defensive checks for uninitialized state:

```csharp
private float spawnTime; // Set in OnGONetReady

public override void OnGONetReady()
{
    base.OnGONetReady();
    spawnTime = Time.time; // Initialize state
}

void Update()
{
    // CRITICAL: Defensive check for race condition
    // GONetParticipant.Awake() is a COROUTINE that yields, so OnGONetReady
    // can fire AFTER Update() starts running!
    if (spawnTime == 0)
    {
        return; // Not ready yet - skip this frame
    }

    float age = Time.time - spawnTime; // Safe to use now
    // ... rest of logic
}
```

**Pros:**
- ✅ Full control over Unity Update() timing
- ✅ No framework overhead if you don't use UpdateAfterGONetReady

**Cons:**
- ❌ Requires manual defensive checks in every Update() method
- ❌ Error-prone - easy to forget the check

### **Solution 2: UpdateAfterGONetReady() (Framework-Provided)**

GONetBehaviour provides a virtual `UpdateAfterGONetReady()` method that's guaranteed to only run AFTER OnGONetReady fires:

```csharp
public class MyNetworkedObject : GONetParticipantCompanionBehaviour
{
    private Vector3 movementDirection;
    private float speed = 10f;

    public override void OnGONetReady()
    {
        base.OnGONetReady();
        // Initialize state that depends on GONet being ready
        movementDirection = transform.forward;
    }

    protected override void UpdateAfterGONetReady()
    {
        // No defensive checks needed - guaranteed to run AFTER OnGONetReady
        if (IsMine)
        {
            transform.position += movementDirection * Time.deltaTime * speed;
        }
    }
}
```

**Performance Characteristics:**

- ✅ **Zero overhead if not overridden** - Static per-type caching ensures empty implementations are never called
- ✅ **One-time reflection cost** - Only the first instance of each GONetBehaviour type uses reflection to detect override
- ✅ **Centralized update loop** - Called from GONet.Update() - no Unity Update() penalty
- ✅ **Per-frame overhead** - Only for behaviours that actually override the method

**How It Works (Implementation Details):**

1. **Static Per-Type Caching (GONetBehaviour.cs:37-44):**
   ```csharp
   // Static cache: One reflection check per type (not per instance)
   private static readonly Dictionary<Type, bool> hasUpdateAfterGONetReadyOverride_ByType = new Dictionary<Type, bool>();

   // Instance flag: Fast per-frame access
   internal bool hasUpdateAfterGONetReadyOverride;
   ```

2. **Override Detection (GONetBehaviour.cs:89-107):**
   ```csharp
   protected virtual void Awake()
   {
       GONetMain.RegisterBehaviour(this);

       // Check if this type's override status is already cached
       Type myType = GetType();
       if (!hasUpdateAfterGONetReadyOverride_ByType.TryGetValue(myType, out bool hasOverride))
       {
           // First instance of this type - use reflection ONCE to detect override
           MethodInfo updateMethod = myType.GetMethod(
               nameof(UpdateAfterGONetReady),
               BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

           // Check if the declaring type is THIS type (not base GONetBehaviour)
           hasOverride = updateMethod != null && updateMethod.DeclaringType != typeof(GONetBehaviour);

           // Cache the result for all future instances of this type
           hasUpdateAfterGONetReadyOverride_ByType[myType] = hasOverride;
       }

       // Store instance-level flag for fast per-frame access
       hasUpdateAfterGONetReadyOverride = hasOverride;
   }
   ```

3. **Centralized Update Loop (GONet.cs:2850-2881):**
   ```csharp
   // Called from GONetGlobal.Update() → GONetMain.Update() → Update_DoTheHeavyLifting_IfAppropriate()
   foreach (var behaviour in allGONetBehaviours)
   {
       // Fast check: Does this behaviour's type override UpdateAfterGONetReady?
       if (!behaviour.hasUpdateAfterGONetReadyOverride)
       {
           continue; // Skip - empty base implementation (zero cost)
       }

       // Check if behaviour is a GONetParticipantCompanionBehaviour with an attached participant
       GONetParticipantCompanionBehaviour companionBehaviour = behaviour as GONetParticipantCompanionBehaviour;
       if (companionBehaviour != null && companionBehaviour.GONetParticipant != null)
       {
           // Only call UpdateAfterGONetReady if this participant is fully ready
           if (IsGONetReady(companionBehaviour.GONetParticipant))
           {
               companionBehaviour.UpdateAfterGONetReady();
           }
       }
       else
       {
           // Non-companion behaviours (plain GONetBehaviour) always get called
           // They're responsible for their own ready checks if needed
           behaviour.UpdateAfterGONetReady();
       }
   }
   ```

### **When to Use UpdateAfterGONetReady vs Update()**

| Use Case | Recommended Approach | Reasoning |
|----------|---------------------|-----------|
| Logic depends on values initialized in OnGONetReady (e.g., spawn time, movement direction, GONetId-based state) | ✅ **UpdateAfterGONetReady** | No defensive checks needed - guaranteed safe |
| Simple animations, input polling, non-networked logic | ✅ **Update()** | No GONet dependency, standard Unity pattern |
| Need Update() for non-networked logic + GONet logic | ✅ **Both** (Update for non-networked, UpdateAfterGONetReady for networked) | Separation of concerns |
| High-performance critical path (every frame, thousands of objects) | ⚠️ **Profile both** | UpdateAfterGONetReady has minimal overhead, but measure if critical |
| **Need precise script execution order control** | ⚠️ **Defensive Update()** | Unity's Script Execution Order only affects Update(), NOT UpdateAfterGONetReady |

### **⚠️ CRITICAL: Script Execution Order Implications**

**Unity's Script Execution Order system** (Edit → Project Settings → Script Execution Order) allows you to control when a script's Update() method runs relative to other scripts.

**IMPORTANT:** UpdateAfterGONetReady() **bypasses this system entirely**!

#### **How Unity's Script Execution Order Works:**

```
Frame Timeline:
-32000: GONetGlobal.Update() ← ALL UpdateAfterGONetReady() calls happen here
  -199: GONetParticipant.Update() (early scripts)
     0: Default scripts' Update() (most user scripts)
  +100: Late scripts' Update()
 +32000: GONetLocal.Update() (very late scripts)
```

**Key Points:**

1. **UpdateAfterGONetReady() execution time:**
   - Runs at GONetGlobal's priority: **-32000** (VERY early in frame)
   - Runs BEFORE most scripts' Update() methods
   - NOT affected by individual script's Script Execution Order settings

2. **Update() execution time:**
   - Runs at script's configured priority (default: 0)
   - CAN be controlled via Unity's Script Execution Order settings
   - Runs AFTER UpdateAfterGONetReady() (if priority > -32000)

3. **Relative ordering of UpdateAfterGONetReady() calls:**
   - ALL UpdateAfterGONetReady() calls execute at the SAME priority (-32000)
   - Order between different GONetBehaviours is UNDEFINED
   - Cannot control which script's UpdateAfterGONetReady() runs first

#### **Example Scenario:**

```csharp
// Script A (Script Execution Order: +50)
public class PlayerController : GONetParticipantCompanionBehaviour
{
    protected override void UpdateAfterGONetReady()
    {
        // Runs at -32000 (GONetGlobal's priority)
        // NOT affected by +50 setting!
        UpdatePlayerPosition();
    }
}

// Script B (Script Execution Order: +100)
public class CameraFollow : MonoBehaviour
{
    void Update()
    {
        // Runs at +100 (as configured)
        // Runs AFTER PlayerController.UpdateAfterGONetReady()
        FollowPlayer();
    }
}
```

**Execution Order:**
1. **-32000:** PlayerController.UpdateAfterGONetReady() (ignores +50 setting)
2. **+50:** (nothing - PlayerController has no Update() method)
3. **+100:** CameraFollow.Update()

#### **When Script Execution Order Matters:**

**Use defensive Update() pattern if you need:**

1. **Precise ordering relative to other scripts:**
   ```csharp
   // Need this to run AFTER PhysicsManager.Update() (priority: +10)
   // Solution: Use Update() at priority +20, NOT UpdateAfterGONetReady()
   void Update()
   {
       if (spawnTime == 0) return; // Defensive check
       ApplyPhysicsResults(); // Runs at +20, after PhysicsManager
   }
   ```

2. **Late-frame logic (after all movement):**
   ```csharp
   // Need this to run LATE in frame (priority: +1000)
   // Solution: Use Update() at priority +1000, NOT UpdateAfterGONetReady()
   void Update()
   {
       if (isInitialized == false) return; // Defensive check
       CalculateFinalCameraPosition(); // Runs very late in frame
   }
   ```

3. **Coordinated ordering between multiple scripts:**
   ```csharp
   // Script A needs to run before Script B
   // Solution: Use Update() with priorities (A: +50, B: +60)
   // UpdateAfterGONetReady() cannot guarantee this ordering!
   ```

**Use UpdateAfterGONetReady() if you don't need:**
- Control over when it runs relative to other scripts
- Late-frame execution (it runs EARLY at -32000)
- Guaranteed ordering between multiple GONetBehaviours

### **Migration Path for Existing Code**

**Before (with defensive check):**
```csharp
public class SpawnTestBeacon : GONetParticipantCompanionBehaviour
{
    private float spawnTime;

    public override void OnGONetReady()
    {
        base.OnGONetReady();
        spawnTime = Time.time;
    }

    void Update()
    {
        // DEFENSIVE CHECK (required due to race condition)
        if (spawnTime == 0) return;

        float age = Time.time - spawnTime;
        // ... rest of logic
    }
}
```

**After (with UpdateAfterGONetReady):**
```csharp
public class SpawnTestBeacon : GONetParticipantCompanionBehaviour
{
    private float spawnTime;

    public override void OnGONetReady()
    {
        base.OnGONetReady();
        spawnTime = Time.time;
    }

    protected override void UpdateAfterGONetReady()
    {
        // NO defensive check needed - guaranteed to run AFTER OnGONetReady
        float age = Time.time - spawnTime;
        // ... rest of logic
    }
}
```

### **Best Practices**

1. **Use UpdateAfterGONetReady for networked logic:**
   - ✅ Accessing values initialized in OnGONetReady
   - ✅ Using IsMine, GONetId, OwnerAuthorityId
   - ✅ Calling RPCs or accessing sync values

2. **Use Update() for non-networked logic:**
   - ✅ Simple animations (rotation, scaling)
   - ✅ Input polling (keyboard, mouse)
   - ✅ Local-only visual effects

3. **Combine both if needed:**
   ```csharp
   void Update()
   {
       // Non-networked logic (always runs)
       HandleLocalInput();
       UpdateAnimations();
   }

   protected override void UpdateAfterGONetReady()
   {
       // Networked logic (only runs after OnGONetReady)
       if (IsMine)
       {
           SendPositionUpdates();
       }
   }
   ```

4. **Document your choice:**
   - Add comments explaining WHY you chose UpdateAfterGONetReady vs Update()
   - Reference SpawnTestBeacon.cs as an example of defensive Update() pattern

---

**Last Updated:** October 9, 2025
**Status:** Implementation complete - awaiting user testing before commit

**Recent Updates:**
- Added "UpdateAfterGONetReady Pattern" section documenting the framework-level solution for Update() race condition
- Explained performance characteristics (static per-type caching, zero overhead if not overridden)
- Provided migration path from defensive Update() checks to UpdateAfterGONetReady
- Added best practices for when to use UpdateAfterGONetReady vs Update()
- Documented implementation details (static caching, centralized update loop)
- Added "Global Broadcast Once Guarantee" section documenting the exactly-once delivery guarantee for every (GONetParticipant, GONetBehaviour) pair
- Explained bidirectional synchronization mechanism (participant broadcasts to all behaviours, new behaviours catch up on ready participants)
- Documented deduplication via `didOnGONetReadyFire` flag
- Added example scenarios and timing guarantees
- Verified existing implementation already handles the guarantee correctly
