# Test Orchestration Setup - Step by Step

## ⚡ Quick Setup (Do this NOW in Unity Editor)

### Step 1: Open GONetSample Scene
1. In Unity, open `Assets/GONet/Sample/GONetSample.unity`

### Step 2: Create Test Executor GameObject
1. In Hierarchy, create empty GameObject: Right-click → `Create Empty`
2. Rename it to: **"TestExecutor"**
3. Position doesn't matter (it's code-only)

### Step 3: Add GONetRuntimeComponentInitializer
1. With TestExecutor selected, click `Add Component`
2. Search for: **GONetRuntimeComponentInitializer**
3. Click to add it

### Step 4: Configure the Initializer
In the GONetRuntimeComponentInitializer component:
1. **Component Type Name**: Enter exactly → `GONet.Sample.GONetTestScriptExecutor`
2. **Remove On Scene Unload**: Set to → `false` (unchecked)
   - This makes it persist across scene changes (important!)

### Step 5: Add the Test Script
1. In the Project panel, navigate to: `Assets/GONet/Sample/Tests/`
2. Find the file: **SpawnSyncBugTest.txt** (Unity shows it as TextAsset)
3. In Hierarchy, select your **TestExecutor** GameObject
4. The GONetRuntimeComponentInitializer will have shown an error saying it couldn't find the component
5. **WAIT** - We need to compile first!

### Step 6: Wait for Compilation
1. Unity should auto-compile the new C# scripts
2. Watch the bottom-right corner - wait for the spinning progress icon to finish
3. This may take 10-30 seconds
4. **If you see compilation errors**, let me know immediately!

### Step 7: Verify Component Initialization
After compilation completes:
1. Select **TestExecutor** GameObject again
2. The GONetRuntimeComponentInitializer should now show:
   - ✅ "Component Type Name: GONet.Sample.GONetTestScriptExecutor" (no error)
   - A new section appeared with "Test Script Executor" fields

### Step 8: Assign the Test Script
1. With TestExecutor still selected
2. Look for the **GONetTestScriptExecutor** component (should be auto-added by initializer on play)
3. **OR** if you don't see it yet (normal - it adds at runtime), just note the script location
4. We'll verify it loads correctly when we run

### Step 9: Save the Scene
1. `File > Save` or `Ctrl+S`
2. Scene is ready!

---

## 🚀 Running the Test

### Build the Project (One-time)

1. `File > Build Settings`
2. Verify scenes are in this order:
   - ✅ GONetSample
   - ✅ JustAnotherScene
   - ✅ ProjectileTest
3. Click **Build**
4. Choose output folder (e.g., `D:/projects/unity/gonet-git/Builds/`)
5. Wait for build to complete (~2-5 minutes)

### Run the Test

**Terminal 1 - Server:**
```bash
cd D:/projects/unity/gonet-git/Builds/
./GONetSandbox.exe -server
```

**Terminal 2 - Client1:**
```bash
cd D:/projects/unity/gonet-git/Builds/
./GONetSandbox.exe -client
```

**Terminal 3 - Client2:**
```bash
cd D:/projects/unity/gonet-git/Builds/
./GONetSandbox.exe -client
```

### What You'll See

1. **Server window**:
   - UI text: "WAITING FOR CLIENTS - Connected: 0/2"
   - Changes to: "Connected: 1/2" when Client1 joins
   - Disappears and test starts when Client2 joins

2. **Test executes automatically**:
   - Watch console logs scroll
   - You'll see: "TEST 1 - Server spawns", "TEST 2 - Scene change", etc.
   - Beacons spawn and move around

3. **Human Action Required** (after ~90 seconds):
   - UI appears: "HUMAN ACTION REQUIRED - Start Client3 now - Press SPACE when complete"

**Terminal 4 - Client3:**
```bash
cd D:/projects/unity/gonet-git/Builds/
./GONetSandbox.exe -client
```

4. **Press SPACE** in any window when Client3 is running

5. **Test Completes**:
   - Final UI: "TEST COMPLETE - Passed: X - Failed: Y"
   - Check server console for detailed results

### Expected Results

**If bug is still present:**
```
[TestExecutor] ✓ PASS | TEST 1 - Server spawns
[TestExecutor] ✓ PASS | TEST 2 - Scene change
[TestExecutor] ✓ PASS | TEST 3 - Server spawns in ProjectileTest
[TestExecutor] ✓ PASS | TEST 4 - Client1 spawns
[TestExecutor] ❌ FAIL | TEST 5 - Verify beacons (Client2 spawns)
[TestExecutor]   ❌ 3/5 beacons MISSING
[TestExecutor] ❌ FAIL | TEST 8 - Verify despawned
[TestExecutor]   ❌ 3/5 beacons STILL EXIST
```

**If bug is fixed:**
```
[TestExecutor] ✓ PASS | All tests
[TestExecutor] TOTAL: 9 passed, 0 failed
```

---

## 📋 Checklist Before Running

- [ ] GONetSample.unity scene open
- [ ] TestExecutor GameObject exists in scene
- [ ] GONetRuntimeComponentInitializer component added
- [ ] Component Type Name = `GONet.Sample.GONetTestScriptExecutor`
- [ ] Remove On Scene Unload = false
- [ ] No compilation errors in Unity Console
- [ ] Scene saved
- [ ] Project built successfully
- [ ] Build output folder ready

---

## ⚠️ Troubleshooting

### "Component Type Name not found"
- **Wait for compilation** - Unity needs to compile new scripts first
- Check Console for compilation errors
- If errors exist, let Claude know!

### "Test doesn't start"
- Check server console for: `[TestExecutor] Loaded test: Spawn Synchronization Bug Test`
- If missing, the TextAsset wasn't assigned/loaded
- Verify SpawnSyncBugTest.txt exists in Tests folder

### "Failed to load SpawnTestBeacon prefab"
- Verify: `Assets/GONet/Sample/Projectile/Resources/SpawnTestBeacon.prefab` exists
- This should already exist from earlier work

### "Test skips verification steps"
- This is expected if running on client
- Only the SERVER runs the orchestrator
- Clients just respond to RPC commands

---

## 🎯 Next Steps

After running this test:
1. Copy the test results from console
2. Share with Claude for analysis
3. We can create more targeted tests based on what we find
4. Or fix the bug and re-run to verify!

Ready? Let's do this! 🚀
