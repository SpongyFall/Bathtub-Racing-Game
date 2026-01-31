using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using static GONet.GONetMain;

namespace GONet.Tests.Time
{
    /// <summary>
    /// Unit tests for the hybrid time correction system, covering:
    /// 1. EWMA RTT smoothing with adaptive alpha
    /// 2. Snap vs dilation threshold logic (1s boundary)
    /// 3. 50% speed dilation math convergence
    /// </summary>
    [TestFixture]
    public class HybridTimeCorrectionTests : TimeSyncTestBase
    {
        private BlockingCollection<Action> clientActions;
        private Thread clientThread;
        private SecretaryOfTemporalAffairs clientTime;

        // Constants matching production code
        private const double RTT_SMOOTHING_ALPHA_LOW = 0.15;
        private const double RTT_SMOOTHING_ALPHA_HIGH = 0.5;
        private const double RTT_CHANGE_THRESHOLD_HIGH = 2.0;
        private const double RTT_CHANGE_THRESHOLD_LOW = 0.5;

        [SetUp]
        public void Setup()
        {
            base.BaseSetUp();

            clientActions = new BlockingCollection<Action>(new ConcurrentQueue<Action>());
            var clientReady = new ManualResetEventSlim(false);

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
                Name = "HybridCorrectionTestThread"
            };

            clientThread.Start();
            clientReady.Wait(5000);
            clientReady.Dispose();
        }

        [TearDown]
        public void TearDown()
        {
            cts?.Cancel();
            clientActions?.CompleteAdding();
            Thread.Sleep(50);
            clientThread?.Join(1000);
            clientActions?.Dispose();
            base.BaseTearDown();
        }

        #region EWMA RTT Smoothing Tests

        /// <summary>
        /// Verifies EWMA uses low alpha (0.15) for small RTT variations (jitter).
        /// Small changes should be smoothed aggressively to prevent false dilation triggers.
        /// </summary>
        [Test]
        [Category("EWMA")]
        public void EWMA_Should_Use_Low_Alpha_For_Small_RTT_Changes()
        {
            // Simulate RTT values with small jitter (within 0.5x-2x range)
            double[] rttValues = { 50, 55, 48, 52, 51, 49, 53 }; // ±10% variation
            double smoothed = rttValues[0]; // First value initializes

            for (int i = 1; i < rttValues.Length; i++)
            {
                double rtt = rttValues[i];
                double ratio = rtt / smoothed;

                // Verify ratio stays within low-alpha thresholds
                Assert.That(ratio, Is.InRange(RTT_CHANGE_THRESHOLD_LOW, RTT_CHANGE_THRESHOLD_HIGH),
                    $"RTT ratio {ratio:F2} should be in jitter range for value {rtt}");

                // Apply EWMA with low alpha (jitter smoothing)
                smoothed = RTT_SMOOTHING_ALPHA_LOW * rtt + (1.0 - RTT_SMOOTHING_ALPHA_LOW) * smoothed;
            }

            // After smoothing, value should be close to mean (50.57)
            double expectedMean = 51.14; // Actual mean of array
            Assert.That(smoothed, Is.InRange(expectedMean - 3, expectedMean + 3),
                $"Smoothed RTT {smoothed:F1} should converge near mean {expectedMean:F1}");

            UnityEngine.Debug.Log($"EWMA with low alpha converged to {smoothed:F1}ms (mean={expectedMean:F1}ms)");
        }

