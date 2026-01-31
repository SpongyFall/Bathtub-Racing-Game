# ReliableNetcode Test Suite

## Overview

This test suite provides comprehensive unit tests for GONet's ReliableNetcode layer - the critical middleware responsible for routing messages through reliable and unreliable channels. **These tests were specifically created to investigate the "projectile freezing" issue** where projectiles would freeze for 7-9 seconds during spawn bursts.

## Purpose

The ReliableNetcode layer had **ZERO unit tests** prior to this implementation, making it impossible to verify correct behavior under load. This test suite fills that critical gap by providing:

1. **Channel Isolation Tests** - Verify reliable channel load doesn't starve unreliable channel
2. **Spawn Burst Simulation** - Replicate exact real-world projectile freezing scenario
3. **Congestion Control Tests** - Ensure congestion control doesn't block unreliable traffic
4. **Reliable Channel Tests** - Verify ordering guarantees and delivery
5. **Unreliable Channel Tests** - Verify duplicate detection works correctly
6. **Channel Interleaving Tests** - Stress test both channels operating simultaneously

## Test Coverage

### Total Tests: 19
- **Channel Isolation**: 3 tests
- **Spawn Burst Simulation**: 3 tests (CRITICAL for bug reproduction)
- **Congestion Control**: 4 tests
- **Reliable Channel**: 4 tests
- **Unreliable Channel**: 4 tests
- **Channel Interleaving**: 4 tests

## Key Test Files

### ReliableEndpointTestBase.cs
Base class providing common test infrastructure:
- Endpoint pair creation with optional latency simulation
- Test message generation with sequence numbers
- Update cycle management
- Latency tracking and verification helpers
- Message counting by channel type

### ChannelIsolationTests.cs
**Critical for projectile freezing investigation**
- `ReliableFlood_DoesNotBlock_UnreliableChannel` - 100 reliable + 100 unreliable messages
- `MixedTraffic_BothChannels_NoStarvation` - Alternating traffic pattern
- `UnreliableChannel_HighFrequency_NotThrottledByReliable` - 200 high-frequency unreliable during reliable burst

**Purpose**: Detect if reliable channel load blocks unreliable channel (the suspected root cause)

### SpawnBurstTests.cs
**Directly replicates the projectile freezing bug**
- `SpawnBurst50_DoesNotStarve_PositionUpdates` - 50 spawn burst + continuous position updates
- `MultipleSpawnBursts_ContinuousPositionUpdates_NoFreeze` - 5 bursts of 20 spawns each
- `LargeSpawnBurst_100Entities_PositionUpdatesStillFlow` - Stress test with 100 spawns

**Purpose**: Simulate exact scenario from logs where 50+ projectiles spawn and existing projectiles freeze

### CongestionControlTests.cs
Tests congestion control behavior (RTT-based throttling):
- `CongestionControl_WhenActive_DoesNotBlock_Unreliable` - High RTT shouldn't affect unreliable
- `ReliableChannel_ThrottlesDuringCongestion_UnreliableDoesNot` - Verify selective throttling
- `HighRTT_TriggersCongest_UnreliableUnaffected` - >250ms RTT threshold
- `CongestionRecovery_UnreliableRemainsUnaffected` - Full congestion cycle

**Purpose**: Verify congestion control (line 124-127 in ReliableMessageChannel.cs) doesn't starve unreliable

### ReliableChannelTests.cs
Validates reliable channel guarantees:
- `ReliableChannel_GuaranteesOrder` - 100 messages arrive in exact order
- `ReliableChannel_AllMessagesArrive_WithPacketLoss` - 20% packet loss with retransmission
- `ReliableChannel_LargeMessages_ArriveInOrder` - Near-fragmentation threshold messages
- `ReliableChannel_RapidBurst_MaintainsOrder` - 200 messages in single frame

**Purpose**: Ensure reliable channel behavior is correct (not causing side effects)

