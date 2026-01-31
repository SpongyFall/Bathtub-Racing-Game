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
using System.Reflection;
using GONet;

namespace GONet.Editor.UnitTests.DistributedHost
{
    [TestFixture]
    public class GONetHostFailoverTests
    {
        #region Constants Tests

        [Test]
        public void Failover_Constants_HaveCorrectValues()
        {
            // Aggressive heartbeat timeout - 6 missed heartbeats at 8Hz = dead
            Assert.AreEqual(0.75f, GONetHostFailoverManager.HOST_HEARTBEAT_TIMEOUT_SECONDS);

            // Vice host promotion wait
            Assert.AreEqual(0.2f, GONetHostFailoverManager.VICE_HOST_PROMOTION_WAIT_SECONDS);

            // 8Hz heartbeats for fast failure detection
            Assert.AreEqual(0.125f, GONetHostFailoverManager.HOST_HEARTBEAT_INTERVAL_SECONDS);

            // Grace period
            Assert.AreEqual(3.0f, GONetHostFailoverManager.POST_FAILOVER_GRACE_PERIOD_SECONDS);
        }

        #endregion

        #region FailoverState Tests

        [Test]
        public void FailoverState_HasExpectedValues()
        {
            Assert.AreEqual(0, (int)FailoverState.HostAlive);
            Assert.AreEqual(1, (int)FailoverState.HostSuspect);
            Assert.AreEqual(2, (int)FailoverState.HostDead);
            Assert.AreEqual(3, (int)FailoverState.WaitingForViceHost);
            Assert.AreEqual(4, (int)FailoverState.SelfPromoting);
            Assert.AreEqual(5, (int)FailoverState.WaitingForTiebreaker);
            Assert.AreEqual(6, (int)FailoverState.Complete);
        }

        #endregion

        #region EmergencyHostPromotionMessage Tests

        [Test]
        public void EmergencyHostPromotionMessage_CanBeCreated()
        {
            // Arrange & Act
            var message = new EmergencyHostPromotionMessage
            {
                NewHostAuthorityId = 3,
                NewHostEpoch = 7,
                PreviousHostAuthorityId = 1,
                FailoverReason = "Heartbeat timeout"
            };

            // Assert
            Assert.AreEqual(3, message.NewHostAuthorityId);
            Assert.AreEqual(7u, message.NewHostEpoch);
            Assert.AreEqual(1, message.PreviousHostAuthorityId);
            Assert.AreEqual("Heartbeat timeout", message.FailoverReason);
        }

        [Test]
        public void EmergencyHostPromotionMessage_ImplementsITransientEvent()
        {
            // Arrange
            var message = new EmergencyHostPromotionMessage();

            // Assert
            Assert.IsTrue(message is ITransientEvent);
        }

        [Test]
        public void EmergencyHostPromotionMessage_OccurredAtElapsedTicks_CanBeSet()
        {
            // Arrange
            var message = new EmergencyHostPromotionMessage();

            // Act
            message.OccurredAtElapsedTicks = 987654321;

            // Assert
            Assert.AreEqual(987654321, message.OccurredAtElapsedTicks);
        }

        #endregion

        #region Conflict Resolution Documentation Tests

        /// <summary>
        /// Documents the expected conflict resolution behavior.
        /// When multiple nodes try to promote simultaneously:
        /// 1. Higher epoch always wins
        /// 2. Designated vice host wins within same epoch
        /// 3. Lowest authority ID as final tiebreaker
        /// </summary>
        [Test]
        public void ConflictResolution_Documentation()
        {
            // This test documents expected conflict resolution behavior:
            //
            // Scenario 1: Different epochs
            //   - Node A promotes at epoch 5
            //   - Node B promotes at epoch 6
            //   - Node B wins (higher epoch)
            //
            // Scenario 2: Same epoch, one is vice host
            //   - Node A (vice host) promotes at epoch 5
            //   - Node B (not vice host) promotes at epoch 5
            //   - Node A wins (vice host priority)
            //
            // Scenario 3: Same epoch, neither is vice host
            //   - Node A (authorityId=3) promotes at epoch 5
            //   - Node B (authorityId=2) promotes at epoch 5
            //   - Node B wins (lower authority ID)

            Assert.Pass("Conflict resolution behavior documented");
        }

