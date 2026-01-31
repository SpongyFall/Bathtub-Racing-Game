# Steamworks Threading Analysis: RTT Accuracy & High-Load Performance

**Status**: ARCHITECTURAL DECISION DOCUMENT
**Date**: December 2025
**Classification**: High Risk / High Reward

---

## Executive Summary

This document analyzes the feasibility of implementing background thread polling for Steamworks to improve RTT accuracy and responsiveness during high-load scenarios.

**Key Finding**: We do NOT need threading to fix RTT accuracy. Steam already provides accurate arrival timestamps via `m_usecTimeReceived` - we're just not using them.

**Recommendation**: Implement timestamp correction first (no threads), then evaluate if threading is still needed.

---

## The Problem

During high-load scenarios (800 objects syncing, scene loads), RTT measurements become inflated:

```
CURRENT BEHAVIOR:
┌─────────────────────────────────────────────────────────────────┐
│ t=0ms    Server sends time sync packet                          │
│ t=50ms   Steam DLL receives packet, stores in internal buffer   │
│ t=50ms   Steam sets m_usecTimeReceived = 50ms                   │
│ t=200ms  Unity main thread finally calls ReceiveMessages        │
│ t=200ms  GONet processes packet, records t3 = 200ms             │
│                                                                 │
│ RESULT: RTT calculated as 200ms (should be 50ms)                │
└─────────────────────────────────────────────────────────────────┘
```

The RTT includes 150ms of **frame latency** that has nothing to do with the network.

---

## The "Hidden Gem" Solution (NO THREADING REQUIRED)

### The Key Insight

`SteamNetworkingMessage_t.m_usecTimeReceived` contains the timestamp when Steam's internal networking layer **actually received** the packet - NOT when we processed it.

This timestamp is set at t=50ms even if we don't call `ReceiveMessagesOnConnection()` until t=200ms.

### The Zero-Risk Fix

**Current (Wrong) Logic:**
```csharp
// GONet.TimeSync.cs - Current implementation
long t3 = GONetMain.Time.ElapsedTicks;  // When WE processed it (200ms)
long rtt = t3 - t0;  // Includes 150ms of frame lag
```

**Correct Logic:**
```csharp
// Use Steam's internal receive timestamp
long t3_steamUsec = message.m_usecTimeReceived;
long t3 = ConvertSteamTimeToGONetTicks(t3_steamUsec);  // When Steam received it (50ms)
long rtt = t3 - t0;  // Accurate network-only RTT
```

### Clock Synchronization Required

Steam uses `SteamNetworkingUtils.GetLocalTimestamp()` (microseconds since process start).
GONet uses `HighResolutionTimeUtils.UtcNowTicks` (100-nanosecond ticks).

**Synchronization at startup:**
```csharp
// Call this once during Steam initialization
public static class SteamTimeSync
{
    private static long _ticksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000; // 10
    private static long _steamToGONetOffset;

    public static void Initialize()
    {
        // Sample both clocks at the same moment
        long steamUsec = SteamNetworkingUtils.GetLocalTimestamp();
        long gonetTicks = HighResolutionTimeUtils.UtcNowTicks;

        // Calculate offset: GONet = Steam * 10 + offset
        _steamToGONetOffset = gonetTicks - (steamUsec * _ticksPerMicrosecond);
    }

    public static long ConvertSteamTimeToGONetTicks(long steamUsec)
    {
        return (steamUsec * _ticksPerMicrosecond) + _steamToGONetOffset;
    }
}
```

### Expected Result

```
AFTER FIX:
┌─────────────────────────────────────────────────────────────────┐
│ RTT Graph: Flat 50ms line even at 5 FPS                         │
│                                                                 │
│ Status UI could show:                                           │
│   Network RTT: 50ms  (from m_usecTimeReceived)                  │
│   Frame Time:  200ms (processing delay)                         │
│   Total Lag:   250ms (what player actually experiences)         │
└─────────────────────────────────────────────────────────────────┘
```

---

## Why Threading is Dangerous for Steamworks

### Approach A: Initialize Steam on Background Thread - **NON-STARTER**

```
❌ DO NOT DO THIS
```

**Why it fails:**
- `SteamAPI_Init()` hooks into OS process, Steam Overlay renderer, and Input systems
- Steam Overlay requires main thread window handle access
- Will likely crash Unity when Overlay tries to hook the window
- Achievement popups, screenshot notifications, etc. will break

### Approach B: Background Polling - **DANGEROUS**

```
⚠️ PROCEED WITH EXTREME CAUTION
```

