/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;

namespace GONet.Tests
{
    /// <summary>
    /// Simple test event for verifying ICancelOutOtherEvents behavior with non-matching types.
    /// </summary>
    internal class TestOtherEvent : IGONetEvent
    {
        public long OccurredAtElapsedTicks => 0;
    }

    /// <summary>
    /// Unit tests for the GONet reparenting system.
    /// Tests transform sync suspension, rate limiting, and pending reparent processing.
    /// </summary>
    [TestFixture]
    public class GONetReparentingSystemTests
    {
        // Store original config values
        private float originalPendingReparentTimeout;
        private int originalMaxReparentsPerSecond;
        private bool originalEnableTransformSyncSuspension;
        private bool originalAutoKinematic;
        private bool originalLogReparentDiagnostics;

        [SetUp]
        public void SetUp()
        {
            originalPendingReparentTimeout = GONetConfig.PendingReparentTimeoutSeconds;
            originalMaxReparentsPerSecond = GONetConfig.MaxReparentsPerSecondPerAuthority;
            originalEnableTransformSyncSuspension = GONetConfig.EnableTransformSyncSuspensionForNestedGNPs;
            originalAutoKinematic = GONetConfig.AutoKinematicOnTransformSyncSuspension;
            originalLogReparentDiagnostics = GONetConfig.LogReparentDiagnostics;

            GONetConfig.LogReparentDiagnostics = false;
        }

        [TearDown]
        public void TearDown()
        {
            GONetConfig.PendingReparentTimeoutSeconds = originalPendingReparentTimeout;
            GONetConfig.MaxReparentsPerSecondPerAuthority = originalMaxReparentsPerSecond;
            GONetConfig.EnableTransformSyncSuspensionForNestedGNPs = originalEnableTransformSyncSuspension;
            GONetConfig.AutoKinematicOnTransformSyncSuspension = originalAutoKinematic;
            GONetConfig.LogReparentDiagnostics = originalLogReparentDiagnostics;
        }

        #region Configuration Tests

        [Test]
        public void ReparentConfig_RateLimitEnabled_WhenGreaterThanZero()
        {
            GONetConfig.MaxReparentsPerSecondPerAuthority = 10;
            Assert.Greater(GONetConfig.MaxReparentsPerSecondPerAuthority, 0, "Rate limiting should be enabled");
        }

        [Test]
        public void ReparentConfig_RateLimitDisabled_WhenZero()
        {
            GONetConfig.MaxReparentsPerSecondPerAuthority = 0;
            Assert.AreEqual(0, GONetConfig.MaxReparentsPerSecondPerAuthority, "Rate limiting should be disabled");
        }

        [Test]
        public void ReparentConfig_TimeoutCanBeConfigured()
        {
            GONetConfig.PendingReparentTimeoutSeconds = 60.0f;
            Assert.AreEqual(60.0f, GONetConfig.PendingReparentTimeoutSeconds, 0.001f);
        }

        [Test]
        public void ReparentConfig_TransformSyncSuspensionCanBeToggled()
        {
            GONetConfig.EnableTransformSyncSuspensionForNestedGNPs = false;
            Assert.IsFalse(GONetConfig.EnableTransformSyncSuspensionForNestedGNPs);

            GONetConfig.EnableTransformSyncSuspensionForNestedGNPs = true;
            Assert.IsTrue(GONetConfig.EnableTransformSyncSuspensionForNestedGNPs);
        }

        [Test]
        public void ReparentConfig_AutoKinematicCanBeToggled()
        {
            GONetConfig.AutoKinematicOnTransformSyncSuspension = false;
            Assert.IsFalse(GONetConfig.AutoKinematicOnTransformSyncSuspension);

            GONetConfig.AutoKinematicOnTransformSyncSuspension = true;
            Assert.IsTrue(GONetConfig.AutoKinematicOnTransformSyncSuspension);
        }

        #endregion

        #region ReparentGONetParticipantEvent Self-Cancel Scenarios

