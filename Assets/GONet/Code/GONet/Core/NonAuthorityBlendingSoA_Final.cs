/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 *
 *
 * Authorized use is explicitly limited to the following:
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original Unity Asset Store package and the unmodified GONet source
 *
 * All other use cases are explicitly forbidden, including but not limited to:
 * -The ability to modify source code for redistribution
 * -The ability to modify source code for use in products outside the original Unity Asset Store package
 */

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace GONet.Core
{
    /// <summary>
    /// GONet v2: Hz-agnostic stream metadata.
    /// Describes a single blending stream (Type + Hz pair).
    /// </summary>
    public struct SoA_StreamInfo
    {
        public SoA_StreamType streamType;      // VECTOR3, QUATERNION, SCALAR
        public float updateInterval;           // 1/Hz (e.g., 0.041667 for 24Hz)
        public int capacity;                   // Max objects in this stream
        public double nextUpdateTime;          // Scheduler: When to kick next blend job

        // Stream index for fast lookup (index into positionStreams/rotationStreams/scalarStreams)
        public int streamIndex;
    }

    /// <summary>
    /// Stream type enumeration.
    /// </summary>
    public enum SoA_StreamType : byte
    {
        VECTOR3,      // Transform positions
        QUATERNION,   // Transform rotations
        SCALAR,       // Custom scalar fields (float, bool, etc.)
        VECTOR2,      // 2D vector fields (UV, screen coords, etc.)
        VECTOR4       // 4D vector fields (color, tangent, etc.)
    }

    /// <summary>
    /// GONet v2: Structure-of-Arrays (SoA) for non-authority object blending.
    ///
    /// Key Benefits:
    /// - Cache-friendly: All positions together, all rotations together (linear memory access)
    /// - SIMD-friendly: Burst auto-vectorizes 4-8 positions at once (AVX2)
    /// - Zero GC: Everything in NativeArrays (pinned, unmanaged memory)
    /// - Lock-free network writes: Ring buffer with Interlocked atomic ops
    /// - Parallel blending: Process 100+ objects across 8 CPU cores simultaneously
    /// - Design-time sized: Code generator pre-computes exact capacities (zero waste)
    /// - Hz-agnostic: Supports ANY combination of update rates (24Hz, 60Hz, etc.)
    ///
    /// Performance Target: 85% CPU reduction (15% → 2-3%) for 100 objects.
    ///
    /// This structure is dynamically configured at design-time based on discovered
    /// (ValueType, SyncInterval) combinations across all prefabs in the project.
    /// </summary>
    public unsafe struct NonAuthorityBlendingSoA_Final : IDisposable
    {
        // ===== DYNAMIC STREAM ARRAYS (Hz-Agnostic Architecture) =====
        // Each array contains ALL streams of that type, regardless of Hz.
        // Example: If project has Vector3 @ 24Hz AND Vector3 @ 60Hz,
        //          positionStreams.Length = 2

        /// <summary>
        /// All Vector3 streams (positions) discovered from prefab analysis.
        /// Length = number of unique Vector3 Hz rates in project.
        /// NOTE: Uses managed array (not NativeArray) to enable ref access for in-place modification.
        /// The inner NativeArrays (posX, posY, posZ, etc.) remain native for zero-GC operation.
        /// </summary>
        public ValueStream_Position[] positionStreams;

        /// <summary>
        /// All Quaternion streams (rotations) discovered from prefab analysis.
        /// Length = number of unique Quaternion Hz rates in project.
        /// NOTE: Uses managed array for ref access. Inner NativeArrays remain native.
        /// </summary>
        public ValueStream_Rotation[] rotationStreams;

        /// <summary>
        /// All scalar streams (float, bool, etc.) discovered from prefab analysis.
        /// Length = number of unique scalar Hz rates in project.
        /// NOTE: Uses managed array for ref access. Inner NativeArrays remain native.
        /// </summary>
        public ValueStream_Scalars[] scalarStreams;

        /// <summary>
        /// All Vector2 streams discovered from prefab analysis.
        /// Length = number of unique Vector2 Hz rates in project.
        /// NOTE: Uses managed array for ref access. Inner NativeArrays remain native.
        /// </summary>
        public ValueStream_Vector2[] vector2Streams;

        /// <summary>
        /// All Vector4 streams discovered from prefab analysis.
        /// Length = number of unique Vector4 Hz rates in project.
        /// NOTE: Uses managed array for ref access. Inner NativeArrays remain native.
        /// </summary>
        public ValueStream_Vector4[] vector4Streams;

        /// <summary>
        /// Metadata for all position streams (intervals, capacities, scheduler state).
        /// Same length as positionStreams.
        /// </summary>
        public NativeArray<SoA_StreamInfo> positionStreamInfos;

        /// <summary>
        /// Metadata for all rotation streams (intervals, capacities, scheduler state).
        /// Same length as rotationStreams.
        /// </summary>
        public NativeArray<SoA_StreamInfo> rotationStreamInfos;

        /// <summary>
        /// Metadata for all scalar streams (intervals, capacities, scheduler state).
        /// Same length as scalarStreams.
        /// </summary>
        public NativeArray<SoA_StreamInfo> scalarStreamInfos;

        /// <summary>
        /// Metadata for all Vector2 streams (intervals, capacities, scheduler state).
        /// Same length as vector2Streams.
        /// </summary>
        public NativeArray<SoA_StreamInfo> vector2StreamInfos;

        /// <summary>
        /// Metadata for all Vector4 streams (intervals, capacities, scheduler state).
        /// Same length as vector4Streams.
        /// </summary>
        public NativeArray<SoA_StreamInfo> vector4StreamInfos;

        // ===== SHADOW BUFFERS (Transform Apply) =====
        // Double-buffered for 1-frame delayed writes (jobs write to current, Transforms read from previous)

        public NativeArray<Vector3> shadowPositionsA;
        public NativeArray<Vector3> shadowPositionsB;
        public NativeArray<Quaternion> shadowRotationsA;
        public NativeArray<Quaternion> shadowRotationsB;
        public NativeArray<Vector2> shadowVector2A;
        public NativeArray<Vector2> shadowVector2B;
        public NativeArray<Vector4> shadowVector4A;
        public NativeArray<Vector4> shadowVector4B;

        public int currentShadowBuffer; // 0 or 1 (ping-pong)

        // Initialization flag
        private bool isInitialized;

        /// <summary>
        /// Initialize shadow buffers with specified capacity.
        /// Called from GONet_SoA_Descriptor.CreateSoA().
        /// </summary>
        /// <param name="maxPositionCapacity">Max positions across ALL Vector3 streams</param>
        /// <param name="maxRotationCapacity">Max rotations across ALL Quaternion streams</param>
        /// <param name="maxVector2Capacity">Max Vector2 across ALL Vector2 streams</param>
        /// <param name="maxVector4Capacity">Max Vector4 across ALL Vector4 streams</param>
        public void InitializeShadowBuffers(int maxPositionCapacity, int maxRotationCapacity, int maxVector2Capacity = 0, int maxVector4Capacity = 0)
        {
            if (isInitialized)
                return;

            // Double-buffered shadow arrays for 1-frame delayed apply
            shadowPositionsA = new NativeArray<Vector3>(maxPositionCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            shadowPositionsB = new NativeArray<Vector3>(maxPositionCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            shadowRotationsA = new NativeArray<Quaternion>(maxRotationCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            shadowRotationsB = new NativeArray<Quaternion>(maxRotationCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            // Vector2 shadow buffers (only allocate if needed)
            if (maxVector2Capacity > 0)
            {
                shadowVector2A = new NativeArray<Vector2>(maxVector2Capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                shadowVector2B = new NativeArray<Vector2>(maxVector2Capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }

            // Vector4 shadow buffers (only allocate if needed)
            if (maxVector4Capacity > 0)
            {
                shadowVector4A = new NativeArray<Vector4>(maxVector4Capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                shadowVector4B = new NativeArray<Vector4>(maxVector4Capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }

            currentShadowBuffer = 0;
            isInitialized = true;
        }

        /// <summary>
        /// Get current shadow buffer for position writes (job output).
        /// </summary>
        public NativeArray<Vector3> GetCurrentShadowPositions()
        {
            return currentShadowBuffer == 0 ? shadowPositionsA : shadowPositionsB;
        }

        /// <summary>
        /// Get previous shadow buffer for position reads (Transform apply).
        /// </summary>
        public NativeArray<Vector3> GetPreviousShadowPositions()
        {
            return currentShadowBuffer == 0 ? shadowPositionsB : shadowPositionsA;
        }

        /// <summary>
        /// Get current shadow buffer for rotation writes (job output).
        /// </summary>
        public NativeArray<Quaternion> GetCurrentShadowRotations()
        {
            return currentShadowBuffer == 0 ? shadowRotationsA : shadowRotationsB;
        }

        /// <summary>
        /// Get previous shadow buffer for rotation reads (Transform apply).
        /// </summary>
        public NativeArray<Quaternion> GetPreviousShadowRotations()
        {
            return currentShadowBuffer == 0 ? shadowRotationsB : shadowRotationsA;
        }

        /// <summary>
        /// Get current shadow buffer for Vector2 writes (job output).
        /// </summary>
        public NativeArray<Vector2> GetCurrentShadowVector2()
        {
            return currentShadowBuffer == 0 ? shadowVector2A : shadowVector2B;
        }

        /// <summary>
        /// Get previous shadow buffer for Vector2 reads (value apply).
        /// </summary>
        public NativeArray<Vector2> GetPreviousShadowVector2()
        {
            return currentShadowBuffer == 0 ? shadowVector2B : shadowVector2A;
        }

        /// <summary>
        /// Get current shadow buffer for Vector4 writes (job output).
        /// </summary>
        public NativeArray<Vector4> GetCurrentShadowVector4()
        {
            return currentShadowBuffer == 0 ? shadowVector4A : shadowVector4B;
        }

        /// <summary>
        /// Get previous shadow buffer for Vector4 reads (value apply).
        /// </summary>
        public NativeArray<Vector4> GetPreviousShadowVector4()
        {
            return currentShadowBuffer == 0 ? shadowVector4B : shadowVector4A;
        }

        /// <summary>
        /// Swap shadow buffers (ping-pong).
        /// Called after Transform apply completes.
        /// </summary>
        public void SwapShadowBuffers()
        {
            currentShadowBuffer = 1 - currentShadowBuffer;
        }

        /// <summary>
        /// Resize shadow buffers to accommodate larger stream capacities.
        /// Called when any stream resizes beyond current shadow buffer capacity.
        /// </summary>
        public void ResizeShadowBuffersIfNeeded(int requiredPositionCapacity, int requiredRotationCapacity, int requiredVector2Capacity = 0, int requiredVector4Capacity = 0)
        {
            bool needsPositionResize = requiredPositionCapacity > shadowPositionsA.Length;
            bool needsRotationResize = requiredRotationCapacity > shadowRotationsA.Length;
            bool needsVector2Resize = requiredVector2Capacity > 0 && (!shadowVector2A.IsCreated || requiredVector2Capacity > shadowVector2A.Length);
            bool needsVector4Resize = requiredVector4Capacity > 0 && (!shadowVector4A.IsCreated || requiredVector4Capacity > shadowVector4A.Length);

            if (!needsPositionResize && !needsRotationResize && !needsVector2Resize && !needsVector4Resize)
                return;

            GONetLog.Info($"[SoA-Resize] Shadow buffers resizing: positions {shadowPositionsA.Length} → {requiredPositionCapacity}, rotations {shadowRotationsA.Length} → {requiredRotationCapacity}, vector2 {(shadowVector2A.IsCreated ? shadowVector2A.Length : 0)} → {requiredVector2Capacity}, vector4 {(shadowVector4A.IsCreated ? shadowVector4A.Length : 0)} → {requiredVector4Capacity}");

            if (needsPositionResize)
            {
                ResizeNativeArray(ref shadowPositionsA, requiredPositionCapacity);
                ResizeNativeArray(ref shadowPositionsB, requiredPositionCapacity);
            }

            if (needsRotationResize)
            {
                ResizeNativeArray(ref shadowRotationsA, requiredRotationCapacity);
                ResizeNativeArray(ref shadowRotationsB, requiredRotationCapacity);
            }

            if (needsVector2Resize)
            {
                ResizeOrCreateNativeArray(ref shadowVector2A, requiredVector2Capacity);
                ResizeOrCreateNativeArray(ref shadowVector2B, requiredVector2Capacity);
            }

            if (needsVector4Resize)
            {
                ResizeOrCreateNativeArray(ref shadowVector4A, requiredVector4Capacity);
                ResizeOrCreateNativeArray(ref shadowVector4B, requiredVector4Capacity);
            }
        }

        /// <summary>
        /// Helper: Resize a NativeArray (allocate new, copy data, dispose old).
        /// </summary>
        private static void ResizeNativeArray<T>(ref NativeArray<T> array, int newSize) where T : struct
        {
            if (!array.IsCreated || array.Length >= newSize)
                return;

            var newArray = new NativeArray<T>(newSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            NativeArray<T>.Copy(array, newArray, array.Length);
            array.Dispose();
            array = newArray;
        }

        /// <summary>
        /// Helper: Resize or create a NativeArray (handles uninitialized arrays).
        /// </summary>
        private static void ResizeOrCreateNativeArray<T>(ref NativeArray<T> array, int newSize) where T : struct
        {
            if (!array.IsCreated)
            {
                array = new NativeArray<T>(newSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                return;
            }

            if (array.Length >= newSize)
                return;

            var newArray = new NativeArray<T>(newSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            NativeArray<T>.Copy(array, newArray, array.Length);
            array.Dispose();
            array = newArray;
        }

        /// <summary>
        /// Dispose all NativeArrays (cleanup on shutdown).
        /// </summary>
        public void Dispose()
        {
            if (!isInitialized)
                return;

            // Dispose shadow buffers
            if (shadowPositionsA.IsCreated) shadowPositionsA.Dispose();
            if (shadowPositionsB.IsCreated) shadowPositionsB.Dispose();
            if (shadowRotationsA.IsCreated) shadowRotationsA.Dispose();
            if (shadowRotationsB.IsCreated) shadowRotationsB.Dispose();
            if (shadowVector2A.IsCreated) shadowVector2A.Dispose();
            if (shadowVector2B.IsCreated) shadowVector2B.Dispose();
            if (shadowVector4A.IsCreated) shadowVector4A.Dispose();
            if (shadowVector4B.IsCreated) shadowVector4B.Dispose();

            // Dispose stream arrays (managed arrays - use null check, inner NativeArrays need Dispose)
            if (positionStreams != null)
            {
                for (int i = 0; i < positionStreams.Length; i++)
                    positionStreams[i].Dispose();
                positionStreams = null;
            }

            if (rotationStreams != null)
            {
                for (int i = 0; i < rotationStreams.Length; i++)
                    rotationStreams[i].Dispose();
                rotationStreams = null;
            }

            if (scalarStreams != null)
            {
                for (int i = 0; i < scalarStreams.Length; i++)
                    scalarStreams[i].Dispose();
                scalarStreams = null;
            }

            if (vector2Streams != null)
            {
                for (int i = 0; i < vector2Streams.Length; i++)
                    vector2Streams[i].Dispose();
                vector2Streams = null;
            }

            if (vector4Streams != null)
            {
                for (int i = 0; i < vector4Streams.Length; i++)
                    vector4Streams[i].Dispose();
                vector4Streams = null;
            }

            // Dispose metadata arrays
            if (positionStreamInfos.IsCreated) positionStreamInfos.Dispose();
            if (rotationStreamInfos.IsCreated) rotationStreamInfos.Dispose();
            if (scalarStreamInfos.IsCreated) scalarStreamInfos.Dispose();
            if (vector2StreamInfos.IsCreated) vector2StreamInfos.Dispose();
            if (vector4StreamInfos.IsCreated) vector4StreamInfos.Dispose();

            isInitialized = false;
        }

        /// <summary>
        /// Check if SoA is initialized and ready for use.
        /// </summary>
        public bool IsInitialized => isInitialized && shadowPositionsA.IsCreated;
    }
}