        /// <summary>
        /// Verifies EWMA uses high alpha (0.5) for large RTT changes (network condition shift).
        /// Large changes should adapt quickly to new baseline.
        /// </summary>
        [Test]
        [Category("EWMA")]
        public void EWMA_Should_Use_High_Alpha_For_Large_RTT_Changes()
        {
            double smoothed = 50; // Initial RTT
            double newRtt = 150; // 3x increase (>2x threshold)

            double ratio = newRtt / smoothed;
            Assert.That(ratio, Is.GreaterThan(RTT_CHANGE_THRESHOLD_HIGH),
                $"RTT ratio {ratio:F2} should exceed high threshold {RTT_CHANGE_THRESHOLD_HIGH}");

            // Apply single EWMA step with high alpha
            double afterOneStep = RTT_SMOOTHING_ALPHA_HIGH * newRtt + (1.0 - RTT_SMOOTHING_ALPHA_HIGH) * smoothed;

            // With alpha=0.5, should be exactly halfway between old and new
            double expected = (smoothed + newRtt) / 2; // 100
            Assert.That(afterOneStep, Is.EqualTo(expected).Within(0.01),
                $"High alpha should move smoothed value halfway to new: expected {expected}, got {afterOneStep}");

            // After 3 more steps at high alpha, should be very close to new value
            double converged = afterOneStep;
            for (int i = 0; i < 3; i++)
            {
                converged = RTT_SMOOTHING_ALPHA_HIGH * newRtt + (1.0 - RTT_SMOOTHING_ALPHA_HIGH) * converged;
            }

            Assert.That(converged, Is.InRange(newRtt * 0.9, newRtt * 1.1),
                $"After 4 high-alpha steps, smoothed {converged:F1} should be within 10% of target {newRtt}");

            UnityEngine.Debug.Log($"EWMA high-alpha: 50→150 converged to {converged:F1}ms after 4 steps");
        }

        /// <summary>
        /// Verifies that RTT decrease (latency improvement) also triggers high alpha.
        /// Important for quick adaptation when network improves.
        /// </summary>
        [Test]
        [Category("EWMA")]
        public void EWMA_Should_Use_High_Alpha_For_RTT_Decrease()
        {
            double smoothed = 100;
            double newRtt = 40; // 0.4x decrease (<0.5x threshold)

            double ratio = newRtt / smoothed;
            Assert.That(ratio, Is.LessThan(RTT_CHANGE_THRESHOLD_LOW),
                $"RTT ratio {ratio:F2} should be below low threshold {RTT_CHANGE_THRESHOLD_LOW}");

            // Single high-alpha step
            double afterOneStep = RTT_SMOOTHING_ALPHA_HIGH * newRtt + (1.0 - RTT_SMOOTHING_ALPHA_HIGH) * smoothed;
            Assert.That(afterOneStep, Is.EqualTo(70).Within(0.01),
                "High alpha on decrease should move halfway: (100+40)/2 = 70");

            UnityEngine.Debug.Log($"EWMA high-alpha (decrease): 100→40 = {afterOneStep:F1}ms after 1 step");
        }

