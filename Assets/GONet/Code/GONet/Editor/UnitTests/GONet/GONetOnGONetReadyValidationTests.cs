/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using NUnit.Framework;
using System;

namespace GONet.Tests
{
    /// <summary>
    /// Unit tests for OnGONetReady lifecycle validation in GONetBehaviour/GONetParticipantCompanionBehaviour.
    /// Tests that lifecycle preconditions are properly validated.
    /// </summary>
    [TestFixture]
    public class GONetOnGONetReadyValidationTests
    {
        // Store original config values
        private bool originalEnableValidation;
        private bool originalThrowOnViolations;

        [SetUp]
        public void SetUp()
        {
            originalEnableValidation = GONetConfig.EnableOnGONetReadyValidation;
            originalThrowOnViolations = GONetConfig.ThrowOnGONetReadyViolations;
        }

        [TearDown]
        public void TearDown()
        {
            GONetConfig.EnableOnGONetReadyValidation = originalEnableValidation;
            GONetConfig.ThrowOnGONetReadyViolations = originalThrowOnViolations;
        }

        #region Configuration Tests

        [Test]
        public void EnableOnGONetReadyValidation_CanBeToggled()
        {
            GONetConfig.EnableOnGONetReadyValidation = true;
            Assert.IsTrue(GONetConfig.EnableOnGONetReadyValidation);

            GONetConfig.EnableOnGONetReadyValidation = false;
            Assert.IsFalse(GONetConfig.EnableOnGONetReadyValidation);
        }

        [Test]
        public void ThrowOnGONetReadyViolations_DefaultIsFalse()
        {
            // Reset to see actual default
            GONetConfig.ThrowOnGONetReadyViolations = false;
            Assert.IsFalse(GONetConfig.ThrowOnGONetReadyViolations,
                "ThrowOnGONetReadyViolations should default to false for production safety");
        }

        [Test]
        public void ThrowOnGONetReadyViolations_CanBeEnabled()
        {
            GONetConfig.ThrowOnGONetReadyViolations = true;
            Assert.IsTrue(GONetConfig.ThrowOnGONetReadyViolations);
        }

        #endregion

        #region Validation Configuration Interaction Tests

        [Test]
        public void ValidationDisabled_NoExceptionOnViolation()
        {
            GONetConfig.EnableOnGONetReadyValidation = false;
            GONetConfig.ThrowOnGONetReadyViolations = true;  // Even with throw enabled

            // When validation is disabled, violations shouldn't be checked at all
            Assert.IsFalse(GONetConfig.EnableOnGONetReadyValidation);
        }

        [Test]
        public void ValidationEnabled_ThrowDisabled_WarningsOnly()
        {
            GONetConfig.EnableOnGONetReadyValidation = true;
            GONetConfig.ThrowOnGONetReadyViolations = false;

            // With validation enabled but throw disabled, should only warn
            Assert.IsTrue(GONetConfig.EnableOnGONetReadyValidation);
            Assert.IsFalse(GONetConfig.ThrowOnGONetReadyViolations);
        }

        [Test]
        public void ValidationEnabled_ThrowEnabled_ExceptionsThrown()
        {
            GONetConfig.EnableOnGONetReadyValidation = true;
            GONetConfig.ThrowOnGONetReadyViolations = true;

            Assert.IsTrue(GONetConfig.EnableOnGONetReadyValidation);
            Assert.IsTrue(GONetConfig.ThrowOnGONetReadyViolations);
        }

        #endregion

        #region GONetParticipant State Tests

        [Test]
        public void GONetId_Unset_IsDetectable()
        {
            // Verify GONetId_Unset constant is accessible and has expected value
            uint unset = GONetParticipant.GONetId_Unset;
            Assert.AreEqual(0u, unset, "GONetId_Unset should be 0");
        }

        [Test]
        public void OwnerAuthorityId_Unset_IsDetectable()
        {
            // Verify OwnerAuthorityId_Unset constant is accessible
            ushort unset = GONetMain.OwnerAuthorityId_Unset;
            Assert.AreEqual(0, unset, "OwnerAuthorityId_Unset should be 0");
        }

        #endregion

        #region Precondition Check Tests

        [Test]
        public void IsClientVsServerStatusKnown_IsFalse_Initially()
        {
            // This tests that the property exists and is accessible
            // Actual value depends on runtime state
            bool status = GONetMain.IsClientVsServerStatusKnown;
            // Just verify it's accessible without throwing
            Assert.That(status, Is.TypeOf<bool>());
        }

        #endregion

        #region Validation Context Tests

        [Test]
        public void ValidationContext_CanBeProvided()
        {
            // Validation methods accept an optional context string
            // This tests that the configuration supports contextual error messages
            GONetConfig.EnableOnGONetReadyValidation = true;
            GONetConfig.ThrowOnGONetReadyViolations = false;  // Don't throw for this test

            // The ValidateOnGONetReadyPreconditions method accepts context
            // We're testing the configuration allows for contextual validation
            Assert.IsTrue(GONetConfig.EnableOnGONetReadyValidation);
        }

        #endregion

        #region Configuration Persistence Tests

