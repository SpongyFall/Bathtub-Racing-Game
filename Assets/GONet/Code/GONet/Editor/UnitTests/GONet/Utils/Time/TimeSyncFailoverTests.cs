using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;
using static GONet.GONetMain;

namespace GONet.Tests.Time
{
    /// <summary>
    /// Tests for time synchronization behavior during host failover.
    ///
    /// SCENARIO:
    /// - Original Server (authority 1023) has time T_server
    /// - Client 1 (authority 1) has offset O1 from server: Client1_time = T_server + O1
    /// - Client 2 (authority 2) has offset O2 from server: Client2_time = T_server + O2
    ///
    /// After failover:
    /// - Client 1 becomes new server (now authority 1023)
    /// - Client 1's local time becomes the new "server time"
    /// - Client 2 needs to re-sync to Client 1's time base
    ///
    /// PROBLEM:
    /// - If Client 1 uses its raw local time as server time, Client 2's offset is now wrong
    /// - Client 2 was calibrated to (T_server + O2), but new server time is (T_server + O1)
    /// - Result: Client 2's perceived time is off by (O1 - O2) = ~5 seconds in observed case
    ///
    /// SOLUTION (Option C):
    /// - Client 1 should preserve its server offset when becoming host
    /// - When responding to time sync, use: local_time + preserved_server_offset
    /// - This maintains time continuity for all other clients
    /// </summary>
    [TestFixture]
    [Timeout(30000)]
    public class TimeSyncFailoverTests : TimeSyncTestBase
    {
        private SecretaryOfTemporalAffairs originalServerTime;
        private SecretaryOfTemporalAffairs client1Time;
        private SecretaryOfTemporalAffairs client2Time;

        // Simulated offsets (in ticks)
        private const long CLIENT1_OFFSET_SECONDS = 3; // Client 1 is 3 seconds behind server
        private const long CLIENT2_OFFSET_SECONDS = 8; // Client 2 is 8 seconds behind server
        private static readonly long CLIENT1_OFFSET_TICKS = CLIENT1_OFFSET_SECONDS * TimeSpan.TicksPerSecond;
        private static readonly long CLIENT2_OFFSET_TICKS = CLIENT2_OFFSET_SECONDS * TimeSpan.TicksPerSecond;

        [SetUp]
        public void Setup()
        {
            base.BaseSetUp();

            // Create three SecretaryOfTemporalAffairs instances to simulate server and clients
            originalServerTime = new SecretaryOfTemporalAffairs();
            client1Time = new SecretaryOfTemporalAffairs();
            client2Time = new SecretaryOfTemporalAffairs();

            // Initialize all
            originalServerTime.Update();
            client1Time.Update();
            client2Time.Update();
        }

        [TearDown]
        public void TearDown()
        {
            base.BaseTearDown();
        }

