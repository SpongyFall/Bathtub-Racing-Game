/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using GONet.Generation;

namespace GONet.Core
{
    /// <summary>
    /// Applies blended values from shadow buffers to Unity components.
    /// This is the final stage of the unified SoA blending pipeline.
    ///
    /// Key Responsibilities:
    /// - Read from shadow buffers (after blending jobs complete)
    /// - Write to Unity Transform components (position/rotation)
    /// - Handle batched writes for performance
    /// - Maintain Transform reference validity
    ///
    /// NOTE: Must run on main thread (Unity API requirement).
    /// </summary>
    public static class SoA_ValueApplicator
    {
        // NOTE: We do NOT cache NonAuthorityBlendingSoA_Final (struct copy = stale pointers after resize!)
        // Instead, pass ref each frame to Apply() method.

        // Initialization state
        private static bool s_IsInitialized;
        private static readonly HashSet<uint> s_MismatchedTransformIds = new HashSet<uint>();

        /// <summary>
        /// Initialize the value applicator. Call once during GONet initialization.
        /// </summary>
        public static void Initialize(ref NonAuthorityBlendingSoA_Final soaData)
        {
            if (s_IsInitialized)
                return;

            // Just mark as initialized - soaData passed fresh each Apply() call
            s_IsInitialized = true;

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Info("[SoA-Applicator] Initialized value applicator");
            }
        }

