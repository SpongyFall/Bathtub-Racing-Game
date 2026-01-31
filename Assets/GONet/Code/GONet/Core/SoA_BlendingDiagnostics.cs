/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;

namespace GONet.Core
{
    /// <summary>
    /// Diagnostic logging for SoA blending pipeline.
    /// Logs dtTarget values to separate log file for Python analysis.
    ///
    /// Usage:
    /// 1. Enable LOG_BLEND_DIAG define
    /// 2. Run test session (server + clients)
    /// 3. Find log file: gonet-BlendDiag-YYYY-MM-DD.log
    /// 4. Analyze: python analyze_blending.py "path/to/gonet-BlendDiag-*.log"
    /// </summary>
    public static class SoA_BlendingDiagnostics
    {
        public const string PROFILE_NAME = "BlendDiag";
        private const int RING_SIZE = 8;

        private static bool s_IsInitialized;
        private static GONetLog.LoggingProfile s_Profile;
        private static long s_LastLogFrame = -1;
        private static int s_SampleCounter = 0;
        private const int LOG_EVERY_N_FRAMES = 5; // Log every 5 frames to reduce spam

        /// <summary>
        /// Initialize the diagnostics logging profile.
        /// Call once during GONet initialization when LOG_BLEND_DIAG is defined.
        /// </summary>
        [System.Diagnostics.Conditional("LOG_BLEND_DIAG")]
        public static void Initialize()
        {
            if (s_IsInitialized) return;

            s_Profile = new GONetLog.LoggingProfile(
                PROFILE_NAME,
                outputToSeparateFile: true,
                includeStackTraces: false,
                minimumLogLevel: GONetLog.LogLevel.Debug
            );
            GONetLog.RegisterLoggingProfile(s_Profile);
            s_IsInitialized = true;

            GONetLog.Info($"[BlendDiag] Initialized blending diagnostics profile. Look for gonet-{PROFILE_NAME}-*.log", PROFILE_NAME);
        }

        /// <summary>
        /// Shutdown diagnostics.
        /// </summary>
        [System.Diagnostics.Conditional("LOG_BLEND_DIAG")]
        public static void Shutdown()
        {
            if (!s_IsInitialized) return;
            GONetLog.UnregisterLoggingProfile(PROFILE_NAME);
            s_IsInitialized = false;
        }

        /// <summary>
        /// Log blending diagnostics after jobs complete.
        /// Computes dtTarget for each active object and logs to separate file.
        ///
        /// Format: BLEND|frame|elapsedSec|streamType|objectIdx|dtTarget|dtSamples|histCount|strategy|isExtrap|bufferLeadSec|isPhysics|gonetId
        /// </summary>
        [System.Diagnostics.Conditional("LOG_BLEND_DIAG")]
        public static void LogBlendingMetrics(ref NonAuthorityBlendingSoA_Final soaData, long targetTicks, long bufferLeadTicks)
        {
            if (!s_IsInitialized || !soaData.IsInitialized) return;

            long currentFrame = GONetMain.Time?.FrameCount ?? 0;

            // Rate limit: only log every N frames
            if (currentFrame == s_LastLogFrame) return;
            if ((currentFrame % LOG_EVERY_N_FRAMES) != 0) return;
            s_LastLogFrame = currentFrame;

            double ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond;
            double elapsedSec = GONetMain.Time?.ElapsedSeconds ?? 0;
            double bufferLeadSec = bufferLeadTicks * ticksToSeconds;

            // Log header every 100 samples for context
            string role = GONetMain.IsServer ? "SVR" : "CLI";
            long currentElapsedTicks = GONetMain.Time?.ElapsedTicks ?? 0;
            if ((s_SampleCounter % 100) == 0)
            {
                // NEW FORMAT: tValue is the actual Lerp parameter, dtBracket is the gap between bracketing samples
                GONetLog.Debug($"HDR|role|frame|elapsedSec|streamType|objIdx|tValue|dtBracket|dtFromUpper|bracketIdx|validCount|sampleAge|isExtrap|isPhysics|gonetId|isMine", PROFILE_NAME);
            }
            s_SampleCounter++;

            // Log position streams
            LogPositionMetrics(ref soaData, targetTicks, currentElapsedTicks, currentFrame, elapsedSec, ticksToSeconds, bufferLeadSec, role);

            // Log rotation streams
            LogRotationMetrics(ref soaData, targetTicks, currentElapsedTicks, currentFrame, elapsedSec, ticksToSeconds, bufferLeadSec, role);
        }

