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

using NUnit.Framework;
using GONet.DistributedHost;
using System.Collections.Generic;

namespace GONet.Editor.UnitTests.DistributedHost
{
    [TestFixture]
    public class GONetViceHostTests
    {
        #region Constants Tests

        [Test]
        public void ViceHost_Constants_HaveCorrectValues()
        {
            // Critical sync at 10 Hz
            Assert.AreEqual(10f, GONetViceHostManager.CRITICAL_SYNC_HZ);

            // Full sync at 1 Hz
            Assert.AreEqual(1f, GONetViceHostManager.FULL_SYNC_HZ);

            // Max delta size 16 KB
            Assert.AreEqual(16384, GONetViceHostManager.MAX_DELTA_SIZE_BYTES);

            // Post-promotion cooldown
            Assert.AreEqual(3.0f, GONetViceHostManager.POST_PROMOTION_COOLDOWN_SECONDS);
        }

        #endregion

        #region ViceHostFullSyncMessage Tests

        [Test]
        public void ViceHostFullSyncMessage_CanBeCreated()
        {
            // Arrange & Act
            var message = new ViceHostFullSyncMessage
            {
                HostIdentity = new HostIdentity(1234, 1, 1023, 2),
                SyncSequence = 42,
                HostTimeElapsedSeconds = 123.456,
                PersistentEventCount = 10,
                GONetIdWatermark = 1000,
                RpcSequenceWatermarks = new Dictionary<ushort, uint>
                {
                    { 1, 100 },
                    { 2, 200 }
                }
            };

            // Assert
            Assert.AreEqual(new HostIdentity(1234, 1, 1023, 2), message.HostIdentity);
            Assert.AreEqual(42UL, message.SyncSequence);
            Assert.AreEqual(123.456, message.HostTimeElapsedSeconds, 0.001);
            Assert.AreEqual(10, message.PersistentEventCount);
            Assert.AreEqual(1000u, message.GONetIdWatermark);
            Assert.AreEqual(2, message.RpcSequenceWatermarks.Count);
            Assert.AreEqual(100u, message.RpcSequenceWatermarks[1]);
            Assert.AreEqual(200u, message.RpcSequenceWatermarks[2]);
        }

        [Test]
        public void ViceHostFullSyncMessage_ImplementsITransientEvent()
        {
            // Arrange
            var message = new ViceHostFullSyncMessage();

            // Assert
            Assert.IsTrue(message is ITransientEvent);
        }

        #endregion

        #region ViceHostDeltaSyncMessage Tests

        [Test]
        public void ViceHostDeltaSyncMessage_CanBeCreated()
        {
            // Arrange & Act
            var message = new ViceHostDeltaSyncMessage
            {
                HostIdentity = new HostIdentity(5678, 2, 1023, 3),
                SyncSequence = 50,
                BaseSequence = 40,
                NewEventsData = new byte[] { 1, 2, 3 },
                GONetIdWatermark = 2000,
                RpcSequenceDeltas = new Dictionary<ushort, uint>
                {
                    { 3, 300 }
                }
            };

            // Assert
            Assert.AreEqual(new HostIdentity(5678, 2, 1023, 3), message.HostIdentity);
            Assert.AreEqual(50UL, message.SyncSequence);
            Assert.AreEqual(40UL, message.BaseSequence);
            Assert.AreEqual(3, message.NewEventsData.Length);
            Assert.AreEqual(2000u, message.GONetIdWatermark);
            Assert.AreEqual(1, message.RpcSequenceDeltas.Count);
        }

        [Test]
        public void ViceHostDeltaSyncMessage_ImplementsITransientEvent()
        {
            // Arrange
            var message = new ViceHostDeltaSyncMessage();

            // Assert
            Assert.IsTrue(message is ITransientEvent);
        }

        #endregion

        #region ViceHostSyncAck Tests

        [Test]
        public void ViceHostSyncAck_CanBeCreated()
        {
            // Arrange & Act
            var ack = new ViceHostSyncAck
            {
                AcknowledgedSequence = 99
            };

            // Assert
            Assert.AreEqual(99UL, ack.AcknowledgedSequence);
        }

        [Test]
        public void ViceHostSyncAck_ImplementsITransientEvent()
        {
            // Arrange
            var ack = new ViceHostSyncAck();

            // Assert
            Assert.IsTrue(ack is ITransientEvent);
        }

        #endregion

        #region ViceHostEventType Tests

        [Test]
        public void ViceHostEventType_HasExpectedValues()
        {
            Assert.AreEqual(0, (int)ViceHostEventType.Unknown);
            Assert.AreEqual(1, (int)ViceHostEventType.Instantiate);
            Assert.AreEqual(2, (int)ViceHostEventType.Despawn);
            Assert.AreEqual(3, (int)ViceHostEventType.SceneLoad);
            Assert.AreEqual(4, (int)ViceHostEventType.SceneUnload);
            Assert.AreEqual(5, (int)ViceHostEventType.PersistentRpc);
            Assert.AreEqual(6, (int)ViceHostEventType.OwnerAuthorityAssignment);
        }

        #endregion

        #region ViceHostHandoffState Tests

        [Test]
        public void ViceHostHandoffState_CanBeCreated()
        {
            // Arrange & Act
            var state = new ViceHostHandoffState
            {
                LastSyncSequence = 123,
                GONetIdWatermark = 5000,
                HostTimeOffset = 0.5,
                RpcSequenceWatermarks = new Dictionary<ushort, uint>
                {
                    { 1, 10 },
                    { 2, 20 }
                },
                ShadowEventCount = 50
            };

            // Assert
            Assert.AreEqual(123UL, state.LastSyncSequence);
            Assert.AreEqual(5000u, state.GONetIdWatermark);
            Assert.AreEqual(0.5, state.HostTimeOffset, 0.001);
            Assert.AreEqual(2, state.RpcSequenceWatermarks.Count);
            Assert.AreEqual(50, state.ShadowEventCount);
        }

        #endregion

        #region ViceHostPersistentEventRecord Tests

        [Test]
        public void ViceHostPersistentEventRecord_CanBeCreated()
        {
            // Arrange & Act
            var record = new ViceHostPersistentEventRecord
            {
                EventType = ViceHostEventType.Instantiate,
                SerializedData = new byte[] { 1, 2, 3, 4 },
                OccurredAtTicks = 999999
            };

            // Assert
            Assert.AreEqual(ViceHostEventType.Instantiate, record.EventType);
            Assert.AreEqual(4, record.SerializedData.Length);
            Assert.AreEqual(999999L, record.OccurredAtTicks);
        }

        #endregion
    }
}
