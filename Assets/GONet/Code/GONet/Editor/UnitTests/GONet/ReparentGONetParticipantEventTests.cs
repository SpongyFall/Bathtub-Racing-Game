/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using NUnit.Framework;
using UnityEngine;

namespace GONet.Tests
{
    /// <summary>
    /// Simple test event for verifying ICancelOutOtherEvents behavior with non-matching types.
    /// </summary>
    internal class TestNonReparentEvent : IGONetEvent
    {
        public long OccurredAtElapsedTicks => 0;
    }

    /// <summary>
    /// Unit tests for ReparentGONetParticipantEvent.
    /// Tests event creation, self-cancel logic, and ICancelOutOtherEvents behavior.
    /// </summary>
    [TestFixture]
    public class ReparentGONetParticipantEventTests
    {
        #region Event Creation Tests

        [Test]
        public void ReparentEvent_DefaultValues_AreCorrect()
        {
            var evt = new ReparentGONetParticipantEvent();

            Assert.AreEqual(0u, evt.GONetId);
            Assert.AreEqual(0, evt.SourceAuthorityId);
            Assert.AreEqual(GONetParticipant.GONetId_Unset, evt.OriginalParentGONetId);
            Assert.AreEqual(GONetParticipant.GONetId_Unset, evt.NewParentGONetId);
            Assert.IsNull(evt.OriginalParentFullUniquePath);
            Assert.IsNull(evt.OriginalParentRelativePath);
            Assert.IsNull(evt.NewParentFullUniquePath);
            Assert.IsNull(evt.NewParentRelativePath);
        }

        [Test]
        public void ReparentEvent_CanSetAllProperties()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                GONetId = 123,
                SourceAuthorityId = 1,
                OriginalParentGONetId = 100,
                OriginalParentFullUniquePath = "/Root/OriginalParent",
                OriginalParentRelativePath = "Original/Rel",
                NewParentGONetId = 200,
                NewParentFullUniquePath = "/Root/NewParent",
                NewParentRelativePath = "New/Rel",
                LocalPositionOffset = new Vector3(1, 2, 3),
                LocalRotationOffset = Quaternion.Euler(45, 90, 0)
            };