        /// <summary>
        /// Shutdown the applicator. Call during GONet shutdown.
        /// </summary>
        public static void Shutdown()
        {
            if (!s_IsInitialized)
                return;

            s_IsInitialized = false;
            s_MismatchedTransformIds.Clear();

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Info("[SoA-Applicator] Shutdown value applicator");
            }
        }

        /// <summary>
        /// Apply blended values from shadow buffers to Unity components.
        /// Call this after SoA_BlendingPipeline.Execute() completes.
        ///
        /// Uses PREVIOUS shadow buffer (blending jobs write to CURRENT, we read from PREVIOUS).
        /// This provides 1-frame delay but ensures no race conditions.
        /// </summary>
        public static unsafe void Apply(ref NonAuthorityBlendingSoA_Final soaData)
        {
            if (!s_IsInitialized || !soaData.IsInitialized)
                return;

            // Get previous shadow buffers (blending wrote to current, we read from previous)
            NativeArray<Vector3> positions = soaData.GetPreviousShadowPositions();
            NativeArray<Quaternion> rotations = soaData.GetPreviousShadowRotations();

            // Apply positions
            ApplyPositions(ref soaData, positions);

            // Apply rotations
            ApplyRotations(ref soaData, rotations);

            // Apply Vector2 (if initialized)
            NativeArray<Vector2> vector2s = soaData.GetPreviousShadowVector2();
            if (vector2s.IsCreated)
            {
                ApplyVector2(ref soaData, vector2s);
            }

            // Apply Vector4 (if initialized)
            NativeArray<Vector4> vector4s = soaData.GetPreviousShadowVector4();
            if (vector4s.IsCreated)
            {
                ApplyVector4(ref soaData, vector4s);
            }
        }

        /// <summary>
        /// Apply position values to Transform components.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ApplyPositions(ref NonAuthorityBlendingSoA_Final soaData, NativeArray<Vector3> shadowPositions)
        {
            if (soaData.positionStreams == null)
                return;

            int shadowOffset = 0;
            for (int streamIdx = 0; streamIdx < soaData.positionStreams.Length; streamIdx++)
            {
                ref var stream = ref soaData.positionStreams[streamIdx];

                for (int objIdx = 0; objIdx < stream.activeCount; objIdx++)
                {
                    if (!stream.isActive[objIdx])
                        continue;

                    // MULTI-SESSION FIX (December 2025): Verify this object belongs to the current session.
                    // When server+client run in same process (common test scenario), they share static SoA data.
                    // Each session must only apply blending to its own non-authority objects.
                    // Skip objects that: don't exist in this session's map, are null, or are owned by this session.
                    uint gonetId = stream.gonetIds[objIdx];
                    if (!GONetMain.gonetParticipantByGONetIdMap.TryGetValue(gonetId, out var gnp) || gnp == null || gnp.IsMine)
                        continue;

                    // REPARENTING FIX (Jan 2026): Skip transform sync if suspended due to parenting.
                    // When a GNP is parented under another GNP with transform sync enabled,
                    // we suspend the child's transform sync to prevent hierarchy ordering desync.
                    // The parent's transform sync will position the child correctly via hierarchy.
                    if (gnp.IsTransformSyncSuspendedDueToParenting)
                        continue;

                    if (!gnp.IsPositionSyncd)
                    {
                        SoA_ObjectHealthMonitor.OnApply(gonetId, wasApplied: false, Vector3.zero, SoA_ObjectHealthMonitor.SKIP_SYNC_DISABLED);
                        continue;
                    }

                    // STALE DATA FIX (December 2025): Skip objects with only seeded data (no actual sync received).
                    // Objects with historyCount <= 2 only have the initial seed samples from registration.
                    // If no real sync data has arrived, applying these stale positions causes objects to be "stuck".
                    // This can happen when:
                    // 1. Object was registered but destroyed before sync data arrived
                    // 2. Network issues prevented sync data from arriving
                    // 3. Object is phantom from shared static SoA (separate process scenario)
                    //
                    // EXCEPTION: During post-handoff grace period, bypass this check.
                    // After voluntary handoff, historyCount is reset to 0. The first sync samples from
                    // the new host would be rejected, causing objects to stay stuck for ~24 seconds.
                    int historyCount = stream.historyCount[objIdx];
                    if (historyCount <= 2 && !GONetMain.IsInPostHandoffGracePeriod)
                    {
                        // FAILOVER-DIAG: Log stale skip for target object (commented out - development diagnostic)
                        // if (gonetId == 4095)
                        // {
                        //     GONetLog.Warning($"[SoA-FAILOVER-DIAG] APPLY-SKIP-STALE gonetId={gonetId} historyCount={historyCount}");
                        // }
                        SoA_ObjectHealthMonitor.OnApply(gonetId, wasApplied: false, Vector3.zero, SoA_ObjectHealthMonitor.SKIP_STALE_DATA);
                        continue;
                    }

                    // Get Transform from GCHandle pointer
                    IntPtr transformPtr = stream.transformPtrs[objIdx];
                    if (transformPtr == IntPtr.Zero)
                        continue;

                    GCHandle handle = GCHandle.FromIntPtr(transformPtr);
                    if (!handle.IsAllocated)
                        continue;

                    Transform transform = handle.Target as Transform;
                    if (transform == null)
                        continue;

                    if (!ValidateTransformMapping(gonetId, transform, gnp, streamIdx, objIdx, isPosition: true))
                        continue;

                    // Apply blended position
                    Vector3 blendedPos = shadowPositions[shadowOffset + objIdx];

                    // Validate before applying (prevent NaN/Infinity corruption)
                    if (!float.IsNaN(blendedPos.x) && !float.IsInfinity(blendedPos.x) &&
                        !float.IsNaN(blendedPos.y) && !float.IsInfinity(blendedPos.y) &&
                        !float.IsNaN(blendedPos.z) && !float.IsInfinity(blendedPos.z))
                    {
                        Vector3 prevPos = transform.position;
                        transform.position = blendedPos;

                        // FAILOVER-DIAG: Log applied position for target object (commented out - development diagnostic)
                        // if (gonetId == 4095 && (UnityEngine.Time.frameCount % 60) == 0)
                        // {
                        //     Vector3 delta = blendedPos - prevPos;
                        //     int baseIdx = objIdx * 8;
                        //     long newestTicks = 0, oldestTicks = long.MaxValue;
                        //     for (int slot = 0; slot < 8 && slot < historyCount; slot++)
                        //     {
                        //         long t = stream.posTicks[baseIdx + slot];
                        //         if (t > newestTicks) newestTicks = t;
                        //         if (t > 0 && t < oldestTicks) oldestTicks = t;
                        //     }
                        //     long targetTicks = GONetMain.Time.ElapsedTicks - (long)(GONetMain.valueBlendingBufferLeadSeconds * TimeSpan.TicksPerSecond);
                        //     float targetSec = (float)(targetTicks / (double)TimeSpan.TicksPerSecond);
                        //     float newestSec = (float)(newestTicks / (double)TimeSpan.TicksPerSecond);
                        //     float oldestSec = (float)(oldestTicks / (double)TimeSpan.TicksPerSecond);
                        //     float dtNewest = (float)((targetTicks - newestTicks) / (double)TimeSpan.TicksPerSecond);
                        //     GONetLog.Warning($"[SoA-FAILOVER-DIAG] APPLY gonetId={gonetId} pos=({blendedPos.x:F2},{blendedPos.y:F2},{blendedPos.z:F2}) " +
                        //                    $"delta=({delta.x:F2},{delta.y:F2},{delta.z:F2}) historyCount={historyCount} " +
                        //                    $"target={targetSec:F2}s newest={newestSec:F2}s oldest={oldestSec:F2}s dtNewest={dtNewest:F3}s");
                        // }

                        // Health monitor: Track successful apply
                        SoA_ObjectHealthMonitor.OnApply(gonetId, wasApplied: true, blendedPos);

                        // LOG_BLEND_DIAG: Log applied position for analysis
                        #if LOG_BLEND_DIAG
                        Vector3 delta = blendedPos - prevPos;
                        if (delta.sqrMagnitude > 0.0001f) // Only log if there's actual movement
                        {
                            SoA_BlendingDiagnostics.LogPositionApplied(gonetId, blendedPos, 0f, false);
                        }
                        #endif
                    }
                    else
                    {
                        // FAILOVER-DIAG: Log NaN/Infinity rejection (commented out - development diagnostic)
                        // if (gonetId == 4095)
                        // {
                        //     GONetLog.Warning($"[SoA-FAILOVER-DIAG] APPLY-SKIP-NAN gonetId={gonetId} blendedPos=({blendedPos.x},{blendedPos.y},{blendedPos.z})");
                        // }
                        SoA_ObjectHealthMonitor.OnApply(gonetId, wasApplied: false, Vector3.zero, SoA_ObjectHealthMonitor.SKIP_NAN_POSITION);
                    }
                }

                shadowOffset += stream.capacity;
            }
        }

        /// <summary>
        /// Apply rotation values to Transform components.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ApplyRotations(ref NonAuthorityBlendingSoA_Final soaData, NativeArray<Quaternion> shadowRotations)
        {
            if (soaData.rotationStreams == null)
                return;

            int shadowOffset = 0;
            for (int streamIdx = 0; streamIdx < soaData.rotationStreams.Length; streamIdx++)
            {
                ref var stream = ref soaData.rotationStreams[streamIdx];

                for (int objIdx = 0; objIdx < stream.activeCount; objIdx++)
                {
                    if (!stream.isActive[objIdx])
                        continue;

                    // MULTI-SESSION FIX (December 2025): Verify this object belongs to the current session.
                    // When server+client run in same process (common test scenario), they share static SoA data.
                    // Each session must only apply blending to its own non-authority objects.
                    // Skip objects that: don't exist in this session's map, are null, or are owned by this session.
                    uint gonetId = stream.gonetIds[objIdx];
                    if (!GONetMain.gonetParticipantByGONetIdMap.TryGetValue(gonetId, out var gnp) || gnp == null || gnp.IsMine)
                        continue;

                    // REPARENTING FIX (Jan 2026): Skip transform sync if suspended due to parenting.
                    // When a GNP is parented under another GNP with transform sync enabled,
                    // we suspend the child's transform sync to prevent hierarchy ordering desync.
                    // The parent's transform sync will position the child correctly via hierarchy.
                    if (gnp.IsTransformSyncSuspendedDueToParenting)
                        continue;

                    if (!gnp.IsRotationSyncd)
                    {
                        SoA_ObjectHealthMonitor.OnApply(gonetId, wasApplied: false, Vector3.zero, SoA_ObjectHealthMonitor.SKIP_SYNC_DISABLED);
                        continue;
                    }

                    // STALE DATA FIX (December 2025): Skip objects with only seeded data (no actual sync received).
                    // Objects with historyCount <= 2 only have the initial seed samples from registration.
                    // If no real sync data has arrived, applying these stale rotations causes objects to be "stuck".
                    //
                    // EXCEPTION: During post-handoff grace period, bypass this check.
                    // After voluntary handoff, historyCount is reset to 0. The first sync samples from
                    // the new host would be rejected, causing rotation values to stay stuck.
                    if (stream.historyCount[objIdx] <= 2 && !GONetMain.IsInPostHandoffGracePeriod)
                    {
                        continue;
                    }

                    // Get Transform from GCHandle pointer
                    IntPtr transformPtr = stream.transformPtrs[objIdx];
                    if (transformPtr == IntPtr.Zero)
                        continue;

                    GCHandle handle = GCHandle.FromIntPtr(transformPtr);
                    if (!handle.IsAllocated)
                        continue;

                    Transform transform = handle.Target as Transform;
                    if (transform == null)
                        continue;

                    if (!ValidateTransformMapping(gonetId, transform, gnp, streamIdx, objIdx, isPosition: false))
                        continue;

                    // Apply blended rotation
                    Quaternion blendedRot = shadowRotations[shadowOffset + objIdx];

                    // Validate quaternion (must be normalized, no NaN)
                    if (!float.IsNaN(blendedRot.x) && !float.IsInfinity(blendedRot.x) &&
                        !float.IsNaN(blendedRot.y) && !float.IsInfinity(blendedRot.y) &&
                        !float.IsNaN(blendedRot.z) && !float.IsInfinity(blendedRot.z) &&
                        !float.IsNaN(blendedRot.w) && !float.IsInfinity(blendedRot.w))
                    {
                        transform.rotation = blendedRot;
                    }
                }

                shadowOffset += stream.capacity;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ValidateTransformMapping(uint gonetId, Transform transform, GONetParticipant gnp, int streamIdx, int objIdx, bool isPosition)
        {
            Transform expectedTransform = gnp.transform;
            if (expectedTransform == transform)
                return true;

            if (s_MismatchedTransformIds.Add(gonetId))
            {
                string expectedName = expectedTransform != null ? expectedTransform.name : "<null>";
                string actualName = transform != null ? transform.name : "<null>";
                GONetLog.Warning($"[SoA-MAP] Transform mismatch GONetId={gonetId} entry={streamIdx}:{objIdx} expected='{expectedName}' actual='{actualName}' - deactivating SoA");
            }

            GONetMain.SoA_DeactivateTransformEntriesForGONetId(gonetId, "transform-mismatch", scanAllStreams: true);
            return false;
        }

        /// <summary>
        /// Combined position+rotation apply using SetPositionAndRotation for better performance.
        /// Use this when both position and rotation streams have matching objects.
        ///
        /// NOTE: This requires position and rotation streams to be aligned (same objects in same order).
        /// In Phase 1, we keep separate applies for simplicity and correctness.
        /// </summary>
        public static unsafe void ApplyCombined(ref NonAuthorityBlendingSoA_Final soaData)
        {
            if (!s_IsInitialized || !soaData.IsInitialized)
                return;

            NativeArray<Vector3> positions = soaData.GetPreviousShadowPositions();
            NativeArray<Quaternion> rotations = soaData.GetPreviousShadowRotations();

            // For combined apply, we assume matching streams (position[i] and rotation[i] are same object)
            // This is true for Transform sync where both position and rotation are synced
            if (soaData.positionStreams == null || soaData.rotationStreams == null)
                return;

            int posStreamCount = soaData.positionStreams.Length;
            int rotStreamCount = soaData.rotationStreams.Length;
            int minStreams = Math.Min(posStreamCount, rotStreamCount);

            int posShadowOffset = 0;
            int rotShadowOffset = 0;

            for (int streamIdx = 0; streamIdx < minStreams; streamIdx++)
            {
                ref var posStream = ref soaData.positionStreams[streamIdx];
                ref var rotStream = ref soaData.rotationStreams[streamIdx];

                int minObjects = Math.Min(posStream.activeCount, rotStream.activeCount);

                for (int objIdx = 0; objIdx < minObjects; objIdx++)
                {
                    if (!posStream.isActive[objIdx] || !rotStream.isActive[objIdx])
                        continue;

                    // Verify same object (GONetId should match)
                    uint gonetId = posStream.gonetIds[objIdx];
                    if (gonetId != rotStream.gonetIds[objIdx])
                        continue;

                    // MULTI-SESSION FIX (December 2025): Verify this object belongs to the current session.
                    if (!GONetMain.gonetParticipantByGONetIdMap.TryGetValue(gonetId, out var gnp) || gnp == null || gnp.IsMine)
                        continue;

                    // REPARENTING FIX (Jan 2026): Skip transform sync if suspended due to parenting.
                    // When a GNP is parented under another GNP with transform sync enabled,
                    // we suspend the child's transform sync to prevent the combined apply from
                    // overwriting the local position offset set during reparenting.
                    if (gnp.IsTransformSyncSuspendedDueToParenting)
                        continue;

                    if (!gnp.IsPositionSyncd || !gnp.IsRotationSyncd)
                    {
                        SoA_ObjectHealthMonitor.OnApply(gonetId, wasApplied: false, Vector3.zero, SoA_ObjectHealthMonitor.SKIP_SYNC_DISABLED);
                        continue;
                    }

                    // STALE DATA FIX (December 2025): Skip objects with only seeded data (no actual sync received).
                    // Check both position and rotation historyCount since we're applying both.
                    // EXCEPTION: During post-handoff grace period, bypass this check.
                    // Position/rotation have write methods (SoA_WritePositionUpdate/SoA_WriteRotationUpdate)
                    // that populate real data, so the grace period bypass is valid here.
                    if ((posStream.historyCount[objIdx] <= 2 || rotStream.historyCount[objIdx] <= 2) && !GONetMain.IsInPostHandoffGracePeriod)
                    {
                        SoA_ObjectHealthMonitor.OnApply(gonetId, wasApplied: false, Vector3.zero, SoA_ObjectHealthMonitor.SKIP_STALE_DATA);
                        continue;
                    }

                    // Get Transform
                    IntPtr transformPtr = posStream.transformPtrs[objIdx];
                    if (transformPtr == IntPtr.Zero)
                        continue;

                    GCHandle handle = GCHandle.FromIntPtr(transformPtr);
                    if (!handle.IsAllocated)
                        continue;

                    Transform transform = handle.Target as Transform;
                    if (transform == null)
                        continue;

                    Vector3 pos = positions[posShadowOffset + objIdx];
                    Quaternion rot = rotations[rotShadowOffset + objIdx];

                    // Validate
                    bool validPos = !float.IsNaN(pos.x) && !float.IsInfinity(pos.x) &&
                                    !float.IsNaN(pos.y) && !float.IsInfinity(pos.y) &&
                                    !float.IsNaN(pos.z) && !float.IsInfinity(pos.z);

                    bool validRot = !float.IsNaN(rot.x) && !float.IsInfinity(rot.x) &&
                                    !float.IsNaN(rot.y) && !float.IsInfinity(rot.y) &&
                                    !float.IsNaN(rot.z) && !float.IsInfinity(rot.z) &&
                                    !float.IsNaN(rot.w) && !float.IsInfinity(rot.w);

                    // Apply using combined method (faster than separate calls)
                    if (validPos && validRot)
                    {
                        transform.SetPositionAndRotation(pos, rot);
                        SoA_ObjectHealthMonitor.OnApply(gonetId, wasApplied: true, pos);
                    }
                    else if (validPos)
                    {
                        transform.position = pos;
                        SoA_ObjectHealthMonitor.OnApply(gonetId, wasApplied: true, pos);
                    }
                    else if (validRot)
                    {
                        transform.rotation = rot;
                        // Note: Position-only tracking is sufficient for stuck detection
                    }
                    else
                    {
                        SoA_ObjectHealthMonitor.OnApply(gonetId, wasApplied: false, Vector3.zero, SoA_ObjectHealthMonitor.SKIP_NAN_POSITION);
                    }
                }

                posShadowOffset += posStream.capacity;
                rotShadowOffset += rotStream.capacity;
            }
        }

        /// <summary>
        /// Apply Vector2 values using generic SetAutoMagicalSyncValue.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ApplyVector2(ref NonAuthorityBlendingSoA_Final soaData, NativeArray<Vector2> shadowVector2)
        {
            if (soaData.vector2Streams == null)
                return;

            int shadowOffset = 0;
            for (int streamIdx = 0; streamIdx < soaData.vector2Streams.Length; streamIdx++)
            {
                ref var stream = ref soaData.vector2Streams[streamIdx];

                for (int objIdx = 0; objIdx < stream.activeCount; objIdx++)
                {
                    if (!stream.isActive[objIdx])
                        continue;

                    // MULTI-SESSION FIX (December 2025): Verify this object belongs to the current session.
                    uint gonetId = stream.gonetIds[objIdx];
                    if (!GONetMain.gonetParticipantByGONetIdMap.TryGetValue(gonetId, out var gnp) || gnp == null || gnp.IsMine)
                        continue;

                    // STALE DATA FIX (December 2025): Skip objects with only seeded data (no actual sync received).
                    // NOTE: Unlike position/rotation, Vector2 does NOT have an SoA write method to populate
                    // real network data. Bypassing this check during grace period would apply stale seed values
                    // and overwrite v1 blending results. Vector2 must use v1 blending exclusively until
                    // SoA_WriteVector2Update is implemented.
                    if (stream.historyCount[objIdx] <= 2)
                        continue;

                    // Get sync companion from GCHandle pointer
                    IntPtr companionPtr = stream.companionPtrs[objIdx];
                    if (companionPtr == IntPtr.Zero)
                        continue;

                    GCHandle handle = GCHandle.FromIntPtr(companionPtr);
                    if (!handle.IsAllocated)
                        continue;

                    var syncCompanion = handle.Target as GONetParticipant_AutoMagicalSyncCompanion_Generated;
                    if (syncCompanion == null)
                        continue;

                    // Get memberIndex and blended value
                    byte memberIndex = stream.memberIndices[objIdx];
                    Vector2 blendedVal = shadowVector2[shadowOffset + objIdx];

                    // Validate before applying (prevent NaN/Infinity corruption)
                    if (!float.IsNaN(blendedVal.x) && !float.IsInfinity(blendedVal.x) &&
                        !float.IsNaN(blendedVal.y) && !float.IsInfinity(blendedVal.y))
                    {
                        // Apply via generic SetAutoMagicalSyncValue
                        GONetSyncableValue syncValue = new GONetSyncableValue();
                        syncValue.UnityEngine_Vector2 = blendedVal;
                        syncCompanion.SetAutoMagicalSyncValue(memberIndex, syncValue);
                    }
                }

                shadowOffset += stream.capacity;
            }
        }

        /// <summary>
        /// Apply Vector4 values using generic SetAutoMagicalSyncValue.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ApplyVector4(ref NonAuthorityBlendingSoA_Final soaData, NativeArray<Vector4> shadowVector4)
        {
            if (soaData.vector4Streams == null)
                return;

            int shadowOffset = 0;
            for (int streamIdx = 0; streamIdx < soaData.vector4Streams.Length; streamIdx++)
            {
                ref var stream = ref soaData.vector4Streams[streamIdx];

                for (int objIdx = 0; objIdx < stream.activeCount; objIdx++)
                {
                    if (!stream.isActive[objIdx])
                        continue;

                    // MULTI-SESSION FIX (December 2025): Verify this object belongs to the current session.
                    uint gonetId = stream.gonetIds[objIdx];
                    if (!GONetMain.gonetParticipantByGONetIdMap.TryGetValue(gonetId, out var gnp) || gnp == null || gnp.IsMine)
                        continue;

                    // STALE DATA FIX (December 2025): Skip objects with only seeded data (no actual sync received).
                    // NOTE: Unlike position/rotation, Vector4 does NOT have an SoA write method to populate
                    // real network data. Bypassing this check during grace period would apply stale seed values
                    // and overwrite v1 blending results. Vector4 must use v1 blending exclusively until
                    // SoA_WriteVector4Update is implemented.
                    if (stream.historyCount[objIdx] <= 2)
                        continue;

                    // Get sync companion from GCHandle pointer
                    IntPtr companionPtr = stream.companionPtrs[objIdx];
                    if (companionPtr == IntPtr.Zero)
                        continue;

                    GCHandle handle = GCHandle.FromIntPtr(companionPtr);
                    if (!handle.IsAllocated)
                        continue;

                    var syncCompanion = handle.Target as GONetParticipant_AutoMagicalSyncCompanion_Generated;
                    if (syncCompanion == null)
                        continue;

                    // Get memberIndex and blended value
                    byte memberIndex = stream.memberIndices[objIdx];
                    Vector4 blendedVal = shadowVector4[shadowOffset + objIdx];

                    // Validate before applying (prevent NaN/Infinity corruption)
                    if (!float.IsNaN(blendedVal.x) && !float.IsInfinity(blendedVal.x) &&
                        !float.IsNaN(blendedVal.y) && !float.IsInfinity(blendedVal.y) &&
                        !float.IsNaN(blendedVal.z) && !float.IsInfinity(blendedVal.z) &&
                        !float.IsNaN(blendedVal.w) && !float.IsInfinity(blendedVal.w))
                    {
                        // Apply via generic SetAutoMagicalSyncValue
                        GONetSyncableValue syncValue = new GONetSyncableValue();
                        syncValue.UnityEngine_Vector4 = blendedVal;
                        syncCompanion.SetAutoMagicalSyncValue(memberIndex, syncValue);
                    }
                }

                shadowOffset += stream.capacity;
            }
        }

        /// <summary>
        /// Get diagnostic information about applied values.
        /// </summary>
        public static string GetDiagnostics(ref NonAuthorityBlendingSoA_Final soaData)
        {
            if (!s_IsInitialized)
                return "[SoA-Applicator] Not initialized";

            int posApplied = 0;
            int rotApplied = 0;
            int vec2Applied = 0;
            int vec4Applied = 0;

            if (soaData.positionStreams != null)
            {
                for (int i = 0; i < soaData.positionStreams.Length; i++)
                    posApplied += soaData.positionStreams[i].activeCount;
            }

            if (soaData.rotationStreams != null)
            {
                for (int i = 0; i < soaData.rotationStreams.Length; i++)
                    rotApplied += soaData.rotationStreams[i].activeCount;
            }

            if (soaData.vector2Streams != null)
            {
                for (int i = 0; i < soaData.vector2Streams.Length; i++)
                    vec2Applied += soaData.vector2Streams[i].activeCount;
            }

            if (soaData.vector4Streams != null)
            {
                for (int i = 0; i < soaData.vector4Streams.Length; i++)
                    vec4Applied += soaData.vector4Streams[i].activeCount;
            }

            return $"[SoA-Applicator] Applied: {posApplied} pos, {rotApplied} rot, {vec2Applied} vec2, {vec4Applied} vec4";
        }
    }
}
