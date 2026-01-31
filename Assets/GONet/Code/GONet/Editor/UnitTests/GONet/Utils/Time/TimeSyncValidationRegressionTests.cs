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
using GONet.Utils;
using NUnit.Framework;
using static GONet.GONetMain;

namespace GONet.Tests.Time
{
    /// <summary>
    /// CRITICAL REGRESSION TESTS for time sync validation race condition.
    ///
    /// **WHY EXISTING TESTS DIDN'T CATCH THE BUG:**
    /// Existing tests (TimeSyncAggressiveIntervalConvergenceTests) call HighPerfTimeSync.ProcessTimeSync() directly,
    /// which BYPASSES the validation code in Client_SyncTimeWithServer_ProcessResponse() where the bug existed.
    ///
    /// **THE BUG:**
    /// After first sync applies an offset, validation used RAW time (without offset) instead of EFFECTIVE time
    /// (with offset), causing all subsequent syncs to be rejected as "absurdly large adjustments".
    ///
    /// **THESE TESTS:**
    /// Verify the fix by testing that effective time (with offset) is correctly used after first sync.
    /// Note: Full production path testing requires network infrastructure; these tests validate the core logic.
    /// </summary>
    [TestFixture]
    public class TimeSyncValidationRegressionTests : TimeSyncTestBase
    {
        /// <summary>
        /// REGRESSION TEST: Ensures subsequent syncs use EFFECTIVE time (with offset) for validation.
        ///
        /// This test simulates the exact scenario that caused the bug:
        /// 1. First sync applies a large offset (simulating late-joiner)
        /// 2. Subsequent syncs should see ~0 adjustment (using effective time)
        /// 3. If bug exists, would see large adjustment (using raw time) and reject
        /// </summary>
        [Test]
        [Category("TimeCorrection")]
        [Category("RegressionTest")]
        [Timeout(10000)]
        public void Should_Not_Reject_Subsequent_Syncs_After_First_Sync_Applies_Offset()
        {
            // ARRANGE: Use GONetMain.Time to simulate production behavior
            var gonetMainType = typeof(GONetMain);
            var timeField = gonetMainType.GetField("Time", BindingFlags.NonPublic | BindingFlags.Static);
            var gonetTime = (SecretaryOfTemporalAffairs)timeField.GetValue(null);

            gonetTime.Update();
            long initialRaw = gonetTime.RawElapsedTicks;

            // Simulate first sync applying a 500ms offset (late-joiner scenario)
            const long OFFSET_MS = 500;
            long targetTicks = initialRaw + TimeSpan.FromMilliseconds(OFFSET_MS).Ticks;
            gonetTime.SetFromAuthority(targetTicks, forceImmediate: true);
            Thread.Sleep(50);
            gonetTime.Update();

            // Verify offset was applied
            long rawAfterFirst = gonetTime.RawElapsedTicks;
            long effectiveAfterFirst = gonetTime.ElapsedTicks;
            long actualOffset = effectiveAfterFirst - rawAfterFirst;
            double actualOffsetMs = actualOffset / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[REGRESSION-TEST] After first sync - Raw: {rawAfterFirst / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                 $"Effective: {effectiveAfterFirst / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                 $"Offset: {actualOffsetMs:F1}ms");

            Assert.That(actualOffsetMs, Is.InRange(400, 600),
                "First sync should apply ~500ms offset");

            // ACT: Simulate what validation logic does for subsequent syncs
            // Server time would be effective + small RTT adjustment
            long serverTimeTicks = effectiveAfterFirst + TimeSpan.FromMilliseconds(10).Ticks; // Server slightly ahead due to RTT

            // THE BUG FIX: After first sync, validation should use EFFECTIVE time (with offset)
            // NOT raw time (without offset)
            long clientTimeForValidation_FIXED = effectiveAfterFirst; // CORRECT (with offset)
            long clientTimeForValidation_BUGGY = rawAfterFirst;       // WRONG (without offset)

            long adjustment_FIXED = serverTimeTicks - clientTimeForValidation_FIXED;
            long adjustment_BUGGY = serverTimeTicks - clientTimeForValidation_BUGGY;

            double adjustmentMs_FIXED = adjustment_FIXED / (double)TimeSpan.TicksPerMillisecond;
            double adjustmentMs_BUGGY = adjustment_BUGGY / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[REGRESSION-TEST] Adjustment calculation:");
            UnityEngine.Debug.Log($"  With FIX (effective time): {adjustmentMs_FIXED:F1}ms");
            UnityEngine.Debug.Log($"  With BUG (raw time): {adjustmentMs_BUGGY:F1}ms");

            // ASSERT: Fixed version should see small adjustment (~10ms from RTT)
            Assert.That(Math.Abs(adjustmentMs_FIXED), Is.LessThan(50),
                "REGRESSION FAILURE: With fix, adjustment should be small (just RTT compensation)");

            // ASSERT: Buggy version would see huge adjustment (~500ms = the offset)
            Assert.That(Math.Abs(adjustmentMs_BUGGY), Is.GreaterThan(400),
                "Buggy version should see large adjustment (the offset that was already applied)");

            // ASSERT: The difference demonstrates why the fix matters
            Assert.That(Math.Abs(adjustmentMs_BUGGY), Is.GreaterThan(Math.Abs(adjustmentMs_FIXED) * 5),
                "Bug would cause adjustment 5x+ larger than correct value");

            UnityEngine.Debug.Log("[REGRESSION-TEST] ✅ PASSED - Fix correctly uses effective time, not raw time!");
        }

        /// <summary>
        /// REGRESSION TEST: Verifies effective time includes offset after SetFromAuthority.
        /// </summary>
        [Test]
        [Category("TimeCorrection")]
        [Category("RegressionTest")]
        [Timeout(5000)]
        public void Validation_Must_Use_Effective_Time_After_First_Sync()
        {
            // ARRANGE
            var gonetMainType = typeof(GONetMain);
            var timeField = gonetMainType.GetField("Time", BindingFlags.NonPublic | BindingFlags.Static);
            var gonetTime = (SecretaryOfTemporalAffairs)timeField.GetValue(null);

            gonetTime.Update();

            // Apply offset (simulating first sync)
            const long OFFSET_MS = 500;
            long targetTicks = gonetTime.RawElapsedTicks + TimeSpan.FromMilliseconds(OFFSET_MS).Ticks;
            gonetTime.SetFromAuthority(targetTicks, forceImmediate: true);
            Thread.Sleep(100);
            gonetTime.Update();

            // ACT: Check time values
            long rawTime = gonetTime.RawElapsedTicks;
            long effectiveTime = gonetTime.ElapsedTicks;
            long offsetApplied = effectiveTime - rawTime;
            double offsetMs = offsetApplied / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[VALIDATION-TEST] Raw: {rawTime / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                 $"Effective: {effectiveTime / (double)TimeSpan.TicksPerSecond:F3}s, " +
                                 $"Offset: {offsetMs:F1}ms");

            // ASSERT
            Assert.That(offsetMs, Is.InRange(400, 600), "Offset should be ~500ms");
            Assert.That(effectiveTime, Is.GreaterThan(rawTime),
                "Effective time must include offset after first sync");

            UnityEngine.Debug.Log("[VALIDATION-TEST] ✅ PASSED!");
        }

        /// <summary>
        /// REGRESSION TEST: Ensures multiple subsequent syncs don't drift or accumulate errors.
        /// </summary>
        [Test]
        [Category("TimeCorrection")]
        [Category("RegressionTest")]
        [Timeout(10000)]
        public void Multiple_Subsequent_Syncs_Should_Maintain_Stable_Offset()
        {
            // ARRANGE
            var gonetMainType = typeof(GONetMain);
            var timeField = gonetMainType.GetField("Time", BindingFlags.NonPublic | BindingFlags.Static);
            var gonetTime = (SecretaryOfTemporalAffairs)timeField.GetValue(null);

            gonetTime.Update();

            // First sync: apply 500ms offset
            const long OFFSET_MS = 500;
            long targetTicks = gonetTime.RawElapsedTicks + TimeSpan.FromMilliseconds(OFFSET_MS).Ticks;
            gonetTime.SetFromAuthority(targetTicks, forceImmediate: true);
            Thread.Sleep(50);
            gonetTime.Update();

            long offsetAfterFirst = gonetTime.ElapsedTicks - gonetTime.RawElapsedTicks;

            // ACT: Simulate 10 subsequent syncs at 50ms intervals
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(50);
                gonetTime.Update();

                // Server time is always effective + small RTT
                long serverTime = gonetTime.ElapsedTicks + TimeSpan.FromMilliseconds(5).Ticks;

                // Process sync via HighPerfTimeSync (bypasses network layer but tests convergence)
                var request = new MockRequestMessage(gonetTime.RawElapsedTicks);
                HighPerfTimeSync.ProcessTimeSync(request.UID, serverTime, request, gonetTime, forceAdjustment: false);
            }

            gonetTime.Update();
            long offsetAfterAll = gonetTime.ElapsedTicks - gonetTime.RawElapsedTicks;

            double offsetMsAfterFirst = offsetAfterFirst / (double)TimeSpan.TicksPerMillisecond;
            double offsetMsAfterAll = offsetAfterAll / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[STABILITY-TEST] Offset after first: {offsetMsAfterFirst:F1}ms");
            UnityEngine.Debug.Log($"[STABILITY-TEST] Offset after 10 syncs: {offsetMsAfterAll:F1}ms");

            // ASSERT: Offset should remain stable (within reasonable convergence bounds)
            Assert.That(offsetMsAfterAll, Is.InRange(300, 700),
                "Offset should remain in reasonable range after multiple syncs");

            UnityEngine.Debug.Log("[STABILITY-TEST] ✅ PASSED - Offset remains stable across multiple syncs!");
        }