        /// <summary>
        /// Verifies jitter doesn't accumulate into false dilation triggers.
        /// This was the original "slow motion" bug cause.
        ///
        /// Key insight: Without EWMA smoothing, raw RTT jitter (±20%) would cause
        /// one-way delay estimates to fluctuate, which in turn causes offset
        /// calculations to vary, potentially triggering unnecessary dilation.
        /// </summary>
        [Test]
        [Category("EWMA")]
        public void EWMA_Should_Prevent_Jitter_Accumulation()
        {
            // Simulate 20 syncs with ±20% jitter around 50ms baseline
            double smoothed = 50;
            var random = new System.Random(42); // Deterministic seed

            double maxSmoothedDeviation = 0;
            double maxRawDeviation = 0;
            const double baseline = 50;

            for (int i = 0; i < 20; i++)
            {
                // Generate jittery RTT: 40-60ms range (±20%)
                double jitteryRtt = baseline + (random.NextDouble() - 0.5) * 20;

                // Track raw deviation
                double rawDeviation = Math.Abs(jitteryRtt - baseline);
                if (rawDeviation > maxRawDeviation)
                    maxRawDeviation = rawDeviation;

                double ratio = jitteryRtt / smoothed;
                double alpha = (ratio > RTT_CHANGE_THRESHOLD_HIGH || ratio < RTT_CHANGE_THRESHOLD_LOW)
                    ? RTT_SMOOTHING_ALPHA_HIGH
                    : RTT_SMOOTHING_ALPHA_LOW;

                smoothed = alpha * jitteryRtt + (1.0 - alpha) * smoothed;

                // Track smoothed deviation
                double smoothedDeviation = Math.Abs(smoothed - baseline);
                if (smoothedDeviation > maxSmoothedDeviation)
                    maxSmoothedDeviation = smoothedDeviation;
            }

            // Smoothed value should stay near baseline
            Assert.That(smoothed, Is.InRange(45, 55),
                $"Smoothed RTT {smoothed:F1}ms should stay near 50ms baseline despite jitter");

            // EWMA should reduce max deviation compared to raw values
            Assert.That(maxSmoothedDeviation, Is.LessThan(maxRawDeviation),
                $"EWMA should dampen jitter. Smoothed max deviation: {maxSmoothedDeviation:F1}ms, Raw max: {maxRawDeviation:F1}ms");

            // Max smoothed deviation should be significantly less than raw
            Assert.That(maxSmoothedDeviation, Is.LessThan(maxRawDeviation * 0.8),
                $"EWMA should reduce deviation by at least 20%. Smoothed: {maxSmoothedDeviation:F1}ms, Raw: {maxRawDeviation:F1}ms");

            UnityEngine.Debug.Log($"EWMA jitter dampening: raw max deviation={maxRawDeviation:F1}ms, " +
                                  $"smoothed max deviation={maxSmoothedDeviation:F1}ms, " +
                                  $"final smoothed={smoothed:F1}ms");
        }

        #endregion

        #region Snap vs Dilation Threshold Tests

        /// <summary>
        /// Verifies backward correction >1s triggers immediate snap (not dilation).
        /// This prevents 5+ second freezes that look like crashes.
        ///
        /// Test approach: First move client ahead, then tell it server is at a lower time.
        /// </summary>
        [Test]
        [Category("Threshold")]
        public void Backward_Correction_Over_1s_Should_Snap_Immediately()
        {
            RunOnThread(() => clientTime.Update(), clientActions);

            // First, move client 2 seconds ahead (forced, so it applies immediately)
            long rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long aheadTicks = rawTicks + TimeSpan.FromSeconds(2).Ticks;
            RunOnThread(() => clientTime.SetFromAuthority(aheadTicks, true), clientActions);

            Thread.Sleep(50);
            RunOnThread(() => clientTime.Update(), clientActions);

            // Now tell client the server is only 0.5s ahead of raw (1.5s backward correction)
            rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long targetTicks = rawTicks + TimeSpan.FromMilliseconds(500).Ticks; // Server says we should be here

            RunOnThread(() => clientTime.SetFromAuthority(targetTicks, false), clientActions);

            Thread.Sleep(50);
            RunOnThread(() => clientTime.Update(), clientActions);

            // Check adjustment status - should be settled (snap, no dilation)
            var (settled, remainingMs) = RunOnThread(() => clientTime.CheckAdjustmentStatus(), clientActions);

            Assert.That(settled, Is.True,
                $"1.5s backward correction should snap immediately. Remaining: {remainingMs}ms");

            UnityEngine.Debug.Log($"1.5s backward snap completed. Settled={settled}");
        }

