/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using NUnit.Framework;
using UnityEngine;

namespace GONet.UnitTests
{
    /// <summary>
    /// Unit tests for Vector and Quaternion serializers.
    /// Tests AreEqualConsideringQuantization behavior both with and without quantization.
    /// These tests verify the fix for Vector4 change detection when using [GONetAutoMagicalSync(QuantizeDownToBitCount = 0)].
    /// </summary>
    [TestFixture]
    public class VectorSerializerTests
    {
        private const float EPSILON = 0.0001f;

        #region Vector2Serializer Tests

        [Test]
        public void Vector2Serializer_AreEqual_WithoutQuantization_SameValues_ReturnsTrue()
        {
            var serializer = new Vector2Serializer();
            // Don't initialize quantization (simulates QuantizeDownToBitCount = 0)

            var valueA = new GONetSyncableValue { UnityEngine_Vector2 = new Vector2(1.5f, 2.5f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector2 = new Vector2(1.5f, 2.5f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsTrue(result, "Same Vector2 values should be equal");
        }

        [Test]
        public void Vector2Serializer_AreEqual_WithoutQuantization_DifferentValues_ReturnsFalse()
        {
            var serializer = new Vector2Serializer();
            // Don't initialize quantization

            var valueA = new GONetSyncableValue { UnityEngine_Vector2 = new Vector2(1.0f, 2.0f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector2 = new Vector2(1.1f, 2.0f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsFalse(result, "Different Vector2 values should not be equal");
        }

        [Test]
        public void Vector2Serializer_AreEqual_WithQuantization_SameValues_ReturnsTrue()
        {
            var serializer = new Vector2Serializer();
            // Initialize with quantization
            serializer.InitQuantizationSettings(8, -100f, 100f);

            // Exact same values - guaranteed to quantize identically
            var valueA = new GONetSyncableValue { UnityEngine_Vector2 = new Vector2(50.0f, 50.0f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector2 = new Vector2(50.0f, 50.0f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsTrue(result, "Same values should be equal with quantization");
        }

        [Test]
        public void Vector2Serializer_AreEqual_WithQuantization_FarValues_ReturnsFalse()
        {
            var serializer = new Vector2Serializer();
            serializer.InitQuantizationSettings(8, -100f, 100f);

            var valueA = new GONetSyncableValue { UnityEngine_Vector2 = new Vector2(50.0f, 50.0f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector2 = new Vector2(52.0f, 52.0f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsFalse(result, "Values beyond quantization threshold should not be equal");
        }

        #endregion

        #region Vector3Serializer Tests

        [Test]
        public void Vector3Serializer_AreEqual_WithoutQuantization_SameValues_ReturnsTrue()
        {
            var serializer = new Vector3Serializer();

            var valueA = new GONetSyncableValue { UnityEngine_Vector3 = new Vector3(1.5f, 2.5f, 3.5f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector3 = new Vector3(1.5f, 2.5f, 3.5f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsTrue(result, "Same Vector3 values should be equal");
        }

        [Test]
        public void Vector3Serializer_AreEqual_WithoutQuantization_DifferentValues_ReturnsFalse()
        {
            var serializer = new Vector3Serializer();

            var valueA = new GONetSyncableValue { UnityEngine_Vector3 = new Vector3(1.0f, 2.0f, 3.0f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector3 = new Vector3(1.0f, 2.0f, 3.1f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsFalse(result, "Different Vector3 values should not be equal");
        }

        [Test]
        public void Vector3Serializer_AreEqual_WithQuantization_SameValues_ReturnsTrue()
        {
            var serializer = new Vector3Serializer();
            serializer.InitQuantizationSettings(8, -100f, 100f);

            // Exact same values - guaranteed to quantize identically
            var valueA = new GONetSyncableValue { UnityEngine_Vector3 = new Vector3(50.0f, 50.0f, 50.0f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector3 = new Vector3(50.0f, 50.0f, 50.0f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsTrue(result, "Same values should be equal with quantization");
        }

        #endregion

        #region Vector4Serializer Tests

        [Test]
        public void Vector4Serializer_AreEqual_WithoutQuantization_SameValues_ReturnsTrue()
        {
            var serializer = new Vector4Serializer();
            // Don't initialize quantization (simulates QuantizeDownToBitCount = 0)

            var valueA = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(1.0f, 0.5f, 0.8f, 1.0f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(1.0f, 0.5f, 0.8f, 1.0f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsTrue(result, "Same Vector4 values should be equal");
        }

        [Test]
        public void Vector4Serializer_AreEqual_WithoutQuantization_DifferentValues_ReturnsFalse()
        {
            var serializer = new Vector4Serializer();
            // Don't initialize quantization - THIS IS THE KEY TEST FOR OUR FIX

            var valueA = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(1.0f, 0.0f, 0.0f, 1.0f) }; // Red
            var valueB = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(0.0f, 1.0f, 0.0f, 1.0f) }; // Green

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsFalse(result, "Different Vector4 values should NOT be equal - this verifies change detection works");
        }

        [Test]
        public void Vector4Serializer_AreEqual_WithoutQuantization_SmallDifference_ReturnsFalse()
        {
            var serializer = new Vector4Serializer();

            // Small but meaningful difference (e.g., color fading)
            var valueA = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(1.0f, 1.0f, 1.0f, 1.0f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(1.0f, 1.0f, 1.0f, 0.9f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsFalse(result, "Even small differences should be detected without quantization");
        }

        [Test]
        public void Vector4Serializer_AreEqual_WithQuantization_SameValues_ReturnsTrue()
        {
            var serializer = new Vector4Serializer();
            serializer.InitQuantizationSettings(8, 0f, 1f); // 8-bit color quantization

            // Exact same values - guaranteed to quantize identically
            var valueA = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(0.5f, 0.5f, 0.5f, 1.0f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(0.5f, 0.5f, 0.5f, 1.0f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsTrue(result, "Same values should be equal with quantization");
        }

        [Test]
        public void Vector4Serializer_AreEqual_WithQuantization_FarValues_ReturnsFalse()
        {
            var serializer = new Vector4Serializer();
            serializer.InitQuantizationSettings(8, 0f, 1f);

            var valueA = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(0.5f, 0.5f, 0.5f, 1.0f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(0.6f, 0.5f, 0.5f, 1.0f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsFalse(result, "Values beyond quantization threshold should not be equal");
        }

        [Test]
        public void Vector4Serializer_ColorTransition_DetectsChanges()
        {
            // This test simulates the AllBlendableTypesTest scenario
            var serializer = new Vector4Serializer();
            // Don't initialize quantization (like the test uses QuantizeDownToBitCount = 0)

            // Simulate color transitioning over time
            Vector4[] colorSequence = new Vector4[]
            {
                new Vector4(1.0f, 0.0f, 0.0f, 1.0f), // Red
                new Vector4(0.9f, 0.1f, 0.0f, 1.0f), // Transitioning
                new Vector4(0.5f, 0.5f, 0.0f, 1.0f), // Yellow-ish
                new Vector4(0.0f, 1.0f, 0.0f, 1.0f), // Green
            };

            // Each consecutive pair should be detected as different
            for (int i = 0; i < colorSequence.Length - 1; i++)
            {
                var valueA = new GONetSyncableValue { UnityEngine_Vector4 = colorSequence[i] };
                var valueB = new GONetSyncableValue { UnityEngine_Vector4 = colorSequence[i + 1] };

                bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

                Assert.IsFalse(result, $"Color transition from index {i} to {i + 1} should be detected as different");
            }
        }

        #endregion

        #region QuaternionSerializer Tests

        [Test]
        public void QuaternionSerializer_AreEqual_SameRotation_ReturnsTrue()
        {
            // QuaternionSerializer uses internal "smallest-three" quantization with predefined bounds
            // Just use default constructor - it has built-in quantization
            var serializer = new QuaternionSerializer();

            var valueA = new GONetSyncableValue { UnityEngine_Quaternion = Quaternion.Euler(45, 90, 0) };
            var valueB = new GONetSyncableValue { UnityEngine_Quaternion = Quaternion.Euler(45, 90, 0) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsTrue(result, "Same rotations should be equal");
        }

        [Test]
        public void QuaternionSerializer_AreEqual_DifferentRotation_ReturnsFalse()
        {
            // QuaternionSerializer uses internal quantization - just use default constructor
            var serializer = new QuaternionSerializer();

            var valueA = new GONetSyncableValue { UnityEngine_Quaternion = Quaternion.Euler(0, 0, 0) };
            var valueB = new GONetSyncableValue { UnityEngine_Quaternion = Quaternion.Euler(0, 90, 0) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsFalse(result, "Different rotations should not be equal");
        }

        #endregion

        #region Edge Cases

        [Test]
        public void Vector4Serializer_ZeroValues_AreEqual()
        {
            var serializer = new Vector4Serializer();

            var valueA = new GONetSyncableValue { UnityEngine_Vector4 = Vector4.zero };
            var valueB = new GONetSyncableValue { UnityEngine_Vector4 = Vector4.zero };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsTrue(result, "Zero vectors should be equal");
        }

        [Test]
        public void Vector4Serializer_OneValue_AreEqual()
        {
            var serializer = new Vector4Serializer();

            var valueA = new GONetSyncableValue { UnityEngine_Vector4 = Vector4.one };
            var valueB = new GONetSyncableValue { UnityEngine_Vector4 = Vector4.one };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsTrue(result, "One vectors should be equal");
        }

        [Test]
        public void Vector4Serializer_NegativeValues_DetectsDifference()
        {
            var serializer = new Vector4Serializer();

            var valueA = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(-1.0f, -1.0f, -1.0f, -1.0f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector4 = new Vector4(1.0f, 1.0f, 1.0f, 1.0f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsFalse(result, "Negative vs positive values should be different");
        }

        [Test]
        public void Vector2Serializer_WithoutInit_ChangeDetection()
        {
            // Verify Vector2 also works correctly without quantization init
            var serializer = new Vector2Serializer();

            var valueA = new GONetSyncableValue { UnityEngine_Vector2 = new Vector2(0.0f, 0.0f) };
            var valueB = new GONetSyncableValue { UnityEngine_Vector2 = new Vector2(1.0f, 1.0f) };

            bool result = serializer.AreEqualConsideringQuantization(valueA, valueB);

            Assert.IsFalse(result, "Different Vector2 values should be detected without quantization");
        }

        #endregion
    }
}
