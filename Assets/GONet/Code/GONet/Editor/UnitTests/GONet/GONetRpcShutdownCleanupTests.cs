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
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace GONet.Tests
{
    /// <summary>
    /// Regression tests for RPC shutdown cleanup (January 2026).
    ///
    /// ROOT CAUSE: Application quit called ShutdownForNewSession() which did NOT cancel
    /// pending async RPC TaskCompletionSources. Processes (especially clients with pending
    /// async RPCs) hung indefinitely on exit because awaiting threads were never unblocked.
    ///
    /// FIX: Moved GONetEventBus.ResetDeferredRpcStateForNewSession() into ShutdownForNewSession()
    /// so it runs on both application-quit and session-reset paths.
    ///
    /// WHAT THESE TESTS VALIDATE:
    /// 1. ResetDeferredRpcStateForNewSession clears the pendingResponses dictionary
    /// 2. ResetDeferredRpcStateForNewSession clears the pendingDeliveryReports dictionary
    /// 3. Pending TaskCompletionSources are cancelled (transition to Canceled state)
    /// 4. Awaiting code is unblocked after cleanup (no hang)
    /// 5. Multiple pending responses are all cleaned up
    /// </summary>
    [TestFixture]
    [Category("RPC")]
    [Category("Shutdown")]
    public class GONetRpcShutdownCleanupTests
    {
        private ConcurrentDictionary<long, object> pendingResponses;
        private ConcurrentDictionary<long, TaskCompletionSource<RpcDeliveryReport>> pendingDeliveryReports;

        [SetUp]
        public void SetUp()
        {
            // Access private fields via reflection for testing
            var eventBus = GONetEventBus.Instance;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            var pendingResponsesField = typeof(GONetEventBus).GetField("pendingResponses", flags);
            Assert.IsNotNull(pendingResponsesField, "pendingResponses field must exist on GONetEventBus");
            pendingResponses = (ConcurrentDictionary<long, object>)pendingResponsesField.GetValue(eventBus);

            var pendingDeliveryReportsField = typeof(GONetEventBus).GetField("pendingDeliveryReports", flags);
            Assert.IsNotNull(pendingDeliveryReportsField, "pendingDeliveryReports field must exist on GONetEventBus");
            pendingDeliveryReports = (ConcurrentDictionary<long, TaskCompletionSource<RpcDeliveryReport>>)pendingDeliveryReportsField.GetValue(eventBus);
        }

        [TearDown]
        public void TearDown()
        {
            // Ensure clean state after each test
            pendingResponses.Clear();
            pendingDeliveryReports.Clear();
        }

        #region pendingResponses Cleanup

        [Test]
        public void ResetDeferredRpcState_ClearsPendingResponses()
        {
            // SCENARIO: Pending async RPC responses exist when shutdown is triggered.
            // EXPECTED: All entries removed from pendingResponses dictionary.

            var tcs = new TaskCompletionSource<int>();
            pendingResponses.TryAdd(1001L, tcs);
            pendingResponses.TryAdd(1002L, tcs);
            pendingResponses.TryAdd(1003L, tcs);

            Assert.AreEqual(3, pendingResponses.Count, "Setup: should have 3 pending responses");

            GONetEventBus.ResetDeferredRpcStateForNewSession();

            Assert.AreEqual(0, pendingResponses.Count,
                "ResetDeferredRpcState must clear all pending responses");
        }

        [Test]
        public void ResetDeferredRpcState_ClearsPendingDeliveryReports()
        {
            // SCENARIO: Pending delivery report TCS instances exist when shutdown is triggered.
            // EXPECTED: All entries removed and TCS cancelled.

            var tcs1 = new TaskCompletionSource<RpcDeliveryReport>();
            var tcs2 = new TaskCompletionSource<RpcDeliveryReport>();
            pendingDeliveryReports.TryAdd(2001L, tcs1);
            pendingDeliveryReports.TryAdd(2002L, tcs2);

            Assert.AreEqual(2, pendingDeliveryReports.Count, "Setup: should have 2 pending delivery reports");

            GONetEventBus.ResetDeferredRpcStateForNewSession();

            Assert.AreEqual(0, pendingDeliveryReports.Count,
                "ResetDeferredRpcState must clear all pending delivery reports");
        }

        [Test]
        public void ResetDeferredRpcState_CancelsPendingDeliveryReportTasks()
        {
            // SCENARIO: Delivery report TCS should transition to Canceled state.
            // IMPACT: Code awaiting delivery reports will throw TaskCanceledException instead of hanging.

            var tcs = new TaskCompletionSource<RpcDeliveryReport>();
            pendingDeliveryReports.TryAdd(3001L, tcs);

            Assert.IsFalse(tcs.Task.IsCompleted, "Setup: TCS should not be completed yet");

            GONetEventBus.ResetDeferredRpcStateForNewSession();

            Assert.IsTrue(tcs.Task.IsCanceled,
                "Delivery report TCS must be cancelled during shutdown cleanup");
        }

        #endregion

        #region Awaiter Unblocking

        [Test]
        public void ResetDeferredRpcState_UnblocksAwaitingDeliveryReportCode()
        {
            // SCENARIO: Code is awaiting a delivery report when shutdown happens.
            // EXPECTED: The await completes (via cancellation) instead of blocking forever.
            // THIS IS THE REGRESSION TEST for the Client 2 shutdown hang.

            var tcs = new TaskCompletionSource<RpcDeliveryReport>();
            pendingDeliveryReports.TryAdd(4001L, tcs);

            bool awaiterCompleted = false;
            var awaiterTask = Task.Run(async () =>
            {
                try
                {
                    await tcs.Task;
                }
                catch (TaskCanceledException)
                {
                    // Expected - this is the correct behavior on shutdown
                }
                awaiterCompleted = true;
            });

            // Give the awaiter a moment to start waiting
            Thread.Sleep(50);
            Assert.IsFalse(awaiterCompleted, "Awaiter should still be blocked before cleanup");

            // Simulate shutdown cleanup
            GONetEventBus.ResetDeferredRpcStateForNewSession();

            // Wait for the awaiter to complete (with timeout to prevent test hang)
            bool completedInTime = awaiterTask.Wait(TimeSpan.FromSeconds(2));

            Assert.IsTrue(completedInTime, "Awaiter must be unblocked within 2 seconds of cleanup");
            Assert.IsTrue(awaiterCompleted, "Awaiter must have completed after cleanup");
        }

        [Test]
        public void ResetDeferredRpcState_MultiplePendingResponses_AllCleaned()
        {
            // SCENARIO: Many pending responses from different async RPCs.
            // EXPECTED: All are removed in a single cleanup pass.

            const int count = 50;
            for (int i = 0; i < count; i++)
            {
                var tcs = new TaskCompletionSource<int>();
                pendingResponses.TryAdd(5000L + i, tcs);
            }

            Assert.AreEqual(count, pendingResponses.Count);

            GONetEventBus.ResetDeferredRpcStateForNewSession();

            Assert.AreEqual(0, pendingResponses.Count,
                $"All {count} pending responses must be cleared");
        }

        #endregion

        #region Idempotency

        [Test]
        public void ResetDeferredRpcState_CalledTwice_NoException()
        {
            // SCENARIO: ResetDeferredRpcStateForNewSession is called twice in succession.
            // This can happen if ShutdownForNewSession is called by both Shutdown() and
            // ResetForNewSession() code paths. Must not throw.

            var tcs = new TaskCompletionSource<RpcDeliveryReport>();
            pendingDeliveryReports.TryAdd(6001L, tcs);

            Assert.DoesNotThrow(() =>
            {
                GONetEventBus.ResetDeferredRpcStateForNewSession();
                GONetEventBus.ResetDeferredRpcStateForNewSession();
            }, "Double cleanup must not throw");
        }

        [Test]
        public void ResetDeferredRpcState_EmptyDictionaries_NoException()
        {
            // SCENARIO: Cleanup called when nothing is pending.
            // EXPECTED: No-op, no exceptions.

            Assert.AreEqual(0, pendingResponses.Count);
            Assert.AreEqual(0, pendingDeliveryReports.Count);

            Assert.DoesNotThrow(() =>
            {
                GONetEventBus.ResetDeferredRpcStateForNewSession();
            }, "Cleanup with empty state must not throw");
        }

        #endregion
    }
}
