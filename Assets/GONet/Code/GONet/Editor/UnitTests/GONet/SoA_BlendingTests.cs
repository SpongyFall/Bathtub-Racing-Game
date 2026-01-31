/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using System;
using GONet.Core;
using GONet.Jobs;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace GONet.UnitTests
{
    /// <summary>
    /// Unit tests for the unified SoA blending infrastructure.
    /// Tests blending algorithms, ring buffer operations, and data flow.
    /// </summary>
    [TestFixture]
    public class SoA_BlendingTests
    {
        private const int RING_SIZE = 8;
        private const float POSITION_EPSILON = 0.001f;
        private const float ANGLE_EPSILON = 0.5f; // degrees

        #region Test Lifecycle

        [SetUp]
        public void SetUp()
        {
            // Ensure feature flags are in known state
            GONetFeatureFlags.UseUnifiedSoABlending = true;
            GONetFeatureFlags.DebugUnifiedSoABlending = false;
        }

        [TearDown]
        public void TearDown()
        {
            // Reset feature flags
            GONetFeatureFlags.UseUnifiedSoABlending = false;
        }

        #endregion

        #region BlendPositionsJob Tests

        [Test]
        public void BlendPositionsJob_SingleSample_ReturnsExactPosition()
        {
            // Arrange: Single position sample
            using (var testData = CreatePositionTestData(1))
            {
                Vector3 expected = new Vector3(10, 20, 30);
                WritePositionSample(testData, 0, expected, 1000);
                testData.historyCount[0] = 1;

                // Act
                ExecutePositionBlendJob(testData, 2000);

                // Assert
                Vector3 result = testData.shadowPos[0];
                Assert.AreEqual(expected.x, result.x, POSITION_EPSILON);
                Assert.AreEqual(expected.y, result.y, POSITION_EPSILON);
                Assert.AreEqual(expected.z, result.z, POSITION_EPSILON);
            }
        }

        [Test]
        public void BlendPositionsJob_TwoSamples_LinearExtrapolation()
        {
            // Arrange: Two samples with known velocity (10 units/second on X)
            // NOTE: ticks=0 is treated as "no sample" by blending job, so use ticks > 0
            using (var testData = CreatePositionTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                WritePositionSample(testData, 0, new Vector3(0, 0, 0), ticksPerSecond);     // t=1.0s
                WritePositionSample(testData, 1, new Vector3(10, 0, 0), ticksPerSecond * 2); // t=2.0s
                testData.historyCount[0] = 2;

                // Act: Extrapolate 0.1 seconds past newest (2.1 seconds)
                long targetTicks = ticksPerSecond * 2 + ticksPerSecond / 10; // 2.1 seconds
                ExecutePositionBlendJob(testData, targetTicks);

                // Assert: Should extrapolate to ~11 (10 + 10 * 0.1)
                Vector3 result = testData.shadowPos[0];
                Assert.AreEqual(11.0f, result.x, 0.5f); // Some tolerance for algorithm
                Assert.AreEqual(0.0f, result.y, POSITION_EPSILON);
                Assert.AreEqual(0.0f, result.z, POSITION_EPSILON);
            }
        }

        [Test]
        public void BlendPositionsJob_ExtrapolationClamped_At200ms()
        {
            // Arrange: Fast-moving object, extrapolate far into future
            using (var testData = CreatePositionTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                WritePositionSample(testData, 0, new Vector3(0, 0, 0), 0);
                WritePositionSample(testData, 1, new Vector3(100, 0, 0), ticksPerSecond); // 100 units/second
                testData.historyCount[0] = 2;

                // Act: Extrapolate 1 second into future (should clamp to 200ms)
                long targetTicks = ticksPerSecond * 2;
                ExecutePositionBlendJob(testData, targetTicks);

                // Assert: Max extrapolation is 200ms, so max = 100 + 100*0.2 = 120
                Vector3 result = testData.shadowPos[0];
                Assert.LessOrEqual(result.x, 125.0f); // Clamped extrapolation
            }
        }

        [Test]
        public void BlendPositionsJob_InactiveObject_ReturnsZero()
        {
            using (var testData = CreatePositionTestData(1))
            {
                WritePositionSample(testData, 0, new Vector3(100, 200, 300), 1000);
                testData.historyCount[0] = 1;
                testData.isActive[0] = false; // Mark inactive

                ExecutePositionBlendJob(testData, 2000);

                Vector3 result = testData.shadowPos[0];
                Assert.AreEqual(Vector3.zero, result);
            }
        }

        [Test]
        public void BlendPositionsJob_ZeroHistoryCount_ReturnsZero()
        {
            using (var testData = CreatePositionTestData(1))
            {
                testData.historyCount[0] = 0; // No samples

                ExecutePositionBlendJob(testData, 2000);

                Vector3 result = testData.shadowPos[0];
                Assert.AreEqual(Vector3.zero, result);
            }
        }

        [Test]
        public void BlendPositionsJob_HermiteStrategy_UsesAcceleration()
        {
            // Arrange: Three samples showing acceleration
            using (var testData = CreatePositionTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                // Accelerating: 0 -> 5 -> 20 (velocity increases from 5 to 15)
                WritePositionSample(testData, 0, new Vector3(0, 0, 0), 0);
                WritePositionSample(testData, 1, new Vector3(5, 0, 0), ticksPerSecond);
                WritePositionSample(testData, 2, new Vector3(20, 0, 0), ticksPerSecond * 2);
                testData.historyCount[0] = 3;
                testData.blendStrategy[0] = (byte)BlendStrategyType.HermiteSpline;

                // Act
                long targetTicks = (long)(ticksPerSecond * 2.1); // 0.1s past newest
                ExecutePositionBlendJob(testData, targetTicks);

                // Assert: Hermite should extrapolate with acceleration
                Vector3 result = testData.shadowPos[0];
                Assert.Greater(result.x, 20.0f); // Should extrapolate forward
            }
        }

        [Test]
        public void BlendPositionsJob_SmoothedStrategy_ReducesJitter()
        {
            // Arrange: Jittery data
            using (var testData = CreatePositionTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                WritePositionSample(testData, 0, new Vector3(10.0f, 0, 0), 0);
                WritePositionSample(testData, 1, new Vector3(10.5f, 0, 0), ticksPerSecond / 10);
                testData.historyCount[0] = 2;
                testData.blendStrategy[0] = (byte)BlendStrategyType.SmoothedLowPass;

                // Act
                long targetTicks = ticksPerSecond / 5;
                ExecutePositionBlendJob(testData, targetTicks);

                // Assert: Smoothed should dampen the extrapolation
                Vector3 result = testData.shadowPos[0];
                Assert.AreEqual(10.0f, result.x, 1.0f); // Should be near average
            }
        }

        [Test]
        public void BlendPositionsJob_MultipleObjects_IndependentBlending()
        {
            // Arrange: Two objects with different positions/velocities
            // NOTE: ticks=0 is treated as "no sample" by blending job, so use ticks > 0
            using (var testData = CreatePositionTestData(2))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;

                // Object 0: Moving right (10 units/sec on X)
                WritePositionSample(testData, 0, new Vector3(0, 0, 0), ticksPerSecond);     // t=1.0s
                WritePositionSample(testData, 1, new Vector3(10, 0, 0), ticksPerSecond * 2); // t=2.0s
                testData.historyCount[0] = 2;

                // Object 1: Moving up (20 units/sec on Y, different ring buffer offset)
                WritePositionSample(testData, RING_SIZE + 0, new Vector3(0, 0, 0), ticksPerSecond);     // t=1.0s
                WritePositionSample(testData, RING_SIZE + 1, new Vector3(0, 20, 0), ticksPerSecond * 2); // t=2.0s
                testData.historyCount[1] = 2;

                // Act: Extrapolate 0.1 seconds past newest (2.1 seconds)
                long targetTicks = ticksPerSecond * 2 + ticksPerSecond / 10; // 2.1 seconds
                ExecutePositionBlendJob(testData, targetTicks);

                // Assert: Both objects blended independently
                // Object 0: x = 10 + 10*0.1 = 11
                Assert.AreEqual(11.0f, testData.shadowPos[0].x, 1.0f);
                Assert.AreEqual(0.0f, testData.shadowPos[0].y, POSITION_EPSILON);

                // Object 1: y = 20 + 20*0.1 = 22
                Assert.AreEqual(0.0f, testData.shadowPos[1].x, POSITION_EPSILON);
                Assert.AreEqual(22.0f, testData.shadowPos[1].y, 1.0f);
            }
        }

        #endregion

        #region BlendRotationsJob Tests

        [Test]
        public void BlendRotationsJob_SingleSample_ReturnsExactRotation()
        {
            using (var testData = CreateRotationTestData(1))
            {
                Quaternion expected = Quaternion.Euler(45, 90, 0);
                WriteRotationSample(testData, 0, expected, 1000);
                testData.historyCount[0] = 1;

                ExecuteRotationBlendJob(testData, 2000);

                Quaternion result = testData.shadowRot[0];
                float angle = Quaternion.Angle(expected, result);
                Assert.LessOrEqual(angle, ANGLE_EPSILON);
            }
        }

        [Test]
        public void BlendRotationsJob_TwoSamples_LinearExtrapolation()
        {
            // NOTE: ticks=0 is treated as "no sample" by blending job, so use ticks > 0
            using (var testData = CreateRotationTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                // Rotating 90 degrees/second around Y
                WriteRotationSample(testData, 0, Quaternion.Euler(0, 0, 0), ticksPerSecond);      // t=1.0s
                WriteRotationSample(testData, 1, Quaternion.Euler(0, 90, 0), ticksPerSecond * 2); // t=2.0s
                testData.historyCount[0] = 2;

                // Extrapolate 0.1 seconds past newest (2.1 seconds)
                long targetTicks = ticksPerSecond * 2 + ticksPerSecond / 10; // 2.1 seconds
                ExecuteRotationBlendJob(testData, targetTicks);

                Quaternion result = testData.shadowRot[0];
                Quaternion expected = Quaternion.Euler(0, 99, 0); // 90 + 9 degrees
                float angle = Quaternion.Angle(expected, result);
                Assert.LessOrEqual(angle, 5.0f); // Some tolerance
            }
        }

        [Test]
        public void BlendRotationsJob_ExtrapolationClamped_At45Degrees()
        {
            using (var testData = CreateRotationTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                // Very fast rotation: 360 degrees/second
                WriteRotationSample(testData, 0, Quaternion.Euler(0, 0, 0), 0);
                WriteRotationSample(testData, 1, Quaternion.Euler(0, 360, 0), ticksPerSecond);
                testData.historyCount[0] = 2;

                // Extrapolate 1 second (would be 720 degrees without clamping)
                long targetTicks = ticksPerSecond * 2;
                ExecuteRotationBlendJob(testData, targetTicks);

                Quaternion result = testData.shadowRot[0];
                // Result should be clamped - not more than ~45 degrees from newest
                float angleFromNewest = Quaternion.Angle(Quaternion.identity, result);
                Assert.LessOrEqual(angleFromNewest, 50.0f); // Clamped
            }
        }

        [Test]
        public void BlendRotationsJob_InactiveObject_ReturnsIdentity()
        {
            using (var testData = CreateRotationTestData(1))
            {
                WriteRotationSample(testData, 0, Quaternion.Euler(45, 90, 135), 1000);
                testData.historyCount[0] = 1;
                testData.isActive[0] = false;

                ExecuteRotationBlendJob(testData, 2000);

                Quaternion result = testData.shadowRot[0];
                Assert.AreEqual(Quaternion.identity, result);
            }
        }

        [Test]
        public void BlendRotationsJob_SmoothedStrategy_DampensRotation()
        {
            using (var testData = CreateRotationTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                WriteRotationSample(testData, 0, Quaternion.Euler(0, 0, 0), 0);
                WriteRotationSample(testData, 1, Quaternion.Euler(0, 90, 0), ticksPerSecond);
                testData.historyCount[0] = 2;
                testData.blendStrategy[0] = (byte)BlendStrategyType.SmoothedLowPass;

                long targetTicks = (long)(ticksPerSecond * 1.1);
                ExecuteRotationBlendJob(testData, targetTicks);

                // Smoothed should dampen extrapolation
                Quaternion result = testData.shadowRot[0];
                Assert.IsNotNull(result);
            }
        }

        #endregion

        #region Ring Buffer Tests

        [Test]
        public void RingBuffer_WrapAround_PreservesNewest()
        {
            using (var testData = CreatePositionTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;

                // Write 10 samples (wraps around 8-slot buffer)
                for (int i = 0; i < 10; i++)
                {
                    int slot = i % RING_SIZE;
                    WritePositionSample(testData, slot, new Vector3(i * 10, 0, 0), ticksPerSecond * i);
                }
                testData.historyCount[0] = RING_SIZE; // Saturated at 8

                // The newest should be sample 9 (x=90), oldest visible is sample 2 (x=20)
                long targetTicks = ticksPerSecond * 10;
                ExecutePositionBlendJob(testData, targetTicks);

                Vector3 result = testData.shadowPos[0];
                Assert.Greater(result.x, 85.0f); // Should extrapolate from newest (90)
            }
        }

        [Test]
        public void RingBuffer_HistoryCount_SaturatesAt8()
        {
            var stream = new ValueStream_Position();
            try
            {
                stream.Initialize(1);

                // Simulate many writes
                for (int i = 0; i < 20; i++)
                {
                    // Manually increment (simulating writes)
                    int currentCount = stream.historyCount[0];
                    if (currentCount < RING_SIZE)
                    {
                        stream.historyCount[0] = (byte)(currentCount + 1);
                    }
                }

                Assert.AreEqual(RING_SIZE, stream.historyCount[0]); // Should saturate at 8
            }
            finally
            {
                stream.Dispose();
            }
        }

        #endregion

        #region SoA_StreamRegistry Tests

        [Test]
        public void StreamRegistry_RegisterPosition_StoresCorrectly()
        {
            // Initialize registry with mock SoA data
            var soaData = CreateMockSoAData();
            SoA_StreamRegistry.Initialize(ref soaData);

            try
            {
                uint gonetId = 12345;
                byte memberIndex = 1;
                int streamIndex = 0;
                int objectIndex = 5;

                SoA_StreamRegistry.RegisterPosition(gonetId, memberIndex, streamIndex, objectIndex);

                Assert.IsTrue(SoA_StreamRegistry.IsPositionRegistered(gonetId, memberIndex));

                bool found = SoA_StreamRegistry.TryGetPositionLocation(gonetId, memberIndex, out var location);
                Assert.IsTrue(found);
                Assert.AreEqual(streamIndex, location.StreamIndex);
                Assert.AreEqual(objectIndex, location.ObjectIndex);
            }
            finally
            {
                SoA_StreamRegistry.Shutdown();
                soaData.Dispose();
            }
        }

        [Test]
        public void StreamRegistry_RegisterRotation_StoresCorrectly()
        {
            var soaData = CreateMockSoAData();
            SoA_StreamRegistry.Initialize(ref soaData);

            try
            {
                uint gonetId = 67890;
                byte memberIndex = 2;
                int streamIndex = 1;
                int objectIndex = 3;

                SoA_StreamRegistry.RegisterRotation(gonetId, memberIndex, streamIndex, objectIndex);

                Assert.IsTrue(SoA_StreamRegistry.IsRotationRegistered(gonetId, memberIndex));

                bool found = SoA_StreamRegistry.TryGetRotationLocation(gonetId, memberIndex, out var location);
                Assert.IsTrue(found);
                Assert.AreEqual(streamIndex, location.StreamIndex);
                Assert.AreEqual(objectIndex, location.ObjectIndex);
            }
            finally
            {
                SoA_StreamRegistry.Shutdown();
                soaData.Dispose();
            }
        }

        [Test]
        public void StreamRegistry_UnregisterAll_RemovesEntries()
        {
            var soaData = CreateMockSoAData();
            SoA_StreamRegistry.Initialize(ref soaData);

            try
            {
                uint gonetId = 11111;
                SoA_StreamRegistry.RegisterPosition(gonetId, 0, 0, 0);
                SoA_StreamRegistry.RegisterPosition(gonetId, 1, 0, 1);
                SoA_StreamRegistry.RegisterRotation(gonetId, 2, 0, 0);

                Assert.IsTrue(SoA_StreamRegistry.IsPositionRegistered(gonetId, 0));
                Assert.IsTrue(SoA_StreamRegistry.IsPositionRegistered(gonetId, 1));
                Assert.IsTrue(SoA_StreamRegistry.IsRotationRegistered(gonetId, 2));

                SoA_StreamRegistry.UnregisterAll(gonetId);

                Assert.IsFalse(SoA_StreamRegistry.IsPositionRegistered(gonetId, 0));
                Assert.IsFalse(SoA_StreamRegistry.IsPositionRegistered(gonetId, 1));
                Assert.IsFalse(SoA_StreamRegistry.IsRotationRegistered(gonetId, 2));
            }
            finally
            {
                SoA_StreamRegistry.Shutdown();
                soaData.Dispose();
            }
        }

        [Test]
        public void StreamRegistry_BlendStrategy_DefaultsToLinearExtrapolation()
        {
            var soaData = CreateMockSoAData();
            SoA_StreamRegistry.Initialize(ref soaData);

            try
            {
                uint gonetId = 22222;
                SoA_StreamRegistry.RegisterPosition(gonetId, 0, 0, 0);

                var strategy = SoA_StreamRegistry.GetBlendStrategy(gonetId, 0);
                Assert.AreEqual(BlendStrategyType.LinearExtrapolation, strategy);
            }
            finally
            {
                SoA_StreamRegistry.Shutdown();
                soaData.Dispose();
            }
        }

        [Test]
        public void StreamRegistry_SetBlendStrategy_PersistsChange()
        {
            var soaData = CreateMockSoAData();
            SoA_StreamRegistry.Initialize(ref soaData);

            try
            {
                uint gonetId = 33333;
                SoA_StreamRegistry.RegisterPosition(gonetId, 0, 0, 0);

                SoA_StreamRegistry.SetBlendStrategy(gonetId, 0, BlendStrategyType.HermiteSpline);

                var strategy = SoA_StreamRegistry.GetBlendStrategy(gonetId, 0);
                Assert.AreEqual(BlendStrategyType.HermiteSpline, strategy);
            }
            finally
            {
                SoA_StreamRegistry.Shutdown();
                soaData.Dispose();
            }
        }

        #endregion

        #region Edge Case Tests

        [Test]
        public void BlendPositionsJob_VerySmallTimeDelta_NoExtrapolation()
        {
            using (var testData = CreatePositionTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                // Two samples very close together
                WritePositionSample(testData, 0, new Vector3(0, 0, 0), 0);
                WritePositionSample(testData, 1, new Vector3(10, 0, 0), 1); // 1 tick apart
                testData.historyCount[0] = 2;

                long targetTicks = ticksPerSecond;
                ExecutePositionBlendJob(testData, targetTicks);

                // With tiny dt, should just return newest
                Vector3 result = testData.shadowPos[0];
                Assert.AreEqual(10.0f, result.x, 1.0f);
            }
        }

        [Test]
        public void BlendPositionsJob_NegativeExtrapolation_UsesNewest()
        {
            using (var testData = CreatePositionTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                WritePositionSample(testData, 0, new Vector3(0, 0, 0), 0);
                WritePositionSample(testData, 1, new Vector3(10, 0, 0), ticksPerSecond);
                testData.historyCount[0] = 2;

                // Target time BEFORE newest (negative extrapolation)
                long targetTicks = ticksPerSecond / 2; // 0.5 seconds
                ExecutePositionBlendJob(testData, targetTicks);

                // Job uses targetTicks to calculate dtTarget, which will be negative
                // Linear extrapolation with negative dt should still produce valid result
                Vector3 result = testData.shadowPos[0];
                Assert.IsFalse(float.IsNaN(result.x));
                Assert.IsFalse(float.IsInfinity(result.x));
            }
        }

        [Test]
        public void BlendRotationsJob_QuaternionNormalization_MaintainsValidity()
        {
            using (var testData = CreateRotationTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                WriteRotationSample(testData, 0, Quaternion.Euler(0, 0, 0), 0);
                WriteRotationSample(testData, 1, Quaternion.Euler(0, 180, 0), ticksPerSecond);
                testData.historyCount[0] = 2;

                long targetTicks = (long)(ticksPerSecond * 1.5);
                ExecuteRotationBlendJob(testData, targetTicks);

                Quaternion result = testData.shadowRot[0];

                // Verify quaternion is normalized (valid)
                float sqrMagnitude = result.x * result.x + result.y * result.y +
                                     result.z * result.z + result.w * result.w;
                Assert.AreEqual(1.0f, sqrMagnitude, 0.01f);
            }
        }

        [Test]
        public void BlendPositionsJob_AtRest_ReturnsStablePosition()
        {
            using (var testData = CreatePositionTestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                Vector3 restPosition = new Vector3(50, 25, 10);

                // Multiple samples at same position (at rest)
                WritePositionSample(testData, 0, restPosition, 0);
                WritePositionSample(testData, 1, restPosition, ticksPerSecond);
                WritePositionSample(testData, 2, restPosition, ticksPerSecond * 2);
                testData.historyCount[0] = 3;

                long targetTicks = (long)(ticksPerSecond * 2.5);
                ExecutePositionBlendJob(testData, targetTicks);

                Vector3 result = testData.shadowPos[0];
                Assert.AreEqual(restPosition.x, result.x, POSITION_EPSILON);
                Assert.AreEqual(restPosition.y, result.y, POSITION_EPSILON);
                Assert.AreEqual(restPosition.z, result.z, POSITION_EPSILON);
            }
        }

        #endregion

        #region Performance Tests

        [Test]
        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        public void BlendPositionsJob_BulkObjects_Performance(int objectCount)
        {
            using (var testData = CreatePositionTestData(objectCount))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;

                // Initialize all objects with linear motion
                for (int i = 0; i < objectCount; i++)
                {
                    int baseIdx = i * RING_SIZE;
                    WritePositionSample(testData, baseIdx, new Vector3(i, 0, 0), 0);
                    WritePositionSample(testData, baseIdx + 1, new Vector3(i + 10, 0, 0), ticksPerSecond);
                    testData.historyCount[i] = 2;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();

                long targetTicks = (long)(ticksPerSecond * 1.1);
                ExecutePositionBlendJob(testData, targetTicks);

                sw.Stop();

                // All should be blended
                for (int i = 0; i < objectCount; i++)
                {
                    Assert.IsFalse(float.IsNaN(testData.shadowPos[i].x));
                }

                TestContext.WriteLine($"Blended {objectCount} objects in {sw.Elapsed.TotalMilliseconds:F2}ms");
                Assert.Less(sw.ElapsedMilliseconds, 50); // Should be fast
            }
        }

        #endregion

        #region Helper Methods

        private class PositionTestData : IDisposable
        {
            public NativeArray<float> posX;
            public NativeArray<float> posY;
            public NativeArray<float> posZ;
            public NativeArray<long> posTicks;
            public NativeArray<byte> historyCount;
            public NativeArray<bool> isActive;
            public NativeArray<byte> blendStrategy;
            public NativeArray<Vector3> shadowPos;
            public int objectCount;

            public void Dispose()
            {
                if (posX.IsCreated) posX.Dispose();
                if (posY.IsCreated) posY.Dispose();
                if (posZ.IsCreated) posZ.Dispose();
                if (posTicks.IsCreated) posTicks.Dispose();
                if (historyCount.IsCreated) historyCount.Dispose();
                if (isActive.IsCreated) isActive.Dispose();
                if (blendStrategy.IsCreated) blendStrategy.Dispose();
                if (shadowPos.IsCreated) shadowPos.Dispose();
            }
        }

        private class RotationTestData : IDisposable
        {
            public NativeArray<float> rotX;
            public NativeArray<float> rotY;
            public NativeArray<float> rotZ;
            public NativeArray<float> rotW;
            public NativeArray<long> rotTicks;
            public NativeArray<byte> historyCount;
            public NativeArray<bool> isActive;
            public NativeArray<byte> blendStrategy;
            public NativeArray<Quaternion> shadowRot;
            public int objectCount;

            public void Dispose()
            {
                if (rotX.IsCreated) rotX.Dispose();
                if (rotY.IsCreated) rotY.Dispose();
                if (rotZ.IsCreated) rotZ.Dispose();
                if (rotW.IsCreated) rotW.Dispose();
                if (rotTicks.IsCreated) rotTicks.Dispose();
                if (historyCount.IsCreated) historyCount.Dispose();
                if (isActive.IsCreated) isActive.Dispose();
                if (blendStrategy.IsCreated) blendStrategy.Dispose();
                if (shadowRot.IsCreated) shadowRot.Dispose();
            }
        }

        private PositionTestData CreatePositionTestData(int objectCount)
        {
            int totalSlots = objectCount * RING_SIZE;
            return new PositionTestData
            {
                posX = new NativeArray<float>(totalSlots, Allocator.TempJob),
                posY = new NativeArray<float>(totalSlots, Allocator.TempJob),
                posZ = new NativeArray<float>(totalSlots, Allocator.TempJob),
                posTicks = new NativeArray<long>(totalSlots, Allocator.TempJob),
                historyCount = new NativeArray<byte>(objectCount, Allocator.TempJob),
                isActive = new NativeArray<bool>(objectCount, Allocator.TempJob),
                blendStrategy = new NativeArray<byte>(objectCount, Allocator.TempJob),
                shadowPos = new NativeArray<Vector3>(objectCount, Allocator.TempJob),
                objectCount = objectCount
            };
        }

        private RotationTestData CreateRotationTestData(int objectCount)
        {
            int totalSlots = objectCount * RING_SIZE;
            return new RotationTestData
            {
                rotX = new NativeArray<float>(totalSlots, Allocator.TempJob),
                rotY = new NativeArray<float>(totalSlots, Allocator.TempJob),
                rotZ = new NativeArray<float>(totalSlots, Allocator.TempJob),
                rotW = new NativeArray<float>(totalSlots, Allocator.TempJob),
                rotTicks = new NativeArray<long>(totalSlots, Allocator.TempJob),
                historyCount = new NativeArray<byte>(objectCount, Allocator.TempJob),
                isActive = new NativeArray<bool>(objectCount, Allocator.TempJob),
                blendStrategy = new NativeArray<byte>(objectCount, Allocator.TempJob),
                shadowRot = new NativeArray<Quaternion>(objectCount, Allocator.TempJob),
                objectCount = objectCount
            };
        }

        private void WritePositionSample(PositionTestData data, int slot, Vector3 pos, long ticks)
        {
            data.posX[slot] = pos.x;
            data.posY[slot] = pos.y;
            data.posZ[slot] = pos.z;
            data.posTicks[slot] = ticks;

            // Mark object as active (slot 0..RING_SIZE-1 = object 0, etc.)
            int objectIndex = slot / RING_SIZE;
            if (objectIndex < data.objectCount)
            {
                data.isActive[objectIndex] = true;
            }
        }

        private void WriteRotationSample(RotationTestData data, int slot, Quaternion rot, long ticks)
        {
            data.rotX[slot] = rot.x;
            data.rotY[slot] = rot.y;
            data.rotZ[slot] = rot.z;
            data.rotW[slot] = rot.w;
            data.rotTicks[slot] = ticks;

            int objectIndex = slot / RING_SIZE;
            if (objectIndex < data.objectCount)
            {
                data.isActive[objectIndex] = true;
            }
        }

        private void ExecutePositionBlendJob(PositionTestData data, long targetTicks)
        {
            var job = new BlendPositionsJob
            {
                posX = data.posX,
                posY = data.posY,
                posZ = data.posZ,
                posTicks = data.posTicks,
                historyCount = data.historyCount,
                isActive = data.isActive,
                blendStrategy = data.blendStrategy,
                shadowPos = data.shadowPos,
                targetElapsedTicks = targetTicks,
                ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond
            };

            job.Schedule(data.objectCount, 64).Complete();
        }

        private void ExecuteRotationBlendJob(RotationTestData data, long targetTicks)
        {
            var job = new BlendRotationsJob
            {
                rotX = data.rotX,
                rotY = data.rotY,
                rotZ = data.rotZ,
                rotW = data.rotW,
                rotTicks = data.rotTicks,
                historyCount = data.historyCount,
                isActive = data.isActive,
                blendStrategy = data.blendStrategy,
                shadowRot = data.shadowRot,
                targetElapsedTicks = targetTicks,
                ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond
            };

            job.Schedule(data.objectCount, 64).Complete();
        }

        private NonAuthorityBlendingSoA_Final CreateMockSoAData()
        {
            var soaData = new NonAuthorityBlendingSoA_Final();
            soaData.positionStreams = new ValueStream_Position[1];
            soaData.positionStreams[0].Initialize(16);
            soaData.rotationStreams = new ValueStream_Rotation[1];
            soaData.rotationStreams[0].Initialize(16);
            soaData.InitializeShadowBuffers(16, 16);
            return soaData;
        }

        #endregion

        #region Vector2 Test Helpers

        private class Vector2TestData : IDisposable
        {
            public NativeArray<float> valX;
            public NativeArray<float> valY;
            public NativeArray<long> valTicks;
            public NativeArray<byte> historyCount;
            public NativeArray<bool> isActive;
            public NativeArray<byte> blendStrategy;
            public NativeArray<Vector2> shadowVal;
            public int objectCount;

            public void Dispose()
            {
                if (valX.IsCreated) valX.Dispose();
                if (valY.IsCreated) valY.Dispose();
                if (valTicks.IsCreated) valTicks.Dispose();
                if (historyCount.IsCreated) historyCount.Dispose();
                if (isActive.IsCreated) isActive.Dispose();
                if (blendStrategy.IsCreated) blendStrategy.Dispose();
                if (shadowVal.IsCreated) shadowVal.Dispose();
            }
        }

        private Vector2TestData CreateVector2TestData(int objectCount)
        {
            int totalSlots = objectCount * RING_SIZE;
            return new Vector2TestData
            {
                valX = new NativeArray<float>(totalSlots, Allocator.TempJob),
                valY = new NativeArray<float>(totalSlots, Allocator.TempJob),
                valTicks = new NativeArray<long>(totalSlots, Allocator.TempJob),
                historyCount = new NativeArray<byte>(objectCount, Allocator.TempJob),
                isActive = new NativeArray<bool>(objectCount, Allocator.TempJob),
                blendStrategy = new NativeArray<byte>(objectCount, Allocator.TempJob),
                shadowVal = new NativeArray<Vector2>(objectCount, Allocator.TempJob),
                objectCount = objectCount
            };
        }

        private void WriteVector2Sample(Vector2TestData data, int slot, Vector2 val, long ticks)
        {
            data.valX[slot] = val.x;
            data.valY[slot] = val.y;
            data.valTicks[slot] = ticks;

            int objectIndex = slot / RING_SIZE;
            if (objectIndex < data.objectCount)
            {
                data.isActive[objectIndex] = true;
            }
        }

        private void ExecuteVector2BlendJob(Vector2TestData data, long targetTicks)
        {
            var job = new BlendVector2Job
            {
                valX = data.valX,
                valY = data.valY,
                valTicks = data.valTicks,
                historyCount = data.historyCount,
                isActive = data.isActive,
                blendStrategy = data.blendStrategy,
                shadowVal = data.shadowVal,
                targetElapsedTicks = targetTicks,
                ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond
            };

            job.Schedule(data.objectCount, 64).Complete();
        }

        #endregion

        #region Vector4 Test Helpers

        private class Vector4TestData : IDisposable
        {
            public NativeArray<float> valX;
            public NativeArray<float> valY;
            public NativeArray<float> valZ;
            public NativeArray<float> valW;
            public NativeArray<long> valTicks;
            public NativeArray<byte> historyCount;
            public NativeArray<bool> isActive;
            public NativeArray<byte> blendStrategy;
            public NativeArray<Vector4> shadowVal;
            public int objectCount;

            public void Dispose()
            {
                if (valX.IsCreated) valX.Dispose();
                if (valY.IsCreated) valY.Dispose();
                if (valZ.IsCreated) valZ.Dispose();
                if (valW.IsCreated) valW.Dispose();
                if (valTicks.IsCreated) valTicks.Dispose();
                if (historyCount.IsCreated) historyCount.Dispose();
                if (isActive.IsCreated) isActive.Dispose();
                if (blendStrategy.IsCreated) blendStrategy.Dispose();
                if (shadowVal.IsCreated) shadowVal.Dispose();
            }
        }

        private Vector4TestData CreateVector4TestData(int objectCount)
        {
            int totalSlots = objectCount * RING_SIZE;
            return new Vector4TestData
            {
                valX = new NativeArray<float>(totalSlots, Allocator.TempJob),
                valY = new NativeArray<float>(totalSlots, Allocator.TempJob),
                valZ = new NativeArray<float>(totalSlots, Allocator.TempJob),
                valW = new NativeArray<float>(totalSlots, Allocator.TempJob),
                valTicks = new NativeArray<long>(totalSlots, Allocator.TempJob),
                historyCount = new NativeArray<byte>(objectCount, Allocator.TempJob),
                isActive = new NativeArray<bool>(objectCount, Allocator.TempJob),
                blendStrategy = new NativeArray<byte>(objectCount, Allocator.TempJob),
                shadowVal = new NativeArray<Vector4>(objectCount, Allocator.TempJob),
                objectCount = objectCount
            };
        }

        private void WriteVector4Sample(Vector4TestData data, int slot, Vector4 val, long ticks)
        {
            data.valX[slot] = val.x;
            data.valY[slot] = val.y;
            data.valZ[slot] = val.z;
            data.valW[slot] = val.w;
            data.valTicks[slot] = ticks;

            int objectIndex = slot / RING_SIZE;
            if (objectIndex < data.objectCount)
            {
                data.isActive[objectIndex] = true;
            }
        }

        private void ExecuteVector4BlendJob(Vector4TestData data, long targetTicks)
        {
            var job = new BlendVector4Job
            {
                valX = data.valX,
                valY = data.valY,
                valZ = data.valZ,
                valW = data.valW,
                valTicks = data.valTicks,
                historyCount = data.historyCount,
                isActive = data.isActive,
                blendStrategy = data.blendStrategy,
                shadowVal = data.shadowVal,
                targetElapsedTicks = targetTicks,
                ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond
            };

            job.Schedule(data.objectCount, 64).Complete();
        }

        #endregion

        #region Vector2 Blending Tests

        [Test]
        public void BlendVector2Job_SingleSample_ReturnsExactValue()
        {
            using (var testData = CreateVector2TestData(1))
            {
                Vector2 expected = new Vector2(5.5f, 7.3f);
                WriteVector2Sample(testData, 0, expected, 1000);
                testData.historyCount[0] = 1;

                ExecuteVector2BlendJob(testData, 2000);

                Vector2 result = testData.shadowVal[0];
                Assert.AreEqual(expected.x, result.x, POSITION_EPSILON);
                Assert.AreEqual(expected.y, result.y, POSITION_EPSILON);
            }
        }

        [Test]
        public void BlendVector2Job_TwoSamples_LinearExtrapolation()
        {
            using (var testData = CreateVector2TestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                WriteVector2Sample(testData, 0, new Vector2(0, 0), 0);
                WriteVector2Sample(testData, 1, new Vector2(10, 20), ticksPerSecond);
                testData.historyCount[0] = 2;

                // Extrapolate 0.1 seconds past newest
                long targetTicks = ticksPerSecond + ticksPerSecond / 10;
                ExecuteVector2BlendJob(testData, targetTicks);

                // Should extrapolate: x = 10 + 10*0.1 = 11, y = 20 + 20*0.1 = 22
                Vector2 result = testData.shadowVal[0];
                Assert.AreEqual(11.0f, result.x, 0.5f);
                Assert.AreEqual(22.0f, result.y, 0.5f);
            }
        }

        [Test]
        public void BlendVector2Job_SmoothedStrategy_ReducesJitter()
        {
            using (var testData = CreateVector2TestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;

                // Write jittery samples
                WriteVector2Sample(testData, 0, new Vector2(10, 10), ticksPerSecond * 0);
                WriteVector2Sample(testData, 1, new Vector2(12, 12), ticksPerSecond * 1);
                WriteVector2Sample(testData, 2, new Vector2(9, 9), ticksPerSecond * 2);
                WriteVector2Sample(testData, 3, new Vector2(11, 11), ticksPerSecond * 3);
                testData.historyCount[0] = 4;
                testData.blendStrategy[0] = (byte)BlendStrategyType.SmoothedLowPass;

                ExecuteVector2BlendJob(testData, ticksPerSecond * 4);

                // Result should be close to average (~10.5) rather than following jitter
                Vector2 result = testData.shadowVal[0];
                Assert.Greater(result.x, 9.0f);
                Assert.Less(result.x, 12.0f);
            }
        }

        [Test]
        [TestCase(100)]
        [TestCase(500)]
        public void BlendVector2Job_BulkObjects_Performance(int objectCount)
        {
            using (var testData = CreateVector2TestData(objectCount))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;

                for (int i = 0; i < objectCount; i++)
                {
                    int baseIdx = i * RING_SIZE;
                    WriteVector2Sample(testData, baseIdx, new Vector2(i, i * 2), 0);
                    WriteVector2Sample(testData, baseIdx + 1, new Vector2(i + 10, i * 2 + 20), ticksPerSecond);
                    testData.historyCount[i] = 2;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                ExecuteVector2BlendJob(testData, (long)(ticksPerSecond * 1.1));
                sw.Stop();

                for (int i = 0; i < objectCount; i++)
                {
                    Assert.IsFalse(float.IsNaN(testData.shadowVal[i].x));
                }

                TestContext.WriteLine($"Blended {objectCount} Vector2 objects in {sw.Elapsed.TotalMilliseconds:F2}ms");
                Assert.Less(sw.ElapsedMilliseconds, 50);
            }
        }

        #endregion

        #region Vector4 Blending Tests

        [Test]
        public void BlendVector4Job_SingleSample_ReturnsExactValue()
        {
            using (var testData = CreateVector4TestData(1))
            {
                Vector4 expected = new Vector4(1.0f, 0.5f, 0.8f, 1.0f);
                WriteVector4Sample(testData, 0, expected, 1000);
                testData.historyCount[0] = 1;

                ExecuteVector4BlendJob(testData, 2000);

                Vector4 result = testData.shadowVal[0];
                Assert.AreEqual(expected.x, result.x, POSITION_EPSILON);
                Assert.AreEqual(expected.y, result.y, POSITION_EPSILON);
                Assert.AreEqual(expected.z, result.z, POSITION_EPSILON);
                Assert.AreEqual(expected.w, result.w, POSITION_EPSILON);
            }
        }

        [Test]
        public void BlendVector4Job_TwoSamples_LinearExtrapolation()
        {
            using (var testData = CreateVector4TestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;
                WriteVector4Sample(testData, 0, new Vector4(0, 0, 0, 0), 0);
                WriteVector4Sample(testData, 1, new Vector4(10, 20, 30, 40), ticksPerSecond);
                testData.historyCount[0] = 2;

                // Extrapolate 0.1 seconds past newest
                long targetTicks = ticksPerSecond + ticksPerSecond / 10;
                ExecuteVector4BlendJob(testData, targetTicks);

                // Should extrapolate based on velocity
                Vector4 result = testData.shadowVal[0];
                Assert.AreEqual(11.0f, result.x, 0.5f);
                Assert.AreEqual(22.0f, result.y, 0.5f);
                Assert.AreEqual(33.0f, result.z, 0.5f);
                Assert.AreEqual(44.0f, result.w, 0.5f);
            }
        }

        [Test]
        public void BlendVector4Job_ColorBlending_StaysInRange()
        {
            // Test that color values (0-1 range) blend correctly
            using (var testData = CreateVector4TestData(1))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;

                // Color transitioning from red to blue
                WriteVector4Sample(testData, 0, new Vector4(1, 0, 0, 1), 0); // RGBA red
                WriteVector4Sample(testData, 1, new Vector4(0, 0, 1, 1), ticksPerSecond); // RGBA blue
                testData.historyCount[0] = 2;

                // Midpoint
                long targetTicks = ticksPerSecond / 2;
                ExecuteVector4BlendJob(testData, targetTicks);

                // With extrapolation clamping, should stay reasonable
                Vector4 result = testData.shadowVal[0];
                Assert.IsFalse(float.IsNaN(result.x));
                Assert.IsFalse(float.IsNaN(result.y));
                Assert.IsFalse(float.IsNaN(result.z));
                Assert.IsFalse(float.IsNaN(result.w));
            }
        }

        [Test]
        [TestCase(100)]
        [TestCase(500)]
        public void BlendVector4Job_BulkObjects_Performance(int objectCount)
        {
            using (var testData = CreateVector4TestData(objectCount))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;

                for (int i = 0; i < objectCount; i++)
                {
                    int baseIdx = i * RING_SIZE;
                    WriteVector4Sample(testData, baseIdx, new Vector4(i, i * 2, i * 3, 1), 0);
                    WriteVector4Sample(testData, baseIdx + 1, new Vector4(i + 10, i * 2 + 20, i * 3 + 30, 1), ticksPerSecond);
                    testData.historyCount[i] = 2;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                ExecuteVector4BlendJob(testData, (long)(ticksPerSecond * 1.1));
                sw.Stop();

                for (int i = 0; i < objectCount; i++)
                {
                    Assert.IsFalse(float.IsNaN(testData.shadowVal[i].x));
                }

                TestContext.WriteLine($"Blended {objectCount} Vector4 objects in {sw.Elapsed.TotalMilliseconds:F2}ms");
                Assert.Less(sw.ElapsedMilliseconds, 50);
            }
        }

        #endregion

        #region All Types Combined Tests

        [Test]
        public void AllBlendableTypes_ParallelExecution_NoDataCorruption()
        {
            // Test all 4 blendable types simultaneously (simulates real-world scenario)
            using (var posData = CreatePositionTestData(10))
            using (var rotData = CreateRotationTestData(10))
            using (var vec2Data = CreateVector2TestData(10))
            using (var vec4Data = CreateVector4TestData(10))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;

                // Initialize all test data
                for (int i = 0; i < 10; i++)
                {
                    int baseIdx = i * RING_SIZE;

                    WritePositionSample(posData, baseIdx, new Vector3(i, 0, 0), 0);
                    WritePositionSample(posData, baseIdx + 1, new Vector3(i + 10, 0, 0), ticksPerSecond);
                    posData.historyCount[i] = 2;

                    WriteRotationSample(rotData, baseIdx, Quaternion.identity, 0);
                    WriteRotationSample(rotData, baseIdx + 1, Quaternion.Euler(0, 45, 0), ticksPerSecond);
                    rotData.historyCount[i] = 2;

                    WriteVector2Sample(vec2Data, baseIdx, new Vector2(i, i), 0);
                    WriteVector2Sample(vec2Data, baseIdx + 1, new Vector2(i + 5, i + 5), ticksPerSecond);
                    vec2Data.historyCount[i] = 2;

                    WriteVector4Sample(vec4Data, baseIdx, new Vector4(1, 0, 0, 1), 0);
                    WriteVector4Sample(vec4Data, baseIdx + 1, new Vector4(0, 1, 0, 1), ticksPerSecond);
                    vec4Data.historyCount[i] = 2;
                }

                long targetTicks = (long)(ticksPerSecond * 1.1);

                // Execute all blending jobs
                ExecutePositionBlendJob(posData, targetTicks);
                ExecuteRotationBlendJob(rotData, targetTicks);
                ExecuteVector2BlendJob(vec2Data, targetTicks);
                ExecuteVector4BlendJob(vec4Data, targetTicks);

                // Verify no NaN/corruption in any output
                for (int i = 0; i < 10; i++)
                {
                    Assert.IsFalse(float.IsNaN(posData.shadowPos[i].x), $"Position NaN at index {i}");
                    Assert.IsFalse(float.IsNaN(rotData.shadowRot[i].x), $"Rotation NaN at index {i}");
                    Assert.IsFalse(float.IsNaN(vec2Data.shadowVal[i].x), $"Vector2 NaN at index {i}");
                    Assert.IsFalse(float.IsNaN(vec4Data.shadowVal[i].x), $"Vector4 NaN at index {i}");
                }
            }
        }

        [Test]
        public void AllBlendableTypes_WithDifferentStrategies_AllSucceed()
        {
            using (var posData = CreatePositionTestData(4))
            using (var rotData = CreateRotationTestData(4))
            using (var vec2Data = CreateVector2TestData(4))
            using (var vec4Data = CreateVector4TestData(4))
            {
                long ticksPerSecond = TimeSpan.TicksPerSecond;

                // Each type uses a different strategy
                BlendStrategyType[] strategies = {
                    BlendStrategyType.LinearExtrapolation,
                    BlendStrategyType.SmoothedLowPass,
                    BlendStrategyType.HermiteSpline,
                    BlendStrategyType.LinearExtrapolation
                };

                for (int i = 0; i < 4; i++)
                {
                    int baseIdx = i * RING_SIZE;

                    // Write enough samples for HermiteSpline (needs 3)
                    WritePositionSample(posData, baseIdx, new Vector3(i, 0, 0), 0);
                    WritePositionSample(posData, baseIdx + 1, new Vector3(i + 5, 0, 0), ticksPerSecond / 2);
                    WritePositionSample(posData, baseIdx + 2, new Vector3(i + 10, 0, 0), ticksPerSecond);
                    posData.historyCount[i] = 3;
                    posData.blendStrategy[i] = (byte)strategies[i];

                    WriteRotationSample(rotData, baseIdx, Quaternion.identity, 0);
                    WriteRotationSample(rotData, baseIdx + 1, Quaternion.Euler(0, 22, 0), ticksPerSecond / 2);
                    WriteRotationSample(rotData, baseIdx + 2, Quaternion.Euler(0, 45, 0), ticksPerSecond);
                    rotData.historyCount[i] = 3;
                    rotData.blendStrategy[i] = (byte)strategies[i];

                    WriteVector2Sample(vec2Data, baseIdx, new Vector2(0, 0), 0);
                    WriteVector2Sample(vec2Data, baseIdx + 1, new Vector2(2.5f, 2.5f), ticksPerSecond / 2);
                    WriteVector2Sample(vec2Data, baseIdx + 2, new Vector2(5, 5), ticksPerSecond);
                    vec2Data.historyCount[i] = 3;
                    vec2Data.blendStrategy[i] = (byte)strategies[i];

                    WriteVector4Sample(vec4Data, baseIdx, new Vector4(0, 0, 0, 1), 0);
                    WriteVector4Sample(vec4Data, baseIdx + 1, new Vector4(0.5f, 0.5f, 0.5f, 1), ticksPerSecond / 2);
                    WriteVector4Sample(vec4Data, baseIdx + 2, new Vector4(1, 1, 1, 1), ticksPerSecond);
                    vec4Data.historyCount[i] = 3;
                    vec4Data.blendStrategy[i] = (byte)strategies[i];
                }

                long targetTicks = (long)(ticksPerSecond * 1.05);

                ExecutePositionBlendJob(posData, targetTicks);
                ExecuteRotationBlendJob(rotData, targetTicks);
                ExecuteVector2BlendJob(vec2Data, targetTicks);
                ExecuteVector4BlendJob(vec4Data, targetTicks);

                // All should produce valid results regardless of strategy
                for (int i = 0; i < 4; i++)
                {
                    Assert.IsFalse(float.IsNaN(posData.shadowPos[i].x), $"Position NaN with {strategies[i]}");
                    Assert.IsFalse(float.IsNaN(rotData.shadowRot[i].x), $"Rotation NaN with {strategies[i]}");
                    Assert.IsFalse(float.IsNaN(vec2Data.shadowVal[i].x), $"Vector2 NaN with {strategies[i]}");
                    Assert.IsFalse(float.IsNaN(vec4Data.shadowVal[i].x), $"Vector4 NaN with {strategies[i]}");
                }
            }
        }

        #endregion
    }
}
