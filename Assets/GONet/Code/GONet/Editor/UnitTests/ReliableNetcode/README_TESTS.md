# ReliableNetcode Channel Starvation Tests

## Test Suite Overview

This test suite validates the **channel starvation fix** that prevents 7-9 second projectile freezes during spawn bursts.

**Fix Location:** `Assets/GONet/Code/ReliableNetcode/MessageChannel.cs` (lines 195-225)

**Fix Summary:** Dequeue up to 100 messages per update (was 1) with 0.5ms time budget protection.

## IMPORTANT: Testing Limitations

⚠️ **Full dequeue testing requires network ACKs**

The dequeue logic (`messageQueue → sendBuffer`) only runs when:
- `sendBufferSize < sendBuffer.Size` (line 214 in MessageChannel.cs)
- ACKs from network remove messages from sendBuffer, freeing space
- Without ACKs, sendBuffer stays full → dequeue never runs

**Therefore, these unit tests validate:**
1. ✅ Fix constants are present (MAX_DEQUEUE_PER_UPDATE=100, MAX_DEQUEUE_TIME_MS=0.5)
2. ✅ Queue overflow behavior (messages queue when sendBuffer is full)
3. ✅ Architecture assumptions (sendBuffer size=256)

**Integration testing required for:**
- ❌ Actual dequeue rate (requires real client/server with ACKs)
- ❌ Time budget enforcement (requires real network load)
- ❌ End-to-end spawn burst scenario (requires GONetSandbox)

## Running the Tests

### Option 1: Unity Test Runner (Recommended)

1. Open Unity Editor
2. Window → General → Test Runner
3. Select "EditMode" tab
4. Expand "GONet.Tests.ReliableNetcode"
5. Expand "ReliableNetcodeStarvationTests"
6. Click "Run All" (should see 3 tests pass)

### Option 2: Command Line

```bash
# From Unity project root
Unity.exe -runTests -testPlatform EditMode -testFilter "GONet.Tests.ReliableNetcode.ReliableNetcodeStarvationTests"
```

## Test Descriptions

### Test 1: TestFixConstants_ArePresent ✅

**Purpose:** Verify fix constants exist in source code

**What it does:**
- Reads MessageChannel.cs source code
- Searches for MAX_DEQUEUE_PER_UPDATE = 100
- Searches for MAX_DEQUEUE_TIME_MS = 0.5
- Verifies dequeue while loop exists
- Verifies sendBufferSize caching optimization

**Expected Result:**
- ✅ All constants found in source

**Pass Criteria:**
```
Assert: sourceCode.Contains("MAX_DEQUEUE_PER_UPDATE = 100")
Assert: sourceCode.Contains("MAX_DEQUEUE_TIME_MS = 0.5")
Assert: sourceCode.Contains("while (messageQueue.Count > 0 &&")
Assert: sourceCode.Contains("sendBufferSize++;  // Track locally")
```

### Test 2: TestMessageQueue_OverflowsBehavior ✅

**Purpose:** Verify messages overflow into messageQueue when sendBuffer is full

**What it does:**
- Creates standalone ReliableEndpoint
- Sends 300 messages (exceeds sendBuffer size of 256)
- Uses reflection to inspect messageQueue.Count
- Validates ~44 messages queued (300 - 256)

**Expected Result:**
- ✅ 40-50 messages in queue

**Pass Criteria:**
```
Assert: messageQueue.Count >= 40
Assert: messageQueue.Count <= 50
```

### Test 3: TestSendBuffer_SizeIs256 ✅

**Purpose:** Verify sendBuffer size architectural assumption

**What it does:**
- Reads MessageChannel.cs source code
- Searches for `sendBuffer = new SequenceBuffer<BufferedPacket>(256)`

**Expected Result:**
- ✅ sendBuffer initialized with size 256

**Pass Criteria:**
```
Assert: sourceCode.Contains("sendBuffer = new SequenceBuffer<BufferedPacket>(256)")
```

## Interpreting Test Results

### All 3 Tests Pass ✅
**Fix constants are present!** Proceed to integration testing with GONetSandbox.

### Test 1 Fails ❌
**Fix constants missing or modified incorrectly.** Check MessageChannel.cs lines 195-225.

### Test 2 Fails ❌
**Queue overflow behavior broken.** SendMessage() logic may have changed.

### Test 3 Fails ❌
**sendBuffer size changed.** Test assumptions need updating.

## Integration Testing (Required for Full Validation)

Unit tests validate fix presence, NOT runtime behavior. To validate fix effectiveness:

### 1. GONetSandbox Testing

**Setup:**
```
1. Open GONetSandbox sample scene
2. Run as server (1 instance)
3. Connect as client (1+ instances)
4. Spawn 50+ projectiles rapidly on server
```

**Expected Behavior (WITH FIX):**
- ✅ All clients see projectiles move immediately
- ✅ No 7-9 second freeze
- ✅ Frame times stay under 33ms @ 60 FPS