        /// <summary>
        /// Demonstrates the PROBLEM: When Client 1 becomes host without preserving time offset,
        /// Client 2's synchronized time becomes incorrect.
        ///
        /// Uses pure mathematical simulation with fixed values to ensure deterministic results.
        /// </summary>
        [Test]
        [Category("FailoverTimeSync")]
        public void Problem_WhenClient1BecomesHost_WithoutPreservingOffset_Client2TimeIsWrong()
        {
            // ARRANGE: Use mathematical simulation with fixed values
            // All clients were synced to the SAME server time before failover

            // Server time T_s = 1000 seconds (arbitrary baseline)
            long T_s = 1000 * TimeSpan.TicksPerSecond;

            // Client 1's local time is BEHIND server by CLIENT1_OFFSET (3s)
            // So: T_1 + O_1 = T_s => T_1 = T_s - O_1 = 997s
            long T_1 = T_s - CLIENT1_OFFSET_TICKS;
            long O_1 = CLIENT1_OFFSET_TICKS;

            // Client 2's local time is BEHIND server by CLIENT2_OFFSET (8s)
            // So: T_2 + O_2 = T_s => T_2 = T_s - O_2 = 992s
            long T_2 = T_s - CLIENT2_OFFSET_TICKS;
            long O_2 = CLIENT2_OFFSET_TICKS;

            // VERIFY: Before failover, both clients perceive the same server time
            long client1PerceivedServerTime = T_1 + O_1;
            long client2PerceivedServerTime = T_2 + O_2;

            Assert.AreEqual(T_s, client1PerceivedServerTime, "Client 1 should perceive correct server time");
            Assert.AreEqual(T_s, client2PerceivedServerTime, "Client 2 should perceive correct server time");

            Debug.Log($"[BEFORE FAILOVER]");
            Debug.Log($"  Server time: {T_s / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  Client 1: local={T_1 / (double)TimeSpan.TicksPerSecond:F1}s + offset={O_1 / (double)TimeSpan.TicksPerSecond:F1}s = {client1PerceivedServerTime / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  Client 2: local={T_2 / (double)TimeSpan.TicksPerSecond:F1}s + offset={O_2 / (double)TimeSpan.TicksPerSecond:F1}s = {client2PerceivedServerTime / (double)TimeSpan.TicksPerSecond:F1}s");

            // ACT: Simulate failover - Client 1 becomes host WITHOUT preserving offset
            // BAD APPROACH: Client 1 uses its RAW local time as "server time"
            long newServerTimeTicks_Bad = T_1; // This is WRONG! Should be T_1 + O_1

            // Client 2 still has its old offset, so its perceived server time is still:
            long client2StillPerceivedServerTime = T_2 + O_2; // = T_s = 1000s

            // ASSERT: Client 2's perceived time is now WRONG relative to new server
            // Client 2 thinks server time is 1000s, but new server says it's 997s
            long timeDiscrepancy = client2StillPerceivedServerTime - newServerTimeTicks_Bad;
            double discrepancySeconds = timeDiscrepancy / (double)TimeSpan.TicksPerSecond;

            Debug.Log($"[AFTER FAILOVER - BAD APPROACH]");
            Debug.Log($"  New server time (Client 1 raw): {newServerTimeTicks_Bad / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  Client 2 perceived server time: {client2StillPerceivedServerTime / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  TIME DISCREPANCY: {discrepancySeconds:F1}s");

            // The discrepancy equals Client 1's original offset (3 seconds)
            // Because: T_s - T_1 = T_s - (T_s - O_1) = O_1
            double expectedDiscrepancy = CLIENT1_OFFSET_SECONDS;
            Assert.AreEqual(expectedDiscrepancy, discrepancySeconds, 0.1,
                $"Time discrepancy should be ~{expectedDiscrepancy}s (Client 1's original offset)");
        }

        /// <summary>
        /// Demonstrates the SOLUTION: When Client 1 becomes host and preserves its server offset,
        /// Client 2's synchronized time remains correct.
        ///
        /// Uses pure mathematical simulation with fixed values to ensure deterministic results.
        /// </summary>
        [Test]
        [Category("FailoverTimeSync")]
        public void Solution_WhenClient1BecomesHost_PreservingOffset_Client2TimeIsCorrect()
        {
            // ARRANGE: Use mathematical simulation with fixed values (same as problem test)
            // Server time T_s = 1000 seconds (arbitrary baseline)
            long T_s = 1000 * TimeSpan.TicksPerSecond;

            // Client 1: T_1 = 997s, O_1 = 3s => perceived = 1000s
            long T_1 = T_s - CLIENT1_OFFSET_TICKS;
            long O_1 = CLIENT1_OFFSET_TICKS;

            // Client 2: T_2 = 992s, O_2 = 8s => perceived = 1000s
            long T_2 = T_s - CLIENT2_OFFSET_TICKS;
            long O_2 = CLIENT2_OFFSET_TICKS;

            Debug.Log($"[BEFORE FAILOVER]");
            Debug.Log($"  Server time: {T_s / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  Client 1: local={T_1 / (double)TimeSpan.TicksPerSecond:F1}s + offset={O_1 / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  Client 2: local={T_2 / (double)TimeSpan.TicksPerSecond:F1}s + offset={O_2 / (double)TimeSpan.TicksPerSecond:F1}s");

            // ACT: Simulate failover - Client 1 becomes host WITH preserved offset
            // GOOD APPROACH: Client 1 uses local_time + preserved_offset as "server time"
            long newServerTimeTicks_Good = T_1 + O_1; // = 997 + 3 = 1000s

            // Client 2 still has its old offset, so its perceived server time is:
            long client2PerceivedServerTime = T_2 + O_2; // = 992 + 8 = 1000s

            // ASSERT: Both should be exactly equal (deterministic math)
            long timeDiscrepancy = Math.Abs(client2PerceivedServerTime - newServerTimeTicks_Good);
            double discrepancySeconds = timeDiscrepancy / (double)TimeSpan.TicksPerSecond;

            Debug.Log($"[AFTER FAILOVER - GOOD APPROACH]");
            Debug.Log($"  New server time (Client 1 + preserved offset): {newServerTimeTicks_Good / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  Client 2 perceived server time: {client2PerceivedServerTime / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  Time discrepancy: {discrepancySeconds:F3}s");

            // With preserved offset, both should be exactly equal (both = T_s = 1000s)
            Assert.AreEqual(0.0, discrepancySeconds, 0.001,
                "With preserved offset, Client 2's time should EXACTLY match new server's time");
        }