        private static void LogPositionMetrics(ref NonAuthorityBlendingSoA_Final soaData, long targetTicks, long currentElapsedTicks, long frame, double elapsedSec, double ticksToSeconds, double bufferLeadSec, string role)
        {
            if (soaData.positionStreams == null) return;

            for (int streamIdx = 0; streamIdx < soaData.positionStreams.Length; streamIdx++)
            {
                ref var stream = ref soaData.positionStreams[streamIdx];
                if (stream.activeCount == 0) continue;

                for (int i = 0; i < stream.activeCount; i++)
                {
                    if (!stream.isActive[i]) continue;

                    int count = stream.historyCount[i];
                    if (count < 2) continue;

                    int baseIdx = i * RING_SIZE;

                    // MIRROR THE BLENDING JOB LOGIC: Sort samples and find bracketing pair
                    // This gives us the ACTUAL t value being used for interpolation
                    long[] sortedTicks = new long[RING_SIZE];
                    int validCount = 0;

                    for (int slot = 0; slot < RING_SIZE && slot < count; slot++)
                    {
                        long ticks = stream.posTicks[baseIdx + slot];
                        if (ticks <= 0) continue;

                        // Insert sorted (ascending)
                        int insertPos = validCount;
                        for (int j = 0; j < validCount; j++)
                        {
                            if (ticks < sortedTicks[j]) { insertPos = j; break; }
                        }
                        for (int j = validCount; j > insertPos; j--)
                            sortedTicks[j] = sortedTicks[j - 1];
                        sortedTicks[insertPos] = ticks;
                        validCount++;
                    }

                    if (validCount < 2) continue;

                    // Find bracketing pair (same logic as BlendingJobs)
                    long lowerTicks = sortedTicks[0], upperTicks = sortedTicks[1];
                    int bracketIdx = 0; // Which pair index (0 = oldest pair, higher = newer pairs)

                    for (int j = 0; j < validCount - 1; j++)
                    {
                        if (targetTicks >= sortedTicks[j] && targetTicks <= sortedTicks[j + 1])
                        {
                            lowerTicks = sortedTicks[j];
                            upperTicks = sortedTicks[j + 1];
                            bracketIdx = j;
                            break;
                        }
                        // If target is after all samples, use newest pair
                        if (j == validCount - 2 && targetTicks > sortedTicks[j + 1])
                        {
                            lowerTicks = sortedTicks[j];
                            upperTicks = sortedTicks[j + 1];
                            bracketIdx = j;
                        }
                    }

                    // Compute the ACTUAL interpolation values
                    float dtBracket = (float)((upperTicks - lowerTicks) * ticksToSeconds); // Gap between bracketing samples
                    float dtFromUpper = (float)((targetTicks - upperTicks) * ticksToSeconds); // Target relative to upper sample
                    float tValue = (dtBracket > 0.0001f) ? Math.Max(0f, Math.Min(1f, 1f + (dtFromUpper / dtBracket))) : 1f;

                    // Also compute old metrics for comparison
                    long newestTicks = sortedTicks[validCount - 1];
                    float sampleAge = (float)((currentElapsedTicks - newestTicks) * ticksToSeconds);
                    float dtTargetFromNewest = (float)((targetTicks - newestTicks) * ticksToSeconds);
                    bool isExtrap = dtFromUpper > 0;

                    byte strategy = stream.blendStrategy[i];
                    byte writeIdx = stream.historyWriteIndex[i];

                    uint gonetId = stream.gonetIds[i];
                    bool isPhysics = false;
                    bool isMine = false;
                    if (GONetMain.gonetParticipantByGONetIdMap.TryGetValue(gonetId, out var gnp) && gnp != null)
                    {
                        isPhysics = gnp.IsRigidBodyOwnerOnlyControlled;
                        isMine = gnp.IsMine;
                    }

                    // NEW FORMAT with interpolation quality metrics:
                    // BLEND|role|frame|elapsed|type|objIdx|tValue|dtBracket|dtFromUpper|bracketIdx|validCount|sampleAge|isExtrap|isPhysics|gonetId|isMine
                    // tValue: The actual Lerp parameter (0-1). Ideal is 0.3-0.7 (middle of bracket)
                    // dtBracket: Time gap between the two bracketing samples (should match sync rate ~24Hz=42ms or ~50Hz=20ms)
                    // dtFromUpper: Target time relative to upper bracket sample (negative=interpolating, positive=extrapolating)
                    // bracketIdx: Which sample pair is being used (0=oldest, higher=newer)
                    GONetLog.Debug($"BLEND|{role}|{frame}|{elapsedSec:F4}|POS|{streamIdx}:{i}|{tValue:F4}|{dtBracket:F6}|{dtFromUpper:F4}|{bracketIdx}|{validCount}|{sampleAge:F4}|{(isExtrap ? 1 : 0)}|{(isPhysics ? 1 : 0)}|{gonetId}|{(isMine ? 1 : 0)}", PROFILE_NAME);
                }
            }
        }