**Expected Behavior (WITHOUT FIX - old code):**
- ❌ Projectiles freeze for 7-9 seconds
- ❌ Then suddenly "wake up" and move
- ❌ Logs show 158s+ delays for Ch=6 messages

### 2. Performance Profiling

**Unity Profiler:**
```
1. Profiler → Scripting
2. Find ReliableMessageChannel.Update()
3. Check execution time per frame
4. Should be <0.5ms per frame (time budget)
```

**Expected:**
- With 200-message queue: ~2 updates to clear (100 msgs/update)
- Frame time overhead: 0.1-0.5ms (negligible)

### 3. Network Conditions

Test fix under adverse conditions:
- High latency: 250ms+ (congestion mode active)
- Packet loss: 5-10%
- Multiple clients: 4+ simultaneous

**Expected:**
- Fix still effective (may take slightly longer)
- No freezes, just smooth degradation

### 4. Log Analysis

**Check for:**
```
grep "Ch=6" logs/gonet.log | grep "delay"
```

**Expected (WITH FIX):**
- Delays: <500ms for spawn messages
- No 158-second delays like before

**Expected (WITHOUT FIX):**
- Delays: 2-20+ seconds
- Queue starvation evident in timestamps

## Performance Expectations

### With Fix (MAX_DEQUEUE_PER_UPDATE=100)
- **Dequeue rate:** 100 messages per Update()
- **200-message burst:** ~2 updates = 33ms @ 60 FPS
- **Frame time:** Protected by 0.5ms budget
- **CPU overhead:** Negligible (~0.1-0.5ms per frame)

### Without Fix (Before - 1 msg/update)
- **Dequeue rate:** 1 message per Update()
- **200-message burst:** 200 updates = 3.3s @ 60 FPS
- **Frame time:** No protection
- **Result:** 7-9 second freezes in production

### Comparison Table

| Scenario | Old Code | New Code (100x) | Improvement |
|----------|----------|-----------------|-------------|
| 200-msg burst | 3.3s | 0.03s | **100× faster** |
| 400-msg burst | 6.6s | 0.06s | **100× faster** |
| Frame time | Unbounded | <0.5ms | Protected |
| CPU usage | Minimal | Minimal | No regression |

## Configuration Tuning

If integration tests show performance issues:

**Increase dequeue rate (more aggressive):**
```csharp
const int MAX_DEQUEUE_PER_UPDATE = 200;  // Was 100
```

**Increase time budget (allow more CPU):**
```csharp
const double MAX_DEQUEUE_TIME_MS = 1.0;  // Was 0.5
```

**Decrease for lower-end devices:**
```csharp
const int MAX_DEQUEUE_PER_UPDATE = 50;  // Was 100
const double MAX_DEQUEUE_TIME_MS = 0.25;  // Was 0.5
```

## Debugging Integration Tests

### Enable Verbose Logging

**Add to MessageChannel.cs Update() method:**
```csharp
if (dequeuedCount > 0) {
    UnityEngine.Debug.Log($"Dequeued {dequeuedCount} msgs in {stopwatch.Elapsed.TotalMilliseconds:F2}ms (queue: {messageQueue.Count} remaining)");
}
```

**Check GONet logs:**
```
tail -f logs/gonet.log | grep "Dequeued"
```

### Monitor Frame Times

**Unity Profiler:**
- Profiler → CPU Usage
- Look for spikes during spawn bursts
- Should stay <33ms @ 60 FPS

### Check Network Stats

**GONet Statistics:**
```csharp
ReliableEndpoint endpoint = ...;
Debug.Log($"RTT: {endpoint.RTTMilliseconds}ms, PacketLoss: {endpoint.PacketLoss}%");
```

**Expected:**
- RTT: <100ms (local network)
- Packet Loss: <5%

## Reference Documentation

- **Root Cause Analysis:** `FINDINGS.md` (479 lines, complete investigation)
- **Fix Implementation:** `MessageChannel.cs` lines 195-225
- **Test Infrastructure:** `ReliableNetcodeTestBase.cs`
- **Original Issue:** Projectile freeze during spawn bursts (158s delay in logs)
- **Fix Commit:** `3bb9f175` (Eliminate reliable channel starvation)
- **Test Commit:** `fa7166e9` (Add comprehensive unit tests)
- **Simplified Tests:** `bc74ea4a` (Validate fix constants, not runtime)

## Contact

Questions about tests or fix? Reference:
- Analysis: `FINDINGS.md`
- Fix: MessageChannel.cs:195-225
- Tests: This file + ReliableNetcodeStarvationTests.cs

## Summary

These unit tests validate that the fix is **present and correctly configured**.

**Integration testing with GONetSandbox is REQUIRED** to validate fix effectiveness in real-world scenarios with actual network ACKs.

The fix is architecturally sound (validated by code review). Unit tests provide regression protection against accidental removal/modification of fix constants.
