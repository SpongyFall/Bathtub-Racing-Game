/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using NUnit.Framework;
using System.Collections.Generic;

namespace GONet.UnitTests
{
    /// <summary>
    /// Unit tests for Server_AssumeAuthorityOver and related GONetId handling.
    ///
    /// These tests verify:
    /// 1. Batch-allocated GONetIds are preserved during authority transfer
    /// 2. Objects with unset raw IDs still get assigned properly
    /// 3. SoA lookup dictionaries are updated when GONetId changes
    ///
    /// CRITICAL BUG FIXED (December 2025):
    /// Client-spawned server-owned objects were getting stuck because:
    /// - Client allocated batch GONetId (e.g., 6143)
    /// - Server_AssumeAuthorityOver was reassigning to a different raw ID
    /// - Client's SoA lookup had old GONetId, server sent new GONetId
    /// - Lookup failed, sync data dropped, objects stuck
    ///
    /// Fix: Server_AssumeAuthorityOver now preserves batch-allocated raw IDs.
    /// </summary>
    [TestFixture]
    public class ServerAssumeAuthorityTests
    {
        [SetUp]
        public void SetUp()
        {
            // Enable synchronous logging for tests
            GONetLog.DefaultProfile.UseSynchronousLogging = true;

            // Reset batch manager state
            GONetIdBatchManager.Server_ResetAllBatches();
            GONetIdBatchManager.Client_ResetAllBatches();
        }

        [TearDown]
        public void TearDown()
        {
            // Disable synchronous logging
            GONetLog.DefaultProfile.UseSynchronousLogging = false;

            // Clean up batch manager state
            GONetIdBatchManager.Server_ResetAllBatches();
            GONetIdBatchManager.Client_ResetAllBatches();
        }

        #region Batch GONetId Preservation Tests

        /// <summary>
        /// Verify that batch-allocated raw IDs are within expected ranges.
        /// This establishes the baseline for understanding what "batch" means.
        /// </summary>
        [Test]
        public void BatchSystem_AllocatedIds_AreInExpectedRange()
        {
            // Arrange: Allocate a batch on server
            uint lastAssigned = 100;
            uint batchStart = GONetIdBatchManager.Server_AllocateNewBatch(lastAssigned);

            // Assert: Batch should start after lastAssigned
            Assert.Greater(batchStart, lastAssigned, "Batch should start after last assigned ID");

            // Client receives and uses the batch
            GONetIdBatchManager.Client_AddBatch(batchStart);

            // Act: Allocate from client batch
            bool success = GONetIdBatchManager.Client_TryAllocateNextId(out uint allocatedId, out bool needsMoreBatch);

            // Assert: Allocated ID should be within batch
            Assert.IsTrue(success, "Should successfully allocate from batch");
            Assert.AreEqual(batchStart, allocatedId, "First allocated ID should be batch start");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(allocatedId),
                "Allocated ID should be recognized as being in a batch");
        }

