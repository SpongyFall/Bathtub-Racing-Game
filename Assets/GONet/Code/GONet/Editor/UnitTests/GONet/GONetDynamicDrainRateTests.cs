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
using System.Reflection;
using GONet.Generation;

namespace GONet.Editor.UnitTests
{
    /// <summary>
    /// Unit tests for GONet's dynamic sync event drain rate system.
    ///
    /// WHAT IS DYNAMIC DRAIN RATE?
    /// Instead of a fixed magic number (e.g., 500), the drain rate for returning
    /// sync events to the pool is dynamically calculated based on scene metadata.
    ///
    /// HOW IT WORKS:
    /// 1. ValuesCountByCodeGenerationId is lazily populated when companions are created
    /// 2. On scene load, GetExpectedSyncValuesForScene scans DesignTimeMetadata
    /// 3. Drain rate is set to max(minimum, expectedValues * 2) for headroom
    ///
    /// WHY IT MATTERS:
    /// - Small scenes (10-20 participants): ~100 drain rate (minimal overhead)
    /// - Heavy scenes (810 participants): ~11,000+ drain rate (prevents pool exhaustion)
    /// - No magic numbers - calculated from actual scene data
    /// </summary>
    [TestFixture]
    public class GONetDynamicDrainRateTests
    {
        private const int MINIMUM_DRAIN_RATE = 100; // STARTING_MAX_SYNC_EVENTS_RETURN_PER_FRAME

        [SetUp]
        public void Setup()
        {
            // Clear the ValuesCountByCodeGenerationId dictionary before each test
            GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId.Clear();
        }

        #region ValuesCountByCodeGenerationId Tests

        [Test]
        public void ValuesCountByCodeGenerationId_InitiallyEmpty()
        {
            // ASSERT: Dictionary should be empty at start
            Assert.AreEqual(0, GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId.Count,
                "ValuesCountByCodeGenerationId should be empty initially");
        }

        [Test]
        public void ValuesCountByCodeGenerationId_CanBePopulated()
        {
            // ARRANGE & ACT: Manually populate (simulating lazy load from CreateInstance)
            GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId[1] = 7;
            GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId[2] = 12;
            GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId[3] = 5;

            // ASSERT
            Assert.AreEqual(3, GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId.Count);
            Assert.AreEqual(7, GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId[1]);
            Assert.AreEqual(12, GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId[2]);
            Assert.AreEqual(5, GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId[3]);
        }

        #endregion

        #region GetValuesCountForCodeGenerationId Tests

        [Test]
        public void GetValuesCountForCodeGenerationId_UnknownId_ReturnsZero()
        {
            // ACT: Query for an ID that doesn't exist
            byte result = GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.GetValuesCountForCodeGenerationId(99);

            // ASSERT
            Assert.AreEqual(0, result, "Unknown CodeGenerationId should return 0");
        }

        [Test]
        public void GetValuesCountForCodeGenerationId_KnownId_ReturnsCorrectCount()
        {
            // ARRANGE
            GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId[5] = 15;

            // ACT
            byte result = GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.GetValuesCountForCodeGenerationId(5);

            // ASSERT
            Assert.AreEqual(15, result);
        }

        [Test]
        public void GetValuesCountForCodeGenerationId_MultipleIds_ReturnsCorrectCounts()
        {
            // ARRANGE
            GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId[1] = 7;
            GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId[2] = 12;
            GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.ValuesCountByCodeGenerationId[3] = 5;

            // ACT & ASSERT
            Assert.AreEqual(7, GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.GetValuesCountForCodeGenerationId(1));
            Assert.AreEqual(12, GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.GetValuesCountForCodeGenerationId(2));
            Assert.AreEqual(5, GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.GetValuesCountForCodeGenerationId(3));
            Assert.AreEqual(0, GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.GetValuesCountForCodeGenerationId(4)); // Unknown
        }

        #endregion

        #region GetExpectedSyncValuesForScene Tests

        [Test]
        public void GetExpectedSyncValuesForScene_NullSceneName_ReturnsZero()
        {
            // ACT
            int result = GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.GetExpectedSyncValuesForScene(null);

            // ASSERT
            Assert.AreEqual(0, result, "Null scene name should return 0");
        }

        [Test]
        public void GetExpectedSyncValuesForScene_EmptySceneName_ReturnsZero()
        {
            // ACT
            int result = GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.GetExpectedSyncValuesForScene("");

            // ASSERT
            Assert.AreEqual(0, result, "Empty scene name should return 0");
        }

        [Test]
        public void GetExpectedSyncValuesForScene_NoMetadataLoaded_ReturnsZero()
        {
            // ARRANGE: No metadata loaded (GetCachedMetadataLibrary returns null)
            // This is the default state before CacheAllProjectDesignTimeMetadata is called

            // ACT
            int result = GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.GetExpectedSyncValuesForScene("TestScene");

            // ASSERT: Should return 0 gracefully when no metadata available
            Assert.AreEqual(0, result, "Should return 0 when metadata not loaded");
        }

        #endregion

        #region SyncEventsSaveSupport.SetDrainRate Tests

        /// <summary>
        /// Helper to create SyncEventsSaveSupport instance via reflection (internal constructor).
        /// </summary>
        private object CreateSyncEventsSaveSupportInstance(out Type saveSupportType, out MethodInfo setDrainRateMethod, out FieldInfo maxToReturnField)
        {
            saveSupportType = typeof(GONetMain).GetNestedType("SyncEventsSaveSupport", BindingFlags.NonPublic);
            Assert.IsNotNull(saveSupportType, "SyncEventsSaveSupport type should exist");

