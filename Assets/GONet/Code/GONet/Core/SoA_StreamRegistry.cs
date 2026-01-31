/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;

namespace GONet.Core
{
    /// <summary>
    /// Central registry for unified SoA value blending.
    /// Maps (GONetId, memberIndex) → stream location for O(1) lookup during network reception.
    /// Single source of truth - eliminates v1/v2 data coupling.
    /// </summary>
    public static class SoA_StreamRegistry
    {
        /// <summary>
        /// Location of a value in the SoA streams.
        /// </summary>
        public struct StreamLocation
        {
            public SoA_StreamType StreamType;   // Which stream type (Vector3, Quaternion, etc.)
            public int StreamIndex;              // Index within typed stream array (Hz-based)
            public int ObjectIndex;              // Index within that stream (object slot)

            public static readonly StreamLocation Invalid = new StreamLocation { StreamIndex = -1, ObjectIndex = -1 };
            public bool IsValid => StreamIndex >= 0 && ObjectIndex >= 0;
        }

        /// <summary>
        /// Pending velocity from VELOCITY bundle (decoupled from mostRecentChanges).
        /// Stored separately and consumed by next VALUE bundle.
        /// </summary>
        public struct PendingVelocity
        {
            public Vector3 Velocity;
            public long Ticks;
            public bool HasVelocity;
        }

        // Registry: (GONetId << 8 | memberIndex) → StreamLocation
        // Using Dictionary for managed code simplicity; could use NativeHashMap for worker thread access
        private static Dictionary<uint, StreamLocation> s_PositionRegistry;
        private static Dictionary<uint, StreamLocation> s_RotationRegistry;
        private static Dictionary<uint, StreamLocation> s_ScalarRegistry;

        // Pending velocity storage (decoupled from v1 mostRecentChanges)
        // Key: (GONetId << 8 | memberIndex)
        private static Dictionary<uint, PendingVelocity> s_PendingVelocities;

        // Blending strategy per registered value
        // Key: (GONetId << 8 | memberIndex), Value: BlendStrategyType
        private static Dictionary<uint, BlendStrategyType> s_BlendStrategies;

        // Reference to SoA data for direct writes
        private static NonAuthorityBlendingSoA_Final s_SoAData;

        // Initialization state
        private static bool s_IsInitialized;

        /// <summary>
        /// Initialize the registry. Call once during GONet initialization.
        /// </summary>
        public static void Initialize(ref NonAuthorityBlendingSoA_Final soaData)
        {
            if (s_IsInitialized)
                return;

            s_SoAData = soaData;
            s_PositionRegistry = new Dictionary<uint, StreamLocation>(256);
            s_RotationRegistry = new Dictionary<uint, StreamLocation>(256);
            s_ScalarRegistry = new Dictionary<uint, StreamLocation>(256);
            s_PendingVelocities = new Dictionary<uint, PendingVelocity>(256);
            s_BlendStrategies = new Dictionary<uint, BlendStrategyType>(512);

            s_IsInitialized = true;

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Info("[SoA-Registry] Initialized unified stream registry");
            }
        }

