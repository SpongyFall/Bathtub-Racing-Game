using GONet.PluginAPI;
using NUnit.Framework;
using System;
using UnityEngine;

namespace GONet
{
    /// <summary>
    /// Unit tests for animator parameter synchronization functionality.
    /// Tests cover:
    /// - Float value blending (used for continuous animator params like Vertical, Horizontal)
    /// - Profile configuration validation (Float vs Discrete profiles)
    /// - Attribute constant validation
    /// </summary>
    [TestFixture]
    public class GONetAnimatorParameterSyncTests
    {
        #region Constants

        private const float FLOAT_EPSILON = 0.01f;
        private const float FLOAT_TOLERANCE_WITH_SMOOTHING = 0.5f;

        // Base time for all tests - use a recent time to pass the "data too old" check
        private static readonly long BaseTimeTicks = DateTime.UtcNow.Ticks;

        #endregion

        #region Helper Methods

        private long SecondsToTicks(float seconds)
        {
            return BaseTimeTicks + (long)(seconds * TimeSpan.TicksPerSecond);
        }

        private NumericValueChangeSnapshot CreateFloatSnapshot(float value, long elapsedTicks)
        {
            var syncableValue = new GONetSyncableValue();
            syncableValue.System_Single = value;
            return NumericValueChangeSnapshot.Create(elapsedTicks, syncableValue);
        }

        #endregion

        #region Profile Template Name Constants Tests

        [Test]
        public void ProfileTemplateNames_AnimatorConstants_AreDefinedCorrectly()
        {
            // Verify the animator parameter profile template name constants exist and have expected values
            Assert.AreEqual("_GONet_Animator_Controller_Parameters",
                GONetAutoMagicalSyncAttribute.PROFILE_TEMPLATE_NAME___ANIMATOR_CONTROLLER_PARAMETERS,
                "Legacy animator parameter profile template name should match expected value");

            Assert.AreEqual("_GONet_Animator_Controller_Parameters_Float",
                GONetAutoMagicalSyncAttribute.PROFILE_TEMPLATE_NAME___ANIMATOR_CONTROLLER_PARAMETERS_FLOAT,
                "Float animator parameter profile template name should match expected value");

            Assert.AreEqual("_GONet_Animator_Controller_Parameters_Discrete",
                GONetAutoMagicalSyncAttribute.PROFILE_TEMPLATE_NAME___ANIMATOR_CONTROLLER_PARAMETERS_DISCRETE,
                "Discrete animator parameter profile template name should match expected value");
        }

        [Test]
        public void ProfileTemplateNames_Float_IsDifferentFromDiscrete()
        {
            // Ensure float and discrete profiles have different names (type-specific handling)
            Assert.AreNotEqual(
                GONetAutoMagicalSyncAttribute.PROFILE_TEMPLATE_NAME___ANIMATOR_CONTROLLER_PARAMETERS_FLOAT,
                GONetAutoMagicalSyncAttribute.PROFILE_TEMPLATE_NAME___ANIMATOR_CONTROLLER_PARAMETERS_DISCRETE,
                "Float and Discrete animator profiles should have different names");
        }

        #endregion

        #region Float Blending Tests (GONetValueBlending_Float_ExtrapolateWithLowPassSmoothingFilter)

        [Test]
        public void FloatBlending_LinearMotion_InterpolatesCorrectly()
        {
            var blender = new GONetValueBlending_Float_ExtrapolateWithLowPassSmoothingFilter();

            // Simulate linear animator parameter change: 0.0 -> 1.0 over 1 second (like Vertical param)
            // Buffer must be in NEWEST FIRST order (index 0 = most recent)
            var buffer = new NumericValueChangeSnapshot[]
            {
                CreateFloatSnapshot(1.0f, SecondsToTicks(1.0f)), // newest (index 0)
                CreateFloatSnapshot(0.0f, SecondsToTicks(0.0f))  // oldest (index 1)
            };

            // Test slightly past newest (extrapolation)
            GONetSyncableValue blendedValue;
            bool didExtrapolate;
            bool result = blender.TryGetBlendedValue(buffer, 2, SecondsToTicks(1.1f), out blendedValue, out didExtrapolate);

            Assert.IsTrue(result, "Blending should succeed");
            Assert.IsTrue(didExtrapolate, "Should extrapolate past newest value");
            // With linear motion at 1 unit/second, at 1.1s we expect ~1.1
            Assert.AreEqual(1.1f, blendedValue.System_Single, FLOAT_TOLERANCE_WITH_SMOOTHING,
                "Extrapolated float value should be close to expected");
        }