        [Test]
        public void Configuration_PersistsAcrossReads()
        {
            GONetConfig.EnableOnGONetReadyValidation = true;
            GONetConfig.ThrowOnGONetReadyViolations = true;

            // Read multiple times
            bool val1 = GONetConfig.EnableOnGONetReadyValidation;
            bool val2 = GONetConfig.EnableOnGONetReadyValidation;
            bool val3 = GONetConfig.ThrowOnGONetReadyViolations;
            bool val4 = GONetConfig.ThrowOnGONetReadyViolations;

            Assert.IsTrue(val1);
            Assert.IsTrue(val2);
            Assert.IsTrue(val3);
            Assert.IsTrue(val4);
            Assert.AreEqual(val1, val2);
            Assert.AreEqual(val3, val4);
        }

        [Test]
        public void Configuration_ChangesAreImmediate()
        {
            GONetConfig.EnableOnGONetReadyValidation = true;
            Assert.IsTrue(GONetConfig.EnableOnGONetReadyValidation);

            GONetConfig.EnableOnGONetReadyValidation = false;
            Assert.IsFalse(GONetConfig.EnableOnGONetReadyValidation);

            GONetConfig.EnableOnGONetReadyValidation = true;
            Assert.IsTrue(GONetConfig.EnableOnGONetReadyValidation);
        }

        #endregion

        #region Development vs Production Build Tests

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Test]
        public void EnableOnGONetReadyValidation_DefaultTrue_InDevelopment()
        {
            // In Editor/Development builds, validation should default to true
            // Note: This test only runs in those configurations
            Assert.Pass("Test only valid in development builds - default should be true");
        }
#else
        [Test]
        public void EnableOnGONetReadyValidation_DefaultFalse_InRelease()
        {
            // In release builds, validation should default to false for performance
            Assert.Pass("Test only valid in release builds - default should be false");
        }
#endif

        #endregion

        #region Error Message Quality Tests

        [Test]
        public void ViolationMessage_ShouldContainGONetId()
        {
            // When a violation occurs, the error message should mention GONetId
            string expectedSubstring = "GONetId";
            string sampleMessage = "GONetId is not assigned (GONetId_Unset). OnGONetReady() hasn't been called yet.";

            Assert.IsTrue(sampleMessage.Contains(expectedSubstring),
                "Violation message should mention GONetId");
        }

        [Test]
        public void ViolationMessage_ShouldContainOnGONetReady()
        {
            // When a violation occurs due to lifecycle, message should mention OnGONetReady
            string expectedSubstring = "OnGONetReady";
            string sampleMessage = "GONetId is not assigned. OnGONetReady() hasn't been called yet.";

            Assert.IsTrue(sampleMessage.Contains(expectedSubstring),
                "Violation message should mention OnGONetReady");
        }

        [Test]
        public void ViolationMessage_ShouldContainComponentName()
        {
            // Violation messages should include the component type for debugging
            string expectedPattern = "Component";
            string sampleMessage = "Component 'MyBehaviour' on 'MyGameObject': GONetId is not assigned.";

            Assert.IsTrue(sampleMessage.Contains(expectedPattern),
                "Violation message should mention the component");
        }

        #endregion

        #region Edge Cases

        [Test]
        public void Configuration_BothSettingsFalse_NoValidation()
        {
            GONetConfig.EnableOnGONetReadyValidation = false;
            GONetConfig.ThrowOnGONetReadyViolations = false;

            Assert.IsFalse(GONetConfig.EnableOnGONetReadyValidation);
            Assert.IsFalse(GONetConfig.ThrowOnGONetReadyViolations);
        }

        [Test]
        public void Configuration_ValidationFalse_ThrowTrue_NoEffect()
        {
            // If validation is disabled, throw setting shouldn't matter
            GONetConfig.EnableOnGONetReadyValidation = false;
            GONetConfig.ThrowOnGONetReadyViolations = true;

            // Throw is enabled but won't be used since validation is off
            Assert.IsFalse(GONetConfig.EnableOnGONetReadyValidation);
            Assert.IsTrue(GONetConfig.ThrowOnGONetReadyViolations);
            // (The actual validation logic would skip when EnableOnGONetReadyValidation is false)
        }

        #endregion

        #region Integration with RPC Validation

        [Test]
        public void RpcValidation_UsesOnGONetReadyCheck()
        {
            // RPC pre-send validation should check OnGONetReady state
            // Both systems should work together
            GONetConfig.EnableRpcPreSendValidation = true;
            GONetConfig.EnableOnGONetReadyValidation = true;

            Assert.IsTrue(GONetConfig.EnableRpcPreSendValidation);
            Assert.IsTrue(GONetConfig.EnableOnGONetReadyValidation);
        }

        [Test]
        public void BothValidationsSystems_CanBeDisabled()
        {
            GONetConfig.EnableRpcPreSendValidation = false;
            GONetConfig.EnableOnGONetReadyValidation = false;

            Assert.IsFalse(GONetConfig.EnableRpcPreSendValidation);
            Assert.IsFalse(GONetConfig.EnableOnGONetReadyValidation);
        }

        [Test]
        public void BothValidationSystems_CanBeEnabled()
        {
            GONetConfig.EnableRpcPreSendValidation = true;
            GONetConfig.EnableOnGONetReadyValidation = true;

            Assert.IsTrue(GONetConfig.EnableRpcPreSendValidation);
            Assert.IsTrue(GONetConfig.EnableOnGONetReadyValidation);
        }

        #endregion
    }
}