        private static void LogRotationMetrics(ref NonAuthorityBlendingSoA_Final soaData, long targetTicks, long currentElapsedTicks, long frame, double elapsedSec, double ticksToSeconds, double bufferLeadSec, string role)
        {
            if (soaData.rotationStreams == null) return;

            for (int streamIdx = 0; streamIdx < soaData.rotationStreams.Length; streamIdx++)
            {
                ref var stream = ref soaData.rotationStreams[streamIdx];
                if (stream.activeCount == 0) continue;

                for (int i = 0; i < stream.activeCount; i++)
                {
                    if (!stream.isActive[i]) continue;

                    int count = stream.historyCount[i];
                    if (count < 2) continue;

                    int baseIdx = i * RING_SIZE;

                    // MIRROR THE BLENDING JOB LOGIC: Sort samples and find bracketing pair
                    long[] sortedTicks = new long[RING_SIZE];
                    int validCount = 0;

                    for (int slot = 0; slot < RING_SIZE && slot < count; slot++)
                    {
                        long ticks = stream.rotTicks[baseIdx + slot];
                        if (ticks <= 0) continue;

                        int insertPos = validCount;
                        for (int j = 0; j < validCount; j++)
                        {
                            if (ticks < sortedTicks[j]) { insertPos = j; break; }
                        }
                        for (int j = validCount; j > insertPos; j--)
                            sortedTicks[j] = sortedTicks[j - 1];
                        sortedTicks[insertPos] = ticks;
                        validCount++;
                    }

                    if (validCount < 2) continue;

                    // Find bracketing pair
                    long lowerTicks = sortedTicks[0], upperTicks = sortedTicks[1];
                    int bracketIdx = 0;

                    for (int j = 0; j < validCount - 1; j++)
                    {
                        if (targetTicks >= sortedTicks[j] && targetTicks <= sortedTicks[j + 1])
                        {
                            lowerTicks = sortedTicks[j];
                            upperTicks = sortedTicks[j + 1];
                            bracketIdx = j;
                            break;
                        }
                        if (j == validCount - 2 && targetTicks > sortedTicks[j + 1])
                        {
                            lowerTicks = sortedTicks[j];
                            upperTicks = sortedTicks[j + 1];
                            bracketIdx = j;
                        }
                    }

                    // Compute the ACTUAL interpolation values
                    float dtBracket = (float)((upperTicks - lowerTicks) * ticksToSeconds);
                    float dtFromUpper = (float)((targetTicks - upperTicks) * ticksToSeconds);
                    float tValue = (dtBracket > 0.0001f) ? Math.Max(0f, Math.Min(1f, 1f + (dtFromUpper / dtBracket))) : 1f;

                    long newestTicks = sortedTicks[validCount - 1];
                    float sampleAge = (float)((currentElapsedTicks - newestTicks) * ticksToSeconds);
                    bool isExtrap = dtFromUpper > 0;

                    byte strategy = stream.blendStrategy[i];
                    byte writeIdx = stream.historyWriteIndex[i];

                    uint gonetId = stream.gonetIds[i];
                    bool isPhysics = false;
                    bool isMine = false;
                    if (GONetMain.gonetParticipantByGONetIdMap.TryGetValue(gonetId, out var gnp) && gnp != null)
                    {
                        isPhysics = gnp.IsRigidBodyOwnerOnlyControlled;
                        isMine = gnp.IsMine;
                    }

                    // Same format as position
                    GONetLog.Debug($"BLEND|{role}|{frame}|{elapsedSec:F4}|ROT|{streamIdx}:{i}|{tValue:F4}|{dtBracket:F6}|{dtFromUpper:F4}|{bracketIdx}|{validCount}|{sampleAge:F4}|{(isExtrap ? 1 : 0)}|{(isPhysics ? 1 : 0)}|{gonetId}|{(isMine ? 1 : 0)}", PROFILE_NAME);
                }
            }
        }

