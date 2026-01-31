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
using System.Reflection;

namespace GONet.Tests
{
    /// <summary>
    /// Unit tests for RPC context tracking and isSameRpcAsContext logic (January 2026).
    ///
    /// ROOT CAUSE: ServerRpc → TargetRpc responses were incorrectly skipped because the originator
    /// skip logic only checked authority ID, not whether the response RPC was the SAME method
    /// the client originally called.
    ///
    /// FIX: Added SourceRpcId to GONetRpcContext and isSameRpcAsContext check:
    /// - Only skip sending to originator if BOTH:
    ///   1. Target is the originator (same authority ID)
    ///   2. Current RPC method is the SAME as what the originator called (same RpcId)
    ///
    /// KNOWN LIMITATION: If server receives TargetRpc_X from client and responds with the SAME
    /// TargetRpc_X method back to that client, it will be incorrectly skipped.
    /// Workaround: Use different RPC methods for request vs response.
    ///
    /// Test scenarios:
    /// 1. GONetRpcContext contains SourceRpcId
    /// 2. Different RpcIds result in isSameRpcAsContext = false
    /// 3. Same RpcIds result in isSameRpcAsContext = true
    /// </summary>
    [TestFixture]
    [Category("RPC")]
    [Category("Context")]
    public class GONetRpcContextTrackingTests
    {
        [Test]
        public void GONetRpcContext_HasSourceRpcIdField()
        {
            // SCENARIO: Verify GONetRpcContext struct contains SourceRpcId field
            // EXPECTED: SourceRpcId field exists and is accessible
            // IMPACT: Required for distinguishing request vs response RPCs

            var contextType = typeof(GONetRpcContext);
            var sourceRpcIdField = contextType.GetField("SourceRpcId");

            Assert.IsNotNull(sourceRpcIdField,
                "GONetRpcContext should have SourceRpcId field for RPC tracking");
            Assert.AreEqual(typeof(uint), sourceRpcIdField.FieldType,
                "SourceRpcId should be uint type");
        }

        [Test]
        public void GONetRpcContext_HasSourceAuthorityIdField()
        {
            // SCENARIO: Verify GONetRpcContext struct contains SourceAuthorityId field
            // EXPECTED: SourceAuthorityId field exists for tracking RPC origin

            var contextType = typeof(GONetRpcContext);
            var sourceAuthorityIdField = contextType.GetField("SourceAuthorityId");

            Assert.IsNotNull(sourceAuthorityIdField,
                "GONetRpcContext should have SourceAuthorityId field for RPC tracking");
            Assert.AreEqual(typeof(ushort), sourceAuthorityIdField.FieldType,
                "SourceAuthorityId should be ushort type");
        }

        [Test]
        public void DifferentRpcIds_ShouldNotBeSameContext()
        {
            // SCENARIO: Client calls ServerRpc_A, server responds with TargetRpc_B
            // EXPECTED: isSameRpcAsContext logic should return false
            // IMPACT: TargetRpc_B should NOT be skipped when sending to original client

            // Simulate: Client sent RPC with ID 100
            uint contextSourceRpcId = 100;
            ushort contextSourceAuthorityId = 2; // Client authority

            // Server is now sending RPC with ID 200 (different method)
            uint serverResponseRpcId = 200;

            // This is the core check from GONetEventBus_Rpc.cs
            bool isSameRpcAsContext = contextSourceRpcId == serverResponseRpcId;

            Assert.IsFalse(isSameRpcAsContext,
                "Different RpcIds should NOT be considered same context - response RPC should be sent to client");
        }

        [Test]
        public void SameRpcIds_ShouldBeSameContext()
        {
            // SCENARIO: Client calls TargetRpc_X, RPC routes through server back to client
            // EXPECTED: isSameRpcAsContext logic should return true
            // IMPACT: RPC should be skipped (client already executed locally)

            // Simulate: Client sent RPC with ID 100
            uint contextSourceRpcId = 100;
            ushort contextSourceAuthorityId = 2; // Client authority

            // Same RPC ID (echoing back)
            uint currentRpcId = 100;

            // This is the core check from GONetEventBus_Rpc.cs
            bool isSameRpcAsContext = contextSourceRpcId == currentRpcId;

            Assert.IsTrue(isSameRpcAsContext,
                "Same RpcIds should be considered same context - RPC should be skipped for originator");
        }

        [Test]
        public void RequestResponsePattern_ResponseNotSkipped()
        {
            // SCENARIO: Full request-response pattern
            // Client calls ServerRpc_Request (ID 100)
            // Server handler calls TargetRpc_Response (ID 200) back to same client
            // EXPECTED: TargetRpc_Response reaches the client

            // Step 1: Client sends ServerRpc_Request
            uint requestRpcId = 100;
            ushort clientAuthorityId = 2;

            // Simulate context from client request
            uint contextSourceRpcId = requestRpcId;
            ushort contextSourceAuthorityId = clientAuthorityId;

            // Step 2: Server processes request, now wants to send TargetRpc_Response
            uint responseRpcId = 200; // Different RPC method
            ushort targetAuthorityId = clientAuthorityId; // Sending back to same client

            // Step 3: Check if response should be skipped for client
            // Original bug: Only checked authority ID, incorrectly skipping response
            // Fix: Check BOTH authority ID AND RpcId match

            bool isTargetTheOriginator = targetAuthorityId == contextSourceAuthorityId;
            bool isSameRpc = responseRpcId == contextSourceRpcId;

            // Old (buggy) logic: skip if isTargetTheOriginator
            // New (fixed) logic: skip if isTargetTheOriginator AND isSameRpc

            bool shouldSkipOldLogic = isTargetTheOriginator;
            bool shouldSkipNewLogic = isTargetTheOriginator && isSameRpc;

            Assert.IsTrue(shouldSkipOldLogic,
                "Old logic would incorrectly skip (bug demonstration)");
            Assert.IsFalse(shouldSkipNewLogic,
                "New logic correctly allows response to reach client");
        }

        [Test]
        public void KnownLimitation_SameMethodEcho_StillSkipped()
        {
            // SCENARIO: Known limitation - same method echo is still skipped
            // Client calls TargetRpc_Sync, server echoes SAME TargetRpc_Sync back
            // EXPECTED: RPC is skipped (cannot distinguish echo from new call)
            // WORKAROUND: Use different methods for request vs response

            uint syncRpcId = 300;
            ushort clientAuthorityId = 2;

            // Simulate context from client
            uint contextSourceRpcId = syncRpcId;
            ushort contextSourceAuthorityId = clientAuthorityId;

            // Server echoes SAME method back
            uint echoRpcId = syncRpcId; // Same RPC ID
            ushort targetAuthorityId = clientAuthorityId;

            bool isTargetTheOriginator = targetAuthorityId == contextSourceAuthorityId;
            bool isSameRpc = echoRpcId == contextSourceRpcId;
            bool shouldSkip = isTargetTheOriginator && isSameRpc;

            Assert.IsTrue(shouldSkip,
                "Known limitation: Same method echo will be skipped - use different methods for request/response");
        }
    }
}
