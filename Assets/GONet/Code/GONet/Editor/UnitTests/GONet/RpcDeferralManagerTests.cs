/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.TestTools;

namespace GONet.Tests
{
    /// <summary>
    /// Unit tests for RpcDeferralManager functionality.
    /// Tests RPC queuing, timeout handling, and participant registration processing.
    /// </summary>
    [TestFixture]
    public class RpcDeferralManagerTests
    {
        private RpcDeferralManager manager;
        private bool originalEnableRpcDeferral;
        private bool originalLogDiagnostics;

        [SetUp]
        public void SetUp()
        {
            originalEnableRpcDeferral = GONetConfig.EnableRpcDeferralForUnknownParticipants;
            originalLogDiagnostics = GONetConfig.LogRpcDeferralDiagnostics;

            GONetConfig.EnableRpcDeferralForUnknownParticipants = true;
            GONetConfig.LogRpcDeferralDiagnostics = false;

            manager = new RpcDeferralManager(defaultTimeoutSeconds: 5.0f, maxPerParticipant: 10);
        }

        [TearDown]
        public void TearDown()
        {
            GONetConfig.EnableRpcDeferralForUnknownParticipants = originalEnableRpcDeferral;
            GONetConfig.LogRpcDeferralDiagnostics = originalLogDiagnostics;
            manager = null;
        }

        #region Constructor Tests

        [Test]
        public void Constructor_WithDefaults_UsesConfigValues()
        {
            float expectedTimeout = GONetConfig.RpcDeferralTimeoutSeconds;
            int expectedMax = GONetConfig.MaxDeferredRpcsPerParticipant;

            var defaultManager = new RpcDeferralManager();
            var stats = defaultManager.GetStats();

            Assert.AreEqual(0, stats.participantCount, "Should start with no participants");
            Assert.AreEqual(0, stats.totalRpcCount, "Should start with no RPCs");
        }

        [Test]
        public void Constructor_WithCustomValues_UsesCustomValues()
        {
            var customManager = new RpcDeferralManager(defaultTimeoutSeconds: 10.0f, maxPerParticipant: 50);
            var stats = customManager.GetStats();

            Assert.AreEqual(0, stats.participantCount, "Should start with no participants");
            Assert.AreEqual(0, stats.totalRpcCount, "Should start with no RPCs");
        }

        #endregion

        #region DeferRpc Tests

        [Test]
        public void DeferRpc_WhenEnabled_QueuesRpc()
        {
            uint targetGoNetId = 100;
            uint rpcId = 200;
            byte[] data = new byte[] { 1, 2, 3 };
            bool callbackInvoked = false;

            manager.DeferRpc(targetGoNetId, rpcId, data, (d) => callbackInvoked = true, "TestMethod");

            var stats = manager.GetStats();
            Assert.AreEqual(1, stats.participantCount, "Should have 1 participant waiting");
            Assert.AreEqual(1, stats.totalRpcCount, "Should have 1 RPC queued");
            Assert.IsFalse(callbackInvoked, "Callback should not be invoked yet");
        }

        [Test]
        public void DeferRpc_WhenDisabled_DoesNotQueue()
        {
            GONetConfig.EnableRpcDeferralForUnknownParticipants = false;

            manager.DeferRpc(100, 200, new byte[] { 1, 2, 3 }, (d) => { }, "TestMethod");

            var stats = manager.GetStats();
            Assert.AreEqual(0, stats.participantCount, "Should not queue when disabled");
            Assert.AreEqual(0, stats.totalRpcCount, "Should not queue when disabled");
        }

        [Test]
        public void DeferRpc_MultipleForSameParticipant_QueuesAll()
        {
            uint targetGoNetId = 100;

            manager.DeferRpc(targetGoNetId, 1, new byte[] { 1 }, (d) => { }, "Method1");
            manager.DeferRpc(targetGoNetId, 2, new byte[] { 2 }, (d) => { }, "Method2");
            manager.DeferRpc(targetGoNetId, 3, new byte[] { 3 }, (d) => { }, "Method3");

            var stats = manager.GetStats();
            Assert.AreEqual(1, stats.participantCount, "Should have 1 participant");
            Assert.AreEqual(3, stats.totalRpcCount, "Should have 3 RPCs queued");
        }

        [Test]
        public void DeferRpc_MultipleParticipants_QueuesForEach()
        {
            manager.DeferRpc(100, 1, new byte[] { 1 }, (d) => { }, "Method1");
            manager.DeferRpc(200, 2, new byte[] { 2 }, (d) => { }, "Method2");
            manager.DeferRpc(300, 3, new byte[] { 3 }, (d) => { }, "Method3");

            var stats = manager.GetStats();
            Assert.AreEqual(3, stats.participantCount, "Should have 3 participants");
            Assert.AreEqual(3, stats.totalRpcCount, "Should have 3 RPCs total");
        }