### UnreliableChannelTests.cs
Validates unreliable channel behavior:
- `UnreliableChannel_IgnoresDuplicates` - Same message sent 5 times, received once
- `UnreliableChannel_AllowsDifferentMessages` - 50 different messages all arrive
- `UnreliableChannel_FastDelivery_NoDuplicates` - 100 messages at 1ms intervals
- `UnreliableChannel_OutOfOrder_AllowedAndDetectedDuplicates` - Resend detection

**Purpose**: Verify duplicate detection (line 60-64 in UnreliableMessageChannel) works correctly

### ChannelInterleavingTests.cs
**Stress tests for simultaneous channel operation**
- `MixedTraffic_1000Messages_BothChannels_NoStarvation` - 500 reliable + 500 unreliable
- `ChannelPriorityTest_UnreliableGetsSlots` - Verify unreliable gets transmission slots
- `BurstyTraffic_BothChannels_NoInterference` - 10 bursts of 20 messages each
- `ContinuousHighFrequency_BothChannels_Sustained` - 500 cycles at 500Hz

**Purpose**: Verify channels can operate simultaneously without interference (core of projectile bug)

## How to Run Tests

### In Unity Editor:
1. Open Unity Test Runner: `Window → General → Test Runner`
2. Click "EditMode" tab
3. Navigate to `GONet.Tests.ReliableNetcode`
4. Click "Run All" or select specific test classes/methods
5. Watch for failures (green = pass, red = fail)

### Expected Results:
✅ **If all tests pass**: ReliableNetcode layer is functioning correctly - projectile freezing has a different root cause
❌ **If tests fail**: Specific failures will indicate the exact nature of channel starvation

## Critical Assertions

### For Projectile Freezing Bug:
Each test includes assertions that would catch the bug:

```csharp
// FAIL = Freezing detected
Assert.AreEqual(0, frozenUpdates,
    "Position updates were frozen (>1s latency) - PROJECTILE FREEZING BUG DETECTED!");

// FAIL = Channel starvation
Assert.GreaterOrEqual(unreliableCount, EXPECTED_COUNT,
    "Unreliable channel starved! Only X/Y messages received");

// FAIL = Latency too high (not technically "frozen" but delayed)
Assert.Less(maxPositionLatency, 1.0,
    "Position update latency too high - indicates channel starvation");
```

## Interpreting Test Failures

### If `SpawnBurstTests` fails:
**Smoking Gun!** This directly replicates the projectile freezing scenario. Failure confirms the bug exists at the ReliableNetcode layer.

**Look for**:
- "FROZEN UPDATE detected! Latency: X.XXs" in test output
- "Position updates were frozen" assertion message
- Gap between position updates > 1 second

### If `ChannelIsolationTests` fails:
**Root cause confirmed**: Reliable channel load is blocking unreliable channel delivery.

**Look for**:
- "Unreliable channel starved!" assertion message
- Unreliable message count << expected count
- High unreliable latency (>500ms)

### If `CongestionControlTests` fails:
**Culprit identified**: Congestion control is throttling unreliable channel when it shouldn't.

**Look for**:
- "Unreliable affected by congestion control" assertion
- Unreliable latency correlating with reliable RTT
- Congestion control active when unreliable blocked

### If `ChannelInterleavingTests` fails:
**Scheduling issue**: Channels aren't being interleaved correctly during Update() cycle.

**Look for**:
- "Channel starved in mixed traffic" assertion
- One channel gets all transmission slots
- High latency on one channel while other is fine

## Telemetry Support

The test suite includes `ReliableEndpointTelemetry.cs` providing real-time monitoring:

```csharp
public struct ReliableEndpointTelemetry
{
    public int ReliableMessagesQueued;          // Messages waiting to send
    public int UnreliableMessagesQueued;        // Should never stay high!
    public float ReliableRTTMs;                 // Round-trip time
    public bool IsCongestionControlActive;      // Throttling enabled?
    public float PacketLoss;                    // 0.0 - 1.0
    public float SentBandwidthKBPS;             // Current throughput
    // ... more fields
}
```

**Future Enhancement**: Add `GetTelemetry()` method to `ReliableEndpoint.cs` to expose this data in production for live debugging.

## Next Steps After Tests

