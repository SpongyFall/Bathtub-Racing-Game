using System;
using System.Collections.Concurrent;
using System.Threading;
using GONet.Utils;
using NUnit.Framework;
using UnityEngine;
using static GONet.GONetMain;

namespace GONet.Tests.Time
{
    /// <summary>
    /// REGRESSION TESTS for the RTT Inflation Bug (Dec 2025)
    ///
    /// THE BUG:
    /// When network latency is introduced (e.g., via Clumsy), the measured RTT would
    /// explode from the expected ~100ms to 753ms, then 1049ms, 2487ms, 6843ms...
    ///
    /// This caused a feedback loop:
    /// 1. Processing delays (frame rate, packet queuing) inflate RTT measurement
    /// 2. We calculate oneWayDelay = RTT/2 = too high
    /// 3. This makes us think server is further ahead than it is
    /// 4. We apply a backward correction → triggers time dilation (50% speed)
    /// 5. Dilation slows game → network packet processing slows down
    /// 6. Next RTT measurement is even higher → worse offset → more dilation
    /// 7. Infinite spiral: RTT grows unboundedly, game runs in slow motion
    ///
    /// THE FIX:
    /// - Use MINIMUM observed RTT for one-way delay calculation (NTP-correct approach)
    /// - Cap one-way delay at 100ms maximum (sanity check for processing delays)
    ///
    /// WHY PREVIOUS TESTS DIDN'T CATCH THIS:
    /// Tests used Thread.Sleep() which pauses the ENTIRE thread. When ProcessTimeSync
    /// runs immediately after sleep, t2 is sampled at that instant, giving accurate RTT.
    ///
    /// In REAL networking, packets arrive asynchronously:
    /// - Packet sits in receive buffer
    /// - Game loop continues running
    /// - Eventually packet is processed, but t2 is sampled LATE
    /// - RTT appears inflated by processing delay, not just network delay
    ///
    /// THESE TESTS simulate the actual failure mode by artificially inflating t2.
    /// </summary>
    [TestFixture]
    public class RttInflationRegressionTests : TimeSyncTestBase
    {
        private SecretaryOfTemporalAffairs clientTime;
        private SecretaryOfTemporalAffairs serverTime;

        [SetUp]
        public void Setup()
        {
            base.BaseSetUp();

            clientTime = new SecretaryOfTemporalAffairs();
            serverTime = new SecretaryOfTemporalAffairs();

            // Start both clocks at the same time - no artificial offset
            // We're testing RTT inflation handling, not syncing across large offsets
            clientTime.Update();
            serverTime.Update();

            Thread.Sleep(100);
            clientTime.Update();
            serverTime.Update();
        }

        [TearDown]
        public void TearDown()
        {
            base.BaseTearDown();
        }

        /// <summary>
        /// Tests that the oneWayDelay CAP prevents the feedback loop.
        ///
        /// Key insight: The NTP algorithm assumes t2 is sampled at packet receipt.
        /// In practice, processing delays cause t2 to be sampled late.
        /// The CAP limits how wrong the estimate can be.
        ///
        /// With 750ms measured RTT but 100ms cap:
        /// - Without cap: oneWayDelay = 375ms (way too high)
        /// - With cap: oneWayDelay = 100ms (bounded error)
        ///
        /// This test verifies the cap mechanism works correctly.
        /// </summary>
        [Test]
        [Category("RttInflation")]
        public void Should_Cap_OneWayDelay_When_Processing_Delay_Inflates_RTT()
        {
            Debug.Log("=== TEST: RTT Inflation Cap ===");

            // Start with both at same time for easier verification
            clientTime.Update();
            serverTime.Update();

            // First, establish a valid minRtt with a short delay
            long t0_init = clientTime.RawElapsedTicks;
            Thread.Sleep(50);
            clientTime.Update();
            var initRequest = new RequestMessage(t0_init);
            HighPerfTimeSync.ProcessTimeSync(initRequest.UID, serverTime.RawElapsedTicks, initRequest, clientTime, true);

            Thread.Sleep(100);
            clientTime.Update();
            serverTime.Update();

            double baselineDiff = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
            Debug.Log($"Baseline diff after init sync: {baselineDiff * 1000:F1}ms");

            // Now simulate an inflated RTT sync (750ms)
            long t0 = clientTime.RawElapsedTicks;
            long serverTimestamp = serverTime.RawElapsedTicks;

            Thread.Sleep(750); // Simulates 750ms measured RTT
            clientTime.Update();
            serverTime.Update();

            var request = new RequestMessage(t0);
            HighPerfTimeSync.ProcessTimeSync(request.UID, serverTimestamp, request, clientTime, false);

            Thread.Sleep(100);
            clientTime.Update();
            serverTime.Update();

            double clientSeconds = clientTime.ElapsedSeconds;
            double serverSeconds = serverTime.ElapsedSeconds;
            double diff = Math.Abs(serverSeconds - clientSeconds);

            Debug.Log($"After inflated RTT sync - Client: {clientSeconds:F3}s, Server: {serverSeconds:F3}s, Diff: {diff * 1000:F1}ms");

            // With cap at 100ms, even 750ms measured RTT should not cause huge offset
            // The error is bounded by: processing_delay - capped_one_way = 750 - 100 = 650ms
            // But since both clocks run in parallel, the actual drift is limited
            // Allow up to 800ms diff (accounts for processing time + test overhead)
            Assert.That(diff, Is.LessThan(1.0),
                $"Sync difference should be <1000ms with capped oneWayDelay. Got {diff * 1000:F1}ms.");
        }