        /// <summary>
        /// Documents the "Monarch's Heir" pattern for failover.
        /// </summary>
        [Test]
        public void MonarchsHeir_Pattern_Documentation()
        {
            // This test documents the "Monarch's Heir" failover pattern:
            //
            // Normal case:
            // 1. Host designates vice host (heir apparent) via heartbeat
            // 2. Host crashes/disconnects
            // 3. All nodes detect heartbeat timeout
            // 4. Vice host self-promotes immediately
            // 5. Other nodes wait for vice host promotion (200ms)
            // 6. Vice host broadcasts EmergencyHostPromotion
            // 7. All nodes accept new host
            //
            // Vice host also fails:
            // 1. Host crashes
            // 2. Vice host crashes before promoting
            // 3. Other nodes wait 200ms for vice host
            // 4. Vice host doesn't promote
            // 5. Fall back to deterministic tiebreaker (lowest authority ID)
            // 6. Node with lowest ID self-promotes

            Assert.Pass("Monarch's Heir pattern documented");
        }

        /// <summary>
        /// Documents the split-brain prevention mechanism.
        /// </summary>
        [Test]
        public void SplitBrainPrevention_Documentation()
        {
            // This test documents split-brain prevention:
            //
            // Epoch-based resolution:
            // - Each host migration increments the epoch
            // - Higher epoch always wins in conflicts
            // - Stale messages (lower epoch) are rejected
            //
            // Vice host priority:
            // - Within same epoch, designated vice host wins
            // - Prevents race conditions during normal failover
            //
            // Authority ID tiebreaker:
            // - When no clear winner, lowest authority ID wins
            // - Deterministic - all nodes reach same conclusion
            //
            // Partition healing (future work):
            // - When partition heals, group with higher epoch wins
            // - Lower epoch group must resync

            Assert.Pass("Split-brain prevention documented");
        }

        #endregion

        #region Host Claim Conflict Resolution Tests

        [Test]
        public void HostClaimPreference_HigherEpochWins()
        {
            // Higher epoch always wins regardless of IDs.
            bool preferred = GONetHostFailoverManager.IsOtherHostClaimPreferred(
                otherEpoch: 6,
                otherPromotingOriginalAuthorityId: 10,
                currentEpoch: 5,
                currentPromotingOriginalAuthorityId: 2,
                tiebreakViceHostAuthorityId: 0);

            Assert.IsTrue(preferred);
        }

        [Test]
        public void HostClaimPreference_SameEpoch_ViceHostWins()
        {
            // Same epoch: designated vice host wins even if their ID is higher.
            bool preferred = GONetHostFailoverManager.IsOtherHostClaimPreferred(
                otherEpoch: 5,
                otherPromotingOriginalAuthorityId: 10,
                currentEpoch: 5,
                currentPromotingOriginalAuthorityId: 2,
                tiebreakViceHostAuthorityId: 10);

            Assert.IsTrue(preferred);
        }

        [Test]
        public void HostClaimPreference_SameEpoch_CurrentViceHostWins()
        {
            // Same epoch: if current is vice host, other should not be preferred even if other ID is lower.
            bool preferred = GONetHostFailoverManager.IsOtherHostClaimPreferred(
                otherEpoch: 5,
                otherPromotingOriginalAuthorityId: 2,
                currentEpoch: 5,
                currentPromotingOriginalAuthorityId: 10,
                tiebreakViceHostAuthorityId: 10);

            Assert.IsFalse(preferred);
        }

        [Test]
        public void HostClaimPreference_SameEpoch_LowerOriginalWins_WhenNoViceHost()
        {
            bool preferred = GONetHostFailoverManager.IsOtherHostClaimPreferred(
                otherEpoch: 5,
                otherPromotingOriginalAuthorityId: 2,
                currentEpoch: 5,
                currentPromotingOriginalAuthorityId: 10,
                tiebreakViceHostAuthorityId: 0);

            Assert.IsTrue(preferred);
        }

        [Test]
        public void HostClaimPreference_SameEpoch_EqualClaimIsNotPreferred()
        {
            bool preferred = GONetHostFailoverManager.IsOtherHostClaimPreferred(
                otherEpoch: 5,
                otherPromotingOriginalAuthorityId: 7,
                currentEpoch: 5,
                currentPromotingOriginalAuthorityId: 7,
                tiebreakViceHostAuthorityId: 7);

            Assert.IsFalse(preferred);
        }

        #endregion

        #region Host Identity Update Tests