### If Tests Pass:
- ✅ ReliableNetcode layer is NOT the culprit
- 🔍 Investigation must move up the stack to GONet.cs (Layer 3)
- 🔍 Check spawn event batching in InstantiateGONetParticipantEvent handling
- 🔍 Review message bundling in GONet.cs:7483 area

### If Tests Fail:
1. **Identify specific failure** (which test class/method)
2. **Review corresponding source** (ReliableEndpoint.cs, MessageChannel.cs, etc.)
3. **Implement fix** based on failure pattern:
   - Channel starvation → Ensure independent bandwidth allocation
   - Congestion blocking → Disable congestion for unreliable
   - Scheduling issue → Fix Update() loop to interleave channels
4. **Re-run tests** to verify fix
5. **Run full integration test** in GONetSample scene with projectile spawning

## Files Added

```
Assets/GONet/Code/GONet/Editor/UnitTests/ReliableNetcode/
├── ReliableEndpointTestBase.cs           (Base class with helpers)
├── ChannelIsolationTests.cs              (3 tests)
├── SpawnBurstTests.cs                    (3 tests - CRITICAL)
├── CongestionControlTests.cs             (4 tests)
├── ReliableChannelTests.cs               (4 tests)
├── UnreliableChannelTests.cs             (4 tests)
├── ChannelInterleavingTests.cs           (4 tests)
└── README.md                             (This file)

Assets/GONet/Code/ReliableNetcode/
└── ReliableEndpointTelemetry.cs          (Telemetry data structure)
```

## Integration with Investigation Plan

This test suite implements **Phase 1** of the Network Stack Investigation Plan:

✅ **Phase 1.1**: Adapt existing Netcode.IO tests → N/A (already in Unity Test Runner format)
✅ **Phase 1.2**: Create ReliableNetcode test suite → **COMPLETE** (19 tests)

**Next phases**:
- **Phase 2**: Add telemetry UI for in-game monitoring (telemetry struct ready)
- **Phase 3**: Isolate root cause using failing tests as guide
- **Phase 4**: Implement fix based on test results
- **Phase 5**: Validate fix with tests + integration testing

## Test Execution Time

Expected execution time for full suite: **~30-60 seconds**
- Most tests run 100-500 update cycles
- Some stress tests run 1000+ messages
- No actual network I/O (in-memory simulation)

## Maintenance

**When to update tests**:
- If ReliableNetcode API changes (constructor, Update() signature, etc.)
- If new QoS types are added beyond Reliable/Unreliable
- If congestion control algorithm changes (currently RTT > 250ms threshold)
- If fragment/packet size thresholds change

**How to add new tests**:
1. Extend `ReliableEndpointTestBase` if you need common infrastructure
2. Add `[TestFixture]` attribute to new test class
3. Add `[Test]` attribute to each test method
4. Use `LogTestProgress()` for debugging output
5. Follow existing assertion patterns for consistency

## Known Limitations

1. **No actual network I/O**: Tests use in-memory callbacks (acceptable for logic testing)
2. **No thread safety testing**: ReliableNetcode is single-threaded by design
3. **No fragmentation testing**: Large message tests exist but don't verify internal fragmentation
4. **No packet reordering simulation**: Latency is consistent, not variable

These limitations are acceptable for the current investigation goal (detect channel starvation).

## References

- **Investigation Plan**: D:/projects/unity/gonet-git/INVESTIGATION_PLAN.md
- **CLAUDE.md**: Project architecture documentation
- **ReliableNetcode Source**: Assets/GONet/Code/ReliableNetcode/
- **Test Logs**: Latest test from 2025-10-07 15:01 showing 7-9 second freezes

## Summary

This comprehensive test suite provides the foundation for systematically investigating the projectile freezing bug. By testing the ReliableNetcode layer in isolation, we can definitively determine whether channel starvation is occurring at this level or if the issue originates higher up in the GONet stack.

**The tests are designed to fail if the bug exists** - a passing test suite means we must look elsewhere for the root cause.

---

*Created: 2025-10-07*
*Author: Claude Code*
*Purpose: Phase 1 of Network Stack Investigation Plan*
