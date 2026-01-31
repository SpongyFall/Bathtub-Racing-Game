/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 *
 * Authorized use is explicitly limited to the following:
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on original source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 * -The ability to modify source code for local use only
 * -The ability to distribute products based on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on modified source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using System;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using static GONet.GONetMain;

namespace GONet.Tests.Time
{
    /// <summary>
    /// CRITICAL REGRESSION TESTS for lobby flow time baseline reset functionality.
    ///
    /// **THE PROBLEM:**
    /// When using a lobby flow where processes start before server/client roles are assigned:
    /// - Process 1 starts at T+0s, sits in lobby until T+30s, becomes CLIENT (RawElapsedTicks=30s)
    /// - Process 3 starts at T+28s, sits in lobby until T+30s, becomes SERVER (RawElapsedTicks=2s)
    /// - Without reset: Client tries to sync backwards 28 seconds → freeze or far-future sync
    ///
    /// **THE FIX:**
    /// Call Time.ResetTimeBaseline() when server/client property setters are invoked.
    /// This resets RawElapsedTicks to 0 at the moment networking begins.
    ///
    /// **HISTORY:**
    /// This functionality was added in commit 2ce15df6 (Nov 14, 2025) but was accidentally
    /// lost during the GONet.cs file split merge (99a3092d, Nov 24, 2025).
    /// These tests ensure we don't regress again.
    /// </summary>
    [TestFixture]
    public class LobbyFlowTimeBaselineTests : TimeSyncTestBase
    {
        private const string CATEGORY_LOBBY = "LobbyFlow";
        private const string CATEGORY_REGRESSION = "RegressionTest";
        private const string CATEGORY_TIMESYNC = "TimeSync";

