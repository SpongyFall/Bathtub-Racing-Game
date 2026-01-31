/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using NUnit.Framework;
using System;

namespace GONet.Tests
{
    /// <summary>
    /// Unit tests for GONetConfig configuration settings and events.
    /// Tests the RPC lifecycle, OnGONetReady lifecycle, and reparenting configuration options.
    /// </summary>
    [TestFixture]
    public class GONetConfigTests
    {
        // Store original values to restore after tests
        private float originalRpcDeferralTimeout;
        private int originalMaxDeferredRpcs;
        private bool originalThrowOnInvalidRpc;
        private bool originalEnableRpcPreSendValidation;
        private bool originalEnableRpcDeferral;
        private bool originalThrowOnGONetReadyViolations;
        private bool originalEnableOnGONetReadyValidation;
        private float originalPendingReparentTimeout;
        private int originalMaxReparentsPerSecond;
        private bool originalEnableTransformSyncSuspension;
        private bool originalAutoKinematic;
        private int originalReparentAutoPublishDelay;
        private bool originalLogRpcDeferralDiagnostics;
        private bool originalLogReparentDiagnostics;

        [SetUp]
        public void SetUp()
        {
            // Store original configuration values
            originalRpcDeferralTimeout = GONetConfig.RpcDeferralTimeoutSeconds;
            originalMaxDeferredRpcs = GONetConfig.MaxDeferredRpcsPerParticipant;
            originalThrowOnInvalidRpc = GONetConfig.ThrowOnInvalidRpc;
            originalEnableRpcPreSendValidation = GONetConfig.EnableRpcPreSendValidation;
            originalEnableRpcDeferral = GONetConfig.EnableRpcDeferralForUnknownParticipants;
            originalThrowOnGONetReadyViolations = GONetConfig.ThrowOnGONetReadyViolations;
            originalEnableOnGONetReadyValidation = GONetConfig.EnableOnGONetReadyValidation;
            originalPendingReparentTimeout = GONetConfig.PendingReparentTimeoutSeconds;
            originalMaxReparentsPerSecond = GONetConfig.MaxReparentsPerSecondPerAuthority;
            originalEnableTransformSyncSuspension = GONetConfig.EnableTransformSyncSuspensionForNestedGNPs;
            originalAutoKinematic = GONetConfig.AutoKinematicOnTransformSyncSuspension;
            originalReparentAutoPublishDelay = GONetConfig.ReparentAutoPublishDelayFrames;
            originalLogRpcDeferralDiagnostics = GONetConfig.LogRpcDeferralDiagnostics;
            originalLogReparentDiagnostics = GONetConfig.LogReparentDiagnostics;
        }

        [TearDown]
        public void TearDown()
        {
            // Restore original configuration values
            GONetConfig.RpcDeferralTimeoutSeconds = originalRpcDeferralTimeout;
            GONetConfig.MaxDeferredRpcsPerParticipant = originalMaxDeferredRpcs;
            GONetConfig.ThrowOnInvalidRpc = originalThrowOnInvalidRpc;
            GONetConfig.EnableRpcPreSendValidation = originalEnableRpcPreSendValidation;
            GONetConfig.EnableRpcDeferralForUnknownParticipants = originalEnableRpcDeferral;
            GONetConfig.ThrowOnGONetReadyViolations = originalThrowOnGONetReadyViolations;
            GONetConfig.EnableOnGONetReadyValidation = originalEnableOnGONetReadyValidation;
            GONetConfig.PendingReparentTimeoutSeconds = originalPendingReparentTimeout;
            GONetConfig.MaxReparentsPerSecondPerAuthority = originalMaxReparentsPerSecond;
            GONetConfig.EnableTransformSyncSuspensionForNestedGNPs = originalEnableTransformSyncSuspension;
            GONetConfig.AutoKinematicOnTransformSyncSuspension = originalAutoKinematic;
            GONetConfig.ReparentAutoPublishDelayFrames = originalReparentAutoPublishDelay;
            GONetConfig.LogRpcDeferralDiagnostics = originalLogRpcDeferralDiagnostics;
            GONetConfig.LogReparentDiagnostics = originalLogReparentDiagnostics;
        }