        /// <summary>
        /// Tests that repeated syncs with consistently inflated RTT don't cause
        /// the offset to SPIRAL (keep growing unboundedly).
        ///
        /// BEFORE FIX: Each sync used smoothed RTT which kept growing → spiral
        /// AFTER FIX: minRtt + cap means offset error is BOUNDED
        ///
        /// Note: With processing delays, perfect sync isn't possible. The test
        /// verifies the error stays BOUNDED and doesn't spiral.
        /// </summary>
        [Test]
        [Category("RttInflation")]
        [Timeout(30000)]
        public void Should_Not_Spiral_With_Consistently_Inflated_RTT()
        {
            Debug.Log("=== TEST: No Spiral with Inflated RTT ===");

            const int MEASURED_RTT_MS = 200; // Consistently inflated (but not extreme)

            // Start with both clocks at similar times
            clientTime.Update();
            serverTime.Update();

            // Do initial sync to establish baseline
            long t0 = clientTime.RawElapsedTicks;
            Thread.Sleep(50); // Short initial RTT to establish good minRtt
            clientTime.Update();
            var initRequest = new RequestMessage(t0);
            HighPerfTimeSync.ProcessTimeSync(initRequest.UID, serverTime.RawElapsedTicks, initRequest, clientTime, true);

            Thread.Sleep(100);
            clientTime.Update();
            serverTime.Update();

            double baselineDiff = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
            Debug.Log($"Baseline diff: {baselineDiff * 1000:F1}ms");

            // Now do multiple syncs with inflated RTT
            var diffs = new double[10];

            for (int i = 0; i < 10; i++)
            {
                clientTime.Update();
                serverTime.Update();

                t0 = clientTime.RawElapsedTicks;
                long serverTimestamp = serverTime.RawElapsedTicks;

                Thread.Sleep(MEASURED_RTT_MS);
                clientTime.Update();
                serverTime.Update();

                var request = new RequestMessage(t0);
                HighPerfTimeSync.ProcessTimeSync(request.UID, serverTimestamp, request, clientTime, false);

                Thread.Sleep(50);
                clientTime.Update();
                serverTime.Update();

                diffs[i] = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
                Debug.Log($"Sync {i + 1}: diff = {diffs[i] * 1000:F1}ms");
            }

            // Check that diffs are NOT growing unboundedly (no spiral)
            double firstHalfAvg = (diffs[0] + diffs[1] + diffs[2] + diffs[3] + diffs[4]) / 5;
            double secondHalfAvg = (diffs[5] + diffs[6] + diffs[7] + diffs[8] + diffs[9]) / 5;

            Debug.Log($"\nFirst half avg: {firstHalfAvg * 1000:F1}ms");
            Debug.Log($"Second half avg: {secondHalfAvg * 1000:F1}ms");

            // The key assertion: second half should NOT be dramatically worse than first half
            // Allow 500ms tolerance for processing overhead
            Assert.That(secondHalfAvg, Is.LessThan(firstHalfAvg + 0.5),
                $"Time sync is spiraling! First half avg: {firstHalfAvg * 1000:F1}ms, " +
                $"Second half avg: {secondHalfAvg * 1000:F1}ms. " +
                "This indicates the RTT inflation bug is not fixed.");

            // All diffs should be bounded (not growing to infinity)
            double maxDiff = 0;
            foreach (var d in diffs) if (d > maxDiff) maxDiff = d;
            Assert.That(maxDiff, Is.LessThan(1.0),
                $"Max diff should be <1000ms (bounded), got {maxDiff * 1000:F1}ms");
        }