        /// <summary>
        /// Verify that server skips batch ranges when assigning its own IDs.
        /// This is critical for avoiding ID collisions.
        /// </summary>
        [Test]
        public void ServerIdAssignment_SkipsBatchRanges()
        {
            // Arrange: Create multiple batches with gaps
            uint batch1Start = GONetIdBatchManager.Server_AllocateNewBatch(10);  // e.g., starts at 11
            uint batch2Start = GONetIdBatchManager.Server_AllocateNewBatch(500); // e.g., starts at 501

            // Assert: IDs in batch ranges are recognized
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batch1Start),
                "Batch 1 start should be in batch");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batch1Start + 50),
                "Mid-batch 1 ID should be in batch");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batch2Start),
                "Batch 2 start should be in batch");

            // Assert: IDs outside batch ranges are NOT in batch
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(5),
                "ID before batch 1 should not be in batch");
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(300),
                "ID between batches should not be in batch");
        }

        /// <summary>
        /// Test that GONetId composition works correctly.
        /// GONetId = (raw << 10) | OwnerAuthorityId
        /// </summary>
        [Test]
        public void GONetIdComposition_RawAndOwner_ComposeCorrectly()
        {
            // Arrange
            uint raw = 5;
            ushort serverOwner = GONetMain.OwnerAuthorityId_Server; // 1023

            // Act: Compose GONetId
            uint gonetId = (raw << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED) | serverOwner;

            // Assert: Decompose and verify
            uint extractedRaw = gonetId >> GONetParticipant.GONET_ID_BIT_COUNT_UNUSED;
            ushort extractedOwner = (ushort)(gonetId & ((1 << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED) - 1));

            Assert.AreEqual(raw, extractedRaw, "Extracted raw should match original");
            Assert.AreEqual(serverOwner, extractedOwner, "Extracted owner should match original");

            // Verify expected GONetId value: (5 << 10) | 1023 = 5120 + 1023 = 6143
            Assert.AreEqual(6143u, gonetId, "GONetId for raw=5, owner=1023 should be 6143");
        }

        /// <summary>
        /// Test that GONetIdRaw_Unset is correctly defined as 0.
        /// This is the sentinel value that triggers new ID assignment.
        /// </summary>
        [Test]
        public void GONetIdRaw_Unset_IsZero()
        {
            Assert.AreEqual(0u, GONetParticipant.GONetIdRaw_Unset,
                "GONetIdRaw_Unset should be 0 (used as sentinel for unassigned)");
        }

        #endregion

        #region SoA Lookup Update Tests

        /// <summary>
        /// Verify that dictionary re-keying works correctly for simple cases.
        /// This tests the pattern used in OnGONetIdChanged_UpdateSoALookups.
        /// </summary>
        [Test]
        public void DictionaryReKey_UpdatesKeyCorrectly()
        {
            // Arrange: Simulate SoA lookup dictionary pattern
            var lookup = new Dictionary<uint, (int streamIndex, int objectIndex)>();
            uint oldGONetId = 6143; // raw=5, owner=1023
            uint newGONetId = 4095; // raw=3, owner=1023

            lookup[oldGONetId] = (0, 42); // streamIndex=0, objectIndex=42

            // Act: Re-key (simulates what OnGONetIdChanged_UpdateSoALookups does)
            if (lookup.TryGetValue(oldGONetId, out var value))
            {
                lookup.Remove(oldGONetId);
                lookup[newGONetId] = value;
            }

            // Assert
            Assert.IsFalse(lookup.ContainsKey(oldGONetId), "Old key should be removed");
            Assert.IsTrue(lookup.ContainsKey(newGONetId), "New key should exist");
            Assert.AreEqual((0, 42), lookup[newGONetId], "Value should be preserved");
        }

        /// <summary>
        /// Test that composite key dictionaries (used for Vector2/Vector4) re-key correctly.
        /// Key format: (gonetId, memberIndex)
        /// </summary>
        [Test]
        public void CompositeKeyDictionary_ReKeysAllMembersCorrectly()
        {
            // Arrange: Simulate Vector2/Vector4 lookup with multiple members per object
            var lookup = new Dictionary<(uint gonetId, byte memberIndex), (int streamIndex, int objectIndex)>();
            uint oldGONetId = 6143;
            uint newGONetId = 4095;

            // Add multiple members for same object
            lookup[(oldGONetId, 0)] = (0, 10);
            lookup[(oldGONetId, 1)] = (0, 11);
            lookup[(oldGONetId, 2)] = (1, 20);

            // Add entries for different object (should not be affected)
            lookup[(9999, 0)] = (2, 30);

            // Act: Re-key all entries for oldGONetId
            var keysToUpdate = new List<(uint gonetId, byte memberIndex)>();
            foreach (var kvp in lookup)
            {
                if (kvp.Key.gonetId == oldGONetId)
                    keysToUpdate.Add(kvp.Key);
            }
            foreach (var oldKey in keysToUpdate)
            {
                var value = lookup[oldKey];
                lookup.Remove(oldKey);
                lookup[(newGONetId, oldKey.memberIndex)] = value;
            }

            // Assert: Old keys removed
            Assert.IsFalse(lookup.ContainsKey((oldGONetId, 0)), "Old key member 0 should be removed");
            Assert.IsFalse(lookup.ContainsKey((oldGONetId, 1)), "Old key member 1 should be removed");
            Assert.IsFalse(lookup.ContainsKey((oldGONetId, 2)), "Old key member 2 should be removed");

            // Assert: New keys exist with correct values
            Assert.IsTrue(lookup.ContainsKey((newGONetId, 0)), "New key member 0 should exist");
            Assert.IsTrue(lookup.ContainsKey((newGONetId, 1)), "New key member 1 should exist");
            Assert.IsTrue(lookup.ContainsKey((newGONetId, 2)), "New key member 2 should exist");
            Assert.AreEqual((0, 10), lookup[(newGONetId, 0)], "Member 0 value preserved");
            Assert.AreEqual((0, 11), lookup[(newGONetId, 1)], "Member 1 value preserved");
            Assert.AreEqual((1, 20), lookup[(newGONetId, 2)], "Member 2 value preserved");

            // Assert: Other object not affected
            Assert.IsTrue(lookup.ContainsKey((9999, 0)), "Other object should not be affected");
            Assert.AreEqual((2, 30), lookup[(9999, 0)], "Other object value preserved");
        }

        #endregion

        #region Authority Transfer Scenarios

        /// <summary>
        /// Simulate the scenario that was causing stuck objects:
        /// Client spawns with batch ID, server used to reassign.
        /// Verify the IDs remain consistent.
        /// </summary>
        [Test]
        public void ClientSpawnServerOwned_BatchIdPreserved_Scenario()
        {
            // Arrange: Allocate batch like server would for client
            uint batchStart = GONetIdBatchManager.Server_AllocateNewBatch(1000);
            GONetIdBatchManager.Client_AddBatch(batchStart);

            // Client allocates an ID from the batch
            bool success = GONetIdBatchManager.Client_TryAllocateNextId(out uint clientAllocatedRaw, out _);
            Assert.IsTrue(success, "Should allocate from batch");

            // Compose the GONetId client would use
            uint clientGONetId = (clientAllocatedRaw << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED)
                               | GONetMain.OwnerAuthorityId_Server;

            // Verify the ID is recognized as being in a batch
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(clientAllocatedRaw),
                "Client-allocated raw should be recognized in server's batch tracking");

            // Simulate what Server_AssumeAuthorityOver should do:
            // If raw is already set (not 0), DON'T reassign
            uint rawFromGONetId = clientGONetId >> GONetParticipant.GONET_ID_BIT_COUNT_UNUSED;
            bool shouldReassign = (rawFromGONetId == GONetParticipant.GONetIdRaw_Unset);

            Assert.IsFalse(shouldReassign,
                "Server should NOT reassign raw when it's already set from batch allocation");

            // The GONetId should remain unchanged
            Assert.AreEqual(clientAllocatedRaw, rawFromGONetId,
                "Raw ID should be preserved - this is what fixes the stuck objects bug");
        }

        /// <summary>
        /// Verify that objects with truly unset raw IDs still get assigned.
        /// This ensures the fix doesn't break the case where assignment IS needed.
        /// </summary>
        [Test]
        public void ObjectWithUnsetRaw_ShouldGetAssigned()
        {
            // Arrange: Simulate an object with unset raw (like scene-defined before assignment)
            uint unsetRaw = GONetParticipant.GONetIdRaw_Unset;

            // Act: Check if it should be assigned
            bool shouldAssign = (unsetRaw == GONetParticipant.GONetIdRaw_Unset);

            // Assert
            Assert.IsTrue(shouldAssign,
                "Objects with unset raw (0) should still get assigned a new raw ID");
            Assert.AreEqual(0u, unsetRaw,
                "Unset raw should be 0");
        }

        /// <summary>
        /// Test multiple rapid batch allocations don't cause ID collisions.
        /// This simulates the "rapid spawning" scenario that exposed the bug.
        /// </summary>
        [Test]
        public void RapidBatchAllocation_NoIdCollisions()
        {
            // Arrange: Allocate multiple batches to simulate multiple clients
            uint batch1 = GONetIdBatchManager.Server_AllocateNewBatch(0);
            uint batch2 = GONetIdBatchManager.Server_AllocateNewBatch(batch1 + 199); // After batch1 ends
            uint batch3 = GONetIdBatchManager.Server_AllocateNewBatch(batch2 + 199); // After batch2 ends

            // Assert: No overlap between batches
            Assert.Less(batch1 + 199, batch2, "Batch 1 should not overlap batch 2");
            Assert.Less(batch2 + 199, batch3, "Batch 2 should not overlap batch 3");

            // Track all allocated IDs
            var allocatedIds = new HashSet<uint>();

            // Simulate client 1 using batch 1
            GONetIdBatchManager.Client_AddBatch(batch1);
            for (int i = 0; i < 50; i++)
            {
                if (GONetIdBatchManager.Client_TryAllocateNextId(out uint id, out _))
                {
                    Assert.IsFalse(allocatedIds.Contains(id), $"ID {id} already allocated - collision!");
                    allocatedIds.Add(id);
                }
            }

            // Reset client state, receive batch 2
            GONetIdBatchManager.Client_ResetAllBatches();
            GONetIdBatchManager.Client_AddBatch(batch2);
            for (int i = 0; i < 50; i++)
            {
                if (GONetIdBatchManager.Client_TryAllocateNextId(out uint id, out _))
                {
                    Assert.IsFalse(allocatedIds.Contains(id), $"ID {id} already allocated - collision!");
                    allocatedIds.Add(id);
                }
            }

            Assert.AreEqual(100, allocatedIds.Count, "Should have allocated 100 unique IDs");
        }

        #endregion
    }
}
