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
    /// GONet v2: Vector4 stream with lock-free ring buffers.
    /// Stores history of Vector4 updates for temporal blending.
    /// </summary>
    public struct ValueStream_Vector4 : IDisposable
    {
        // Object tracking
        public NativeArray<uint> gonetIds;           // GONetId for each object slot
        public NativeArray<byte> memberIndices;      // memberIndex for each object slot (for generic value application)
        public NativeArray<IntPtr> companionPtrs;    // Weak GCHandle to GONetParticipant_AutoMagicalSyncCompanion_Generated
        public NativeArray<bool> isActive;           // Is slot active?
        public int activeCount;                      // Number of active objects
        public int capacity;                         // Max objects

        // Ring buffer - Vector4 components (X, Y, Z, W)
        public NativeArray<float> valX;              // [objectIndex][historyIndex]
        public NativeArray<float> valY;
        public NativeArray<float> valZ;
        public NativeArray<float> valW;
        public NativeArray<long> valTicks;           // Timestamp for each sample

        // Ring buffer metadata
        public NativeArray<byte> historyWriteIndex;  // Current write position per object (0-7)
        public NativeArray<byte> historyCount;       // Current samples per object (0-8)
        public const int RING_BUFFER_SIZE = 8;       // History samples per object (lock-free = 8)

        // Blending configuration (pluggable strategies)
        public NativeArray<byte> blendStrategy;      // BlendStrategyType per object (byte for Burst compatibility)

        /// <summary>
        /// Initialize stream with specified capacity.
        /// </summary>
        public void Initialize(int maxCapacity)
        {
            capacity = maxCapacity;
            activeCount = 0;

            // Object tracking arrays
            gonetIds = new NativeArray<uint>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            memberIndices = new NativeArray<byte>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            companionPtrs = new NativeArray<IntPtr>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            isActive = new NativeArray<bool>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            // Ring buffer arrays (flattened: objectIndex * RING_BUFFER_SIZE + historyIndex)
            int totalSize = maxCapacity * RING_BUFFER_SIZE;
            valX = new NativeArray<float>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            valY = new NativeArray<float>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            valZ = new NativeArray<float>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            valW = new NativeArray<float>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            valTicks = new NativeArray<long>(totalSize, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            historyWriteIndex = new NativeArray<byte>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            historyCount = new NativeArray<byte>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            blendStrategy = new NativeArray<byte>(maxCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        /// <summary>
        /// Register a new object in the stream.
        /// Returns the object index (slot) where it was added.
        /// </summary>
        public int RegisterObject(uint gonetId, byte memberIndex, IntPtr companionPtr)
        {
            // Auto-resize if approaching capacity (75% threshold)
            const float GROWTH_THRESHOLD = 0.75f;
            if (activeCount >= capacity * GROWTH_THRESHOLD)
            {
                // Growth strategy: doubling + minimum 64 (Unity NativeList pattern)
                int newCapacity = Mathf.Max(capacity * 2, capacity + 64);
                Resize(newCapacity);
            }

            if (activeCount >= capacity)
            {
                GONetLog.Error($"[SoA] Vector4 stream STILL full after resize! Cannot register GONetId {gonetId} (capacity: {capacity})");
                return -1;
            }

            int index = activeCount;
            gonetIds[index] = gonetId;
            memberIndices[index] = memberIndex;
            companionPtrs[index] = companionPtr;
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

            GONetLog.Info($"[SoA-Resize] VECTOR4 stream resizing: {capacity} → {newCapacity} (activeCount: {activeCount})");

            int oldCapacity = capacity;
            capacity = newCapacity;

            // Resize object tracking arrays
            ResizeArray(ref gonetIds, oldCapacity, newCapacity);
            ResizeArray(ref memberIndices, oldCapacity, newCapacity);
            ResizeArray(ref companionPtrs, oldCapacity, newCapacity);
            ResizeArray(ref isActive, oldCapacity, newCapacity);
            ResizeArray(ref historyWriteIndex, oldCapacity, newCapacity);
            ResizeArray(ref historyCount, oldCapacity, newCapacity);
            ResizeArray(ref blendStrategy, oldCapacity, newCapacity);

            // Resize ring buffer arrays (flattened: objectIndex * RING_BUFFER_SIZE + historyIndex)
            int oldTotalSize = oldCapacity * RING_BUFFER_SIZE;
            int newTotalSize = newCapacity * RING_BUFFER_SIZE;
            ResizeArray(ref valX, oldTotalSize, newTotalSize);
            ResizeArray(ref valY, oldTotalSize, newTotalSize);
            ResizeArray(ref valZ, oldTotalSize, newTotalSize);
            ResizeArray(ref valW, oldTotalSize, newTotalSize);
            ResizeArray(ref valTicks, oldTotalSize, newTotalSize);
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
            if (memberIndices.IsCreated) memberIndices.Dispose();
            if (companionPtrs.IsCreated) companionPtrs.Dispose();
            if (isActive.IsCreated) isActive.Dispose();
            if (valX.IsCreated) valX.Dispose();
            if (valY.IsCreated) valY.Dispose();
            if (valZ.IsCreated) valZ.Dispose();
            if (valW.IsCreated) valW.Dispose();
            if (valTicks.IsCreated) valTicks.Dispose();
            if (historyWriteIndex.IsCreated) historyWriteIndex.Dispose();
            if (historyCount.IsCreated) historyCount.Dispose();
            if (blendStrategy.IsCreated) blendStrategy.Dispose();
        }
    }
}