        #region RPC Lifecycle Configuration Tests

        [Test]
        public void RpcDeferralTimeoutSeconds_DefaultValue_IsPositive()
        {
            Assert.Greater(GONetConfig.RpcDeferralTimeoutSeconds, 0f,
                "RpcDeferralTimeoutSeconds should have a positive default value");
        }

        [Test]
        public void RpcDeferralTimeoutSeconds_CanBeModified()
        {
            float newValue = 10.0f;
            GONetConfig.RpcDeferralTimeoutSeconds = newValue;
            Assert.AreEqual(newValue, GONetConfig.RpcDeferralTimeoutSeconds);
        }

        [Test]
        public void MaxDeferredRpcsPerParticipant_DefaultValue_IsPositive()
        {
            Assert.Greater(GONetConfig.MaxDeferredRpcsPerParticipant, 0,
                "MaxDeferredRpcsPerParticipant should have a positive default value");
        }

        [Test]
        public void MaxDeferredRpcsPerParticipant_CanBeModified()
        {
            int newValue = 50;
            GONetConfig.MaxDeferredRpcsPerParticipant = newValue;
            Assert.AreEqual(newValue, GONetConfig.MaxDeferredRpcsPerParticipant);
        }

        [Test]
        public void ThrowOnInvalidRpc_DefaultValue_IsFalse()
        {
            // Should default to false for production safety
            Assert.IsFalse(GONetConfig.ThrowOnInvalidRpc,
                "ThrowOnInvalidRpc should default to false for production safety");
        }

        [Test]
        public void EnableRpcPreSendValidation_DefaultValue_IsTrue()
        {
            Assert.IsTrue(GONetConfig.EnableRpcPreSendValidation,
                "EnableRpcPreSendValidation should default to true");
        }

        [Test]
        public void EnableRpcDeferralForUnknownParticipants_DefaultValue_IsTrue()
        {
            Assert.IsTrue(GONetConfig.EnableRpcDeferralForUnknownParticipants,
                "EnableRpcDeferralForUnknownParticipants should default to true");
        }

        #endregion

        #region OnGONetReady Lifecycle Configuration Tests

        [Test]
        public void ThrowOnGONetReadyViolations_DefaultValue_IsFalse()
        {
            Assert.IsFalse(GONetConfig.ThrowOnGONetReadyViolations,
                "ThrowOnGONetReadyViolations should default to false for production safety");
        }

        [Test]
        public void ThrowOnGONetReadyViolations_CanBeModified()
        {
            GONetConfig.ThrowOnGONetReadyViolations = true;
            Assert.IsTrue(GONetConfig.ThrowOnGONetReadyViolations);
            GONetConfig.ThrowOnGONetReadyViolations = false;
            Assert.IsFalse(GONetConfig.ThrowOnGONetReadyViolations);
        }

        #endregion

        #region Reparenting Configuration Tests

        [Test]
        public void PendingReparentTimeoutSeconds_DefaultValue_IsPositive()
        {
            Assert.Greater(GONetConfig.PendingReparentTimeoutSeconds, 0f,
                "PendingReparentTimeoutSeconds should have a positive default value");
        }

        [Test]
        public void PendingReparentTimeoutSeconds_CanBeModified()
        {
            float newValue = 60.0f;
            GONetConfig.PendingReparentTimeoutSeconds = newValue;
            Assert.AreEqual(newValue, GONetConfig.PendingReparentTimeoutSeconds);
        }

        [Test]
        public void MaxReparentsPerSecondPerAuthority_DefaultValue_IsPositive()
        {
            Assert.Greater(GONetConfig.MaxReparentsPerSecondPerAuthority, 0,
                "MaxReparentsPerSecondPerAuthority should have a positive default value for rate limiting");
        }