        /// <summary>
        /// Shutdown and cleanup. Call during GONet shutdown.
        /// </summary>
        public static void Shutdown()
        {
            if (!s_IsInitialized)
                return;

            s_PositionRegistry?.Clear();
            s_RotationRegistry?.Clear();
            s_ScalarRegistry?.Clear();
            s_PendingVelocities?.Clear();
            s_BlendStrategies?.Clear();

            s_PositionRegistry = null;
            s_RotationRegistry = null;
            s_ScalarRegistry = null;
            s_PendingVelocities = null;
            s_BlendStrategies = null;

            s_IsInitialized = false;

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Info("[SoA-Registry] Shutdown unified stream registry");
            }
        }

        /// <summary>
        /// CRITICAL FIX (Dec 2025): Reset SoA history for all registered objects after demotion.
        ///
        /// When a host demotes to client, its SoA blend buffers contain stale data from when it was
        /// the authority. Without resetting, incoming sync data from the new host will be blended
        /// with stale history, causing objects to appear at crazy positions or move erratically.
        ///
        /// This function:
        /// 1. Resets historyCount and historyWriteIndex to 0
        /// 2. Seeds with 2 backdated samples at current position (like registration does)
        ///
        /// The seeding is critical because blending requires at least 2 samples to interpolate.
        /// Without it, the first incoming sync would be the only sample, causing extrapolation issues.
        /// </summary>
        /// <param name="bufferLeadTicks">Buffer lead time in ticks (from GONetMain.valueBlendingBufferLeadTicks)</param>
        /// <returns>Number of values reset (positions + rotations).</returns>
        public static int ResetAllHistoryForDemotion(long bufferLeadTicks)
        {
            if (!s_IsInitialized || s_SoAData.positionStreams == null)
                return 0;

            int resetCount = 0;
            long currentTicks = GONetMain.Time.ElapsedTicks;
            long doubleBackdatedTicks = currentTicks - (2 * bufferLeadTicks);
            long singleBackdatedTicks = currentTicks - bufferLeadTicks;

            // Reset and seed position history
            if (s_PositionRegistry != null)
            {
                foreach (var kvp in s_PositionRegistry)
                {
                    var location = kvp.Value;
                    if (!location.IsValid || location.StreamIndex >= s_SoAData.positionStreams.Length)
                        continue;

                    ref var stream = ref s_SoAData.positionStreams[location.StreamIndex];
                    int objIdx = location.ObjectIndex;

                    if (objIdx < stream.capacity && stream.isActive[objIdx])
                    {
                        // Get current position from transform
                        UnityEngine.Vector3 currentPos = UnityEngine.Vector3.zero;
                        if (stream.transformPtrs[objIdx] != System.IntPtr.Zero)
                        {
                            try
                            {
                                var handle = System.Runtime.InteropServices.GCHandle.FromIntPtr(stream.transformPtrs[objIdx]);
                                if (handle.IsAllocated && handle.Target is UnityEngine.Transform transform && transform != null)
                                {
                                    currentPos = transform.position;
                                }
                            }
                            catch { /* Transform may be destroyed */ }
                        }

                        // Reset history
                        stream.historyWriteIndex[objIdx] = 0;
                        stream.historyCount[objIdx] = 0;

                        // Seed with 2 backdated samples (like registration does)
                        // This ensures blending has samples to work with immediately
                        SoA_LockFreeRingBuffer.WritePositionUpdate(stream, objIdx, currentPos, doubleBackdatedTicks, false);
                        SoA_LockFreeRingBuffer.WritePositionUpdate(stream, objIdx, currentPos, singleBackdatedTicks, false);

                        resetCount++;
                    }
                }
            }

            // Reset and seed rotation history
            if (s_RotationRegistry != null && s_SoAData.rotationStreams != null)
            {
                foreach (var kvp in s_RotationRegistry)
                {
                    var location = kvp.Value;
                    if (!location.IsValid || location.StreamIndex >= s_SoAData.rotationStreams.Length)
                        continue;

                    ref var stream = ref s_SoAData.rotationStreams[location.StreamIndex];
                    int objIdx = location.ObjectIndex;

                    if (objIdx < stream.capacity && stream.isActive[objIdx])
                    {
                        // Get current rotation from transform
                        UnityEngine.Quaternion currentRot = UnityEngine.Quaternion.identity;
                        if (stream.transformPtrs[objIdx] != System.IntPtr.Zero)
                        {
                            try
                            {
                                var handle = System.Runtime.InteropServices.GCHandle.FromIntPtr(stream.transformPtrs[objIdx]);
                                if (handle.IsAllocated && handle.Target is UnityEngine.Transform transform && transform != null)
                                {
                                    currentRot = transform.rotation;
                                }
                            }
                            catch { /* Transform may be destroyed */ }
                        }

                        // Reset history
                        stream.historyWriteIndex[objIdx] = 0;
                        stream.historyCount[objIdx] = 0;

                        // Seed with 2 backdated samples
                        SoA_LockFreeRingBuffer.WriteRotationUpdate(stream, objIdx, currentRot, doubleBackdatedTicks, false);
                        SoA_LockFreeRingBuffer.WriteRotationUpdate(stream, objIdx, currentRot, singleBackdatedTicks, false);

                        resetCount++;
                    }
                }
            }

            // Clear pending velocities - they're stale after demotion
            s_PendingVelocities?.Clear();

            if (resetCount > 0)
            {
                GONetLog.Info($"[SoA-Registry] Reset and seeded history for {resetCount} SoA values after demotion - ready for fresh sync");
            }

            return resetCount;
        }

        /// <summary>
        /// Create registry key from GONetId and member index.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint MakeKey(uint gonetId, byte memberIndex)
        {
            return (gonetId << 8) | memberIndex;
        }

        /// <summary>
        /// Register a Vector3 position value in the registry.
        /// Called during non-authority object initialization.
        /// </summary>
        public static void RegisterPosition(
            uint gonetId,
            byte memberIndex,
            int streamIndex,
            int objectIndex,
            BlendStrategyType blendStrategy = BlendStrategyType.LinearExtrapolation)
        {
            if (!s_IsInitialized)
            {
                GONetLog.Warning("[SoA-Registry] Not initialized - cannot register position");
                return;
            }

            uint key = MakeKey(gonetId, memberIndex);
            var location = new StreamLocation
            {
                StreamType = SoA_StreamType.VECTOR3,
                StreamIndex = streamIndex,
                ObjectIndex = objectIndex
            };

            s_PositionRegistry[key] = location;
            s_BlendStrategies[key] = blendStrategy;

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Debug($"[SoA-Registry] Registered POSITION: GONetId={gonetId}, memberIndex={memberIndex} → stream[{streamIndex}][{objectIndex}], strategy={blendStrategy}");
            }
        }

        /// <summary>
        /// Register a Quaternion rotation value in the registry.
        /// </summary>
        public static void RegisterRotation(
            uint gonetId,
            byte memberIndex,
            int streamIndex,
            int objectIndex,
            BlendStrategyType blendStrategy = BlendStrategyType.LinearExtrapolation)
        {
            if (!s_IsInitialized)
            {
                GONetLog.Warning("[SoA-Registry] Not initialized - cannot register rotation");
                return;
            }

            uint key = MakeKey(gonetId, memberIndex);
            var location = new StreamLocation
            {
                StreamType = SoA_StreamType.QUATERNION,
                StreamIndex = streamIndex,
                ObjectIndex = objectIndex
            };

            s_RotationRegistry[key] = location;
            s_BlendStrategies[key] = blendStrategy;

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Debug($"[SoA-Registry] Registered ROTATION: GONetId={gonetId}, memberIndex={memberIndex} → stream[{streamIndex}][{objectIndex}], strategy={blendStrategy}");
            }
        }

        /// <summary>
        /// Register a scalar (float) value in the registry.
        /// </summary>
        public static void RegisterScalar(
            uint gonetId,
            byte memberIndex,
            int streamIndex,
            int objectIndex,
            BlendStrategyType blendStrategy = BlendStrategyType.LinearExtrapolation)
        {
            if (!s_IsInitialized)
            {
                GONetLog.Warning("[SoA-Registry] Not initialized - cannot register scalar");
                return;
            }

            uint key = MakeKey(gonetId, memberIndex);
            var location = new StreamLocation
            {
                StreamType = SoA_StreamType.SCALAR,
                StreamIndex = streamIndex,
                ObjectIndex = objectIndex
            };

            s_ScalarRegistry[key] = location;
            s_BlendStrategies[key] = blendStrategy;

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Debug($"[SoA-Registry] Registered SCALAR: GONetId={gonetId}, memberIndex={memberIndex} → stream[{streamIndex}][{objectIndex}], strategy={blendStrategy}");
            }
        }

        /// <summary>
        /// Unregister all values for a GONetParticipant (object destroyed or authority changed).
        /// </summary>
        public static void UnregisterAll(uint gonetId)
        {
            if (!s_IsInitialized)
                return;

            // Remove all entries with this GONetId (scan all member indices 0-255)
            for (byte memberIndex = 0; memberIndex < 255; memberIndex++)
            {
                uint key = MakeKey(gonetId, memberIndex);
                s_PositionRegistry?.Remove(key);
                s_RotationRegistry?.Remove(key);
                s_ScalarRegistry?.Remove(key);
                s_PendingVelocities?.Remove(key);
                s_BlendStrategies?.Remove(key);
            }

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Debug($"[SoA-Registry] Unregistered all values for GONetId={gonetId}");
            }
        }

        /// <summary>
        /// Update all registry entries when a GONetId changes.
        /// This is critical for client-spawned server-owned objects where the server
        /// reassigns the GONetId raw after taking authority.
        /// </summary>
        public static void UpdateGONetId(uint previousGONetId, uint newGONetId)
        {
            if (!s_IsInitialized)
                return;

            if (previousGONetId == newGONetId)
                return;

            int updatedCount = 0;

            // Re-key all entries for all member indices (0-255)
            for (byte memberIndex = 0; memberIndex < 255; memberIndex++)
            {
                uint oldKey = MakeKey(previousGONetId, memberIndex);
                uint newKey = MakeKey(newGONetId, memberIndex);

                // Position registry
                if (s_PositionRegistry != null && s_PositionRegistry.TryGetValue(oldKey, out var posLocation))
                {
                    s_PositionRegistry.Remove(oldKey);
                    s_PositionRegistry[newKey] = posLocation;
                    updatedCount++;
                }

                // Rotation registry
                if (s_RotationRegistry != null && s_RotationRegistry.TryGetValue(oldKey, out var rotLocation))
                {
                    s_RotationRegistry.Remove(oldKey);
                    s_RotationRegistry[newKey] = rotLocation;
                    updatedCount++;
                }

                // Scalar registry
                if (s_ScalarRegistry != null && s_ScalarRegistry.TryGetValue(oldKey, out var scalarLocation))
                {
                    s_ScalarRegistry.Remove(oldKey);
                    s_ScalarRegistry[newKey] = scalarLocation;
                    updatedCount++;
                }

                // Pending velocities
                if (s_PendingVelocities != null && s_PendingVelocities.TryGetValue(oldKey, out var pending))
                {
                    s_PendingVelocities.Remove(oldKey);
                    s_PendingVelocities[newKey] = pending;
                }

                // Blend strategies
                if (s_BlendStrategies != null && s_BlendStrategies.TryGetValue(oldKey, out var strategy))
                {
                    s_BlendStrategies.Remove(oldKey);
                    s_BlendStrategies[newKey] = strategy;
                }
            }

            if (updatedCount > 0 && GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Debug($"[SoA-Registry] Updated GONetId: {previousGONetId} → {newGONetId} ({updatedCount} entries)");
            }
        }

        /// <summary>
        /// Check if a position is registered for unified SoA blending.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPositionRegistered(uint gonetId, byte memberIndex)
        {
            if (!s_IsInitialized || s_PositionRegistry == null)
                return false;

            return s_PositionRegistry.ContainsKey(MakeKey(gonetId, memberIndex));
        }

        /// <summary>
        /// Check if a rotation is registered for unified SoA blending.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsRotationRegistered(uint gonetId, byte memberIndex)
        {
            if (!s_IsInitialized || s_RotationRegistry == null)
                return false;

            return s_RotationRegistry.ContainsKey(MakeKey(gonetId, memberIndex));
        }

        /// <summary>
        /// Try to get the stream location for a position value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetPositionLocation(uint gonetId, byte memberIndex, out StreamLocation location)
        {
            if (!s_IsInitialized || s_PositionRegistry == null)
            {
                location = StreamLocation.Invalid;
                return false;
            }

            return s_PositionRegistry.TryGetValue(MakeKey(gonetId, memberIndex), out location);
        }

        /// <summary>
        /// Try to get the stream location for a rotation value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetRotationLocation(uint gonetId, byte memberIndex, out StreamLocation location)
        {
            if (!s_IsInitialized || s_RotationRegistry == null)
            {
                location = StreamLocation.Invalid;
                return false;
            }

            return s_RotationRegistry.TryGetValue(MakeKey(gonetId, memberIndex), out location);
        }

        /// <summary>
        /// Write a position value directly to the unified SoA stream.
        /// Called from main thread during network event processing.
        /// NOTE: Thread-safe because network events are queued and processed on main thread via EventBus.
        /// </summary>
        /// <param name="gonetId">GONet object ID</param>
        /// <param name="memberIndex">Member index within object</param>
        /// <param name="position">Position value</param>
        /// <param name="ticks">Timestamp in ticks</param>
        /// <param name="isAnchor">True for VALUE bundles (anchor points), false for VELOCITY-synthesized</param>
        /// <param name="isTeleport">True for teleport/respawn - clears history and snaps immediately (no blending)</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WritePosition(uint gonetId, byte memberIndex, Vector3 position, long ticks, bool isAnchor = false, bool isTeleport = false)
        {
            if (!s_IsInitialized || s_SoAData.positionStreams == null)
                return;

            uint key = MakeKey(gonetId, memberIndex);
            if (!s_PositionRegistry.TryGetValue(key, out var location))
                return;

            ref var stream = ref s_SoAData.positionStreams[location.StreamIndex];
            int objIdx = location.ObjectIndex;

            // TELEPORT: Clear history and snap immediately (prevents wild interpolation across map)
            if (isTeleport)
            {
                stream.historyWriteIndex[objIdx] = 0;
                stream.historyCount[objIdx] = 0;

                if (GONetFeatureFlags.DebugUnifiedSoABlending)
                {
                    GONetLog.Debug($"[SoA-Registry] TELEPORT position: GONetId={gonetId}, pos={position}");
                }
            }

            // Write to ring buffer
            int ringIdx = objIdx * ValueStream_Position.RING_BUFFER_SIZE + stream.historyWriteIndex[objIdx];

            stream.posX[ringIdx] = position.x;
            stream.posY[ringIdx] = position.y;
            stream.posZ[ringIdx] = position.z;
            stream.posTicks[ringIdx] = ticks;

            // Advance ring buffer
            stream.historyWriteIndex[objIdx] = (byte)((stream.historyWriteIndex[objIdx] + 1) % ValueStream_Position.RING_BUFFER_SIZE);
            if (stream.historyCount[objIdx] < ValueStream_Position.RING_BUFFER_SIZE)
            {
                stream.historyCount[objIdx]++;
            }

            // For anchor points (VALUE bundles), double-write to prevent velocity spikes
            // This matches existing v2 behavior
            // Skip double-write on teleport (we want exactly one sample)
            if (isAnchor && !isTeleport && stream.historyCount[objIdx] < ValueStream_Position.RING_BUFFER_SIZE)
            {
                ringIdx = objIdx * ValueStream_Position.RING_BUFFER_SIZE + stream.historyWriteIndex[objIdx];
                stream.posX[ringIdx] = position.x;
                stream.posY[ringIdx] = position.y;
                stream.posZ[ringIdx] = position.z;
                stream.posTicks[ringIdx] = ticks + 1; // Slightly later

                stream.historyWriteIndex[objIdx] = (byte)((stream.historyWriteIndex[objIdx] + 1) % ValueStream_Position.RING_BUFFER_SIZE);
                stream.historyCount[objIdx]++;
            }
        }

        /// <summary>
        /// Write a rotation value directly to the unified SoA stream.
        /// </summary>
        /// <param name="isTeleport">True for teleport/respawn - clears history and snaps immediately (no blending)</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRotation(uint gonetId, byte memberIndex, Quaternion rotation, long ticks, bool isAnchor = false, bool isTeleport = false)
        {
            if (!s_IsInitialized || s_SoAData.rotationStreams == null)
                return;

            uint key = MakeKey(gonetId, memberIndex);
            if (!s_RotationRegistry.TryGetValue(key, out var location))
                return;

            ref var stream = ref s_SoAData.rotationStreams[location.StreamIndex];
            int objIdx = location.ObjectIndex;

            // TELEPORT: Clear history and snap immediately
            if (isTeleport)
            {
                stream.historyWriteIndex[objIdx] = 0;
                stream.historyCount[objIdx] = 0;

                if (GONetFeatureFlags.DebugUnifiedSoABlending)
                {
                    GONetLog.Debug($"[SoA-Registry] TELEPORT rotation: GONetId={gonetId}, rot={rotation}");
                }
            }

            // Write to ring buffer
            int ringIdx = objIdx * ValueStream_Rotation.RING_BUFFER_SIZE + stream.historyWriteIndex[objIdx];

            stream.rotX[ringIdx] = rotation.x;
            stream.rotY[ringIdx] = rotation.y;
            stream.rotZ[ringIdx] = rotation.z;
            stream.rotW[ringIdx] = rotation.w;
            stream.rotTicks[ringIdx] = ticks;

            // Advance ring buffer
            stream.historyWriteIndex[objIdx] = (byte)((stream.historyWriteIndex[objIdx] + 1) % ValueStream_Rotation.RING_BUFFER_SIZE);
            if (stream.historyCount[objIdx] < ValueStream_Rotation.RING_BUFFER_SIZE)
            {
                stream.historyCount[objIdx]++;
            }

            // Double-write for anchors (skip on teleport)
            if (isAnchor && !isTeleport && stream.historyCount[objIdx] < ValueStream_Rotation.RING_BUFFER_SIZE)
            {
                ringIdx = objIdx * ValueStream_Rotation.RING_BUFFER_SIZE + stream.historyWriteIndex[objIdx];
                stream.rotX[ringIdx] = rotation.x;
                stream.rotY[ringIdx] = rotation.y;
                stream.rotZ[ringIdx] = rotation.z;
                stream.rotW[ringIdx] = rotation.w;
                stream.rotTicks[ringIdx] = ticks + 1;

                stream.historyWriteIndex[objIdx] = (byte)((stream.historyWriteIndex[objIdx] + 1) % ValueStream_Rotation.RING_BUFFER_SIZE);
                stream.historyCount[objIdx]++;
            }
        }

        /// <summary>
        /// Store pending velocity from VELOCITY bundle (decoupled from mostRecentChanges).
        /// Will be consumed by next VALUE bundle for the same value.
        /// </summary>
        public static void SetPendingVelocity(uint gonetId, byte memberIndex, Vector3 velocity, long ticks)
        {
            if (!s_IsInitialized || s_PendingVelocities == null)
                return;

            uint key = MakeKey(gonetId, memberIndex);
            s_PendingVelocities[key] = new PendingVelocity
            {
                Velocity = velocity,
                Ticks = ticks,
                HasVelocity = true
            };

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Debug($"[SoA-Registry] Set pending velocity: GONetId={gonetId}, memberIndex={memberIndex}, velocity={velocity}");
            }
        }

        /// <summary>
        /// Get and clear pending velocity for a value.
        /// Returns (velocity, hasVelocity) tuple.
        /// </summary>
        public static (Vector3 velocity, bool hasVelocity) GetPendingVelocity(uint gonetId, byte memberIndex, long currentTicks)
        {
            if (!s_IsInitialized || s_PendingVelocities == null)
                return (Vector3.zero, false);

            uint key = MakeKey(gonetId, memberIndex);
            if (s_PendingVelocities.TryGetValue(key, out var pending) && pending.HasVelocity)
            {
                // Check if velocity is recent enough (200ms threshold)
                const long VELOCITY_FRESHNESS_TICKS = TimeSpan.TicksPerSecond / 5; // 200ms
                if (currentTicks - pending.Ticks < VELOCITY_FRESHNESS_TICKS)
                {
                    // Clear after consumption
                    s_PendingVelocities[key] = new PendingVelocity { HasVelocity = false };
                    return (pending.Velocity, true);
                }
            }

            return (Vector3.zero, false);
        }

        /// <summary>
        /// Get the blending strategy for a registered value.
        /// </summary>
        public static BlendStrategyType GetBlendStrategy(uint gonetId, byte memberIndex)
        {
            if (!s_IsInitialized || s_BlendStrategies == null)
                return BlendStrategyType.LinearExtrapolation;

            uint key = MakeKey(gonetId, memberIndex);
            if (s_BlendStrategies.TryGetValue(key, out var strategy))
                return strategy;

            return BlendStrategyType.LinearExtrapolation;
        }

        /// <summary>
        /// Set the blending strategy for a registered value at runtime.
        /// </summary>
        public static void SetBlendStrategy(uint gonetId, byte memberIndex, BlendStrategyType strategy)
        {
            if (!s_IsInitialized || s_BlendStrategies == null)
                return;

            uint key = MakeKey(gonetId, memberIndex);
            s_BlendStrategies[key] = strategy;

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Debug($"[SoA-Registry] Changed blend strategy: GONetId={gonetId}, memberIndex={memberIndex} → {strategy}");
            }
        }

        /// <summary>
        /// Get total count of registered values (for diagnostics).
        /// </summary>
        public static (int positions, int rotations, int scalars) GetRegisteredCounts()
        {
            if (!s_IsInitialized)
                return (0, 0, 0);

            return (
                s_PositionRegistry?.Count ?? 0,
                s_RotationRegistry?.Count ?? 0,
                s_ScalarRegistry?.Count ?? 0
            );
        }
    }
}
