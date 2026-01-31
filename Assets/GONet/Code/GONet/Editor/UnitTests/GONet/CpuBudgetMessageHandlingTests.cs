/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using NUnit.Framework;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace GONet.Editor.UnitTests
{
    /// <summary>
    /// Unit tests for CPU budget message handling during receive-side processing.
    ///
    /// ROOT CAUSE (December 2025):
    /// When CPU budget was exceeded mid-loop, the code would dequeue a message and then
    /// immediately break, causing the dequeued message to be LOST (never processed).
    ///
    /// THE FIX:
    /// - Reliable messages: MUST be processed before breaking (can't lose reliable)
    /// - Unreliable messages: Can be dropped (return buffer to pool, then break)
    ///
    /// These tests validate the conceptual behavior of the fix without requiring
    /// the full GONet runtime.
    /// </summary>
    [TestFixture]
    public class CpuBudgetMessageHandlingTests
    {
        #region Test Infrastructure

        /// <summary>
        /// Simulates NetworkData for testing purposes
        /// </summary>
        private class MockNetworkData
        {
            public byte channelId;
            public byte[] messageBytes;
            public int bytesUsedCount;
            public bool isReliable;
            public bool wasProcessed;
            public bool wasReturnedToPool;

            public MockNetworkData(byte channelId, bool isReliable, int size = 80)
            {
                this.channelId = channelId;
                this.isReliable = isReliable;
                this.bytesUsedCount = size;
                this.messageBytes = new byte[size];
                this.wasProcessed = false;
                this.wasReturnedToPool = false;
            }
        }

        /// <summary>
        /// Simulates the message processing loop with CPU budget checking.
        /// This mirrors the actual implementation in GONet.Network.cs.
        /// </summary>
        private class MessageProcessingSimulator
        {
            public ConcurrentQueue<MockNetworkData> incomingQueue = new ConcurrentQueue<MockNetworkData>();
            public List<MockNetworkData> processedMessages = new List<MockNetworkData>();
            public List<MockNetworkData> droppedMessages = new List<MockNetworkData>();
            public List<MockNetworkData> returnedToPool = new List<MockNetworkData>();

            public double cpuBudgetMs = 2.5;
            public double simulatedElapsedMs = 0;
            public bool isCpuBudgetEnabled = true;

            /// <summary>
            /// Simulates the FIXED processing loop behavior.
            /// </summary>
            public void ProcessMessages_Fixed()
            {
                int processedCount = 0;
                int readyCount = incomingQueue.Count;

                while (processedCount < readyCount && incomingQueue.TryDequeue(out MockNetworkData networkData))
                {
                    ++processedCount;

                    // Simulate CPU budget check at processedCount % 10 == 0
                    bool shouldBreakAfterCurrentMessage = false;
                    if (isCpuBudgetEnabled && processedCount % 10 == 0)
                    {
                        if (simulatedElapsedMs > cpuBudgetMs)
                        {
                            // CRITICAL FIX: Handle based on reliability
                            if (networkData.isReliable)
                            {
                                // Reliable message: MUST process, then break
                                shouldBreakAfterCurrentMessage = true;
                            }
                            else
                            {
                                // Unreliable message: Can be dropped
                                networkData.wasReturnedToPool = true;
                                returnedToPool.Add(networkData);
                                droppedMessages.Add(networkData);
                                break;
                            }
                        }
                    }

                    // Process the message
                    networkData.wasProcessed = true;
                    processedMessages.Add(networkData);

                    // Break after processing if flag was set
                    if (shouldBreakAfterCurrentMessage)
                    {
                        break;
                    }
                }
            }

            /// <summary>
            /// Simulates the BUGGY (pre-fix) processing loop behavior.
            /// This demonstrates what was happening before the fix.
            /// </summary>
            public void ProcessMessages_Buggy()
            {
                int processedCount = 0;
                int readyCount = incomingQueue.Count;

                while (processedCount < readyCount && incomingQueue.TryDequeue(out MockNetworkData networkData))
                {
                    ++processedCount;

                    // BUG: CPU budget check with immediate break (BEFORE processing)
                    if (isCpuBudgetEnabled && processedCount % 10 == 0)
                    {
                        if (simulatedElapsedMs > cpuBudgetMs)
                        {
                            // BUG: Breaking immediately after dequeue - message is LOST!
                            droppedMessages.Add(networkData);
                            break;
                        }
                    }

                    // Process the message (never reached for the 10th message when CPU exceeded)
                    networkData.wasProcessed = true;
                    processedMessages.Add(networkData);
                }
            }

            public void Reset()
            {
                while (incomingQueue.TryDequeue(out _)) { }
                processedMessages.Clear();
                droppedMessages.Clear();
                returnedToPool.Clear();
                simulatedElapsedMs = 0;
            }
        }

        private MessageProcessingSimulator simulator;

        [SetUp]
        public void Setup()
        {
            simulator = new MessageProcessingSimulator();
        }

        [TearDown]
        public void TearDown()
        {
            simulator = null;
        }

        #endregion

        #region Bug Reproduction Tests

        /// <summary>
        /// Reproduces the original bug: reliable message lost when CPU budget exceeded at 10th message.
        /// </summary>
        [Test]
        public void Bug_ReliableMessageLost_WhenCpuBudgetExceededAt10thMessage()
        {
            // Arrange - 10 reliable messages, CPU budget exceeded
            for (int i = 0; i < 10; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true)); // Channel 6 = EventSingles_Reliable
            }
            simulator.simulatedElapsedMs = 5.0; // Exceeds 2.5ms budget

            // Act - use buggy implementation
            simulator.ProcessMessages_Buggy();

            // Assert - BUG: 10th message was lost!
            Assert.AreEqual(9, simulator.processedMessages.Count,
                "BUG DEMO: Only 9 messages processed (10th was dequeued but not processed)");
            Assert.AreEqual(1, simulator.droppedMessages.Count,
                "BUG DEMO: 1 message was lost (dequeued then dropped by immediate break)");
            Assert.IsTrue(simulator.droppedMessages[0].isReliable,
                "BUG DEMO: The lost message was RELIABLE - this is unacceptable!");
        }

        /// <summary>
        /// Verifies the fix: reliable message is processed before breaking.
        /// </summary>
        [Test]
        public void Fix_ReliableMessageProcessed_WhenCpuBudgetExceededAt10thMessage()
        {
            // Arrange - 10 reliable messages, CPU budget exceeded
            for (int i = 0; i < 10; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true));
            }
            simulator.simulatedElapsedMs = 5.0; // Exceeds 2.5ms budget

            // Act - use fixed implementation
            simulator.ProcessMessages_Fixed();

            // Assert - FIX: All 10 messages processed (10th processed before break)
            Assert.AreEqual(10, simulator.processedMessages.Count,
                "FIX: All 10 messages should be processed (reliable messages can't be dropped)");
            Assert.AreEqual(0, simulator.droppedMessages.Count,
                "FIX: No messages should be dropped when all are reliable");
        }

        #endregion

        #region Unreliable Message Handling Tests

        /// <summary>
        /// Verifies unreliable messages CAN be dropped when CPU budget exceeded.
        /// </summary>
        [Test]
        public void Fix_UnreliableMessageDropped_WhenCpuBudgetExceeded()
        {
            // Arrange - 10 unreliable messages, CPU budget exceeded
            for (int i = 0; i < 10; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(5, isReliable: false)); // Channel 5 = unreliable
            }
            simulator.simulatedElapsedMs = 5.0; // Exceeds 2.5ms budget

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - 10th unreliable message correctly dropped
            Assert.AreEqual(9, simulator.processedMessages.Count,
                "First 9 messages should be processed");
            Assert.AreEqual(1, simulator.droppedMessages.Count,
                "10th unreliable message should be dropped (acceptable for unreliable)");
            Assert.IsFalse(simulator.droppedMessages[0].isReliable,
                "Dropped message should be unreliable");
            Assert.IsTrue(simulator.droppedMessages[0].wasReturnedToPool,
                "Dropped message buffer should be returned to pool");
        }

        /// <summary>
        /// Verifies unreliable messages are processed normally when CPU budget not exceeded.
        /// </summary>
        [Test]
        public void Fix_UnreliableMessageProcessed_WhenCpuBudgetNotExceeded()
        {
            // Arrange - 10 unreliable messages, CPU budget NOT exceeded
            for (int i = 0; i < 10; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(5, isReliable: false));
            }
            simulator.simulatedElapsedMs = 1.0; // Under 2.5ms budget

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - all processed
            Assert.AreEqual(10, simulator.processedMessages.Count,
                "All unreliable messages should be processed when CPU budget not exceeded");
            Assert.AreEqual(0, simulator.droppedMessages.Count,
                "No messages should be dropped when CPU budget not exceeded");
        }

        #endregion

        #region Mixed Reliability Tests

        /// <summary>
        /// Verifies correct handling when 10th message is reliable (process, then break).
        /// </summary>
        [Test]
        public void Fix_MixedMessages_Reliable10th_ProcessesBeforeBreak()
        {
            // Arrange - 9 unreliable + 1 reliable at position 10
            for (int i = 0; i < 9; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(5, isReliable: false));
            }
            simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true)); // 10th is reliable
            simulator.simulatedElapsedMs = 5.0; // Exceeds budget

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - all 10 processed because 10th is reliable
            Assert.AreEqual(10, simulator.processedMessages.Count,
                "All 10 messages should be processed (10th is reliable, must process before break)");
            Assert.AreEqual(0, simulator.droppedMessages.Count,
                "No messages should be dropped");
            Assert.IsTrue(simulator.processedMessages[9].isReliable,
                "10th processed message should be the reliable one");
        }

        /// <summary>
        /// Verifies correct handling when 10th message is unreliable (drop, return to pool, break).
        /// </summary>
        [Test]
        public void Fix_MixedMessages_Unreliable10th_DropsAndBreaks()
        {
            // Arrange - 9 reliable + 1 unreliable at position 10
            for (int i = 0; i < 9; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true));
            }
            simulator.incomingQueue.Enqueue(new MockNetworkData(5, isReliable: false)); // 10th is unreliable
            simulator.simulatedElapsedMs = 5.0; // Exceeds budget

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - 9 processed, 10th dropped
            Assert.AreEqual(9, simulator.processedMessages.Count,
                "First 9 reliable messages should be processed");
            Assert.AreEqual(1, simulator.droppedMessages.Count,
                "10th unreliable message should be dropped");
            Assert.IsFalse(simulator.droppedMessages[0].isReliable,
                "Dropped message should be unreliable");
        }

        #endregion

        #region CPU Budget Trigger Tests

        /// <summary>
        /// Verifies CPU check only happens at processedCount % 10 == 0.
        /// </summary>
        [Test]
        public void CpuBudgetCheck_OnlyTriggersAtMultipleOf10()
        {
            // Arrange - 15 reliable messages
            for (int i = 0; i < 15; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true));
            }
            simulator.simulatedElapsedMs = 5.0; // Exceeds budget

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - should process exactly 10 (check triggers at 10, breaks after processing)
            Assert.AreEqual(10, simulator.processedMessages.Count,
                "Should process exactly 10 messages (CPU check at count 10, processes then breaks)");
        }

        /// <summary>
        /// Verifies no CPU check at 9 messages (just under trigger).
        /// </summary>
        [Test]
        public void CpuBudgetCheck_DoesNotTriggerAt9Messages()
        {
            // Arrange - exactly 9 messages
            for (int i = 0; i < 9; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true));
            }
            simulator.simulatedElapsedMs = 5.0; // Exceeds budget, but won't be checked

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - all 9 processed (CPU check never triggered)
            Assert.AreEqual(9, simulator.processedMessages.Count,
                "All 9 messages should be processed (CPU check triggers at 10, not 9)");
        }

        /// <summary>
        /// Verifies CPU budget disabled (0ms) processes all messages.
        /// </summary>
        [Test]
        public void CpuBudgetDisabled_ProcessesAllMessages()
        {
            // Arrange - 20 messages, CPU budget disabled
            for (int i = 0; i < 20; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true));
            }
            simulator.isCpuBudgetEnabled = false;
            simulator.simulatedElapsedMs = 100.0; // Irrelevant when disabled

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - all processed
            Assert.AreEqual(20, simulator.processedMessages.Count,
                "All messages should be processed when CPU budget is disabled");
        }

        #endregion

        #region Buffer Pool Return Tests

        /// <summary>
        /// Verifies dropped unreliable message has buffer returned to pool.
        /// </summary>
        [Test]
        public void DroppedUnreliable_BufferReturnedToPool()
        {
            // Arrange
            for (int i = 0; i < 10; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(5, isReliable: false));
            }
            simulator.simulatedElapsedMs = 5.0;

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert
            Assert.AreEqual(1, simulator.returnedToPool.Count,
                "Exactly 1 buffer should be returned to pool (the dropped unreliable)");
            Assert.AreSame(simulator.droppedMessages[0], simulator.returnedToPool[0],
                "The dropped message and returned-to-pool message should be the same instance");
        }

        /// <summary>
        /// Verifies processed reliable message does NOT prematurely return buffer to pool.
        /// </summary>
        [Test]
        public void ProcessedReliable_BufferNotReturnedPrematurely()
        {
            // Arrange
            for (int i = 0; i < 10; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true));
            }
            simulator.simulatedElapsedMs = 5.0;

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - no premature pool returns (buffer returned AFTER processing, not during)
            Assert.AreEqual(0, simulator.returnedToPool.Count,
                "No buffers should be returned during CPU budget handling (reliable messages are processed)");
        }

        #endregion

        #region Spawn Message Specific Tests

        /// <summary>
        /// Verifies spawn messages (80-101 bytes on channel 6) are always processed.
        /// Spawn messages are critical and must never be dropped.
        /// </summary>
        [Test]
        public void SpawnMessages_AlwaysProcessed_NeverDropped()
        {
            // Arrange - simulate spawn messages (80 bytes = ImmediatelyRelinquish=True, 101 bytes = False)
            simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true, size: 80));  // Spawn type 1
            simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true, size: 101)); // Spawn type 2
            for (int i = 0; i < 8; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true, size: 80));
            }
            simulator.simulatedElapsedMs = 5.0;

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - all spawn messages processed
            Assert.AreEqual(10, simulator.processedMessages.Count,
                "All spawn messages should be processed (they're reliable)");
            Assert.AreEqual(0, simulator.droppedMessages.Count,
                "No spawn messages should ever be dropped");
        }

        /// <summary>
        /// Verifies the exact scenario that caused the original bug:
        /// Client sends spawn, server receives at processedCount=10, message was lost.
        /// </summary>
        [Test]
        public void OriginalBugScenario_SpawnMessageAt10thPosition_NowProcessed()
        {
            // Arrange - simulate the exact bug scenario
            // Server has a batch of sync messages + a spawn message at position 10
            for (int i = 0; i < 9; i++)
            {
                // Sync messages (various sizes, unreliable)
                simulator.incomingQueue.Enqueue(new MockNetworkData(4, isReliable: false, size: 64));
            }
            // Spawn message arrives at position 10 (reliable, 80 bytes)
            simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true, size: 80));

            simulator.simulatedElapsedMs = 5.0; // CPU budget exceeded

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - spawn message at position 10 must be processed
            Assert.AreEqual(10, simulator.processedMessages.Count,
                "All 10 messages including spawn should be processed");
            Assert.IsTrue(simulator.processedMessages[9].isReliable,
                "10th message (spawn) should be reliable and processed");
            Assert.AreEqual(80, simulator.processedMessages[9].bytesUsedCount,
                "10th message should be 80-byte spawn message");
        }

        #endregion

        #region Edge Cases

        /// <summary>
        /// Verifies empty queue handling.
        /// </summary>
        [Test]
        public void EmptyQueue_NoProcessing()
        {
            // Arrange - empty queue
            simulator.simulatedElapsedMs = 5.0;

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert
            Assert.AreEqual(0, simulator.processedMessages.Count);
            Assert.AreEqual(0, simulator.droppedMessages.Count);
        }

        /// <summary>
        /// Verifies single message handling (no CPU check at count 1).
        /// </summary>
        [Test]
        public void SingleMessage_AlwaysProcessed()
        {
            // Arrange - single unreliable message
            simulator.incomingQueue.Enqueue(new MockNetworkData(5, isReliable: false));
            simulator.simulatedElapsedMs = 100.0; // Way over budget

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - processed (CPU check not triggered at count 1)
            Assert.AreEqual(1, simulator.processedMessages.Count);
            Assert.AreEqual(0, simulator.droppedMessages.Count);
        }

        /// <summary>
        /// Verifies multiple CPU checks (at 10, 20, 30, etc.).
        /// </summary>
        [Test]
        public void MultipleCpuChecks_BreaksAtFirst()
        {
            // Arrange - 25 reliable messages
            for (int i = 0; i < 25; i++)
            {
                simulator.incomingQueue.Enqueue(new MockNetworkData(6, isReliable: true));
            }
            simulator.simulatedElapsedMs = 5.0;

            // Act
            simulator.ProcessMessages_Fixed();

            // Assert - breaks after first check at 10
            Assert.AreEqual(10, simulator.processedMessages.Count,
                "Should break after processing 10th message (first CPU check)");
        }

        #endregion

        #region Integration Documentation

        /// <summary>
        /// Documents the fix and its validation through actual log analysis.
        ///
        /// ORIGINAL BUG (reliable-review7 logs):
        /// - 45 spawn messages showed: DEQUEUE=True, DESER=False
        /// - Messages were dequeued but never processed
        /// - Root cause: CPU budget break at processedCount=10 dropped the dequeued message
        ///
        /// FIX VALIDATION:
        /// 1. Run test with spawn-heavy scenario
        /// 2. Check logs for [DEQUEUE] and [SPAWN-DESER] matching
        /// 3. Verify no messages show DEQUEUE=True but DESER=False
        /// 4. All spawn GONetIds should appear in both logs
        /// </summary>
        [Test]
        public void Documentation_FixValidation()
        {
            Assert.Pass(@"
FIX SUMMARY (December 2025):
============================

ROOT CAUSE:
- ProcessIncomingBytes_QueuedNetworkData_MainThread would dequeue a message
- CPU budget check at processedCount % 10 == 0
- If budget exceeded: immediate 'break' BEFORE processing
- The dequeued message was lost (never processed, never returned to pool)

THE FIX (GONet.Network.cs lines 293-365):
- Check if current message is reliable or unreliable
- RELIABLE: Process it via ProcessIncomingBytes_QueuedNetworkData_MainThread_INTERNAL, then break
- UNRELIABLE: Return buffer to pool, then break (acceptable to drop)

VALIDATION STEPS:
1. Run spawn-heavy scenario (client spawns many objects)
2. Check server logs for matching [DEQUEUE] and [SPAWN-DESER] entries
3. Run compare_spawn_flow.py to verify no lost messages
4. Verify 'Lost after dequeue' count is 0

KEY INSIGHT:
- Send side doesn't have this bug (thinning happens BEFORE the send loop)
- Only receive side had the mid-loop CPU check with immediate break
");
        }

        #endregion
    }
}