        [Test]
        public void FloatBlending_StationaryValue_ReturnsLastKnownValue()
        {
            var blender = new GONetValueBlending_Float_ExtrapolateWithLowPassSmoothingFilter();

            // Simulate stationary animator parameter (same value across multiple samples)
            var buffer = new NumericValueChangeSnapshot[]
            {
                CreateFloatSnapshot(0.75f, SecondsToTicks(0.3f)), // newest
                CreateFloatSnapshot(0.75f, SecondsToTicks(0.2f)), // middle
                CreateFloatSnapshot(0.75f, SecondsToTicks(0.1f))  // oldest
            };

            GONetSyncableValue blendedValue;
            bool didExtrapolate;
            bool result = blender.TryGetBlendedValue(buffer, 3, SecondsToTicks(0.35f), out blendedValue, out didExtrapolate);

            Assert.IsTrue(result, "Blending should succeed");
            // Stationary value should remain at 0.75
            Assert.AreEqual(0.75f, blendedValue.System_Single, FLOAT_EPSILON,
                "Stationary float value should remain unchanged");
        }

        [Test]
        public void FloatBlending_EmptyBuffer_ReturnsFalse()
        {
            var blender = new GONetValueBlending_Float_ExtrapolateWithLowPassSmoothingFilter();

            var buffer = new NumericValueChangeSnapshot[0];

            GONetSyncableValue blendedValue;
            bool didExtrapolate;
            bool result = blender.TryGetBlendedValue(buffer, 0, SecondsToTicks(0.5f), out blendedValue, out didExtrapolate);

            Assert.IsFalse(result, "Blending should fail with empty buffer");
        }

        [Test]
        public void FloatBlending_SingleValue_ReturnsValue()
        {
            var blender = new GONetValueBlending_Float_ExtrapolateWithLowPassSmoothingFilter();

            // Single value in buffer (common during initial sync)
            var buffer = new NumericValueChangeSnapshot[]
            {
                CreateFloatSnapshot(0.5f, SecondsToTicks(0.1f))
            };

            GONetSyncableValue blendedValue;
            bool didExtrapolate;
            bool result = blender.TryGetBlendedValue(buffer, 1, SecondsToTicks(0.15f), out blendedValue, out didExtrapolate);

            Assert.IsTrue(result, "Blending should succeed with single value");
            Assert.AreEqual(0.5f, blendedValue.System_Single, FLOAT_EPSILON,
                "Single value should be returned as-is");
        }

        [Test]
        public void FloatBlending_RapidChanges_HandlesHighFrequencyUpdates()
        {
            var blender = new GONetValueBlending_Float_ExtrapolateWithLowPassSmoothingFilter();

            // Simulate rapid animator parameter changes (20Hz updates as per Float profile)
            // 50ms intervals = 20Hz
            var buffer = new NumericValueChangeSnapshot[]
            {
                CreateFloatSnapshot(1.0f, SecondsToTicks(0.20f)),  // newest
                CreateFloatSnapshot(0.8f, SecondsToTicks(0.15f)),
                CreateFloatSnapshot(0.6f, SecondsToTicks(0.10f)),
                CreateFloatSnapshot(0.4f, SecondsToTicks(0.05f)),
                CreateFloatSnapshot(0.2f, SecondsToTicks(0.00f))   // oldest
            };

            GONetSyncableValue blendedValue;
            bool didExtrapolate;
            // Query at 225ms (25ms past newest)
            bool result = blender.TryGetBlendedValue(buffer, 5, SecondsToTicks(0.225f), out blendedValue, out didExtrapolate);

            Assert.IsTrue(result, "Blending should succeed with multiple samples");
            Assert.IsTrue(didExtrapolate, "Should extrapolate past newest");
            // Linear increase of 4 units/second, at 0.225s expect ~1.1
            Assert.Greater(blendedValue.System_Single, 1.0f, "Extrapolated value should exceed last known value");
            Assert.Less(blendedValue.System_Single, 1.5f, "Extrapolated value should be reasonable");
        }

