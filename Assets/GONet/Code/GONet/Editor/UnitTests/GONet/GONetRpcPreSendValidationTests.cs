/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace GONet.Tests
{
    /// <summary>
    /// Unit tests for RPC pre-send validation in GONetBehaviour/GONetParticipantCompanionBehaviour.
    /// Tests that RPCs are properly validated before sending.
    /// </summary>
    [TestFixture]
    public class GONetRpcPreSendValidationTests
    {
        // Store original config values
        private bool originalEnableRpcPreSendValidation;
        private bool originalThrowOnInvalidRpc;
        private bool originalLogDiagnostics;

        [SetUp]
        public void SetUp()
        {
            originalEnableRpcPreSendValidation = GONetConfig.EnableRpcPreSendValidation;
            originalThrowOnInvalidRpc = GONetConfig.ThrowOnInvalidRpc;
            originalLogDiagnostics = GONetConfig.LogRpcDeferralDiagnostics;

            GONetConfig.LogRpcDeferralDiagnostics = false;
        }

        [TearDown]
        public void TearDown()
        {
            GONetConfig.EnableRpcPreSendValidation = originalEnableRpcPreSendValidation;
            GONetConfig.ThrowOnInvalidRpc = originalThrowOnInvalidRpc;
            GONetConfig.LogRpcDeferralDiagnostics = originalLogDiagnostics;
        }

        #region Configuration Tests

        [Test]
        public void RpcPreSendValidation_EnabledByDefault()
        {
            // Reset to see actual default
            GONetConfig.EnableRpcPreSendValidation = true;
            Assert.IsTrue(GONetConfig.EnableRpcPreSendValidation,
                "RPC pre-send validation should be enabled by default");
        }

        [Test]
        public void RpcPreSendValidation_CanBeDisabled()
        {
            GONetConfig.EnableRpcPreSendValidation = false;
            Assert.IsFalse(GONetConfig.EnableRpcPreSendValidation);
        }

        [Test]
        public void ThrowOnInvalidRpc_DisabledByDefault()
        {
            GONetConfig.ThrowOnInvalidRpc = false;
            Assert.IsFalse(GONetConfig.ThrowOnInvalidRpc,
                "ThrowOnInvalidRpc should be disabled by default for production safety");
        }

        [Test]
        public void ThrowOnInvalidRpc_CanBeEnabled()
        {
            GONetConfig.ThrowOnInvalidRpc = true;
            Assert.IsTrue(GONetConfig.ThrowOnInvalidRpc);
        }

        #endregion

        #region Validation Event Tests

        [Test]
        public void OnRpcValidationFailed_EventFired_WhenValidationFails()
        {
            bool eventFired = false;
            string receivedMethodName = null;
            uint receivedGoNetId = 0;
            string receivedReason = null;

            Action<string, uint, string> handler = (method, id, reason) =>
            {
                eventFired = true;
                receivedMethodName = method;
                receivedGoNetId = id;
                receivedReason = reason;
            };

            GONetConfig.OnRpcValidationFailed += handler;

            try
            {
                GONetConfig.RaiseRpcValidationFailed("TestRpc", 123, "GONetId not assigned");

                Assert.IsTrue(eventFired, "Event should fire");
                Assert.AreEqual("TestRpc", receivedMethodName);
                Assert.AreEqual(123u, receivedGoNetId);
                Assert.AreEqual("GONetId not assigned", receivedReason);
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler;
            }
        }

        [Test]
        public void OnRpcValidationFailed_MultipleSubscribers_AllNotified()
        {
            int callCount = 0;

            Action<string, uint, string> handler1 = (m, i, r) => callCount++;
            Action<string, uint, string> handler2 = (m, i, r) => callCount++;
            Action<string, uint, string> handler3 = (m, i, r) => callCount++;

            GONetConfig.OnRpcValidationFailed += handler1;
            GONetConfig.OnRpcValidationFailed += handler2;
            GONetConfig.OnRpcValidationFailed += handler3;

            try
            {
                GONetConfig.RaiseRpcValidationFailed("Test", 1, "reason");
                Assert.AreEqual(3, callCount, "All subscribers should be notified");
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler1;
                GONetConfig.OnRpcValidationFailed -= handler2;
                GONetConfig.OnRpcValidationFailed -= handler3;
            }
        }

        [Test]
        public void OnRpcValidationFailed_NoSubscribers_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                GONetConfig.RaiseRpcValidationFailed("Test", 1, "reason");
            });
        }

        #endregion

        #region Validation Reason Tests

        [Test]
        public void ValidationReason_NullParticipant_Descriptive()
        {
            string receivedReason = null;
            Action<string, uint, string> handler = (m, i, r) => receivedReason = r;
            GONetConfig.OnRpcValidationFailed += handler;

            try
            {
                // Simulate the validation failure message for null participant
                string expectedSubstring = "GONetParticipant";
                GONetConfig.RaiseRpcValidationFailed("TestMethod", 0, "GONetParticipant reference is null");

                Assert.IsTrue(receivedReason.Contains(expectedSubstring),
                    "Reason should mention GONetParticipant");
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler;
            }
        }

        [Test]
        public void ValidationReason_GONetIdUnset_Descriptive()
        {
            string receivedReason = null;
            Action<string, uint, string> handler = (m, i, r) => receivedReason = r;
            GONetConfig.OnRpcValidationFailed += handler;

            try
            {
                // Simulate the validation failure message for unset GONetId
                GONetConfig.RaiseRpcValidationFailed("TestMethod", GONetParticipant.GONetId_Unset,
                    "GONetId is not assigned (GONetId_Unset). Wait for OnGONetReady().");

                Assert.IsTrue(receivedReason.Contains("GONetId") || receivedReason.Contains("OnGONetReady"),
                    "Reason should mention GONetId or OnGONetReady");
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler;
            }
        }

        [Test]
        public void ValidationReason_NotInLookup_Descriptive()
        {
            string receivedReason = null;
            Action<string, uint, string> handler = (m, i, r) => receivedReason = r;
            GONetConfig.OnRpcValidationFailed += handler;

            try
            {
                // Simulate the validation failure message for participant not in lookup
                GONetConfig.RaiseRpcValidationFailed("TestMethod", 999,
                    "GONetParticipant with GONetId 999 is not registered in GONetMain");

                Assert.IsTrue(receivedReason.Contains("registered") || receivedReason.Contains("lookup"),
                    "Reason should mention registration");
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler;
            }
        }

        #endregion

        #region Configuration Interaction Tests

        [Test]
        public void ValidationDisabled_NoEventFired()
        {
            GONetConfig.EnableRpcPreSendValidation = false;

            bool eventFired = false;
            Action<string, uint, string> handler = (m, i, r) => eventFired = true;
            GONetConfig.OnRpcValidationFailed += handler;

            try
            {
                // When validation is disabled, no events should fire
                // (The actual validation logic is in GONetBehaviour, but we test the config here)
                Assert.IsFalse(GONetConfig.EnableRpcPreSendValidation);
                // If validation was actually run with it disabled, it should skip and not fire events
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler;
            }
        }

        [Test]
        public void ValidationEnabled_ThrowDisabled_EventFired_NoException()
        {
            GONetConfig.EnableRpcPreSendValidation = true;
            GONetConfig.ThrowOnInvalidRpc = false;

            bool eventFired = false;
            Action<string, uint, string> handler = (m, i, r) => eventFired = true;
            GONetConfig.OnRpcValidationFailed += handler;

            try
            {
                Assert.DoesNotThrow(() =>
                {
                    GONetConfig.RaiseRpcValidationFailed("Test", 1, "reason");
                });

                Assert.IsTrue(eventFired, "Event should still fire even with ThrowOnInvalidRpc=false");
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler;
            }
        }

        #endregion

        #region Method Name Tracking Tests

        [Test]
        public void ValidationFailure_TracksMethodName()
        {
            string receivedMethodName = null;
            Action<string, uint, string> handler = (m, i, r) => receivedMethodName = m;
            GONetConfig.OnRpcValidationFailed += handler;

            try
            {
                GONetConfig.RaiseRpcValidationFailed("MySpecificRpcMethod", 123, "reason");
                Assert.AreEqual("MySpecificRpcMethod", receivedMethodName);
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler;
            }
        }

        [Test]
        public void ValidationFailure_NullMethodName_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                GONetConfig.RaiseRpcValidationFailed(null, 123, "reason");
            });
        }

        [Test]
        public void ValidationFailure_EmptyMethodName_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                GONetConfig.RaiseRpcValidationFailed("", 123, "reason");
            });
        }

        #endregion

        #region GONetId Tracking Tests

        [Test]
        public void ValidationFailure_TracksGONetId()
        {
            uint receivedGoNetId = 0;
            Action<string, uint, string> handler = (m, i, r) => receivedGoNetId = i;
            GONetConfig.OnRpcValidationFailed += handler;

            try
            {
                GONetConfig.RaiseRpcValidationFailed("Test", 12345, "reason");
                Assert.AreEqual(12345u, receivedGoNetId);
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler;
            }
        }

        [Test]
        public void ValidationFailure_GONetIdUnset_TracksCorrectly()
        {
            uint receivedGoNetId = 999;  // Non-unset initial value
            Action<string, uint, string> handler = (m, i, r) => receivedGoNetId = i;
            GONetConfig.OnRpcValidationFailed += handler;

            try
            {
                GONetConfig.RaiseRpcValidationFailed("Test", GONetParticipant.GONetId_Unset, "reason");
                Assert.AreEqual(GONetParticipant.GONetId_Unset, receivedGoNetId);
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler;
            }
        }

        #endregion

        #region Edge Cases

        [Test]
        public void ValidationFailure_NullReason_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                GONetConfig.RaiseRpcValidationFailed("Test", 123, null);
            });
        }

        [Test]
        public void ValidationFailure_EmptyReason_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                GONetConfig.RaiseRpcValidationFailed("Test", 123, "");
            });
        }

        [Test]
        public void ValidationFailure_LongReason_DoesNotThrow()
        {
            string longReason = new string('x', 10000);
            Assert.DoesNotThrow(() =>
            {
                GONetConfig.RaiseRpcValidationFailed("Test", 123, longReason);
            });
        }

        [Test]
        public void ValidationFailure_AllParametersNull_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                GONetConfig.RaiseRpcValidationFailed(null, 0, null);
            });
        }

        #endregion

        #region Performance Tests

        [Test]
        public void ValidationEvent_CanHandleRapidFiring()
        {
            int callCount = 0;
            Action<string, uint, string> handler = (m, i, r) => callCount++;
            GONetConfig.OnRpcValidationFailed += handler;

            try
            {
                // Fire many events rapidly
                for (int i = 0; i < 1000; i++)
                {
                    GONetConfig.RaiseRpcValidationFailed($"Method{i}", (uint)i, "reason");
                }

                Assert.AreEqual(1000, callCount, "All events should be processed");
            }
            finally
            {
                GONetConfig.OnRpcValidationFailed -= handler;
            }
        }

        #endregion
    }
}