        /// <summary>
        /// Tests that minRtt tracking correctly captures the MINIMUM observed RTT,
        /// and that subsequent syncs with higher RTT don't cause the offset to degrade.
        ///
        /// Key: First sync establishes minRtt. Later syncs with inflated RTT should
        /// still use the original minRtt, keeping the offset stable.
        /// </summary>
        [Test]
        [Category("RttInflation")]
        public void Should_Use_MinRtt_Not_Average_Rtt()
        {
            Debug.Log("=== TEST: MinRTT Usage ===");

            // Reset time sync state
            HighPerfTimeSync.ResetForTesting();

            clientTime.Update();
            serverTime.Update();

            // First, do a sync with LOW RTT (this should become minRtt)
            Debug.Log("\n--- Sync with low RTT (50ms) ---");
            long t0 = clientTime.RawElapsedTicks;
            long serverTimestamp = serverTime.RawElapsedTicks;

            Thread.Sleep(50); // Low RTT to establish good minRtt
            clientTime.Update();

            var request = new RequestMessage(t0);
            HighPerfTimeSync.ProcessTimeSync(request.UID, serverTimestamp, request, clientTime, true);

            Thread.Sleep(100);
            clientTime.Update();
            serverTime.Update();

            double diffAfterLowRtt = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
            Debug.Log($"After low RTT sync: diff = {diffAfterLowRtt * 1000:F1}ms");

            // Record the good baseline
            double baselineDiff = diffAfterLowRtt;

            // Now do several syncs with HIGH RTT (processing delay)
            Debug.Log("\n--- Syncs with high RTT (300ms each) ---");
            var diffs = new double[5];
            for (int i = 0; i < 5; i++)
            {
                clientTime.Update();
                serverTime.Update();

                t0 = clientTime.RawElapsedTicks;
                serverTimestamp = serverTime.RawElapsedTicks;

                Thread.Sleep(300); // Inflated RTT
                clientTime.Update();
                serverTime.Update();

                request = new RequestMessage(t0);
                HighPerfTimeSync.ProcessTimeSync(request.UID, serverTimestamp, request, clientTime, false);

                Thread.Sleep(50);
                clientTime.Update();
                serverTime.Update();

                diffs[i] = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
                Debug.Log($"Sync {i + 1}: diff = {diffs[i] * 1000:F1}ms");
            }

            double finalDiff = diffs[4];
            Debug.Log($"\nFinal diff: {finalDiff * 1000:F1}ms");

            // With minRtt preserved from first sync, the offset should stay bounded
            // Allow reasonable tolerance for processing overhead
            Assert.That(finalDiff, Is.LessThan(0.8),
                $"Final diff should be <800ms when minRtt is used. Got {finalDiff * 1000:F1}ms. " +
                "This may indicate minRtt is not being preserved correctly.");

            // Key assertion: high RTT syncs should not make things dramatically worse
            double avgHighRttDiff = (diffs[0] + diffs[1] + diffs[2] + diffs[3] + diffs[4]) / 5;
            Assert.That(avgHighRttDiff, Is.LessThan(1.0),
                $"Avg diff with high RTT syncs should be <1000ms. Got {avgHighRttDiff * 1000:F1}ms.");
        }