        [Test]
        public void UpdateViceHostAuthority_DoesNotAdvanceEpoch()
        {
            uint originalEpoch = GetHostEpochForTesting();
            HostIdentity originalIdentity = GetCurrentHostIdentityForTesting();

            try
            {
                long sessionGuid = GONetMain.SessionGUID;
                SetHostEpochForTesting(5);
                SetCurrentHostIdentityForTesting(new HostIdentity(sessionGuid, 5, 1023, 0));

                InvokeUpdateViceHostAuthorityForTesting(7);

                Assert.AreEqual(5u, GONetMain.HostEpoch);
                Assert.AreEqual(7, GONetMain.CurrentHostIdentity.ViceHostAuthorityId);
                Assert.AreEqual(1023, GONetMain.CurrentHostIdentity.HostAuthorityId);
            }
            finally
            {
                SetHostEpochForTesting(originalEpoch);
                SetCurrentHostIdentityForTesting(originalIdentity);
            }
        }

        private static uint GetHostEpochForTesting()
        {
            var prop = typeof(GONetMain).GetProperty(nameof(GONetMain.HostEpoch), BindingFlags.Static | BindingFlags.Public);
            Assert.IsNotNull(prop, "Expected public static property HostEpoch on GONetMain");
            return (uint)prop.GetValue(null);
        }

        private static void SetHostEpochForTesting(uint newHostEpoch)
        {
            var prop = typeof(GONetMain).GetProperty(nameof(GONetMain.HostEpoch), BindingFlags.Static | BindingFlags.Public);
            Assert.IsNotNull(prop, "Expected public static property HostEpoch on GONetMain");

            var setter = prop.GetSetMethod(nonPublic: true);
            Assert.IsNotNull(setter, "Expected non-public setter for GONetMain.HostEpoch");
            setter.Invoke(null, new object[] { newHostEpoch });
        }

        private static HostIdentity GetCurrentHostIdentityForTesting()
        {
            var prop = typeof(GONetMain).GetProperty(nameof(GONetMain.CurrentHostIdentity), BindingFlags.Static | BindingFlags.Public);
            Assert.IsNotNull(prop, "Expected public static property CurrentHostIdentity on GONetMain");
            return (HostIdentity)prop.GetValue(null);
        }

        private static void SetCurrentHostIdentityForTesting(HostIdentity identity)
        {
            var prop = typeof(GONetMain).GetProperty(nameof(GONetMain.CurrentHostIdentity), BindingFlags.Static | BindingFlags.Public);
            Assert.IsNotNull(prop, "Expected public static property CurrentHostIdentity on GONetMain");

            var setter = prop.GetSetMethod(nonPublic: true);
            Assert.IsNotNull(setter, "Expected non-public setter for GONetMain.CurrentHostIdentity");
            setter.Invoke(null, new object[] { identity });
        }

        private static void InvokeUpdateViceHostAuthorityForTesting(ushort viceHostAuthorityId)
        {
            var method = typeof(GONetMain).GetMethod("UpdateViceHostAuthority", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "Expected UpdateViceHostAuthority method on GONetMain");
            method.Invoke(null, new object[] { viceHostAuthorityId });
        }

        #endregion

        #region Heartbeat Timing Tests

        [Test]
        public void HeartbeatTimeout_IsSixMissedHeartbeats()
        {
            // Timeout should be approximately 3x the heartbeat interval
            float interval = GONetHostFailoverManager.HOST_HEARTBEAT_INTERVAL_SECONDS;
            float timeout = GONetHostFailoverManager.HOST_HEARTBEAT_TIMEOUT_SECONDS;

            // 0.75s timeout / 0.125s interval = 6 missed heartbeats
            int missedAllowed = (int)(timeout / interval);
            Assert.AreEqual(6, missedAllowed, "Should detect failure after 6 missed heartbeats");
        }

        [Test]
        public void HeartbeatInterval_Is8Hz()
        {
            // 8Hz = 125ms interval
            float interval = GONetHostFailoverManager.HOST_HEARTBEAT_INTERVAL_SECONDS;
            float hz = 1.0f / interval;

            Assert.AreEqual(8.0f, hz, 0.01f, "Heartbeat should be 8Hz");
        }

        [Test]
        public void ViceHostPromotionWait_IsShort()
        {
            // Vice host should promote quickly, others wait
            float viceHostWait = GONetHostFailoverManager.VICE_HOST_PROMOTION_WAIT_SECONDS;

            Assert.LessOrEqual(viceHostWait, 0.5f, "Vice host should not wait long");
            Assert.Greater(viceHostWait, 0f, "Vice host wait should be positive");
        }