        [Test]
        public void FloatBlending_NegativeToPositive_HandlesSignChange()
        {
            var blender = new GONetValueBlending_Float_ExtrapolateWithLowPassSmoothingFilter();

            // Simulate animator parameter crossing zero (e.g., Horizontal going from left to right)
            var buffer = new NumericValueChangeSnapshot[]
            {
                CreateFloatSnapshot(0.5f, SecondsToTicks(0.2f)),   // newest
                CreateFloatSnapshot(0.0f, SecondsToTicks(0.1f)),   // zero crossing
                CreateFloatSnapshot(-0.5f, SecondsToTicks(0.0f))   // oldest
            };

            GONetSyncableValue blendedValue;
            bool didExtrapolate;
            bool result = blender.TryGetBlendedValue(buffer, 3, SecondsToTicks(0.25f), out blendedValue, out didExtrapolate);

            Assert.IsTrue(result, "Blending should succeed");
            // Linear increase of 5 units/second, at 0.25s expect ~0.75
            Assert.Greater(blendedValue.System_Single, 0.5f, "Extrapolated value should continue past last known");
        }

        [Test]
        public void FloatBlending_StaleData_ReturnsFalse()
        {
            var blender = new GONetValueBlending_Float_ExtrapolateWithLowPassSmoothingFilter();

            // Data that is too old (more than 1 second since newest)
            var buffer = new NumericValueChangeSnapshot[]
            {
                CreateFloatSnapshot(1.0f, SecondsToTicks(0.0f)),  // newest but old
                CreateFloatSnapshot(0.5f, SecondsToTicks(-0.5f)) // even older
            };

            GONetSyncableValue blendedValue;
            bool didExtrapolate;
            // Query 1.5 seconds after newest (exceeds AUTO_STOP_PROCESSING_BLENDING_IF_INACTIVE_FOR_TICKS)
            bool result = blender.TryGetBlendedValue(buffer, 2, SecondsToTicks(1.5f), out blendedValue, out didExtrapolate);

            Assert.IsFalse(result, "Blending should fail when data is too old");
        }

        #endregion

        #region Profile Asset Existence Tests (Editor only)

#if UNITY_EDITOR
        [Test]
        public void ProfileAssets_FloatProfile_ExistsAndHasCorrectSettings()
        {
            string assetPath = "Assets/GONet/Resources/GONet/SyncSettingsProfiles/_GONet_Animator_Controller_Parameters_Float.asset";
            var profile = UnityEditor.AssetDatabase.LoadAssetAtPath<GONetAutoMagicalSyncSettings_ProfileTemplate>(assetPath);

            Assert.IsNotNull(profile, "Float animator parameter profile asset should exist");
            Assert.IsTrue(profile.ShouldBlendBetweenValuesReceived, "Float profile should have blending enabled");
            Assert.AreEqual(AutoMagicalSyncReliability.Unreliable, profile.SendViaReliability,
                "Float profile should use unreliable transport");
            Assert.IsFalse(profile.SyncChangesASAP, "Float profile should not use ASAP delivery");
            Assert.AreEqual(20, profile.SyncChangesFrequencyOccurrences,
                "Float profile should use 20Hz frequency");
        }

