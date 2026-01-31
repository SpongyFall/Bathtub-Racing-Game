# GONet Test Orchestration System

## Overview

The GONet Test Orchestration System allows you to write automated, reproducible test cases in simple text files (`.gotest` format) that programmatically control multiplayer test execution.

**Key Features:**
- ✅ Write tests in simple, human-readable text files
- ✅ Server orchestrates entire test flow
- ✅ Command clients to spawn objects, change scenes, etc. via RPCs
- ✅ Automated verification of synchronization state
- ✅ UI instructions for required human actions (e.g., "Start Client3")
- ✅ Comprehensive pass/fail logging
- ✅ Reproducible test cases for debugging

## Quick Start

### 1. Setup

**Option A: Using GONetRuntimeComponentInitializer (Recommended)**

1. Open your first scene (e.g., `GONetSample.unity`)
2. Create an empty GameObject: `GameObject > Create Empty` → Name it "TestExecutor"
3. Add `GONetRuntimeComponentInitializer` component
4. Configure:
   - **Component Type Name**: `GONet.Sample.GONetTestScriptExecutor`
   - **Remove On Scene Unload**: `false` (persistent across scenes)
5. Add a child TextAsset reference:
   - Create a TextAsset: Right-click in Project → `Create > Text File`
   - Rename to `MyTest.gotest`
   - Copy your test script content into it
6. Drag the TextAsset onto the `Test Script Asset` field

**Option B: Attach Directly to GONetGlobal (Alternative)**

1. Locate the `GONetGlobal` prefab instance in your scene
2. Add `GONetTestScriptExecutor` component
3. Assign your `.gotest` TextAsset to `Test Script Asset` field

### 2. Create a Test Script

Create a new text file with `.gotest` extension (e.g., `MyFirstTest.gotest`):

```
# My First GONet Test
name: Basic Spawn Test
description: Tests that server spawns sync to all clients
require_clients: 2
despawn_wait: 40

# Wait for 2 clients to connect
wait_clients: 2

# Spawn 3 beacons from server
spawn_server: 3

# Wait for sync
wait: 2

# Verify all clients have the beacons
verify_beacons: all

# Success!
log: Test complete - all clients should see 3 beacons
```

### 3. Run the Test

1. Build your project: `File > Build Settings > Build`
2. **Start Server**: Run the build with `-server` argument (or just run first)
3. **Start Client1**: Run a second instance with `-client` argument
4. **Start Client2**: Run a third instance with `-client` argument
5. Watch the test execute automatically!
6. Check logs for test results

## Test Script Format

### Metadata (Top of file)

```
name: Your Test Name
description: Optional description
require_clients: 2          # How many clients needed
despawn_wait: 40            # Default despawn wait time (seconds)
```

### Available Commands

#### Client Management

```
# Wait for N clients to connect before continuing
wait_clients: 2

# Wait for a specific client to connect
wait_client: 3

# Ask human to perform action (shows UI prompt, waits for SPACE key)
human_action: Start Client3 now
```

#### Spawning

```
# Spawn N beacons from server
spawn_server: 3

# Spawn N beacons from specific client (by client number)
spawn_client: 1, count=2

# Spawn N beacons from ALL connected clients
spawn_all_clients: 2
```

#### Scene Management

```
# Change to a different scene (networked)
scene_change: ProjectileTest
```

#### Waiting

```
# Wait N seconds
wait: 2.5

# Wait for beacons to despawn naturally (with UI countdown)
wait_despawn: 40
```

#### Verification

```
# Verify all tracked beacons exist on server
verify_beacons: all

# Verify specific beacons exist (by GONetId)
verify_beacons: 1024,1025,1026

# Verify all tracked beacons have despawned
verify_despawned: all

# Verify exact beacon count
verify_count: 5
```

#### Logging

```
# Log a custom message
log: Starting Phase 2 of test
```

### Comments

```
# This is a comment
# Comments are ignored by the parser
```

## Example Test Scripts

### Example 1: Basic Spawn and Verify

```
name: Basic Spawn Test
require_clients: 2

wait_clients: 2
spawn_server: 3
wait: 2
verify_beacons: all
log: ✓ All clients synced!
```

### Example 2: Multi-Client Spawn

```
name: Multi-Client Spawn Test
require_clients: 2

wait_clients: 2

# Each client spawns 2 beacons
spawn_all_clients: 2

# Server also spawns 2
spawn_server: 2

wait: 2

# Total should be 6 (2*2 clients + 2 server)
verify_count: 6
```

### Example 3: Scene Change Test

```
name: Scene Change Sync Test
require_clients: 2

wait_clients: 2

# Spawn in initial scene
spawn_server: 3
wait: 2
verify_beacons: all

# Change scene (should despawn old beacons)
scene_change: ProjectileTest
wait: 3

# Verify old beacons gone
verify_despawned: all

# Spawn in new scene
spawn_server: 2
wait: 2
verify_beacons: all
```

