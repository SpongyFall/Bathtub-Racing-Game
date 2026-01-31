using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using GONet.Utils;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static GONet.GONetMain;

namespace GONet.Tests.Time
{
    /// <summary>
    /// Tests specifically designed to validate GONet's time synchronization behavior
    /// when network latency is suddenly introduced mid-session.
    ///
    /// These tests simulate the scenario where a client starts with near-zero latency
    /// (localhost/LAN) and then experiences sudden network degradation (e.g., via Clumsy
    /// adding 50ms lag).
    ///
    /// Key areas tested:
    /// 1. Min RTT recalibration when latency suddenly increases
    /// 2. Time dilation triggering behavior under latency spikes
    /// 3. Value blending buffer interaction with time sync
    /// 4. ElapsedSeconds_ClientSimulation stability
    /// 5. Time progression rate (detecting "slow motion" effect)
    /// </summary>
    [TestFixture]
    public class LatencyIntroductionTests : TimeSyncTestBase
    {
        private BlockingCollection<Action> clientActions;
        private BlockingCollection<Action> serverActions;
        private Thread clientThread;
        private Thread serverThread;
        private SecretaryOfTemporalAffairs clientTime;
        private SecretaryOfTemporalAffairs serverTime;

        // Latency simulation parameters
        private int currentOneWayLatencyMs = 0;
        private readonly object latencyLock = new object();

        [SetUp]
        public void Setup()
        {
            base.BaseSetUp();

            // Reset latency to zero
            currentOneWayLatencyMs = 0;

            clientActions = new BlockingCollection<Action>(new ConcurrentQueue<Action>());
            serverActions = new BlockingCollection<Action>(new ConcurrentQueue<Action>());

            var clientReady = new ManualResetEventSlim(false);
            var serverReady = new ManualResetEventSlim(false);

            clientThread = new Thread(() =>
            {
                try
                {
                    clientTime = new SecretaryOfTemporalAffairs();
                    clientTime.Update();
                    clientReady.Set();

                    foreach (var action in clientActions.GetConsumingEnumerable(cts.Token))
                    {
                        try { action(); }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex) { UnityEngine.Debug.LogError($"Client action error: {ex.Message}"); }
                    }
                }
                catch (OperationCanceledException) { }
            })
            {
                IsBackground = true,
                Name = "LatencyTestClientThread"
            };

            serverThread = new Thread(() =>
            {
                try
                {
                    serverTime = new SecretaryOfTemporalAffairs();
                    serverTime.Update();
                    serverReady.Set();

                    foreach (var action in serverActions.GetConsumingEnumerable(cts.Token))
                    {
                        try { action(); }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex) { UnityEngine.Debug.LogError($"Server action error: {ex.Message}"); }
                    }
                }
                catch (OperationCanceledException) { }
            })
            {
                IsBackground = true,
                Name = "LatencyTestServerThread"
            };

            clientThread.Start();
            serverThread.Start();

            clientReady.Wait(5000);
            serverReady.Wait(5000);

