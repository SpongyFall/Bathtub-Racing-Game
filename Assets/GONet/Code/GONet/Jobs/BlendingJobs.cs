/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 */

using GONet.Core;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace GONet.Jobs
{
    public struct BlendPositionsJob : IJobParallelFor
    {
        private const int RING_SIZE = 8;
        private const float SMOOTHING_FACTOR = 0.15f;

        [ReadOnly] public NativeArray<float> posX;
        [ReadOnly] public NativeArray<float> posY;
        [ReadOnly] public NativeArray<float> posZ;
        [ReadOnly] public NativeArray<long> posTicks;
        [ReadOnly] public NativeArray<byte> historyCount;
        [ReadOnly] public NativeArray<bool> isActive;
        [ReadOnly] public NativeArray<byte> blendStrategy;
        [ReadOnly] public long targetElapsedTicks;
        [ReadOnly] public double ticksToSeconds;
        [WriteOnly] public NativeArray<Vector3> shadowPos;

        public void Execute(int i)
        {
            if (!isActive[i]) { shadowPos[i] = Vector3.zero; return; }
            int count = historyCount[i];
            if (count < 1) { shadowPos[i] = Vector3.zero; return; }
            int baseIdx = i * RING_SIZE;
            if (count == 1) { shadowPos[i] = new Vector3(posX[baseIdx], posY[baseIdx], posZ[baseIdx]); return; }

            // Sort samples by timestamp to find the pair that BRACKETS the target time.
            // We need to find samples A and B where A.ticks <= targetTicks <= B.ticks
            // This ensures we interpolate between the correct pair, not just the two most recent.

            // Collect all valid samples with their timestamps
            int validCount = 0;
            int idx0 = -1, idx1 = -1, idx2 = -1, idx3 = -1, idx4 = -1, idx5 = -1, idx6 = -1, idx7 = -1;
            long t0 = 0, t1 = 0, t2 = 0, t3 = 0, t4 = 0, t5 = 0, t6 = 0, t7 = 0;

            for (int slot = 0; slot < RING_SIZE && slot < count; slot++)
            {
                int idx = baseIdx + slot;
                long ticks = posTicks[idx];
                if (ticks <= 0) continue;

                // Insert sorted (ascending by ticks)
                if (validCount == 0) { idx0 = idx; t0 = ticks; }
                else if (ticks < t0) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx4; t5 = t4; idx4 = idx3; t4 = t3; idx3 = idx2; t3 = t2; idx2 = idx1; t2 = t1; idx1 = idx0; t1 = t0; idx0 = idx; t0 = ticks; }
                else if (validCount == 1 || ticks < t1) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx4; t5 = t4; idx4 = idx3; t4 = t3; idx3 = idx2; t3 = t2; idx2 = idx1; t2 = t1; idx1 = idx; t1 = ticks; }
                else if (validCount == 2 || ticks < t2) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx4; t5 = t4; idx4 = idx3; t4 = t3; idx3 = idx2; t3 = t2; idx2 = idx; t2 = ticks; }
                else if (validCount == 3 || ticks < t3) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx4; t5 = t4; idx4 = idx3; t4 = t3; idx3 = idx; t3 = ticks; }
                else if (validCount == 4 || ticks < t4) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx4; t5 = t4; idx4 = idx; t4 = ticks; }
                else if (validCount == 5 || ticks < t5) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx; t5 = ticks; }
                else if (validCount == 6 || ticks < t6) { idx7 = idx6; t7 = t6; idx6 = idx; t6 = ticks; }
                else { idx7 = idx; t7 = ticks; }
                validCount++;
            }

            if (validCount < 2)
            {
                if (idx0 < 0)
                {
                    // No valid samples (all had ticks <= 0) - return zero
                    shadowPos[i] = Vector3.zero;
                    return;
                }
                shadowPos[i] = new Vector3(posX[idx0], posY[idx0], posZ[idx0]);
                return;
            }

            // Find the pair that brackets targetElapsedTicks
            // Samples are sorted ascending: t0 <= t1 <= t2 <= ... <= t(validCount-1)
            int lowerIdx = idx0, upperIdx = idx1;
            long lowerTicks = t0, upperTicks = t1;

            // Walk through sorted samples to find bracket
            if (validCount >= 2 && targetElapsedTicks >= t0 && targetElapsedTicks <= t1) { lowerIdx = idx0; upperIdx = idx1; lowerTicks = t0; upperTicks = t1; }
            else if (validCount >= 3 && targetElapsedTicks >= t1 && targetElapsedTicks <= t2) { lowerIdx = idx1; upperIdx = idx2; lowerTicks = t1; upperTicks = t2; }
            else if (validCount >= 4 && targetElapsedTicks >= t2 && targetElapsedTicks <= t3) { lowerIdx = idx2; upperIdx = idx3; lowerTicks = t2; upperTicks = t3; }
            else if (validCount >= 5 && targetElapsedTicks >= t3 && targetElapsedTicks <= t4) { lowerIdx = idx3; upperIdx = idx4; lowerTicks = t3; upperTicks = t4; }
            else if (validCount >= 6 && targetElapsedTicks >= t4 && targetElapsedTicks <= t5) { lowerIdx = idx4; upperIdx = idx5; lowerTicks = t4; upperTicks = t5; }
            else if (validCount >= 7 && targetElapsedTicks >= t5 && targetElapsedTicks <= t6) { lowerIdx = idx5; upperIdx = idx6; lowerTicks = t5; upperTicks = t6; }
            else if (validCount >= 8 && targetElapsedTicks >= t6 && targetElapsedTicks <= t7) { lowerIdx = idx6; upperIdx = idx7; lowerTicks = t6; upperTicks = t7; }
            else if (targetElapsedTicks < t0)
            {
                // Target is before oldest sample - extrapolate backward (rare)
                lowerIdx = idx0; upperIdx = idx1; lowerTicks = t0; upperTicks = t1;
            }
            else
            {
                // Target is after newest sample - extrapolate forward
                // Use two most recent samples
                if (validCount == 2) { lowerIdx = idx0; upperIdx = idx1; lowerTicks = t0; upperTicks = t1; }
                else if (validCount == 3) { lowerIdx = idx1; upperIdx = idx2; lowerTicks = t1; upperTicks = t2; }
                else if (validCount == 4) { lowerIdx = idx2; upperIdx = idx3; lowerTicks = t2; upperTicks = t3; }
                else if (validCount == 5) { lowerIdx = idx3; upperIdx = idx4; lowerTicks = t3; upperTicks = t4; }
                else if (validCount == 6) { lowerIdx = idx4; upperIdx = idx5; lowerTicks = t4; upperTicks = t5; }
                else if (validCount == 7) { lowerIdx = idx5; upperIdx = idx6; lowerTicks = t5; upperTicks = t6; }
                else { lowerIdx = idx6; upperIdx = idx7; lowerTicks = t6; upperTicks = t7; }
            }

            Vector3 lowerPos = new Vector3(posX[lowerIdx], posY[lowerIdx], posZ[lowerIdx]);
            Vector3 upperPos = new Vector3(posX[upperIdx], posY[upperIdx], posZ[upperIdx]);
            float dtSamples = (float)((upperTicks - lowerTicks) * ticksToSeconds);
            float dtTarget = (float)((targetElapsedTicks - upperTicks) * ticksToSeconds);

            byte strategy = blendStrategy[i];
            switch (strategy)
            {
                case (byte)BlendStrategyType.HermiteSpline:
                    shadowPos[i] = BlendHermite(count, upperPos, lowerPos, lowerTicks, -1, 0, dtSamples, dtTarget);
                    break;
                case (byte)BlendStrategyType.SmoothedLowPass:
                    shadowPos[i] = BlendSmoothed(upperPos, lowerPos, dtSamples, dtTarget);
                    break;
                default:
                    shadowPos[i] = BlendLinear(upperPos, lowerPos, dtSamples, dtTarget);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector3 BlendLinear(Vector3 newest, Vector3 older, float dtSamples, float dtTarget)
        {
            if (dtSamples < 0.0001f) return newest;

            if (dtTarget <= 0)
            {
                // INTERPOLATION: Target time is between older and newest samples.
                // t=0 means we want the oldest sample, t=1 means we want the newest sample.
                // dtTarget is negative (how far before newest), dtSamples is the gap between samples.
                // t = (dtSamples + dtTarget) / dtSamples = 1 + (dtTarget / dtSamples)
                float t = Mathf.Clamp01(1f + (dtTarget / dtSamples));
                return Vector3.Lerp(older, newest, t);
            }
            else
            {
                // EXTRAPOLATION: Target time is after newest sample.
                // Project forward using velocity, clamped to prevent runaway.
                Vector3 velocity = (newest - older) / dtSamples;
                return newest + velocity * Mathf.Min(dtTarget, 0.2f);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector3 BlendHermite(int count, Vector3 p1, Vector3 p0, long t0, int thirdIdx, long thirdTicks, float dtSamples, float dtTarget)
        {
            if (dtSamples < 0.0001f) return p1;

            if (dtTarget <= 0)
            {
                // INTERPOLATION: Use simple lerp between samples for smoothness.
                // Hermite extrapolation not needed when we have data on both sides of target.
                float t = Mathf.Clamp01(1f + (dtTarget / dtSamples));
                return Vector3.Lerp(p0, p1, t);
            }

            // EXTRAPOLATION: Use Hermite with acceleration estimation
            Vector3 v1 = (p1 - p0) / dtSamples;
            Vector3 v0 = v1;
            if (count >= 3 && thirdTicks > 0)
            {
                Vector3 p_third = new Vector3(posX[thirdIdx], posY[thirdIdx], posZ[thirdIdx]);
                float dt_third = (float)((t0 - thirdTicks) * ticksToSeconds);
                if (dt_third > 0.0001f) v0 = (p0 - p_third) / dt_third;
            }
            Vector3 accel = (v1 - v0) / dtSamples;
            Vector3 extrapVel = v1 + accel * Mathf.Min(dtTarget, 0.1f) * 0.5f;
            return p1 + extrapVel * Mathf.Min(dtTarget, 0.15f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector3 BlendSmoothed(Vector3 newest, Vector3 older, float dtSamples, float dtTarget)
        {
            if (dtSamples < 0.0001f) return newest;

            if (dtTarget <= 0)
            {
                // INTERPOLATION: Lerp between samples with smoothing bias toward newest
                float t = Mathf.Clamp01(1f + (dtTarget / dtSamples));
                float smoothT = Mathf.Lerp(t, 1f, SMOOTHING_FACTOR); // Bias toward newest for stability
                return Vector3.Lerp(older, newest, smoothT);
            }
            else
            {
                // EXTRAPOLATION: Project forward with smoothing
                Vector3 velocity = (newest - older) / dtSamples;
                float smooth = Mathf.Clamp01(SMOOTHING_FACTOR * (1f + dtTarget * 2f));
                Vector3 extrap = newest + velocity * Mathf.Min(dtTarget, 0.2f);
                return Vector3.Lerp(newest, extrap, 1f - smooth);
            }
        }
    }

    public struct BlendRotationsJob : IJobParallelFor
    {
        private const int RING_SIZE = 8;
        private const float SMOOTHING_FACTOR = 0.15f;

        [ReadOnly] public NativeArray<float> rotX;
        [ReadOnly] public NativeArray<float> rotY;
        [ReadOnly] public NativeArray<float> rotZ;
        [ReadOnly] public NativeArray<float> rotW;
        [ReadOnly] public NativeArray<long> rotTicks;
        [ReadOnly] public NativeArray<byte> historyCount;
        [ReadOnly] public NativeArray<bool> isActive;
        [ReadOnly] public NativeArray<byte> blendStrategy;
        [ReadOnly] public long targetElapsedTicks;
        [ReadOnly] public double ticksToSeconds;
        [WriteOnly] public NativeArray<Quaternion> shadowRot;

        public void Execute(int i)
        {
            if (!isActive[i]) { shadowRot[i] = Quaternion.identity; return; }
            int count = historyCount[i];
            if (count < 1) { shadowRot[i] = Quaternion.identity; return; }
            int baseIdx = i * RING_SIZE;
            if (count == 1) { shadowRot[i] = new Quaternion(rotX[baseIdx], rotY[baseIdx], rotZ[baseIdx], rotW[baseIdx]); return; }

            // Sort samples by timestamp to find the pair that BRACKETS the target time.
            int validCount = 0;
            int idx0 = -1, idx1 = -1, idx2 = -1, idx3 = -1, idx4 = -1, idx5 = -1, idx6 = -1, idx7 = -1;
            long t0 = 0, t1 = 0, t2 = 0, t3 = 0, t4 = 0, t5 = 0, t6 = 0, t7 = 0;

            for (int slot = 0; slot < RING_SIZE && slot < count; slot++)
            {
                int idx = baseIdx + slot;
                long ticks = rotTicks[idx];
                if (ticks <= 0) continue;

                // Insert sorted (ascending by ticks)
                if (validCount == 0) { idx0 = idx; t0 = ticks; }
                else if (ticks < t0) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx4; t5 = t4; idx4 = idx3; t4 = t3; idx3 = idx2; t3 = t2; idx2 = idx1; t2 = t1; idx1 = idx0; t1 = t0; idx0 = idx; t0 = ticks; }
                else if (validCount == 1 || ticks < t1) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx4; t5 = t4; idx4 = idx3; t4 = t3; idx3 = idx2; t3 = t2; idx2 = idx1; t2 = t1; idx1 = idx; t1 = ticks; }
                else if (validCount == 2 || ticks < t2) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx4; t5 = t4; idx4 = idx3; t4 = t3; idx3 = idx2; t3 = t2; idx2 = idx; t2 = ticks; }
                else if (validCount == 3 || ticks < t3) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx4; t5 = t4; idx4 = idx3; t4 = t3; idx3 = idx; t3 = ticks; }
                else if (validCount == 4 || ticks < t4) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx4; t5 = t4; idx4 = idx; t4 = ticks; }
                else if (validCount == 5 || ticks < t5) { idx7 = idx6; t7 = t6; idx6 = idx5; t6 = t5; idx5 = idx; t5 = ticks; }
                else if (validCount == 6 || ticks < t6) { idx7 = idx6; t7 = t6; idx6 = idx; t6 = ticks; }
                else { idx7 = idx; t7 = ticks; }
                validCount++;
            }

            if (validCount < 2)
            {
                if (idx0 < 0)
                {
                    // No valid samples (all had ticks <= 0) - return identity
                    shadowRot[i] = Quaternion.identity;
                    return;
                }
                shadowRot[i] = new Quaternion(rotX[idx0], rotY[idx0], rotZ[idx0], rotW[idx0]);
                return;
            }

            // Find the pair that brackets targetElapsedTicks
            int lowerIdx = idx0, upperIdx = idx1;
            long lowerTicks = t0, upperTicks = t1;

            if (validCount >= 2 && targetElapsedTicks >= t0 && targetElapsedTicks <= t1) { lowerIdx = idx0; upperIdx = idx1; lowerTicks = t0; upperTicks = t1; }
            else if (validCount >= 3 && targetElapsedTicks >= t1 && targetElapsedTicks <= t2) { lowerIdx = idx1; upperIdx = idx2; lowerTicks = t1; upperTicks = t2; }
            else if (validCount >= 4 && targetElapsedTicks >= t2 && targetElapsedTicks <= t3) { lowerIdx = idx2; upperIdx = idx3; lowerTicks = t2; upperTicks = t3; }
            else if (validCount >= 5 && targetElapsedTicks >= t3 && targetElapsedTicks <= t4) { lowerIdx = idx3; upperIdx = idx4; lowerTicks = t3; upperTicks = t4; }
            else if (validCount >= 6 && targetElapsedTicks >= t4 && targetElapsedTicks <= t5) { lowerIdx = idx4; upperIdx = idx5; lowerTicks = t4; upperTicks = t5; }
            else if (validCount >= 7 && targetElapsedTicks >= t5 && targetElapsedTicks <= t6) { lowerIdx = idx5; upperIdx = idx6; lowerTicks = t5; upperTicks = t6; }
            else if (validCount >= 8 && targetElapsedTicks >= t6 && targetElapsedTicks <= t7) { lowerIdx = idx6; upperIdx = idx7; lowerTicks = t6; upperTicks = t7; }
            else if (targetElapsedTicks < t0)
            {
                lowerIdx = idx0; upperIdx = idx1; lowerTicks = t0; upperTicks = t1;
            }
            else
            {
                // Target after newest - use two most recent
                if (validCount == 2) { lowerIdx = idx0; upperIdx = idx1; lowerTicks = t0; upperTicks = t1; }
                else if (validCount == 3) { lowerIdx = idx1; upperIdx = idx2; lowerTicks = t1; upperTicks = t2; }
                else if (validCount == 4) { lowerIdx = idx2; upperIdx = idx3; lowerTicks = t2; upperTicks = t3; }
                else if (validCount == 5) { lowerIdx = idx3; upperIdx = idx4; lowerTicks = t3; upperTicks = t4; }
                else if (validCount == 6) { lowerIdx = idx4; upperIdx = idx5; lowerTicks = t4; upperTicks = t5; }
                else if (validCount == 7) { lowerIdx = idx5; upperIdx = idx6; lowerTicks = t5; upperTicks = t6; }
                else { lowerIdx = idx6; upperIdx = idx7; lowerTicks = t6; upperTicks = t7; }
            }

            Quaternion lowerRot = new Quaternion(rotX[lowerIdx], rotY[lowerIdx], rotZ[lowerIdx], rotW[lowerIdx]);
            Quaternion upperRot = new Quaternion(rotX[upperIdx], rotY[upperIdx], rotZ[upperIdx], rotW[upperIdx]);
            float dtSamples = (float)((upperTicks - lowerTicks) * ticksToSeconds);
            float dtTarget = (float)((targetElapsedTicks - upperTicks) * ticksToSeconds);

            byte strategy = blendStrategy[i];
            if (strategy == (byte)BlendStrategyType.SmoothedLowPass)
                shadowRot[i] = BlendRotSmoothed(upperRot, lowerRot, dtSamples, dtTarget);
            else
                shadowRot[i] = BlendRotLinear(upperRot, lowerRot, dtSamples, dtTarget);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Quaternion BlendRotLinear(Quaternion newest, Quaternion older, float dtSamples, float dtTarget)
        {
            if (dtSamples < 0.0001f) return newest;

            // CRITICAL: Ensure quaternions are on the same hemisphere before computing delta.
            // When dot(q1, q2) < 0, they represent the same rotation but q and -q are on opposite
            // hemispheres. This causes the delta rotation to go the "long way around" (nearly 360°).
            // Fix: Negate one quaternion to ensure shortest path interpolation.
            float dot = newest.x * older.x + newest.y * older.y + newest.z * older.z + newest.w * older.w;
            if (dot < 0f)
            {
                older = new Quaternion(-older.x, -older.y, -older.z, -older.w);
            }

            if (dtTarget <= 0)
            {
                // INTERPOLATION: Slerp between older and newest samples.
                // t=0 means older sample, t=1 means newest sample.
                float t = Mathf.Clamp01(1f + (dtTarget / dtSamples));
                return Quaternion.Slerp(older, newest, t);
            }
            else
            {
                // EXTRAPOLATION: Project forward using angular velocity
                Quaternion deltaRot = newest * Quaternion.Inverse(older);
                float angle; Vector3 axis;
                deltaRot.ToAngleAxis(out angle, out axis);
                if (axis.sqrMagnitude < 0.0001f) return newest;
                axis.Normalize();
                float extrapAngle = Mathf.Clamp((angle / dtSamples) * Mathf.Min(dtTarget, 0.2f), -45f, 45f);
                return Quaternion.AngleAxis(extrapAngle, axis) * newest;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Quaternion BlendRotSmoothed(Quaternion newest, Quaternion older, float dtSamples, float dtTarget)
        {
            if (dtSamples < 0.0001f) return newest;

            // Ensure same hemisphere for shortest path
            float dot = newest.x * older.x + newest.y * older.y + newest.z * older.z + newest.w * older.w;
            if (dot < 0f)
            {
                older = new Quaternion(-older.x, -older.y, -older.z, -older.w);
            }

            if (dtTarget <= 0)
            {
                // INTERPOLATION: Slerp with smoothing bias toward newest
                float t = Mathf.Clamp01(1f + (dtTarget / dtSamples));
                float smoothT = Mathf.Lerp(t, 1f, SMOOTHING_FACTOR);
                return Quaternion.Slerp(older, newest, smoothT);
            }
            else
            {
                // EXTRAPOLATION: Project forward with smoothing
                Quaternion extrap = BlendRotLinear(newest, older, dtSamples, dtTarget);
                float smooth = Mathf.Clamp01(SMOOTHING_FACTOR * (1f + dtTarget * 2f));
                return Quaternion.Slerp(extrap, newest, smooth);
            }
        }
    }

    public struct BlendScalarsJob : IJobParallelFor
    {
        private const int RING_SIZE = 8;
        [ReadOnly] public NativeArray<float> values;
        [ReadOnly] public NativeArray<long> valueTicks;
        [ReadOnly] public NativeArray<byte> historyCount;
        [ReadOnly] public NativeArray<bool> isActive;
        [ReadOnly] public NativeArray<byte> blendStrategy;
        [ReadOnly] public long targetElapsedTicks;
        [ReadOnly] public double ticksToSeconds;
        [WriteOnly] public NativeArray<float> shadowValues;

        public void Execute(int i)
        {
            if (!isActive[i]) { shadowValues[i] = 0f; return; }
            int count = historyCount[i];
            if (count < 1) { shadowValues[i] = 0f; return; }
            int baseIdx = i * RING_SIZE;
            if (count == 1) { shadowValues[i] = values[baseIdx]; return; }

            int newestIdx = baseIdx, olderIdx = baseIdx;
            long newestTicks = long.MinValue, olderTicks = long.MinValue;
            for (int slot = 0; slot < RING_SIZE && slot < count; slot++)
            {
                int idx = baseIdx + slot;
                long ticks = valueTicks[idx];
                if (ticks > newestTicks) { olderTicks = newestTicks; olderIdx = newestIdx; newestTicks = ticks; newestIdx = idx; }
                else if (ticks > olderTicks) { olderTicks = ticks; olderIdx = idx; }
            }

            float newestVal = values[newestIdx], olderVal = values[olderIdx];
            float dtSamples = (float)((newestTicks - olderTicks) * ticksToSeconds);
            if (dtSamples < 0.0001f) { shadowValues[i] = newestVal; return; }
            float dtTarget = (float)((targetElapsedTicks - newestTicks) * ticksToSeconds);

            if (dtTarget <= 0)
            {
                // INTERPOLATION: Lerp between older and newest samples
                float t = Mathf.Clamp01(1f + (dtTarget / dtSamples));
                shadowValues[i] = Mathf.Lerp(olderVal, newestVal, t);
            }
            else
            {
                // EXTRAPOLATION: Project forward using rate of change
                float rate = (newestVal - olderVal) / dtSamples;
                shadowValues[i] = newestVal + rate * Mathf.Min(dtTarget, 0.2f);
            }
        }
    }

    public struct BlendVector2Job : IJobParallelFor
    {
        private const int RING_SIZE = 8;
        private const float SMOOTHING_FACTOR = 0.15f;

        [ReadOnly] public NativeArray<float> valX;
        [ReadOnly] public NativeArray<float> valY;
        [ReadOnly] public NativeArray<long> valTicks;
        [ReadOnly] public NativeArray<byte> historyCount;
        [ReadOnly] public NativeArray<bool> isActive;
        [ReadOnly] public NativeArray<byte> blendStrategy;
        [ReadOnly] public long targetElapsedTicks;
        [ReadOnly] public double ticksToSeconds;
        [WriteOnly] public NativeArray<Vector2> shadowVal;

        public void Execute(int i)
        {
            if (!isActive[i]) { shadowVal[i] = Vector2.zero; return; }
            int count = historyCount[i];
            if (count < 1) { shadowVal[i] = Vector2.zero; return; }
            int baseIdx = i * RING_SIZE;
            if (count == 1) { shadowVal[i] = new Vector2(valX[baseIdx], valY[baseIdx]); return; }

            int newestIdx = baseIdx, olderIdx = baseIdx;
            long newestTicks = long.MinValue, olderTicks = long.MinValue;
            for (int slot = 0; slot < RING_SIZE && slot < count; slot++)
            {
                int idx = baseIdx + slot;
                long ticks = valTicks[idx];
                if (ticks > newestTicks) { olderTicks = newestTicks; olderIdx = newestIdx; newestTicks = ticks; newestIdx = idx; }
                else if (ticks > olderTicks) { olderTicks = ticks; olderIdx = idx; }
            }

            Vector2 newestVal = new Vector2(valX[newestIdx], valY[newestIdx]);
            Vector2 olderVal = new Vector2(valX[olderIdx], valY[olderIdx]);
            float dtSamples = (float)((newestTicks - olderTicks) * ticksToSeconds);
            float dtTarget = (float)((targetElapsedTicks - newestTicks) * ticksToSeconds);

            byte strategy = blendStrategy[i];
            if (strategy == (byte)BlendStrategyType.SmoothedLowPass)
                shadowVal[i] = BlendSmoothed(newestVal, olderVal, dtSamples, dtTarget);
            else
                shadowVal[i] = BlendLinear(newestVal, olderVal, dtSamples, dtTarget);
        }

        private Vector2 BlendLinear(Vector2 newest, Vector2 older, float dtSamples, float dtTarget)
        {
            if (dtSamples < 0.0001f) return newest;

            if (dtTarget <= 0)
            {
                // INTERPOLATION: Lerp between samples
                float t = Mathf.Clamp01(1f + (dtTarget / dtSamples));
                return Vector2.Lerp(older, newest, t);
            }
            else
            {
                // EXTRAPOLATION: Project forward
                Vector2 velocity = (newest - older) / dtSamples;
                return newest + velocity * Mathf.Min(dtTarget, 0.2f);
            }
        }

        private Vector2 BlendSmoothed(Vector2 newest, Vector2 older, float dtSamples, float dtTarget)
        {
            if (dtSamples < 0.0001f) return newest;

            if (dtTarget <= 0)
            {
                // INTERPOLATION: Lerp with smoothing bias
                float t = Mathf.Clamp01(1f + (dtTarget / dtSamples));
                float smoothT = Mathf.Lerp(t, 1f, SMOOTHING_FACTOR);
                return Vector2.Lerp(older, newest, smoothT);
            }
            else
            {
                // EXTRAPOLATION: Project forward with smoothing
                Vector2 velocity = (newest - older) / dtSamples;
                float smooth = Mathf.Clamp01(SMOOTHING_FACTOR * (1f + dtTarget * 2f));
                Vector2 extrap = newest + velocity * Mathf.Min(dtTarget, 0.2f);
                return Vector2.Lerp(newest, extrap, 1f - smooth);
            }
        }
    }

    public struct BlendVector4Job : IJobParallelFor
    {
        private const int RING_SIZE = 8;
        private const float SMOOTHING_FACTOR = 0.15f;

        [ReadOnly] public NativeArray<float> valX;
        [ReadOnly] public NativeArray<float> valY;
        [ReadOnly] public NativeArray<float> valZ;
        [ReadOnly] public NativeArray<float> valW;
        [ReadOnly] public NativeArray<long> valTicks;
        [ReadOnly] public NativeArray<byte> historyCount;
        [ReadOnly] public NativeArray<bool> isActive;
        [ReadOnly] public NativeArray<byte> blendStrategy;
        [ReadOnly] public long targetElapsedTicks;
        [ReadOnly] public double ticksToSeconds;
        [WriteOnly] public NativeArray<Vector4> shadowVal;

        public void Execute(int i)
        {
            if (!isActive[i]) { shadowVal[i] = Vector4.zero; return; }
            int count = historyCount[i];
            if (count < 1) { shadowVal[i] = Vector4.zero; return; }
            int baseIdx = i * RING_SIZE;
            if (count == 1) { shadowVal[i] = new Vector4(valX[baseIdx], valY[baseIdx], valZ[baseIdx], valW[baseIdx]); return; }

            int newestIdx = baseIdx, olderIdx = baseIdx;
            long newestTicks = long.MinValue, olderTicks = long.MinValue;
            for (int slot = 0; slot < RING_SIZE && slot < count; slot++)
            {
                int idx = baseIdx + slot;
                long ticks = valTicks[idx];
                if (ticks > newestTicks) { olderTicks = newestTicks; olderIdx = newestIdx; newestTicks = ticks; newestIdx = idx; }
                else if (ticks > olderTicks) { olderTicks = ticks; olderIdx = idx; }
            }

            Vector4 newestVal = new Vector4(valX[newestIdx], valY[newestIdx], valZ[newestIdx], valW[newestIdx]);
            Vector4 olderVal = new Vector4(valX[olderIdx], valY[olderIdx], valZ[olderIdx], valW[olderIdx]);
            float dtSamples = (float)((newestTicks - olderTicks) * ticksToSeconds);
            float dtTarget = (float)((targetElapsedTicks - newestTicks) * ticksToSeconds);

            byte strategy = blendStrategy[i];
            if (strategy == (byte)BlendStrategyType.SmoothedLowPass)
                shadowVal[i] = BlendSmoothed(newestVal, olderVal, dtSamples, dtTarget);
            else
                shadowVal[i] = BlendLinear(newestVal, olderVal, dtSamples, dtTarget);
        }

        private Vector4 BlendLinear(Vector4 newest, Vector4 older, float dtSamples, float dtTarget)
        {
            if (dtSamples < 0.0001f) return newest;

            if (dtTarget <= 0)
            {
                // INTERPOLATION: Lerp between samples
                float t = Mathf.Clamp01(1f + (dtTarget / dtSamples));
                return Vector4.Lerp(older, newest, t);
            }
            else
            {
                // EXTRAPOLATION: Project forward
                Vector4 velocity = (newest - older) / dtSamples;
                return newest + velocity * Mathf.Min(dtTarget, 0.2f);
            }
        }

        private Vector4 BlendSmoothed(Vector4 newest, Vector4 older, float dtSamples, float dtTarget)
        {
            if (dtSamples < 0.0001f) return newest;

            if (dtTarget <= 0)
            {
                // INTERPOLATION: Lerp with smoothing bias
                float t = Mathf.Clamp01(1f + (dtTarget / dtSamples));
                float smoothT = Mathf.Lerp(t, 1f, SMOOTHING_FACTOR);
                return Vector4.Lerp(older, newest, smoothT);
            }
            else
            {
                // EXTRAPOLATION: Project forward with smoothing
                Vector4 velocity = (newest - older) / dtSamples;
                float smooth = Mathf.Clamp01(SMOOTHING_FACTOR * (1f + dtTarget * 2f));
                Vector4 extrap = newest + velocity * Mathf.Min(dtTarget, 0.2f);
                return Vector4.Lerp(newest, extrap, 1f - smooth);
            }
        }
    }
}