        /// <summary>
        /// Test that verifies HighPerfTimeSync.ProcessTimeSync correctly adjusts client time
        /// when given a server timestamp.
        ///
        /// This is a simpler integration test that just verifies the time sync mechanism works.
        /// The mathematical proof of offset preservation is in the other tests.
        /// </summary>
        [Test]
        [Category("FailoverTimeSync")]
        public void Integration_TimeSyncWithPreservedOffset_MaintainsCorrectClientTime()
        {
            // ARRANGE: Setup time instances
            Thread.Sleep(100); // Allow some initial time to pass
            client1Time.Update();
            client2Time.Update();

            // Record the server time we'll send (simulating new host with preserved offset)
            long client1PreservedOffset = CLIENT1_OFFSET_TICKS;
            long serverTimeAtRequest = client1Time.ElapsedTicks + client1PreservedOffset;

            // Create request from client2's perspective
            var request = CreateTimeSyncRequest();
            Thread.Sleep(20); // Simulate network RTT

            Debug.Log($"[TIME SYNC FLOW - GOOD APPROACH]");
            Debug.Log($"  Server time sent: {serverTimeAtRequest / (double)TimeSpan.TicksPerSecond:F3}s");
            Debug.Log($"  Client 2 time before sync: {client2Time.ElapsedTicks / (double)TimeSpan.TicksPerSecond:F3}s");

            // Process the time sync on Client 2 - this should adjust client2Time
            HighPerfTimeSync.ProcessTimeSync(
                request.UID,
                serverTimeAtRequest,
                request,
                client2Time,
                true // Force adjustment for test
            );

            Thread.Sleep(50); // Allow time sync to settle
            client2Time.Update();

            // After sync, client2Time.ElapsedTicks should be close to what the server time
            // would be NOW (accounting for elapsed time since we sent serverTimeAtRequest)
            long client2SyncedTime = client2Time.ElapsedTicks;

            Debug.Log($"  Client 2 time after sync: {client2SyncedTime / (double)TimeSpan.TicksPerSecond:F3}s");

            // The key verification: client2's synced time should be reasonably close to
            // the server time we sent (plus elapsed time). Since ~70ms elapsed (20+50),
            // and we sent serverTimeAtRequest, client2 should now be around that value.
            //
            // More importantly, the offset should be approximately CLIENT1_OFFSET_TICKS
            // (since client2 started at ~0 and server time was ~3s)
            double client2TimeSeconds = client2SyncedTime / (double)TimeSpan.TicksPerSecond;
            double expectedMinTime = CLIENT1_OFFSET_SECONDS - 0.5; // Allow 500ms tolerance

            Debug.Log($"  Expected client2 time to be >= {expectedMinTime:F1}s (server offset minus tolerance)");

            // Client 2 should now be synced to approximately the server's time base
            // (which includes the 3 second offset)
            Assert.GreaterOrEqual(client2TimeSeconds, expectedMinTime,
                $"Client 2 should be synced to server time base (>= {expectedMinTime}s)");
        }

        /// <summary>
        /// Verifies that PreserveTimeOffsetForFailover anchors to an authoritative commit tick
        /// instead of relying on potentially stale client offsets.
        /// </summary>
        [Test]
        [Category("FailoverTimeSync")]
        public void PreserveTimeOffsetForFailover_UsesCommitTickAnchor()
        {
            // Arrange
            GONetMain.Time.ResetTimeBaseline();
            long commitTick = 1000 * TimeSpan.TicksPerSecond;
            long oneWayDelayTicks = 25 * TimeSpan.TicksPerMillisecond;

            long rawBefore = GONetMain.Time.RawElapsedTicks;

            // Act
            MethodInfo preserveMethod = typeof(GONetMain).GetMethod(
                "PreserveTimeOffsetForFailover",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(long), typeof(long), typeof(string) },
                null);
            Assert.NotNull(preserveMethod, "PreserveTimeOffsetForFailover overload not found");
            preserveMethod.Invoke(null, new object[] { commitTick, oneWayDelayTicks, "test" });