        /// <summary>
        /// Log aggregate summary every N seconds (less frequent, for overview).
        /// Format: SUMMARY|frame|elapsedSec|totalPos|totalRot|extrapCount|interpCount|avgDtTarget|minDtTarget|maxDtTarget
        /// </summary>
        [System.Diagnostics.Conditional("LOG_BLEND_DIAG")]
        public static void LogAggregateSummary(ref NonAuthorityBlendingSoA_Final soaData, long targetTicks)
        {
            if (!s_IsInitialized || !soaData.IsInitialized) return;

            double ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond;

            int totalPos = 0, totalRot = 0;
            int extrapCount = 0, interpCount = 0;
            float sumDtTarget = 0, minDt = float.MaxValue, maxDt = float.MinValue;

            // Aggregate position metrics
            if (soaData.positionStreams != null)
            {
                for (int streamIdx = 0; streamIdx < soaData.positionStreams.Length; streamIdx++)
                {
                    ref var stream = ref soaData.positionStreams[streamIdx];
                    totalPos += stream.activeCount;

                    for (int i = 0; i < stream.activeCount; i++)
                    {
                        if (!stream.isActive[i]) continue;
                        int count = stream.historyCount[i];
                        if (count < 2) continue;

                        int baseIdx = i * RING_SIZE;
                        long newestTicks = long.MinValue;
                        for (int slot = 0; slot < RING_SIZE && slot < count; slot++)
                        {
                            long ticks = stream.posTicks[baseIdx + slot];
                            if (ticks > newestTicks) newestTicks = ticks;
                        }

                        float dt = (float)((targetTicks - newestTicks) * ticksToSeconds);
                        if (dt > 0) extrapCount++; else interpCount++;
                        sumDtTarget += dt;
                        if (dt < minDt) minDt = dt;
                        if (dt > maxDt) maxDt = dt;
                    }
                }
            }

            // Aggregate rotation metrics (similar)
            if (soaData.rotationStreams != null)
            {
                for (int streamIdx = 0; streamIdx < soaData.rotationStreams.Length; streamIdx++)
                {
                    ref var stream = ref soaData.rotationStreams[streamIdx];
                    totalRot += stream.activeCount;

                    for (int i = 0; i < stream.activeCount; i++)
                    {
                        if (!stream.isActive[i]) continue;
                        int count = stream.historyCount[i];
                        if (count < 2) continue;

                        int baseIdx = i * RING_SIZE;
                        long newestTicks = long.MinValue;
                        for (int slot = 0; slot < RING_SIZE && slot < count; slot++)
                        {
                            long ticks = stream.rotTicks[baseIdx + slot];
                            if (ticks > newestTicks) newestTicks = ticks;
                        }

                        float dt = (float)((targetTicks - newestTicks) * ticksToSeconds);
                        if (dt > 0) extrapCount++; else interpCount++;
                        sumDtTarget += dt;
                        if (dt < minDt) minDt = dt;
                        if (dt > maxDt) maxDt = dt;
                    }
                }
            }

            int totalSamples = extrapCount + interpCount;
            float avgDt = totalSamples > 0 ? sumDtTarget / totalSamples : 0;
            if (minDt == float.MaxValue) minDt = 0;
            if (maxDt == float.MinValue) maxDt = 0;

            long frame = GONetMain.Time?.FrameCount ?? 0;
            double elapsed = GONetMain.Time?.ElapsedSeconds ?? 0;

            GONetLog.Debug($"SUMMARY|{frame}|{elapsed:F4}|{totalPos}|{totalRot}|{extrapCount}|{interpCount}|{avgDt:F4}|{minDt:F4}|{maxDt:F4}", PROFILE_NAME);
        }