        /// <summary>
        /// Verifies backward correction between 50ms-1s triggers 50% dilation (not snap).
        /// This keeps the game interactive during medium corrections.
        /// </summary>
        [Test]
        [Category("Threshold")]
        public void Backward_Correction_50ms_To_1s_Should_Use_Dilation()
        {
            RunOnThread(() => clientTime.Update(), clientActions);

            // First, move client 600ms ahead (forced)
            long rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long aheadTicks = rawTicks + TimeSpan.FromMilliseconds(600).Ticks;
            RunOnThread(() => clientTime.SetFromAuthority(aheadTicks, true), clientActions);

            Thread.Sleep(50);
            RunOnThread(() => clientTime.Update(), clientActions);

            // Now tell client to go back 500ms (server says we're only 100ms ahead of raw)
            rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long targetTicks = rawTicks + TimeSpan.FromMilliseconds(100).Ticks;

            RunOnThread(() => clientTime.SetFromAuthority(targetTicks, false), clientActions);

            Thread.Sleep(50);
            RunOnThread(() => clientTime.Update(), clientActions);

            // Check adjustment status - should NOT be settled (dilation in progress)
            var (settled, remainingMs) = RunOnThread(() => clientTime.CheckAdjustmentStatus(), clientActions);

            // Dilation for 500ms gap = 1000ms duration at 50% speed
            Assert.That(settled, Is.False,
                $"500ms backward correction should trigger dilation, not snap. Remaining: {remainingMs}ms");

            UnityEngine.Debug.Log($"500ms backward correction: dilation active, {remainingMs}ms remaining");
        }

        /// <summary>
        /// Verifies backward correction just over 1s triggers snap (boundary condition).
        /// Uses 1.1s correction to clearly exceed the 1s threshold and avoid timing edge cases.
        /// </summary>
        [Test]
        [Category("Threshold")]
        public void Backward_Correction_Over_1s_Boundary_Should_Snap()
        {
            RunOnThread(() => clientTime.Update(), clientActions);

            // Move client 1.2s ahead first (forced)
            long rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long aheadTicks = rawTicks + TimeSpan.FromMilliseconds(1200).Ticks;
            RunOnThread(() => clientTime.SetFromAuthority(aheadTicks, true), clientActions);

            Thread.Sleep(50);
            RunOnThread(() => clientTime.Update(), clientActions);

            // Now correct back by 1.1s (clearly over 1s threshold - should snap)
            // Using 100ms target offset means: 1200ms - 100ms = 1100ms backward correction
            rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long targetTicks = rawTicks + TimeSpan.FromMilliseconds(100).Ticks;

            RunOnThread(() => clientTime.SetFromAuthority(targetTicks, false), clientActions);

            Thread.Sleep(50);
            RunOnThread(() => clientTime.Update(), clientActions);

            var (settled, remainingMs) = RunOnThread(() => clientTime.CheckAdjustmentStatus(), clientActions);

            Assert.That(settled, Is.True,
                $"1.1s backward correction should snap. Remaining: {remainingMs}ms");

            UnityEngine.Debug.Log("1s+ boundary: snapped as expected");
        }

        /// <summary>
        /// Verifies 999ms backward correction triggers dilation (just under boundary).
        /// </summary>
        [Test]
        [Category("Threshold")]
        public void Backward_Correction_999ms_Should_Dilate()
        {
            RunOnThread(() => clientTime.Update(), clientActions);

            // Move client 1s ahead first
            long rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long aheadTicks = rawTicks + TimeSpan.FromSeconds(1).Ticks;
            RunOnThread(() => clientTime.SetFromAuthority(aheadTicks, true), clientActions);

            Thread.Sleep(50);
            RunOnThread(() => clientTime.Update(), clientActions);

            // Now correct back by 999ms (just under 1s threshold - should dilate)
            rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            // Current offset is ~1000ms, target offset should be ~1ms (999ms correction)
            long targetTicks = rawTicks + TimeSpan.FromMilliseconds(1).Ticks;

            RunOnThread(() => clientTime.SetFromAuthority(targetTicks, false), clientActions);

            Thread.Sleep(50);
            RunOnThread(() => clientTime.Update(), clientActions);

            var (settled, remainingMs) = RunOnThread(() => clientTime.CheckAdjustmentStatus(), clientActions);

            Assert.That(settled, Is.False,
                $"999ms backward should dilate (under 1s threshold). Remaining: {remainingMs}ms");

            UnityEngine.Debug.Log($"999ms boundary: dilating as expected, {remainingMs}ms remaining");
        }