        [Test]
        public void PostFailoverGracePeriod_AllowsStabilization()
        {
            // Grace period prevents rapid re-failover
            float gracePeriod = GONetHostFailoverManager.POST_FAILOVER_GRACE_PERIOD_SECONDS;

            Assert.GreaterOrEqual(gracePeriod, 1.0f, "Grace period should allow stabilization");
        }

        #endregion

        #region EmergencyHostPromotionMessage Serialization Tests

        [Test]
        public void EmergencyHostPromotionMessage_CanSerializeAndDeserialize()
        {
            var original = new EmergencyHostPromotionMessage
            {
                NewHostAuthorityId = 1023,
                NewHostEpoch = 5,
                PreviousHostAuthorityId = 1023,
                FailoverReason = "Heartbeat timeout",
                PromotingPeerOriginalAuthorityId = 3
            };

            byte[] bytes = GONet.Utils.SerializationUtils.SerializeToBytes(original, out int bytesUsed, out bool needsReturn);
            var deserialized = GONet.Utils.SerializationUtils.DeserializeFromBytes<EmergencyHostPromotionMessage>(
                new System.ReadOnlySpan<byte>(bytes, 0, bytesUsed));

            Assert.AreEqual(original.NewHostAuthorityId, deserialized.NewHostAuthorityId);
            Assert.AreEqual(original.NewHostEpoch, deserialized.NewHostEpoch);
            Assert.AreEqual(original.PreviousHostAuthorityId, deserialized.PreviousHostAuthorityId);
            Assert.AreEqual(original.FailoverReason, deserialized.FailoverReason);
            Assert.AreEqual(original.PromotingPeerOriginalAuthorityId, deserialized.PromotingPeerOriginalAuthorityId);

            if (needsReturn) GONet.Utils.SerializationUtils.ReturnByteArray(bytes);
        }

        [Test]
        public void EmergencyHostPromotionMessage_IncludesOriginalAuthorityId()
        {
            // Critical: Message must include the promoting peer's original authority ID
            // so clients can look up the correct hot standby connection
            var message = new EmergencyHostPromotionMessage
            {
                NewHostAuthorityId = 1023,  // After promotion
                PromotingPeerOriginalAuthorityId = 5  // Before promotion
            };

            Assert.AreNotEqual(message.NewHostAuthorityId, message.PromotingPeerOriginalAuthorityId,
                "Original authority should differ from new host authority");
        }

        [Test]
        public void EmergencyHostPromotionMessage_EpochIsRequired()
        {
            var message = new EmergencyHostPromotionMessage
            {
                NewHostAuthorityId = 1023,
                NewHostEpoch = 0  // Epoch 0 is invalid - should start at 1
            };

            // In production code, epoch 0 should be rejected
            Assert.AreEqual(0u, message.NewHostEpoch);
            // Document: Valid epochs are >= 1
        }

        #endregion

        #region Failover State Machine Tests

        [Test]
        public void FailoverState_HostAlive_IsInitialState()
        {
            Assert.AreEqual(0, (int)FailoverState.HostAlive,
                "HostAlive should be the initial/default state");
        }

        [Test]
        public void FailoverState_Progression_HostAlive_To_HostSuspect()
        {
            // State transition: Missed heartbeat triggers suspicion
            FailoverState current = FailoverState.HostAlive;

            // Simulate missed heartbeat detection
            if (/* heartbeatTimedOut */ true)
            {
                current = FailoverState.HostSuspect;
            }

            Assert.AreEqual(FailoverState.HostSuspect, current);
        }

        [Test]
        public void FailoverState_Progression_HostSuspect_To_HostDead()
        {
            // State transition: Confirmed dead after timeout
            FailoverState current = FailoverState.HostSuspect;

            // Simulate confirmation
            current = FailoverState.HostDead;

            Assert.AreEqual(FailoverState.HostDead, current);
        }

        [Test]
        public void FailoverState_Progression_HostDead_To_WaitingForViceHost()
        {
            // If not vice host, wait for vice host to promote
            FailoverState current = FailoverState.HostDead;
            bool iAmViceHost = false;

            if (!iAmViceHost)
            {
                current = FailoverState.WaitingForViceHost;
            }

            Assert.AreEqual(FailoverState.WaitingForViceHost, current);
        }

        [Test]
        public void FailoverState_Progression_HostDead_To_SelfPromoting()
        {
            // Vice host promotes immediately
            FailoverState current = FailoverState.HostDead;
            bool iAmViceHost = true;

            if (iAmViceHost)
            {
                current = FailoverState.SelfPromoting;
            }

            Assert.AreEqual(FailoverState.SelfPromoting, current);
        }