        [Test]
        public void MaxReparentsPerSecondPerAuthority_CanBeSetToZero_DisablesRateLimiting()
        {
            GONetConfig.MaxReparentsPerSecondPerAuthority = 0;
            Assert.AreEqual(0, GONetConfig.MaxReparentsPerSecondPerAuthority,
                "Setting to 0 should disable rate limiting");
        }

        [Test]
        public void EnableTransformSyncSuspensionForNestedGNPs_DefaultValue_IsTrue()
        {
            Assert.IsTrue(GONetConfig.EnableTransformSyncSuspensionForNestedGNPs,
                "EnableTransformSyncSuspensionForNestedGNPs should default to true");
        }

        [Test]
        public void AutoKinematicOnTransformSyncSuspension_DefaultValue_IsTrue()
        {
            Assert.IsTrue(GONetConfig.AutoKinematicOnTransformSyncSuspension,
                "AutoKinematicOnTransformSyncSuspension should default to true");
        }

        [Test]
        public void ReparentAutoPublishDelayFrames_DefaultValue_IsPositive()
        {
            Assert.GreaterOrEqual(GONetConfig.ReparentAutoPublishDelayFrames, 0,
                "ReparentAutoPublishDelayFrames should be non-negative");
        }

        #endregion

        #region Logging Configuration Tests

        [Test]
        public void LogRpcDeferralDiagnostics_DefaultValue_IsFalse()
        {
            Assert.IsFalse(GONetConfig.LogRpcDeferralDiagnostics,
                "LogRpcDeferralDiagnostics should default to false for performance");
        }

        [Test]
        public void LogReparentDiagnostics_DefaultValue_IsFalse()
        {
            Assert.IsFalse(GONetConfig.LogReparentDiagnostics,
                "LogReparentDiagnostics should default to false for performance");
        }

        #endregion

        #region Event Tests

        [Test]
        public void OnRpcDeferralTimeout_EventCanBeSubscribed()
        {
            bool eventFired = false;
            uint receivedGoNetId = 0;
            uint receivedRpcId = 0;
            float receivedWaitedSeconds = 0;

            Action<uint, uint, float> handler = (gonetId, rpcId, waited) =>
            {
                eventFired = true;
                receivedGoNetId = gonetId;
                receivedRpcId = rpcId;
                receivedWaitedSeconds = waited;
            };

            GONetConfig.OnRpcDeferralTimeout += handler;

            try
            {
                GONetConfig.RaiseRpcDeferralTimeout(123, 456, 5.5f);

                Assert.IsTrue(eventFired, "Event should have fired");
                Assert.AreEqual(123u, receivedGoNetId);
                Assert.AreEqual(456u, receivedRpcId);
                Assert.AreEqual(5.5f, receivedWaitedSeconds, 0.001f);
            }
            finally
            {
                GONetConfig.OnRpcDeferralTimeout -= handler;
            }
        }

        [Test]
        public void OnReparentTimeout_EventCanBeSubscribed()
        {
            bool eventFired = false;
            uint receivedObjectId = 0;
            uint receivedParentId = 0;
            float receivedWaitedSeconds = 0;

            Action<uint, uint, float> handler = (objectId, parentId, waited) =>
            {
                eventFired = true;
                receivedObjectId = objectId;
                receivedParentId = parentId;
                receivedWaitedSeconds = waited;
            };

            GONetConfig.OnReparentTimeout += handler;

            try
            {
                GONetConfig.RaiseReparentTimeout(100, 200, 30.0f);

                Assert.IsTrue(eventFired, "Event should have fired");
                Assert.AreEqual(100u, receivedObjectId);
                Assert.AreEqual(200u, receivedParentId);
                Assert.AreEqual(30.0f, receivedWaitedSeconds, 0.001f);
            }
            finally
            {
                GONetConfig.OnReparentTimeout -= handler;
            }
        }