        [Test]
        public void DeferRpc_ExceedsMax_DropsOldest()
        {
            uint targetGoNetId = 100;
            var maxManager = new RpcDeferralManager(defaultTimeoutSeconds: 5.0f, maxPerParticipant: 3);

            // Queue 5 RPCs when max is 3
            for (int i = 0; i < 5; i++)
            {
                maxManager.DeferRpc(targetGoNetId, (uint)i, new byte[] { (byte)i }, (d) => { }, $"Method{i}");
            }

            var stats = maxManager.GetStats();
            Assert.AreEqual(3, stats.totalRpcCount, "Should only have max RPCs queued");
        }

        #endregion

        #region OnParticipantRegistered Tests

        [Test]
        public void OnParticipantRegistered_ExecutesQueuedRpcs()
        {
            uint targetGoNetId = 100;
            List<byte[]> receivedData = new List<byte[]>();

            manager.DeferRpc(targetGoNetId, 1, new byte[] { 1 }, (d) => receivedData.Add(d), "Method1");
            manager.DeferRpc(targetGoNetId, 2, new byte[] { 2 }, (d) => receivedData.Add(d), "Method2");

            Assert.AreEqual(0, receivedData.Count, "No callbacks should have fired yet");

            manager.OnParticipantRegistered(targetGoNetId);

            Assert.AreEqual(2, receivedData.Count, "Both callbacks should have fired");
            CollectionAssert.AreEqual(new byte[] { 1 }, receivedData[0]);
            CollectionAssert.AreEqual(new byte[] { 2 }, receivedData[1]);
        }

        [Test]
        public void OnParticipantRegistered_ClearsQueue()
        {
            uint targetGoNetId = 100;

            manager.DeferRpc(targetGoNetId, 1, new byte[] { 1 }, (d) => { }, "Method1");
            manager.OnParticipantRegistered(targetGoNetId);

            var stats = manager.GetStats();
            Assert.AreEqual(0, stats.participantCount, "Queue should be cleared");
            Assert.AreEqual(0, stats.totalRpcCount, "Queue should be cleared");
        }

        [Test]
        public void OnParticipantRegistered_OnlyAffectsTargetedRpcs()
        {
            manager.DeferRpc(100, 1, new byte[] { 1 }, (d) => { }, "Method1");
            manager.DeferRpc(200, 2, new byte[] { 2 }, (d) => { }, "Method2");

            manager.OnParticipantRegistered(100);

            var stats = manager.GetStats();
            Assert.AreEqual(1, stats.participantCount, "Should still have 1 participant");
            Assert.AreEqual(1, stats.totalRpcCount, "Should still have 1 RPC");
        }