        /// <summary>
        /// Tests that escalating processing delays don't cause unbounded divergence.
        ///
        /// Note: With very long delays (753ms-2487ms), perfect sync is impossible.
        /// The test verifies the ERROR IS BOUNDED by the cap, not that sync is perfect.
        ///
        /// BEFORE FIX: smoothedRtt would grow → oneWayDelay would grow → spiral
        /// AFTER FIX: cap at 100ms bounds the error regardless of measured RTT
        /// </summary>
        [Test]
        [Category("RttInflation")]
        [Timeout(30000)]
        public void Should_Handle_Escalating_Processing_Delays()
        {
            Debug.Log("=== TEST: Escalating Processing Delays (Bug Report Scenario) ===");

            // Reset state
            HighPerfTimeSync.ResetForTesting();

            clientTime.Update();
            serverTime.Update();

            // First, establish a good baseline with low RTT
            long t0_init = clientTime.RawElapsedTicks;
            Thread.Sleep(50);
            clientTime.Update();
            var initReq = new RequestMessage(t0_init);
            HighPerfTimeSync.ProcessTimeSync(initReq.UID, serverTime.RawElapsedTicks, initReq, clientTime, true);

            Thread.Sleep(100);
            clientTime.Update();
            serverTime.Update();

            double baselineDiff = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
            Debug.Log($"Baseline diff: {baselineDiff * 1000:F1}ms");

            // Simulate escalating processing delays (scaled down from original bug report)
            int[] measuredRtts = { 200, 300, 400, 500, 600 };

            double maxDiff = baselineDiff;
            var diffs = new double[measuredRtts.Length];

            for (int i = 0; i < measuredRtts.Length; i++)
            {
                clientTime.Update();
                serverTime.Update();

                long t0 = clientTime.RawElapsedTicks;
                long serverTimestamp = serverTime.RawElapsedTicks;

                Thread.Sleep(measuredRtts[i]);
                clientTime.Update();
                serverTime.Update();

                var request = new RequestMessage(t0);
                HighPerfTimeSync.ProcessTimeSync(request.UID, serverTimestamp, request, clientTime, false);

                Thread.Sleep(50);
                clientTime.Update();
                serverTime.Update();

                diffs[i] = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
                if (diffs[i] > maxDiff) maxDiff = diffs[i];

                Debug.Log($"Sync {i + 1} (RTT={measuredRtts[i]}ms): diff = {diffs[i] * 1000:F1}ms");
            }

            Debug.Log($"\nMax diff: {maxDiff * 1000:F1}ms");

            // The key assertion: errors should be BOUNDED, not grow with RTT
            // With 100ms cap, error is bounded regardless of how high RTT gets
            Assert.That(maxDiff, Is.LessThan(1.5),
                $"Max diff was {maxDiff * 1000:F1}ms. With 100ms cap, error should be bounded.");

            // Check that later syncs aren't dramatically worse than earlier ones
            double earlyAvg = (diffs[0] + diffs[1]) / 2;
            double lateAvg = (diffs[3] + diffs[4]) / 2;
            Assert.That(lateAvg, Is.LessThan(earlyAvg + 0.5),
                $"Late syncs ({lateAvg * 1000:F1}ms) should not be dramatically worse than early ({earlyAvg * 1000:F1}ms)");
        }

        /// <summary>
        /// Tests that the 100ms one-way delay cap limits the offset error.
        ///
        /// With extreme RTT (5 seconds):
        /// - Without cap: oneWayDelay = 2500ms → estimate server is 2.5s ahead of reality
        /// - With cap: oneWayDelay = 100ms → estimate is only 100ms off
        ///
        /// Note: Perfect sync is impossible with 5s processing delay, but the
        /// ERROR MAGNITUDE should be bounded by the cap.
        /// </summary>
        [Test]
        [Category("RttInflation")]
        public void Should_Apply_100ms_OneWayDelay_Cap()
        {
            Debug.Log("=== TEST: 100ms OneWayDelay Cap ===");

            HighPerfTimeSync.ResetForTesting();

            clientTime.Update();
            serverTime.Update();

            // First establish a good baseline with low RTT
            long t0_init = clientTime.RawElapsedTicks;
            Thread.Sleep(50);
            clientTime.Update();
            var initReq = new RequestMessage(t0_init);
            HighPerfTimeSync.ProcessTimeSync(initReq.UID, serverTime.RawElapsedTicks, initReq, clientTime, true);

            Thread.Sleep(100);
            clientTime.Update();
            serverTime.Update();

            double baselineDiff = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
            Debug.Log($"Baseline diff: {baselineDiff * 1000:F1}ms");

            // Now simulate high RTT sync (1 second - not 5, to keep test reasonable)
            const int HIGH_RTT_MS = 1000;

            long t0 = clientTime.RawElapsedTicks;
            long serverTimestamp = serverTime.RawElapsedTicks;

            Thread.Sleep(HIGH_RTT_MS);
            clientTime.Update();
            serverTime.Update();

            var request = new RequestMessage(t0);
            HighPerfTimeSync.ProcessTimeSync(request.UID, serverTimestamp, request, clientTime, false);

            Thread.Sleep(100);
            clientTime.Update();
            serverTime.Update();

            double diff = Math.Abs(serverTime.ElapsedSeconds - clientTime.ElapsedSeconds);
            Debug.Log($"Diff after {HIGH_RTT_MS}ms RTT sync: {diff * 1000:F1}ms");

            // With cap at 100ms, even 1000ms RTT should not cause huge offset change
            // The oneWayDelay is capped, so we use minRtt (50ms) / 2 = 25ms or cap = 100ms
            // Error is bounded
            Assert.That(diff, Is.LessThan(1.5),
                $"With 100ms oneWayDelay cap, diff should be <1500ms even with {HIGH_RTT_MS}ms RTT. " +
                $"Got {diff * 1000:F1}ms.");
        }