        /// <summary>
        /// Log position data received from network (written to ring buffer).
        /// Format: DATA_IN|role|frame|elapsedSec|POS|gonetId|x|y|z|ticksAtSend|isAnchor|isPhysics
        /// </summary>
        [System.Diagnostics.Conditional("LOG_BLEND_DIAG")]
        public static void LogPositionReceived(uint gonetId, Vector3 position, long ticksAtSend, bool isAnchor, bool isPhysicsObject)
        {
            if (!s_IsInitialized) return;

            string role = GONetMain.IsServer ? "SVR" : "CLI";
            long frame = GONetMain.Time?.FrameCount ?? 0;
            double elapsed = GONetMain.Time?.ElapsedSeconds ?? 0;

            GONetLog.Debug($"DATA_IN|{role}|{frame}|{elapsed:F4}|POS|{gonetId}|{position.x:F4}|{position.y:F4}|{position.z:F4}|{ticksAtSend}|{(isAnchor ? 1 : 0)}|{(isPhysicsObject ? 1 : 0)}", PROFILE_NAME);
        }

        /// <summary>
        /// Log rotation data received from network (written to ring buffer).
        /// Format: DATA_IN|role|frame|elapsedSec|ROT|gonetId|x|y|z|w|ticksAtSend|isAnchor|isPhysics
        /// </summary>
        [System.Diagnostics.Conditional("LOG_BLEND_DIAG")]
        public static void LogRotationReceived(uint gonetId, Quaternion rotation, long ticksAtSend, bool isAnchor, bool isPhysicsObject)
        {
            if (!s_IsInitialized) return;

            string role = GONetMain.IsServer ? "SVR" : "CLI";
            long frame = GONetMain.Time?.FrameCount ?? 0;
            double elapsed = GONetMain.Time?.ElapsedSeconds ?? 0;

            GONetLog.Debug($"DATA_IN|{role}|{frame}|{elapsed:F4}|ROT|{gonetId}|{rotation.x:F4}|{rotation.y:F4}|{rotation.z:F4}|{rotation.w:F4}|{ticksAtSend}|{(isAnchor ? 1 : 0)}|{(isPhysicsObject ? 1 : 0)}", PROFILE_NAME);
        }

        /// <summary>
        /// Log blended position being applied to transform.
        /// Format: DATA_OUT|role|frame|elapsedSec|POS|gonetId|x|y|z|dtTarget|isExtrap
        /// </summary>
        [System.Diagnostics.Conditional("LOG_BLEND_DIAG")]
        public static void LogPositionApplied(uint gonetId, Vector3 position, float dtTarget, bool isExtrapolating)
        {
            if (!s_IsInitialized) return;

            string role = GONetMain.IsServer ? "SVR" : "CLI";
            long frame = GONetMain.Time?.FrameCount ?? 0;
            double elapsed = GONetMain.Time?.ElapsedSeconds ?? 0;

            GONetLog.Debug($"DATA_OUT|{role}|{frame}|{elapsed:F4}|POS|{gonetId}|{position.x:F4}|{position.y:F4}|{position.z:F4}|{dtTarget:F4}|{(isExtrapolating ? 1 : 0)}", PROFILE_NAME);
        }