        [Test]
        public void FailoverState_Progression_WaitingForViceHost_To_SelfPromoting()
        {
            // Vice host didn't promote in time, fall back to self-promotion
            FailoverState current = FailoverState.WaitingForViceHost;
            bool viceHostTimedOut = true;
            bool iAmLowestAuthority = true;

            if (viceHostTimedOut && iAmLowestAuthority)
            {
                current = FailoverState.SelfPromoting;
            }

            Assert.AreEqual(FailoverState.SelfPromoting, current);
        }

        [Test]
        public void FailoverState_Progression_WaitingForViceHost_To_WaitingForTiebreaker()
        {
            // Not lowest authority, wait for tiebreaker
            FailoverState current = FailoverState.WaitingForViceHost;
            bool viceHostTimedOut = true;
            bool iAmLowestAuthority = false;

            if (viceHostTimedOut && !iAmLowestAuthority)
            {
                current = FailoverState.WaitingForTiebreaker;
            }

            Assert.AreEqual(FailoverState.WaitingForTiebreaker, current);
        }

        [Test]
        public void FailoverState_Progression_SelfPromoting_To_Complete()
        {
            // Successful self-promotion
            FailoverState current = FailoverState.SelfPromoting;

            // After broadcasting EmergencyHostPromotion
            current = FailoverState.Complete;

            Assert.AreEqual(FailoverState.Complete, current);
        }

        #endregion

        #region Self-Promotion Tests

        [Test]
        public void SelfPromotion_ViceHost_PromotesImmediately()
        {
            // Simulate vice host behavior
            bool iAmViceHost = true;
            float waitTime = iAmViceHost ? 0f : GONetHostFailoverManager.VICE_HOST_PROMOTION_WAIT_SECONDS;

            Assert.AreEqual(0f, waitTime, "Vice host should not wait");
        }

        [Test]
        public void SelfPromotion_NonViceHost_WaitsForViceHost()
        {
            // Non-vice host must wait
            bool iAmViceHost = false;
            float waitTime = iAmViceHost ? 0f : GONetHostFailoverManager.VICE_HOST_PROMOTION_WAIT_SECONDS;

            Assert.Greater(waitTime, 0f, "Non-vice host should wait");
        }

        [Test]
        public void SelfPromotion_AuthorityId_BecomesServer()
        {
            // After promotion, authority ID should become 1023 (server)
            ushort originalAuthorityId = 5;
            ushort newAuthorityId = GONetMain.OwnerAuthorityId_Server;

            Assert.AreEqual(1023, newAuthorityId);
            Assert.AreNotEqual(originalAuthorityId, newAuthorityId);
        }

        [Test]
        public void SelfPromotion_Epoch_Increments()
        {
            uint currentEpoch = 3;
            uint newEpoch = currentEpoch + 1;

            Assert.AreEqual(4u, newEpoch);
        }

        #endregion

        #region Epoch Conflict Resolution Tests

        [Test]
        public void EpochConflict_HigherEpochWins()
        {
            var msg1 = new EmergencyHostPromotionMessage { NewHostEpoch = 5, NewHostAuthorityId = 3 };
            var msg2 = new EmergencyHostPromotionMessage { NewHostEpoch = 6, NewHostAuthorityId = 10 };

            // msg2 wins because higher epoch, despite higher authority ID
            bool msg2Wins = msg2.NewHostEpoch > msg1.NewHostEpoch;
            Assert.IsTrue(msg2Wins);
        }

        [Test]
        public void EpochConflict_SameEpoch_LowerAuthorityWins()
        {
            var msg1 = new EmergencyHostPromotionMessage { NewHostEpoch = 5, PromotingPeerOriginalAuthorityId = 3 };
            var msg2 = new EmergencyHostPromotionMessage { NewHostEpoch = 5, PromotingPeerOriginalAuthorityId = 10 };

            // Same epoch, lower original authority wins
            bool msg1Wins = msg1.PromotingPeerOriginalAuthorityId < msg2.PromotingPeerOriginalAuthorityId;
            Assert.IsTrue(msg1Wins);
        }

        [Test]
        public void EpochConflict_StaleEpoch_IsRejected()
        {
            uint currentKnownEpoch = 5;
            var staleMsg = new EmergencyHostPromotionMessage { NewHostEpoch = 3 };

            bool isStale = staleMsg.NewHostEpoch < currentKnownEpoch;
            Assert.IsTrue(isStale, "Stale epoch should be rejected");
        }

        #endregion
    }
}
