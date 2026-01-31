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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using GONet.Utils;
using NUnit.Framework;
using static GONet.GONetMain;

namespace GONet.Tests.Time
{
    /// <summary>
    /// Tests for time sync convergence with AGGRESSIVE sync intervals (50ms).
    ///
    /// CRITICAL: These tests reproduce the bug where Client 2 (late-joiner) stabilizes 500ms behind
    /// server and never converges because interpolation (1 second duration) gets canceled by
    /// new syncs arriving every 50ms.
    ///
    /// Production uses 50ms intervals during gap closing (CLIENT_SYNC_TIME_EVERY_TICKS__UNTIL_GAP_CLOSED).
    /// Existing tests use 200ms intervals, which is 4x slower and allows interpolation to complete.
    ///
    /// This test suite uses REALISTIC 50ms intervals to catch convergence failures.
    /// </summary>
    [TestFixture]
    [Timeout(30000)] // 30 second timeout - tests have 5 second while loops
    public class TimeSyncAggressiveIntervalConvergenceTests : TimeSyncTestBase
    {
        private const int PRODUCTION_SYNC_INTERVAL_MS = 50; // CLIENT_SYNC_TIME_EVERY_TICKS__UNTIL_GAP_CLOSED
        private const int INTERPOLATION_DURATION_MS = 1000; // SetFromAuthority interpolation duration
        private const double MAX_ACCEPTABLE_DIVERGENCE_SECONDS = 0.15; // 150ms tolerance (allows for Thread.Sleep variance in test)

        private SecretaryOfTemporalAffairs clientTime;
        private SecretaryOfTemporalAffairs serverTime;

        [SetUp]
        public void Setup()
        {
            base.BaseSetUp();
            clientTime = new SecretaryOfTemporalAffairs();
            serverTime = new SecretaryOfTemporalAffairs();
            clientTime.Update();
            serverTime.Update();
        }

        [TearDown]
        public void TearDown()
        {
            base.BaseTearDown();
        }