        /// <summary>
        /// Log blended rotation being applied to transform.
        /// Format: DATA_OUT|role|frame|elapsedSec|ROT|gonetId|x|y|z|w|dtTarget|isExtrap
        /// </summary>
        [System.Diagnostics.Conditional("LOG_BLEND_DIAG")]
        public static void LogRotationApplied(uint gonetId, Quaternion rotation, float dtTarget, bool isExtrapolating)
        {
            if (!s_IsInitialized) return;

            string role = GONetMain.IsServer ? "SVR" : "CLI";
            long frame = GONetMain.Time?.FrameCount ?? 0;
            double elapsed = GONetMain.Time?.ElapsedSeconds ?? 0;

            GONetLog.Debug($"DATA_OUT|{role}|{frame}|{elapsed:F4}|ROT|{gonetId}|{rotation.x:F4}|{rotation.y:F4}|{rotation.z:F4}|{rotation.w:F4}|{dtTarget:F4}|{(isExtrapolating ? 1 : 0)}", PROFILE_NAME);
        }

        /// <summary>
        /// Log SoA registration event.
        /// Format: SOA_REG|role|frame|elapsedSec|gonetId|streamType|streamIdx|objIdx|hz|objectName
        /// </summary>
        [System.Diagnostics.Conditional("LOG_BLEND_DIAG")]
        public static void LogSoARegistration(uint gonetId, string streamType, int streamIdx, int objIdx, int hz, string objectName)
        {
            if (!s_IsInitialized) return;

            string role = GONetMain.IsServer ? "SVR" : "CLI";
            long frame = GONetMain.Time?.FrameCount ?? 0;
            double elapsed = GONetMain.Time?.ElapsedSeconds ?? 0;

            GONetLog.Debug($"SOA_REG|{role}|{frame}|{elapsed:F4}|{gonetId}|{streamType}|{streamIdx}|{objIdx}|{hz}|{objectName}", PROFILE_NAME);
        }

        /// <summary>
        /// Log SoA unregistration event.
        /// Format: SOA_UNREG|role|frame|elapsedSec|gonetId|streamType|streamIdx|objIdx|objectName
        /// </summary>
        [System.Diagnostics.Conditional("LOG_BLEND_DIAG")]
        public static void LogSoAUnregistration(uint gonetId, string streamType, int streamIdx, int objIdx, string objectName)
        {
            if (!s_IsInitialized) return;

            string role = GONetMain.IsServer ? "SVR" : "CLI";
            long frame = GONetMain.Time?.FrameCount ?? 0;
            double elapsed = GONetMain.Time?.ElapsedSeconds ?? 0;

            GONetLog.Debug($"SOA_UNREG|{role}|{frame}|{elapsed:F4}|{gonetId}|{streamType}|{streamIdx}|{objIdx}|{objectName}", PROFILE_NAME);
        }

        /// <summary>
        /// Log blocked duplicate SoA registration attempt.
        /// Format: SOA_DUP_BLOCKED|role|frame|elapsedSec|gonetId|objectName
        /// </summary>
        [System.Diagnostics.Conditional("LOG_BLEND_DIAG")]
        public static void LogDuplicateRegistrationBlocked(uint gonetId, string objectName)
        {
            if (!s_IsInitialized) return;

            string role = GONetMain.IsServer ? "SVR" : "CLI";
            long frame = GONetMain.Time?.FrameCount ?? 0;
            double elapsed = GONetMain.Time?.ElapsedSeconds ?? 0;

            GONetLog.Debug($"SOA_DUP_BLOCKED|{role}|{frame}|{elapsed:F4}|{gonetId}|{objectName}", PROFILE_NAME);
        }
    }
}
