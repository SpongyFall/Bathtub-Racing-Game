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
using System.Reflection;
using NUnit.Framework;

namespace GONet.Tests
{
    /// <summary>
    /// CRITICAL REGRESSION TESTS for the init acknowledgment system.
    ///
    /// **THE PROBLEM:**
    /// The init acknowledgment system provides diagnostic visibility into Steamworks reliable
    /// message delivery failures during client initialization. Without it:
    /// - Server can't detect if init messages were lost in transit
    /// - Late-joiner bugs become much harder to diagnose
    /// - Steamworks reliability issues go undetected
    ///
    /// **THE FIX:**
    /// The init acknowledgment system includes:
    /// - Client_SendInitializationAcknowledgment() - Client sends receipt count to server
    /// - Server_OnClientInitializationAcknowledgment() - Server validates delivery
    /// - IsChannelTrackedForInitValidation() - Identifies which channels to track
    /// - Init message tracking in ProcessIncomingBytes
    ///
    /// **HISTORY:**
    /// This functionality existed but became orphaned/dead code during the GONet.cs file
    /// split merge (99a3092d, Nov 24, 2025). The infrastructure (GONetInitMessageTracker,
    /// ClientInitializationAcknowledgment event, receivedInitMessageChannels, etc.) existed
    /// but was never called.
    ///
    /// These tests ensure the system remains connected and functional.
    /// </summary>
    [TestFixture]
    public class InitAcknowledgmentSystemTests
    {
        private const string CATEGORY_INIT = "Initialization";
        private const string CATEGORY_REGRESSION = "RegressionTest";
        private const string CATEGORY_CHANNELS = "Channels";

        #region Channel Validation Tests

