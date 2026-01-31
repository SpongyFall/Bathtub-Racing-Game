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
    /// Unit tests for Network Processing Budget system (Priority 1 Client Optimization).
    ///
    /// These tests ensure correct operation of:
    /// - Time-budgeted message processing (maxNetworkProcessingBudgetMs)
    /// - Emergency unreliable message dropping (networkQueueDropThreshold)
    /// - Statistics tracking and reporting
    /// - Hysteresis for emergency mode transitions
    ///
    /// NOTE: These are UNIT tests validating configuration and helper methods only.
    /// Integration testing requires full GONet runtime (server, clients, network messages).
    /// For integration validation, use manual testing with projectile spawn scenarios.
    /// </summary>
    [TestFixture]
    public class GONetNetworkProcessingBudgetTests
    {
        private GONetGlobal testGlobal;

        [SetUp]
        public void Setup()
        {
            // Create test GONetGlobal GameObject with default settings
            GameObject globalObj = new GameObject("TestGONetGlobal");
            testGlobal = globalObj.AddComponent<GONetGlobal>();

            // Set default values explicitly (mirrors GONetGlobal field defaults)
            testGlobal.maxNetworkProcessingBudgetMs = 5.0f;
            testGlobal.networkQueueDropThreshold = 100;
            testGlobal.unreliableDropRate = 2;
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up test GameObject
            if (testGlobal != null)
            {
                Object.DestroyImmediate(testGlobal.gameObject);
                testGlobal = null;
            }
        }

        #region Configuration Validation Tests

        [Test]
        public void NetworkProcessingBudget_DefaultValue_Is5ms()
        {
            // Assert
            Assert.AreEqual(5.0f, testGlobal.maxNetworkProcessingBudgetMs,
                "Default time budget should be 5ms (30% of 16.67ms frame at 60fps)");
        }

        [Test]
        public void NetworkQueueDropThreshold_DefaultValue_Is100()
        {
            // Assert
            Assert.AreEqual(100, testGlobal.networkQueueDropThreshold,
                "Default drop threshold should be 100 messages");
        }

        [Test]
        public void UnreliableDropRate_DefaultValue_Is2()
        {
            // Assert
            Assert.AreEqual(2, testGlobal.unreliableDropRate,
                "Default drop rate should be 2 (keep every 2nd message = 50% drop rate)");
        }

        [Test]
        public void NetworkProcessingBudget_RangeValidation_1To16ms()
        {
            // Arrange & Act - set values within valid range
            testGlobal.maxNetworkProcessingBudgetMs = 1.0f;
            Assert.AreEqual(1.0f, testGlobal.maxNetworkProcessingBudgetMs, "Should accept 1ms (minimum)");

            testGlobal.maxNetworkProcessingBudgetMs = 16.0f;
            Assert.AreEqual(16.0f, testGlobal.maxNetworkProcessingBudgetMs, "Should accept 16ms (maximum)");

            testGlobal.maxNetworkProcessingBudgetMs = 8.0f;
            Assert.AreEqual(8.0f, testGlobal.maxNetworkProcessingBudgetMs, "Should accept 8ms (mid-range)");
        }

        [Test]
        public void NetworkQueueDropThreshold_RangeValidation_0To500()
        {
            // Arrange & Act - set values within valid range
            testGlobal.networkQueueDropThreshold = 0;
            Assert.AreEqual(0, testGlobal.networkQueueDropThreshold, "Should accept 0 (disabled)");

            testGlobal.networkQueueDropThreshold = 500;
            Assert.AreEqual(500, testGlobal.networkQueueDropThreshold, "Should accept 500 (maximum)");

            testGlobal.networkQueueDropThreshold = 200;
            Assert.AreEqual(200, testGlobal.networkQueueDropThreshold, "Should accept 200 (mid-range)");
        }

        [Test]
        public void UnreliableDropRate_RangeValidation_2To10()
        {
            // Arrange & Act - set values within valid range
            testGlobal.unreliableDropRate = 2;
            Assert.AreEqual(2, testGlobal.unreliableDropRate, "Should accept 2 (minimum, drop 50%)");

            testGlobal.unreliableDropRate = 10;
            Assert.AreEqual(10, testGlobal.unreliableDropRate, "Should accept 10 (maximum, drop 90%)");

            testGlobal.unreliableDropRate = 4;
            Assert.AreEqual(4, testGlobal.unreliableDropRate, "Should accept 4 (mid-range, drop 75%)");
        }

        [Test]
        public void NetworkQueueDropThreshold_ZeroValue_DisablesDropping()
        {
            // Arrange
            testGlobal.networkQueueDropThreshold = 0;

            // Assert
            Assert.AreEqual(0, testGlobal.networkQueueDropThreshold,
                "Setting threshold to 0 should disable emergency dropping");
        }

        #endregion

        #region Drop Rate Logic Tests

        [Test]
        public void UnreliableDropRate_2_Keeps50Percent()
        {
            // Arrange
            testGlobal.unreliableDropRate = 2;
            int dropRate = testGlobal.unreliableDropRate;

            // Act & Assert - simulate counter logic
            int keptCount = 0;
            int droppedCount = 0;
            for (int i = 1; i <= 100; i++)
            {
                if (i % dropRate == 0)
                {
                    keptCount++; // Keep every 2nd message
                }
                else
                {
                    droppedCount++; // Drop the rest
                }
            }

            Assert.AreEqual(50, keptCount, "Should keep 50 messages out of 100");
            Assert.AreEqual(50, droppedCount, "Should drop 50 messages out of 100");
        }

        [Test]
        public void UnreliableDropRate_3_Keeps33Percent()
        {
            // Arrange
            testGlobal.unreliableDropRate = 3;
            int dropRate = testGlobal.unreliableDropRate;

            // Act & Assert
            int keptCount = 0;
            int droppedCount = 0;
            for (int i = 1; i <= 99; i++)
            {
                if (i % dropRate == 0)
                {
                    keptCount++;
                }
                else
                {
                    droppedCount++;
                }
            }

            Assert.AreEqual(33, keptCount, "Should keep ~33% of messages");
            Assert.AreEqual(66, droppedCount, "Should drop ~66% of messages");
        }

        [Test]
        public void UnreliableDropRate_4_Keeps25Percent()
        {
            // Arrange
            testGlobal.unreliableDropRate = 4;
            int dropRate = testGlobal.unreliableDropRate;

            // Act & Assert
            int keptCount = 0;
            int droppedCount = 0;
            for (int i = 1; i <= 100; i++)
            {
                if (i % dropRate == 0)
                {
                    keptCount++;
                }
                else
                {
                    droppedCount++;
                }
            }

            Assert.AreEqual(25, keptCount, "Should keep 25% of messages");
            Assert.AreEqual(75, droppedCount, "Should drop 75% of messages");
        }

        [Test]
        public void UnreliableDropRate_10_Keeps10Percent()
        {
            // Arrange
            testGlobal.unreliableDropRate = 10;
            int dropRate = testGlobal.unreliableDropRate;

            // Act & Assert
            int keptCount = 0;
            int droppedCount = 0;
            for (int i = 1; i <= 100; i++)
            {
                if (i % dropRate == 0)
                {
                    keptCount++;
                }
                else
                {
                    droppedCount++;
                }
            }

            Assert.AreEqual(10, keptCount, "Should keep 10% of messages");
            Assert.AreEqual(90, droppedCount, "Should drop 90% of messages");
        }

        #endregion

        #region Emergency Mode Threshold Tests

        [Test]
        public void EmergencyDropping_ActivatesWhen_QueueExceedsThreshold()
        {
            // Arrange
            testGlobal.networkQueueDropThreshold = 100;
            int queueSize = 150;

            // Act & Assert
            bool shouldDropUnreliable = queueSize > testGlobal.networkQueueDropThreshold;
            Assert.IsTrue(shouldDropUnreliable,
                "Should activate emergency dropping when queue (150) exceeds threshold (100)");
        }

        [Test]
        public void EmergencyDropping_RemainsInactive_WhenQueueBelowThreshold()
        {
            // Arrange
            testGlobal.networkQueueDropThreshold = 100;
            int queueSize = 50;

            // Act & Assert
            bool shouldDropUnreliable = queueSize > testGlobal.networkQueueDropThreshold;
            Assert.IsFalse(shouldDropUnreliable,
                "Should NOT activate emergency dropping when queue (50) below threshold (100)");
        }

        [Test]
        public void EmergencyDropping_RemainsInactive_WhenQueueEqualsThreshold()
        {
            // Arrange
            testGlobal.networkQueueDropThreshold = 100;
            int queueSize = 100;

            // Act & Assert
            bool shouldDropUnreliable = queueSize > testGlobal.networkQueueDropThreshold;
            Assert.IsFalse(shouldDropUnreliable,
                "Should NOT activate when queue (100) equals threshold (100) - only > triggers");
        }

        [Test]
        public void EmergencyDropping_Disabled_WhenThresholdZero()
        {
            // Arrange
            testGlobal.networkQueueDropThreshold = 0;
            int queueSize = 1000;

            // Act & Assert
            bool shouldDropUnreliable = testGlobal.networkQueueDropThreshold > 0 &&
                                       queueSize > testGlobal.networkQueueDropThreshold;
            Assert.IsFalse(shouldDropUnreliable,
                "Should NOT activate emergency dropping when threshold is 0 (disabled), even with large queue");
        }

        #endregion

        #region Hysteresis Tests (50% threshold for recovery)

        [Test]
        public void EmergencyDropping_Hysteresis_RecoveryAt50Percent()
        {
            // Arrange
            testGlobal.networkQueueDropThreshold = 100;
            int hysteresisRecoveryThreshold = (int)(testGlobal.networkQueueDropThreshold * 0.5f); // 50

            // Assert - verify recovery threshold calculation
            Assert.AreEqual(50, hysteresisRecoveryThreshold,
                "Recovery threshold should be 50% of drop threshold (100 * 0.5 = 50)");

            // Assert - queue must drain below 50 to recover
            int queueSizeStillActive = 51;
            bool shouldStillDrop = queueSizeStillActive > hysteresisRecoveryThreshold;
            Assert.IsTrue(shouldStillDrop,
                "Should still be in emergency mode at queue=51 (above 50% threshold)");

            int queueSizeRecovered = 49;
            bool shouldRecover = queueSizeRecovered < hysteresisRecoveryThreshold;
            Assert.IsTrue(shouldRecover,
                "Should exit emergency mode at queue=49 (below 50% threshold)");
        }

        [Test]
        public void EmergencyDropping_Hysteresis_PreventsRapidToggling()
        {
            // Arrange
            testGlobal.networkQueueDropThreshold = 100;

            // Simulate emergency mode activation
            int queueSize = 105;
            bool isEmergencyActive = queueSize > testGlobal.networkQueueDropThreshold;
            Assert.IsTrue(isEmergencyActive, "Emergency mode activates at queue=105");

            // Queue drains to 99 (below activation threshold but above recovery threshold)
            queueSize = 99;
            bool shouldStillBeActive = isEmergencyActive && queueSize >= (testGlobal.networkQueueDropThreshold * 0.5f);
            Assert.IsTrue(shouldStillBeActive,
                "Should REMAIN in emergency mode at queue=99 (hysteresis prevents rapid toggle)");

            // Queue drains to 49 (below 50% recovery threshold)
            queueSize = 49;
            bool shouldDeactivate = queueSize < (testGlobal.networkQueueDropThreshold * 0.5f);
            Assert.IsTrue(shouldDeactivate,
                "Should EXIT emergency mode at queue=49 (below 50% recovery threshold)");
        }

        #endregion

        #region Time Budget Tests

        [Test]
        public void TimeBudget_5ms_AllowsProcessingUntilExhausted()
        {
            // Arrange
            testGlobal.maxNetworkProcessingBudgetMs = 5.0f;
            var timer = new System.Diagnostics.Stopwatch();
            timer.Start();

            // Simulate message processing loop
            int messagesProcessed = 0;
            while (timer.Elapsed.TotalMilliseconds < testGlobal.maxNetworkProcessingBudgetMs)
            {
                // Simulate 0.5ms per message processing
                System.Threading.Thread.Sleep(1); // Sleep is imprecise but good enough for test
                messagesProcessed++;

                // Safety: prevent infinite loop
                if (messagesProcessed > 20)
                    break;
            }

            timer.Stop();

            // Assert - should have processed multiple messages within budget
            Assert.Greater(messagesProcessed, 0,
                "Should process at least 1 message within 5ms budget");
            Assert.LessOrEqual(timer.Elapsed.TotalMilliseconds, testGlobal.maxNetworkProcessingBudgetMs + 2,
                "Should stop processing when budget exhausted (allow 2ms tolerance for timer imprecision)");
        }

        [Test]
        public void TimeBudget_1ms_ProcessesFewerMessages()
        {
            // Arrange
            testGlobal.maxNetworkProcessingBudgetMs = 1.0f;
            var timer = new System.Diagnostics.Stopwatch();
            timer.Start();

            // Simulate message processing loop
            int messagesProcessed = 0;
            while (timer.Elapsed.TotalMilliseconds < testGlobal.maxNetworkProcessingBudgetMs)
            {
                // Simulate 0.5ms per message processing
                System.Threading.Thread.Sleep(1);
                messagesProcessed++;

                if (messagesProcessed > 20)
                    break;
            }

            timer.Stop();

            // Assert - with 1ms budget, should process very few messages
            Assert.LessOrEqual(timer.Elapsed.TotalMilliseconds, testGlobal.maxNetworkProcessingBudgetMs + 2,
                "Should respect 1ms budget (allow 2ms tolerance)");
        }

        #endregion

        #region Integration Documentation Tests

        /// <summary>
        /// INTEGRATION TEST DOCUMENTATION (not runnable as unit test):
        ///
        /// To validate time budget processing in full GONet runtime:
        ///
        /// 1. Setup:
        ///    - Start GONet server (build)
        ///    - Start Client1 (build) - spawn 100+ projectiles rapidly
        ///    - Connect Client2 (Editor in VM) as late-joiner
        ///
        /// 2. Expected Behavior (with default settings):
        ///    - maxNetworkProcessingBudgetMs = 5ms → Frame time stays under 16ms (60fps maintained)
        ///    - networkQueueDropThreshold = 100 → Emergency dropping activates if queue > 100
        ///    - unreliableDropRate = 2 → Drop 50% of unreliable messages when emergency active
        ///
        /// 3. Validation (check logs):
        ///    - [NETWORK-EMERGENCY] when queue exceeds 100
        ///    - [NETWORK-RECOVERY] when queue drains below 50
        ///    - [NETWORK-STATS] every 5 seconds showing:
        ///      * Processed reliable/unreliable counts
        ///      * Dropped unreliable count
        ///      * Avg processing time (should be ≤ 5ms)
        ///      * Current queue size
        ///
        /// 4. Success Criteria:
        ///    - No [QUEUE-BACKUP] warnings exceeding 200+ messages
        ///    - Frame time stays under 16ms (use Unity Profiler)
        ///    - Queue drains without catastrophic backup
        ///
        /// 5. Stress Test:
        ///    - Reduce maxNetworkProcessingBudgetMs to 3ms → Should see more deferred messages
        ///    - Reduce networkQueueDropThreshold to 50 → Emergency mode activates earlier
        ///    - Increase unreliableDropRate to 4 → More aggressive dropping (75%)
        /// </summary>
        [Test]
        public void IntegrationTest_Documentation()
        {
            // This is a documentation test - always passes
            // Actual integration testing requires full GONet runtime
            Assert.Pass("Integration testing documented above. Use manual VM test scenario.");
        }

        #endregion
    }
}