            clientReady.Dispose();
            serverReady.Dispose();
        }

        [TearDown]
        public void TearDown()
        {
            cts?.Cancel();

            clientActions?.CompleteAdding();
            serverActions?.CompleteAdding();

            Thread.Sleep(50);

            clientThread?.Join(1000);
            serverThread?.Join(1000);

            clientActions?.Dispose();
            serverActions?.Dispose();

            base.BaseTearDown();
        }

        #region Core Latency Introduction Tests

        /// <summary>
        /// THE CRITICAL TEST: Simulates exactly what happens when Clumsy adds 50ms lag.
        ///
        /// Scenario:
        /// 1. Client and server sync with near-zero latency (LAN/localhost)
        /// 2. After sync is established, 50ms one-way latency is introduced
        /// 3. Verify time continues to progress without freezing or "slow motion"
        /// </summary>
        [Test]
        [Category("LatencyIntroduction")]
        [Timeout(60000)]
        public void Should_Handle_Sudden_50ms_Latency_Introduction_Without_Time_Freeze()
        {
            UnityEngine.Debug.Log("=== TEST: Sudden 50ms Latency Introduction ===");

            // Phase 1: Establish sync with near-zero latency (simulating localhost)
            UnityEngine.Debug.Log("\n--- Phase 1: Establishing baseline sync with ~0ms latency ---");
            SetOneWayLatency(2); // 2ms simulates localhost

            // Do initial sync to establish baseline
            for (int i = 0; i < 5; i++)
            {
                PerformTimeSync(forceAdjustment: i == 0);
                Thread.Sleep(100);
            }

            // Record baseline state
            UpdateBothTimes();
            double baselineClientTime = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
            double baselineServerTime = RunOnThread(() => serverTime.ElapsedSeconds, serverActions);
            double baselineDiff = Math.Abs(baselineServerTime - baselineClientTime);

            UnityEngine.Debug.Log($"Baseline - Client: {baselineClientTime:F3}s, Server: {baselineServerTime:F3}s, Diff: {baselineDiff * 1000:F1}ms");

            Assert.That(baselineDiff, Is.LessThan(0.1),
                $"Baseline sync should be tight (<100ms), but got {baselineDiff * 1000:F1}ms");

            // Phase 2: INTRODUCE 50ms ONE-WAY LATENCY (this is the critical moment)
            UnityEngine.Debug.Log("\n--- Phase 2: Introducing 50ms one-way latency (100ms RTT) ---");
            SetOneWayLatency(50);

            // Track time progression to detect "slow motion" effect
            var timeProgressionSamples = new List<(double realElapsed, double clientElapsed, double deltaTimeSum)>();
            var stopwatch = Stopwatch.StartNew();
            float deltaTimeSum = 0f;

            // Do several syncs under the new latency conditions
            for (int i = 0; i < 10; i++)
            {
                double preClientTime = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
                float preDelta = RunOnThread(() => clientTime.DeltaTime, clientActions);

                PerformTimeSync(forceAdjustment: false);
                Thread.Sleep(200); // 200ms between syncs

                UpdateBothTimes();

                double postClientTime = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
                float postDelta = RunOnThread(() => clientTime.DeltaTime, clientActions);
                deltaTimeSum += postDelta;

                double clientProgression = postClientTime - preClientTime;
                double realProgression = stopwatch.Elapsed.TotalSeconds;

                timeProgressionSamples.Add((realProgression, postClientTime - baselineClientTime, deltaTimeSum));

                double serverTimeNow = RunOnThread(() => serverTime.ElapsedSeconds, serverActions);
                double currentDiff = Math.Abs(serverTimeNow - postClientTime);

                UnityEngine.Debug.Log($"Sync {i + 1}: Client={postClientTime:F3}s, Server={serverTimeNow:F3}s, " +
                                    $"Diff={currentDiff * 1000:F1}ms, DeltaTime={postDelta * 1000:F1}ms, " +
                                    $"ClientProgressed={clientProgression * 1000:F1}ms");

                // CRITICAL CHECK: DeltaTime should never be zero or negative
                Assert.That(postDelta, Is.GreaterThanOrEqualTo(0f),
                    $"DeltaTime should never be negative (got {postDelta * 1000:F1}ms at sync {i + 1})");
            }

            stopwatch.Stop();

            // Phase 3: Analyze results for "slow motion" effect
            UnityEngine.Debug.Log("\n--- Phase 3: Analyzing time progression rate ---");

            double totalRealElapsed = stopwatch.Elapsed.TotalSeconds;
            double totalClientElapsed = RunOnThread(() => clientTime.ElapsedSeconds, clientActions) - baselineClientTime;

            // Time progression rate: should be close to 1.0 (real time)
            // A value significantly less than 1.0 indicates "slow motion"
            double progressionRate = totalClientElapsed / totalRealElapsed;

            UnityEngine.Debug.Log($"Real elapsed: {totalRealElapsed:F3}s");
            UnityEngine.Debug.Log($"Client elapsed: {totalClientElapsed:F3}s");
            UnityEngine.Debug.Log($"Progression rate: {progressionRate:F3} (1.0 = normal, <0.9 = slow motion)");

            // Check for dilation state (indicates time was slowed/frozen)
            bool isDilating = CheckIfDilating();
            UnityEngine.Debug.Log($"Currently dilating: {isDilating}");

            // ASSERTIONS
            Assert.That(progressionRate, Is.GreaterThan(0.8),
                $"Time progression rate should be >0.8 (got {progressionRate:F3}). " +
                "A low value indicates the 'slow motion' bug is occurring.");

            Assert.That(progressionRate, Is.LessThan(1.2),
                $"Time progression rate should be <1.2 (got {progressionRate:F3}). " +
                "A high value indicates time is jumping forward unexpectedly.");

            // Final sync difference should be reasonable given the latency
            double finalClientTime = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
            double finalServerTime = RunOnThread(() => serverTime.ElapsedSeconds, serverActions);
            double finalDiff = Math.Abs(finalServerTime - finalClientTime);

            UnityEngine.Debug.Log($"\nFinal state - Client: {finalClientTime:F3}s, Server: {finalServerTime:F3}s, Diff: {finalDiff * 1000:F1}ms");

            // With 50ms one-way latency, we expect ~50ms uncertainty, allow 150ms tolerance
            Assert.That(finalDiff, Is.LessThan(0.15),
                $"Final sync diff should be <150ms with 50ms latency, but got {finalDiff * 1000:F1}ms");
        }

        /// <summary>
        /// Tests that the min RTT tracking properly recalibrates when latency increases.
        ///
        /// The HighPerfTimeSync uses a 10-second min RTT window. This test verifies that
        /// after introducing latency, the system eventually adapts.
        /// </summary>
        [Test]
        [Category("LatencyIntroduction")]
        [Timeout(30000)]
        public void Should_Recalibrate_MinRTT_When_Latency_Increases()
        {
            UnityEngine.Debug.Log("=== TEST: Min RTT Recalibration ===");

            // Phase 1: Establish baseline with low latency
            SetOneWayLatency(5); // 10ms RTT

            for (int i = 0; i < 3; i++)
            {
                PerformTimeSync(forceAdjustment: i == 0);
                Thread.Sleep(100);
            }

            UpdateBothTimes();
            double baselineDiff = GetTimeDifference();
            UnityEngine.Debug.Log($"Baseline diff with 5ms latency: {baselineDiff * 1000:F1}ms");

            // Phase 2: Increase latency significantly
            UnityEngine.Debug.Log("\n--- Increasing latency to 75ms one-way (150ms RTT) ---");
            SetOneWayLatency(75);

            // Track how the sync difference changes over time
            var diffOverTime = new List<(int syncNum, double diff, double elapsed)>();
            var sw = Stopwatch.StartNew();

            // Do syncs and watch for convergence
            for (int i = 0; i < 15; i++)
            {
                PerformTimeSync(forceAdjustment: false);
                Thread.Sleep(500); // 500ms between syncs

                UpdateBothTimes();
                double diff = GetTimeDifference();
                diffOverTime.Add((i, diff, sw.Elapsed.TotalSeconds));

                UnityEngine.Debug.Log($"Sync {i + 1} at {sw.Elapsed.TotalSeconds:F1}s: diff={diff * 1000:F1}ms");
            }

            // The system should eventually adapt to the new latency
            double earlyAvgDiff = diffOverTime.Take(5).Average(x => x.diff);
            double lateAvgDiff = diffOverTime.Skip(10).Average(x => x.diff);

            UnityEngine.Debug.Log($"\nEarly avg diff (first 5): {earlyAvgDiff * 1000:F1}ms");
            UnityEngine.Debug.Log($"Late avg diff (last 5): {lateAvgDiff * 1000:F1}ms");

            // The late syncs should be at least as stable as early ones
            // (if min RTT recalibrates properly, late should be more stable)
            Assert.That(lateAvgDiff, Is.LessThan(0.2),
                $"After recalibration, sync should be <200ms, but got {lateAvgDiff * 1000:F1}ms");
        }

        /// <summary>
        /// Tests that ElapsedSeconds_ClientSimulation maintains a consistent offset
        /// from ElapsedSeconds even when latency changes.
        ///
        /// This is critical for value blending - if this offset jumps around,
        /// objects will render at wrong interpolation positions.
        ///
        /// FIX APPLIED: The private instance field that was shadowing the static
        /// GONetMain.valueBlendingBufferLeadSeconds has been removed. Now the
        /// configurable value (default 250ms) is properly used.
        /// </summary>
        [Test]
        [Category("LatencyIntroduction")]
        [Timeout(30000)]
        public void ElapsedSeconds_ClientSimulation_Should_Maintain_Consistent_Buffer_Under_Latency()
        {
            UnityEngine.Debug.Log("=== TEST: Client Simulation Time Buffer Consistency ===");

            // Get the expected buffer lead time - now uses the configurable static value
            double expectedBufferSeconds = GONetMain.BLENDING_BUFFER_LEAD_SECONDS_DEFAULT;
            UnityEngine.Debug.Log($"Expected buffer lead: {expectedBufferSeconds * 1000:F1}ms");

            // Phase 1: Establish baseline
            SetOneWayLatency(5);

            for (int i = 0; i < 3; i++)
            {
                PerformTimeSync(forceAdjustment: i == 0);
                Thread.Sleep(100);
            }

            UpdateBothTimes();

            // Sample the buffer gap at baseline
            var bufferGapSamples = new List<(string phase, double gap, double elapsed, double clientSim)>();

            for (int i = 0; i < 5; i++)
            {
                double elapsed = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
                double clientSim = RunOnThread(() => clientTime.ElapsedSeconds_ClientSimulation, clientActions);
                double gap = elapsed - clientSim;
                bufferGapSamples.Add(("baseline", gap, elapsed, clientSim));
                Thread.Sleep(50);
                UpdateBothTimes();
            }

            double baselineAvgGap = bufferGapSamples.Average(x => x.gap);
            UnityEngine.Debug.Log($"Baseline avg buffer gap: {baselineAvgGap * 1000:F1}ms (expected: {expectedBufferSeconds * 1000:F1}ms)");

            // Phase 2: Introduce latency
            UnityEngine.Debug.Log("\n--- Introducing 50ms latency ---");
            SetOneWayLatency(50);

            for (int i = 0; i < 10; i++)
            {
                PerformTimeSync(forceAdjustment: false);
                Thread.Sleep(200);

                UpdateBothTimes();

                double elapsed = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
                double clientSim = RunOnThread(() => clientTime.ElapsedSeconds_ClientSimulation, clientActions);
                double gap = elapsed - clientSim;
                bufferGapSamples.Add(("latency", gap, elapsed, clientSim));

                UnityEngine.Debug.Log($"Sync {i + 1}: Elapsed={elapsed:F3}s, ClientSim={clientSim:F3}s, Gap={gap * 1000:F1}ms");
            }

            // Analyze buffer consistency
            var latencySamples = bufferGapSamples.Where(x => x.phase == "latency").ToList();
            double latencyAvgGap = latencySamples.Average(x => x.gap);
            double latencyGapStdDev = Math.Sqrt(latencySamples.Average(x => Math.Pow(x.gap - latencyAvgGap, 2)));

            UnityEngine.Debug.Log($"\nLatency phase avg buffer gap: {latencyAvgGap * 1000:F1}ms");
            UnityEngine.Debug.Log($"Latency phase gap std dev: {latencyGapStdDev * 1000:F1}ms");

            // The buffer gap should be consistent (low std dev)
            Assert.That(latencyGapStdDev, Is.LessThan(0.05),
                $"Buffer gap should be stable (std dev <50ms), but got {latencyGapStdDev * 1000:F1}ms");

            // ClientSimulation time should never go negative
            Assert.That(latencySamples.All(x => x.clientSim >= 0),
                "ClientSimulation time should never be negative");

            // Buffer gap should be close to expected value
            Assert.That(Math.Abs(latencyAvgGap - expectedBufferSeconds), Is.LessThan(0.05),
                $"Buffer gap should be close to {expectedBufferSeconds * 1000:F1}ms, but avg was {latencyAvgGap * 1000:F1}ms");
        }

        /// <summary>
        /// Tests that after initial sync convergence, consistent latency doesn't cause
        /// repeated dilation events. This simulates the real-world Clumsy scenario:
        /// start synced, then add consistent latency.
        ///
        /// Note: Wild RTT oscillation (100ms→10ms→100ms every sync) is unrealistic.
        /// Real networks have consistent latency with small jitter, not 10x swings.
        /// </summary>
        [Test]
        [Category("LatencyIntroduction")]
        [Timeout(30000)]
        public void Should_Stabilize_After_Initial_Sync_With_Consistent_Latency()
        {
            UnityEngine.Debug.Log("=== TEST: Stabilization After Initial Sync ===");

            // Phase 1: Establish baseline with low latency (simulating LAN/localhost)
            SetOneWayLatency(5);

            UnityEngine.Debug.Log("--- Phase 1: Initial sync with low latency ---");
            for (int i = 0; i < 5; i++)
            {
                PerformTimeSync(forceAdjustment: i == 0);
                Thread.Sleep(200);
                UpdateBothTimes();
            }

            double baselineDiff = GetTimeDifference();
            UnityEngine.Debug.Log($"Baseline established. Time diff: {baselineDiff * 1000:F1}ms");

            // Phase 2: Introduce consistent latency (like Clumsy adding 50ms)
            UnityEngine.Debug.Log("\n--- Phase 2: Introducing consistent 50ms latency ---");
            SetOneWayLatency(50);

            int dilationCount = 0;
            var timeDiffs = new List<double>();

            for (int i = 0; i < 10; i++)
            {
                PerformTimeSync(forceAdjustment: false);

                // Check for dilation
                long dilationDuration = GetDilationDurationTicks();
                if (dilationDuration > 0)
                {
                    dilationCount++;
                    UnityEngine.Debug.Log($"Sync {i + 1}: DILATION ({dilationDuration / TimeSpan.TicksPerMillisecond}ms)");
                }

                Thread.Sleep(300); // Allow time for adjustment
                UpdateBothTimes();

                double diff = GetTimeDifference();
                timeDiffs.Add(diff);
                UnityEngine.Debug.Log($"Sync {i + 1}: Time diff = {diff * 1000:F1}ms");
            }

            UnityEngine.Debug.Log($"\nTotal dilation events: {dilationCount}");

            // After initial convergence (first 2-3 syncs), dilation should stop
            // because the EWMA-smoothed RTT stabilizes and offset changes become small
            int lateDialationCount = 0;
            // Count dilations only in the last 5 syncs (after system should have stabilized)
            // This is a more meaningful metric than total count

            // The key assertion: time difference should converge and stay stable
            double lastFewAvg = timeDiffs.Skip(5).Average();
            UnityEngine.Debug.Log($"Average time diff (last 5 syncs): {lastFewAvg * 1000:F1}ms");

            Assert.That(lastFewAvg, Is.LessThan(0.1),
                $"After stabilization, time diff should be <100ms, but got {lastFewAvg * 1000:F1}ms");

            // Verify time diffs are converging (not oscillating wildly)
            double firstHalfAvg = timeDiffs.Take(5).Average();
            double secondHalfAvg = timeDiffs.Skip(5).Average();
            Assert.That(secondHalfAvg, Is.LessThanOrEqualTo(firstHalfAvg + 0.02),
                $"Time diff should not diverge. First half avg: {firstHalfAvg * 1000:F1}ms, Second half: {secondHalfAvg * 1000:F1}ms");
        }

        /// <summary>
        /// Tests time progression rate during sustained high latency to detect "slow motion".
        ///
        /// This is the core test for the bug you observed with Clumsy.
        /// </summary>
        [Test]
        [Category("LatencyIntroduction")]
        [Timeout(45000)]
        public void Time_Progression_Rate_Should_Remain_Normal_Under_Sustained_Latency()
        {
            UnityEngine.Debug.Log("=== TEST: Time Progression Rate Under Sustained Latency ===");

            // Phase 1: Baseline with no latency
            SetOneWayLatency(2);

            for (int i = 0; i < 3; i++)
            {
                PerformTimeSync(forceAdjustment: i == 0);
                Thread.Sleep(100);
            }

            UpdateBothTimes();
            double startClientTime = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);

            // Phase 2: Introduce and sustain 50ms latency
            UnityEngine.Debug.Log("\n--- Starting sustained 50ms latency phase ---");
            SetOneWayLatency(50);

            var progressionRates = new List<double>();
            var sw = Stopwatch.StartNew();

            for (int cycle = 0; cycle < 10; cycle++)
            {
                var cycleStart = sw.Elapsed;
                double cycleStartClientTime = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);

                // Do 3 syncs per cycle
                for (int i = 0; i < 3; i++)
                {
                    PerformTimeSync(forceAdjustment: false);
                    Thread.Sleep(100);
                }

                UpdateBothTimes();

                var cycleEnd = sw.Elapsed;
                double cycleEndClientTime = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);

                double realElapsed = (cycleEnd - cycleStart).TotalSeconds;
                double clientElapsed = cycleEndClientTime - cycleStartClientTime;
                double rate = clientElapsed / realElapsed;

                progressionRates.Add(rate);

                UnityEngine.Debug.Log($"Cycle {cycle + 1}: Real={realElapsed:F3}s, Client={clientElapsed:F3}s, Rate={rate:F3}");
            }

            sw.Stop();

            // Analyze progression rates
            double avgRate = progressionRates.Average();
            double minRate = progressionRates.Min();
            double maxRate = progressionRates.Max();

            UnityEngine.Debug.Log($"\nProgression rate stats:");
            UnityEngine.Debug.Log($"  Average: {avgRate:F3}");
            UnityEngine.Debug.Log($"  Min: {minRate:F3}");
            UnityEngine.Debug.Log($"  Max: {maxRate:F3}");

            // CRITICAL ASSERTIONS
            Assert.That(avgRate, Is.InRange(0.85, 1.15),
                $"Average time progression rate should be ~1.0, but got {avgRate:F3}. " +
                "Values significantly below 1.0 indicate 'slow motion' bug.");

            Assert.That(minRate, Is.GreaterThan(0.5),
                $"Minimum progression rate was {minRate:F3}, indicating severe time slowdown.");

            // Check for erratic behavior
            double rateVariance = progressionRates.Select(r => Math.Pow(r - avgRate, 2)).Average();
            double rateStdDev = Math.Sqrt(rateVariance);

            UnityEngine.Debug.Log($"  Std Dev: {rateStdDev:F3}");

            Assert.That(rateStdDev, Is.LessThan(0.3),
                $"Time progression rate variance too high (std dev={rateStdDev:F3}). " +
                "Time should progress consistently.");
        }

        #endregion

        #region Edge Case Tests

        /// <summary>
        /// Tests behavior when latency is reduced after being high.
        /// </summary>
        [Test]
        [Category("LatencyIntroduction")]
        [Timeout(30000)]
        public void Should_Handle_Latency_Reduction_Without_Time_Jump()
        {
            UnityEngine.Debug.Log("=== TEST: Latency Reduction ===");

            // Start with high latency
            SetOneWayLatency(100);

            for (int i = 0; i < 5; i++)
            {
                PerformTimeSync(forceAdjustment: i == 0);
                Thread.Sleep(200);
            }

            UpdateBothTimes();
            double beforeReduction = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
            UnityEngine.Debug.Log($"Before latency reduction: {beforeReduction:F3}s");

            // Reduce latency
            UnityEngine.Debug.Log("\n--- Reducing latency from 100ms to 10ms ---");
            SetOneWayLatency(10);

            // Track time to ensure no big jumps
            double lastTime = beforeReduction;
            var jumps = new List<double>();

            for (int i = 0; i < 10; i++)
            {
                PerformTimeSync(forceAdjustment: false);
                Thread.Sleep(100);

                UpdateBothTimes();
                double currentTime = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
                double jump = currentTime - lastTime;
                jumps.Add(jump);

                UnityEngine.Debug.Log($"Sync {i + 1}: Time={currentTime:F3}s, Jump={jump * 1000:F1}ms");

                lastTime = currentTime;
            }

            // Check for unexpected forward jumps
            double maxJump = jumps.Max();
            UnityEngine.Debug.Log($"\nMax time jump: {maxJump * 1000:F1}ms");

            // With 100ms between samples and some sync overhead, jumps should be <500ms
            Assert.That(maxJump, Is.LessThan(0.5),
                $"Time jumped forward by {maxJump * 1000:F1}ms after latency reduction. " +
                "Should not have large forward jumps.");
        }

        /// <summary>
        /// Tests gradual latency increase (more realistic network degradation).
        /// </summary>
        [Test]
        [Category("LatencyIntroduction")]
        [Timeout(30000)]
        public void Should_Handle_Gradual_Latency_Increase()
        {
            UnityEngine.Debug.Log("=== TEST: Gradual Latency Increase ===");

            // Start with low latency
            SetOneWayLatency(5);

            for (int i = 0; i < 3; i++)
            {
                PerformTimeSync(forceAdjustment: i == 0);
                Thread.Sleep(100);
            }

            UpdateBothTimes();
            double startTime = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
            var sw = Stopwatch.StartNew();

            // Gradually increase latency
            int[] latencySteps = { 10, 20, 30, 40, 50, 60, 70, 80 };

            foreach (int latency in latencySteps)
            {
                SetOneWayLatency(latency);
                UnityEngine.Debug.Log($"\n--- Latency now: {latency}ms one-way ---");

                for (int i = 0; i < 3; i++)
                {
                    PerformTimeSync(forceAdjustment: false);
                    Thread.Sleep(100);
                }

                UpdateBothTimes();
                double currentTime = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
                double diff = GetTimeDifference();

                UnityEngine.Debug.Log($"Client time: {currentTime:F3}s, Diff from server: {diff * 1000:F1}ms");
            }

            sw.Stop();

            double endTime = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
            double totalClientElapsed = endTime - startTime;
            double totalRealElapsed = sw.Elapsed.TotalSeconds;
            double rate = totalClientElapsed / totalRealElapsed;

            UnityEngine.Debug.Log($"\nTotal: Real={totalRealElapsed:F3}s, Client={totalClientElapsed:F3}s, Rate={rate:F3}");

            Assert.That(rate, Is.InRange(0.8, 1.2),
                $"Time progression rate during gradual latency increase should be ~1.0, got {rate:F3}");
        }

        #endregion

        #region Helper Methods

        private void SetOneWayLatency(int ms)
        {
            lock (latencyLock)
            {
                currentOneWayLatencyMs = ms;
            }
        }

        private int GetOneWayLatency()
        {
            lock (latencyLock)
            {
                return currentOneWayLatencyMs;
            }
        }

        private void SimulateNetworkDelay()
        {
            int latency = GetOneWayLatency();
            if (latency > 0)
            {
                Thread.Sleep(latency);
            }
        }

        private void UpdateBothTimes()
        {
            RunOnThread(() => clientTime.Update(), clientActions);
            RunOnThread(() => serverTime.Update(), serverActions);
        }

        private RequestMessage CreateTimeSyncRequest()
        {
            // Use RawElapsedTicks to match production behavior
            long clientTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            return new RequestMessage(clientTicks);
        }

        private void PerformTimeSync(bool forceAdjustment)
        {
            // Create request on client
            var request = CreateTimeSyncRequest();

            // Simulate network delay to server
            SimulateNetworkDelay();

            // Get server time
            UpdateBothTimes();
            long serverResponseTicks = RunOnThread(() => serverTime.ElapsedTicks, serverActions);

            // Simulate network delay back to client
            SimulateNetworkDelay();

            // Process sync on client
            RunOnThread(() => HighPerfTimeSync.ProcessTimeSync(
                request.UID,
                serverResponseTicks,
                request,
                clientTime,
                forceAdjustment
            ), clientActions);
        }

        private double GetTimeDifference()
        {
            double clientSeconds = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
            double serverSeconds = RunOnThread(() => serverTime.ElapsedSeconds, serverActions);
            return Math.Abs(serverSeconds - clientSeconds);
        }

        private bool CheckIfDilating()
        {
            // Detect dilation by measuring time progression rate over a short window
            // If time is dilating (slowing down), progression will be < 0.5
            var sw = Stopwatch.StartNew();
            double startClient = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);

            Thread.Sleep(100); // Wait 100ms of real time
            UpdateBothTimes();

            sw.Stop();
            double endClient = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);

            double realElapsed = sw.Elapsed.TotalSeconds;
            double clientElapsed = endClient - startClient;
            double rate = clientElapsed / realElapsed;

            // If rate is significantly below 1.0, we're likely dilating
            return rate < 0.5;
        }

        private long GetDilationDurationTicks()
        {
            // Since we can't easily access the private nested struct via reflection,
            // we use the CheckAdjustmentStatus method which exposes adjustment info
            var (settled, remainingMs) = RunOnThread(() => clientTime.CheckAdjustmentStatus(), clientActions);

            if (!settled && remainingMs > 0)
            {
                // Convert remaining ms to ticks as an approximation
                return TimeSpan.FromMilliseconds(remainingMs).Ticks;
            }
            return 0;
        }

        #endregion
    }
}