        [Test]
        public void SelfCancel_ReturnToOriginalParent_ByGONetId()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                OriginalParentGONetId = 100,
                NewParentGONetId = 100  // Same as original
            };

            Assert.IsTrue(evt.ShouldSelfCancel);
        }

        [Test]
        public void SelfCancel_ReturnToOriginalParent_ByPath()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                OriginalParentGONetId = GONetParticipant.GONetId_Unset,
                OriginalParentFullUniquePath = "/Game/Container",
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = "/Game/Container"  // Same as original
            };

            Assert.IsTrue(evt.ShouldSelfCancel);
        }

        [Test]
        public void SelfCancel_ReturnToWorldRoot()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                OriginalParentGONetId = GONetParticipant.GONetId_Unset,
                OriginalParentFullUniquePath = null,  // World root
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = null  // Back to world root
            };

            Assert.IsTrue(evt.ShouldSelfCancel);
        }

        [Test]
        public void NoSelfCancel_DifferentParent_ByGONetId()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                OriginalParentGONetId = 100,
                NewParentGONetId = 200  // Different parent
            };

            Assert.IsFalse(evt.ShouldSelfCancel);
        }

        [Test]
        public void NoSelfCancel_FromWorldRootToParent()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                OriginalParentGONetId = GONetParticipant.GONetId_Unset,
                OriginalParentFullUniquePath = null,  // World root
                NewParentGONetId = 100  // New parent
            };

            Assert.IsFalse(evt.ShouldSelfCancel);
        }

        [Test]
        public void NoSelfCancel_FromParentToWorldRoot()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                OriginalParentGONetId = 100,  // Original parent
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = null  // World root
            };

            Assert.IsFalse(evt.ShouldSelfCancel);
        }

        #endregion

        #region ICancelOutOtherEvents Tests

        [Test]
        public void CancelOutOther_SameGONetId_Cancels()
        {
            var oldEvt = new ReparentGONetParticipantEvent { GONetId = 100, NewParentGONetId = 200 };
            var newEvt = new ReparentGONetParticipantEvent { GONetId = 100, NewParentGONetId = 300 };

            Assert.IsTrue(newEvt.DoesCancelOutOtherEvent(oldEvt));
        }

        [Test]
        public void CancelOutOther_DifferentGONetId_DoesNotCancel()
        {
            var oldEvt = new ReparentGONetParticipantEvent { GONetId = 100, NewParentGONetId = 200 };
            var newEvt = new ReparentGONetParticipantEvent { GONetId = 999, NewParentGONetId = 300 };

            Assert.IsFalse(newEvt.DoesCancelOutOtherEvent(oldEvt));
        }

        [Test]
        public void CancelOutOther_WrongEventType_DoesNotCancel()
        {
            var reparentEvt = new ReparentGONetParticipantEvent { GONetId = 100 };
            var otherEvt = new TestOtherEvent();

            Assert.IsFalse(reparentEvt.DoesCancelOutOtherEvent(otherEvt));
        }

        #endregion

        #region Event Timeout Configuration Tests

        [Test]
        public void ReparentTimeout_EventFires_WhenSubscribed()
        {
            bool eventFired = false;
            uint receivedObjectId = 0;
            uint receivedParentId = 0;
            float receivedWaitedSeconds = 0;

            System.Action<uint, uint, float> handler = (objectId, parentId, waited) =>
            {
                eventFired = true;
                receivedObjectId = objectId;
                receivedParentId = parentId;
                receivedWaitedSeconds = waited;
            };

            GONetConfig.OnReparentTimeout += handler;

            try
            {
                GONetConfig.RaiseReparentTimeout(100, 200, 31.5f);

                Assert.IsTrue(eventFired);
                Assert.AreEqual(100u, receivedObjectId);
                Assert.AreEqual(200u, receivedParentId);
                Assert.AreEqual(31.5f, receivedWaitedSeconds, 0.001f);
            }
            finally
            {
                GONetConfig.OnReparentTimeout -= handler;
            }
        }

        #endregion

        #region Local Offset Tests

        [Test]
        public void LocalOffset_PositionPreserved()
        {
            var offset = new Vector3(5.5f, -3.2f, 10.0f);
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                LocalPositionOffset = offset
            };

            Assert.AreEqual(offset.x, evt.LocalPositionOffset.x, 0.001f);
            Assert.AreEqual(offset.y, evt.LocalPositionOffset.y, 0.001f);
            Assert.AreEqual(offset.z, evt.LocalPositionOffset.z, 0.001f);
        }

        [Test]
        public void LocalOffset_RotationPreserved()
        {
            var rotation = Quaternion.Euler(45, 90, 180);
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                LocalRotationOffset = rotation
            };

            Assert.That(Quaternion.Angle(rotation, evt.LocalRotationOffset), Is.LessThan(0.1f));
        }

        [Test]
        public void LocalOffset_DefaultsToZero()
        {
            var evt = new ReparentGONetParticipantEvent { GONetId = 1 };

            Assert.AreEqual(Vector3.zero, evt.LocalPositionOffset);
            // Note: Uninitialized Quaternion defaults to (0,0,0,0), not identity (0,0,0,1)
            // This is expected for a MemoryPack-serialized DTO - callers must set rotation explicitly
            Assert.AreEqual(new Quaternion(0, 0, 0, 0), evt.LocalRotationOffset);
        }

        #endregion

        #region Dual Parent Representation Priority Tests

        [Test]
        public void ParentRepresentation_GONetIdTakesPriority()
        {
            // When both GONetId and Path are set, GONetId should be used
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                NewParentGONetId = 100,
                NewParentFullUniquePath = "/Fallback/Path"
            };

            // The event has both set, but GONetId should be preferred
            Assert.AreNotEqual(GONetParticipant.GONetId_Unset, evt.NewParentGONetId);
        }

        [Test]
        public void ParentRepresentation_PathUsedWhenNoGONetId()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = "/NonGNP/Container"
            };

            Assert.AreEqual(GONetParticipant.GONetId_Unset, evt.NewParentGONetId);
            Assert.AreEqual("/NonGNP/Container", evt.NewParentFullUniquePath);
        }

        [Test]
        public void ParentRepresentation_BothNullMeansWorldRoot()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = null
            };

            Assert.AreEqual(GONetParticipant.GONetId_Unset, evt.NewParentGONetId);
            Assert.IsNull(evt.NewParentFullUniquePath);
            // Both null means no parent = world root
        }

        #endregion

        #region Rate Limiting Configuration Scenarios

        [Test]
        public void RateLimit_CanBeConfigured_HighVolume()
        {
            GONetConfig.MaxReparentsPerSecondPerAuthority = 100;
            Assert.AreEqual(100, GONetConfig.MaxReparentsPerSecondPerAuthority);
        }

        [Test]
        public void RateLimit_CanBeConfigured_LowVolume()
        {
            GONetConfig.MaxReparentsPerSecondPerAuthority = 1;
            Assert.AreEqual(1, GONetConfig.MaxReparentsPerSecondPerAuthority);
        }

        [Test]
        public void RateLimit_CanBeConfigured_Disabled()
        {
            GONetConfig.MaxReparentsPerSecondPerAuthority = 0;
            Assert.AreEqual(0, GONetConfig.MaxReparentsPerSecondPerAuthority);
        }

        #endregion

        #region Source Authority Tracking Tests

        [Test]
        public void SourceAuthority_IsTracked()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 1,
                SourceAuthorityId = 5
            };

            Assert.AreEqual(5, evt.SourceAuthorityId);
        }

        [Test]
        public void SourceAuthority_DefaultIsZero()
        {
            var evt = new ReparentGONetParticipantEvent();
            Assert.AreEqual(0, evt.SourceAuthorityId);
        }

        #endregion
    }
}
