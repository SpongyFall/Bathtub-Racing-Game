/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using System;
using System.Runtime.CompilerServices;
using GONet.Jobs;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace GONet.Core
{
    /// <summary>
    /// Orchestrates SoA blending job scheduling for the unified blending pipeline.
    /// Replaces the scattered Update_SoA_DynamicMultiRate scheduling with a centralized approach.
    ///
    /// Key Features:
    /// - Schedules Burst-compiled blending jobs for all value types
    /// - Supports pluggable blending strategies via enum switch
    /// - Combines job handles for parallel execution
    /// - Provides clear separation between job scheduling and value application
    /// - Deferred completion: schedule early in frame, complete late for max parallelism
    /// </summary>
    public static class SoA_BlendingPipeline
    {
        // NOTE: We do NOT cache NonAuthorityBlendingSoA_Final (struct copy = stale pointers after resize!)
        // Instead, pass ref each frame to Execute() and Apply() methods.

        // Job handles for current frame
        private static JobHandle s_CombinedJobHandle;

        // Initialization state
        private static bool s_IsInitialized;

        // Deferred completion state (for async scheduling - schedule early, complete late)
        private static bool s_JobsScheduledPendingCompletion;
        private static int s_ScheduledFrame; // Track which frame jobs were scheduled on

        /// <summary>
        /// Returns true if blending jobs have been scheduled but not yet completed.
        /// </summary>
        public static bool HasPendingJobs => s_JobsScheduledPendingCompletion;

        /// <summary>
        /// Initialize the blending pipeline. Call once during GONet initialization.
        /// </summary>
        public static void Initialize(ref NonAuthorityBlendingSoA_Final soaData)
        {
            if (s_IsInitialized)
                return;

            // Just mark as initialized - soaData passed fresh each Execute() call
            s_IsInitialized = true;
            s_JobsScheduledPendingCompletion = false;

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Info("[SoA-Pipeline] Initialized unified blending pipeline");
            }
        }

        /// <summary>
        /// Shutdown the pipeline. Call during GONet shutdown.
        /// </summary>
        public static void Shutdown()
        {
            if (!s_IsInitialized)
                return;

            // Ensure all jobs complete before shutdown
            s_CombinedJobHandle.Complete();
            s_JobsScheduledPendingCompletion = false;

            s_IsInitialized = false;

            if (GONetFeatureFlags.DebugUnifiedSoABlending)
            {
                GONetLog.Info("[SoA-Pipeline] Shutdown unified blending pipeline");
            }
        }

        /// <summary>
        /// Execute the unified blending pipeline SYNCHRONOUSLY.
        /// Schedules all blending jobs and waits for completion.
        ///
        /// NOTE: For better performance, prefer ScheduleBlendingJobs() + CompleteBlendingJobs()
        /// to allow jobs to run while main thread does other work.
        ///
        /// Call this from GONetMain update loop when UseUnifiedSoABlending = true.
        /// </summary>
        /// <param name="targetTicks">Target time for blending (GONetMain.Time.ElapsedTicks)</param>
        public static void Execute(ref NonAuthorityBlendingSoA_Final soaData, long targetTicks)
        {
            // Synchronous path: schedule and immediately complete
            ScheduleBlendingJobs(ref soaData, targetTicks);
            CompleteBlendingJobs(ref soaData);
        }

        /// <summary>
        /// Schedule all blending jobs for execution on worker threads.
        /// Jobs will run in parallel with main thread work.
        ///
        /// Call CompleteBlendingJobs() later (ideally in LateUpdate) to wait for completion.
        ///
        /// THREADING: This method schedules Burst-compiled IJobParallelFor jobs.
        /// Jobs run on Unity worker threads, NOT the main thread.
        /// Main thread returns immediately after scheduling.
        /// </summary>
        /// <param name="targetTicks">Target time for blending (GONetMain.Time.ElapsedTicks)</param>
        public static void ScheduleBlendingJobs(ref NonAuthorityBlendingSoA_Final soaData, long targetTicks)
        {
            if (!s_IsInitialized || !soaData.IsInitialized)
                return;

            // Safety: If jobs from a previous frame were not completed, complete them now
            // This should not happen in normal operation but prevents job system errors
            if (s_JobsScheduledPendingCompletion)
            {
                GONetLog.Warning("[SoA-Pipeline] Previous frame jobs not completed! Forcing completion.");
                s_CombinedJobHandle.Complete();
                s_JobsScheduledPendingCompletion = false;
            }

            // Phase 1: Schedule all blending jobs in parallel
            // Each Schedule() returns immediately - jobs run on worker threads
            JobHandle posHandle = SchedulePositionBlending(ref soaData, targetTicks);
            JobHandle rotHandle = ScheduleRotationBlending(ref soaData, targetTicks);
            JobHandle vec2Handle = ScheduleVector2Blending(ref soaData, targetTicks);
            JobHandle vec4Handle = ScheduleVector4Blending(ref soaData, targetTicks);
            // TODO: Schedule scalar blending when enabled

            // Phase 2: Combine all job handles (4-way combine)
            // Jobs now running in parallel on worker threads
            JobHandle posRotHandle = JobHandle.CombineDependencies(posHandle, rotHandle);
            JobHandle vec2Vec4Handle = JobHandle.CombineDependencies(vec2Handle, vec4Handle);
            s_CombinedJobHandle = JobHandle.CombineDependencies(posRotHandle, vec2Vec4Handle);

            // Mark as pending - main thread can now do other work
            s_JobsScheduledPendingCompletion = true;
            s_ScheduledFrame = UnityEngine.Time.frameCount;
        }

        /// <summary>
        /// Wait for scheduled blending jobs to complete and swap shadow buffers.
        ///
        /// Call this after ScheduleBlendingJobs(), ideally late in the frame (LateUpdate)
        /// to maximize time for jobs to complete on worker threads.
        ///
        /// THREADING: Blocks main thread until all worker threads finish.
        /// If jobs already completed, returns immediately (no blocking).
        /// </summary>
        public static void CompleteBlendingJobs(ref NonAuthorityBlendingSoA_Final soaData)
        {
            if (!s_JobsScheduledPendingCompletion)
                return;

            // Wait for all jobs to complete (may already be done if enough time passed)
            // If jobs finished while main thread did other work, this returns immediately
            s_CombinedJobHandle.Complete();
            s_JobsScheduledPendingCompletion = false;

            // Swap shadow buffers (ping-pong for 1-frame delay)
            soaData.SwapShadowBuffers();
        }

        /// <summary>
        /// Check if scheduled jobs have completed without blocking.
        /// Useful for diagnostics or adaptive scheduling.
        /// </summary>
        public static bool AreJobsComplete()
        {
            if (!s_JobsScheduledPendingCompletion)
                return true;

            return s_CombinedJobHandle.IsCompleted;
        }

        /// <summary>
        /// Ensure any pending blending jobs have completed before modifying SoA arrays.
        /// MUST be called before UnregisterObject or any other operation that writes to SoA NativeArrays.
        /// Does NOT swap shadow buffers (that happens in CompleteBlendingJobs).
        /// </summary>
        public static void EnsureJobsComplete()
        {
            if (s_JobsScheduledPendingCompletion)
            {
                s_CombinedJobHandle.Complete();
                // Note: We don't set s_JobsScheduledPendingCompletion = false here because
                // CompleteBlendingJobs still needs to do the buffer swap.
                // The job handle is now safe for writes, but the pipeline state is unchanged.
            }
        }

        /// <summary>
        /// Schedule position blending jobs for all Vector3 streams.
        /// Returns combined job handle.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static JobHandle SchedulePositionBlending(ref NonAuthorityBlendingSoA_Final soaData, long targetTicks)
        {
            if (soaData.positionStreams == null || soaData.positionStreams.Length == 0)
                return default;

            JobHandle combinedHandle = default;
            double ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond;

            NativeArray<Vector3> shadowBuffer = soaData.GetCurrentShadowPositions();

            int shadowOffset = 0;
            for (int streamIdx = 0; streamIdx < soaData.positionStreams.Length; streamIdx++)
            {
                ref var stream = ref soaData.positionStreams[streamIdx];

                if (stream.activeCount == 0)
                {
                    shadowOffset += stream.capacity;
                    continue;
                }

                var job = new BlendPositionsJob
                {
                    posX = stream.posX,
                    posY = stream.posY,
                    posZ = stream.posZ,
                    posTicks = stream.posTicks,
                    historyCount = stream.historyCount,
                    isActive = stream.isActive,
                    blendStrategy = stream.blendStrategy,
                    shadowPos = shadowBuffer.GetSubArray(shadowOffset, stream.capacity),
                    targetElapsedTicks = targetTicks,
                    ticksToSeconds = ticksToSeconds
                };

                var handle = job.Schedule(stream.activeCount, 64, combinedHandle);
                combinedHandle = handle;

                shadowOffset += stream.capacity;
            }

            return combinedHandle;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static JobHandle ScheduleRotationBlending(ref NonAuthorityBlendingSoA_Final soaData, long targetTicks)
        {
            if (soaData.rotationStreams == null || soaData.rotationStreams.Length == 0)
                return default;

            JobHandle combinedHandle = default;
            double ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond;

            NativeArray<Quaternion> shadowBuffer = soaData.GetCurrentShadowRotations();

            int shadowOffset = 0;
            for (int streamIdx = 0; streamIdx < soaData.rotationStreams.Length; streamIdx++)
            {
                ref var stream = ref soaData.rotationStreams[streamIdx];

                if (stream.activeCount == 0)
                {
                    shadowOffset += stream.capacity;
                    continue;
                }

                var job = new BlendRotationsJob
                {
                    rotX = stream.rotX,
                    rotY = stream.rotY,
                    rotZ = stream.rotZ,
                    rotW = stream.rotW,
                    rotTicks = stream.rotTicks,
                    historyCount = stream.historyCount,
                    isActive = stream.isActive,
                    blendStrategy = stream.blendStrategy,
                    shadowRot = shadowBuffer.GetSubArray(shadowOffset, stream.capacity),
                    targetElapsedTicks = targetTicks,
                    ticksToSeconds = ticksToSeconds
                };

                var handle = job.Schedule(stream.activeCount, 64, combinedHandle);
                combinedHandle = handle;

                shadowOffset += stream.capacity;
            }

            return combinedHandle;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static JobHandle ScheduleVector2Blending(ref NonAuthorityBlendingSoA_Final soaData, long targetTicks)
        {
            if (soaData.vector2Streams == null || soaData.vector2Streams.Length == 0)
                return default;

            JobHandle combinedHandle = default;
            double ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond;

            NativeArray<Vector2> shadowBuffer = soaData.GetCurrentShadowVector2();
            if (!shadowBuffer.IsCreated)
                return default;

            int shadowOffset = 0;
            for (int streamIdx = 0; streamIdx < soaData.vector2Streams.Length; streamIdx++)
            {
                ref var stream = ref soaData.vector2Streams[streamIdx];

                if (stream.activeCount == 0)
                {
                    shadowOffset += stream.capacity;
                    continue;
                }

                var job = new BlendVector2Job
                {
                    valX = stream.valX,
                    valY = stream.valY,
                    valTicks = stream.valTicks,
                    historyCount = stream.historyCount,
                    isActive = stream.isActive,
                    blendStrategy = stream.blendStrategy,
                    shadowVal = shadowBuffer.GetSubArray(shadowOffset, stream.capacity),
                    targetElapsedTicks = targetTicks,
                    ticksToSeconds = ticksToSeconds
                };

                var handle = job.Schedule(stream.activeCount, 64, combinedHandle);
                combinedHandle = handle;

                shadowOffset += stream.capacity;
            }

            return combinedHandle;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static JobHandle ScheduleVector4Blending(ref NonAuthorityBlendingSoA_Final soaData, long targetTicks)
        {
            if (soaData.vector4Streams == null || soaData.vector4Streams.Length == 0)
                return default;

            JobHandle combinedHandle = default;
            double ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond;

            NativeArray<Vector4> shadowBuffer = soaData.GetCurrentShadowVector4();
            if (!shadowBuffer.IsCreated)
                return default;

            int shadowOffset = 0;
            for (int streamIdx = 0; streamIdx < soaData.vector4Streams.Length; streamIdx++)
            {
                ref var stream = ref soaData.vector4Streams[streamIdx];

                if (stream.activeCount == 0)
                {
                    shadowOffset += stream.capacity;
                    continue;
                }

                var job = new BlendVector4Job
                {
                    valX = stream.valX,
                    valY = stream.valY,
                    valZ = stream.valZ,
                    valW = stream.valW,
                    valTicks = stream.valTicks,
                    historyCount = stream.historyCount,
                    isActive = stream.isActive,
                    blendStrategy = stream.blendStrategy,
                    shadowVal = shadowBuffer.GetSubArray(shadowOffset, stream.capacity),
                    targetElapsedTicks = targetTicks,
                    ticksToSeconds = ticksToSeconds
                };

                var handle = job.Schedule(stream.activeCount, 64, combinedHandle);
                combinedHandle = handle;

                shadowOffset += stream.capacity;
            }

            return combinedHandle;
        }

        public static bool HasActiveStreams(ref NonAuthorityBlendingSoA_Final soaData)
        {
            if (!s_IsInitialized || !soaData.IsInitialized)
                return false;

            if (soaData.positionStreams != null)
            {
                for (int i = 0; i < soaData.positionStreams.Length; i++)
                {
                    if (soaData.positionStreams[i].activeCount > 0)
                        return true;
                }
            }

            if (soaData.rotationStreams != null)
            {
                for (int i = 0; i < soaData.rotationStreams.Length; i++)
                {
                    if (soaData.rotationStreams[i].activeCount > 0)
                        return true;
                }
            }

            if (soaData.vector2Streams != null)
            {
                for (int i = 0; i < soaData.vector2Streams.Length; i++)
                {
                    if (soaData.vector2Streams[i].activeCount > 0)
                        return true;
                }
            }

            if (soaData.vector4Streams != null)
            {
                for (int i = 0; i < soaData.vector4Streams.Length; i++)
                {
                    if (soaData.vector4Streams[i].activeCount > 0)
                        return true;
                }
            }

            return false;
        }

        public static string GetDiagnostics(ref NonAuthorityBlendingSoA_Final soaData)
        {
            if (!s_IsInitialized)
                return "[SoA-Pipeline] Not initialized";

            int posStreams = soaData.positionStreams?.Length ?? 0;
            int rotStreams = soaData.rotationStreams?.Length ?? 0;
            int vec2Streams = soaData.vector2Streams?.Length ?? 0;
            int vec4Streams = soaData.vector4Streams?.Length ?? 0;
            int posObjects = 0;
            int rotObjects = 0;
            int vec2Objects = 0;
            int vec4Objects = 0;

            if (soaData.positionStreams != null)
            {
                for (int i = 0; i < soaData.positionStreams.Length; i++)
                    posObjects += soaData.positionStreams[i].activeCount;
            }

            if (soaData.rotationStreams != null)
            {
                for (int i = 0; i < soaData.rotationStreams.Length; i++)
                    rotObjects += soaData.rotationStreams[i].activeCount;
            }

            if (soaData.vector2Streams != null)
            {
                for (int i = 0; i < soaData.vector2Streams.Length; i++)
                    vec2Objects += soaData.vector2Streams[i].activeCount;
            }

            if (soaData.vector4Streams != null)
            {
                for (int i = 0; i < soaData.vector4Streams.Length; i++)
                    vec4Objects += soaData.vector4Streams[i].activeCount;
            }

            string pendingStatus = s_JobsScheduledPendingCompletion
                ? ", pending frame " + s_ScheduledFrame
                : ", no pending jobs";
            return "[SoA-Pipeline] Streams: pos=" + posStreams + " (" + posObjects + "), rot=" + rotStreams + " (" + rotObjects + "), vec2=" + vec2Streams + " (" + vec2Objects + "), vec4=" + vec4Streams + " (" + vec4Objects + ")" + pendingStatus;
        }
    }
}