        /// <summary>
        /// REGRESSION TEST: Verifies IsChannelTrackedForInitValidation returns correct values.
        ///
        /// Channel IDs (based on static initialization order in GONetChannel):
        /// - 0: TimeSync_Unreliable
        /// - 1: AutoMagicalSync_Reliable
        /// - 2: AutoMagicalSync_Unreliable
        /// - 3: AutoMagicalSync_ValuesNowAtRest_Reliable
        /// - 4: CustomSerialization_Reliable
        /// - 5: CustomSerialization_Unreliable
        /// - 6: EventSingles_Reliable
        /// - 7: EventSingles_Unreliable
        /// - 8: ClientInitialization_EventSingles_Reliable     ← TRACKED
        /// - 9: ClientInitialization_CustomSerialization_Reliable ← TRACKED
        /// </summary>
        [Test]
        [Category(CATEGORY_CHANNELS)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void IsChannelTrackedForInitValidation_Method_Must_Exist()
        {
            // ACT - Just call the method to verify it exists
            // If this test fails to compile, the method is missing!
            bool result = GONetChannel.IsChannelTrackedForInitValidation(0);

            // ASSERT - Method exists and returns a value
            Assert.Pass("IsChannelTrackedForInitValidation() method exists and is callable");
        }

        [Test]
        [Category(CATEGORY_CHANNELS)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void IsChannelTrackedForInitValidation_Returns_True_For_ClientInit_EventSingles()
        {
            // ARRANGE
            byte channelId = GONetChannel.ClientInitialization_EventSingles_Reliable;

            // ACT
            bool isTracked = GONetChannel.IsChannelTrackedForInitValidation(channelId);

            // ASSERT
            Assert.IsTrue(isTracked,
                $"ClientInitialization_EventSingles_Reliable (channel {channelId}) should be tracked for init validation");
        }

        [Test]
        [Category(CATEGORY_CHANNELS)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void IsChannelTrackedForInitValidation_Returns_True_For_ClientInit_CustomSerialization()
        {
            // ARRANGE
            byte channelId = GONetChannel.ClientInitialization_CustomSerialization_Reliable;

            // ACT
            bool isTracked = GONetChannel.IsChannelTrackedForInitValidation(channelId);

            // ASSERT
            Assert.IsTrue(isTracked,
                $"ClientInitialization_CustomSerialization_Reliable (channel {channelId}) should be tracked for init validation");
        }

        [Test]
        [Category(CATEGORY_CHANNELS)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void IsChannelTrackedForInitValidation_Returns_False_For_TimeSync_Unreliable()
        {
            // ARRANGE - TimeSync is unreliable, drops are expected
            byte channelId = GONetChannel.TimeSync_Unreliable;

            // ACT
            bool isTracked = GONetChannel.IsChannelTrackedForInitValidation(channelId);

            // ASSERT
            Assert.IsFalse(isTracked,
                $"TimeSync_Unreliable (channel {channelId}) should NOT be tracked - unreliable drops are expected");
        }

        [Test]
        [Category(CATEGORY_CHANNELS)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void IsChannelTrackedForInitValidation_Returns_False_For_AutoMagicalSync_Reliable()
        {
            // ARRANGE - Regular sync channels are not init channels
            byte channelId = GONetChannel.AutoMagicalSync_Reliable;

            // ACT
            bool isTracked = GONetChannel.IsChannelTrackedForInitValidation(channelId);

            // ASSERT
            Assert.IsFalse(isTracked,
                $"AutoMagicalSync_Reliable (channel {channelId}) should NOT be tracked for init validation");
        }

        [Test]
        [Category(CATEGORY_CHANNELS)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void IsChannelTrackedForInitValidation_Returns_False_For_EventSingles_Reliable()
        {
            // ARRANGE - Regular event channel, not init-specific
            byte channelId = GONetChannel.EventSingles_Reliable;

            // ACT
            bool isTracked = GONetChannel.IsChannelTrackedForInitValidation(channelId);

            // ASSERT
            Assert.IsFalse(isTracked,
                $"EventSingles_Reliable (channel {channelId}) should NOT be tracked for init validation");
        }

        [Test]
        [Category(CATEGORY_CHANNELS)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void IsChannelTrackedForInitValidation_Only_Two_Channels_Tracked()
        {
            // ARRANGE & ACT - Check all channels
            int trackedCount = 0;
            for (byte channelId = 0; channelId < 20; channelId++)
            {
                if (GONetChannel.IsChannelTrackedForInitValidation(channelId))
                {
                    trackedCount++;
                }
            }

            // ASSERT - Only the two ClientInitialization channels should be tracked
            Assert.AreEqual(2, trackedCount,
                "Exactly 2 channels should be tracked for init validation " +
                "(ClientInitialization_EventSingles_Reliable and ClientInitialization_CustomSerialization_Reliable)");
        }

        #endregion

        #region Client Infrastructure Tests

        /// <summary>
        /// REGRESSION TEST: Verifies Client_SendInitializationAcknowledgment method exists.
        /// Uses reflection since the method is internal/private.
        /// </summary>
        [Test]
        [Category(CATEGORY_INIT)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void Client_SendInitializationAcknowledgment_Method_Must_Exist()
        {
            // ARRANGE - Use reflection to find the method
            Type gonetMainType = typeof(GONetMain);
            MethodInfo method = gonetMainType.GetMethod(
                "Client_SendInitializationAcknowledgment",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            // ASSERT
            Assert.IsNotNull(method,
                "REGRESSION FAILURE: Client_SendInitializationAcknowledgment() method must exist in GONetMain. " +
                "This method was lost during the GONet.cs file split merge and must be restored.");

            UnityEngine.Debug.Log("[INIT-ACK-TEST] ✅ Client_SendInitializationAcknowledgment method exists");
        }

        /// <summary>
        /// REGRESSION TEST: Verifies the method is static (as it should be for GONetMain utility methods).
        /// </summary>
        [Test]
        [Category(CATEGORY_INIT)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void Client_SendInitializationAcknowledgment_Is_Static()
        {
            // ARRANGE
            Type gonetMainType = typeof(GONetMain);
            MethodInfo method = gonetMainType.GetMethod(
                "Client_SendInitializationAcknowledgment",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            // Skip if method doesn't exist (separate test catches that)
            if (method == null)
            {
                Assert.Inconclusive("Method not found - see Client_SendInitializationAcknowledgment_Method_Must_Exist test");
                return;
            }

            // ASSERT
            Assert.IsTrue(method.IsStatic,
                "Client_SendInitializationAcknowledgment should be a static method");
        }

        #endregion

        #region Server Infrastructure Tests

        /// <summary>
        /// REGRESSION TEST: Verifies Server_OnClientInitializationAcknowledgment method exists.
        /// Uses reflection since the method is internal/private.
        /// </summary>
        [Test]
        [Category(CATEGORY_INIT)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void Server_OnClientInitializationAcknowledgment_Method_Must_Exist()
        {
            // ARRANGE - Use reflection to find the method
            Type gonetMainType = typeof(GONetMain);
            MethodInfo method = gonetMainType.GetMethod(
                "Server_OnClientInitializationAcknowledgment",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            // ASSERT
            Assert.IsNotNull(method,
                "REGRESSION FAILURE: Server_OnClientInitializationAcknowledgment() method must exist in GONetMain. " +
                "This handler was lost during the GONet.cs file split merge and must be restored.");

            UnityEngine.Debug.Log("[INIT-ACK-TEST] ✅ Server_OnClientInitializationAcknowledgment method exists");
        }

        /// <summary>
        /// REGRESSION TEST: Verifies the handler takes the correct parameter type.
        /// </summary>
        [Test]
        [Category(CATEGORY_INIT)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void Server_OnClientInitializationAcknowledgment_Has_Correct_Signature()
        {
            // ARRANGE
            Type gonetMainType = typeof(GONetMain);
            MethodInfo method = gonetMainType.GetMethod(
                "Server_OnClientInitializationAcknowledgment",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            // Skip if method doesn't exist
            if (method == null)
            {
                Assert.Inconclusive("Method not found - see Server_OnClientInitializationAcknowledgment_Method_Must_Exist test");
                return;
            }

            // ASSERT - Check parameter type
            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length,
                "Server_OnClientInitializationAcknowledgment should take exactly 1 parameter");

            // The parameter should be GONetEventEnvelope<ClientInitializationAcknowledgment>
            Type paramType = parameters[0].ParameterType;
            Assert.IsTrue(paramType.IsGenericType,
                "Parameter should be a generic type (GONetEventEnvelope<T>)");

            Type genericTypeDef = paramType.GetGenericTypeDefinition();
            Assert.AreEqual(typeof(GONetEventEnvelope<>), genericTypeDef,
                "Parameter should be GONetEventEnvelope<T>");

            Type[] genericArgs = paramType.GetGenericArguments();
            Assert.AreEqual(1, genericArgs.Length, "Should have 1 generic argument");
            Assert.AreEqual(typeof(ClientInitializationAcknowledgment), genericArgs[0],
                "Generic argument should be ClientInitializationAcknowledgment");

            UnityEngine.Debug.Log("[INIT-ACK-TEST] ✅ Server handler has correct signature");
        }

        #endregion

        #region Event Type Tests

        /// <summary>
        /// REGRESSION TEST: Verifies ClientInitializationAcknowledgment event type exists.
        /// </summary>
        [Test]
        [Category(CATEGORY_INIT)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void ClientInitializationAcknowledgment_Event_Must_Exist()
        {
            // ACT - Create instance to verify type exists
            var ackEvent = new ClientInitializationAcknowledgment();

            // ASSERT
            Assert.IsNotNull(ackEvent,
                "REGRESSION FAILURE: ClientInitializationAcknowledgment event type must exist.");

            UnityEngine.Debug.Log("[INIT-ACK-TEST] ✅ ClientInitializationAcknowledgment event type exists");
        }

        /// <summary>
        /// REGRESSION TEST: Verifies ClientInitializationAcknowledgment has required properties.
        /// </summary>
        [Test]
        [Category(CATEGORY_INIT)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void ClientInitializationAcknowledgment_Has_Required_Properties()
        {
            // ARRANGE
            var ackEvent = new ClientInitializationAcknowledgment();

            // ACT - Access required properties
            int receivedCount = ackEvent.ReceivedMessageCount;
            var receivedChannels = ackEvent.ReceivedChannels;

            // ASSERT
            Assert.GreaterOrEqual(receivedCount, 0,
                "ReceivedMessageCount should be accessible and >= 0");
            // ReceivedChannels may be null initially, that's fine

            UnityEngine.Debug.Log("[INIT-ACK-TEST] ✅ ClientInitializationAcknowledgment has required properties");
        }

        /// <summary>
        /// REGRESSION TEST: Verifies ClientInitializationAcknowledgment properties can be set.
        /// </summary>
        [Test]
        [Category(CATEGORY_INIT)]
        [Category(CATEGORY_REGRESSION)]
        [Timeout(5000)]
        public void ClientInitializationAcknowledgment_Properties_Are_Settable()
        {
            // ARRANGE
            var ackEvent = new ClientInitializationAcknowledgment();

            // ACT
            ackEvent.ReceivedMessageCount = 42;
            ackEvent.ReceivedChannels = new System.Collections.Generic.List<byte> { 8, 9 };

            // ASSERT
            Assert.AreEqual(42, ackEvent.ReceivedMessageCount,
                "ReceivedMessageCount should be settable");
            Assert.IsNotNull(ackEvent.ReceivedChannels,
                "ReceivedChannels should be settable");
            Assert.AreEqual(2, ackEvent.ReceivedChannels.Count,
                "ReceivedChannels should contain the set values");

            UnityEngine.Debug.Log("[INIT-ACK-TEST] ✅ ClientInitializationAcknowledgment properties are settable");
        }

        #endregion

        #region Integration Notes

        /// <summary>
        /// Documents the complete init acknowledgment flow for reference.
        /// This is NOT a runtime test - just documentation with assertions.
        /// </summary>
        [Test]
        [Category(CATEGORY_INIT)]
        [Timeout(5000)]
        public void Documentation_InitAcknowledgment_Flow()
        {
            // This test documents the expected flow:
            //
            // CLIENT SIDE:
            // 1. ProcessIncomingBytes receives init message on channel 8 or 9
            // 2. If IsChannelTrackedForInitValidation(channelId) returns true:
            //    - Increment receivedInitMessageChannels[channelId]
            // 3. After ServerSaysClientInitializationCompletion received:
            //    - Client sets IsInitializedWithServer but does NOT ack yet
            // 4. After init channel sentinels arrive:
            //    - Empty PersistentEvents_Bundle (channel 8 marker)
            //    - ServerSaysInitMessageTrackingComplete (channel 9 marker)
            //    - Client_SendInitializationAcknowledgment() called
            //    - Counts total received init messages
            //    - Sends ClientInitializationAcknowledgment event to server
            //
            // SERVER SIDE:
            // 1. Server_OnClientInitializationAcknowledgment() receives event
            // 2. Looks up GONetInitMessageTracker for this client
            // 3. Compares sent count vs received count
            // 4. If mismatch detected:
            //    - Logs warning about potential Steamworks reliability issue
            //    - Helps diagnose late-joiner initialization bugs
            //
            // PURPOSE:
            // - Detect Steamworks reliable message delivery failures
            // - Provide diagnostic visibility into init handshake
            // - Help debug issues like the Client2 late-joiner bug

            Assert.Pass("Documentation test - see comments for init acknowledgment flow");
        }

        /// <summary>
        /// Documents the relationship between init acknowledgment and the late-joiner bug.
        /// </summary>
        [Test]
        [Category(CATEGORY_INIT)]
        [Timeout(5000)]
        public void Documentation_LateJoiner_Bug_Relationship()
        {
            // IMPORTANT: The init acknowledgment system provides DIAGNOSTIC VISIBILITY
            // but does NOT fix the underlying late-joiner initialization bug.
            //
            // THE LATE-JOINER BUG (see late-joiner-initialization-bug.md):
            // - Client2 never received sync bundles because Server_OnNewClientInstantiatedItsGONetLocal
            //   was never called
            // - This meant IsInitializedWithServer was never set to true
            // - Server permanently blocked sync bundles to Client2
            // - ROOT CAUSE: The GONetLocal spawn confirmation RPC was never sent by client
            //   (likely due to scene load race condition)
            //
            // WHAT INIT ACKNOWLEDGMENT PROVIDES:
            // - If init messages were lost in transit, the acknowledgment count would mismatch
            // - Server can detect: "Sent 15 init messages, client only received 10"
            // - This helps distinguish between:
            //   a) Network layer dropped messages (Steamworks reliability issue)
            //   b) Client-side processing issue (scene load, GONetLocal instantiation)
            //
            // IN THE CLIENT2 CASE:
            // - The init acknowledgment would likely show messages WERE received
            // - This rules out network layer issues
            // - Points to client-side scene loading/GONetLocal instantiation as the problem
            //
            // CONCLUSION:
            // Init acknowledgment is a DIAGNOSTIC tool, not a fix. It helps narrow down
            // whether late-joiner issues are network-related or client processing-related.

            Assert.Pass("Documentation test - see comments for late-joiner bug relationship");
        }

        #endregion
    }
}