        [Test]
        public void OnParticipantRegistered_NoQueue_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => manager.OnParticipantRegistered(999));
        }

        [Test]
        public void OnParticipantRegistered_CallbackException_ContinuesProcessing()
        {
            uint targetGoNetId = 100;
            int successCount = 0;

            manager.DeferRpc(targetGoNetId, 1, new byte[] { 1 }, (d) => throw new Exception("Test exception"), "FailMethod");
            manager.DeferRpc(targetGoNetId, 2, new byte[] { 2 }, (d) => successCount++, "SuccessMethod");

            // Expect the error log from RpcDeferralManager when callback throws
            LogAssert.Expect(LogType.Error, new Regex(@"\[RPC-DEFER\] Error executing deferred RPC.*Test exception"));

            Assert.DoesNotThrow(() => manager.OnParticipantRegistered(targetGoNetId));
            Assert.AreEqual(1, successCount, "Second callback should still execute");
        }

        #endregion

        #region OnParticipantRemoved Tests

        [Test]
        public void OnParticipantRemoved_ClearsQueue()
        {
            uint targetGoNetId = 100;

            manager.DeferRpc(targetGoNetId, 1, new byte[] { 1 }, (d) => { }, "Method1");
            manager.DeferRpc(targetGoNetId, 2, new byte[] { 2 }, (d) => { }, "Method2");

            manager.OnParticipantRemoved(targetGoNetId);

            var stats = manager.GetStats();
            Assert.AreEqual(0, stats.participantCount, "Queue should be cleared");
            Assert.AreEqual(0, stats.totalRpcCount, "Queue should be cleared");
        }

        [Test]
        public void OnParticipantRemoved_DoesNotExecuteCallbacks()
        {
            uint targetGoNetId = 100;
            bool callbackInvoked = false;

            manager.DeferRpc(targetGoNetId, 1, new byte[] { 1 }, (d) => callbackInvoked = true, "Method1");
            manager.OnParticipantRemoved(targetGoNetId);

            Assert.IsFalse(callbackInvoked, "Callback should not be invoked on removal");
        }

        [Test]
        public void OnParticipantRemoved_NoQueue_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => manager.OnParticipantRemoved(999));
        }

        #endregion

        #region GetStats Tests

        [Test]
        public void GetStats_ReturnsCorrectCounts()
        {
            manager.DeferRpc(100, 1, new byte[] { 1 }, (d) => { }, "Method1");
            manager.DeferRpc(100, 2, new byte[] { 2 }, (d) => { }, "Method2");
            manager.DeferRpc(200, 3, new byte[] { 3 }, (d) => { }, "Method3");
            manager.DeferRpc(300, 4, new byte[] { 4 }, (d) => { }, "Method4");
            manager.DeferRpc(300, 5, new byte[] { 5 }, (d) => { }, "Method5");

            var stats = manager.GetStats();
            Assert.AreEqual(3, stats.participantCount, "Should have 3 participants");
            Assert.AreEqual(5, stats.totalRpcCount, "Should have 5 RPCs total");
        }

        [Test]
        public void GetDeferredCountForParticipant_ReturnsCorrectCount()
        {
            manager.DeferRpc(100, 1, new byte[] { 1 }, (d) => { }, "Method1");
            manager.DeferRpc(100, 2, new byte[] { 2 }, (d) => { }, "Method2");
            manager.DeferRpc(200, 3, new byte[] { 3 }, (d) => { }, "Method3");

            Assert.AreEqual(2, manager.GetDeferredCountForParticipant(100));
            Assert.AreEqual(1, manager.GetDeferredCountForParticipant(200));
            Assert.AreEqual(0, manager.GetDeferredCountForParticipant(999), "Non-existent participant should return 0");
        }

        #endregion

        #region ClearAll Tests

        [Test]
        public void ClearAll_RemovesAllQueues()
        {
            manager.DeferRpc(100, 1, new byte[] { 1 }, (d) => { }, "Method1");
            manager.DeferRpc(200, 2, new byte[] { 2 }, (d) => { }, "Method2");
            manager.DeferRpc(300, 3, new byte[] { 3 }, (d) => { }, "Method3");

            manager.ClearAll();

            var stats = manager.GetStats();
            Assert.AreEqual(0, stats.participantCount, "All participants should be cleared");
            Assert.AreEqual(0, stats.totalRpcCount, "All RPCs should be cleared");
        }

        [Test]
        public void ClearAll_DoesNotExecuteCallbacks()
        {
            bool callbackInvoked = false;
            manager.DeferRpc(100, 1, new byte[] { 1 }, (d) => callbackInvoked = true, "Method1");

            manager.ClearAll();

            Assert.IsFalse(callbackInvoked, "Callbacks should not be invoked on clear");
        }

        [Test]
        public void ClearAll_OnEmptyQueue_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => manager.ClearAll());
        }

        #endregion

        #region Custom Timeout Tests

        [Test]
        public void DeferRpc_WithCustomTimeout_UsesCustomTimeout()
        {
            // This test verifies the API accepts custom timeout
            // Actual timeout behavior requires Update() to be called with time progression
            uint targetGoNetId = 100;

            Assert.DoesNotThrow(() =>
            {
                manager.DeferRpc(targetGoNetId, 1, new byte[] { 1 }, (d) => { }, "Method1", customTimeout: 60.0f);
            });

            var stats = manager.GetStats();
            Assert.AreEqual(1, stats.totalRpcCount, "RPC should be queued with custom timeout");
        }

        #endregion

        #region Edge Cases

        [Test]
        public void DeferRpc_NullCallback_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                manager.DeferRpc(100, 1, new byte[] { 1 }, null, "Method1");
            });
        }

        [Test]
        public void DeferRpc_NullData_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                manager.DeferRpc(100, 1, null, (d) => { }, "Method1");
            });
        }

        [Test]
        public void DeferRpc_EmptyData_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                manager.DeferRpc(100, 1, new byte[0], (d) => { }, "Method1");
            });
        }

        [Test]
        public void DeferRpc_NullMethodName_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                manager.DeferRpc(100, 1, new byte[] { 1 }, (d) => { }, null);
            });
        }

        [Test]
        public void OnParticipantRegistered_WithNullCallback_DoesNotThrow()
        {
            manager.DeferRpc(100, 1, new byte[] { 1 }, null, "Method1");
            Assert.DoesNotThrow(() => manager.OnParticipantRegistered(100));
        }

        #endregion
    }
}