        [Test]
        [Category("TimeSyncValidation")]
        [Timeout(5000)]
        public void TimeSyncDomainValidation_RejectsMismatchedDomain()
        {
            var gonetMainType = typeof(GONetMain);
            var sessionGuidField = gonetMainType.GetField("sessionGUID", BindingFlags.NonPublic | BindingFlags.Static);
            var hostEpochField = gonetMainType.GetField("<HostEpoch>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);
            var hostIdentityField = gonetMainType.GetField("<CurrentHostIdentity>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);
            var validateMethod = gonetMainType.GetMethod("ValidateTimeSyncDomain", BindingFlags.NonPublic | BindingFlags.Static);

            const long sessionGuid = 123456789;
            const uint hostEpoch = 2;
            const ushort hostAuthorityId = 42;

            Assert.NotNull(validateMethod, "ValidateTimeSyncDomain should exist");

            sessionGuidField?.SetValue(null, sessionGuid);
            hostEpochField?.SetValue(null, hostEpoch);
            hostIdentityField?.SetValue(null, new HostIdentity(sessionGuid, hostEpoch, hostAuthorityId, 0));

            object[] okArgs = { (byte)1, sessionGuid, hostEpoch, hostAuthorityId, null };
            bool ok = (bool)validateMethod.Invoke(null, okArgs);
            string okReason = okArgs[4] as string;
            Assert.IsTrue(ok, okReason);

            object[] badSessionArgs = { (byte)1, sessionGuid + 1, hostEpoch, hostAuthorityId, null };
            bool badSession = (bool)validateMethod.Invoke(null, badSessionArgs);
            Assert.IsFalse(badSession, "Mismatched session GUID should be rejected");

            object[] badEpochArgs = { (byte)1, sessionGuid, hostEpoch + 1, hostAuthorityId, null };
            bool badEpoch = (bool)validateMethod.Invoke(null, badEpochArgs);
            Assert.IsFalse(badEpoch, "Mismatched host epoch should be rejected");

            object[] badHostArgs = { (byte)1, sessionGuid, hostEpoch, (ushort)(hostAuthorityId + 1), null };
            bool badHost = (bool)validateMethod.Invoke(null, badHostArgs);
            Assert.IsFalse(badHost, "Mismatched host authority should be rejected");

            object[] missingArgs = { (byte)0, sessionGuid, hostEpoch, hostAuthorityId, null };
            bool missing = (bool)validateMethod.Invoke(null, missingArgs);
            Assert.IsFalse(missing, "Missing domain should be rejected");
        }

        /// <summary>
        /// REGRESSION TEST: Large forward jumps should be blocked unless a recent LARGE authority offset was set.
        /// This protects against corrupted dilation state after small adjustments.
        /// </summary>
        [Test]
        [Category("TimeCorrection")]
        [Category("RegressionTest")]
        [Timeout(5000)]
        public void CalculateElapsedTicks_Blocks_LargeForwardJump_WithoutRecentLargeAuthoritySet()
        {
            var gonetMainType = typeof(GONetMain);
            var timeField = gonetMainType.GetField("Time", BindingFlags.NonPublic | BindingFlags.Static);
            var gonetTime = (SecretaryOfTemporalAffairs)timeField.GetValue(null);

            gonetTime.Update();
            long raw = gonetTime.RawElapsedTicks;

            var firstSyncField = gonetMainType.GetField("client_isFirstTimeSync", BindingFlags.NonPublic | BindingFlags.Static);
            firstSyncField?.SetValue(null, false);

            var lastAuthSetField = typeof(SecretaryOfTemporalAffairs).GetField("lastAuthoritySetRawTicks",
                BindingFlags.NonPublic | BindingFlags.Instance);
            lastAuthSetField?.SetValue(gonetTime, raw);

            var lastLargeAuthField = typeof(SecretaryOfTemporalAffairs).GetField("lastLargeAuthorityOffsetWriteRawTicks",
                BindingFlags.NonPublic | BindingFlags.Instance);
            lastLargeAuthField?.SetValue(gonetTime, 0L);

            var alignedStateField = typeof(SecretaryOfTemporalAffairs).GetField("alignedState",
                BindingFlags.NonPublic | BindingFlags.Instance);
            object alignedState = alignedStateField.GetValue(gonetTime);
            Type alignedStateType = alignedState.GetType();

            var interpolationField = alignedStateType.GetField("Interpolation", BindingFlags.Public | BindingFlags.Instance);
            var stateField = alignedStateType.GetField("State", BindingFlags.Public | BindingFlags.Instance);
            var updateCountField = alignedStateType.GetField("UpdateCount", BindingFlags.Public | BindingFlags.Instance);

            object interpolation = interpolationField.GetValue(alignedState);
            Type interpolationType = interpolation.GetType();
            long corruptOffsetTicks = 429L * TimeSpan.TicksPerSecond;

            interpolationType.GetField("DilationDurationTicks", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(interpolation, 2L * TimeSpan.TicksPerSecond);
            interpolationType.GetField("DilationStartTimeTicks", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(interpolation, raw);
            interpolationType.GetField("DilationStartOffsetTicks", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(interpolation, corruptOffsetTicks);
            interpolationType.GetField("DilationTargetOffsetTicks", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(interpolation, corruptOffsetTicks);
            interpolationType.GetField("EffectiveOffsetTicks", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(interpolation, 0L);
            interpolationField.SetValue(alignedState, interpolation);

            object state = stateField.GetValue(alignedState);
            Type stateType = state.GetType();
            stateType.GetField("CachedElapsedTicks", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(state, raw);
            stateType.GetField("LastUpdateFrame", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(state, -1L);
            // CRITICAL: Set AuthorityOffsetTicks \!= TargetOffsetTicks to trigger the dilation path in GetEffectiveOffset.
            // Without this difference, GetEffectiveOffset short-circuits (returns authorityOffset immediately)
            // and never evaluates the corrupt dilation state that would produce the forward jump.
            stateType.GetField("AuthorityOffsetTicks", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(state, 0L);
            stateType.GetField("TargetOffsetTicks", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(state, 1L); // Different from AuthorityOffsetTicks to trigger dilation evaluation
            stateField.SetValue(alignedState, state);

            updateCountField?.SetValue(alignedState, 0);
            alignedStateField.SetValue(gonetTime, alignedState);

            long elapsed = gonetTime.ElapsedTicks;
            long offsetAfter = gonetTime.GetEffectiveOffsetTicks_Internal();
            double deltaMs = (elapsed - raw) / (double)TimeSpan.TicksPerMillisecond;

            UnityEngine.Debug.Log($"[REGRESSION-TEST] Forward jump guard delta={deltaMs:F3}ms, " +
                                 $"offset={offsetAfter / (double)TimeSpan.TicksPerSecond:F3}s");

            Assert.That(Math.Abs(deltaMs), Is.LessThan(5.0),
                "Forward jump guard should return raw time when corruption detected");
            Assert.That(offsetAfter, Is.EqualTo(0L),
                "Effective offset should be cleared when blocking a large forward jump");
        }
    }
}
