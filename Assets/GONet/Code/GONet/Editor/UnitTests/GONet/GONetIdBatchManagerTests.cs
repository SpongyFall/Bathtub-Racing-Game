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

using NUnit.Framework;
using UnityEngine;

namespace GONet
{
    /// <summary>
    /// Unit tests for GONetIdBatchManager - the production-grade batch allocation system
    /// for client-spawned, server-controlled objects.
    ///
    /// These tests ensure bulletproof operation across:
    /// - Sequential ID allocation
    /// - Batch exhaustion and removal
    /// - Scene change state resets
    /// - Low batch threshold triggering
    /// - Validation and error handling
    /// </summary>
    [TestFixture]
    public class GONetIdBatchManagerTests
    {
        [SetUp]
        public void Setup()
        {
            // Enable synchronous logging for tests (required for LogAssert.Expect)
            GONetLog.DefaultProfile.UseSynchronousLogging = true;

            // Reset all state before each test
            GONetIdBatchManager.Server_ResetAllBatches();
            GONetIdBatchManager.Client_ResetAllBatches();
        }

        [TearDown]
        public void TearDown()
        {
            // Disable synchronous logging for tests
            GONetLog.DefaultProfile.UseSynchronousLogging = false;

            // Clean up after each test
            GONetIdBatchManager.Server_ResetAllBatches();
            GONetIdBatchManager.Client_ResetAllBatches();
        }

        #region Server Tests

        [Test]
        public void Server_AllocateNewBatch_ReturnsSequentialBatches()
        {
            // Arrange
            uint lastId = 1000;

            // Act (batch size is 200)
            uint batch1 = GONetIdBatchManager.Server_AllocateNewBatch(lastId);
            uint batch2 = GONetIdBatchManager.Server_AllocateNewBatch(batch1 + 199);

            // Assert
            Assert.AreEqual(1001, batch1, "First batch should start at lastId + 1");
            Assert.AreEqual(1201, batch2, "Second batch should start at previous batch end + 1");
        }