        [Test]
        public void ProfileAssets_DiscreteProfile_ExistsAndHasCorrectSettings()
        {
            string assetPath = "Assets/GONet/Resources/GONet/SyncSettingsProfiles/_GONet_Animator_Controller_Parameters_Discrete.asset";
            var profile = UnityEditor.AssetDatabase.LoadAssetAtPath<GONetAutoMagicalSyncSettings_ProfileTemplate>(assetPath);

            Assert.IsNotNull(profile, "Discrete animator parameter profile asset should exist");
            Assert.IsFalse(profile.ShouldBlendBetweenValuesReceived, "Discrete profile should have blending disabled");
            Assert.AreEqual(AutoMagicalSyncReliability.Reliable, profile.SendViaReliability,
                "Discrete profile should use reliable transport");
            Assert.IsTrue(profile.SyncChangesASAP, "Discrete profile should use ASAP delivery");
        }

        [Test]
        public void ProfileAssets_FloatVsDiscrete_HaveDifferentBlendingSettings()
        {
            string floatPath = "Assets/GONet/Resources/GONet/SyncSettingsProfiles/_GONet_Animator_Controller_Parameters_Float.asset";
            string discretePath = "Assets/GONet/Resources/GONet/SyncSettingsProfiles/_GONet_Animator_Controller_Parameters_Discrete.asset";

            var floatProfile = UnityEditor.AssetDatabase.LoadAssetAtPath<GONetAutoMagicalSyncSettings_ProfileTemplate>(floatPath);
            var discreteProfile = UnityEditor.AssetDatabase.LoadAssetAtPath<GONetAutoMagicalSyncSettings_ProfileTemplate>(discretePath);

            Assert.IsNotNull(floatProfile, "Float profile should exist");
            Assert.IsNotNull(discreteProfile, "Discrete profile should exist");

            Assert.AreNotEqual(floatProfile.ShouldBlendBetweenValuesReceived, discreteProfile.ShouldBlendBetweenValuesReceived,
                "Float and Discrete profiles should have different blending settings");
            Assert.AreNotEqual(floatProfile.SendViaReliability, discreteProfile.SendViaReliability,
                "Float and Discrete profiles should have different reliability settings");
        }
#endif

        #endregion

        #region GONetSyncableValueTypes Tests

        [Test]
        public void GONetSyncableValueTypes_FloatType_CanStoreAnimatorFloatValue()
        {
            var syncableValue = new GONetSyncableValue();

            // Test typical animator float values
            float[] testValues = { 0.0f, 0.5f, 1.0f, -1.0f, 0.123456f };

            foreach (float testValue in testValues)
            {
                syncableValue.System_Single = testValue;
                Assert.AreEqual(testValue, syncableValue.System_Single, FLOAT_EPSILON,
                    $"GONetSyncableValue should correctly store float value {testValue}");
            }
        }

        [Test]
        public void GONetSyncableValueTypes_IntType_CanStoreAnimatorIntValue()
        {
            var syncableValue = new GONetSyncableValue();

            // Test typical animator int values (state IDs, mode values)
            int[] testValues = { 0, 1, 2, 5, 10, -1, 100 };

            foreach (int testValue in testValues)
            {
                syncableValue.System_Int32 = testValue;
                Assert.AreEqual(testValue, syncableValue.System_Int32,
                    $"GONetSyncableValue should correctly store int value {testValue}");
            }
        }

        [Test]
        public void GONetSyncableValueTypes_BoolType_CanStoreAnimatorBoolValue()
        {
            var syncableValue = new GONetSyncableValue();

            syncableValue.System_Boolean = true;
            Assert.IsTrue(syncableValue.System_Boolean, "GONetSyncableValue should correctly store true");

            syncableValue.System_Boolean = false;
            Assert.IsFalse(syncableValue.System_Boolean, "GONetSyncableValue should correctly store false");
        }

        #endregion
    }
}
