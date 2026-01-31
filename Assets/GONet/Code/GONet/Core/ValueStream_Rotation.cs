/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using System;
using Unity.Collections;
using UnityEngine;

namespace GONet.Core
{
    /// <summary>
    /// GONet v2: Rotation (Quaternion) stream with lock-free ring buffers.
    /// Stores history of rotation updates for temporal blending.
    /// </summary>
    public struct ValueStream_Rotation : IDisposable
    {
        // Object tracking
        public NativeArray<uint> gonetIds;           // GONetId for each object slot
        public NativeArray<IntPtr> transformPtrs;    // Weak GCHandle to Transform
        public NativeArray<bool> isActive;           // Is slot active?
        public int activeCount;                      // Number of active objects
        public int capacity;                         // Max objects

        // Ring buffer - Rotation components (Quaternion)
        public NativeArray<float> rotX;              // [objectIndex][historyIndex]
        public NativeArray<float> rotY;
        public NativeArray<float> rotZ;
        public NativeArray<float> rotW;
        public NativeArray<long> rotTicks;           // Timestamp for each sample

        // Ring buffer metadata
        public NativeArray<byte> historyWriteIndex;  // Current write position per object (0-7)
        public NativeArray<byte> historyCount;       // Current samples per object (0-8)
        public const int RING_BUFFER_SIZE = 8;       // History samples per object (lock-free = 8)

        // Blending configuration (Phase 2: pluggable strategies)
        public NativeArray<byte> blendStrategy;      // BlendStrategyType per object (byte for Burst compatibility)

        /// <summary>
        /// Initialize stream with specified capacity.
        /// </summary>
        public void Initialize(int maxCapacity)
        {
            capacity = maxCapacity;
            activeCount = 0;

            // Object tracking arrays
            // CRITICAL FIX (December 2025): Use ClearMemory to zero-initialize arrays.
            // Without this, isActive contains garbage that may evaluate to true,
            // causing the blending job to process uninitialized slots with phantom GONetIds.
            GONetLog.Info($"[SoA-INIT] ValueStream_Rotation.Initialize({maxCapacity}) with ClearMemory fix active");
            gonetIds = new NativeArray<uint>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            transformPtrs = new NativeArray<IntPtr>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            isActive = new NativeArray<bool>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            // Ring buffer arrays (flattened: objectIndex * RING_BUFFER_SIZE + historyIndex)
            int totalSize = maxCapacity * RING_BUFFER_SIZE;
            rotX = new NativeArray<float>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            rotY = new NativeArray<float>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            rotZ = new NativeArray<float>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            rotW = new NativeArray<float>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            rotTicks = new NativeArray<long>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            historyWriteIndex = new NativeArray<byte>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            historyCount = new NativeArray<byte>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            blendStrategy = new NativeArray<byte>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        /// <summary>
        /// Register a new object in the stream.
        /// Returns the object index (slot) where it was added.
        /// </summary>
        public int RegisterObject(uint gonetId, IntPtr transformPtr)
        {
            // Auto-resize if approaching capacity (75% threshold)
            const float GROWTH_THRESHOLD = 0.75f;
            if (activeCount >= capacity * GROWTH_THRESHOLD)
            {
                // Growth strategy: doubling + minimum 64 (Unity NativeList pattern)
                int newCapacity = UnityEngine.Mathf.Max(capacity * 2, capacity + 64);
                Resize(newCapacity);
            }

            if (activeCount >= capacity)
            {
                GONetLog.Error($"[SoA] Rotation stream STILL full after resize! Cannot register GONetId {gonetId} (capacity: {capacity})");
                return -1;
            }

            int index = activeCount;
            gonetIds[index] = gonetId;
            transformPtrs[index] = transformPtr;
            isActive[index] = true;
            historyCount[index] = 0;
            blendStrategy[index] = (byte)BlendStrategyType.LinearExtrapolation; // Default strategy
            activeCount++;

            return index;
        }

        /// <summary>
        /// Unregister an object by marking it inactive.
        /// Does NOT compact array (sparse array OK for blending).
        /// </summary>
        public void UnregisterObject(int index)
        {
            if (index < 0 || index >= capacity)
                return;

            isActive[index] = false;
            historyCount[index] = 0;
            // Note: activeCount NOT decremented (sparse array)
        }

        /// <summary>
        /// Resize stream to new capacity (growth strategy: doubling + min 64).
        /// Allocates new arrays, copies existing data, disposes old arrays.
        /// </summary>
        public void Resize(int newCapacity)
        {
            if (newCapacity <= capacity)
            {
                GONetLog.Warning($"[SoA-Resize] Attempted resize to smaller/equal capacity: {capacity} → {newCapacity}. Ignoring.");
                return;
            }

            GONetLog.Info($"[SoA-Resize] QUATERNION stream resizing: {capacity} → {newCapacity} (activeCount: {activeCount})");

            int oldCapacity = capacity;
            capacity = newCapacity;

            // Resize object tracking arrays
            ResizeArray(ref gonetIds, oldCapacity, newCapacity);
            ResizeArray(ref transformPtrs, oldCapacity, newCapacity);
            ResizeArray(ref isActive, oldCapacity, newCapacity);
            ResizeArray(ref historyWriteIndex, oldCapacity, newCapacity);
            ResizeArray(ref historyCount, oldCapacity, newCapacity);
            ResizeArray(ref blendStrategy, oldCapacity, newCapacity);

            // Resize ring buffer arrays (flattened: objectIndex * RING_BUFFER_SIZE + historyIndex)
            int oldTotalSize = oldCapacity * RING_BUFFER_SIZE;
            int newTotalSize = newCapacity * RING_BUFFER_SIZE;
            ResizeArray(ref rotX, oldTotalSize, newTotalSize);
            ResizeArray(ref rotY, oldTotalSize, newTotalSize);
            ResizeArray(ref rotZ, oldTotalSize, newTotalSize);
            ResizeArray(ref rotW, oldTotalSize, newTotalSize);
            ResizeArray(ref rotTicks, oldTotalSize, newTotalSize);
        }

        /// <summary>
        /// Helper: Resize a NativeArray (allocate new, copy data, dispose old).
        /// </summary>
        private static void ResizeArray<T>(ref NativeArray<T> array, int oldSize, int newSize) where T : struct
        {
            var newArray = new NativeArray<T>(newSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            // Copy existing data
            if (array.IsCreated)
            {
                NativeArray<T>.Copy(array, newArray, Mathf.Min(oldSize, newSize));
                array.Dispose();
            }

            array = newArray;
        }

        /// <summary>
        /// Dispose all NativeArrays.
        /// </summary>
        public void Dispose()
        {
            if (gonetIds.IsCreated) gonetIds.Dispose();
            if (transformPtrs.IsCreated) transformPtrs.Dispose();
            if (isActive.IsCreated) isActive.Dispose();
            if (rotX.IsCreated) rotX.Dispose();
            if (rotY.IsCreated) rotY.Dispose();
            if (rotZ.IsCreated) rotZ.Dispose();
            if (rotW.IsCreated) rotW.Dispose();
            if (rotTicks.IsCreated) rotTicks.Dispose();
            if (historyWriteIndex.IsCreated) historyWriteIndex.Dispose();
            if (historyCount.IsCreated) historyCount.Dispose();
            if (blendStrategy.IsCreated) blendStrategy.Dispose();
        }
    }
}