**The Race Condition:**
```
┌─────────────────────────────────────────────────────────────────┐
│ MAIN THREAD                    │ BACKGROUND THREAD              │
│ ────────────────────────────── │ ─────────────────────────────  │
│ SteamAPI.RunCallbacks()        │ ReceiveMessagesOnConnection()  │
│ ↓ Checks internal pipes        │ ↓ Checks sockets               │
│ ↓ Touches connection state     │ ↓ Touches connection state     │
│                                │                                │
│ ══════════════ RACE CONDITION ══════════════                    │
└─────────────────────────────────────────────────────────────────┘
```

**The Problem:**
- In Steam's C++ layer, `RunCallbacks` and `ReceiveMessagesOnConnection` often touch the same connection state structures
- The Steamworks DLL is **NOT guaranteed** to be thread-safe for simultaneous polling and callback execution
- The open-source `GameNetworkingSockets` library IS thread-safe, but the Steamworks DLL may not be

**Symptoms of Race Conditions:**
- Random `AccessViolationException` in native code
- Corrupted message data
- Dropped connections
- Crashes that only happen under load (hard to reproduce)

---

## The ACK Problem: Why Threading Alone Doesn't Fix Congestion

Even if background thread polling works perfectly for **reading**, it doesn't fix **backpressure**.

### The Issue: ACKs Still Wait for Main Thread

```
┌─────────────────────────────────────────────────────────────────┐
│ t=0ms    Server sends packet                                    │
│ t=50ms   Background thread receives packet, queues it           │
│ t=50ms   ✓ We know the accurate arrival time!                   │
│ t=200ms  Main thread wakes up, processes queue                  │
│ t=200ms  ACK is generated (by Steam's internal protocol)        │
│ t=250ms  Server receives ACK                                    │
│                                                                 │
│ SERVER CALCULATES: RTT = 250ms → TRIGGERS BACKPRESSURE          │
└─────────────────────────────────────────────────────────────────┘
```

**The Root Cause:**
- `SteamNetworkingSockets` handles ACKs internally
- ACKs are only sent when the internal loop is "pumped"
- `RunCallbacks()` or message processing triggers the pump
- If main thread is blocked, ACKs don't flow

### What Would Actually Fix Congestion

To truly decouple from the main thread, the background thread must:
1. Read the packet
2. **Immediately trigger ACK** (requires calling `RunCallbacks` or `FlushMessagesOnConnection` on the thread)
3. Queue the payload for the game thread

But this creates the race condition problem described above.

---

## Recommended Implementation: The "Pro" Compromise

### Step 1: Implement Accurate RTT (NO THREADS)

**Priority: HIGH | Risk: LOW | Effort: LOW**

Modify the time sync system to use `m_usecTimeReceived`:

```csharp
// In SteamworksTransport.cs - when receiving time sync responses
public void OnMessageReceived(SteamNetworkingMessage_t message)
{
    // Extract Steam's internal receive timestamp
    long steamReceiveUsec = message.m_usecTimeReceived;
    long actualArrivalTicks = SteamTimeSync.ConvertSteamTimeToGONetTicks(steamReceiveUsec);

    // Pass the ACCURATE timestamp to the time sync system
    ProcessTimeSyncResponse(message.Data, actualArrivalTicks);
}

// In GONet.TimeSync.cs - use the passed timestamp instead of "now"
void ProcessTimeSyncResponse(byte[] data, long actualArrivalTicks)
{
    long t3 = actualArrivalTicks;  // NOT Time.ElapsedTicks
    // ... rest of time sync logic
}
```

**Benefit**: Stats overlay shows accurate network RTT even during frame drops.

### Step 2: Implement "KeepAlive" Pattern (Already Done)

**Priority: HIGH | Risk: LOW | Effort: DONE**

We already have `GONetMain.ProcessNetworkEvents()` and `GONetSteamManager.ProcessNetworkEvents()`.

Users call these during heavy operations to keep ACKs flowing:

```csharp
IEnumerator LoadLevel() {
    var asyncOp = GONetSceneManager.LoadSceneAsync("Game");
    while (!asyncOp.isDone) {
        GONetMain.ProcessNetworkEvents();  // Keeps ACKs flowing!
        yield return null;
    }
}
```

### Step 3: Threaded Polling (ONLY IF Steps 1 & 2 Fail)

**Priority: LOW | Risk: HIGH | Effort: HIGH**