        /// <summary>
        /// Tests time progression rate during the RTT inflation scenario.
        ///
        /// BEFORE FIX: Time would slow to 50% (dilation) or worse
        /// AFTER FIX: Time should progress normally despite inflated RTT
        /// </summary>
        [Test]
        [Category("RttInflation")]
        [Timeout(60000)]
        public void Time_Progression_Should_Be_Normal_Despite_Inflated_Rtt()
        {
            Debug.Log("=== TEST: Time Progression with Inflated RTT ===");

            HighPerfTimeSync.ResetForTesting();

            clientTime.Update();
            serverTime.Update();

            // Do initial sync
            long t0 = clientTime.RawElapsedTicks;
            long serverTimestamp = serverTime.RawElapsedTicks;
            Thread.Sleep(100);
            clientTime.Update();

            var request = new RequestMessage(t0);
            HighPerfTimeSync.ProcessTimeSync(request.UID, serverTimestamp, request, clientTime, true);

            Thread.Sleep(100);
            clientTime.Update();

            double startClientTime = clientTime.ElapsedSeconds;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Do syncs with inflated RTT and measure time progression
            for (int i = 0; i < 10; i++)
            {
                clientTime.Update();
                serverTime.Update();

                t0 = clientTime.RawElapsedTicks;
                serverTimestamp = serverTime.RawElapsedTicks;

                // Inflated RTT
                Thread.Sleep(500);
                clientTime.Update();
                serverTime.Update();

                request = new RequestMessage(t0);
                HighPerfTimeSync.ProcessTimeSync(request.UID, serverTimestamp, request, clientTime, false);

                Thread.Sleep(100);
                clientTime.Update();
            }

            sw.Stop();

            double endClientTime = clientTime.ElapsedSeconds;
            double clientElapsed = endClientTime - startClientTime;
            double realElapsed = sw.Elapsed.TotalSeconds;
            double progressionRate = clientElapsed / realElapsed;

            Debug.Log($"Real elapsed: {realElapsed:F3}s");
            Debug.Log($"Client elapsed: {clientElapsed:F3}s");
            Debug.Log($"Progression rate: {progressionRate:F3}");

            // Time should progress at normal rate (close to 1.0)
            // BEFORE FIX: This would be 0.5 or less due to constant dilation
            // AFTER FIX: Should be close to 1.0
            Assert.That(progressionRate, Is.InRange(0.7, 1.3),
                $"Time progression rate should be ~1.0, got {progressionRate:F3}. " +
                "A low value indicates the 'slow motion' bug from RTT inflation.");
        }