            long rawAfter = GONetMain.Time.RawElapsedTicks;
            FieldInfo offsetField = typeof(GONetMain).GetField(
                "failoverPreservedServerOffset",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(offsetField, "failoverPreservedServerOffset field not found");
            long preservedOffset = (long)offsetField.GetValue(null);

            // Assert: offset computed from commit tick +/- the small elapsed window
            long minExpected = commitTick + oneWayDelayTicks - rawAfter;
            long maxExpected = commitTick + oneWayDelayTicks - rawBefore;
            Assert.That(preservedOffset, Is.InRange(minExpected, maxExpected),
                $"Preserved offset should be derived from commit tick. Expected range: [{minExpected}, {maxExpected}], actual: {preservedOffset}");
        }

        /// <summary>
        /// Test that demonstrates the TIME JUMP when we DON'T preserve the offset during failover.
        /// Uses pure mathematical simulation for deterministic results.
        ///
        /// The key insight: if Client 2 was synced to server time T_s with offset O_2,
        /// and the new host uses raw local time T_1 instead of T_1 + O_1,
        /// then Client 2 will experience a time jump of O_1 (Client 1's original offset).
        /// </summary>
        [Test]
        [Category("FailoverTimeSync")]
        public void Integration_TimeSyncWithoutPreservedOffset_ShowsTimeDiscrepancy()
        {
            // ARRANGE: Use mathematical simulation for deterministic results
            // Server time T_s = 1000 seconds
            long T_s = 1000 * TimeSpan.TicksPerSecond;

            // Client 1: local T_1 = 997s (3s behind server)
            long T_1 = T_s - CLIENT1_OFFSET_TICKS;

            // Client 2: local T_2 = 992s, offset O_2 = 8s => perceived = 1000s
            long T_2 = T_s - CLIENT2_OFFSET_TICKS;
            long O_2 = CLIENT2_OFFSET_TICKS;

            // BAD: New host uses raw local time (no preserved offset)
            long newHostResponseTime_Bad = T_1; // 997s - WRONG!

            // Client 2's perceived server time (before re-sync)
            long client2OldPerceivedServerTime = T_2 + O_2; // = 1000s

            Debug.Log($"[BAD TIME SYNC FLOW - No Preserved Offset]");
            Debug.Log($"  New host raw time: {newHostResponseTime_Bad / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  Client 2 perceived time: {client2OldPerceivedServerTime / (double)TimeSpan.TicksPerSecond:F1}s");

            // The difference represents the "jump" Client 2 would experience
            // Jump = T_s - T_1 = O_1 = 3 seconds
            long jumpTicks = client2OldPerceivedServerTime - newHostResponseTime_Bad;
            double jumpSeconds = jumpTicks / (double)TimeSpan.TicksPerSecond;

            Debug.Log($"  TIME JUMP Client 2 experiences: {jumpSeconds:F1}s (= Client 1's offset)");

            // Expected jump is Client 1's original offset
            double expectedJump = CLIENT1_OFFSET_SECONDS;
            Assert.AreEqual(expectedJump, jumpSeconds, 0.1,
                $"Time jump should be {expectedJump}s (Client 1's original offset)");
        }