Only consider if:
- Steps 1 & 2 don't solve the problem
- You have scenarios with 5+ second main thread blocks
- You can afford extensive cross-platform testing
- You accept the risk of hard-to-debug native crashes

**If you MUST thread:**

1. **Do NOT** use `SteamAPI.RunCallbacks()` on the background thread
2. **Use** `SteamNetworkingSockets.ReceiveMessagesOnConnection()` only
3. **Must** call `SteamNetworkingSockets.FlushMessagesOnConnection()` on the thread to push ACKs
4. **Must** disable Steam's default callback mechanism for that connection to prevent `RunCallbacks` from consuming messages before your thread

```csharp
// EXPERIMENTAL - NOT RECOMMENDED FOR PRODUCTION
class SteamBackgroundPoller
{
    private Thread pollingThread;
    private volatile bool isRunning;
    private ConcurrentQueue<(long arrivalTicks, byte[] data)> messageQueue;

    void ThreadLoop()
    {
        while (isRunning)
        {
            // Receive messages
            IntPtr[] messages = new IntPtr[64];
            int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(
                connection, messages, 64);

            for (int i = 0; i < count; i++)
            {
                var msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(messages[i]);
                long arrivalTicks = SteamTimeSync.ConvertSteamTimeToGONetTicks(msg.m_usecTimeReceived);
                messageQueue.Enqueue((arrivalTicks, CopyData(msg)));
                SteamNetworkingMessage_t.Release(messages[i]);
            }

            // CRITICAL: Flush to send ACKs
            SteamNetworkingSockets.FlushMessagesOnConnection(connection);

            Thread.Sleep(1);  // 1000Hz polling
        }
    }
}
```

---

## Risk Assessment Matrix

| Approach | RTT Accuracy | Backpressure Fix | Risk Level | Effort |
|----------|--------------|------------------|------------|--------|
| Step 1: Timestamp Fix | ✅ Full | ❌ No | 🟢 Low | 1 day |
| Step 2: KeepAlive API | ✅ Full | ✅ Yes (with user cooperation) | 🟢 Low | Done |
| Step 3: Threading | ✅ Full | ✅ Yes (automatic) | 🔴 High | 2-3 weeks |

---

## Implementation Checklist

### Phase 1: Timestamp Correction (DO THIS FIRST)

- [ ] Add `SteamTimeSync` class with clock synchronization
- [ ] Call `SteamTimeSync.Initialize()` after `SteamAPI.Init()`
- [ ] Modify `SteamworksTransport` to extract `m_usecTimeReceived` from messages
- [ ] Pass accurate arrival timestamp to time sync system
- [ ] Update GONet status UI to show "Network RTT" vs "Frame Time" separately
- [ ] Test: Verify RTT stays flat during deliberate frame drops

### Phase 2: User Guidance (DONE)

- [x] `GONetMain.ProcessNetworkEvents()` API
- [x] `GONetSteamManager.ProcessNetworkEvents()` API
- [x] Documentation with GONetSceneManager examples
- [x] Time-sliced instantiation examples

### Phase 3: Threading (IF NEEDED - NOT RECOMMENDED)

- [ ] Create isolated test project first
- [ ] Test `ReceiveMessagesOnConnection` from background thread
- [ ] Test `FlushMessagesOnConnection` from background thread
- [ ] Verify no crashes across Windows/Mac/Linux
- [ ] Implement proper shutdown synchronization
- [ ] Add extensive logging for race condition detection
- [ ] Stress test under extreme load (10,000+ messages/sec)

---

## Final Verdict

**Don't do the thread yet.**

1. **Fix the Math**: Use `m_usecTimeReceived`. Steam already has accurate timestamps - we're just ignoring them.

2. **Fix the Load**: The 800-object spawn is the disease. Threading is a painkiller. Time-sliced instantiation cures the disease.

3. **Evaluate After**: If RTT math is fixed and users follow the KeepAlive pattern, Steamworks will likely perform just fine.

The "lag" we're seeing is primarily:
- **Visual/game-logic side** (processing delay)
- **Measurement error** (using wrong timestamp)

NOT actual network latency.

---

## References

- [Steamworks.NET Documentation](https://steamworks.github.io/)
- [GameNetworkingSockets (Open Source)](https://github.com/ValveSoftware/GameNetworkingSockets)
- [SteamNetworkingMessage_t.m_usecTimeReceived](https://partner.steamgames.com/doc/api/steamnetworkingtypes#SteamNetworkingMessage_t)
- GONet Backpressure System: `GONet.Congestion.cs`
- GONet Time Sync: `GONet.TimeSync.cs`
