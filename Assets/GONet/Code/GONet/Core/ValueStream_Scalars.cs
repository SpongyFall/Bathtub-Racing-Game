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
    /// GONet v2: Scalar (float/bool/int) stream with lock-free ring buffers.
    /// Stores history of scalar value updates for temporal blending.
    /// </summary>
    public struct ValueStream_Scalars : IDisposable
    {
        // Object tracking
        public NativeArray<uint> gonetIds;           // GONetId for each object slot
        public NativeArray<IntPtr> componentPtrs;    // Weak GCHandle to component (not Transform)
        public NativeArray<bool> isActive;           // Is slot active?
        public int activeCount;                      // Number of active objects
        public int capacity;                         // Max objects

        // Ring buffer - Scalar values
        public NativeArray<float> values;            // [objectIndex][historyIndex] - matches SoA_LockFreeRingBuffer
        public NativeArray<long> valueTicks;         // Timestamp for each sample - matches SoA_LockFreeRingBuffer

        // Ring buffer metadata
        public NativeArray<byte> historyWriteIndex;  // Current write position per object (0-7)
        public NativeArray<byte> historyCount;       // Current samples per object (0-8)
        public const int RING_BUFFER_SIZE = 8;       // History samples per object (lock-free = 8)

        /// <summary>
        /// Initialize stream with specified capacity.
        /// </summary>
        public void Initialize(int maxCapacity)
        {
            capacity = maxCapacity;
            activeCount = 0;

            // Object tracking arrays
            gonetIds = new NativeArray<uint>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            componentPtrs = new NativeArray<IntPtr>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            isActive = new NativeArray<bool>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            // Ring buffer arrays (flattened: objectIndex * RING_BUFFER_SIZE + historyIndex)
            int totalSize = maxCapacity * RING_BUFFER_SIZE;
            values = new NativeArray<float>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            valueTicks = new NativeArray<long>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            historyWriteIndex = new NativeArray<byte>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            historyCount = new NativeArray<byte>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        /// <summary>
        /// Register a new object in the stream.
        /// Returns the object index (slot) where it was added.
        /// </summary>
        public int RegisterObject(uint gonetId, IntPtr componentPtr)
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
                GONetLog.Error($"[SoA] Scalar stream STILL full after resize! Cannot register GONetId {gonetId} (capacity: {capacity})");
                return -1;
            }

            int index = activeCount;
            gonetIds[index] = gonetId;
            componentPtrs[index] = componentPtr;
            isActive[index] = true;
            historyCount[index] = 0;
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

            GONetLog.Info($"[SoA-Resize] SCALAR stream resizing: {capacity} → {newCapacity} (activeCount: {activeCount})");

            int oldCapacity = capacity;
            capacity = newCapacity;

            // Resize object tracking arrays
            ResizeArray(ref gonetIds, oldCapacity, newCapacity);
            ResizeArray(ref componentPtrs, oldCapacity, newCapacity);
            ResizeArray(ref isActive, oldCapacity, newCapacity);
            ResizeArray(ref historyWriteIndex, oldCapacity, newCapacity);
            ResizeArray(ref historyCount, oldCapacity, newCapacity);

            // Resize ring buffer arrays (flattened: objectIndex * RING_BUFFER_SIZE + historyIndex)
            int oldTotalSize = oldCapacity * RING_BUFFER_SIZE;
            int newTotalSize = newCapacity * RING_BUFFER_SIZE;
            ResizeArray(ref values, oldTotalSize, newTotalSize);
            ResizeArray(ref valueTicks, oldTotalSize, newTotalSize);
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
            if (componentPtrs.IsCreated) componentPtrs.Dispose();
            if (isActive.IsCreated) isActive.Dispose();
            if (values.IsCreated) values.Dispose();
            if (valueTicks.IsCreated) valueTicks.Dispose();
            if (historyWriteIndex.IsCreated) historyWriteIndex.Dispose();
            if (historyCount.IsCreated) historyCount.Dispose();
        }
    }
}