        [Test]
        public void Server_IsIdInAnyBatch_ChecksSingleBatch()
        {
            // Arrange (batch size is 200)
            uint batchStart = GONetIdBatchManager.Server_AllocateNewBatch(1000);

            // Assert - IDs within batch
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batchStart), "Batch start should be in batch");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batchStart + 100), "Middle of batch should be in batch");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batchStart + 199), "Batch end should be in batch");

            // Assert - IDs outside batch
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(batchStart - 1), "ID before batch should not be in batch");
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(batchStart + 200), "ID after batch should not be in batch");
        }

        [Test]
        public void Server_IsIdInAnyBatch_ChecksMultipleBatches()
        {
            // Arrange
            uint batch1 = GONetIdBatchManager.Server_AllocateNewBatch(1000);
            uint batch2 = GONetIdBatchManager.Server_AllocateNewBatch(2000);
            uint batch3 = GONetIdBatchManager.Server_AllocateNewBatch(3000);

            // Assert - all batches recognized
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batch1 + 10), "ID in first batch");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batch2 + 10), "ID in second batch");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batch3 + 10), "ID in third batch");

            // Assert - gaps between batches not recognized
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(1500), "ID between batches should not be in any batch");
        }

        [Test]
        public void Server_ReleaseBatch_RemovesBatchFromTracking()
        {
            // Arrange
            uint batchStart = GONetIdBatchManager.Server_AllocateNewBatch(1000);
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batchStart + 10), "Batch should exist before release");

            // Act
            GONetIdBatchManager.Server_ReleaseBatch(batchStart);

            // Assert
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(batchStart + 10), "Batch should not exist after release");
        }

        [Test]
        public void Server_ResetAllBatches_ClearsAllAllocations()
        {
            // Arrange
            uint batch1 = GONetIdBatchManager.Server_AllocateNewBatch(1000);
            uint batch2 = GONetIdBatchManager.Server_AllocateNewBatch(2000);

            // Act
            GONetIdBatchManager.Server_ResetAllBatches();

            // Assert
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(batch1 + 10), "First batch should be cleared");
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(batch2 + 10), "Second batch should be cleared");
        }

        #endregion

        #region Client Tests

        [Test]
        public void Client_AddBatch_StoresBatchCorrectly()
        {
            // Act
            GONetIdBatchManager.Client_AddBatch(5000);

            // Assert (batch size is 200, so range is [5000-5199])
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInActiveBatch(5000), "Batch start should be in active batch");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInActiveBatch(5199), "Batch end should be in active batch");
        }

        [Test]
        public void Client_AddBatch_IgnoresDuplicates()
        {
            // Arrange
            GONetIdBatchManager.Client_AddBatch(5000);

            // Act - add duplicate
            GONetIdBatchManager.Client_AddBatch(5000);

            // Assert - should still work correctly (no crash, consistent state)
            uint id;
            bool shouldRequest;
            bool success = GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
            Assert.IsTrue(success, "Should still allocate after duplicate add");
            Assert.AreEqual(5000, id, "Should allocate from first (not duplicated) batch");
        }

        [Test]
        public void Client_TryAllocateNextId_ReturnsSequentialIds()
        {
            // Arrange
            GONetIdBatchManager.Client_AddBatch(5000);

            // Act
            uint id1, id2, id3;
            bool shouldRequest1, shouldRequest2, shouldRequest3;
            bool success1 = GONetIdBatchManager.Client_TryAllocateNextId(out id1, out shouldRequest1);
            bool success2 = GONetIdBatchManager.Client_TryAllocateNextId(out id2, out shouldRequest2);
            bool success3 = GONetIdBatchManager.Client_TryAllocateNextId(out id3, out shouldRequest3);

            // Assert
            Assert.IsTrue(success1, "First allocation should succeed");
            Assert.IsTrue(success2, "Second allocation should succeed");
            Assert.IsTrue(success3, "Third allocation should succeed");
            Assert.AreEqual(5000, id1, "First ID should be batch start");
            Assert.AreEqual(5001, id2, "Second ID should be batch start + 1");
            Assert.AreEqual(5002, id3, "Third ID should be batch start + 2");
            Assert.IsFalse(shouldRequest1, "Should not request new batch yet");
            Assert.IsFalse(shouldRequest2, "Should not request new batch yet");
            Assert.IsFalse(shouldRequest3, "Should not request new batch yet");
        }

        [Test]
        public void Client_TryAllocateNextId_RequestsNewBatchWhenLow()
        {
            // Arrange (batch size is 200, threshold is 50% = 100 remaining)
            GONetIdBatchManager.Client_AddBatch(5000);

            // Consume 200 IDs (200 - 100 = 100 remaining, at threshold)
            for (int i = 0; i < 100; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
                Assert.IsFalse(shouldRequest, $"Should not request at ID {i} (still have {200 - i} remaining)");
            }

            // Act - 101st allocation should trigger request (99 remaining, below threshold of 100)
            uint triggerId;
            bool shouldRequestNow;
            bool success = GONetIdBatchManager.Client_TryAllocateNextId(out triggerId, out shouldRequestNow);

            // Assert
            Assert.IsTrue(success, "Allocation should succeed");
            Assert.AreEqual(5100, triggerId, "Should allocate correct ID");
            Assert.IsTrue(shouldRequestNow, "Should request new batch when remaining drops below 100");
        }

        [Test]
        public void Client_TryAllocateNextId_FailsWhenNoBatchesAvailable()
        {
            // Arrange - no batches added
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(".*CLIENT has NO available batch IDs.*"));

            // Act
            uint id;
            bool shouldRequest;
            bool success = GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);

            // Assert
            Assert.IsFalse(success, "Should fail when no batches available");
            Assert.AreEqual(GONetParticipant.GONetIdRaw_Unset, id, "Should return unset ID on failure");
        }

        [Test]
        public void Client_TryAllocateNextId_RemovesExhaustedBatch()
        {
            // Arrange (batch size is 200)
            GONetIdBatchManager.Client_AddBatch(5000);

            // Act - exhaust entire batch (200 IDs)
            for (int i = 0; i < 200; i++)
            {
                uint id;
                bool shouldRequest;
                bool success = GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
                Assert.IsTrue(success, $"Allocation {i} should succeed");
                Assert.AreEqual(5000 + (uint)i, id, $"ID {i} should match expected value");
            }

            // Assert - batch should be exhausted and removed
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(".*CLIENT has NO available batch IDs.*"));
            uint nextId;
            bool shouldRequestNext;
            bool successNext = GONetIdBatchManager.Client_TryAllocateNextId(out nextId, out shouldRequestNext);
            Assert.IsFalse(successNext, "Should fail after batch exhausted");
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInActiveBatch(5100), "Exhausted batch should be removed");
        }

        [Test]
        public void Client_TryAllocateNextId_WorksAcrossMultipleBatches()
        {
            // Arrange - add 3 batches (batch size is 200)
            GONetIdBatchManager.Client_AddBatch(5000);
            GONetIdBatchManager.Client_AddBatch(6000);
            GONetIdBatchManager.Client_AddBatch(7000);

            // Act - exhaust first batch (200 IDs)
            for (int i = 0; i < 200; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
            }

            // Get first ID from second batch
            uint nextId;
            bool shouldRequestNext;
            bool success = GONetIdBatchManager.Client_TryAllocateNextId(out nextId, out shouldRequestNext);

            // Assert
            Assert.IsTrue(success, "Should allocate from second batch");
            Assert.AreEqual(6000, nextId, "Should start from second batch");
        }

        [Test]
        public void Client_IsIdInActiveBatch_ValidatesCorrectly()
        {
            // Arrange (batch size is 200)
            GONetIdBatchManager.Client_AddBatch(5000);

            // Assert - within batch
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInActiveBatch(5000), "Batch start");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInActiveBatch(5100), "Batch middle");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInActiveBatch(5199), "Batch end");

            // Assert - outside batch
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInActiveBatch(4999), "Before batch");
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInActiveBatch(5200), "After batch");
        }

        [Test]
        public void Client_ResetAllBatches_ClearsAllState()
        {
            // Arrange
            GONetIdBatchManager.Client_AddBatch(5000);
            GONetIdBatchManager.Client_AddBatch(6000);

            // Allocate some IDs
            uint id1, id2;
            bool shouldRequest1, shouldRequest2;
            GONetIdBatchManager.Client_TryAllocateNextId(out id1, out shouldRequest1);
            GONetIdBatchManager.Client_TryAllocateNextId(out id2, out shouldRequest2);

            // Act
            GONetIdBatchManager.Client_ResetAllBatches();

            // Assert
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(".*CLIENT has NO available batch IDs.*"));
            uint nextId;
            bool shouldRequestNext;
            bool success = GONetIdBatchManager.Client_TryAllocateNextId(out nextId, out shouldRequestNext);
            Assert.IsFalse(success, "Should have no batches after reset");
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInActiveBatch(5000), "First batch should be cleared");
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInActiveBatch(6000), "Second batch should be cleared");
        }

        [Test]
        public void Client_GetDiagnostics_ReturnsAccurateInfo()
        {
            // Arrange
            GONetIdBatchManager.Client_AddBatch(5000);
            GONetIdBatchManager.Client_AddBatch(6000);

            // Allocate 10 IDs
            for (int i = 0; i < 10; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
            }

            // Act
            string diagnostics = GONetIdBatchManager.Client_GetDiagnostics();

            // Assert
            Assert.IsTrue(diagnostics.Contains("Batches: 2"), "Should show 2 batches");
            Assert.IsTrue(diagnostics.Contains("Allocated: 400"), "Should show 400 total IDs");
            Assert.IsTrue(diagnostics.Contains("Used: 10"), "Should show 10 used IDs");
            Assert.IsTrue(diagnostics.Contains("Remaining: 390"), "Should show 390 remaining IDs");
        }

        #endregion

        #region Integration & Scene Change Tests

        [Test]
        public void SceneChange_Simulation_ServerResetsClientPreserves()
        {
            // Arrange - simulate first scene
            uint serverBatch1 = GONetIdBatchManager.Server_AllocateNewBatch(1000);
            GONetIdBatchManager.Client_AddBatch(serverBatch1);

            uint id1;
            bool shouldRequest1;
            GONetIdBatchManager.Client_TryAllocateNextId(out id1, out shouldRequest1);
            Assert.AreEqual(serverBatch1, id1, "Should allocate from first batch");

            // Act - simulate scene change
            // SERVER resets batches (to reclaim ID space)
            // CLIENT does NOT reset (batches are global, persist across scenes)
            GONetIdBatchManager.Server_ResetAllBatches();
            // GONetIdBatchManager.Client_ResetAllBatches(); // NO LONGER CALLED!

            // Arrange - server allocates new batch for second scene
            uint serverBatch2 = GONetIdBatchManager.Server_AllocateNewBatch(2000);
            // Client does NOT receive new batch - continues using existing batch

            // Client continues allocating from existing batch
            uint id2;
            bool shouldRequest2;
            bool success = GONetIdBatchManager.Client_TryAllocateNextId(out id2, out shouldRequest2);

            // Assert
            Assert.IsTrue(success, "Should allocate successfully in new scene from existing batch");
            Assert.AreEqual(serverBatch1 + 1, id2, "Should continue from existing batch (1001 + 1 = 1002)");
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(serverBatch1), "Old server batch should be cleared");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInActiveBatch(serverBatch1), "Client batch should PERSIST across scene change");
        }

        [Test]
        public void MultipleSceneChanges_ClientBatchPersists()
        {
            // Simulate multiple scene changes - client should keep initial batch
            // (matching the user's test scenario where scene changes occurred)

            // Arrange - client receives initial batch
            uint initialBatch = GONetIdBatchManager.Server_AllocateNewBatch(5000);
            GONetIdBatchManager.Client_AddBatch(initialBatch);

            // Allocate 10 IDs in first scene
            for (int spawn = 0; spawn < 10; spawn++)
            {
                uint id;
                bool shouldRequest;
                bool success = GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
                Assert.IsTrue(success, $"Initial scene, spawn {spawn} should succeed");
                Assert.AreEqual(initialBatch + (uint)spawn, id, $"ID {spawn} should be sequential from initial batch");
            }

            // Simulate 5 scene changes
            for (int scene = 1; scene <= 5; scene++)
            {
                // Server resets (reclaims ID space)
                GONetIdBatchManager.Server_ResetAllBatches();

                // Client does NOT reset - batches persist!
                // GONetIdBatchManager.Client_ResetAllBatches(); // NO LONGER CALLED!

                // Server allocates new batch for tracking (but client doesn't need it yet)
                uint serverBatch = GONetIdBatchManager.Server_AllocateNewBatch((uint)(scene * 10000));

                // Client continues using initial batch
                for (int spawn = 0; spawn < 10; spawn++)
                {
                    uint id;
                    bool shouldRequest;
                    bool success = GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
                    Assert.IsTrue(success, $"Scene {scene}, spawn {spawn} should succeed from persisted batch");

                    // Should continue from where we left off in initial batch
                    uint expectedId = initialBatch + 10 + (uint)((scene - 1) * 10) + (uint)spawn;
                    Assert.AreEqual(expectedId, id, $"Scene {scene}, spawn {spawn} should continue from initial batch");
                }
            }

            // After 6 scene changes (1 initial + 5 more), verify batch state
            string diagnostics = GONetIdBatchManager.Client_GetDiagnostics();
            Assert.IsTrue(diagnostics.Contains("Batches: 1"), "Should still have only the initial batch");
            Assert.IsTrue(diagnostics.Contains("Used: 60"), "Should have used 60 IDs (10 per scene × 6 scenes)");
            Assert.IsTrue(diagnostics.Contains("Remaining: 140"), "Should have 140 IDs remaining in initial batch");

            // Verify initial batch is still active
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInActiveBatch(initialBatch + 50),
                "Initial batch should still be active after multiple scene changes");
        }

        [Test]
        public void ValidateBatchIntegrity_DetectsValidState()
        {
            // Arrange
            GONetIdBatchManager.Client_AddBatch(5000);
            GONetIdBatchManager.Client_AddBatch(6000);

            // Allocate some IDs
            for (int i = 0; i < 10; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
            }

            // Act
            string errorMessage;
            bool isValid = GONetIdBatchManager.ValidateBatchIntegrity(out errorMessage);

            // Assert
            Assert.IsTrue(isValid, "Batch integrity should be valid");
            Assert.IsNull(errorMessage, "Should have no error message when valid");
        }

        #endregion

        #region Batch Exhaustion and Request Tests

        [Test]
        public void Client_ExhaustSingleBatch_TriggersRequestAtThreshold()
        {
            // Arrange
            GONetIdBatchManager.Client_AddBatch(5000);

            // Act - consume until we hit the low threshold (< 100 remaining)
            // 200 IDs in batch, so allocate 101 to get to 99 remaining (triggers at 50% = 100 remaining)
            bool shouldRequestBatch = false;
            for (int i = 0; i < 101; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
                if (shouldRequest)
                {
                    shouldRequestBatch = true;
                }
            }

            // Assert
            Assert.IsTrue(shouldRequestBatch, "Should have triggered batch request when dropping below 20 IDs");
        }

        [Test]
        public void Client_CompletelyExhaustBatch_FailsNextAllocation()
        {
            // Arrange
            GONetIdBatchManager.Client_AddBatch(5000);

            // Act - exhaust all 200 IDs
            for (int i = 0; i < 200; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
            }

            // Try to allocate one more (should fail)
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(".*CLIENT has NO available batch IDs.*"));
            uint failedId;
            bool shouldRequestNow;
            bool success = GONetIdBatchManager.Client_TryAllocateNextId(out failedId, out shouldRequestNow);

            // Assert
            Assert.IsFalse(success, "Should fail when all batches exhausted");
            Assert.AreEqual(GONetParticipant.GONetIdRaw_Unset, failedId, "Should return unset ID on failure");
        }

        [Test]
        public void Client_ReceiveNewBatch_WhileExistingBatchActive()
        {
            // Arrange - simulate client getting initial batch
            GONetIdBatchManager.Client_AddBatch(5000);

            // Act - consume 85 IDs (leaving 15, which triggers request at 81)
            for (int i = 0; i < 85; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
            }

            // Simulate server sending new batch before old one exhausted
            GONetIdBatchManager.Client_AddBatch(6000);

            // Continue allocating - should use remaining from first batch, then second
            uint id1, id2, id3;
            bool req1, req2, req3;
            GONetIdBatchManager.Client_TryAllocateNextId(out id1, out req1); // Should get 5085 (from first batch)

            // Exhaust rest of first batch (115 remaining -> 114 allocations to exhaust)
            for (int i = 0; i < 114; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
            }

            GONetIdBatchManager.Client_TryAllocateNextId(out id2, out req2); // Should get 6000 (from second batch)
            GONetIdBatchManager.Client_TryAllocateNextId(out id3, out req3); // Should get 6001 (from second batch)

            // Assert
            Assert.AreEqual(5085, id1, "Should allocate from first batch first");
            Assert.AreEqual(6000, id2, "Should switch to second batch after first exhausted");
            Assert.AreEqual(6001, id3, "Should continue from second batch");
        }

        [Test]
        public void Client_ManualReset_ClearsAndReallocates()
        {
            // This tests MANUAL reset (e.g., disconnect/reconnect), NOT automatic scene change behavior
            // NOTE: Scene changes do NOT automatically reset client batches (they persist)

            // Arrange
            GONetIdBatchManager.Client_AddBatch(5000);

            // Allocate some IDs
            for (int i = 0; i < 50; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
            }

            // Act - MANUAL reset (e.g., client disconnect/reconnect scenario)
            GONetIdBatchManager.Client_ResetAllBatches();

            // Simulate receiving new batch after reset
            GONetIdBatchManager.Client_AddBatch(7000);

            uint firstIdAfterReset;
            bool shouldReq;
            bool success = GONetIdBatchManager.Client_TryAllocateNextId(out firstIdAfterReset, out shouldReq);

            // Assert
            Assert.IsTrue(success, "Should successfully allocate after manual reset");
            Assert.AreEqual(7000, firstIdAfterReset, "Should allocate from new batch, not old one");
        }

        [Test]
        public void Client_ExhaustMultipleBatches_InSequence()
        {
            // Arrange - add 3 batches
            GONetIdBatchManager.Client_AddBatch(5000); // 200 IDs
            GONetIdBatchManager.Client_AddBatch(6000); // 200 IDs
            GONetIdBatchManager.Client_AddBatch(7000); // 200 IDs
            // Total: 600 IDs available

            // Act - exhaust all 600 IDs
            uint lastId = 0;
            for (int i = 0; i < 600; i++)
            {
                uint id;
                bool shouldRequest;
                bool success = GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
                Assert.IsTrue(success, $"Allocation {i} should succeed");
                lastId = id;
            }

            // Try one more (should fail)
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(".*CLIENT has NO available batch IDs.*"));
            uint failedId;
            bool shouldReqNow;
            bool finalSuccess = GONetIdBatchManager.Client_TryAllocateNextId(out failedId, out shouldReqNow);

            // Assert
            Assert.AreEqual(7199, lastId, "Last successful ID should be end of third batch");
            Assert.IsFalse(finalSuccess, "Should fail after all batches exhausted");
        }

        [Test]
        public void Client_LowThreshold_TriggersOnlyOnce()
        {
            // Arrange
            GONetIdBatchManager.Client_AddBatch(5000);

            // Act - consume to exactly threshold (101 allocated = 99 remaining)
            int requestCount = 0;
            for (int i = 0; i < 105; i++) // Allocate 105 to be well past threshold
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
                if (shouldRequest)
                {
                    requestCount++;
                }
            }

            // Assert - should only request once when crossing threshold
            Assert.AreEqual(1, requestCount, "Should only trigger request once when crossing threshold");
        }

        #endregion

        #region Regression Tests

        [Test]
        public void Client_AllocateExactly100Ids_ExhaustsOneBatchCorrectly()
        {
            // This test catches the double-counting bug where allocating 200 IDs
            // would incorrectly report batch exhausted at ID 199 instead of 200

            // Arrange
            GONetIdBatchManager.Client_AddBatch(5000);

            // Act - allocate exactly 200 IDs (full batch)
            uint[] allocatedIds = new uint[200];
            bool[] shouldRequestFlags = new bool[200];

            for (int i = 0; i < 200; i++)
            {
                bool success = GONetIdBatchManager.Client_TryAllocateNextId(out allocatedIds[i], out shouldRequestFlags[i]);
                Assert.IsTrue(success, $"Allocation {i} should succeed (ID {allocatedIds[i]})");
            }

            // Assert - all 200 IDs should be sequential from the batch
            Assert.AreEqual(5000, allocatedIds[0], "First ID should be start of batch");
            Assert.AreEqual(5199, allocatedIds[199], "Last ID should be end of batch");

            // Verify all IDs are unique and sequential
            for (int i = 0; i < 199; i++)
            {
                Assert.AreEqual(allocatedIds[i] + 1, allocatedIds[i + 1],
                    $"IDs should be sequential: {allocatedIds[i]} -> {allocatedIds[i + 1]}");
            }

            // Next allocation should fail (batch exhausted)
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex(".*CLIENT has NO available batch IDs.*"));
            uint failId;
            bool shouldReq;
            bool nextSuccess = GONetIdBatchManager.Client_TryAllocateNextId(out failId, out shouldReq);
            Assert.IsFalse(nextSuccess, "101st allocation should fail - batch exhausted");
        }

        [Test]
        public void Client_AllocateThenCheckRemaining_CountsCorrectly()
        {
            // This test verifies the batch manager's internal counters are accurate
            // Previously: allocate + retrieve = double count, counters off by 1 each time

            // Arrange
            GONetIdBatchManager.Client_AddBatch(5000);
            string initialDiagnostics = GONetIdBatchManager.Client_GetDiagnostics();

            // Should show: Allocated=100, Used=0, Remaining=100
            Assert.IsTrue(initialDiagnostics.Contains("Allocated: 200"), "Should have 100 allocated");
            Assert.IsTrue(initialDiagnostics.Contains("Used: 0"), "Should have 0 used initially");
            Assert.IsTrue(initialDiagnostics.Contains("Remaining: 200"), "Should have 100 remaining");

            // Act - allocate 10 IDs
            for (int i = 0; i < 10; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
            }

            // Assert - diagnostics should show correct counts
            string afterDiagnostics = GONetIdBatchManager.Client_GetDiagnostics();
            Assert.IsTrue(afterDiagnostics.Contains("Allocated: 200"), "Should still have 200 allocated");
            Assert.IsTrue(afterDiagnostics.Contains("Used: 10"), "Should have 10 used");
            Assert.IsTrue(afterDiagnostics.Contains("Remaining: 190"), "Should have 190 remaining");

            // Act - allocate 190 more (total 200)
            for (int i = 0; i < 190; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
            }

            // Assert - all IDs consumed
            string finalDiagnostics = GONetIdBatchManager.Client_GetDiagnostics();
            Assert.IsTrue(finalDiagnostics.Contains("Allocated: 200"), "Should still have 200 allocated");
            Assert.IsTrue(finalDiagnostics.Contains("Used: 200"), "Should have 200 used");
            Assert.IsTrue(finalDiagnostics.Contains("Remaining: 0"), "Should have 0 remaining");
        }

        [Test]
        public void Client_SceneChange_PreservesBatchesAcrossScenes()
        {
            // REGRESSION TEST: Clients must NOT clear batches on scene change
            // Bug: Client received batch [4-103], scene changed, batch was cleared,
            //      spawns failed with "NO available batch IDs", fell back to regular
            //      ID assignment causing GONetId collisions (e.g., 4095 collision)
            //
            // Expected: Client batches persist across scenes since batch IDs are
            //           global and not scene-specific

            // Arrange - simulate client receiving initial batch in scene 1
            GONetIdBatchManager.Client_AddBatch(6000); // Batch [6000-6199]

            // Allocate some IDs in scene 1
            uint id1, id2, id3;
            bool req1, req2, req3;
            bool success1 = GONetIdBatchManager.Client_TryAllocateNextId(out id1, out req1);
            bool success2 = GONetIdBatchManager.Client_TryAllocateNextId(out id2, out req2);
            bool success3 = GONetIdBatchManager.Client_TryAllocateNextId(out id3, out req3);

            Assert.IsTrue(success1 && success2 && success3, "Should allocate successfully in scene 1");
            Assert.AreEqual(6000, id1, "First ID should be 6000");
            Assert.AreEqual(6001, id2, "Second ID should be 6001");
            Assert.AreEqual(6002, id3, "Third ID should be 6002");

            // Act - simulate scene change (DO NOT reset client batches)
            // NOTE: In the bug, Client_ResetAllBatches() was called here, which was wrong!
            // Now we verify that NOT calling it preserves the batch correctly

            // In scene 2, try to allocate more IDs from the SAME batch
            uint id4, id5;
            bool req4, req5;
            bool success4 = GONetIdBatchManager.Client_TryAllocateNextId(out id4, out req4);
            bool success5 = GONetIdBatchManager.Client_TryAllocateNextId(out id5, out req5);

            // Assert - should continue allocating from existing batch
            Assert.IsTrue(success4, "Should allocate successfully in scene 2 from existing batch");
            Assert.IsTrue(success5, "Should continue allocating in scene 2");
            Assert.AreEqual(6003, id4, "Fourth ID should be 6003 (continuing from scene 1)");
            Assert.AreEqual(6004, id5, "Fifth ID should be 6004 (sequential)");

            // Verify batch is still active
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInActiveBatch(6050),
                "Batch should still be active after scene change");

            // Verify diagnostics show batch persisted
            string diagnostics = GONetIdBatchManager.Client_GetDiagnostics();
            Assert.IsTrue(diagnostics.Contains("Batches: 1"), "Should still have 1 batch");
            Assert.IsTrue(diagnostics.Contains("Used: 5"), "Should have 5 used IDs (3 from scene 1 + 2 from scene 2)");
            Assert.IsTrue(diagnostics.Contains("Remaining: 195"), "Should have 195 remaining IDs");
        }

        [Test]
        public void ServerBatchPersistence_PreventsLateJoinerOverlaps()
        {
            // CRITICAL REGRESSION TEST (October 2025):
            // Server MUST persist batch tracking across scene changes to prevent late-joining
            // clients from receiving overlapping batches.
            //
            // BUG SCENARIO (from real 5-client test):
            // 1. Scene 1: Server allocates [604-803] to Client 2, Client 2 keeps it
            // 2. Scene changes, server RESETS batch tracking (forgets [604-803])
            // 3. Client 2 still has [604-803] (batches persist on client side)
            // 4. Client 3 joins late (after scene change)
            // 5. Server allocates [704-903] to Client 3 (overlaps with [604-803]!)
            // 6. Both Client 2 and Client 3 allocate raw ID 704
            // 7. Same GONetId (721919) used by beacon AND cannonball
            // 8. Cannonball despawn message despawns beacon instead → zombie object
            //
            // FIX: Server batch tracking persists across scenes (symmetric with client behavior)

            // Arrange - Scene 1: Multiple clients connect
            uint batch1_Client1 = GONetIdBatchManager.Server_AllocateNewBatch(1000); // [1001-1200]
            uint batch2_Client2 = GONetIdBatchManager.Server_AllocateNewBatch(2000); // [2001-2200]

            // Simulate Client 2 receiving batch (client keeps it across scenes)
            GONetIdBatchManager.Client_AddBatch(batch2_Client2);

            // Verify Server knows about both batches
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batch1_Client1 + 50), "Server should track Client 1 batch");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batch2_Client2 + 50), "Server should track Client 2 batch");

            // Act - Scene change: Server KEEPS batch tracking (FIX), clients keep batches
            // OLD BUG: GONetIdBatchManager.Server_ResetAllBatches(); // This caused the overlap bug!
            // NEW FIX: Server does NOT reset, batches persist

            // Late joiner (Client 3) connects after scene change
            uint batch3_Client3_LateJoiner = GONetIdBatchManager.Server_AllocateNewBatch(2500); // Should get [2501-2700] (after 2500)

            // Assert - Late joiner batch should NOT overlap with existing batches
            // With the FIX: batch3 starts at 2501 (no overlap)
            // With the BUG: server forgot about [2001-2200], would allocate overlapping batch
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(batch2_Client2 + 50) &&
                          batch3_Client3_LateJoiner <= batch2_Client2 + 199,
                          "Late joiner batch should not overlap with Client 2's batch");

            // Verify no overlap between Client 2 and Client 3 batches
            bool hasOverlap = !(batch3_Client3_LateJoiner >= batch2_Client2 + 200 || // batch3 starts after batch2 ends
                               batch2_Client2 >= batch3_Client3_LateJoiner + 200);    // batch2 starts after batch3 ends

            Assert.IsFalse(hasOverlap, $"Client 2 batch [{batch2_Client2}-{batch2_Client2 + 199}] should NOT overlap with " +
                                      $"Client 3 batch [{batch3_Client3_LateJoiner}-{batch3_Client3_LateJoiner + 199}]");

            // Specific check: Verify the bug scenario (raw ID 704 in both batches) cannot happen
            // If Client 2 has batch [604-803] and Client 3 gets [704-903], ID 704 is in BOTH
            // With the fix, if Client 2 has [604-803], Client 3 should get [804-1003] or later

            // First, server allocates batch [604-803] to Client 2 (simulating the real scenario)
            uint batch_Client2_Example = GONetIdBatchManager.Server_AllocateNewBatch(603); // Start at 603 → batch [604-803]

            // Now Client 3 tries to get a batch near Client 2's range
            uint batch_Client3_Example = GONetIdBatchManager.Server_AllocateNewBatch(650); // Try to allocate near Client 2's batch

            // The overlap detection should move Client 3's batch to [804-1003]
            Assert.IsTrue(batch_Client3_Example >= batch_Client2_Example + 200,
                         $"Client 3 batch should start at or after {batch_Client2_Example + 200}, got {batch_Client3_Example}");
        }

        #endregion

        #region Edge Case Tests

        [Test]
        public void Client_TryAllocateNextId_HandlesRapidConsumption()
        {
            // Arrange - simulate rapid spawning (like user's "rapid-fire spawn test")
            GONetIdBatchManager.Client_AddBatch(5000);

            // Act - allocate 6 IDs with zero delay (matching test scenario)
            uint[] ids = new uint[6];
            for (int i = 0; i < 6; i++)
            {
                bool shouldRequest;
                bool success = GONetIdBatchManager.Client_TryAllocateNextId(out ids[i], out shouldRequest);
                Assert.IsTrue(success, $"Rapid allocation {i} should succeed");
            }

            // Assert - all IDs should be unique and sequential
            for (int i = 0; i < 6; i++)
            {
                Assert.AreEqual(5000 + i, ids[i], $"ID {i} should be sequential");

                // Verify no duplicates
                for (int j = i + 1; j < 6; j++)
                {
                    Assert.AreNotEqual(ids[i], ids[j], $"IDs {i} and {j} should be unique");
                }
            }
        }

        [Test]
        public void Server_AllocateNewBatch_HandlesLargeIdValues()
        {
            // Arrange - near max value
            uint nearMax = GONetParticipant.GONetId_Raw_MaxValue - 500;

            // Act
            uint batch = GONetIdBatchManager.Server_AllocateNewBatch(nearMax);

            // Assert
            Assert.AreEqual(nearMax + 1, batch, "Should allocate batch even near max value");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batch + 50), "Batch should be trackable");
        }

        #endregion

        #region ID Collision Prevention Tests (December 2025 Fixes)

        /// <summary>
        /// Verifies that Client_IsIdInAnyBatch correctly identifies IDs within batch ranges.
        /// This is critical for preventing client-owned objects from using IDs reserved for batches.
        /// </summary>
        [Test]
        public void Client_IsIdInAnyBatch_DetectsIdsInReceivedBatches()
        {
            // Arrange - add multiple batches (batch size is 200)
            GONetIdBatchManager.Client_AddBatch(4);      // Batch 1: [4-203]
            GONetIdBatchManager.Client_AddBatch(205);    // Batch 2: [205-404]

            // Assert - IDs within batches should be detected
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(4), "Batch 1 start should be detected");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(100), "Batch 1 middle should be detected");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(203), "Batch 1 end should be detected");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(205), "Batch 2 start should be detected");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(300), "Batch 2 middle should be detected");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(403), "Batch 2 end should be detected");

            // Assert - IDs outside batches should NOT be detected
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInAnyBatch(3), "ID before batch 1 should not be detected");
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInAnyBatch(204), "ID between batches should not be detected");
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInAnyBatch(405), "ID after batch 2 should not be detected");
        }

        /// <summary>
        /// Verifies that Client_IsIdInAnyBatch still works after a batch is exhausted and removed.
        /// This is critical because batch collision checking must persist even after active batches are removed.
        /// </summary>
        [Test]
        public void Client_IsIdInAnyBatch_PersistsAfterBatchExhaustion()
        {
            // Arrange - add a batch (batch size is 200)
            GONetIdBatchManager.Client_AddBatch(5000);

            // Verify it's in active batch tracking
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInActiveBatch(5100), "Should be in active batch initially");

            // Act - exhaust the entire batch (200 IDs)
            for (int i = 0; i < 200; i++)
            {
                uint id;
                bool shouldRequest;
                GONetIdBatchManager.Client_TryAllocateNextId(out id, out shouldRequest);
            }

            // Note: Exhausted batches are removed on the NEXT allocation attempt, not immediately.
            // Trigger the removal by attempting another allocation (which will fail).
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(".*CLIENT has NO available batch IDs.*"));
            uint failedId;
            bool shouldRequestNow;
            GONetIdBatchManager.Client_TryAllocateNextId(out failedId, out shouldRequestNow);

            // Assert - active batch tracking is cleared after exhaustion + removal
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInActiveBatch(5100), "Active batch should be removed after exhaustion");

            // Assert - BUT Client_IsIdInAnyBatch should STILL detect it (for collision prevention)
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(5000), "Batch start should still be detected in any batch");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(5100), "Batch middle should still be detected in any batch");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(5199), "Batch end should still be detected in any batch");
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInAnyBatch(5200), "ID after batch should not be detected");
        }

        /// <summary>
        /// Verifies the batch size offset calculation is correct for server batch allocation.
        /// Bug fix: Server was using hardcoded +99 instead of actual batch size -1.
        /// </summary>
        [Test]
        public void Server_BatchAllocation_UsesCorrectBatchSizeOffset()
        {
            // Arrange
            uint lastId = 1000;
            int batchSize = GONetIdBatchManager.GetBatchSize(); // Should be 200

            // Act
            uint batch1 = GONetIdBatchManager.Server_AllocateNewBatch(lastId);

            // Assert - batch should span from lastId+1 to lastId+batchSize
            Assert.AreEqual(1001, batch1, "First batch should start at lastId + 1");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batch1), "Batch start should be in batch");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(batch1 + (uint)batchSize - 1), "Batch end should be in batch");
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(batch1 + (uint)batchSize), "ID after batch should not be in batch");

            // Verify that if server was at batch end, next batch would start correctly
            uint batch2 = GONetIdBatchManager.Server_AllocateNewBatch(batch1 + (uint)batchSize - 1);
            Assert.AreEqual(batch1 + (uint)batchSize, batch2, "Second batch should start immediately after first batch ends");
        }

        /// <summary>
        /// Simulates the actual collision scenario that was causing stuck objects:
        /// 1. Client receives batch [4-203]
        /// 2. Client assigns IDs for client-owned objects
        /// 3. Without the fix, client would assign IDs 204, 205, etc.
        /// 4. Server later allocates batch [205-404]
        /// 5. Client uses batch IDs 205+ for server-owned objects
        /// 6. Collision: same raw ID used for both client-owned and server-owned objects
        ///
        /// With the fix, Client_IsIdInAnyBatch should prevent this.
        /// </summary>
        [Test]
        public void CollisionScenario_ClientSkipsBatchIdsForClientOwnedObjects()
        {
            // This test verifies the fix at the GONetIdBatchManager level
            // The actual integration is in GetNextAvailableGONetIdRaw

            // Arrange - client receives first batch [4-203]
            GONetIdBatchManager.Client_AddBatch(4);

            // Assert - IDs 4-203 should be detected as batch IDs
            for (uint id = 4; id < 204; id++)
            {
                Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(id),
                    $"ID {id} should be detected as a batch ID and skipped for client-owned objects");
            }

            // Assert - ID 204 should NOT be detected (gap between batches)
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInAnyBatch(204),
                "ID 204 should not be in batch 1 range [4-203]");

            // Arrange - client receives second batch [205-404]
            GONetIdBatchManager.Client_AddBatch(205);

            // Assert - now IDs 205-404 should also be detected
            for (uint id = 205; id < 405; id++)
            {
                Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(id),
                    $"ID {id} should be detected as a batch ID after second batch received");
            }

            // Assert - ID 204 is still in the gap
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInAnyBatch(204),
                "ID 204 should remain in gap between batches");

            // Assert - ID 405 is after all batches
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInAnyBatch(405),
                "ID 405 should be after all batches");
        }

        /// <summary>
        /// Verifies that Client_ResetAllBatches also clears the persistent batch range tracking.
        /// </summary>
        [Test]
        public void Client_ResetAllBatches_ClearsAllBatchRangeTracking()
        {
            // Arrange
            GONetIdBatchManager.Client_AddBatch(4);
            GONetIdBatchManager.Client_AddBatch(205);

            // Verify tracking is active
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(100), "Should detect batch ID before reset");
            Assert.IsTrue(GONetIdBatchManager.Client_IsIdInAnyBatch(300), "Should detect batch ID before reset");

            // Act
            GONetIdBatchManager.Client_ResetAllBatches();

            // Assert - all tracking should be cleared
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInAnyBatch(100), "Should NOT detect batch ID after reset");
            Assert.IsFalse(GONetIdBatchManager.Client_IsIdInAnyBatch(300), "Should NOT detect batch ID after reset");
        }

        /// <summary>
        /// Documents and tests the batch allocation race condition fix.
        ///
        /// ROOT CAUSE (December 2025):
        /// When server allocates a batch, it uses lastAssignedGONetIdRaw + 1 as the batch start.
        /// But if the client has spawned client-owned objects with higher IDs (e.g., 205, 206...),
        /// the server's lastAssignedGONetIdRaw doesn't know about these and allocates a batch
        /// that overlaps with IDs the client already used.
        ///
        /// FIX: Server now updates its lastAssignedGONetIdRaw when receiving client-owned spawns
        /// in CompleteRemoteInstantiation (GONet.cs). This ensures batch allocation starts
        /// AFTER any IDs the client has already used.
        ///
        /// SCENARIO WITHOUT FIX:
        /// 1. Server allocates batch [4-203], sets lastAssignedGONetIdRaw = 203
        /// 2. Client receives batch, also sets lastAssignedGONetIdRaw = 203
        /// 3. Client spawns client-owned objects: raw=204, 205, 206, ..., 225
        /// 4. Server's lastAssignedGONetIdRaw is still 203 (doesn't track client spawns!)
        /// 5. Server allocates batch [204-403] - but client already used 204-225!
        /// 6. Collision: same raw ID used for both client-owned and server-owned objects
        ///
        /// SCENARIO WITH FIX:
        /// 1. Server allocates batch [4-203], sets lastAssignedGONetIdRaw = 203
        /// 2. Client spawns client-owned objects: raw=204, 205, 206, ..., 225
        /// 3. Server receives spawns, updates lastAssignedGONetIdRaw to 225 (FIX!)
        /// 4. Server allocates batch [226-425] - no overlap with client IDs
        /// </summary>
        [Test]
        public void Server_BatchAllocation_MustAccountForClientOwnedSpawns()
        {
            // This test verifies the batch manager allocates correctly when given the
            // correct lastAssignedGONetIdRaw value. The actual fix is in GONet.cs
            // CompleteRemoteInstantiation which updates lastAssignedGONetIdRaw when
            // receiving client spawns.

            // Simulate the scenario:
            // 1. Server's lastAssignedGONetIdRaw = 3 (before any batches)
            // 2. Client connects, server allocates batch [4-203]
            // 3. Client spawns client-owned objects with raw=204, 205, ..., 225
            // 4. WITH THE FIX: Server's lastAssignedGONetIdRaw updates to 225

            // The key insight: If the server properly tracks client spawns,
            // the next batch allocation will start AFTER the client's highest ID.

            // Arrange - server's lastAssignedGONetIdRaw after tracking client spawns
            // (In real code, CompleteRemoteInstantiation updates this when receiving spawns)
            uint serverLastId_AfterTrackingClientSpawns = 225;

            // Act - allocate a batch
            uint batch = GONetIdBatchManager.Server_AllocateNewBatch(serverLastId_AfterTrackingClientSpawns);

            // Assert - batch should start AFTER the client's highest ID
            Assert.AreEqual(226, batch, "Batch should start at lastAssignedGONetIdRaw + 1 (226)");

            // Verify the batch range
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(226), "ID 226 should be in batch");
            Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(425), "ID 425 (batch end) should be in batch");
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(225), "ID 225 (client-owned) should NOT be in batch");
            Assert.IsFalse(GONetIdBatchManager.Server_IsIdInAnyBatch(426), "ID 426 (after batch) should NOT be in batch");
        }

        /// <summary>
        /// Verifies that the fix prevents the actual collision scenario.
        /// This test simulates what happens WITHOUT the fix (bug scenario).
        /// </summary>
        [Test]
        public void Server_BatchAllocation_BugScenario_OverlapDetectedWhenLastIdNotTracked()
        {
            // BUG SCENARIO: Server doesn't track client spawns, allocates overlapping batch
            //
            // This test shows WHY the fix is needed:
            // - Server allocates batch [4-203]
            // - Client spawns with 204, 205, ..., 225 (server doesn't track!)
            // - Server still thinks lastAssignedGONetIdRaw = 203
            // - Server allocates batch [204-403] - OVERLAPS with client IDs 204-225!

            // Arrange - simulate server with stale lastAssignedGONetIdRaw (BUG scenario)
            uint serverLastId_Stale = 203; // Server didn't track client spawns

            // Act - allocate batch (this would be the problematic allocation)
            uint buggyBatch = GONetIdBatchManager.Server_AllocateNewBatch(serverLastId_Stale);

            // This batch starts at 204, which is BAD because client already used 204-225
            Assert.AreEqual(204, buggyBatch, "Without fix, batch starts at 204");

            // The batch [204-403] contains IDs that client already used!
            // IDs 204-225 would collide with client-owned objects
            for (uint clientUsedId = 204; clientUsedId <= 225; clientUsedId++)
            {
                Assert.IsTrue(GONetIdBatchManager.Server_IsIdInAnyBatch(clientUsedId),
                    $"ID {clientUsedId} is in the buggy batch - would collide with client-owned object!");
            }

            // This is why the fix is critical: CompleteRemoteInstantiation must update
            // lastAssignedGONetIdRaw when receiving client spawns to prevent this overlap.
        }

        #endregion
    }
}