### Example 4: Late Joiner Test

```
name: Late Joiner Sync Test
require_clients: 2

wait_clients: 2

# Spawn some beacons
spawn_server: 5
wait: 2

# Ask human to start Client3
human_action: Start Client3
wait_client: 3
wait: 3

# Verify Client3 sees all beacons
verify_beacons: all
log: Late joiner should see all 5 beacons
```

### Example 5: Despawn Test

```
name: Natural Despawn Test
require_clients: 2

wait_clients: 2

# Spawn beacons with 35-second lifetime
spawn_server: 5
wait: 2
verify_beacons: all

# Wait for natural despawn (40 seconds)
wait_despawn: 40

# Verify all despawned
verify_despawned: all
log: All beacons should be gone
```

## Understanding Test Results

### Console Logs

The test executor logs detailed results:

```
[TestExecutor] ✓ PASSED: Verify Beacons (all)
[TestExecutor]   ✓ All 3 beacons exist

[TestExecutor] ❌ FAILED: Verify Despawned (all)
[TestExecutor]   ❌ 2/5 beacons STILL EXIST
```

### Final Summary

```
[TestExecutor] ========================================
[TestExecutor] TEST COMPLETE: My Test Name
[TestExecutor] ========================================
[TestExecutor] ✓ PASS | Verify Beacons (all)
[TestExecutor]        ✓ All 3 beacons exist
[TestExecutor] ❌ FAIL | Verify Despawned (all)
[TestExecutor]        ❌ 2/5 beacons STILL EXIST
[TestExecutor] ========================================
[TestExecutor] TOTAL: 1 passed, 1 failed
[TestExecutor] ========================================
```

### UI Display

During test execution, the UI shows:
- "WAITING FOR CLIENTS - Connected: 1/2" (while waiting)
- "WAITING FOR DESPAWN - Time remaining: 25s" (during despawn wait)
- "HUMAN ACTION REQUIRED - Start Client3 - Press SPACE when complete"
- "TEST COMPLETE - Passed: 5, Failed: 2" (at end)

## Advanced: Reproducing the Spawn Sync Bug

The included `SpawnSyncBugTest.gotest` reproduces the exact bug where Client2's spawn messages stop reaching the server:

```bash
# 1. Start server
./MyGame.exe -server

# 2. Start Client1
./MyGame.exe -client

# 3. Start Client2
./MyGame.exe -client

# 4. Watch test execute
# 5. When prompted, start Client3
./MyGame.exe -client

# 6. Check logs for which verification steps failed
```

Expected failure: "Verify Beacons (all)" will fail after Client2's 3rd-5th spawns, proving those messages never reached the server.

## Troubleshooting

### "No test script assigned"
- Make sure you've assigned a TextAsset to the `Test Script Asset` field
- The TextAsset should contain valid `.gotest` format content

### "Failed to load SpawnTestBeacon prefab"
- Ensure `SpawnTestBeacon.prefab` exists in `Resources/` folder
- Path should be: `Assets/GONet/Sample/Projectile/Resources/SpawnTestBeacon.prefab`

### Test doesn't start
- Check Unity console for parsing errors
- Verify you're running as server (orchestrator only runs on server)
- Make sure `GONetGlobal` is in the scene

### Clients don't connect
- Verify all instances are running (server + clients)
- Check that automatic server/client detection is enabled in GONet settings
- OR manually start server with ALT+S, clients with ALT+C

## Creating Custom Test Commands

You can extend the test script system by:

1. Add new `TestStepType` enum value to `GONetTestScript.cs`
2. Add parsing logic in `ParseStep()`
3. Add execution logic in `GONetTestScriptExecutor.ExecuteStep()`

Example:

```csharp
// In GONetTestScript.TestStepType enum:
TeleportClient,  // teleport_client: 1, x=5, y=2, z=3

// In GONetTestScriptExecutor.ExecuteStep():
case GONetTestScript.TestStepType.TeleportClient:
    yield return Step_TeleportClient(
        step.GetParamInt("client"),
        step.GetParamFloat("x"),
        step.GetParamFloat("y"),
        step.GetParamFloat("z")
    );
    break;
```

## Future Enhancements

Planned features:
- [ ] Gherkin/Cucumber syntax support (Given/When/Then)
- [ ] Async RPC result validation
- [ ] Client-side assertion reporting
- [ ] Test suite runner (multiple .gotest files)
- [ ] Screenshot capture on failure
- [ ] Network traffic analysis integration
- [ ] CI/CD integration (headless mode)

## Philosophy

This system follows the principle: **"If Claude can script it, you can test it"**.

Every test case you write becomes:
- ✅ A reproducible bug report
- ✅ A regression test
- ✅ Documentation of expected behavior
- ✅ A tool for debugging in the future

Happy testing! 🎮
