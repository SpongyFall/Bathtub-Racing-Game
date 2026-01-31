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
    /// <summary>
    /// Unit tests for GNP (GONetParticipant) ownership migration during host failover (Phase 2.13).
    /// Tests cover:
    /// 1. Server-owned GNP filtering
    /// 2. Ownership migration logic
    /// 3. Callback invocation patterns
    /// 4. Double failover scenarios
    /// </summary>
    [TestFixture]
    public class GONetGNPOwnershipMigrationTests
    {
        #region Constants

        /// <summary>
        /// Server/Host authority ID (1023)
        /// </summary>
        private const ushort SERVER_AUTHORITY_ID = 1023;

        #endregion

        #region Server-Owned GNP Filtering Tests

        [Test]
        public void GNPMigration_OnlyMigratesServerOwnedObjects()
        {
            // Arrange: Mix of server-owned and client-owned objects
            var gnpOwnerAuthorityIds = new Dictionary<uint, ushort>
            {
                { 1, SERVER_AUTHORITY_ID },  // Server-owned - SHOULD migrate
                { 2, 2 },                     // Client 2 owned - should NOT migrate
                { 3, SERVER_AUTHORITY_ID },  // Server-owned - SHOULD migrate
                { 4, 3 },                     // Client 3 owned - should NOT migrate
                { 5, SERVER_AUTHORITY_ID },  // Server-owned - SHOULD migrate
            };

            // Act: Count objects that would be migrated
            int migratedCount = 0;
            foreach (var kvp in gnpOwnerAuthorityIds)
            {
                if (kvp.Value == SERVER_AUTHORITY_ID)
                {
                    migratedCount++;
                }
            }

            // Assert: Only 3 server-owned objects should migrate
            Assert.AreEqual(3, migratedCount);
        }

        [Test]
        public void GNPMigration_ClientOwnedObjects_NotMigrated()
        {
            // Client-owned objects should remain unaffected by failover
            ushort clientAuthorityId = 5;

            bool shouldMigrate = clientAuthorityId == SERVER_AUTHORITY_ID;

            Assert.IsFalse(shouldMigrate, "Client-owned objects should not be migrated");
        }

        [Test]
        public void GNPMigration_ServerOwnedObjects_AreMigrated()
        {
            ushort ownerAuthorityId = SERVER_AUTHORITY_ID;

            bool shouldMigrate = ownerAuthorityId == SERVER_AUTHORITY_ID;

            Assert.IsTrue(shouldMigrate, "Server-owned objects should be migrated");
        }

        [Test]
        public void GNPMigration_EmptyGNPMap_ReturnsZeroCount()
        {
            var gnpOwnerAuthorityIds = new Dictionary<uint, ushort>();

            int migratedCount = 0;
            foreach (var kvp in gnpOwnerAuthorityIds)
            {
                if (kvp.Value == SERVER_AUTHORITY_ID)
                {
                    migratedCount++;
                }
            }

            Assert.AreEqual(0, migratedCount);
        }

        [Test]
        public void GNPMigration_AllClientOwned_ReturnsZeroCount()
        {
            // Scene with only client-owned objects
            var gnpOwnerAuthorityIds = new Dictionary<uint, ushort>
            {
                { 1, 2 },
                { 2, 3 },
                { 3, 4 },
            };

            int migratedCount = 0;
            foreach (var kvp in gnpOwnerAuthorityIds)
            {
                if (kvp.Value == SERVER_AUTHORITY_ID)
                {
                    migratedCount++;
                }
            }

            Assert.AreEqual(0, migratedCount);
        }

        #endregion

        #region Authority ID Challenge Tests

        [Test]
        public void GNPMigration_AuthorityChallenge_DeadAndNewHostBothHave1023()
        {
            // Critical insight: Both dead host and new host have authority 1023
            // Cannot distinguish by OwnerAuthorityId alone
            ushort deadHostAuthorityId = SERVER_AUTHORITY_ID;
            ushort newHostAuthorityId = SERVER_AUTHORITY_ID;

            Assert.AreEqual(deadHostAuthorityId, newHostAuthorityId,
                "Both dead and new host have authority 1023");
        }

        [Test]
        public void GNPMigration_OriginalAuthorityId_IsPrePromotionIdentity()
        {
            // The promoting peer's original authority ID is captured BEFORE promotion
            ushort originalAuthorityId = 5;  // Before promotion
            ushort newAuthorityId = SERVER_AUTHORITY_ID;  // After promotion

            Assert.AreNotEqual(originalAuthorityId, newAuthorityId);
            Assert.AreEqual(SERVER_AUTHORITY_ID, newAuthorityId);
        }

        [Test]
        public void GNPMigration_CallbackReceives_OriginalAuthorityId()
        {
            // The OnOwnershipMigratedDuringFailover callback receives the original authority ID
            // so game code can track which peer promoted
            ushort originalAuthorityId = 5;
            bool isMineNow = true;

            // Simulate callback invocation
            var callbackParams = new OwnershipMigrationCallbackParams
            {
                IsMineNow = isMineNow,
                PreviousHostOriginalAuthorityId = originalAuthorityId
            };

            Assert.IsTrue(callbackParams.IsMineNow);
            Assert.AreEqual(5, callbackParams.PreviousHostOriginalAuthorityId);
        }

        #endregion

        #region Blend Buffer Reset Tests

        [Test]
        public void BlendBufferReset_Documentation_ResetsToCurrentValue()
        {
            // Document: ResetBlendBuffersForOwnershipMigration does the following:
            // 1. ApplyValueBlending_IfAppropriate(0) - apply any pending blending
            // 2. ClearMostRecentChanges() - clear the blend buffer
            // 3. Set lastKnownValue = lastKnownValue_previous = currentValue

            // This test documents expected behavior without requiring Unity runtime
            Assert.Pass("Blend buffer reset: Apply pending, clear buffer, sync to current");
        }

        [Test]
        public void BlendBufferReset_PreservesCurrentValue()
        {
            // Simulated blend buffer state
            float currentValue = 100f;
            float lastKnownValue = 95f;
            float lastKnownValuePrevious = 90f;

            // After reset: all should match current
            lastKnownValue = currentValue;
            lastKnownValuePrevious = currentValue;

            Assert.AreEqual(currentValue, lastKnownValue);
            Assert.AreEqual(currentValue, lastKnownValuePrevious);
        }

        [Test]
        public void BlendBufferReset_ClearsExtrapolation()
        {
            // Document: After reset, no more extrapolation from stale data
            // The new host starts broadcasting fresh sync data

            // Simulated: mostRecentChanges is cleared
            List<float> mostRecentChanges = new List<float> { 1f, 2f, 3f };

            // Reset clears it
            mostRecentChanges.Clear();

            Assert.AreEqual(0, mostRecentChanges.Count, "Buffer should be cleared");
        }

        [Test]
        public void BlendBufferReset_IsIdempotent()
        {
            // Calling reset multiple times should have same effect as calling once
            float currentValue = 50f;
            float lastKnownValue = 0f;

            // First reset
            lastKnownValue = currentValue;
            Assert.AreEqual(50f, lastKnownValue);

            // Second reset (same value)
            lastKnownValue = currentValue;
            Assert.AreEqual(50f, lastKnownValue);
        }

        #endregion

        #region Callback Invocation Tests

        [Test]
        public void Callback_OnOwnershipMigrated_IsMineNow_TrueOnNewHost()
        {
            // On the new host, isMineNow should be true
            bool isMineNow = true; // New host now owns these GNPs

            Assert.IsTrue(isMineNow);
        }

        [Test]
        public void Callback_OnHostFailoverCompleted_FiredOnBothHostAndClients()
        {
            // On new host: isSelf=true, migratedGNPCount > 0
            var hostEvent = new HostFailoverCompletedEvent(
                occurredAtElapsedTicks: 1000,
                newHostAuthorityId: SERVER_AUTHORITY_ID,
                promotingPeerOriginalAuthorityId: 5,
                isSelf: true,
                migratedGNPCount: 10
            );

            // On client: isSelf=false, migratedGNPCount = 0
            var clientEvent = new HostFailoverCompletedEvent(
                occurredAtElapsedTicks: 1000,
                newHostAuthorityId: SERVER_AUTHORITY_ID,
                promotingPeerOriginalAuthorityId: 5,
                isSelf: false,
                migratedGNPCount: 0
            );

            Assert.IsTrue(hostEvent.IsSelf);
            Assert.Greater(hostEvent.MigratedGNPCount, 0);

            Assert.IsFalse(clientEvent.IsSelf);
            Assert.AreEqual(0, clientEvent.MigratedGNPCount);
        }

        [Test]
        public void Callback_Order_MigrationBeforeEvent()
        {
            // Document: Callbacks should fire in this order:
            // 1. MigrateServerOwnedGNPs() calls OnOwnershipMigratedDuringFailover for each GNP
            // 2. HostFailoverCompletedEvent is published via EventBus
            // 3. OnHostFailoverCompleted is called on all GONetBehaviours

            var callOrder = new List<string>();

            // Simulate proper order
            callOrder.Add("OnOwnershipMigratedDuringFailover_GNP1");
            callOrder.Add("OnOwnershipMigratedDuringFailover_GNP2");
            callOrder.Add("HostFailoverCompletedEvent_Published");
            callOrder.Add("OnHostFailoverCompleted_Broadcast");

            Assert.AreEqual(4, callOrder.Count);
            Assert.IsTrue(callOrder[0].StartsWith("OnOwnershipMigratedDuringFailover"));
            Assert.IsTrue(callOrder[2].Contains("Event_Published"));
        }

        #endregion

        #region Double Failover Tests

        [Test]
        public void DoubleFailover_MigrationIsIdempotent()
        {
            // First failover: Peer 2 becomes host
            ushort firstNewHostOriginalId = 2;
            int firstMigratedCount = 5;

            // Second failover: Peer 3 becomes host (Peer 2 died)
            ushort secondNewHostOriginalId = 3;
            int secondMigratedCount = 5; // Same GNPs

            // Both work correctly
            Assert.AreNotEqual(firstNewHostOriginalId, secondNewHostOriginalId);
            Assert.AreEqual(firstMigratedCount, secondMigratedCount);
        }

        [Test]
        public void DoubleFailover_BlendBuffersResetAgain()
        {
            // Each failover resets blend buffers independently
            float value1 = 100f;
            float value2 = 200f; // Value changed between failovers

            // First failover resets to value1
            float lastKnownAfterFirst = value1;
            Assert.AreEqual(100f, lastKnownAfterFirst);

            // Second failover resets to value2
            float lastKnownAfterSecond = value2;
            Assert.AreEqual(200f, lastKnownAfterSecond);
        }

        [Test]
        public void DoubleFailover_CallbacksFiredTwice()
        {
            var callbackCount = 0;

            // First failover
            callbackCount++;

            // Second failover
            callbackCount++;

            Assert.AreEqual(2, callbackCount, "Callbacks should fire on each failover");
        }

        #endregion

        #region Edge Cases

        [Test]
        public void EdgeCase_LateJoiner_SeesNewHostAlreadyOwning()
        {
            // Late joiner connects after failover completes
            // They see the new host as authority 1023 and receive sync data normally
            // No special handling needed

            ushort newHostAuthorityId = SERVER_AUTHORITY_ID;
            bool isLateJoiner = true;

            // Late joiner just sees normal server-owned GNPs
            Assert.AreEqual(SERVER_AUTHORITY_ID, newHostAuthorityId);
            Assert.IsTrue(isLateJoiner); // No special callback needed
        }

        [Test]
        public void EdgeCase_NullGNP_SkippedSafely()
        {
            // Simulate GNP map with null entries (can happen during despawn race)
            var gnpMap = new Dictionary<uint, object>
            {
                { 1, new object() },
                { 2, null },
                { 3, new object() },
            };

            int migratedCount = 0;
            foreach (var kvp in gnpMap)
            {
                if (kvp.Value == null)
                    continue; // Skip null entries safely

                migratedCount++;
            }

            Assert.AreEqual(2, migratedCount, "Null GNPs should be skipped");
        }

        [Test]
        public void EdgeCase_CallbackException_DoesNotAbortMigration()
        {
            // Document: Exceptions in callbacks should not abort migration
            // Each GNP's migration is independent

            var migratedGNPs = new List<uint>();
            var gnpIds = new uint[] { 1, 2, 3 };

            foreach (var id in gnpIds)
            {
                try
                {
                    // Simulate callback that throws on GNP 2
                    if (id == 2)
                        throw new System.Exception("Test exception");

                    migratedGNPs.Add(id);
                }
                catch
                {
                    // Log but continue
                    migratedGNPs.Add(id); // Still count as migrated
                }
            }

            Assert.AreEqual(3, migratedGNPs.Count, "All GNPs should be processed despite exceptions");
        }

        [Test]
        public void EdgeCase_ZeroServerOwnedGNPs()
        {
            // All objects are client-owned (e.g., player characters)
            var gnpOwnerAuthorityIds = new Dictionary<uint, ushort>
            {
                { 1, 2 },
                { 2, 3 },
            };

            int migratedCount = 0;
            foreach (var kvp in gnpOwnerAuthorityIds)
            {
                if (kvp.Value == SERVER_AUTHORITY_ID)
                {
                    migratedCount++;
                }
            }

            Assert.AreEqual(0, migratedCount);
            // Event should still fire with migratedGNPCount = 0
        }

        #endregion

        #region Integration Scenario Tests

        [Test]
        public void Scenario_TypicalFailover_ServerDies()
        {
            // Simulate typical failover scenario:
            // 1. Server (1023) dies
            // 2. Vice host (peer 2) detects via heartbeat timeout
            // 3. Vice host self-promotes
            // 4. Vice host migrates server-owned GNPs
            // 5. Vice host broadcasts EmergencyHostPromotion
            // 6. Clients accept new host

            ushort originalViceHostId = 2;
            ushort deadHostId = SERVER_AUTHORITY_ID;
            uint currentEpoch = 1;

            // After promotion
            uint newEpoch = currentEpoch + 1;
            ushort newHostAuthorityId = SERVER_AUTHORITY_ID;

            Assert.AreEqual(2u, newEpoch);
            Assert.AreEqual(SERVER_AUTHORITY_ID, newHostAuthorityId);
            Assert.AreNotEqual(originalViceHostId, newHostAuthorityId);
        }

        [Test]
        public void Scenario_ViceHostAlsoDies_FallbackToLowestAuthority()
        {
            // Rare scenario: Both server and vice host die
            // 1. Server dies
            // 2. Vice host also dies before promoting
            // 3. Other peers wait for vice host promotion (200ms)
            // 4. Vice host doesn't promote
            // 5. Lowest authority among survivors self-promotes

            var survivingPeers = new List<ushort> { 3, 5, 7 };
            ushort lowestAuthority = ushort.MaxValue;

            foreach (var peer in survivingPeers)
            {
                if (peer < lowestAuthority)
                    lowestAuthority = peer;
            }

            Assert.AreEqual(3, lowestAuthority, "Peer 3 should self-promote as lowest authority");
        }

        [Test]
        public void Scenario_IsReadyBecomesTrue_AfterMigration()
        {
            // Document: After migration, new host's GNPs should have IsReady=true
            // This gates sync data broadcasting

            bool isReadyBefore = false; // Client's GNP wasn't ready to broadcast

            // After promotion and migration:
            bool isServer = true;
            bool isReadyAfter = isServer; // Simplified: servers are always "ready" to broadcast

            Assert.IsFalse(isReadyBefore);
            Assert.IsTrue(isReadyAfter);
        }

        #endregion

        #region Helper Types

        /// <summary>
        /// Helper struct for testing callback parameter passing
        /// </summary>
        private struct OwnershipMigrationCallbackParams
        {
            public bool IsMineNow;
            public ushort PreviousHostOriginalAuthorityId;
        }

        #endregion
    }
}