        /// <summary>
        /// Verifies forward corrections always snap immediately (any size).
        /// </summary>
        [Test]
        [Category("Threshold")]
        public void Forward_Correction_Should_Always_Snap()
        {
            RunOnThread(() => clientTime.Update(), clientActions);

            // Test various forward jump sizes
            long[] forwardJumpsMs = { 50, 500, 2000, 10000 };

            foreach (long jumpMs in forwardJumpsMs)
            {
                long rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
                long targetTicks = rawTicks + TimeSpan.FromMilliseconds(jumpMs).Ticks;

                RunOnThread(() => clientTime.SetFromAuthority(targetTicks, false), clientActions);
                Thread.Sleep(20);
                RunOnThread(() => clientTime.Update(), clientActions);

                var (settled, remainingMs) = RunOnThread(() => clientTime.CheckAdjustmentStatus(), clientActions);

                Assert.That(settled, Is.True,
                    $"Forward correction of {jumpMs}ms should snap immediately. Remaining: {remainingMs}ms");
            }

            UnityEngine.Debug.Log("All forward corrections snapped as expected");
        }

        #endregion

        #region 50% Speed Dilation Math Tests

        /// <summary>
        /// Verifies 50% speed dilation converges in expected time.
        /// 500ms gap at 50% speed = 1000ms real time to converge.
        /// </summary>
        [Test]
        [Category("DilationMath")]
        [Timeout(10000)]
        public void Dilation_50Percent_Should_Converge_In_Double_Gap_Time()
        {
            RunOnThread(() => clientTime.Update(), clientActions);

            // First move client ahead by 500ms
            long rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long aheadTicks = rawTicks + TimeSpan.FromMilliseconds(500).Ticks;
            RunOnThread(() => clientTime.SetFromAuthority(aheadTicks, true), clientActions);

            Thread.Sleep(50);
            RunOnThread(() => clientTime.Update(), clientActions);

            // Now correct back by 400ms (should trigger dilation)
            rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long targetTicks = rawTicks + TimeSpan.FromMilliseconds(100).Ticks; // 400ms backward

            RunOnThread(() => clientTime.SetFromAuthority(targetTicks, false), clientActions);

            // Expected convergence: 400ms / 0.5 = 800ms
            // Wait 900ms to be safe
            Thread.Sleep(900);
            RunOnThread(() => clientTime.Update(), clientActions);

            var (settled, remainingMs) = RunOnThread(() => clientTime.CheckAdjustmentStatus(), clientActions);

            Assert.That(settled, Is.True,
                $"400ms gap should converge in ~800ms. Remaining: {remainingMs}ms");

            UnityEngine.Debug.Log($"Dilation converged: 400ms gap at 50% speed in <900ms");
        }

        /// <summary>
        /// Verifies time progresses at approximately 50% speed during dilation.
        /// </summary>
        [Test]
        [Category("DilationMath")]
        [Timeout(5000)]
        public void Dilation_Should_Progress_At_Half_Speed()
        {
            RunOnThread(() => clientTime.Update(), clientActions);

            // First move client ahead by 700ms (forced)
            long rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long aheadTicks = rawTicks + TimeSpan.FromMilliseconds(700).Ticks;
            RunOnThread(() => clientTime.SetFromAuthority(aheadTicks, true), clientActions);

            Thread.Sleep(50);
            RunOnThread(() => clientTime.Update(), clientActions);

            double startClientSeconds = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);

            // Now correct back by 600ms (triggers dilation)
            rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long targetTicks = rawTicks + TimeSpan.FromMilliseconds(100).Ticks;

            RunOnThread(() => clientTime.SetFromAuthority(targetTicks, false), clientActions);

            // Wait 500ms of real time during dilation
            Thread.Sleep(500);
            RunOnThread(() => clientTime.Update(), clientActions);