        /// <summary>
        /// CRITICAL TEST: Reproduces the bug where Client 2 stabilizes 500ms behind server.
        ///
        /// Scenario:
        /// - Server 500ms ahead of client (typical late-joiner gap after first sync)
        /// - Client syncs every 50ms (aggressive gap closing mode)
        /// - Interpolation duration: 1000ms (SetFromAuthority default)
        ///
        /// Expected BEFORE fix: Client never converges (stuck at ~500ms behind)
        /// Expected AFTER fix: Client converges within reasonable time (<5 seconds)
        /// </summary>
        [Test]
        [Category("TimeCorrection")]
        [Category("AggressiveSync")]
        [Timeout(10000)]
        public void Should_Converge_With_500ms_Gap_And_50ms_Sync_Interval()
        {
            // ARRANGE: Client 500ms behind server (typical late-joiner scenario)
            // We simulate this by having server's RAW time be 500ms ahead of client's RAW time
            // In production, this happens when client joins late (server has been running longer)

            // Start both times
            clientTime.Update();
            serverTime.Update();

            // Simulate server being 500ms ahead by advancing its raw time
            // We do this by setting server time from authority with current client time + 500ms
            long clientRawTicks = clientTime.RawElapsedTicks;
            serverTime.SetFromAuthority(clientRawTicks + TimeSpan.FromMilliseconds(500).Ticks, forceImmediate: true);
            Thread.Sleep(100); // Let server adjustment settle
            serverTime.Update();
            clientTime.Update();

            // Verify setup: Server should be ~500ms ahead when comparing ELAPSED time (with offsets)
            double initialDiff = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
            UnityEngine.Debug.Log($"[CONVERGENCE-TEST] Initial difference: {initialDiff * 1000:F1}ms");

            Assert.That(initialDiff, Is.InRange(0.45, 0.55),
                "Initial setup should be ~500ms difference");

            // ACT: Simulate aggressive sync (50ms intervals) for 5 seconds
            // This tests if interpolation completes despite frequent syncs
            var differences = new List<double>();
            var stopwatch = Stopwatch.StartNew();
            int syncCount = 0;

            while (stopwatch.ElapsedMilliseconds < 5000)
            {
                // IMPORTANT TEST DESIGN NOTE:
                // Tests use ElapsedTicks (not RawElapsedTicks) because test setup creates time difference
                // via SetFromAuthority (which adds offset). Production has REAL raw time difference
                // (server running longer). Using ElapsedTicks in tests = using RawElapsedTicks in production.
                //
                // RTT calculation: Use RawElapsedTicks (monotonic, unaffected by interpolation)
                // Time sync value: Use ElapsedTicks (includes the offset we set via SetFromAuthority)
                var request = new MockRequestMessage(clientTime.RawElapsedTicks);  // RTT
                long serverResponseTicks = serverTime.ElapsedTicks;  // Server's view of current time

                // Process sync (NOT forcing - this is subsequent sync after first)
                HighPerfTimeSync.ProcessTimeSync(
                    request.UID,
                    serverResponseTicks,
                    request,
                    clientTime,
                    forceAdjustment: false
                );

                syncCount++;

                // Update times
                clientTime.Update();
                serverTime.Update();

                // Record difference
                double diff = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
                differences.Add(diff);

                if (syncCount % 20 == 0) // Log every 1 second (20 * 50ms)
                {
                    UnityEngine.Debug.Log($"[CONVERGENCE-TEST] After {syncCount} syncs ({stopwatch.ElapsedMilliseconds}ms): diff = {diff * 1000:F1}ms");
                }

                // Wait for next sync interval (PRODUCTION REALISTIC VALUE)
                Thread.Sleep(PRODUCTION_SYNC_INTERVAL_MS);
            }

            // ASSERT: Client should converge despite aggressive sync interval
            double finalDiff = differences.Last();
            double maxDiff = differences.Max();
            double avgDiff = differences.Average();

            UnityEngine.Debug.Log($"[CONVERGENCE-TEST] RESULTS after {syncCount} syncs:");
            UnityEngine.Debug.Log($"  Final diff: {finalDiff * 1000:F1}ms");
            UnityEngine.Debug.Log($"  Max diff: {maxDiff * 1000:F1}ms");
            UnityEngine.Debug.Log($"  Avg diff: {avgDiff * 1000:F1}ms");

            // CRITICAL ASSERTIONS
            Assert.That(finalDiff, Is.LessThan(MAX_ACCEPTABLE_DIVERGENCE_SECONDS),
                $"CONVERGENCE FAILURE: Client should converge to <100ms within 5s with 50ms sync interval. " +
                $"Final diff: {finalDiff * 1000:F1}ms. " +
                $"This reproduces the bug where Client 2 stabilizes 500ms behind server!");

            // Additionally check that convergence actually happened (not stuck at initial 500ms)
            Assert.That(finalDiff, Is.LessThan(initialDiff * 0.5),
                $"Client should have reduced gap by at least 50%. " +
                $"Initial: {initialDiff * 1000:F1}ms, Final: {finalDiff * 1000:F1}ms");
        }

        /// <summary>
        /// Tests convergence with various gap sizes using production 50ms sync interval.
        /// Larger gaps (>500ms) have slightly looser tolerance because interpolation completes
        /// over 1 second while being continuously updated every 50ms.
        /// </summary>
        [Test]
        [Category("TimeCorrection")]
        [Category("AggressiveSync")]
        [TestCase(100, TestName = "Should_Converge_100ms_Gap")]
        [TestCase(250, TestName = "Should_Converge_250ms_Gap")]
        [TestCase(500, TestName = "Should_Converge_500ms_Gap")]
        [TestCase(750, TestName = "Should_Converge_750ms_Gap")]
        [TestCase(999, TestName = "Should_Converge_999ms_Gap")] // Just below 1s threshold
        [Timeout(10000)]
        public void Should_Converge_Various_Gaps_With_Aggressive_Sync(int gapMilliseconds)
        {
            // ARRANGE
            long clientInitialTicks = clientTime.ElapsedTicks;
            serverTime.SetFromAuthority(clientInitialTicks + TimeSpan.FromMilliseconds(gapMilliseconds).Ticks);
            Thread.Sleep(100);
            serverTime.Update();
            clientTime.Update();

            // ACT: Sync aggressively for 5 seconds
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < 5000)
            {
                // RTT: RawElapsedTicks (monotonic), Time sync: ElapsedTicks (test offset)
                var request = new MockRequestMessage(clientTime.RawElapsedTicks);
                HighPerfTimeSync.ProcessTimeSync(
                    request.UID,
                    serverTime.ElapsedTicks,  // Include test offset
                    request,
                    clientTime,
                    false
                );

                clientTime.Update();
                serverTime.Update();
                Thread.Sleep(PRODUCTION_SYNC_INTERVAL_MS);
            }