        /// <summary>
        /// REGRESSION TEST: Verifies that ResetTimeBaseline() method exists on SecretaryOfTemporalAffairs.
        /// This is the first line of defense - if the method doesn't exist, compilation fails.
        /// </summary>
        [Test]
        [Category(CATEGORY_LOBBY)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void ResetTimeBaseline_Method_Must_Exist()
        {
            // ARRANGE
            var gonetTime = new SecretaryOfTemporalAffairs();

            // ACT - Just call the method to verify it exists and doesn't throw
            // If this test fails to compile, the method is missing!
            gonetTime.ResetTimeBaseline();

            // ASSERT - Method exists and executed without exception
            Assert.Pass("ResetTimeBaseline() method exists and is callable");
        }

        /// <summary>
        /// REGRESSION TEST: Verifies that ResetTimeBaseline() resets RawElapsedTicks to ~0.
        ///
        /// This is the core functionality - after sitting in a lobby for N seconds,
        /// calling ResetTimeBaseline() should make RawElapsedTicks return to ~0.
        /// </summary>
        [Test]
        [Category(CATEGORY_LOBBY)]
        [Category(CATEGORY_REGRESSION)]
        [Category(CATEGORY_TIMESYNC)]
        [Timeout(10000)]
        public void ResetTimeBaseline_Should_Reset_RawElapsedTicks_To_Zero()
        {
            // ARRANGE: Simulate sitting in lobby for a while
            var gonetTime = new SecretaryOfTemporalAffairs();
            gonetTime.Update();

            // Wait to accumulate some time (simulating lobby wait)
            Thread.Sleep(500);
            gonetTime.Update();

            long rawTicksBeforeReset = gonetTime.RawElapsedTicks;
            double msBeforeReset = rawTicksBeforeReset / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[LOBBY-TEST] Before reset: RawElapsedTicks = {msBeforeReset:F1}ms");

            // Verify we actually accumulated time
            Assert.That(msBeforeReset, Is.GreaterThan(400),
                "Test setup error: Should have accumulated at least 400ms in 'lobby'");

            // ACT: Reset the time baseline (what happens when server/client is assigned)
            gonetTime.ResetTimeBaseline();
            gonetTime.Update();

            // ASSERT: RawElapsedTicks should be very close to 0
            long rawTicksAfterReset = gonetTime.RawElapsedTicks;
            double msAfterReset = rawTicksAfterReset / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[LOBBY-TEST] After reset: RawElapsedTicks = {msAfterReset:F1}ms");

            Assert.That(msAfterReset, Is.LessThan(100),
                $"REGRESSION FAILURE: After ResetTimeBaseline(), RawElapsedTicks should be near 0, but was {msAfterReset:F1}ms");

            UnityEngine.Debug.Log("[LOBBY-TEST] ✅ PASSED - ResetTimeBaseline correctly resets time to 0");
        }

        /// <summary>
        /// REGRESSION TEST: Verifies that ResetTimeBaseline() clears all time sync state.
        ///
        /// Any pending offsets, dilations, or cached values should be cleared.
        /// </summary>
        [Test]
        [Category(CATEGORY_LOBBY)]
        [Category(CATEGORY_REGRESSION)]
        [Category(CATEGORY_TIMESYNC)]
        [Timeout(10000)]
        public void ResetTimeBaseline_Should_Clear_All_Time_Sync_State()
        {
            // ARRANGE: Set up time with an offset applied
            var gonetTime = new SecretaryOfTemporalAffairs();
            gonetTime.Update();

            // Apply an offset (simulating a prior sync)
            long offsetToApply = TimeSpan.FromMilliseconds(1000).Ticks;
            long targetTicks = gonetTime.RawElapsedTicks + offsetToApply;
            gonetTime.SetFromAuthority(targetTicks, forceImmediate: true);
            Thread.Sleep(50);
            gonetTime.Update();

            // Verify offset was applied
            long effectiveBefore = gonetTime.ElapsedTicks;
            long rawBefore = gonetTime.RawElapsedTicks;
            long offsetBefore = effectiveBefore - rawBefore;
            double offsetMsBefore = offsetBefore / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[SYNC-STATE-TEST] Before reset: Offset = {offsetMsBefore:F1}ms");
            Assert.That(offsetMsBefore, Is.GreaterThan(500), "Test setup: Should have ~1000ms offset");

            // ACT: Reset the time baseline
            gonetTime.ResetTimeBaseline();
            gonetTime.Update();

            // ASSERT: Offset should be cleared (effective ~= raw ~= 0)
            long effectiveAfter = gonetTime.ElapsedTicks;
            long rawAfter = gonetTime.RawElapsedTicks;
            long offsetAfter = effectiveAfter - rawAfter;
            double offsetMsAfter = offsetAfter / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[SYNC-STATE-TEST] After reset: Raw = {rawAfter / (double)TimeSpan.TicksPerMillisecond:F1}ms, " +
                                 $"Effective = {effectiveAfter / (double)TimeSpan.TicksPerMillisecond:F1}ms, " +
                                 $"Offset = {offsetMsAfter:F1}ms");

            Assert.That(Math.Abs(offsetMsAfter), Is.LessThan(50),
                $"REGRESSION FAILURE: After ResetTimeBaseline(), offset should be ~0, but was {offsetMsAfter:F1}ms");

            Assert.That(rawAfter / (double)TimeSpan.TicksPerMillisecond, Is.LessThan(100),
                "After reset, raw time should be near 0");

            Assert.That(effectiveAfter / (double)TimeSpan.TicksPerMillisecond, Is.LessThan(100),
                "After reset, effective time should be near 0");

            UnityEngine.Debug.Log("[SYNC-STATE-TEST] ✅ PASSED - All time sync state correctly cleared");
        }