        /// <summary>
        /// Verifies the mathematical relationship between offsets during failover.
        /// </summary>
        [Test]
        [Category("FailoverTimeSync")]
        public void Math_OffsetPreservation_MaintainsTimeContinuity()
        {
            // Let's define:
            // T_s = original server's local time
            // T_1 = Client 1's local time
            // T_2 = Client 2's local time
            // O_1 = Client 1's sync offset (what Client 1 adds to get server time)
            // O_2 = Client 2's sync offset (what Client 2 adds to get server time)

            // Before failover:
            // Client 1's perceived server time: T_1 + O_1 ≈ T_s
            // Client 2's perceived server time: T_2 + O_2 ≈ T_s

            // After failover (Client 1 becomes host):
            // New "server time" should be: T_1 + O_1 (to maintain continuity)
            // Client 2's perceived server time is still: T_2 + O_2

            // For Client 2 to remain in sync:
            // T_1 + O_1 ≈ T_2 + O_2
            // This was already true before failover!

            // If Client 1 uses raw T_1 as server time:
            // New "server time" = T_1
            // Client 2 perceived = T_2 + O_2 ≈ T_1 + O_1
            // Discrepancy = O_1 (Client 2 is ahead by O_1)

            long T_1 = 1000 * TimeSpan.TicksPerSecond; // Client 1 local time = 1000s
            long T_2 = 1002 * TimeSpan.TicksPerSecond; // Client 2 local time = 1002s
            long O_1 = CLIENT1_OFFSET_TICKS; // Client 1 offset = 3s
            long O_2 = CLIENT2_OFFSET_TICKS; // Client 2 offset = 8s

            // Before failover - both should have same perceived server time
            long client1PerceivedServerTime = T_1 + O_1;
            long client2PerceivedServerTime = T_2 + O_2;

            // Since O_2 - O_1 = 5s, and T_2 - T_1 should be approximately 0 (they started together)
            // But we set T_2 = T_1 + 2s to simulate real-world variation
            // So: T_2 + O_2 - (T_1 + O_1) = 2 + 8 - 3 = 7s??? No wait...
            // Actually T_2 - T_1 = 2s, O_2 - O_1 = 5s
            // But offsets are defined as "what to add to local to get server time"
            // So if Client 2's local is 2s ahead and offset is 5s more, they should still match

            // Let me re-think: in real scenario, all clients sync to SAME server time
            // So T_1 + O_1 = T_2 + O_2 = T_s
            // If T_2 = T_1 + 2s, then O_2 = O_1 - 2s to compensate

            // Let's use consistent values:
            // Assume server time T_s = 1000s
            // Client 1: local T_1 = 997s, offset O_1 = 3s, perceived = 1000s ✓
            // Client 2: local T_2 = 992s, offset O_2 = 8s, perceived = 1000s ✓

            long T_s = 1000 * TimeSpan.TicksPerSecond;
            T_1 = T_s - O_1; // 1000 - 3 = 997s
            T_2 = T_s - O_2; // 1000 - 8 = 992s

            client1PerceivedServerTime = T_1 + O_1;
            client2PerceivedServerTime = T_2 + O_2;

            Assert.AreEqual(T_s, client1PerceivedServerTime, "Client 1 should perceive server time correctly");
            Assert.AreEqual(T_s, client2PerceivedServerTime, "Client 2 should perceive server time correctly");

            Debug.Log($"[MATH VERIFICATION]");
            Debug.Log($"  Server time: {T_s / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  Client 1: local={T_1 / (double)TimeSpan.TicksPerSecond:F1}s + offset={O_1 / (double)TimeSpan.TicksPerSecond:F1}s = {client1PerceivedServerTime / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  Client 2: local={T_2 / (double)TimeSpan.TicksPerSecond:F1}s + offset={O_2 / (double)TimeSpan.TicksPerSecond:F1}s = {client2PerceivedServerTime / (double)TimeSpan.TicksPerSecond:F1}s");

            // After failover - Client 1 becomes host
            // GOOD: Client 1 uses T_1 + O_1 as new server time
            long newServerTime_Good = T_1 + O_1;
            // Client 2 perceived = T_2 + O_2 = T_s = newServerTime_Good ✓
            Assert.AreEqual(newServerTime_Good, client2PerceivedServerTime,
                "With preserved offset, Client 2's perceived time matches new server time");

            // BAD: Client 1 uses raw T_1 as new server time
            long newServerTime_Bad = T_1;
            // Client 2 perceived = T_2 + O_2 = T_s
            // Discrepancy = T_s - T_1 = O_1 = 3s
            long discrepancy = client2PerceivedServerTime - newServerTime_Bad;
            Assert.AreEqual(O_1, discrepancy,
                "Without preserved offset, Client 2 is ahead by Client 1's original offset");

            Debug.Log($"  [FAILOVER]");
            Debug.Log($"  Good approach - new server time: {newServerTime_Good / (double)TimeSpan.TicksPerSecond:F1}s (matches Client 2's perceived)");
            Debug.Log($"  Bad approach - new server time: {newServerTime_Bad / (double)TimeSpan.TicksPerSecond:F1}s");
            Debug.Log($"  Discrepancy with bad approach: {discrepancy / (double)TimeSpan.TicksPerSecond:F1}s (= Client 1's original offset)");
        }

        /// <summary>
        /// Helper method to create a time sync request
        /// </summary>
        private RequestMessage CreateTimeSyncRequest()
        {
            return new MockRequestMessage(client2Time.ElapsedTicks);
        }
    }
}
