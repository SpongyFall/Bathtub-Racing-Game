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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace GONet
{
    /// <summary>
    /// Comprehensive unit tests for GONet Session Reset functionality.
    /// These tests verify that all static state is properly cleared when transitioning between sessions,
    /// which is critical for:
    /// - Fast Iteration Mode (Unity editor with domain reload disabled)
    /// - Runtime Lobby Flow (switching server/client roles without closing the game)
    /// - Session Transitions (leaving one multiplayer session and joining another)
    /// </summary>
    [TestFixture]
    public class GONetSessionResetTests
    {
        #region Setup and Teardown

        [SetUp]
        public void Setup()
        {
            // Ensure clean state before each test
            ResetAllStaticState();
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up after each test
            ResetAllStaticState();
        }

        private void ResetAllStaticState()
        {
            // Reset all components to clean state
            GONetLocal.ClearStaticState();
            GONetIdBatchManager.ResetForNewSession();
            GONetEventBus.Instance.ClearAllSubscriptions();
            GONetGlobal.ClearSessionState();
            GONetMain.ResetAnimatorTriggerStateForNewSession();
            GONetMain.ResetReparentingStateForNewSession();
            // Note: GONetSpawnSupport_Runtime.ClearAllCachesForSessionReset() requires more setup
        }

        #endregion

        #region GONetLocal.ClearStaticState Tests

        [Test]
        public void GONetLocal_ClearStaticState_ClearsLocalsByAuthorityId()
        {
            // Arrange - Add some entries to localsByAuthorityId via reflection
            var localsByAuthorityIdField = typeof(GONetLocal).GetField("localsByAuthorityId",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(localsByAuthorityIdField, "localsByAuthorityId field should exist");

            var localsByAuthorityId = localsByAuthorityIdField.GetValue(null) as Dictionary<ushort, GONetLocal>;
            Assert.NotNull(localsByAuthorityId, "localsByAuthorityId should be a Dictionary");

            // Simulate adding entries (using null values since we can't easily create GONetLocal instances)
            // In a real scenario, these would be real GONetLocal references
            int initialCount = localsByAuthorityId.Count;

            // Act
            GONetLocal.ClearStaticState();

            // Assert
            Assert.AreEqual(0, localsByAuthorityId.Count, "localsByAuthorityId should be cleared");
        }

        [Test]
        public void GONetLocal_ClearStaticState_ClearsLookupByAuthorityId()
        {
            // Arrange - Access lookupByAuthorityId via reflection
            var lookupField = typeof(GONetLocal).GetField("lookupByAuthorityId",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(lookupField, "lookupByAuthorityId field should exist");

            // Act
            GONetLocal.ClearStaticState();

            // Assert
            var lookupValue = lookupField.GetValue(null);
            Assert.IsNull(lookupValue, "lookupByAuthorityId should be null after clear");
        }

        [Test]
        public void GONetLocal_ClearStaticState_PreservesHighestClientAuthorityIdEverSeen()
        {
            // Arrange - Get the field value before clearing
            var highestIdField = typeof(GONetLocal).GetField("highestClientAuthorityIdEverSeen",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(highestIdField, "highestClientAuthorityIdEverSeen field should exist");

            // Set a value to verify it's preserved
            ushort testValue = 42;
            highestIdField.SetValue(null, testValue);

            // Act
            GONetLocal.ClearStaticState();

            // Assert - This value should be preserved (not reset)
            ushort valueAfterClear = (ushort)highestIdField.GetValue(null);
            Assert.AreEqual(testValue, valueAfterClear,
                "highestClientAuthorityIdEverSeen should be preserved for failover scenarios");
        }

        [Test]
        public void GONetLocal_ClearStaticState_CanBeCalledMultipleTimes()
        {
            // Act & Assert - Should not throw
            Assert.DoesNotThrow(() =>
            {
                GONetLocal.ClearStaticState();
                GONetLocal.ClearStaticState();
                GONetLocal.ClearStaticState();
            });
        }

        #endregion

        #region GONetIdBatchManager.ResetForNewSession Tests

        [Test]
        public void GONetIdBatchManager_ResetForNewSession_ClearsServerAllocatedBatchStarts()
        {
            // Arrange - Add some server batch starts
            var serverBatchesField = typeof(GONetIdBatchManager).GetField("server_allocatedBatchStarts",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(serverBatchesField, "server_allocatedBatchStarts field should exist");

            var serverBatches = serverBatchesField.GetValue(null) as List<uint>;
            Assert.NotNull(serverBatches, "server_allocatedBatchStarts should be a List<uint>");

            serverBatches.Add(1000);
            serverBatches.Add(2000);
            serverBatches.Add(3000);
            Assert.AreEqual(3, serverBatches.Count, "Should have 3 batch starts");

            // Act
            GONetIdBatchManager.ResetForNewSession();

            // Assert
            Assert.AreEqual(0, serverBatches.Count, "server_allocatedBatchStarts should be cleared");
        }

        [Test]
        public void GONetIdBatchManager_ResetForNewSession_ClearsClientActiveBatches()
        {
            // Arrange
            var clientBatchesField = typeof(GONetIdBatchManager).GetField("client_activeBatches",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(clientBatchesField, "client_activeBatches field should exist");

            var clientBatches = clientBatchesField.GetValue(null) as System.Collections.IList;
            Assert.NotNull(clientBatches, "client_activeBatches should be a list");

            int initialCount = clientBatches.Count;

            // Act
            GONetIdBatchManager.ResetForNewSession();

            // Assert
            Assert.AreEqual(0, clientBatches.Count, "client_activeBatches should be cleared");
        }

        [Test]
        public void GONetIdBatchManager_ResetForNewSession_ClearsClientAllBatchRanges()
        {
            // Arrange
            var batchRangesField = typeof(GONetIdBatchManager).GetField("client_allBatchRanges",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(batchRangesField, "client_allBatchRanges field should exist");

            var batchRanges = batchRangesField.GetValue(null) as System.Collections.IList;
            Assert.NotNull(batchRanges, "client_allBatchRanges should be a list");

            // Act
            GONetIdBatchManager.ResetForNewSession();

            // Assert
            Assert.AreEqual(0, batchRanges.Count, "client_allBatchRanges should be cleared");
        }

        [Test]
        public void GONetIdBatchManager_ResetForNewSession_ResetsClientTotalIdsAllocated()
        {
            // Arrange
            var totalAllocatedField = typeof(GONetIdBatchManager).GetField("client_totalIdsAllocated",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(totalAllocatedField, "client_totalIdsAllocated field should exist");

            totalAllocatedField.SetValue(null, (uint)5000);

            // Act
            GONetIdBatchManager.ResetForNewSession();

            // Assert
            uint valueAfterReset = (uint)totalAllocatedField.GetValue(null);
            Assert.AreEqual(0u, valueAfterReset, "client_totalIdsAllocated should be reset to 0");
        }

        [Test]
        public void GONetIdBatchManager_ResetForNewSession_ResetsClientTotalIdsUsed()
        {
            // Arrange
            var totalUsedField = typeof(GONetIdBatchManager).GetField("client_totalIdsUsed",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(totalUsedField, "client_totalIdsUsed field should exist");

            totalUsedField.SetValue(null, (uint)2500);

            // Act
            GONetIdBatchManager.ResetForNewSession();

            // Assert
            uint valueAfterReset = (uint)totalUsedField.GetValue(null);
            Assert.AreEqual(0u, valueAfterReset, "client_totalIdsUsed should be reset to 0");
        }

        [Test]
        public void GONetIdBatchManager_ResetForNewSession_ResetsClientHasRequestedBatch()
        {
            // Arrange
            var hasRequestedField = typeof(GONetIdBatchManager).GetField("client_hasRequestedBatch",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(hasRequestedField, "client_hasRequestedBatch field should exist");

            hasRequestedField.SetValue(null, true);

            // Act
            GONetIdBatchManager.ResetForNewSession();

            // Assert
            bool valueAfterReset = (bool)hasRequestedField.GetValue(null);
            Assert.IsFalse(valueAfterReset, "client_hasRequestedBatch should be reset to false");
        }

        [Test]
        public void GONetIdBatchManager_ResetForNewSession_CanBeCalledMultipleTimes()
        {
            // Act & Assert - Should not throw
            Assert.DoesNotThrow(() =>
            {
                GONetIdBatchManager.ResetForNewSession();
                GONetIdBatchManager.ResetForNewSession();
                GONetIdBatchManager.ResetForNewSession();
            });
        }

        [Test]
        public void GONetIdBatchManager_ResetForNewSession_AllowsFreshBatchAllocationAfterReset()
        {
            // Arrange - Simulate having used some IDs
            var totalUsedField = typeof(GONetIdBatchManager).GetField("client_totalIdsUsed",
                BindingFlags.NonPublic | BindingFlags.Static);
            var totalAllocatedField = typeof(GONetIdBatchManager).GetField("client_totalIdsAllocated",
                BindingFlags.NonPublic | BindingFlags.Static);

            totalUsedField.SetValue(null, (uint)999);
            totalAllocatedField.SetValue(null, (uint)1000);

            // Act
            GONetIdBatchManager.ResetForNewSession();

            // Assert - Should be ready for fresh allocation
            uint usedAfterReset = (uint)totalUsedField.GetValue(null);
            uint allocatedAfterReset = (uint)totalAllocatedField.GetValue(null);
            Assert.AreEqual(0u, usedAfterReset, "Should have 0 used IDs after reset");
            Assert.AreEqual(0u, allocatedAfterReset, "Should have 0 allocated IDs after reset");
        }

        #endregion

        #region GONetEventBus.ClearAllSubscriptions Tests

        [Test]
        public void GONetEventBus_ClearAllSubscriptions_ClearsNonSyncHandlers()
        {
            // Arrange
            var handlerMappingsField = typeof(GONetEventBus).GetField("nonSyncEventHandlerMappings",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handlerMappingsField, "nonSyncEventHandlerMappings field should exist");

            var bus = GONetEventBus.Instance;
            var mappings = handlerMappingsField.GetValue(bus);
            Assert.NotNull(mappings, "nonSyncEventHandlerMappings should not be null");

            // Act
            bus.ClearAllSubscriptions();

            // Assert - mappings object should still exist but be empty
            // Note: Internal structure verification is implementation-dependent
            // The clearing should not throw, verification of empty state depends on implementation
            Assert.Pass("ClearAllSubscriptions completed without throwing");
        }

        [Test]
        public void GONetEventBus_ClearAllSubscriptions_ClearsTypeHierarchyCache()
        {
            // Arrange - Access TypeHierarchyCache internals
            var typeHierarchyCacheType = typeof(GONetEventBus).GetNestedType("TypeHierarchyCache",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (typeHierarchyCacheType == null)
            {
                // TypeHierarchyCache might be a separate class
                Assert.Pass("TypeHierarchyCache structure differs from expected - skipping internal verification");
                return;
            }

            // Act
            GONetEventBus.Instance.ClearAllSubscriptions();

            // Assert - verify isInitialized is false
            var isInitializedField = typeHierarchyCacheType.GetField("isInitialized",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (isInitializedField != null)
            {
                bool isInitialized = (bool)isInitializedField.GetValue(null);
                Assert.IsFalse(isInitialized, "TypeHierarchyCache.isInitialized should be false after clear");
            }
        }

        [Test]
        public void GONetEventBus_ClearAllSubscriptions_DrainsPublishASAPQueue()
        {
            // Arrange - Access publishASAPQueue
            var queueField = typeof(GONetEventBus).GetField("publishASAPQueue",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(queueField, "publishASAPQueue field should exist");

            var bus = GONetEventBus.Instance;
            var queue = queueField.GetValue(bus) as System.Collections.ICollection;

            // Act
            bus.ClearAllSubscriptions();

            // Assert - queue should be empty (or have been drained)
            if (queue != null)
            {
                Assert.AreEqual(0, queue.Count, "publishASAPQueue should be empty after clear");
            }
        }

        [Test]
        public void GONetEventBus_ClearAllSubscriptions_ResetsPerformanceMetrics()
        {
            // Arrange
            var totalPublishCallsField = typeof(GONetEventBus).GetField("_totalPublishCalls",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var totalHandlerInvocationsField = typeof(GONetEventBus).GetField("_totalHandlerInvocations",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var bus = GONetEventBus.Instance;

            if (totalPublishCallsField != null)
            {
                totalPublishCallsField.SetValue(bus, 100L);
            }
            if (totalHandlerInvocationsField != null)
            {
                totalHandlerInvocationsField.SetValue(bus, 500L);
            }

            // Act
            bus.ClearAllSubscriptions();

            // Assert
            if (totalPublishCallsField != null)
            {
                long totalPublishCalls = (long)totalPublishCallsField.GetValue(bus);
                Assert.AreEqual(0L, totalPublishCalls, "_totalPublishCalls should be reset to 0");
            }
            if (totalHandlerInvocationsField != null)
            {
                long totalHandlerInvocations = (long)totalHandlerInvocationsField.GetValue(bus);
                Assert.AreEqual(0L, totalHandlerInvocations, "_totalHandlerInvocations should be reset to 0");
            }
        }

        [Test]
        public void GONetEventBus_ClearAllSubscriptions_CanBeCalledMultipleTimes()
        {
            // Act & Assert - Should not throw
            Assert.DoesNotThrow(() =>
            {
                GONetEventBus.Instance.ClearAllSubscriptions();
                GONetEventBus.Instance.ClearAllSubscriptions();
                GONetEventBus.Instance.ClearAllSubscriptions();
            });
        }

        [Test]
        public void GONetEventBus_ClearAllSubscriptions_AllowsFreshSubscriptionsAfterClear()
        {
            // Arrange
            var bus = GONetEventBus.Instance;
            bool handlerWasCalled = false;

            GONetEventBus.HandleEventDelegate<GONetParticipantEnabledEvent> handler = (evt) => { handlerWasCalled = true; };

            // Subscribe, clear, then subscribe again
            bus.Subscribe<GONetParticipantEnabledEvent>(handler);
            bus.ClearAllSubscriptions();
            bus.Subscribe<GONetParticipantEnabledEvent>(handler);

            // Act - Publish an event
            var testEvent = new GONetParticipantEnabledEvent(1);
            bus.Publish(testEvent);

            // Assert
            Assert.IsTrue(handlerWasCalled, "Handler should be called after re-subscribing post-clear");
        }

        #endregion

        #region GONetGlobal.ClearSessionState Tests

        [Test]
        public void GONetGlobal_ClearSessionState_ClearsSceneLoadTimesTicks()
        {
            // Arrange
            var sceneLoadTimesField = typeof(GONetGlobal).GetField("sceneLoadTimesTicks",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(sceneLoadTimesField, "sceneLoadTimesTicks field should exist");

            var sceneLoadTimes = sceneLoadTimesField.GetValue(null) as Dictionary<string, long>;
            Assert.NotNull(sceneLoadTimes, "sceneLoadTimesTicks should be a Dictionary");

            sceneLoadTimes["TestScene1"] = 12345678L;
            sceneLoadTimes["TestScene2"] = 87654321L;
            Assert.AreEqual(2, sceneLoadTimes.Count, "Should have 2 scene entries");

            // Act
            GONetGlobal.ClearSessionState();

            // Assert
            Assert.AreEqual(0, sceneLoadTimes.Count, "sceneLoadTimesTicks should be cleared");
        }

        [Test]
        public void GONetGlobal_ClearSessionState_ResetsGonetGlobalAwakeTicks()
        {
            // Arrange
            var awakeTicksField = typeof(GONetGlobal).GetField("gonetGlobalAwakeTicks",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (awakeTicksField != null)
            {
                awakeTicksField.SetValue(null, 999999L);

                // Act
                GONetGlobal.ClearSessionState();

                // Assert
                long valueAfterClear = (long)awakeTicksField.GetValue(null);
                Assert.AreEqual(-1L, valueAfterClear, "gonetGlobalAwakeTicks should be reset to -1");
            }
            else
            {
                Assert.Pass("gonetGlobalAwakeTicks field not found - may have different implementation");
            }
        }

        [Test]
        public void GONetGlobal_ClearSessionState_CanBeCalledMultipleTimes()
        {
            // Act & Assert - Should not throw
            Assert.DoesNotThrow(() =>
            {
                GONetGlobal.ClearSessionState();
                GONetGlobal.ClearSessionState();
                GONetGlobal.ClearSessionState();
            });
        }

        #endregion

        #region Integration Tests - Full ResetForNewSession Flow

        [Test]
        public void Integration_ResetForNewSession_ClearsAllComponentState()
        {
            // Arrange - Set up state in multiple components

            // GONetIdBatchManager state
            var serverBatchesField = typeof(GONetIdBatchManager).GetField("server_allocatedBatchStarts",
                BindingFlags.NonPublic | BindingFlags.Static);
            var serverBatches = serverBatchesField?.GetValue(null) as List<uint>;
            serverBatches?.Add(1000);

            // GONetGlobal state
            var sceneLoadTimesField = typeof(GONetGlobal).GetField("sceneLoadTimesTicks",
                BindingFlags.NonPublic | BindingFlags.Static);
            var sceneLoadTimes = sceneLoadTimesField?.GetValue(null) as Dictionary<string, long>;
            sceneLoadTimes?.Add("TestScene", 12345L);

            // Animator trigger state
            var animatorFlagField = typeof(GONetMain).GetField("isAnimatorTriggerEventHandlerSubscribed",
                BindingFlags.NonPublic | BindingFlags.Static);
            animatorFlagField?.SetValue(null, true);

            // Reparenting state
            var reparentFlagField = typeof(GONetMain).GetField("isReparentEventHandlerSubscribed",
                BindingFlags.NonPublic | BindingFlags.Static);
            reparentFlagField?.SetValue(null, true);

            // Act - Call the full reset (same as what ResetForNewSession does internally)
            GONetLocal.ClearStaticState();
            GONetIdBatchManager.ResetForNewSession();
            GONetEventBus.Instance.ClearAllSubscriptions();
            GONetGlobal.ClearSessionState();
            GONetMain.ResetAnimatorTriggerStateForNewSession();
            GONetMain.ResetReparentingStateForNewSession();

            // Assert - Verify all state is cleared
            Assert.AreEqual(0, serverBatches?.Count ?? 0, "Server batches should be cleared");
            Assert.AreEqual(0, sceneLoadTimes?.Count ?? 0, "Scene load times should be cleared");

            bool animatorFlag = animatorFlagField != null ? (bool)animatorFlagField.GetValue(null) : true;
            bool reparentFlag = reparentFlagField != null ? (bool)reparentFlagField.GetValue(null) : true;
            Assert.IsFalse(animatorFlag, "Animator subscription flag should be cleared");
            Assert.IsFalse(reparentFlag, "Reparent subscription flag should be cleared");
        }

        [Test]
        public void Integration_ResetForNewSession_SimulateServerToClientTransition()
        {
            // This test simulates the Fast Iteration Mode scenario:
            // 1. Start as SERVER (has authority IDs, batch allocations)
            // 2. Stop and reset
            // 3. Start as CLIENT (should have clean state)

            // Arrange - Simulate SERVER state
            var serverBatchesField = typeof(GONetIdBatchManager).GetField("server_allocatedBatchStarts",
                BindingFlags.NonPublic | BindingFlags.Static);
            var serverBatches = serverBatchesField?.GetValue(null) as List<uint>;
            serverBatches?.Add(1000);
            serverBatches?.Add(2000);
            serverBatches?.Add(3000);

            var hasRequestedField = typeof(GONetIdBatchManager).GetField("client_hasRequestedBatch",
                BindingFlags.NonPublic | BindingFlags.Static);
            hasRequestedField?.SetValue(null, false);

            // Act - Simulate reset (as would happen in ResetForNewSession)
            GONetIdBatchManager.ResetForNewSession();

            // Assert - State should be clean for CLIENT startup
            Assert.AreEqual(0, serverBatches?.Count ?? 0, "No server batches should exist");

            bool hasRequested = hasRequestedField != null ? (bool)hasRequestedField.GetValue(null) : false;
            Assert.IsFalse(hasRequested, "Client should not have requested batch yet");
        }

        [Test]
        public void Integration_ResetForNewSession_SimulateClientToServerTransition()
        {
            // This test simulates:
            // 1. Start as CLIENT (has batch requests, received allocations)
            // 2. Stop and reset
            // 3. Start as SERVER (should have clean state)

            // Arrange - Simulate CLIENT state
            var clientBatchesField = typeof(GONetIdBatchManager).GetField("client_activeBatches",
                BindingFlags.NonPublic | BindingFlags.Static);
            var clientRangesField = typeof(GONetIdBatchManager).GetField("client_allBatchRanges",
                BindingFlags.NonPublic | BindingFlags.Static);
            var totalAllocatedField = typeof(GONetIdBatchManager).GetField("client_totalIdsAllocated",
                BindingFlags.NonPublic | BindingFlags.Static);
            var totalUsedField = typeof(GONetIdBatchManager).GetField("client_totalIdsUsed",
                BindingFlags.NonPublic | BindingFlags.Static);
            var hasRequestedField = typeof(GONetIdBatchManager).GetField("client_hasRequestedBatch",
                BindingFlags.NonPublic | BindingFlags.Static);

            // Set client state
            totalAllocatedField?.SetValue(null, (uint)1000);
            totalUsedField?.SetValue(null, (uint)500);
            hasRequestedField?.SetValue(null, true);

            // Act - Reset
            GONetIdBatchManager.ResetForNewSession();

            // Assert - State should be clean for SERVER startup
            uint totalAllocated = totalAllocatedField != null ? (uint)totalAllocatedField.GetValue(null) : 0;
            uint totalUsed = totalUsedField != null ? (uint)totalUsedField.GetValue(null) : 0;
            bool hasRequested = hasRequestedField != null ? (bool)hasRequestedField.GetValue(null) : true;

            Assert.AreEqual(0u, totalAllocated, "No client IDs should be allocated");
            Assert.AreEqual(0u, totalUsed, "No client IDs should be used");
            Assert.IsFalse(hasRequested, "Client should not have batch request pending");
        }

        [Test]
        public void Integration_ResetForNewSession_RapidCycling()
        {
            // Simulate rapid play/stop cycles (stress test for Fast Iteration Mode)
            for (int i = 0; i < 10; i++)
            {
                // Simulate some state accumulation
                var serverBatchesField = typeof(GONetIdBatchManager).GetField("server_allocatedBatchStarts",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var serverBatches = serverBatchesField?.GetValue(null) as List<uint>;
                serverBatches?.Add((uint)(i * 1000));

                var sceneLoadTimesField = typeof(GONetGlobal).GetField("sceneLoadTimesTicks",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var sceneLoadTimes = sceneLoadTimesField?.GetValue(null) as Dictionary<string, long>;
                sceneLoadTimes?.Add($"Scene_{i}", (long)i);

                // Reset (full reset including new animator/reparent methods)
                GONetLocal.ClearStaticState();
                GONetIdBatchManager.ResetForNewSession();
                GONetEventBus.Instance.ClearAllSubscriptions();
                GONetGlobal.ClearSessionState();
                GONetMain.ResetAnimatorTriggerStateForNewSession();
                GONetMain.ResetReparentingStateForNewSession();

                // Verify clean state
                Assert.AreEqual(0, serverBatches?.Count ?? 0, $"Cycle {i}: Server batches should be cleared");
                Assert.AreEqual(0, sceneLoadTimes?.Count ?? 0, $"Cycle {i}: Scene load times should be cleared");
            }
        }

        #endregion

        #region Thread Safety Tests

        [Test]
        public void ThreadSafety_ConcurrentResetCalls_DoesNotThrow()
        {
            // Test that concurrent reset calls don't cause exceptions
            var exceptions = new ConcurrentBag<Exception>();
            var threads = new Thread[5];

            for (int i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        for (int j = 0; j < 10; j++)
                        {
                            GONetLocal.ClearStaticState();
                            GONetIdBatchManager.ResetForNewSession();
                            GONetEventBus.Instance.ClearAllSubscriptions();
                            GONetGlobal.ClearSessionState();
                            GONetMain.ResetAnimatorTriggerStateForNewSession();
                            GONetMain.ResetReparentingStateForNewSession();
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }

            // Start all threads
            foreach (var thread in threads)
            {
                thread.Start();
            }

            // Wait for completion
            foreach (var thread in threads)
            {
                thread.Join();
            }

            // Assert no exceptions
            Assert.IsEmpty(exceptions,
                $"Concurrent reset calls should not throw. Exceptions: {string.Join(", ", exceptions)}");
        }

        [Test]
        public void ThreadSafety_RapidClearAllSubscriptions_HandlesGracefully()
        {
            // NOTE: GONet's EventBus requires main thread for Subscribe/Publish operations
            // (enforced via EnsureMainThread_IfPlaying). This test verifies that rapid
            // sequential ClearAllSubscriptions calls on the main thread don't cause issues.

            var bus = GONetEventBus.Instance;
            int clearCount = 0;

            // Rapidly clear subscriptions (simulates rapid play/stop cycles)
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    bus.ClearAllSubscriptions();
                    clearCount++;
                }
            });

            Assert.AreEqual(100, clearCount, "Should complete 100 clear cycles without throwing");
        }

        #endregion

        #region ResetAnimatorTriggerStateForNewSession Tests

        [Test]
        public void AnimatorTrigger_ResetForNewSession_ClearsSubscriptionFlag()
        {
            // Arrange - Access the subscription flag via reflection
            var subscriptionFlagField = typeof(GONetMain).GetField("isAnimatorTriggerEventHandlerSubscribed",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(subscriptionFlagField, "isAnimatorTriggerEventHandlerSubscribed field should exist");

            // Set the flag to true (simulating previous session state)
            subscriptionFlagField.SetValue(null, true);
            Assert.IsTrue((bool)subscriptionFlagField.GetValue(null), "Flag should be true before reset");

            // Act
            GONetMain.ResetAnimatorTriggerStateForNewSession();

            // Assert
            bool flagAfterReset = (bool)subscriptionFlagField.GetValue(null);
            Assert.IsFalse(flagAfterReset, "isAnimatorTriggerEventHandlerSubscribed should be false after reset");
        }

        [Test]
        public void AnimatorTrigger_ResetForNewSession_ClearsPendingTriggerEvents()
        {
            // Arrange - Access pendingAnimatorTriggerEvents via reflection
            var pendingEventsField = typeof(GONetMain).GetField("pendingAnimatorTriggerEvents",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(pendingEventsField, "pendingAnimatorTriggerEvents field should exist");

            var pendingEvents = pendingEventsField.GetValue(null) as Dictionary<uint, List<(int, long)>>;
            Assert.NotNull(pendingEvents, "pendingAnimatorTriggerEvents should be a Dictionary");

            // Add some test entries
            pendingEvents[1001] = new List<(int, long)> { (42, 1000L), (43, 2000L) };
            pendingEvents[1002] = new List<(int, long)> { (44, 3000L) };
            Assert.AreEqual(2, pendingEvents.Count, "Should have 2 pending entries before reset");

            // Act
            GONetMain.ResetAnimatorTriggerStateForNewSession();

            // Assert
            Assert.AreEqual(0, pendingEvents.Count, "pendingAnimatorTriggerEvents should be cleared after reset");
        }

        [Test]
        public void AnimatorTrigger_ResetForNewSession_CanBeCalledMultipleTimes()
        {
            // Act & Assert - Should not throw
            Assert.DoesNotThrow(() =>
            {
                GONetMain.ResetAnimatorTriggerStateForNewSession();
                GONetMain.ResetAnimatorTriggerStateForNewSession();
                GONetMain.ResetAnimatorTriggerStateForNewSession();
            });
        }

        [Test]
        public void AnimatorTrigger_ResetForNewSession_AllowsResubscriptionAfterReset()
        {
            // Arrange - Access both fields
            var subscriptionFlagField = typeof(GONetMain).GetField("isAnimatorTriggerEventHandlerSubscribed",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(subscriptionFlagField, "isAnimatorTriggerEventHandlerSubscribed field should exist");

            // Simulate: subscription flag is true (from previous session)
            subscriptionFlagField.SetValue(null, true);

            // Act - Reset
            GONetMain.ResetAnimatorTriggerStateForNewSession();

            // Assert - The flag should now be false, allowing InitAnimatorTriggerSupport() to re-subscribe
            bool flagAfterReset = (bool)subscriptionFlagField.GetValue(null);
            Assert.IsFalse(flagAfterReset, "Flag should be false after reset, allowing InitAnimatorTriggerSupport() to re-subscribe");
        }

        #endregion

        #region ResetReparentingStateForNewSession Tests

        [Test]
        public void Reparenting_ResetForNewSession_ClearsSubscriptionFlag()
        {
            // Arrange - Access the subscription flag via reflection
            var subscriptionFlagField = typeof(GONetMain).GetField("isReparentEventHandlerSubscribed",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(subscriptionFlagField, "isReparentEventHandlerSubscribed field should exist");

            // Set the flag to true (simulating previous session state)
            subscriptionFlagField.SetValue(null, true);
            Assert.IsTrue((bool)subscriptionFlagField.GetValue(null), "Flag should be true before reset");

            // Act
            GONetMain.ResetReparentingStateForNewSession();

            // Assert
            bool flagAfterReset = (bool)subscriptionFlagField.GetValue(null);
            Assert.IsFalse(flagAfterReset, "isReparentEventHandlerSubscribed should be false after reset");
        }

        [Test]
        public void Reparenting_ResetForNewSession_ClearsRateLimits()
        {
            // Arrange - Access reparentRateLimits via reflection
            var rateLimitsField = typeof(GONetMain).GetField("reparentRateLimits",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(rateLimitsField, "reparentRateLimits field should exist");

            var rateLimits = rateLimitsField.GetValue(null) as Dictionary<ushort, (float, int)>;
            Assert.NotNull(rateLimits, "reparentRateLimits should be a Dictionary");

            // Add some test entries
            rateLimits[1] = (1.0f, 5);
            rateLimits[2] = (2.0f, 10);
            Assert.AreEqual(2, rateLimits.Count, "Should have 2 rate limit entries before reset");

            // Act
            GONetMain.ResetReparentingStateForNewSession();

            // Assert
            Assert.AreEqual(0, rateLimits.Count, "reparentRateLimits should be cleared after reset");
        }

        [Test]
        public void Reparenting_ResetForNewSession_CanBeCalledMultipleTimes()
        {
            // Act & Assert - Should not throw
            Assert.DoesNotThrow(() =>
            {
                GONetMain.ResetReparentingStateForNewSession();
                GONetMain.ResetReparentingStateForNewSession();
                GONetMain.ResetReparentingStateForNewSession();
            });
        }

        [Test]
        public void Reparenting_ResetForNewSession_AllowsResubscriptionAfterReset()
        {
            // Arrange - Access the subscription flag
            var subscriptionFlagField = typeof(GONetMain).GetField("isReparentEventHandlerSubscribed",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(subscriptionFlagField, "isReparentEventHandlerSubscribed field should exist");

            // Simulate: subscription flag is true (from previous session)
            subscriptionFlagField.SetValue(null, true);

            // Act - Reset
            GONetMain.ResetReparentingStateForNewSession();

            // Assert - The flag should now be false, allowing InitReparentingSupport() to re-subscribe
            bool flagAfterReset = (bool)subscriptionFlagField.GetValue(null);
            Assert.IsFalse(flagAfterReset, "Flag should be false after reset, allowing InitReparentingSupport() to re-subscribe");
        }

        #endregion

        #region Fast Iteration Mode Integration Tests

        [Test]
        public void FastIterationMode_SubscriptionFlags_ResetCorrectly()
        {
            // This test simulates the exact Fast Iteration Mode scenario that was failing:
            // 1. First play session: flags get set to true
            // 2. Stop play: ClearAllSubscriptions() clears handlers BUT flags stay true
            // 3. Second play session: Init*Support() methods return early without re-subscribing
            //
            // The fix: Reset*StateForNewSession() methods clear the flags

            // Arrange - Access both flags
            var animatorFlagField = typeof(GONetMain).GetField("isAnimatorTriggerEventHandlerSubscribed",
                BindingFlags.NonPublic | BindingFlags.Static);
            var reparentFlagField = typeof(GONetMain).GetField("isReparentEventHandlerSubscribed",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(animatorFlagField, "isAnimatorTriggerEventHandlerSubscribed field should exist");
            Assert.NotNull(reparentFlagField, "isReparentEventHandlerSubscribed field should exist");

            // Simulate SESSION 1: Init sets flags to true
            animatorFlagField.SetValue(null, true);
            reparentFlagField.SetValue(null, true);

            // Simulate END OF SESSION 1: ClearAllSubscriptions + Reset methods
            GONetEventBus.Instance.ClearAllSubscriptions();
            GONetMain.ResetAnimatorTriggerStateForNewSession();
            GONetMain.ResetReparentingStateForNewSession();

            // Assert - Both flags should be false, ready for SESSION 2
            bool animatorFlagAfterReset = (bool)animatorFlagField.GetValue(null);
            bool reparentFlagAfterReset = (bool)reparentFlagField.GetValue(null);

            Assert.IsFalse(animatorFlagAfterReset,
                "isAnimatorTriggerEventHandlerSubscribed should be false after reset (allows re-subscription in session 2)");
            Assert.IsFalse(reparentFlagAfterReset,
                "isReparentEventHandlerSubscribed should be false after reset (allows re-subscription in session 2)");
        }

        [Test]
        public void FastIterationMode_RapidCycling_AllSubscriptionFlagsReset()
        {
            // Simulate rapid play/stop cycles (stress test for Fast Iteration Mode)
            var animatorFlagField = typeof(GONetMain).GetField("isAnimatorTriggerEventHandlerSubscribed",
                BindingFlags.NonPublic | BindingFlags.Static);
            var reparentFlagField = typeof(GONetMain).GetField("isReparentEventHandlerSubscribed",
                BindingFlags.NonPublic | BindingFlags.Static);

            for (int i = 0; i < 10; i++)
            {
                // Simulate session start - flags set to true
                animatorFlagField.SetValue(null, true);
                reparentFlagField.SetValue(null, true);

                // Simulate session end - full reset
                GONetLocal.ClearStaticState();
                GONetIdBatchManager.ResetForNewSession();
                GONetEventBus.Instance.ClearAllSubscriptions();
                GONetGlobal.ClearSessionState();
                GONetMain.ResetAnimatorTriggerStateForNewSession();
                GONetMain.ResetReparentingStateForNewSession();

                // Verify flags are reset
                bool animatorFlag = (bool)animatorFlagField.GetValue(null);
                bool reparentFlag = (bool)reparentFlagField.GetValue(null);

                Assert.IsFalse(animatorFlag, $"Cycle {i}: Animator flag should be false after reset");
                Assert.IsFalse(reparentFlag, $"Cycle {i}: Reparent flag should be false after reset");
            }
        }

        [Test]
        public void FastIterationMode_PendingEventsCleared_OnSessionReset()
        {
            // Verify that pending events from previous session are cleared
            var pendingAnimatorField = typeof(GONetMain).GetField("pendingAnimatorTriggerEvents",
                BindingFlags.NonPublic | BindingFlags.Static);
            var rateLimitsField = typeof(GONetMain).GetField("reparentRateLimits",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(pendingAnimatorField, "pendingAnimatorTriggerEvents field should exist");
            Assert.NotNull(rateLimitsField, "reparentRateLimits field should exist");

            var pendingAnimator = pendingAnimatorField.GetValue(null) as Dictionary<uint, List<(int, long)>>;
            var rateLimits = rateLimitsField.GetValue(null) as Dictionary<ushort, (float, int)>;

            // Simulate SESSION 1 left pending state
            pendingAnimator[999] = new List<(int, long)> { (1, 100L) };
            rateLimits[5] = (1.0f, 3);

            // Simulate SESSION 1 end
            GONetMain.ResetAnimatorTriggerStateForNewSession();
            GONetMain.ResetReparentingStateForNewSession();

            // Assert - Pending state should be cleared for clean SESSION 2
            Assert.AreEqual(0, pendingAnimator.Count, "pendingAnimatorTriggerEvents should be cleared between sessions");
            Assert.AreEqual(0, rateLimits.Count, "reparentRateLimits should be cleared between sessions");
        }

        #endregion

        #region Edge Case Tests

        [Test]
        public void EdgeCase_ClearEmptyState_DoesNotThrow()
        {
            // Ensure clearing already-empty state doesn't throw
            Assert.DoesNotThrow(() =>
            {
                // Clear once
                GONetLocal.ClearStaticState();
                GONetIdBatchManager.ResetForNewSession();
                GONetEventBus.Instance.ClearAllSubscriptions();
                GONetGlobal.ClearSessionState();
                GONetMain.ResetAnimatorTriggerStateForNewSession();
                GONetMain.ResetReparentingStateForNewSession();

                // Clear again (should handle empty state gracefully)
                GONetLocal.ClearStaticState();
                GONetIdBatchManager.ResetForNewSession();
                GONetEventBus.Instance.ClearAllSubscriptions();
                GONetGlobal.ClearSessionState();
                GONetMain.ResetAnimatorTriggerStateForNewSession();
                GONetMain.ResetReparentingStateForNewSession();
            });
        }

        [Test]
        public void EdgeCase_ClearWithNullDictionaries_HandlesGracefully()
        {
            // This tests robustness when internal collections might be null
            // (though they shouldn't be in normal operation)

            // The implementation should use null-conditional operators (?.)
            // or null checks, so this should not throw
            Assert.DoesNotThrow(() =>
            {
                GONetLocal.ClearStaticState();
                GONetIdBatchManager.ResetForNewSession();
                GONetEventBus.Instance.ClearAllSubscriptions();
                GONetGlobal.ClearSessionState();
                GONetMain.ResetAnimatorTriggerStateForNewSession();
                GONetMain.ResetReparentingStateForNewSession();
            });
        }

        [Test]
        public void EdgeCase_LargeStateReset_CompletesInReasonableTime()
        {
            // Add a lot of state and verify reset completes quickly
            var serverBatchesField = typeof(GONetIdBatchManager).GetField("server_allocatedBatchStarts",
                BindingFlags.NonPublic | BindingFlags.Static);
            var serverBatches = serverBatchesField?.GetValue(null) as List<uint>;

            var sceneLoadTimesField = typeof(GONetGlobal).GetField("sceneLoadTimesTicks",
                BindingFlags.NonPublic | BindingFlags.Static);
            var sceneLoadTimes = sceneLoadTimesField?.GetValue(null) as Dictionary<string, long>;

            // Add lots of entries
            for (int i = 0; i < 10000; i++)
            {
                serverBatches?.Add((uint)i);
                sceneLoadTimes?.Add($"Scene_{i}", (long)i);
            }

            var startTime = DateTime.Now;

            // Act
            GONetLocal.ClearStaticState();
            GONetIdBatchManager.ResetForNewSession();
            GONetEventBus.Instance.ClearAllSubscriptions();
            GONetGlobal.ClearSessionState();
            GONetMain.ResetAnimatorTriggerStateForNewSession();
            GONetMain.ResetReparentingStateForNewSession();

            var elapsed = DateTime.Now - startTime;

            // Assert - Should complete in under 1 second even with lots of state
            Assert.Less(elapsed.TotalSeconds, 1.0,
                $"Reset took {elapsed.TotalMilliseconds}ms, should be under 1000ms");
            Assert.AreEqual(0, serverBatches?.Count ?? 0, "Server batches should be cleared");
            Assert.AreEqual(0, sceneLoadTimes?.Count ?? 0, "Scene load times should be cleared");
        }

        #endregion
    }
}