        /// <summary>
        /// REGRESSION TEST: Simulates the exact lobby flow scenario that caused the original bug.
        ///
        /// In the real scenario:
        /// - Process 1 starts at T+0s, sits in lobby until T+30s, becomes CLIENT (RawElapsedTicks=30s)
        /// - Process 3 starts at T+28s, sits in lobby until T+30s, becomes SERVER (RawElapsedTicks=2s)
        /// - Without reset: 28 second mismatch causes sync failure
        /// - With reset: Both start at 0 when networking begins
        ///
        /// This test simulates the single-process perspective: a process that has been running
        /// for a while (simulating lobby time) calls ResetTimeBaseline() and should see its
        /// RawElapsedTicks reset to ~0, eliminating accumulated lobby wait time.
        /// </summary>
        [Test]
        [Category(CATEGORY_LOBBY)]
        [Category(CATEGORY_REGRESSION)]
        [Category(CATEGORY_TIMESYNC)]
        [Timeout(25000)]
        public void LobbyFlow_ServerClientTimeMismatch_Should_Be_Resolved_By_Reset()
        {
            // ARRANGE: Create a time instance and let it accumulate "lobby wait" time
            var gonetTime = new SecretaryOfTemporalAffairs();
            gonetTime.Update();

            // Simulate sitting in lobby for 500ms before role assignment
            Thread.Sleep(500);
            gonetTime.Update();

            long rawTicksBeforeReset = gonetTime.RawElapsedTicks;
            double msBeforeReset = rawTicksBeforeReset / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[MISMATCH-TEST] Before reset (simulating lobby wait): RawElapsedTicks = {msBeforeReset:F0}ms");

            // Verify we accumulated lobby time
            Assert.That(msBeforeReset, Is.GreaterThan(400),
                "Test setup: Should have accumulated at least 400ms of 'lobby time'");

            // ACT: Reset when role is assigned (what happens in server/client property setters)
            // This is the critical operation that was lost in the merge
            gonetTime.ResetTimeBaseline();

            // Small delay then update (simulating the time between reset and first sync)
            Thread.Sleep(50);
            gonetTime.Update();

            // ASSERT: After reset, time should be near 0 (lobby time eliminated)
            long rawTicksAfterReset = gonetTime.RawElapsedTicks;
            double msAfterReset = rawTicksAfterReset / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[MISMATCH-TEST] After reset: RawElapsedTicks = {msAfterReset:F0}ms");

            // The key assertion: accumulated lobby time should be gone
            Assert.That(msAfterReset, Is.LessThan(150),
                $"REGRESSION FAILURE: After ResetTimeBaseline(), RawElapsedTicks should be near 0 (lobby time cleared), but was {msAfterReset:F0}ms");

            // Verify the magnitude of time saved by the reset
            double timeSavedMs = msBeforeReset - msAfterReset;
            UnityEngine.Debug.Log($"[MISMATCH-TEST] Time baseline shifted by: {timeSavedMs:F0}ms (this would have been the mismatch!)");

            Assert.That(timeSavedMs, Is.GreaterThan(350),
                "Reset should have eliminated at least 350ms of accumulated lobby time");

            UnityEngine.Debug.Log("[MISMATCH-TEST] ✅ PASSED - Lobby flow time mismatch resolved by reset");
        }

        /// <summary>
        /// REGRESSION TEST: Verifies physics time is also reset.
        /// </summary>
        [Test]
        [Category(CATEGORY_LOBBY)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void ResetTimeBaseline_Should_Reset_Physics_Time_State()
        {
            // ARRANGE
            var gonetTime = new SecretaryOfTemporalAffairs();
            gonetTime.Update();

            // Accumulate some time
            Thread.Sleep(300);
            gonetTime.Update();

            // ACT
            gonetTime.ResetTimeBaseline();

            // ASSERT: FixedElapsedSeconds should also be near 0
            double fixedElapsed = gonetTime.FixedElapsedSeconds;

            UnityEngine.Debug.Log($"[PHYSICS-TEST] After reset: FixedElapsedSeconds = {fixedElapsed:F3}s");

            Assert.That(fixedElapsed, Is.LessThan(0.1),
                $"REGRESSION FAILURE: FixedElapsedSeconds should be near 0 after reset, but was {fixedElapsed:F3}s");

            UnityEngine.Debug.Log("[PHYSICS-TEST] ✅ PASSED - Physics time state correctly reset");
        }

        /// <summary>
        /// REGRESSION TEST: Verifies that time continues to progress normally after reset.
        /// </summary>
        [Test]
        [Category(CATEGORY_LOBBY)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void Time_Should_Progress_Normally_After_ResetTimeBaseline()
        {
            // ARRANGE
            var gonetTime = new SecretaryOfTemporalAffairs();
            gonetTime.Update();

            // Reset
            gonetTime.ResetTimeBaseline();
            gonetTime.Update();

            long ticksAtReset = gonetTime.RawElapsedTicks;

            // ACT: Wait and verify time progresses
            Thread.Sleep(200);
            gonetTime.Update();

            long ticksAfterWait = gonetTime.RawElapsedTicks;
            long ticksDelta = ticksAfterWait - ticksAtReset;
            double msDelta = ticksDelta / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[PROGRESS-TEST] After 200ms wait: Delta = {msDelta:F1}ms");

            // ASSERT: Time should have progressed ~200ms
            Assert.That(msDelta, Is.InRange(150, 300),
                $"Time should progress normally after reset, but only progressed {msDelta:F1}ms in 200ms");

            UnityEngine.Debug.Log("[PROGRESS-TEST] ✅ PASSED - Time progresses normally after reset");
        }
    }
}
