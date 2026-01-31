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
 * -The ability to commercialize products built on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace GONet.Editor.UnitTests.DistributedHost
{
    [TestFixture]
    public class GONetPoolingTests
    {
        private FieldInfo myAuthorityField;
        private ushort previousAuthority;

        [SetUp]
        public void SetUp()
        {
            myAuthorityField = typeof(GONetMain).GetField("<MyAuthorityId>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(myAuthorityField, "Expected to locate GONetMain.MyAuthorityId backing field.");
            previousAuthority = (ushort)myAuthorityField.GetValue(null);
        }

        [TearDown]
        public void TearDown()
        {
            if (myAuthorityField != null)
            {
                myAuthorityField.SetValue(null, previousAuthority);
            }
        }

        private void SetMyAuthorityId(ushort value)
        {
            myAuthorityField.SetValue(null, value);
        }

        [Test]
        public void IsLocallyControlled_ReturnsTrue_WhenBorrowerMatchesLocalAuthority()
        {
            SetMyAuthorityId(5);

            var go = new GameObject("PooledBorrower");
            go.SetActive(false);
            var participant = go.AddComponent<GONetParticipant>();
            participant.OwnerAuthorityId = GONetMain.OwnerAuthorityId_Server;
            participant.RemotelyControlledByAuthorityId = 5;

            Assert.IsFalse(participant.IsMine);
            Assert.IsTrue(participant.IsMine_ToRemotelyControl);
            Assert.IsTrue(participant.IsLocallyControlled);

            Object.DestroyImmediate(go);
        }

        [Test]
        public void IsLocallyControlled_ReturnsFalse_WhenNotOwnerOrBorrower()
        {
            SetMyAuthorityId(5);

            var go = new GameObject("PooledNotLocal");
            go.SetActive(false);
            var participant = go.AddComponent<GONetParticipant>();
            participant.OwnerAuthorityId = GONetMain.OwnerAuthorityId_Server;
            participant.RemotelyControlledByAuthorityId = 7;

            Assert.IsFalse(participant.IsMine);
            Assert.IsFalse(participant.IsMine_ToRemotelyControl);
            Assert.IsFalse(participant.IsLocallyControlled);

            Object.DestroyImmediate(go);
        }

        [Test]
        public void PoolReturnEvent_CancelsBorrowEvent_WhenGONetIdMatches()
        {
            var borrowEvent = new PoolObjectBorrowEvent { GONetId = 1001 };
            var returnEvent = new PoolObjectReturnEvent { GONetId = 1001 };

            Assert.IsTrue(returnEvent.DoesCancelOutOtherEvent(borrowEvent));
        }

        [Test]
        public void PoolBorrowEvent_CancelsPriorBorrowEvent_WhenGONetIdMatches()
        {
            var priorBorrowEvent = new PoolObjectBorrowEvent { GONetId = 3003 };
            var nextBorrowEvent = new PoolObjectBorrowEvent { GONetId = 3003 };

            Assert.IsTrue(nextBorrowEvent.DoesCancelOutOtherEvent(priorBorrowEvent));
        }

        [Test]
        public void PoolDestroyedEvent_CancelsBorrowEvent_WhenGONetIdMatches()
        {
            var borrowEvent = new PoolObjectBorrowEvent { GONetId = 2002 };
            var destroyedEvent = new PoolObjectDestroyedEvent { GONetId = 2002 };

            Assert.IsTrue(destroyedEvent.DoesCancelOutOtherEvent(borrowEvent));
        }
    }
}