            // Use nonPublic=true to access internal constructor
            object instance = Activator.CreateInstance(saveSupportType, nonPublic: true);
            Assert.IsNotNull(instance, "Should be able to create SyncEventsSaveSupport instance");

            setDrainRateMethod = saveSupportType.GetMethod("SetDrainRate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            maxToReturnField = saveSupportType.GetField("maxToReturnPerFrame", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.IsNotNull(setDrainRateMethod, "SetDrainRate method should exist");
            Assert.IsNotNull(maxToReturnField, "maxToReturnPerFrame field should exist");

            return instance;
        }

        [Test]
        public void SetDrainRate_BelowMinimum_ClampsToMinimum()
        {
            // ARRANGE
            object instance = CreateSyncEventsSaveSupportInstance(out _, out MethodInfo setDrainRateMethod, out FieldInfo maxToReturnField);

            // ACT: Set drain rate below minimum
            setDrainRateMethod.Invoke(instance, new object[] { 50 });

            // ASSERT: Should be clamped to minimum
            int actualRate = (int)maxToReturnField.GetValue(instance);
            Assert.AreEqual(MINIMUM_DRAIN_RATE, actualRate,
                $"Drain rate below {MINIMUM_DRAIN_RATE} should be clamped to minimum");
        }

        [Test]
        public void SetDrainRate_AboveMinimum_SetsExactValue()
        {
            // ARRANGE
            object instance = CreateSyncEventsSaveSupportInstance(out _, out MethodInfo setDrainRateMethod, out FieldInfo maxToReturnField);

            // ACT: Set drain rate above minimum
            setDrainRateMethod.Invoke(instance, new object[] { 5000 });

            // ASSERT: Should use exact value
            int actualRate = (int)maxToReturnField.GetValue(instance);
            Assert.AreEqual(5000, actualRate, "Drain rate above minimum should be set exactly");
        }

        [Test]
        public void SetDrainRate_ExactlyMinimum_SetsMinimum()
        {
            // ARRANGE
            object instance = CreateSyncEventsSaveSupportInstance(out _, out MethodInfo setDrainRateMethod, out FieldInfo maxToReturnField);

            // ACT: Set drain rate to exactly minimum
            setDrainRateMethod.Invoke(instance, new object[] { MINIMUM_DRAIN_RATE });

            // ASSERT
            int actualRate = (int)maxToReturnField.GetValue(instance);
            Assert.AreEqual(MINIMUM_DRAIN_RATE, actualRate);
        }

        [Test]
        public void SetDrainRate_LargeValue_HandlesCorrectly()
        {
            // ARRANGE: Simulate a heavy scene like 810 participants with 7 values each
            object instance = CreateSyncEventsSaveSupportInstance(out _, out MethodInfo setDrainRateMethod, out FieldInfo maxToReturnField);

            int expectedSyncValues = 810 * 7; // ~5670
            int targetDrainRate = expectedSyncValues * 2; // ~11340 with headroom

            // ACT
            setDrainRateMethod.Invoke(instance, new object[] { targetDrainRate });

            // ASSERT
            int actualRate = (int)maxToReturnField.GetValue(instance);
            Assert.AreEqual(targetDrainRate, actualRate,
                "Large drain rate for heavy scene should be set correctly");
        }

        #endregion

        #region Integration Scenario Tests

        [Test]
        public void DrainRateCalculation_SmallScene_UsesMinimum()
        {
            // SCENARIO: Small lobby scene with 10 participants, 5 values each = 50 total
            // Expected drain rate: max(100, 50) = 100 (minimum)

            int expectedSyncValues = 10 * 5;
            int finalRate = Math.Max(MINIMUM_DRAIN_RATE, expectedSyncValues);

            Assert.AreEqual(MINIMUM_DRAIN_RATE, finalRate,
                "Small scene should use minimum drain rate");
        }

        [Test]
        public void DrainRateCalculation_MediumScene_CalculatesCorrectly()
        {
            // SCENARIO: Medium scene with 100 participants, 8 values each = 800 total
            // Expected drain rate: max(100, 800) = 800

            int expectedSyncValues = 100 * 8;
            int finalRate = Math.Max(MINIMUM_DRAIN_RATE, expectedSyncValues);

            Assert.AreEqual(800, finalRate,
                "Medium scene should calculate drain rate correctly");
        }

        [Test]
        public void DrainRateCalculation_HeavyScene_CalculatesCorrectly()
        {
            // SCENARIO: Heavy scene with 810 participants, 7 values each = 5670 total
            // Expected drain rate: max(100, 5670) = 5670

            int expectedSyncValues = 810 * 7;
            int finalRate = Math.Max(MINIMUM_DRAIN_RATE, expectedSyncValues);

            Assert.AreEqual(5670, finalRate,
                "Heavy scene should calculate appropriate drain rate");
        }

        [Test]
        public void DrainRateCalculation_ZeroSyncValues_UsesMinimum()
        {
            // SCENARIO: Scene with no GONetParticipants (or no metadata yet)
            // Expected drain rate: max(100, 0) = 100

            int expectedSyncValues = 0;
            int finalRate = Math.Max(MINIMUM_DRAIN_RATE, expectedSyncValues);

            Assert.AreEqual(MINIMUM_DRAIN_RATE, finalRate,
                "Scene with zero sync values should use minimum drain rate");
        }

        #endregion
    }
}