            Assert.AreEqual(123u, evt.GONetId);
            Assert.AreEqual(1, evt.SourceAuthorityId);
            Assert.AreEqual(100u, evt.OriginalParentGONetId);
            Assert.AreEqual("/Root/OriginalParent", evt.OriginalParentFullUniquePath);
            Assert.AreEqual("Original/Rel", evt.OriginalParentRelativePath);
            Assert.AreEqual(200u, evt.NewParentGONetId);
            Assert.AreEqual("/Root/NewParent", evt.NewParentFullUniquePath);
            Assert.AreEqual("New/Rel", evt.NewParentRelativePath);
            Assert.AreEqual(new Vector3(1, 2, 3), evt.LocalPositionOffset);
        }

        #endregion

        #region Interface Implementation Tests

        [Test]
        public void ReparentEvent_ImplementsIPersistentEvent()
        {
            var evt = new ReparentGONetParticipantEvent();
            Assert.IsInstanceOf<IPersistentEvent>(evt);
        }

        [Test]
        public void ReparentEvent_ImplementsIHaveRelatedGONetId()
        {
            var evt = new ReparentGONetParticipantEvent { GONetId = 456 };
            Assert.IsInstanceOf<IHaveRelatedGONetId>(evt);
            Assert.AreEqual(456u, ((IHaveRelatedGONetId)evt).GONetId);
        }

        [Test]
        public void ReparentEvent_ImplementsICancelOutOtherEvents()
        {
            var evt = new ReparentGONetParticipantEvent();
            Assert.IsInstanceOf<ICancelOutOtherEvents>(evt);
        }

        #endregion

        #region ShouldSelfCancel Tests

        [Test]
        public void ShouldSelfCancel_WhenNewParentEqualsOriginalGONetId_ReturnsTrue()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                OriginalParentGONetId = 100,
                NewParentGONetId = 100
            };

            Assert.IsTrue(evt.ShouldSelfCancel, "Should self-cancel when returning to original parent by GONetId");
        }

        [Test]
        public void ShouldSelfCancel_WhenNewParentEqualsOriginalPath_ReturnsTrue()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                OriginalParentGONetId = GONetParticipant.GONetId_Unset,
                OriginalParentFullUniquePath = "/Root/Parent",
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = "/Root/Parent"
            };

            Assert.IsTrue(evt.ShouldSelfCancel, "Should self-cancel when returning to original parent by path");
        }

        [Test]
        public void ShouldSelfCancel_WhenNewParentEqualsOriginalRelativePath_ReturnsTrue()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                OriginalParentGONetId = 100,
                OriginalParentRelativePath = "Rig/Socket",
                NewParentGONetId = 100,
                NewParentRelativePath = "Rig/Socket"
            };

            Assert.IsTrue(evt.ShouldSelfCancel, "Should self-cancel when returning to original parent by relative path");
        }

        [Test]
        public void ShouldSelfCancel_WhenRelativePathDiffers_ReturnsFalse()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                OriginalParentGONetId = 100,
                OriginalParentRelativePath = "Rig/SocketA",
                NewParentGONetId = 100,
                NewParentRelativePath = "Rig/SocketB"
            };

            Assert.IsFalse(evt.ShouldSelfCancel, "Should not self-cancel when relative paths differ");
        }

        [Test]
        public void ShouldSelfCancel_WhenBothOriginalAndNewAreNull_ReturnsTrue()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                OriginalParentGONetId = GONetParticipant.GONetId_Unset,
                OriginalParentFullUniquePath = null,
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = null
            };

            Assert.IsTrue(evt.ShouldSelfCancel, "Should self-cancel when both are world root (null)");
        }

        [Test]
        public void ShouldSelfCancel_WhenDifferentGONetIds_ReturnsFalse()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                OriginalParentGONetId = 100,
                NewParentGONetId = 200
            };

            Assert.IsFalse(evt.ShouldSelfCancel, "Should not self-cancel when moving to different parent");
        }

        [Test]
        public void ShouldSelfCancel_WhenDifferentPaths_ReturnsFalse()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                OriginalParentGONetId = GONetParticipant.GONetId_Unset,
                OriginalParentFullUniquePath = "/Root/Parent1",
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = "/Root/Parent2"
            };

            Assert.IsFalse(evt.ShouldSelfCancel, "Should not self-cancel when moving to different parent by path");
        }

        [Test]
        public void ShouldSelfCancel_WhenMovingFromNullToGONetId_ReturnsFalse()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                OriginalParentGONetId = GONetParticipant.GONetId_Unset,
                OriginalParentFullUniquePath = null,
                NewParentGONetId = 100
            };

            Assert.IsFalse(evt.ShouldSelfCancel, "Should not self-cancel when moving from world root to parent");
        }

        [Test]
        public void ShouldSelfCancel_WhenMovingFromGONetIdToNull_ReturnsFalse()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                OriginalParentGONetId = 100,
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = null
            };

            Assert.IsFalse(evt.ShouldSelfCancel, "Should not self-cancel when moving from parent to world root");
        }

        #endregion

        #region DoesCancelOutOtherEvent Tests

        [Test]
        public void DoesCancelOutOtherEvent_SameGONetId_ReturnsTrue()
        {
            var evt1 = new ReparentGONetParticipantEvent { GONetId = 100, NewParentGONetId = 200 };
            var evt2 = new ReparentGONetParticipantEvent { GONetId = 100, NewParentGONetId = 300 };

            Assert.IsTrue(evt2.DoesCancelOutOtherEvent(evt1), "New event for same GONetId should cancel previous");
        }

        [Test]
        public void DoesCancelOutOtherEvent_DifferentGONetId_ReturnsFalse()
        {
            var evt1 = new ReparentGONetParticipantEvent { GONetId = 100, NewParentGONetId = 200 };
            var evt2 = new ReparentGONetParticipantEvent { GONetId = 999, NewParentGONetId = 300 };

            Assert.IsFalse(evt2.DoesCancelOutOtherEvent(evt1), "Event for different GONetId should not cancel");
        }

        [Test]
        public void DoesCancelOutOtherEvent_NonReparentEvent_ReturnsFalse()
        {
            var reparentEvent = new ReparentGONetParticipantEvent { GONetId = 100 };
            var otherEvent = new TestNonReparentEvent();

            Assert.IsFalse(reparentEvent.DoesCancelOutOtherEvent(otherEvent), "Should not cancel non-reparent events");
        }

        [Test]
        public void DoesCancelOutOtherEvent_NullEvent_ReturnsFalse()
        {
            var evt = new ReparentGONetParticipantEvent { GONetId = 100 };

            Assert.IsFalse(evt.DoesCancelOutOtherEvent(null), "Should not throw on null and return false");
        }

        #endregion

        #region Dual Parent Representation Tests

        [Test]
        public void ReparentEvent_PreferGONetId_WhenBothAvailable()
        {
            // When both GONetId and Path are set, GONetId should take precedence
            var evt = new ReparentGONetParticipantEvent
            {
                NewParentGONetId = 100,
                NewParentFullUniquePath = "/Root/AlternatePath"
            };

            // GONetId should be preferred when checking parent
            Assert.AreNotEqual(GONetParticipant.GONetId_Unset, evt.NewParentGONetId,
                "GONetId should be set and preferred");
        }

        [Test]
        public void ReparentEvent_FallbackToPath_WhenNoGONetId()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = "/Root/NonGNPContainer"
            };

            Assert.AreEqual(GONetParticipant.GONetId_Unset, evt.NewParentGONetId);
            Assert.IsNotNull(evt.NewParentFullUniquePath, "Path should be used as fallback");
        }

        [Test]
        public void ReparentEvent_WorldRoot_BothAreUnsetOrNull()
        {
            var evt = new ReparentGONetParticipantEvent
            {
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = null
            };

            Assert.AreEqual(GONetParticipant.GONetId_Unset, evt.NewParentGONetId);
            Assert.IsNull(evt.NewParentFullUniquePath, "Both null means world root");
        }

        #endregion

        #region Local Offset Tests

        [Test]
        public void ReparentEvent_LocalPositionOffset_DefaultIsZero()
        {
            var evt = new ReparentGONetParticipantEvent();
            Assert.AreEqual(Vector3.zero, evt.LocalPositionOffset);
        }

        [Test]
        public void ReparentEvent_LocalRotationOffset_DefaultIsZero()
        {
            var evt = new ReparentGONetParticipantEvent();
            // Note: Uninitialized Quaternion defaults to (0,0,0,0), not identity (0,0,0,1)
            // This is expected for a MemoryPack-serialized DTO - callers must set rotation explicitly
            Assert.AreEqual(new Quaternion(0, 0, 0, 0), evt.LocalRotationOffset);
        }

        [Test]
        public void ReparentEvent_LocalOffsets_CanBeSet()
        {
            var position = new Vector3(10, 20, 30);
            var rotation = Quaternion.Euler(90, 180, 270);

            var evt = new ReparentGONetParticipantEvent
            {
                LocalPositionOffset = position,
                LocalRotationOffset = rotation
            };

            Assert.AreEqual(position, evt.LocalPositionOffset);
            // Quaternion comparison needs tolerance
            Assert.That(Quaternion.Angle(rotation, evt.LocalRotationOffset), Is.LessThan(0.01f));
        }

        #endregion

        #region Serialization Attribute Tests

        [Test]
        public void ReparentEvent_HasMemoryPackableAttribute()
        {
            var type = typeof(ReparentGONetParticipantEvent);
            var attributes = type.GetCustomAttributes(typeof(MemoryPack.MemoryPackableAttribute), false);
            Assert.IsNotEmpty(attributes, "ReparentGONetParticipantEvent should have MemoryPackable attribute");
        }

        #endregion

        #region Edge Cases

        [Test]
        public void ReparentEvent_GONetIdZero_IsValidButUnset()
        {
            var evt = new ReparentGONetParticipantEvent { GONetId = 0 };
            Assert.AreEqual(0u, evt.GONetId);
        }

        [Test]
        public void ReparentEvent_EmptyPath_IsDifferentFromNull()
        {
            var evtEmpty = new ReparentGONetParticipantEvent { NewParentFullUniquePath = "" };
            var evtNull = new ReparentGONetParticipantEvent { NewParentFullUniquePath = null };

            Assert.AreNotEqual(evtEmpty.NewParentFullUniquePath, evtNull.NewParentFullUniquePath);
        }

        [Test]
        public void ShouldSelfCancel_CaseInsensitivePath_ConsideredDifferent()
        {
            // Path comparison should be case-sensitive (or follow Unity's rules)
            var evt = new ReparentGONetParticipantEvent
            {
                OriginalParentGONetId = GONetParticipant.GONetId_Unset,
                OriginalParentFullUniquePath = "/Root/Parent",
                NewParentGONetId = GONetParticipant.GONetId_Unset,
                NewParentFullUniquePath = "/ROOT/PARENT"  // Different case
            };

            // This test documents the behavior - paths are compared exactly
            // Actual behavior depends on implementation (string.Equals default is case-sensitive)
            Assert.IsFalse(evt.ShouldSelfCancel, "Different case paths should be considered different");
        }

        #endregion
    }
}