        [Test]
        public void OnRpcValidationFailed_EventCanBeSubscribed()
        {
            bool eventFired = false;
            string receivedMethodName = null;
            uint receivedGoNetId = 0;
            string receivedReason = null;

            Action<string, uint, string> handler = (methodName, gonetId, reason) =>
            {
                eventFired = true;
                receivedMethodName = methodName;
                receivedGoNetId = gonetId;
                receivedReason = reason;
            };

            GONetConfig.OnRpcValidationFailed += handler;

            try
            {
                GONetConfig.RaiseRpcValidationFailed("TestMethod", 789, "Test reason");

                Assert.IsTrue(eventFired, "Event should have fired");
                Assert.AreEqual("TestMethod", receivedMethodName);
                Assert.AreEqual(789u, receivedGoNetId);
                Assert.AreEqual("Test reason", receivedReason);
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler;
            }
        }

        [Test]
        public void Events_WithNoSubscribers_DoNotThrow()
        {
            // These should not throw even with no subscribers
            Assert.DoesNotThrow(() => GONetConfig.RaiseRpcDeferralTimeout(1, 2, 3.0f));
            Assert.DoesNotThrow(() => GONetConfig.RaiseReparentTimeout(1, 2, 3.0f));
            Assert.DoesNotThrow(() => GONetConfig.RaiseRpcValidationFailed("Test", 1, "reason"));
        }

        #endregion

        #region Configuration Consistency Tests

        [Test]
        public void AllConfigValues_CanBeModifiedAndRead()
        {
            // Test that all config values can be modified and read back correctly
            GONetConfig.RpcDeferralTimeoutSeconds = 15.0f;
            GONetConfig.MaxDeferredRpcsPerParticipant = 200;
            GONetConfig.ThrowOnInvalidRpc = true;
            GONetConfig.EnableRpcPreSendValidation = false;
            GONetConfig.EnableRpcDeferralForUnknownParticipants = false;
            GONetConfig.ThrowOnGONetReadyViolations = true;
            GONetConfig.EnableOnGONetReadyValidation = false;
            GONetConfig.PendingReparentTimeoutSeconds = 45.0f;
            GONetConfig.MaxReparentsPerSecondPerAuthority = 20;
            GONetConfig.EnableTransformSyncSuspensionForNestedGNPs = false;
            GONetConfig.AutoKinematicOnTransformSyncSuspension = false;
            GONetConfig.ReparentAutoPublishDelayFrames = 3;
            GONetConfig.LogRpcDeferralDiagnostics = true;
            GONetConfig.LogReparentDiagnostics = true;

            Assert.AreEqual(15.0f, GONetConfig.RpcDeferralTimeoutSeconds, 0.001f);
            Assert.AreEqual(200, GONetConfig.MaxDeferredRpcsPerParticipant);
            Assert.IsTrue(GONetConfig.ThrowOnInvalidRpc);
            Assert.IsFalse(GONetConfig.EnableRpcPreSendValidation);
            Assert.IsFalse(GONetConfig.EnableRpcDeferralForUnknownParticipants);
            Assert.IsTrue(GONetConfig.ThrowOnGONetReadyViolations);
            Assert.IsFalse(GONetConfig.EnableOnGONetReadyValidation);
            Assert.AreEqual(45.0f, GONetConfig.PendingReparentTimeoutSeconds, 0.001f);
            Assert.AreEqual(20, GONetConfig.MaxReparentsPerSecondPerAuthority);
            Assert.IsFalse(GONetConfig.EnableTransformSyncSuspensionForNestedGNPs);
            Assert.IsFalse(GONetConfig.AutoKinematicOnTransformSyncSuspension);
            Assert.AreEqual(3, GONetConfig.ReparentAutoPublishDelayFrames);
            Assert.IsTrue(GONetConfig.LogRpcDeferralDiagnostics);
            Assert.IsTrue(GONetConfig.LogReparentDiagnostics);
        }

        #endregion
    }
}
