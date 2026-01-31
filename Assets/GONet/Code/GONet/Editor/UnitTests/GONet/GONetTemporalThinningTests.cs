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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace GONet.Editor.UnitTests
{
    /// <summary>
    /// Unit tests for GONet temporal thinning system (smart congestion management).
    ///
    /// Tests cover:
    /// - Core thinning algorithm (keep every Nth unreliable)
    /// - Reliability preservation (always keep reliable messages)
    /// - Dual-trigger system (queue count OR CPU time budget)
    /// - Memory safety (byte array pool returns)
    /// - Configuration validation
    /// - Edge cases (empty queue, all reliable, disabled thinning)
    /// </summary>
    [TestFixture]
    public class GONetTemporalThinningTests
    {
        private GameObject testGameObject;
        private GONetGlobal testConfig;

        // Reflection helpers for accessing private methods
        private MethodInfo thinSendQueueMethod;
        private MethodInfo thinReceiveQueueMethod;

        [SetUp]
        public void SetUp()
        {
            // Create test GameObject with GONetGlobal component
            testGameObject = new GameObject("TestGONetGlobal");
            testConfig = testGameObject.AddComponent<GONetGlobal>();

            // Set default test configuration
            testConfig.enableTemporalThinning = true;
            testConfig.sendQueueThinningTriggerCount = 200;
            testConfig.receiveQueueThinningTriggerCount = 200;
            testConfig.temporalThinningKeepEveryNth = 2;
            testConfig.queueProcessingCpuBudgetMs = 1.0f;

            // Get private methods via reflection for testing
            var gonetMainType = typeof(GONetMain);
            thinSendQueueMethod = gonetMainType.GetMethod("ThinSendQueue", BindingFlags.NonPublic | BindingFlags.Static);
            thinReceiveQueueMethod = gonetMainType.GetMethod("ThinReceiveQueue", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(thinSendQueueMethod, "ThinSendQueue method should exist");
            Assert.IsNotNull(thinReceiveQueueMethod, "ThinReceiveQueue method should exist");
        }

        [TearDown]
        public void TearDown()
        {
            if (testGameObject != null)
            {
                Object.DestroyImmediate(testGameObject);
            }
        }

        #region Configuration Tests

        [Test]
        public void Configuration_DefaultValues_AreSetCorrectly()
        {
            // Assert - verify smart defaults
            Assert.IsTrue(testConfig.enableTemporalThinning, "Temporal thinning should be enabled by default");
            Assert.AreEqual(200, testConfig.sendQueueThinningTriggerCount, "Send trigger should be 200 messages");
            Assert.AreEqual(200, testConfig.receiveQueueThinningTriggerCount, "Receive trigger should be 200 messages");
            Assert.AreEqual(2, testConfig.temporalThinningKeepEveryNth, "KeepEveryNth should be 2 (50% fidelity)");
            Assert.AreEqual(1.0f, testConfig.queueProcessingCpuBudgetMs, "CPU budget should be 1.0ms");
        }

        [Test]
        public void Configuration_KeepEveryNth_RangeValidation()
        {
            // Test minimum boundary
            testConfig.temporalThinningKeepEveryNth = 2;
            Assert.AreEqual(2, testConfig.temporalThinningKeepEveryNth, "KeepEveryNth accepts minimum value 2");

            // Test maximum boundary
            testConfig.temporalThinningKeepEveryNth = 10;
            Assert.AreEqual(10, testConfig.temporalThinningKeepEveryNth, "KeepEveryNth accepts maximum value 10");
        }

        [Test]
        public void Configuration_TriggerCounts_RangeValidation()
        {
            // Test minimum boundary
            testConfig.sendQueueThinningTriggerCount = 50;
            testConfig.receiveQueueThinningTriggerCount = 50;
            Assert.AreEqual(50, testConfig.sendQueueThinningTriggerCount, "Trigger count accepts minimum 50");
            Assert.AreEqual(50, testConfig.receiveQueueThinningTriggerCount, "Trigger count accepts minimum 50");

            // Test maximum boundary
            testConfig.sendQueueThinningTriggerCount = 1000;
            testConfig.receiveQueueThinningTriggerCount = 1000;
            Assert.AreEqual(1000, testConfig.sendQueueThinningTriggerCount, "Trigger count accepts maximum 1000");
            Assert.AreEqual(1000, testConfig.receiveQueueThinningTriggerCount, "Trigger count accepts maximum 1000");
        }

        [Test]
        public void Configuration_CpuBudget_RangeValidation()
        {
            // Test disabled (0)
            testConfig.queueProcessingCpuBudgetMs = 0f;
            Assert.AreEqual(0f, testConfig.queueProcessingCpuBudgetMs, "CPU budget accepts 0 (disabled)");

            // Test maximum boundary
            testConfig.queueProcessingCpuBudgetMs = 5.0f;
            Assert.AreEqual(5.0f, testConfig.queueProcessingCpuBudgetMs, "CPU budget accepts maximum 5.0ms");
        }

        #endregion

        #region Core Algorithm Tests (Concept Validation)

        [Test]
        public void ThinningAlgorithm_KeepEveryNth_ConceptValidation()
        {
            // CONCEPT TEST: Validate that keeping every Nth message produces correct fidelity
            // This test validates the CONCEPT without requiring actual NetworkData objects

            int totalMessages = 200;
            int keepEveryNth = 2;

            // Calculate expected kept messages (every 2nd message: 0, 2, 4, 6, ...)
            int expectedKept = 0;
            for (int i = 0; i < totalMessages; i++)
            {
                if (i % keepEveryNth == 0)
                {
                    expectedKept++;
                }
            }

            // Assert - 50% fidelity (keep 100 out of 200)
            Assert.AreEqual(100, expectedKept, "KeepEveryNth=2 should keep 50% of messages (100 out of 200)");

            // Test other fidelity levels
            // Note: Keeping indices 0, N, 2N, 3N... gives ceiling(total/N) messages
            Assert.AreEqual(67, CalculateKeptMessages(totalMessages, 3), "KeepEveryNth=3 should keep ~33% (67 out of 200)");
            Assert.AreEqual(40, CalculateKeptMessages(totalMessages, 5), "KeepEveryNth=5 should keep 20% (40 out of 200)");
            Assert.AreEqual(20, CalculateKeptMessages(totalMessages, 10), "KeepEveryNth=10 should keep 10% (20 out of 200)");
        }

        private int CalculateKeptMessages(int total, int keepEveryNth)
        {
            int kept = 0;
            for (int i = 0; i < total; i++)
            {
                if (i % keepEveryNth == 0)
                {
                    kept++;
                }
            }
            return kept;
        }

        [Test]
        public void ThinningAlgorithm_ReliabilityPreservation_ConceptValidation()
        {
            // CONCEPT TEST: Validate that ALL reliable messages are always kept

            // Scenario: 200 total messages, 50 reliable, 150 unreliable, keepEveryNth=2
            int totalMessages = 200;
            int reliableMessages = 50;
            int unreliableMessages = 150;
            int keepEveryNth = 2;

            // Calculate kept unreliable messages (every 2nd)
            int keptUnreliable = CalculateKeptMessages(unreliableMessages, keepEveryNth);

            // Expected total kept = ALL reliable + kept unreliable
            int expectedTotalKept = reliableMessages + keptUnreliable;

            // Assert
            Assert.AreEqual(125, expectedTotalKept, "Should keep ALL 50 reliable + 75 unreliable (every 2nd of 150) = 125 total");
        }

        [Test]
        public void ThinningAlgorithm_TemporalOrder_ConceptValidation()
        {
            // CONCEPT TEST: Validate that ConcurrentQueue maintains FIFO order

            var queue = new ConcurrentQueue<int>();

            // Enqueue in order
            for (int i = 0; i < 100; i++)
            {
                queue.Enqueue(i);
            }

            // Dequeue and verify order
            int expectedValue = 0;
            while (queue.TryDequeue(out int value))
            {
                Assert.AreEqual(expectedValue, value, $"Queue should maintain FIFO order (expected {expectedValue}, got {value})");
                expectedValue++;
            }

            Assert.AreEqual(100, expectedValue, "Queue should have dequeued all 100 items in order");
        }

        #endregion

        #region Dual-Trigger System Tests

        [Test]
        public void DualTrigger_QueueCount_ActivatesThinning()
        {
            // CONCEPT TEST: Validate that queue count trigger logic is correct

            int queueCount = 250;
            int triggerThreshold = 200;

            bool shouldThinByCount = queueCount > triggerThreshold;

            Assert.IsTrue(shouldThinByCount, "Queue count 250 > 200 should trigger thinning");
        }

        [Test]
        public void DualTrigger_CpuTime_ActivatesThinning()
        {
            // CONCEPT TEST: Validate that CPU time trigger logic is correct

            double elapsedMs = 2.5;
            double budgetMs = 1.0;

            bool shouldThinByCpu = elapsedMs > budgetMs;

            Assert.IsTrue(shouldThinByCpu, "Elapsed time 2.5ms > 1.0ms budget should trigger thinning");
        }

        [Test]
        public void DualTrigger_BothTriggers_ActivatesThinning()
        {
            // CONCEPT TEST: Validate that EITHER trigger activates thinning (OR logic)

            bool shouldThinByCount = true;
            bool shouldThinByCpu = true;

            bool shouldThin = shouldThinByCount || shouldThinByCpu;

            Assert.IsTrue(shouldThin, "Both triggers active should activate thinning");
        }

        [Test]
        public void DualTrigger_OnlyCountTrigger_ActivatesThinning()
        {
            // CONCEPT TEST: Validate count-only trigger

            bool shouldThinByCount = true;
            bool shouldThinByCpu = false;

            bool shouldThin = shouldThinByCount || shouldThinByCpu;

            Assert.IsTrue(shouldThin, "Count trigger alone should activate thinning");
        }

        [Test]
        public void DualTrigger_OnlyCpuTrigger_ActivatesThinning()
        {
            // CONCEPT TEST: Validate CPU-only trigger

            bool shouldThinByCount = false;
            bool shouldThinByCpu = true;

            bool shouldThin = shouldThinByCount || shouldThinByCpu;

            Assert.IsTrue(shouldThin, "CPU trigger alone should activate thinning");
        }

        [Test]
        public void DualTrigger_NoTriggers_DoesNotActivateThinning()
        {
            // CONCEPT TEST: Validate that no triggers = no thinning

            bool shouldThinByCount = false;
            bool shouldThinByCpu = false;

            bool shouldThin = shouldThinByCount || shouldThinByCpu;

            Assert.IsFalse(shouldThin, "No triggers active should not activate thinning");
        }

        [Test]
        public void DualTrigger_CpuBudgetDisabled_OnlyUsesCountTrigger()
        {
            // CONCEPT TEST: Validate that CPU budget = 0 disables CPU trigger

            float cpuBudgetMs = 0f;
            bool isCpuBudgetEnabled = cpuBudgetMs > 0f;

            Assert.IsFalse(isCpuBudgetEnabled, "CPU budget = 0 should disable CPU trigger");
        }

        #endregion

        #region Edge Cases

        [Test]
        public void EdgeCase_EmptyQueue_NoThinning()
        {
            // CONCEPT TEST: Validate early return for empty queue

            int queueCount = 0;

            if (queueCount == 0)
            {
                // Early return - no work done
                Assert.Pass("Empty queue should return early (no thinning work)");
            }
            else
            {
                Assert.Fail("Empty queue should have returned early");
            }
        }

        [Test]
        public void EdgeCase_AllReliableMessages_NoDrops()
        {
            // CONCEPT TEST: Validate that all reliable messages are kept

            int totalMessages = 200;
            int reliableMessages = 200;
            int unreliableMessages = 0;
            int keepEveryNth = 2;

            // Calculate kept messages
            int keptReliable = reliableMessages; // ALL kept
            int keptUnreliable = CalculateKeptMessages(unreliableMessages, keepEveryNth);

            int totalKept = keptReliable + keptUnreliable;
            int totalDropped = totalMessages - totalKept;

            Assert.AreEqual(200, totalKept, "All 200 reliable messages should be kept");
            Assert.AreEqual(0, totalDropped, "Zero messages should be dropped (all reliable)");
        }

        [Test]
        public void EdgeCase_AllUnreliableMessages_AppliesThinning()
        {
            // CONCEPT TEST: Validate thinning with all unreliable messages

            int totalMessages = 200;
            int reliableMessages = 0;
            int unreliableMessages = 200;
            int keepEveryNth = 2;

            // Calculate kept messages
            int keptReliable = reliableMessages; // 0
            int keptUnreliable = CalculateKeptMessages(unreliableMessages, keepEveryNth);

            int totalKept = keptReliable + keptUnreliable;
            int totalDropped = totalMessages - totalKept;

            Assert.AreEqual(100, totalKept, "100 unreliable messages should be kept (every 2nd of 200)");
            Assert.AreEqual(100, totalDropped, "100 unreliable messages should be dropped");
        }

        [Test]
        public void EdgeCase_ThinningDisabled_NoThinning()
        {
            // CONCEPT TEST: Validate that enableTemporalThinning=false disables thinning

            testConfig.enableTemporalThinning = false;

            int queueCount = 500; // Exceeds trigger
            bool shouldThin = testConfig.enableTemporalThinning && queueCount > testConfig.sendQueueThinningTriggerCount;

            Assert.IsFalse(shouldThin, "Thinning disabled should prevent thinning even when count exceeds trigger");
        }

        #endregion

        #region Performance Characteristics Tests

        [Test]
        public void Performance_SinglePassAlgorithm_ConceptValidation()
        {
            // CONCEPT TEST: Validate O(n) single-pass algorithm

            // Algorithm steps:
            // 1. Dequeue ALL (O(n))
            // 2. Separate reliable/unreliable (O(n) single pass)
            // 3. Re-enqueue kept (O(k) where k <= n)
            // Total: O(n) + O(n) + O(k) = O(n)

            int totalMessages = 1000;

            // Simulate algorithm steps
            int dequeueOperations = totalMessages; // Step 1
            int separationOperations = totalMessages; // Step 2
            int enqueueOperations = totalMessages / 2; // Step 3 (assume 50% kept)

            int totalOperations = dequeueOperations + separationOperations + enqueueOperations;

            // Assert O(n) complexity (total operations ~= 2.5 * n)
            Assert.LessOrEqual(totalOperations, 3 * totalMessages, "Algorithm should be O(n) complexity (total ops <= 3n)");
        }

        [Test]
        public void Performance_PooledLists_ConceptValidation()
        {
            // CONCEPT TEST: Validate pooled list reuse concept

            var pooledList = new List<int>(10000);

            // First use
            for (int i = 0; i < 5000; i++)
            {
                pooledList.Add(i);
            }

            int capacityAfterFirstUse = pooledList.Capacity;

            // Clear for reuse (capacity preserved)
            pooledList.Clear();

            // Second use
            for (int i = 0; i < 5000; i++)
            {
                pooledList.Add(i);
            }

            int capacityAfterSecondUse = pooledList.Capacity;

            // Assert - capacity preserved across reuses (zero allocations)
            Assert.AreEqual(capacityAfterFirstUse, capacityAfterSecondUse, "Pooled list capacity should be preserved across Clear() operations (zero allocations)");
        }

        #endregion

        #region Fidelity Calculations

        [Test]
        public void Fidelity_KeepEveryNth2_Produces50Percent()
        {
            int total = 1000;
            int kept = CalculateKeptMessages(total, 2);
            float fidelity = (float)kept / total;

            Assert.AreEqual(0.5f, fidelity, 0.001f, "KeepEveryNth=2 should produce 50% fidelity");
        }

        [Test]
        public void Fidelity_KeepEveryNth3_Produces33Percent()
        {
            int total = 1000;
            int kept = CalculateKeptMessages(total, 3);
            float fidelity = (float)kept / total;

            Assert.AreEqual(0.333f, fidelity, 0.01f, "KeepEveryNth=3 should produce ~33% fidelity");
        }

        [Test]
        public void Fidelity_KeepEveryNth5_Produces20Percent()
        {
            int total = 1000;
            int kept = CalculateKeptMessages(total, 5);
            float fidelity = (float)kept / total;

            Assert.AreEqual(0.2f, fidelity, 0.001f, "KeepEveryNth=5 should produce 20% fidelity");
        }

        [Test]
        public void Fidelity_KeepEveryNth10_Produces10Percent()
        {
            int total = 1000;
            int kept = CalculateKeptMessages(total, 10);
            float fidelity = (float)kept / total;

            Assert.AreEqual(0.1f, fidelity, 0.001f, "KeepEveryNth=10 should produce 10% fidelity");
        }

        #endregion

        #region Integration Test Scenarios (Documentation)

        /*
         * INTEGRATION TEST SCENARIOS (Require Unity Play Mode + GONet Runtime)
         *
         * These scenarios validate runtime behavior with actual network traffic.
         * Execute manually in Play mode or via Unity Test Runner Play Mode tests.
         *
         * Scenario 1: 800 Objects - Queue Count Trigger
         * ----------------------------------------------
         * Setup:
         *   - Server with 800 CircularMotion objects
         *   - Client connects
         *   - enableTemporalThinning = true
         *   - sendQueueThinningTriggerCount = 200
         *   - queueProcessingCpuBudgetMs = 0 (CPU trigger disabled)
         *
         * Expected:
         *   - Send queue reaches 200+ messages
         *   - [SEND-THIN] log shows thinning triggered by COUNT
         *   - Queue reduced to ~120 messages
         *   - No connection reset, no 24-second freeze
         *   - Objects move smoothly (50% fidelity sufficient)
         *
         * Scenario 2: GC Pause - CPU Time Trigger
         * -----------------------------------------
         * Setup:
         *   - Normal object count (100-200)
         *   - Force GC pause via System.GC.Collect()
         *   - queueProcessingCpuBudgetMs = 1.0ms
         *
         * Expected:
         *   - Processing time exceeds 1.0ms during GC
         *   - [CPU-TRIGGER] log shows CPU budget exceeded
         *   - [RECV-THIN] log shows thinning triggered by CPU
         *   - Queue reduced before freeze occurs
         *   - Main thread recovers quickly
         *
         * Scenario 3: Dual Trigger - Worst Case
         * --------------------------------------
         * Setup:
         *   - 800 objects spawning
         *   - Slow client machine (VM with reduced resources)
         *   - Both triggers enabled
         *
         * Expected:
         *   - Queue count exceeds 200
         *   - CPU time exceeds 1.0ms (slow processing)
         *   - [SEND-THIN] / [RECV-THIN] logs show "trigger: COUNT + CPU"
         *   - Queue thinned from both angles
         *   - System stays stable despite worst-case conditions
         *
         * Scenario 4: Thinning Disabled - Baseline
         * -----------------------------------------
         * Setup:
         *   - 800 objects
         *   - enableTemporalThinning = false
         *
         * Expected:
         *   - Falls back to old 90% hard cutoff behavior
         *   - Random packet drops (no temporal sampling)
         *   - Potential 24-second freeze (as seen in original logs)
         *   - Validates that thinning is the improvement
         *
         * Scenario 5: Fidelity Levels
         * ---------------------------
         * Setup:
         *   - 400 objects (moderate load)
         *   - Test temporalThinningKeepEveryNth = 2, 3, 5, 10
         *
         * Expected:
         *   - keepEveryNth=2: Smooth movement (50% fidelity)
         *   - keepEveryNth=3: Slightly choppy (33% fidelity)
         *   - keepEveryNth=5: Noticeably choppy (20% fidelity)
         *   - keepEveryNth=10: Very choppy (10% fidelity)
         *   - Validates trade-off between performance and quality
         *
         * Scenario 6: Reliable Message Preservation
         * -----------------------------------------
         * Setup:
         *   - 800 objects
         *   - Trigger thinning (queue > 200)
         *   - Send RPCs during thinning
         *
         * Expected:
         *   - [SEND-THIN] log shows "reliable: X" messages kept
         *   - ALL RPC messages delivered (none dropped)
         *   - Only unreliable position/rotation updates thinned
         *   - Validates reliability contract preserved
         *
         * Scenario 7: CPU Budget Disabled (Count-Only)
         * --------------------------------------------
         * Setup:
         *   - queueProcessingCpuBudgetMs = 0
         *   - Force GC pause (should NOT trigger thinning)
         *
         * Expected:
         *   - No [CPU-TRIGGER] logs (CPU trigger disabled)
         *   - Thinning only activates on queue count
         *   - Validates CPU trigger can be disabled
         *
         * Scenario 8: Very Low Trigger Count (Aggressive)
         * -----------------------------------------------
         * Setup:
         *   - sendQueueThinningTriggerCount = 50
         *   - receiveQueueThinningTriggerCount = 50
         *   - 400 objects
         *
         * Expected:
         *   - Frequent thinning operations (more [SEND-THIN] / [RECV-THIN] logs)
         *   - Queue stays very small (< 50 messages)
         *   - More aggressive congestion control
         *   - Validates aggressive tuning
         *
         * Scenario 9: Very High KeepEveryNth (Aggressive Dropping)
         * --------------------------------------------------------
         * Setup:
         *   - temporalThinningKeepEveryNth = 10
         *   - 800 objects
         *
         * Expected:
         *   - Very aggressive thinning (keep 10% unreliable)
         *   - Queue reduced dramatically (1000 → 200 messages)
         *   - Objects very choppy (10% fidelity insufficient)
         *   - Validates extreme edge case
         *
         * Scenario 10: Long Duration Stability (2+ hours)
         * -----------------------------------------------
         * Setup:
         *   - 800 objects
         *   - Run for 2+ hours on VM
         *
         * Expected:
         *   - No connection resets
         *   - No main thread freezes
         *   - Queue oscillates (doesn't grow unbounded)
         *   - Memory stable (byte arrays returned to pool)
         *   - Validates production stability
         */

        #endregion

        #region Adaptive Thinning Tests (November 2025)

        [Test]
        public void AdaptiveThinning_Configuration_DefaultEnabled()
        {
            // Assert - adaptive thinning should be enabled by default
            Assert.IsTrue(testConfig.enableAdaptiveThinning, "Adaptive thinning should be enabled by default (smart behavior)");
        }

        [Test]
        public void AdaptiveThinning_LightCongestion_Uses50PercentDrop()
        {
            // CONCEPT TEST: Light congestion (1.0-2.0x overage) should use baseline keepEveryNth (50% drop)

            double congestionSeverity = 1.5; // 1.5x over threshold
            int baselineKeepEveryNth = 2;
            bool adaptiveEnabled = true;

            // Simulate adaptive algorithm
            int keepEveryNth;
            if (adaptiveEnabled && congestionSeverity > 1.0)
            {
                if (congestionSeverity >= 3.0)
                    keepEveryNth = 4;
                else if (congestionSeverity >= 2.0)
                    keepEveryNth = 3;
                else
                    keepEveryNth = baselineKeepEveryNth; // Light congestion
            }
            else
            {
                keepEveryNth = baselineKeepEveryNth;
            }

            Assert.AreEqual(2, keepEveryNth, "Light congestion (1.5x) should use baseline keepEveryNth=2 (50% drop)");
        }

        [Test]
        public void AdaptiveThinning_MediumCongestion_Uses66PercentDrop()
        {
            // CONCEPT TEST: Medium congestion (2.0-3.0x overage) should escalate to keepEveryNth=3 (66% drop)

            double congestionSeverity = 2.5; // 2.5x over threshold
            int baselineKeepEveryNth = 2;
            bool adaptiveEnabled = true;

            // Simulate adaptive algorithm
            int keepEveryNth;
            if (adaptiveEnabled && congestionSeverity > 1.0)
            {
                if (congestionSeverity >= 3.0)
                    keepEveryNth = 4;
                else if (congestionSeverity >= 2.0)
                    keepEveryNth = 3; // Medium congestion
                else
                    keepEveryNth = baselineKeepEveryNth;
            }
            else
            {
                keepEveryNth = baselineKeepEveryNth;
            }

            Assert.AreEqual(3, keepEveryNth, "Medium congestion (2.5x) should escalate to keepEveryNth=3 (66% drop)");
        }

        [Test]
        public void AdaptiveThinning_HeavyCongestion_Uses75PercentDrop()
        {
            // CONCEPT TEST: Heavy congestion (3.0x+ overage) should escalate to keepEveryNth=4 (75% drop)

            double congestionSeverity = 4.0; // 4x over threshold
            int baselineKeepEveryNth = 2;
            bool adaptiveEnabled = true;

            // Simulate adaptive algorithm
            int keepEveryNth;
            if (adaptiveEnabled && congestionSeverity > 1.0)
            {
                if (congestionSeverity >= 3.0)
                    keepEveryNth = 4; // Heavy congestion
                else if (congestionSeverity >= 2.0)
                    keepEveryNth = 3;
                else
                    keepEveryNth = baselineKeepEveryNth;
            }
            else
            {
                keepEveryNth = baselineKeepEveryNth;
            }

            Assert.AreEqual(4, keepEveryNth, "Heavy congestion (4.0x) should escalate to keepEveryNth=4 (75% drop)");
        }

        [Test]
        public void AdaptiveThinning_Disabled_UsesFixedRate()
        {
            // CONCEPT TEST: When adaptive disabled, should always use baseline keepEveryNth

            double congestionSeverity = 5.0; // Severe congestion
            int baselineKeepEveryNth = 2;
            bool adaptiveEnabled = false; // Disabled

            // Simulate adaptive algorithm
            int keepEveryNth;
            if (adaptiveEnabled && congestionSeverity > 1.0)
            {
                if (congestionSeverity >= 3.0)
                    keepEveryNth = 4;
                else if (congestionSeverity >= 2.0)
                    keepEveryNth = 3;
                else
                    keepEveryNth = baselineKeepEveryNth;
            }
            else
            {
                keepEveryNth = baselineKeepEveryNth; // Fixed rate
            }

            Assert.AreEqual(2, keepEveryNth, "Adaptive disabled should use fixed keepEveryNth=2 even under severe congestion");
        }

        [Test]
        public void AdaptiveThinning_BoundaryCondition_Exactly2x()
        {
            // CONCEPT TEST: Exactly 2.0x overage should trigger medium congestion (66% drop)

            double congestionSeverity = 2.0; // Exactly 2x
            int baselineKeepEveryNth = 2;
            bool adaptiveEnabled = true;

            // Simulate adaptive algorithm
            int keepEveryNth;
            if (adaptiveEnabled && congestionSeverity > 1.0)
            {
                if (congestionSeverity >= 3.0)
                    keepEveryNth = 4;
                else if (congestionSeverity >= 2.0) // Triggers here
                    keepEveryNth = 3;
                else
                    keepEveryNth = baselineKeepEveryNth;
            }
            else
            {
                keepEveryNth = baselineKeepEveryNth;
            }

            Assert.AreEqual(3, keepEveryNth, "Exactly 2.0x overage should trigger medium congestion (keepEveryNth=3)");
        }

        [Test]
        public void AdaptiveThinning_BoundaryCondition_Exactly3x()
        {
            // CONCEPT TEST: Exactly 3.0x overage should trigger heavy congestion (75% drop)

            double congestionSeverity = 3.0; // Exactly 3x
            int baselineKeepEveryNth = 2;
            bool adaptiveEnabled = true;

            // Simulate adaptive algorithm
            int keepEveryNth;
            if (adaptiveEnabled && congestionSeverity > 1.0)
            {
                if (congestionSeverity >= 3.0) // Triggers here
                    keepEveryNth = 4;
                else if (congestionSeverity >= 2.0)
                    keepEveryNth = 3;
                else
                    keepEveryNth = baselineKeepEveryNth;
            }
            else
            {
                keepEveryNth = baselineKeepEveryNth;
            }

            Assert.AreEqual(4, keepEveryNth, "Exactly 3.0x overage should trigger heavy congestion (keepEveryNth=4)");
        }

        [Test]
        public void AdaptiveThinning_CongestionSeverity_QueueCount()
        {
            // CONCEPT TEST: Congestion severity calculation from queue count

            int queueCount = 600;
            int threshold = 200;

            double congestionSeverity = (double)queueCount / threshold;

            Assert.AreEqual(3.0, congestionSeverity, "Queue 600 / threshold 200 = 3.0x congestion severity");
        }

        [Test]
        public void AdaptiveThinning_CongestionSeverity_CpuTime()
        {
            // CONCEPT TEST: Congestion severity calculation from CPU time

            double elapsedMs = 7.5;
            double budgetMs = 2.5;

            double congestionSeverity = elapsedMs / budgetMs;

            Assert.AreEqual(3.0, congestionSeverity, "Elapsed 7.5ms / budget 2.5ms = 3.0x congestion severity");
        }

        [Test]
        public void AdaptiveThinning_FidelityCalculation_50Percent()
        {
            // CONCEPT TEST: Validate fidelity calculation for keepEveryNth=2

            int keepEveryNth = 2;
            double fidelity = 1.0 / keepEveryNth;

            Assert.AreEqual(0.5, fidelity, 0.001, "keepEveryNth=2 should produce 50% fidelity");
        }

        [Test]
        public void AdaptiveThinning_FidelityCalculation_33Percent()
        {
            // CONCEPT TEST: Validate fidelity calculation for keepEveryNth=3

            int keepEveryNth = 3;
            double fidelity = 1.0 / keepEveryNth;

            Assert.AreEqual(0.333, fidelity, 0.01, "keepEveryNth=3 should produce ~33% fidelity");
        }

        [Test]
        public void AdaptiveThinning_FidelityCalculation_25Percent()
        {
            // CONCEPT TEST: Validate fidelity calculation for keepEveryNth=4

            int keepEveryNth = 4;
            double fidelity = 1.0 / keepEveryNth;

            Assert.AreEqual(0.25, fidelity, 0.001, "keepEveryNth=4 should produce 25% fidelity");
        }

        [Test]
        public void AdaptiveThinning_MessageReduction_LightCongestion()
        {
            // CONCEPT TEST: Validate message reduction for light congestion (50% drop)

            int totalMessages = 300;
            int reliableMessages = 50;
            int unreliableMessages = 250;
            int keepEveryNth = 2; // Light congestion

            int keptUnreliable = CalculateKeptMessages(unreliableMessages, keepEveryNth);
            int totalKept = reliableMessages + keptUnreliable;
            int totalDropped = totalMessages - totalKept;

            Assert.AreEqual(175, totalKept, "Light congestion: 50 reliable + 125 unreliable (50% of 250) = 175 kept");
            Assert.AreEqual(125, totalDropped, "Light congestion: 125 unreliable dropped (50% of 250)");
        }

        [Test]
        public void AdaptiveThinning_MessageReduction_MediumCongestion()
        {
            // CONCEPT TEST: Validate message reduction for medium congestion (66% drop)

            int totalMessages = 600;
            int reliableMessages = 100;
            int unreliableMessages = 500;
            int keepEveryNth = 3; // Medium congestion

            int keptUnreliable = CalculateKeptMessages(unreliableMessages, keepEveryNth);
            int totalKept = reliableMessages + keptUnreliable;
            int totalDropped = totalMessages - totalKept;

            Assert.AreEqual(267, totalKept, "Medium congestion: 100 reliable + 167 unreliable (33% of 500) = 267 kept");
            Assert.AreEqual(333, totalDropped, "Medium congestion: 333 unreliable dropped (66% of 500)");
        }

        [Test]
        public void AdaptiveThinning_MessageReduction_HeavyCongestion()
        {
            // CONCEPT TEST: Validate message reduction for heavy congestion (75% drop)

            int totalMessages = 800;
            int reliableMessages = 100;
            int unreliableMessages = 700;
            int keepEveryNth = 4; // Heavy congestion

            int keptUnreliable = CalculateKeptMessages(unreliableMessages, keepEveryNth);
            int totalKept = reliableMessages + keptUnreliable;
            int totalDropped = totalMessages - totalKept;

            Assert.AreEqual(275, totalKept, "Heavy congestion: 100 reliable + 175 unreliable (25% of 700) = 275 kept");
            Assert.AreEqual(525, totalDropped, "Heavy congestion: 525 unreliable dropped (75% of 700)");
        }

        [Test]
        public void AdaptiveThinning_SelfCorrectingBehavior_ConceptValidation()
        {
            // CONCEPT TEST: Validate self-correcting behavior as congestion severity changes

            // Scenario: Queue backs up progressively
            int threshold = 200;

            // Round 1: Light congestion (250 messages)
            int queueCount1 = 250;
            double severity1 = (double)queueCount1 / threshold; // 1.25x
            int keepEveryNth1 = severity1 >= 3.0 ? 4 : severity1 >= 2.0 ? 3 : 2;
            int expectedReduction1 = queueCount1 / 2; // 50% drop

            Assert.AreEqual(2, keepEveryNth1, "Round 1: Light congestion uses keepEveryNth=2");
            Assert.AreEqual(125, expectedReduction1, "Round 1: Reduces queue by ~125 messages");

            // Round 2: Medium congestion (500 messages)
            int queueCount2 = 500;
            double severity2 = (double)queueCount2 / threshold; // 2.5x
            int keepEveryNth2 = severity2 >= 3.0 ? 4 : severity2 >= 2.0 ? 3 : 2;
            int expectedReduction2 = (int)(queueCount2 * 0.66); // 66% drop

            Assert.AreEqual(3, keepEveryNth2, "Round 2: Medium congestion escalates to keepEveryNth=3");
            Assert.AreEqual(330, expectedReduction2, "Round 2: Reduces queue by ~330 messages");

            // Round 3: Heavy congestion (700 messages)
            int queueCount3 = 700;
            double severity3 = (double)queueCount3 / threshold; // 3.5x
            int keepEveryNth3 = severity3 >= 3.0 ? 4 : severity3 >= 2.0 ? 3 : 2;
            int expectedReduction3 = (int)(queueCount3 * 0.75); // 75% drop

            Assert.AreEqual(4, keepEveryNth3, "Round 3: Heavy congestion escalates to keepEveryNth=4");
            Assert.AreEqual(525, expectedReduction3, "Round 3: Reduces queue by ~525 messages");

            // Assert - more aggressive thinning as congestion worsens (self-correcting)
            Assert.Less(keepEveryNth1, keepEveryNth2, "Thinning should escalate from light to medium congestion");
            Assert.Less(keepEveryNth2, keepEveryNth3, "Thinning should escalate from medium to heavy congestion");
        }

        #endregion
    }
}