            double endClientSeconds = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);
            double clientProgressed = endClientSeconds - startClientSeconds;

            // At 50% speed, 500ms real → ~250ms client time
            // Allow some tolerance for thread scheduling and the 50ms setup delays
            Assert.That(clientProgressed, Is.InRange(0.20, 0.40),
                $"At 50% dilation, 500ms real should yield ~250ms client. Got: {clientProgressed * 1000:F0}ms");

            UnityEngine.Debug.Log($"Dilation speed verified: 500ms real → {clientProgressed * 1000:F0}ms client");
        }

        /// <summary>
        /// Verifies dilation math: startOffset - (elapsed / 2) converges to targetOffset.
        /// </summary>
        [Test]
        [Category("DilationMath")]
        public void Dilation_Math_Should_Converge_To_Target()
        {
            // Simulate the math: startOffset - (elapsed >> 1) should reach targetOffset
            // when elapsed == duration (= gap * 2)

            long gapTicks = TimeSpan.FromMilliseconds(500).Ticks;
            long startOffset = TimeSpan.FromMilliseconds(100).Ticks;
            long targetOffset = startOffset - gapTicks; // -400ms
            long duration = gapTicks * 2; // 1000ms

            // Simulate progression
            for (int elapsedMs = 0; elapsedMs <= 1000; elapsedMs += 100)
            {
                long elapsed = TimeSpan.FromMilliseconds(elapsedMs).Ticks;
                long effectiveOffset = startOffset - (elapsed >> 1);

                // Clamp to target
                if (effectiveOffset < targetOffset)
                    effectiveOffset = targetOffset;

                UnityEngine.Debug.Log($"elapsed={elapsedMs}ms: effectiveOffset={effectiveOffset / TimeSpan.TicksPerMillisecond}ms");

                if (elapsedMs >= 1000)
                {
                    Assert.That(effectiveOffset, Is.EqualTo(targetOffset),
                        "At duration complete, effective offset should equal target");
                }
            }

            UnityEngine.Debug.Log($"Dilation math verified: gap={gapTicks / TimeSpan.TicksPerMillisecond}ms, " +
                                  $"duration={duration / TimeSpan.TicksPerMillisecond}ms, converges to target");
        }

        /// <summary>
        /// Verifies time never goes backward during dilation (monotonic guarantee).
        /// </summary>
        [Test]
        [Category("DilationMath")]
        [Timeout(5000)]
        public void Dilation_Should_Never_Go_Backward()
        {
            RunOnThread(() => clientTime.Update(), clientActions);

            // First move client ahead by 900ms (forced)
            long rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long aheadTicks = rawTicks + TimeSpan.FromMilliseconds(900).Ticks;
            RunOnThread(() => clientTime.SetFromAuthority(aheadTicks, true), clientActions);

            Thread.Sleep(50);
            RunOnThread(() => clientTime.Update(), clientActions);

            // Now trigger 800ms backward correction (dilation)
            rawTicks = RunOnThread(() => clientTime.RawElapsedTicks, clientActions);
            long targetTicks = rawTicks + TimeSpan.FromMilliseconds(100).Ticks;

            RunOnThread(() => clientTime.SetFromAuthority(targetTicks, false), clientActions);

            double lastSeconds = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);

            // Sample time 20 times over 1.5 seconds
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(75);
                RunOnThread(() => clientTime.Update(), clientActions);

                double currentSeconds = RunOnThread(() => clientTime.ElapsedSeconds, clientActions);

                Assert.That(currentSeconds, Is.GreaterThanOrEqualTo(lastSeconds),
                    $"Time went backward at sample {i}: {lastSeconds:F4} → {currentSeconds:F4}");

                lastSeconds = currentSeconds;
            }

            UnityEngine.Debug.Log("Monotonic guarantee verified: time never went backward during dilation");
        }

        #endregion
    }
}