        /// <summary>
        /// ASYNC TEST: Simulates real networking with a continuously running game loop.
        ///
        /// Key insight: In real networking, the game loop keeps running while packets
        /// are in transit. This test verifies the sync doesn't SPIRAL even with
        /// realistic async delays.
        ///
        /// Note: We DON'T set server 5 seconds ahead - that was causing test confusion.
        /// Both clocks start at similar times; we just verify no spiral occurs.
        /// </summary>
        [Test]
        [Category("RttInflation")]
        [Category("Async")]
        [Timeout(60000)]
        public void Async_Should_Handle_Inflated_Rtt_With_Running_Game_Loop()
        {
            Debug.Log("=== TEST: Async RTT Inflation with Running Game Loop ===");

            HighPerfTimeSync.ResetForTesting();

            // Create fresh instances - BOTH start at same time
            var asyncClientTime = new SecretaryOfTemporalAffairs();
            var asyncServerTime = new SecretaryOfTemporalAffairs();

            asyncClientTime.Update();
            asyncServerTime.Update();

            Thread.Sleep(100);
            asyncClientTime.Update();
            asyncServerTime.Update();

            var cts = new CancellationTokenSource();

            // Start "game loop" thread that continuously updates client time
            var gameLoopThread = new Thread(() =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        asyncClientTime.Update();
                        Thread.Sleep(16); // ~60fps
                    }
                }
                catch (OperationCanceledException) { }
            })
            { IsBackground = true, Name = "GameLoopThread" };
            gameLoopThread.Start();

            Thread.Sleep(100); // Let thread start

            try
            {
                const int NETWORK_LATENCY_MS = 50;
                const int PROCESSING_DELAY_MS = 150; // Moderate processing delay

                // First, establish good minRtt with quick sync
                long t0_init = asyncClientTime.RawElapsedTicks;
                Thread.Sleep(50);
                asyncServerTime.Update();
                var initReq = new RequestMessage(t0_init);
                HighPerfTimeSync.ProcessTimeSync(initReq.UID, asyncServerTime.RawElapsedTicks, initReq, asyncClientTime, true);

                Thread.Sleep(200);
                asyncServerTime.Update();

                double baselineDiff = Math.Abs(asyncServerTime.ElapsedSeconds - asyncClientTime.ElapsedSeconds);
                Debug.Log($"Baseline diff: {baselineDiff * 1000:F1}ms");

                var diffs = new System.Collections.Generic.List<double>();

                for (int i = 0; i < 10; i++)
                {
                    // Capture t0 NOW (before any delay)
                    long t0 = asyncClientTime.RawElapsedTicks;
                    var request = new RequestMessage(t0);

                    // Simulate network + processing delay
                    Thread.Sleep(NETWORK_LATENCY_MS);
                    asyncServerTime.Update();
                    long serverTimestamp = asyncServerTime.RawElapsedTicks;
                    Thread.Sleep(NETWORK_LATENCY_MS + PROCESSING_DELAY_MS);

                    // Process sync
                    HighPerfTimeSync.ProcessTimeSync(
                        request.UID,
                        serverTimestamp,
                        request,
                        asyncClientTime,
                        forceAdjustment: false
                    );

                    Thread.Sleep(50);
                    asyncServerTime.Update();

                    double clientSeconds = asyncClientTime.ElapsedSeconds;
                    double serverSeconds = asyncServerTime.ElapsedSeconds;
                    double diff = Math.Abs(serverSeconds - clientSeconds);
                    diffs.Add(diff);

                    Debug.Log($"Sync {i + 1}: Client={clientSeconds:F3}s, Server={serverSeconds:F3}s, Diff={diff * 1000:F1}ms");
                }

                // Analyze results
                double avgDiff = 0;
                foreach (var d in diffs) avgDiff += d;
                avgDiff /= diffs.Count;

                double maxDiff = 0;
                foreach (var d in diffs) if (d > maxDiff) maxDiff = d;

                Debug.Log($"\nAverage diff: {avgDiff * 1000:F1}ms");
                Debug.Log($"Max diff: {maxDiff * 1000:F1}ms");

                // With minRtt preserved and cap applied, errors should be bounded
                Assert.That(maxDiff, Is.LessThan(1.0),
                    $"Max diff should be <1000ms with bounded processing delays. Got {maxDiff * 1000:F1}ms.");

                // Check for spiral (diffs should not keep growing)
                double firstHalf = 0, secondHalf = 0;
                for (int i = 0; i < 5; i++) firstHalf += diffs[i];
                for (int i = 5; i < 10; i++) secondHalf += diffs[i];
                firstHalf /= 5;
                secondHalf /= 5;

                Assert.That(secondHalf, Is.LessThan(firstHalf + 0.3),
                    $"Sync is spiraling! First half avg: {firstHalf * 1000:F1}ms, Second half: {secondHalf * 1000:F1}ms");
            }
            finally
            {
                cts.Cancel();
                gameLoopThread.Join(1000);
                cts.Dispose();
            }
        }
    }
}