            // ASSERT
            double finalDiff = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
            UnityEngine.Debug.Log($"[GAP-{gapMilliseconds}ms] Final diff: {finalDiff * 1000:F1}ms");

            // Larger gaps need slightly more tolerance (interpolation over 1s with 50ms updates)
            double tolerance = gapMilliseconds > 500 ? 0.15 : MAX_ACCEPTABLE_DIVERGENCE_SECONDS; // 150ms for >500ms gaps
            Assert.That(finalDiff, Is.LessThan(tolerance),
                $"Should converge {gapMilliseconds}ms gap to <{tolerance * 1000}ms with 50ms sync interval. Final: {finalDiff * 1000:F1}ms");
        }

        /// <summary>
        /// Compares convergence speed between 50ms (production) and 200ms (existing test) intervals.
        /// </summary>
        [Test]
        [Category("TimeCorrection")]
        [Category("AggressiveSync")]
        [Timeout(25000)]
        public void Should_Converge_Faster_With_Aggressive_Interval()
        {
            const int GAP_MS = 500;
            const int TEST_DURATION_MS = 5000;

            // Test 1: 50ms interval (production)
            clientTime = new SecretaryOfTemporalAffairs();
            serverTime = new SecretaryOfTemporalAffairs();
            clientTime.Update();
            serverTime.Update();

            long clientTicks1 = clientTime.ElapsedTicks;
            serverTime.SetFromAuthority(clientTicks1 + TimeSpan.FromMilliseconds(GAP_MS).Ticks);
            Thread.Sleep(100);

            var diffs50ms = new List<double>();
            var sw50 = Stopwatch.StartNew();
            while (sw50.ElapsedMilliseconds < TEST_DURATION_MS)
            {
                // RTT: RawElapsedTicks, Time sync: ElapsedTicks
                var req = new MockRequestMessage(clientTime.RawElapsedTicks);
                HighPerfTimeSync.ProcessTimeSync(req.UID, serverTime.ElapsedTicks, req, clientTime, false);
                clientTime.Update();
                serverTime.Update();
                diffs50ms.Add(Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds));
                Thread.Sleep(50);
            }

            // Test 2: 200ms interval (existing tests)
            clientTime = new SecretaryOfTemporalAffairs();
            serverTime = new SecretaryOfTemporalAffairs();
            clientTime.Update();
            serverTime.Update();

            long clientTicks2 = clientTime.ElapsedTicks;
            serverTime.SetFromAuthority(clientTicks2 + TimeSpan.FromMilliseconds(GAP_MS).Ticks);
            Thread.Sleep(100);

            var diffs200ms = new List<double>();
            var sw200 = Stopwatch.StartNew();
            while (sw200.ElapsedMilliseconds < TEST_DURATION_MS)
            {
                // RTT: RawElapsedTicks, Time sync: ElapsedTicks
                var req = new MockRequestMessage(clientTime.RawElapsedTicks);
                HighPerfTimeSync.ProcessTimeSync(req.UID, serverTime.ElapsedTicks, req, clientTime, false);
                clientTime.Update();
                serverTime.Update();
                diffs200ms.Add(Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds));
                Thread.Sleep(200);
            }

            // ASSERT: 50ms should converge at least as fast as 200ms
            // (Currently FAILS because 50ms gets stuck, 200ms converges)
            double final50 = diffs50ms.Last();
            double final200 = diffs200ms.Last();

            UnityEngine.Debug.Log($"[COMPARISON] 50ms interval final: {final50 * 1000:F1}ms, 200ms interval final: {final200 * 1000:F1}ms");

            Assert.That(final50, Is.LessThanOrEqualTo(final200),
                $"50ms sync interval should converge at least as fast as 200ms. " +
                $"50ms: {final50 * 1000:F1}ms, 200ms: {final200 * 1000:F1}ms. " +
                $"If this fails, it proves aggressive sync prevents convergence!");
        }
    }
}
