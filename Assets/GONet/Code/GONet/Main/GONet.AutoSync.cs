/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 *
 * Authorized use is explicitly limited to the following:	
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on original source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 * -The ability to modify source code for local use only
 * -The ability to distribute products based on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on modified source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using GONet.DistributedHost;
using GONet.Generation;
using GONet.Utils;
using ReliableNetcode;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

using GONetCodeGenerationId = System.Byte;
using GONetChannelId = System.Byte;
using System.IO;
using System.Runtime.Serialization;
using System.Net;
using System.Collections;
using System.Diagnostics;
using GONet.PluginAPI;
using System.Text;
using System.Runtime.InteropServices;

namespace GONet
{
    public static partial class GONetMain
    {
        internal class AutoMagicalSync_ValueMonitoringSupport_ChangedValue
        {
            internal byte index;
            internal GONetParticipant_AutoMagicalSyncCompanion_Generated syncCompanion;
            internal string memberName;

            /// <summary>
            /// For animator parameters, stores the specific parameter name for runtime isSyncd lookup.
            /// Null for non-animator sync values.
            /// </summary>
            internal string animatorParameterName;

            /// <summary>
            /// PERFORMANCE OPTIMIZATION: Cached isSyncd state for animator parameters.
            /// Eliminates per-frame string-based dictionary lookup in hot path.
            /// True = this animator parameter should be synced (don't skip).
            /// False = this animator parameter should NOT be synced (skip it).
            /// Initialized at companion creation from animatorSyncSupport dictionary.
            /// Call <see cref="GONetParticipant.RefreshAnimatorSyncCache"/> if you change
            /// animatorSyncSupport at runtime.
            /// </summary>
            internal bool isAnimatorParameterSyncd_Cached;

            /// <summary>
            /// Indicates whether <see cref="isAnimatorParameterSyncd_Cached"/> has been initialized.
            /// Used to detect first-time initialization vs uninitialized state.
            /// </summary>
            internal bool isAnimatorParameterSyncd_CacheInitialized;

            #region properties copied off of GONetAutoMagicalSyncAttribute
            /// <summary>
            /// Matches with <see cref="GONetAutoMagicalSyncAttribute.MustRunOnUnityMainThread"/>
            /// </summary>
            internal bool syncAttribute_MustRunOnUnityMainThread;
            /// <summary>
            /// Matches with <see cref="GONetAutoMagicalSyncAttribute.ProcessingPriority"/>
            /// </summary>
            internal int syncAttribute_ProcessingPriority;
            /// <summary>
            /// Matches with <see cref="GONetAutoMagicalSyncAttribute.ProcessingPriority_GONetInternalOverride"/>
            /// </summary>
            internal int syncAttribute_ProcessingPriority_GONetInternalOverride;
            /// <summary>
            /// Matches with <see cref="GONetAutoMagicalSyncAttribute.SyncChangesEverySeconds"/>
            /// </summary>
            internal float syncAttribute_SyncChangesEverySeconds;
            /// <summary>
            /// Matches with <see cref="GONetAutoMagicalSyncAttribute.Reliability"/>
            /// </summary>
            internal AutoMagicalSyncReliability syncAttribute_Reliability;
            /// <summary>
            /// Matches with <see cref="GONetAutoMagicalSyncAttribute.ShouldBlendBetweenValuesReceived"/>
            /// </summary>
            internal bool syncAttribute_ShouldBlendBetweenValuesReceived;
            /// <summary>
            /// Matches with <see cref="GONetAutoMagicalSyncAttribute.ShouldSkipSync"/>
            /// </summary>
            internal Func<GONetMain.AutoMagicalSync_ValueMonitoringSupport_ChangedValue, int, bool> syncAttribute_ShouldSkipSync;
            /// <summary>
            /// Matches/corresponds with/to each of the following members:
            ///     <see cref="GONetAutoMagicalSyncAttribute.QuantizeDownToBitCount"/>
            ///     <see cref="GONetAutoMagicalSyncAttribute.QuantizeLowerBound"/>
            ///     <see cref="GONetAutoMagicalSyncAttribute.QuantizeUpperBound"/>
            /// </summary>
            internal QuantizerSettingsGroup syncAttribute_QuantizerSettingsGroup;
            /// <summary>
            /// Matches with <see cref="GONetAutoMagicalSyncSettings_ProfileTemplate.PhysicsUpdateInterval"/>
            /// Physics sync frequency: 1=every FixedUpdate, 2=every 2nd, 3=every 3rd, 4=every 4th.
            /// Only used for physics sync (Rigidbody position/rotation when IsRigidBodyOwnerOnlyControlled=true).
            /// </summary>
            internal int syncAttribute_PhysicsUpdateInterval;
            /// <summary>
            /// Matches with <see cref="GONetAutoMagicalSyncSettings_ProfileTemplate.EnablePhysicsSnapping"/>
            /// Experimental: Enable physics-based precision snapping for at-rest objects.
            /// Default: false (Stage 2 smart at-rest value selection is preferred).
            /// </summary>
            internal bool syncAttribute_EnablePhysicsSnapping;
            /// <summary>
            /// Type of this syncable value (Vector3, Quaternion, float, etc.).
            /// Used for type-specific velocity calculations and range checking.
            /// </summary>
            internal GONetSyncableValueTypes codeGenerationMemberType;

            /// <summary>
            /// similar to keeping track of initial value, but it could change over time due to some new rules to support increased quantization/compression
            /// </summary>
            internal GONetSyncableValue baselineValue_current;

            internal GONetSyncableValue lastKnownValue;
            internal GONetSyncableValue lastKnownValue_previous;

            /// <summary>
            /// Timestamp (in ticks) when lastKnownValue was captured (CURRENT value).
            /// Copied to lastKnownValue_previous_elapsedTicks on next sync.
            /// </summary>
            internal long lastKnownValue_elapsedTicks;

            /// <summary>
            /// Timestamp (in ticks) when lastKnownValue_previous was captured (PREVIOUS sync).
            /// Used for correct velocity calculation: velocity = (current - previous) / (currentTime - previousTime).
            /// Updated by copying lastKnownValue_elapsedTicks before it's overwritten.
            /// CRITICAL: Velocity MUST be calculated over SYNC interval, not physics frame interval!
            /// </summary>
            internal long lastKnownValue_previous_elapsedTicks;

            /// <summary>
            /// Throughout a session, this will represent the minimum value of <see cref="lastKnownValue"/> encountered since the start of the session.
            /// IMPORTANT: This is only updated ***on the owner's machine*** if the following precompiler definition exists: GONET_MEASURE_VALUES_MIN_MAX (see <see cref="GONetSyncableValue.UpdateMinimumEncountered_IfApppropriate"/>)
            /// </summary>
            internal GONetSyncableValue valueLimitEncountered_min;
            /// <summary>
            /// Throughout a session, this will represent the maximum value of <see cref="lastKnownValue"/> encountered since the start of the session.
            /// IMPORTANT: This is only updated ***on the owner's machine*** if the following precompiler definition exists: GONET_MEASURE_VALUES_MIN_MAX (see <see cref="GONetSyncableValue.UpdateMinimumEncountered_IfApppropriate"/>)
            /// </summary>
            internal GONetSyncableValue valueLimitEncountered_max;

            internal const int MOST_RECENT_CHANGEs_SIZE_MINIMUM = 10;
            internal const int MOST_RECENT_CHANGEs_SIZE_MAX_EXPECTED = 100;
            internal static readonly ArrayPool<NumericValueChangeSnapshot> mostRecentChangesPool = new ArrayPool<NumericValueChangeSnapshot>(1000, 50, MOST_RECENT_CHANGEs_SIZE_MINIMUM, MOST_RECENT_CHANGEs_SIZE_MAX_EXPECTED);
            internal static readonly long AUTO_STOP_PROCESSING_BLENDING_IF_INACTIVE_FOR_TICKS = TimeSpan.FromSeconds(1).Ticks;
            internal static readonly long AT_REST_CLEAR_THRESHOLD_TICKS = TimeSpan.FromSeconds(1).Ticks;

            /// <summary>
            /// This will be null when <see cref="syncAttribute_ShouldBlendBetweenValuesReceived"/> is false AND/OR if the value type is NOT numeric (although, the latter will be identified early on in either generation or runtime and cause an exception to essentially disallow that!).
            /// IMPORTANT: This is always sorted in most recent with lowest index to oldest with highest index order.
            /// </summary>
            internal NumericValueChangeSnapshot[] mostRecentChanges;
            internal int mostRecentChanges_capacitySize;
            internal int mostRecentChanges_usedSize = 0;
            private ushort mostRecentChanges_UpdatedByAuthorityId;

            /// <summary>
            /// If true, a message from owner came in indicating this is at rest, but is awaiting processing of that while the
            /// value blending buffer lead time transpires first.
            /// Also, if true, <see cref="hasAwaitingAtRest_assumedInitialRestElapsedTicks"/> will indicate when the source indicating as much (i.e., elapsedTicksAtSend).
            /// </summary>
            internal bool hasAwaitingAtRest;
            internal long hasAwaitingAtRest_assumedInitialRestElapsedTicks;
            internal long hasAwaitingAtRest_sinceLeadTimeAdjustedElapsedTicks;
            internal long hasAwaitingAtRest_lastProcessedAtRestTicks;
            internal GONetSyncableValue hasAwaitingAtRest_value;

            /// <summary>
            /// NEW: Physics snapping flag for at-rest handling.
            /// If true, when the at-rest value is applied, trigger physics snapping on the GONetParticipant
            /// to eliminate quantization error (position: ~0.95mm → sub-mm, rotation: ~0.3° → sub-0.01°).
            /// Only applies to physics objects (IsRigidBodyOwnerOnlyControlled=true) on non-authority clients.
            /// </summary>
            internal bool hasAwaitingAtRest_needsPhysicsSnap;

            /// <summary>
            /// STALE VALUE TRACKING (Jan 2026): Prevents fighting with animation curves.
            /// When blending fails due to stale data, we apply the last known value ONCE, then stop.
            /// This flag tracks whether we've already applied the stale value.
            /// Reset when new data arrives (in AddToMostRecentChangeQueue_IfAppropriate).
            /// </summary>
            internal bool hasAppliedStaleValue;
            /// <summary>
            /// Timestamp of the newest buffer entry when we applied the stale value.
            /// Used to detect when new data arrives (buffer timestamp changes) to reset hasAppliedStaleValue.
            /// </summary>
            internal long staleValue_appliedAtBufferNewestTicks;

            #region VELOCITY-AUGMENTED SYNC: Velocity tracking and expiration
            /// <summary>
            /// True if this value is eligible for velocity-augmented sync (Vector3, Quaternion, etc.).
            /// Set during initialization based on sync attribute configuration.
            /// </summary>
            internal bool isVelocityEligible;

            /// <summary>
            /// Velocity quantization lower bound from sync attribute (in value-units/second).
            /// User-configured value for ease of use.
            /// </summary>
            internal float syncAttribute_VelocityQuantizeLowerBound;

            /// <summary>
            /// Velocity quantization upper bound from sync attribute (in value-units/second).
            /// User-configured value for ease of use.
            /// </summary>
            internal float syncAttribute_VelocityQuantizeUpperBound;

            /// <summary>
            /// Velocity anchor interval from sync attribute (in seconds).
            /// Interval between mandatory VALUE anchor bundles during VELOCITY-augmented sync.
            /// 0 = use global default from GONetGlobal.velocityAnchorIntervalSeconds
            /// >0 = custom interval for this specific sync value
            /// </summary>
            internal float syncAttribute_VelocityAnchorIntervalSeconds;

            /// <summary>
            /// PRE-CALCULATED lower bound for velocity in value-units-per-sync-interval.
            /// = syncAttribute_VelocityQuantizeLowerBound * deltaTime
            /// Used for efficient runtime range checking (no division required).
            /// </summary>
            internal float velocityQuantizeLowerBound_PerSyncInterval;

            /// <summary>
            /// PRE-CALCULATED upper bound for velocity in value-units-per-sync-interval.
            /// = syncAttribute_VelocityQuantizeUpperBound * deltaTime
            /// Used for efficient runtime range checking (no division required).
            /// </summary>
            internal float velocityQuantizeUpperBound_PerSyncInterval;

            /// <summary>
            /// Last received velocity value from VELOCITY bundle.
            /// Used for synthesis when VALUE bundles arrive.
            /// </summary>
            internal GONetSyncableValue lastReceivedVelocity;

            /// <summary>
            /// Timestamp when last velocity was received (in ticks).
            /// Used for time-based expiration.
            /// </summary>
            internal long lastVelocityTimestamp;

            /// <summary>
            /// Velocity expiration duration in milliseconds.
            /// After this duration without VELOCITY bundle, stop synthesizing and use VALUE directly.
            /// Increased from 100ms → 200ms to tolerate:
            /// - Server snapshot accumulation delay (~80-120ms for 2+ snapshots at 24Hz)
            /// - Network jitter and packet loss
            /// - Late-joiner synchronization delays
            /// Default: 200ms (~12 frames at 60fps)
            /// </summary>
            internal const long VELOCITY_VALID_DURATION_MS = 200;

            /// <summary>
            /// Timestamp (in ticks) when last VALUE anchor bundle was sent for this value.
            /// Used to enforce periodic anchoring during VELOCITY-augmented sync.
            /// Prevents drift accumulation from packet loss on unreliable channels.
            /// Resets to current time when:
            /// - VALUE bundle sent (forced anchor or velocity out of range)
            /// - First sync after object spawn
            /// </summary>
            internal long lastAnchorTimeTicks;
            #endregion

            #region ADAPTIVE BUFFER LEAD TIME: Per-value buffer adaptation for variable update rates
            /// <summary>
            /// Per-value adaptive buffer state for variable update rate scenarios.
            /// Tracks inter-packet interval via EWMA and adapts buffer lead time accordingly.
            ///
            /// PROBLEM SOLVED:
            /// When backpressure trickle mode slows updates to 2Hz (500ms intervals), a fixed 100ms buffer
            /// causes stutter: client plays through buffer in 0.1s, then waits 0.4s in silence.
            ///
            /// SOLUTION:
            /// - Measure actual inter-packet interval
            /// - Target buffer = interval * 1.5 (margin for jitter)
            /// - Asymmetric adaptation: fast expand when slowing, slow shrink when improving
            /// </summary>
            internal Utils.AdaptiveBufferState adaptiveBufferState;
            #endregion

            /// <summary>
            /// DO NOT USE THIS.
            /// Public default constructor is required for object pool instantiation under current impl of <see cref="ObjectPool{T}"/>;
            /// </summary>
            public AutoMagicalSync_ValueMonitoringSupport_ChangedValue() { }

            /// <summary>
            /// This is called in generated code (i.e., sub-classes of <see cref="GONetParticipant_AutoMagicalSyncCompanion_Generated"/>) for any
            /// member decorated with <see cref="GONetAutoMagicalSyncAttribute.ShouldBlendBetweenValuesReceived"/> set to true.
            /// </summary>
            internal void AddToMostRecentChangeQueue_IfAppropriate(long elapsedTicksAtChange, GONetSyncableValue value)
            {
                // Check if this is arriving after an at-rest was set but not yet applied
                if (hasAwaitingAtRest && elapsedTicksAtChange < hasAwaitingAtRest_assumedInitialRestElapsedTicks)
                {
                    // Late arrival logging - only when ValueBlendUtils.ShouldLog is enabled
                    // if (ValueBlendUtils.ShouldLog) GONetLog.Debug($"[LATE-ARRIVAL-REJECTED-AWAITING] index:{index} valueTime:{TimeSpan.FromTicks(elapsedTicksAtChange).TotalSeconds}s value:{value} atRestTime:{TimeSpan.FromTicks(hasAwaitingAtRest_assumedInitialRestElapsedTicks).TotalSeconds}s");
                    return; // Reject updates from before the pending at-rest time
                }

                // Check if this is arriving after an at-rest was already applied
                if (hasAwaitingAtRest_lastProcessedAtRestTicks > 0 && elapsedTicksAtChange < hasAwaitingAtRest_lastProcessedAtRestTicks)
                {
                    // Late arrival logging - only when ValueBlendUtils.ShouldLog is enabled
                    // if (ValueBlendUtils.ShouldLog) GONetLog.Debug($"[LATE-ARRIVAL-REJECTED-PROCESSED] index:{index} valueTime:{TimeSpan.FromTicks(elapsedTicksAtChange).TotalSeconds}s value:{value} lastAtRestTime:{TimeSpan.FromTicks(hasAwaitingAtRest_lastProcessedAtRestTicks).TotalSeconds}s");
                    return; // Reject updates from before the last processed at-rest time
                }

                // Log what's being added (only if logging is enabled)
                if (syncAttribute_ShouldBlendBetweenValuesReceived && ValueBlendUtils.ShouldLog)
                {
                    GONetLog.Debug($"[BUFFER-ADD] index:{index} " +
                        $"time:{TimeSpan.FromTicks(elapsedTicksAtChange).TotalSeconds}s " +
                        $"value:{value} " +
                        $"bufferSize:{mostRecentChanges_usedSize} " +
                        $"hasAwaitingAtRest:{hasAwaitingAtRest}");
                }

                // Track arrival for adaptive buffer lead time calculation
                // This measures actual inter-packet interval to adapt buffer size for variable update rates
                if (GONetGlobal.Instance?.enableAdaptiveBlendingBuffer == true)
                {
                    adaptiveBufferState.RecordArrival(elapsedTicksAtChange);
                }

                // Check for duplicate timestamps
                for (int i = 0; i < mostRecentChanges_usedSize; ++i)
                {
                    var item = mostRecentChanges[i];
                    if (item.elapsedTicksAtChange == elapsedTicksAtChange)
                    {
                        return; // avoid adding items with same timestamp as it will mess up value blending
                    }

                    if (item.elapsedTicksAtChange < elapsedTicksAtChange)
                    {
                        // insert new guy, who is more recent than current, here at i; but first, move all the ones down a notch as they are all older than the new guy:
                        for (int j = mostRecentChanges_usedSize; j >= i; --j)
                        {
                            if (j < (mostRecentChanges_capacitySize - 1))
                            {
                                mostRecentChanges[j + 1] = mostRecentChanges[j];
                            }
                        }

                        bool isPreviousValuePresent = (i + 1) < mostRecentChanges_usedSize;
                        if (isPreviousValuePresent)
                        {
                            AdjustValueOnExpectedUpcomingNewBaseline_IfAppropriate(ref value, mostRecentChanges[i + 1].numericValue);
                        }

                        mostRecentChanges[i] = NumericValueChangeSnapshot.Create(elapsedTicksAtChange, value);
                        if (mostRecentChanges_usedSize < mostRecentChanges_capacitySize)
                        {
                            ++mostRecentChanges_usedSize;
                        }

                        // New data arrived - reset stale value tracking so blending can resume
                        hasAppliedStaleValue = false;

                        // Consider clearing lastProcessedAtRestTicks if we're clearly moving again
                        // Using pre-calculated constant instead of TimeSpan.FromSeconds(1).Ticks
                        if (hasAwaitingAtRest_lastProcessedAtRestTicks > 0 &&
                            (elapsedTicksAtChange - hasAwaitingAtRest_lastProcessedAtRestTicks) > AT_REST_CLEAR_THRESHOLD_TICKS)
                        {
                            // At-rest cleared logging - only when ValueBlendUtils.ShouldLog is enabled
                            // if (ValueBlendUtils.ShouldLog) GONetLog.Debug($"[AT-REST-CLEARED] index:{index} - object moving again, clearing at-rest protection");
                            hasAwaitingAtRest_lastProcessedAtRestTicks = 0;
                        }

                        return;
                    }
                }

                if (mostRecentChanges_usedSize < mostRecentChanges_capacitySize)
                {
                    mostRecentChanges[mostRecentChanges_usedSize] = NumericValueChangeSnapshot.Create(elapsedTicksAtChange, value);
                    ++mostRecentChanges_usedSize;

                    // New data arrived - reset stale value tracking so blending can resume
                    hasAppliedStaleValue = false;

                    // Consider clearing lastProcessedAtRestTicks here too
                    if (hasAwaitingAtRest_lastProcessedAtRestTicks > 0 &&
                        (elapsedTicksAtChange - hasAwaitingAtRest_lastProcessedAtRestTicks) > AT_REST_CLEAR_THRESHOLD_TICKS)
                    {
                        // At-rest cleared logging - only when ValueBlendUtils.ShouldLog is enabled
                        // if (ValueBlendUtils.ShouldLog) GONetLog.Debug($"[AT-REST-CLEARED] index:{index} - object moving again, clearing at-rest protection");
                        hasAwaitingAtRest_lastProcessedAtRestTicks = 0;
                    }
                }
            }

            /// <summary>
            /// Adds synthesized value from velocity packet to the queue, storing velocity metadata for velocity-aware blending.
            /// </summary>
            internal void AddToMostRecentChangeQueue_IfAppropriate_WithVelocity(long elapsedTicksAtChange, GONetSyncableValue synthesizedValue, GONetSyncableValue velocityValue)
            {
                // Reject late arrivals (same logic as base method)
                if (hasAwaitingAtRest && elapsedTicksAtChange < hasAwaitingAtRest_assumedInitialRestElapsedTicks)
                {
                    // Late arrival logging - only when ValueBlendUtils.ShouldLog is enabled
                    // if (ValueBlendUtils.ShouldLog) GONetLog.Debug($"[LATE-ARRIVAL-REJECTED-AWAITING-VEL] index:{index} valueTime:{TimeSpan.FromTicks(elapsedTicksAtChange).TotalSeconds}s atRestTime:{TimeSpan.FromTicks(hasAwaitingAtRest_assumedInitialRestElapsedTicks).TotalSeconds}s");
                    return;
                }

                if (hasAwaitingAtRest_lastProcessedAtRestTicks > 0 && elapsedTicksAtChange < hasAwaitingAtRest_lastProcessedAtRestTicks)
                {
                    // Late arrival logging - only when ValueBlendUtils.ShouldLog is enabled
                    // if (ValueBlendUtils.ShouldLog) GONetLog.Debug($"[LATE-ARRIVAL-REJECTED-PROCESSED-VEL] index:{index} valueTime:{TimeSpan.FromTicks(elapsedTicksAtChange).TotalSeconds}s lastAtRestTime:{TimeSpan.FromTicks(hasAwaitingAtRest_lastProcessedAtRestTicks).TotalSeconds}s");
                    return;
                }

                // Check for duplicate timestamps
                for (int i = 0; i < mostRecentChanges_usedSize; ++i)
                {
                    var item = mostRecentChanges[i];
                    if (item.elapsedTicksAtChange == elapsedTicksAtChange)
                    {
                        return; // Avoid adding items with same timestamp
                    }

                    if (item.elapsedTicksAtChange < elapsedTicksAtChange)
                    {
                        // Insert new snapshot (more recent than current) at position i
                        for (int j = mostRecentChanges_usedSize; j >= i; --j)
                        {
                            if (j < (mostRecentChanges_capacitySize - 1))
                            {
                                mostRecentChanges[j + 1] = mostRecentChanges[j];
                            }
                        }

                        bool isPreviousValuePresent = (i + 1) < mostRecentChanges_usedSize;
                        if (isPreviousValuePresent)
                        {
                            AdjustValueOnExpectedUpcomingNewBaseline_IfAppropriate(ref synthesizedValue, mostRecentChanges[i + 1].numericValue);
                        }

                        // CRITICAL: Use CreateFromVelocityPacket to store velocity metadata
                        mostRecentChanges[i] = NumericValueChangeSnapshot.CreateFromVelocityPacket(elapsedTicksAtChange, synthesizedValue, velocityValue);
                        if (mostRecentChanges_usedSize < mostRecentChanges_capacitySize)
                        {
                            ++mostRecentChanges_usedSize;
                        }

                        if (hasAwaitingAtRest_lastProcessedAtRestTicks > 0 &&
                            (elapsedTicksAtChange - hasAwaitingAtRest_lastProcessedAtRestTicks) > AT_REST_CLEAR_THRESHOLD_TICKS)
                        {
                            // At-rest cleared logging - only when ValueBlendUtils.ShouldLog is enabled
                            // if (ValueBlendUtils.ShouldLog) GONetLog.Debug($"[AT-REST-CLEARED-VEL] index:{index} - object moving again");
                            hasAwaitingAtRest_lastProcessedAtRestTicks = 0;
                        }

                        return;
                    }
                }

                // Add at the end if we made it here
                if (mostRecentChanges_usedSize < mostRecentChanges_capacitySize)
                {
                    mostRecentChanges[mostRecentChanges_usedSize] = NumericValueChangeSnapshot.CreateFromVelocityPacket(elapsedTicksAtChange, synthesizedValue, velocityValue);
                    ++mostRecentChanges_usedSize;

                    if (hasAwaitingAtRest_lastProcessedAtRestTicks > 0 &&
                        (elapsedTicksAtChange - hasAwaitingAtRest_lastProcessedAtRestTicks) > AT_REST_CLEAR_THRESHOLD_TICKS)
                    {
                        // At-rest cleared logging - only when ValueBlendUtils.ShouldLog is enabled
                        // if (ValueBlendUtils.ShouldLog) GONetLog.Debug($"[AT-REST-CLEARED-VEL] index:{index} - object moving again");
                        hasAwaitingAtRest_lastProcessedAtRestTicks = 0;
                    }
                }
            }

            private void AdjustValueOnExpectedUpcomingNewBaseline_IfAppropriate(ref GONetSyncableValue valueNew, GONetSyncableValue valuePrevious)
            {
                switch (valueNew.GONetSyncType)
                {
                    case GONetSyncableValueTypes.UnityEngine_Vector3: // see IsLastKnownValue_VeryCloseTo_Or_AlreadyOutsideOf_QuantizationRange to consolidate impls like below since they are very similar?
                        UnityEngine.Vector3 diff = valueNew.UnityEngine_Vector3 - valuePrevious.UnityEngine_Vector3;
                        System.Single componentLimitLower = syncAttribute_QuantizerSettingsGroup.lowerBound;// * 0.8f; // TODO cache this value
                        System.Single componentLimitUpper = syncAttribute_QuantizerSettingsGroup.upperBound;// * 0.8f; // TODO cache this value
                        
                        bool isLikelyBeingProcessedPriorToExpectedUpcomingNewBaseline =
                            diff.x < componentLimitLower || diff.x > componentLimitUpper ||
                            diff.y < componentLimitLower || diff.y > componentLimitUpper ||
                            diff.z < componentLimitLower || diff.z > componentLimitUpper;

                        if (isLikelyBeingProcessedPriorToExpectedUpcomingNewBaseline)
                        {
                            //GONetLog.Debug("the new value being placed in buffer is happening prior to applying the new baseline!");

                            Vector3 replacementValue = valueNew.UnityEngine_Vector3;

                            if (diff.x < componentLimitLower) replacementValue.x += componentLimitLower;
                            if (diff.x > componentLimitUpper) replacementValue.x -= componentLimitUpper;
                            if (diff.y < componentLimitLower) replacementValue.y += componentLimitLower;
                            if (diff.y > componentLimitUpper) replacementValue.y -= componentLimitUpper;
                            if (diff.z < componentLimitLower) replacementValue.z += componentLimitLower;
                            if (diff.z > componentLimitUpper) replacementValue.z -= componentLimitUpper;

                            valueNew = replacementValue;
                        }
                        break;
                }
            }

            internal bool TryGetMostRecentChangeAtTime(long elapsedTicksAtChange, out GONetSyncableValue value)
            {
                for (int i = 0; i < mostRecentChanges_usedSize; ++i)
                {
                    var item = mostRecentChanges[i];
                    if (item.elapsedTicksAtChange == elapsedTicksAtChange)
                    {
                        value = item.numericValue;
                        return true;
                    }
                }

                value = default;
                return false;
            }

            long lastLogBufferContentsTicks;

            private void LogBufferContentsIfAppropriate(float onlyEverySeconds = 0.01f, bool isFullRequired = false)
            {
                if ((!isFullRequired || mostRecentChanges_usedSize == mostRecentChanges_capacitySize) && 
                    (TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastLogBufferContentsTicks).TotalSeconds > onlyEverySeconds))
                {
                    lastLogBufferContentsTicks = DateTime.UtcNow.Ticks;
                    GONetLog.Debug("==============================================================================================");
                    for (int k = 0; k < mostRecentChanges_usedSize; ++k)
                    {
                        GONetLog.Debug(string.Concat("item: ", k, " value: ", mostRecentChanges[k].numericValue, " changed @ time (seconds): ", TimeSpan.FromTicks(mostRecentChanges[k].elapsedTicksAtChange).TotalSeconds));
                    }
                }
            }

            /// <summary>
            /// <para>Expected that this is called each frame.</para>
            /// <para>IMPORTANT: This method will do nothing (i.e., not appripriate) if <see cref="syncCompanion"/>'s <see cref="GONetParticipant_AutoMagicalSyncCompanion_Generated.gonetParticipant"/> is mine (<see cref="GONetMain.IsMine(GONetParticipant)"/>) - do not value blend on something I own...value blending is only something that makes sense for GNPs that others own</para>
            /// <para>Loop through the recent changes to interpolate or extrapolate if possible.</para>
            /// <para>POST: The related/associated value is updated to what is believed to be the current value based on recent changes accumulated from owner/source.</para>
            /// </summary>
            internal void ApplyValueBlending_IfAppropriate(long useBufferLeadTicks)
            {
                // FIX (Oct 2025): Prevent MissingReferenceException when accessing destroyed objects after scene unload
                // Unity's "fake null" means destroyed UnityEngine.Object references are not truly null,
                // but accessing their properties (especially Transform) throws MissingReferenceException.
                // This check catches destroyed GONetParticipants before we try to access Transform.position.
                if (syncCompanion.gonetParticipant == null)
                {
                    return; // GONetParticipant has been destroyed (scene unload or manual destroy)
                }

                if (syncCompanion.gonetParticipant.IsMine)
                {
                    return;
                }

                // DISCRETE PARAMS FIX (Jan 2026): Skip blending for values configured with ShouldBlendBetweenValuesReceived=false.
                // Discrete values (like animator int/bool params) are applied directly when received via InitSingle/DeserializeInitAll,
                // not queued for blending. This check prevents spurious ANIMATOR-BLEND-FAIL warnings.
                if (!syncAttribute_ShouldBlendBetweenValuesReceived)
                {
                    return;
                }

                // REPARENTING FIX (Jan 2026): Skip v1 blending when transform sync is suspended.
                // When a child is reparented, SuspendTransformSync() is called to prevent blending
                // from overwriting the local position/rotation offset set during reparenting.
                // This is critical for late joiners who may not yet be registered in SoA v2.
                if (syncCompanion.gonetParticipant.IsTransformSyncSuspendedDueToParenting)
                {
                    return;
                }

                //TODO FIX ME Revisit
                /*Since an IsMine_ToRemotelyControl entity is going to be controlled by the server based on the client inputs we don't want to interpolate this entity but extrapolate it.
                  If we interpolate it, not only will we be adding at least a visual lag equal to RTT ms but also an additional useBufferLeadTicks ms from the interpolation buffer.
                  This can make the entity feel really unresponsive. However, if the user only trust extrapolation, although the visual lag is not going to be that much, the behaviour
                  could feel glitchy based on the issues that extrapolation techniques bring to the table.*/
                if (syncCompanion.gonetParticipant.IsMine_ToRemotelyControl)
                {
                    useBufferLeadTicks = 0;
                }
                // ADAPTIVE BUFFER: Use per-value adaptive lead time if enabled and initialized
                // This ensures smooth interpolation even when trickle mode slows updates to 2Hz
                // SMART: Cap to actual queue contents to avoid requesting more history than exists
                else if (GONetGlobal.Instance?.enableAdaptiveBlendingBuffer == true && adaptiveBufferState.isInitialized && mostRecentChanges_usedSize >= 2)
                {
                    // Calculate actual time span in queue (newest entry is at index 0, oldest at usedSize-1)
                    long newestTicks = mostRecentChanges[0].elapsedTicksAtChange;
                    long oldestTicks = mostRecentChanges[mostRecentChanges_usedSize - 1].elapsedTicksAtChange;
                    long queueTimeSpanTicks = newestTicks - oldestTicks;

                    // Use smart version that caps to available queue history
                    useBufferLeadTicks = adaptiveBufferState.GetAdaptedLeadTimeTicks(queueTimeSpanTicks);
                }

                GONetSyncableValue currentValue = syncCompanion.GetAutoMagicalSyncValue(index);
                GONetSyncableValue blendedValue;

                // DIAG (January 2026): Log v1 blending attempts for non-transform types to debug Vector2/Vector4 sync after handoff
                bool isNonTransformDebug = memberName != null && (memberName.Contains("Vector") || memberName.Contains("Scale"));

                // DIAG: Debug animator parameter blending
                bool isAnimatorDebug = !string.IsNullOrEmpty(animatorParameterName);

                if (ValueBlendUtils.TryGetBlendedValue(this, Time.ElapsedTicks - useBufferLeadTicks, out blendedValue, out bool didExtrapolatePastMostRecentChanges))
                {
                    // DIAG: Log successful blend application for non-transform types
                    // COMMENTED (log cleanup) - fires every frame for each synced member, very spammy
                    /*if (isNonTransformDebug && Time.ElapsedSeconds > 5)
                    {
                        bool isSameValue = currentValue.Equals(blendedValue);
                        GONetLog.Warning($"[V1-BLEND-APPLY] member={memberName} bufferSize={mostRecentChanges_usedSize} GONetId={syncCompanion.gonetParticipant.GONetId} current={currentValue} blended={blendedValue} sameValue={isSameValue} hasAtRest={hasAwaitingAtRest} extrap={didExtrapolatePastMostRecentChanges}");
                    }*/

                    // DIAG: Log successful animator blend (only when ValueBlendUtils.ShouldLog is enabled)
                    // if (ValueBlendUtils.ShouldLog && isAnimatorDebug) GONetLog.Debug($"[ANIMATOR-BLEND-OK] param='{animatorParameterName}' current={currentValue} blended={blendedValue} bufferSize={mostRecentChanges_usedSize}");

                    // We do not want to apply TRULY extrapolated (past end of most recent values) values if an at rest command is awaiting
                    // processing since it is likely that the extrapolation occurred due to lack of information coming from owner since it is at rest.
                    if (!hasAwaitingAtRest || !didExtrapolatePastMostRecentChanges)
                    {
                        // Try to apply via Rigidbody.MovePosition/MoveRotation for smooth physics rendering
                        // Falls back to standard SetAutoMagicalSyncValue if no Rigidbody or not position/rotation
                        if (!TryApplyBlendedValue_UsingRigidbodyIfPresent(blendedValue))
                        {
                            syncCompanion.SetAutoMagicalSyncValue(index, blendedValue);

                            // DIAG: Verify value was actually written to the component
                            if (isNonTransformDebug && Time.ElapsedSeconds > 5)
                            {
                                GONetSyncableValue afterSet = syncCompanion.GetAutoMagicalSyncValue(index);
                                bool writeSucceeded = afterSet.Equals(blendedValue);
                                if (!writeSucceeded)
                                {
                                    GONetLog.Error($"[V1-BLEND-WRITE-FAIL] member={memberName} GONetId={syncCompanion.gonetParticipant.GONetId} attempted={blendedValue} actual={afterSet}");
                                }
                            }
                        }

                        //if (hasAwaitingAtRest)
                        //{
                            //float lerp = (Time.ElapsedTicks - useBufferLeadTicks - hasAwaitingAtRest_sinceLeadTimeAdjustedElapsedTicks) / (float)(hasAwaitingAtRest_assumedInitialRestElapsedTicks - hasAwaitingAtRest_sinceLeadTimeAdjustedElapsedTicks);
                            //if (lerp < 0) lerp = 0;
                            //if (lerp > 1) lerp = 1;
                            //GONetLog.Debug($"sync[{index}] recv({TimeSpan.FromTicks(hasAwaitingAtRest_sinceLeadTimeAdjustedElapsedTicks).TotalSeconds}) lerp:{lerp} until({TimeSpan.FromTicks(hasAwaitingAtRest_assumedInitialRestElapsedTicks).TotalSeconds})  was blending at time: {TimeSpan.FromTicks(Time.ElapsedTicks - useBufferLeadTicks).TotalSeconds}");
                            //GONetLog.Debug($"sync[{index}] current:({syncCompanion.GetAutoMagicalSyncValue(index)}) rest:({hasAwaitingAtRest_value})");
                        //}
                    }
                    //else GONetLog.Debug("hasAwaitingAtRest && didExtrapolate -- skipping auto magical value set.  index: " + index);
                    //else //if (hasAwaitingAtRest)
                    //{
                        //float lerp = (Time.ElapsedTicks - useBufferLeadTicks - hasAwaitingAtRest_sinceLeadTimeAdjustedElapsedTicks) / (float)(hasAwaitingAtRest_assumedInitialRestElapsedTicks - hasAwaitingAtRest_sinceLeadTimeAdjustedElapsedTicks);
                        //if (lerp < 0) lerp = 0;
                        //if (lerp > 1) lerp = 1;
                        //GONetLog.Debug($"will not change! sync[{index}] didExtrapolate:{didExtrapolate} recv({TimeSpan.FromTicks(hasAwaitingAtRest_sinceLeadTimeAdjustedElapsedTicks).TotalSeconds}) lerp:{lerp} until({TimeSpan.FromTicks(hasAwaitingAtRest_assumedInitialRestElapsedTicks).TotalSeconds})  was blending at time: {TimeSpan.FromTicks(Time.ElapsedTicks - useBufferLeadTicks).TotalSeconds}");
                        //GONetLog.Debug($"sync[{index}] current:({syncCompanion.GetAutoMagicalSyncValue(index)}) rest:({hasAwaitingAtRest_value})");
                    //}
                }
                else
                {
                    // STALE VALUE HANDLING (Jan 2026): Apply the last known value ONCE when blending fails.
                    // This ensures animator floats get their correct value even when no updates arrive,
                    // while avoiding constant fighting with animation curves (which would cause jitter).
                    if (mostRecentChanges_usedSize > 0 && !hasAppliedStaleValue)
                    {
                        // Apply the newest value from the buffer once
                        GONetSyncableValue staleValue = mostRecentChanges[0].numericValue;
                        syncCompanion.SetAutoMagicalSyncValue(index, staleValue);
                        hasAppliedStaleValue = true;
                        staleValue_appliedAtBufferNewestTicks = mostRecentChanges[0].elapsedTicksAtChange;
                    }
                }
                //if (Input.GetKeyDown(KeyCode.L)) GONetLog.Append_FlushDebug("**************************************************   something strange happened \n");
            }

            /// <summary>
            /// Attempts to apply blended value using Rigidbody.MovePosition/MoveRotation if a Rigidbody exists.
            /// This respects Unity's Rigidbody interpolation for smooth rendering on non-authority clients.
            /// NOTE: Relies on implementation detail that position/rotation sync values use GONetMain.IsPositionNotSyncd/IsRotationNotSyncd as their ShouldSkipSync delegates.
            /// </summary>
            /// <returns>True if applied via Rigidbody, false if no Rigidbody or not a position/rotation field</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool TryApplyBlendedValue_UsingRigidbodyIfPresent(GONetSyncableValue blendedValue)
            {
                GONetParticipant participant = syncCompanion.gonetParticipant;

                // EARLY EXIT: Only apply for non-authority (IsMine = false)
                if (participant.IsMine)
                    return false;

                // EARLY EXIT: Only proceed if there's a cached Rigidbody (3D or 2D)
                // This check avoids expensive function pointer comparisons when no physics body exists
                if (participant.myRigidBody == null && participant.myRigidBody2D == null)
                    return false;

                // Check if this is position or rotation by matching the skip sync function
                // This is an implementation detail but avoids string comparisons
                bool isPosition = syncAttribute_ShouldSkipSync == GONetMain.IsPositionNotSyncd;
                bool isRotation = syncAttribute_ShouldSkipSync == GONetMain.IsRotationNotSyncd;

                if (!isPosition && !isRotation)
                    return false;

                // Try Rigidbody (3D) - use cached reference
                Rigidbody rb = participant.myRigidBody;
                if (rb != null && rb.isKinematic)
                {
                    if (isPosition)
                    {
                        rb.MovePosition(blendedValue.UnityEngine_Vector3);
                        return true;
                    }
                    else if (isRotation)
                    {
                        rb.MoveRotation(blendedValue.UnityEngine_Quaternion);
                        return true;
                    }
                }

                // Try Rigidbody2D - use cached reference
                Rigidbody2D rb2D = participant.myRigidBody2D;
                if (rb2D != null && rb2D.bodyType == RigidbodyType2D.Kinematic)
                {
                    if (isPosition)
                    {
                        rb2D.MovePosition(blendedValue.UnityEngine_Vector3);
                        return true;
                    }
                    else if (isRotation)
                    {
                        // Rigidbody2D.MoveRotation takes float (Z-axis rotation in degrees)
                        // Extract Z component from Quaternion
                        float zRotation = blendedValue.UnityEngine_Quaternion.eulerAngles.z;
                        rb2D.MoveRotation(zRotation);
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// At time of writing, the only case for this is when transferring ownership of client owned thing over to server ownership and on server there will no longer be value blending as it will be the owner/source for others
            /// </summary>
            internal void ClearMostRecentChanges()
            {
                //GONetLog.Debug("Clearing most recent changes...gonetId: " + syncCompanion.gonetParticipant.GONetId + " index: " + index + "\nbuffer:\n" + GetMostRecentChangesString());
                mostRecentChanges_usedSize = 0; // TODO there really may need to be some more housekeeping to do here, but this is functional.

                // CRITICAL: Invalidate velocity data when clearing queue
                // This prevents blending from using stale velocity after AT-REST messages
                // Set timestamp to 0 so velocity expiration check (200ms) will fail
                lastVelocityTimestamp = 0;

                // CRITICAL FIX (Dec 2025): Clear at-rest rejection flags to allow new sync data after handoff.
                // Without this, AddToMostRecentChangeQueue_IfAppropriate would reject sync updates
                // because their timestamps would be before the stale hasAwaitingAtRest timestamps from
                // the old authority, causing objects to appear "stuck" after voluntary handoff.
                hasAwaitingAtRest = false;
                hasAwaitingAtRest_assumedInitialRestElapsedTicks = 0;
                hasAwaitingAtRest_sinceLeadTimeAdjustedElapsedTicks = 0;
                hasAwaitingAtRest_lastProcessedAtRestTicks = 0;
                hasAwaitingAtRest_needsPhysicsSnap = false;
            }

            private string GetMostRecentChangesString()
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < mostRecentChanges_usedSize; ++i)
                {
                    sb.Append("[").Append(i).Append("], timeAtChange: ").Append(TimeSpan.FromTicks(mostRecentChanges[i].elapsedTicksAtChange).TotalSeconds);
                    sb.Append(" value: ").Append( mostRecentChanges[i].numericValue).AppendLine();
                }
                return sb.ToString();
            }

            internal bool TryGetBlendedValue(long atElapsedTicks, out GONetSyncableValue blendedValue, out bool didExtrapolatePastMostRecentChanges)
            {
                return syncCompanion.TryGetBlendedValue(index, mostRecentChanges, mostRecentChanges_usedSize, atElapsedTicks, out blendedValue, out didExtrapolatePastMostRecentChanges);
            }
        }

        /// <summary>
        /// Only (re)used in <see cref="OnEnable_StartMonitoringForAutoMagicalNetworking"/>.
        /// </summary>
        static readonly HashSet<SyncBundleUniqueGrouping> uniqueSyncGroupings = new HashSet<SyncBundleUniqueGrouping>();
        static readonly HashSet<GONetParticipant> pendingAutoSyncCompanionRecovery = new HashSet<GONetParticipant>();
        static readonly List<GONetParticipant> pendingAutoSyncCompanionRecoveryScratch = new List<GONetParticipant>(128);

        internal struct SyncBundleUniqueGrouping : IEquatable<SyncBundleUniqueGrouping>
        {
            /// <summary>
            /// How many seconds between each scheduled call?
            /// </summary>
            internal readonly float scheduleFrequency;
            /// <summary>
            /// How many times a second is the scheduled frequency?
            /// </summary>
            internal readonly short scheduleFrequencyHz;
            internal readonly AutoMagicalSyncReliability reliability;
            internal readonly bool mustRunOnUnityMainThread;

            internal SyncBundleUniqueGrouping(float scheduleFrequency, AutoMagicalSyncReliability reliability, bool mustRunOnUnityMainThread)
            {
                this.scheduleFrequency = scheduleFrequency;

                float v = 1.0f / scheduleFrequency;
                scheduleFrequencyHz = (short)(v + 0.5f);

                this.reliability = reliability;
                this.mustRunOnUnityMainThread = mustRunOnUnityMainThread;
            }

            public override bool Equals(object obj)
            {
                if (!(obj is SyncBundleUniqueGrouping))
                {
                    return false;
                }

                var other = (SyncBundleUniqueGrouping)obj;
                return scheduleFrequency == other.scheduleFrequency &&
                       reliability == other.reliability &&
                       mustRunOnUnityMainThread == other.mustRunOnUnityMainThread;
            }

            public bool Equals(SyncBundleUniqueGrouping other)
            {
                return scheduleFrequency == other.scheduleFrequency &&
                       reliability == other.reliability &&
                       mustRunOnUnityMainThread == other.mustRunOnUnityMainThread;
            }

            public override int GetHashCode()
            {
                var hashCode = -1343937139;
                hashCode = hashCode * -1521134295 + scheduleFrequency.GetHashCode();
                hashCode = hashCode * -1521134295 + reliability.GetHashCode();
                hashCode = hashCode * -1521134295 + mustRunOnUnityMainThread.GetHashCode();
                return hashCode;
            }
        }

        internal static IEnumerator OnAwake_ApplyDesignTimeMetadata(GONetParticipant gonetParticipant)
        {
            //GONetLog.Debug($"dreetsi cikd wash sod installed");
            if (Application.isPlaying) // now that [ExecuteInEditMode] was added to GONetParticipant for OnDestroy, we have to guard this to only run in play
            {
                while (!GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached)
                {
                    //GONetLog.Warning($"dreetsi");
                    yield return null;
                }
                //GONetLog.Debug($"dreetsi   --- now we poop!");

                InitDesignTimeMetadata_IfNeeded(gonetParticipant);
            }
        }

        /// <summary>
        /// Call me in the <paramref name="gonetParticipant"/>'s OnEnable method.
        /// </summary>
        internal static void OnEnable_StartMonitoringForAutoMagicalNetworking(GONetParticipant gonetParticipant)
        {
            // IMPORTANT: We no longer can call this at this time becauase due to the latest implementation of how desigh time location is
            //            stored/processed, the WasInstantiated is not known at this point and if we called the method below bad things would happen
            //            because the WasInstantiated is needed to be known in order to figure out design time metadata like code gen id which is needed for next method to work.
            //            Instead, check out OnWasInstantiatedKnown_StartMonitoringForAutoMagicalNetworking
            //StartMonitoringForAutoMagicalNetworking(gonetParticipant);

            //GONetLog.Debug($"gnp.name: {gonetParticipant.name} WasInstantiatedForce: {gonetParticipant.wasInstantiatedForce}");
            if (gonetParticipant.wasInstantiatedForce)
            {
                // we now know this was instantiated (from remote source as that is the only time WasInstantiatedForce is true)....scene stuff gets this called automatically elsewhere
                EnsureAutoMagicalSyncCompanionRegistered(gonetParticipant, "OnEnable(remote spawn)");
            }
            else if (WasDefinedInScene(gonetParticipant))
            {
                // Scene-defined objects can be disabled/re-enabled after initial scene processing
                // (e.g., reparented under an inactive parent). Ensure monitoring is restored on re-enable.
                EnsureAutoMagicalSyncCompanionRegistered(gonetParticipant, "OnEnable(scene re-enable)");
            }
            else if (gonetParticipant.GONetId != GONetParticipant.GONetId_Unset)
            {
                // Runtime-spawned objects can be disabled/re-enabled (e.g., parented under inactive hierarchies).
                // Ensure maps/sync companions are restored on re-enable.
                if (GONetConfig.LogParticipantMapDiagnostics)
                {
                    GONetLog.Debug($"[PARTICIPANT-REENABLE] Runtime re-enable for '{gonetParticipant.name}' " +
                                   $"GONetId={gonetParticipant.GONetId} InstantiationId={gonetParticipant.GONetIdAtInstantiation} " +
                                   $"ActiveInHierarchy={gonetParticipant.gameObject.activeInHierarchy} Enabled={gonetParticipant.enabled}");
                }
                EnsureAutoMagicalSyncCompanionRegistered(gonetParticipant, "OnEnable(runtime re-enable)");
            }
        }

        private static void EnsureAutoMagicalSyncCompanionRegistered(GONetParticipant gonetParticipant, string context)
        {
            if (gonetParticipant == null)
            {
                return;
            }

            if (gonetParticipant.DidStartMonitoringForAutoMagicalNetworking &&
                GetSyncCompanionByGNP(gonetParticipant) != null)
            {
                return;
            }

            if (!gonetParticipant.wasInstantiatedForce &&
                !gonetParticipant.IsDesignTimeMetadataInitd &&
                !GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached)
            {
                EnqueueAutoSyncCompanionRecovery(gonetParticipant, context);
                return;
            }

            gonetParticipant.DidStartMonitoringForAutoMagicalNetworking = false;
            OnWasInstantiatedKnown_StartMonitoringForAutoMagicalNetworking(gonetParticipant);

            if (GetSyncCompanionByGNP(gonetParticipant) == null && GONetConfig.LogSpawnDiagnostics)
            {
                GONetLog.Warning($"[AUTOMAGIC-RECOVERY] Sync companion still missing for '{gonetParticipant.gameObject.name}' after {context} " +
                                $"(GONetId: {gonetParticipant.GONetId}, CodeGenId: {gonetParticipant.CodeGenerationId}).");
            }
        }

        private static void EnqueueAutoSyncCompanionRecovery(GONetParticipant gonetParticipant, string context)
        {
            if (gonetParticipant == null)
            {
                return;
            }

            if (!pendingAutoSyncCompanionRecovery.Add(gonetParticipant))
            {
                return;
            }

            if (GONetConfig.LogSpawnDiagnostics)
            {
                GONetLog.Debug($"[AUTOMAGIC-RECOVERY] Deferred sync companion registration for '{gonetParticipant.gameObject.name}' " +
                               $"until design-time metadata is cached (context: {context}).");
            }
        }

        private static void ProcessPendingAutoSyncCompanionRecovery()
        {
            if (pendingAutoSyncCompanionRecovery.Count == 0)
            {
                return;
            }

            if (!GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached)
            {
                return;
            }

            pendingAutoSyncCompanionRecoveryScratch.Clear();
            pendingAutoSyncCompanionRecoveryScratch.AddRange(pendingAutoSyncCompanionRecovery);
            pendingAutoSyncCompanionRecovery.Clear();

            foreach (var gonetParticipant in pendingAutoSyncCompanionRecoveryScratch)
            {
                if (gonetParticipant == null)
                {
                    continue;
                }

                if (!gonetParticipant.gameObject.activeInHierarchy || !gonetParticipant.enabled)
                {
                    continue;
                }

                if (gonetParticipant.DidStartMonitoringForAutoMagicalNetworking &&
                    GetSyncCompanionByGNP(gonetParticipant) != null)
                {
                    continue;
                }

                EnsureAutoMagicalSyncCompanionRegistered(gonetParticipant, "Deferred recovery");

                if (!gonetParticipant.IsMine && !gonetParticipant.v2_isRegisteredInSoA &&
                    GetSyncCompanionByGNP(gonetParticipant) != null)
                {
                    RegisterObjectInSoA(gonetParticipant);
                }
            }
        }

        private static void OnWasInstantiatedKnown_StartMonitoringForAutoMagicalNetworking(GONetParticipant gonetParticipant)
        {
            // Process AutoDontDestroyOnLoad flag BEFORE starting monitoring
            // This ensures the scene identifier is set correctly
            if (gonetParticipant.AutoDontDestroyOnLoad)
            {
                UnityEngine.Object.DontDestroyOnLoad(gonetParticipant.gameObject);
                GONetLog.Debug($"[DDOL] Auto-applied DontDestroyOnLoad to: {gonetParticipant.gameObject.name}");
            }

            StartMonitoringForAutoMagicalNetworking(gonetParticipant);
        }

        private static void StartMonitoringForAutoMagicalNetworking(GONetParticipant gonetParticipant)
        {
            if (Application.isPlaying) // now that [ExecuteInEditMode] was added to GONetParticipant for OnDestroy, we have to guard this to only run in play
            {
                InitDesignTimeMetadata_IfNeeded(gonetParticipant);

                if (gonetParticipant.CodeGenerationId == GONetParticipant.CodeGenerationId_Unset ||
                    gonetParticipant.DidStartMonitoringForAutoMagicalNetworking)
                {
                    //GONetLog.Debug($"dreetsi never never in life.  code gen id: {gonetParticipant.CodeGenerationId}, did start? {gonetParticipant.DidStartMonitoringForAutoMagicalNetworking}");
                    return;
                }

                { // auto-magical sync related housekeeping
                    Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> autoSyncCompanions;
                    //GONetLog.Debug($"[COMPANION-CREATE] Creating companion for '{gonetParticipant.gameObject.name}' with CodeGenerationId={gonetParticipant.CodeGenerationId}");
                    if (!activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(gonetParticipant.CodeGenerationId, out autoSyncCompanions))
                    {
                        autoSyncCompanions = new Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated>(1000);
                        activeAutoSyncCompanionsByCodeGenerationIdMap[gonetParticipant.CodeGenerationId] = autoSyncCompanions; // NOTE: This is the only place we add to the outer dictionary and this is always run in the main unity thread, THEREFORE no need for Concurrent....just on the inner ones
                    }
                    GONetParticipant_AutoMagicalSyncCompanion_Generated companion = GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.CreateInstance(gonetParticipant);

#if UNITY_EDITOR
                    // RUNTIME VALIDATION: Detect GONetParticipants that are problematic due to design-time changes.
                    // Two cases are handled:
                    // 1. companion == null: CodeGenerationId not recognized (GNP added after build)
                    // 2. GNP path matches a known problematic path from dirty reasons file
                    if (shouldValidateUnknownGNPs)
                    {
                        string gnpHierarchyPath = GONet.Utils.HierarchyUtils.GetFullUniquePath(gonetParticipant.gameObject);
                        string disableReason = null;

                        if (companion == null)
                        {
                            // Case 1: Unknown CodeGenerationId - definitely problematic
                            disableReason = $"CodeGenerationId {gonetParticipant.CodeGenerationId} is not recognized by the current build.";
                        }
                        else
                        {
                            // Case 2: Check if this GNP matches a known problematic path
                            string pathMatchReason = GetProblematicReasonForGNP(gonetParticipant);
                            if (pathMatchReason != null)
                            {
                                disableReason = pathMatchReason;
                            }
                        }

                        if (disableReason != null)
                        {
                            // Check GONetGlobal config to determine how to handle problematic GNPs
                            bool shouldDisable = GONetGlobal.Instance != null &&
                                GONetGlobal.Instance.problematicGNPHandling == GONetGlobal.ProblematicGNPHandling.Disable;

                            string actionMessage;
                            if (shouldDisable)
                            {
                                actionMessage = "GONetParticipant and all GONetParticipantCompanionBehaviour components DISABLED to prevent errors.";
                            }
                            else
                            {
                                actionMessage = "Logging only (components NOT disabled). Object will attempt to network but may cause errors.";
                            }

                            // Use display-friendly path for GameObject line (strips internal unique identifiers)
                            string displayPath = GetDisplayFriendlyPath(gnpHierarchyPath);

                            GONetLog.Error($"[GONet] PROBLEMATIC GONetParticipant DETECTED\n" +
                                $"  GameObject: '{displayPath}'\n" +
                                $"  CodeGenerationId: {gonetParticipant.CodeGenerationId}\n" +
                                $"  Why: {disableReason}\n" +
                                $"  (Note: Paths in 'Why' may contain special characters like _+3...N06 that GONet uses internally to identify siblings with the same name.)\n" +
                                $"  Action: {actionMessage}\n" +
                                $"  TO FIX: Create a new build (File → Build and Run) so that all clients and the server share the same GONet-related content.\n" +
                                $"          This ensures every networked object is recognized consistently across all machines.\n" +
                                $"  CONFIG: To change this behavior, adjust 'Problematic GNP Handling' on GONetGlobal in your scene.\n" +
                                $"  NOTE: If this detection seems incorrect (false positive), set 'Problematic GNP Handling' to 'LogOnly' on GONetGlobal.");

                            if (shouldDisable)
                            {
                                // Disable the GONetParticipant
                                gonetParticipant.enabled = false;

                                // Also disable all GONetParticipantCompanionBehaviour components on the same GameObject
                                var companionBehaviours = gonetParticipant.GetComponents<GONetParticipantCompanionBehaviour>();
                                int disabledCount = 0;
                                foreach (var cb in companionBehaviours)
                                {
                                    if (cb.enabled)
                                    {
                                        cb.enabled = false;
                                        disabledCount++;
                                    }
                                }

                                if (disabledCount > 0)
                                {
                                    GONetLog.Warning($"[GONet] Also disabled {disabledCount} GONetParticipantCompanionBehaviour component(s) on '{displayPath}'.");
                                }

                                return; // Don't process this GNP further
                            }
                            // If LogOnly mode, continue processing (may cause errors, but user chose this)
                        }
                    }
#endif

                    autoSyncCompanions[gonetParticipant] = companion; // NOTE: This is the only place where the inner dictionary is added to and is ensured to run on unity main thread since OnEnable, so no need for concurrency as long as we can say the same about removes

                    // RACE CONDITION FIX: Eagerly populate uint-keyed map if GONetIdAtInstantiation already assigned
                    // Normally, uint-keyed map is populated when OnGONetIdAtInstantiationChanged event fires.
                    // However, during rapid spawning, sync bundles may arrive BEFORE the event processes.
                    // If GONetIdAtInstantiation is already set, populate uint-keyed map immediately to eliminate race window.
                    if (gonetParticipant.GONetIdAtInstantiation != GONetParticipant.GONetId_Unset)
                    {
                        Dictionary<uint, GONetParticipant_AutoMagicalSyncCompanion_Generated> autoSyncCompanions_uintKeyForPerformance;
                        if (!activeAutoSyncCompanionsByCodeGenerationIdMap_uintKeyForPerformance.TryGetValue(gonetParticipant.CodeGenerationId, out autoSyncCompanions_uintKeyForPerformance))
                        {
                            autoSyncCompanions_uintKeyForPerformance = new Dictionary<uint, GONetParticipant_AutoMagicalSyncCompanion_Generated>(1000);
                            activeAutoSyncCompanionsByCodeGenerationIdMap_uintKeyForPerformance[gonetParticipant.CodeGenerationId] = autoSyncCompanions_uintKeyForPerformance;
                        }
                        autoSyncCompanions_uintKeyForPerformance[gonetParticipant.GONetIdAtInstantiation] = companion;
                    }

                    gonetParticipant.AddGONetIdAtInstantiationChangedHandler(OnGONetIdAtInstantiationChanged_DoSomeMapMaintenanceForKeyLookupPerformanceLater);

                    uniqueSyncGroupings.Clear();
                    for (int i = 0; i < companion.valuesCount; ++i)
                    {
                        AutoMagicalSync_ValueMonitoringSupport_ChangedValue monitoringSupport = companion.valuesChangesSupport[i];

                        if (!GONetParticipant_AutoMagicalSyncCompanion_Generated.ShouldSkipSync(monitoringSupport, i))
                        {
                            SyncBundleUniqueGrouping grouping =
                                new SyncBundleUniqueGrouping(
                                    monitoringSupport.syncAttribute_SyncChangesEverySeconds,
                                    monitoringSupport.syncAttribute_Reliability,
                                    monitoringSupport.syncAttribute_MustRunOnUnityMainThread);

                            uniqueSyncGroupings.Add(grouping); // since it is a set, duplicates will be discarded
                        }
                    }

                    if (gonetParticipant.animatorSyncSupport != null)
                    { // auto-sync stuffs, but this time for animation controller parameters
                        var animatorSyncSupportEnum = gonetParticipant.animatorSyncSupport.GetEnumerator();
                        while (animatorSyncSupportEnum.MoveNext())
                        {
                            string parameterName = animatorSyncSupportEnum.Current.Key;
                            GONetParticipant.AnimatorControllerParameter parameter = animatorSyncSupportEnum.Current.Value;

                            //GONetLog.Debug(string.Concat("animator parameter....name: ", parameterName, " type: ", parameter.valueType, " isSyncd: ", parameter.isSyncd));
                        }
                    }

                    foreach (SyncBundleUniqueGrouping uniqueSyncGrouping in uniqueSyncGroupings)
                    {
                        if (!autoSyncProcessingSupportByFrequencyMap.ContainsKey(uniqueSyncGrouping))
                        {
                            GONetLog.Debug($"[SYNC-SCHEDULER-CREATE] Creating sync scheduler for grouping={uniqueSyncGrouping.scheduleFrequency}/{uniqueSyncGrouping.reliability}, mustRunOnMainThread={uniqueSyncGrouping.mustRunOnUnityMainThread}");
                            var autoSyncProcessingSupport =
                                new AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable(uniqueSyncGrouping, activeAutoSyncCompanionsByCodeGenerationIdMap); // IMPORTANT: this starts the thread!
                            autoSyncProcessingSupport.AboutToProcess += AutoSyncProcessingSupport_AboutToProcess;
                            autoSyncProcessingSupportByFrequencyMap[uniqueSyncGrouping] = autoSyncProcessingSupport;

                            if (uniqueSyncGrouping.mustRunOnUnityMainThread)
                            {
                                autoSyncProcessingSupports_UnityMainThread.Add(autoSyncProcessingSupport);
                            }
                        }
                    }
                }

                if (gonetParticipant.GONetId != GONetParticipant.GONetId_Unset) // FYI, the normal case is that at this point, GONetId will be 0/unset, because this is happening as a result of Instantiate being called in which case the actual GONetId assignment will not occur until just AFTER OnEnable is finished!
                {
                    gonetParticipantByGONetIdMap[gonetParticipant.GONetId] = gonetParticipant; // be doubly sure we have this (the case where it would not already is if gnp was started-disabled-enabled

                    // Deferred RPC system will automatically retry via ProcessDeferredRpcs() running every frame
                }

                uint gonetIdThatIsGoingToBePopulated = isCurrentlyProcessingInstantiateGNPEvent ? currentlyProcessingInstantiateGNPEvent.GONetId : gonetParticipant.GONetId;
                var enableEvent = new GONetParticipantEnabledEvent(gonetIdThatIsGoingToBePopulated);
                PublishEventAsSoonAsSufficientInfoAvailable(enableEvent, gonetParticipant);

                //const string INSTANTIATE = "GNP Enabled go.name: ";
                //const string ID = " gonetId: ";
                //GONetLog.Debug(string.Concat(INSTANTIATE, gonetParticipant.gameObject.name, ID + gonetParticipant.GONetId));

                gonetParticipant.DidStartMonitoringForAutoMagicalNetworking = true;
            }
        }

        private static void InitDesignTimeMetadata_IfNeeded(GONetParticipant gonetParticipant)
        {
            //GONetLog.Debug($"InitDesignTimeMetadata_IfNeeded: Called for '{gonetParticipant.gameObject.name}', IsDesignTimeMetadataInitd: {gonetParticipant.IsDesignTimeMetadataInitd}, UnityGuid: '{gonetParticipant.UnityGuid}'");

            if (!gonetParticipant.IsDesignTimeMetadataInitd)
            {
                // IMPORTANT: We must ensure the design-time metadata cache is loaded before attempting to initialize
                // This check prevents initialization before the DesignTimeMetadata.json file has been loaded into memory
                // Normally this is guaranteed by GONetParticipant.AwakeCoroutine() waiting for the cache,
                // but when called from AutoPropagateInitialInstantiation, we need this explicit guard
                if (!GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached)
                {
                    GONetLog.Warning($"InitDesignTimeMetadata_IfNeeded: Cannot initialize metadata for '{gonetParticipant.gameObject.name}' - design-time metadata cache not loaded yet! This should not happen in normal flow.");
                    return;
                }

                string fullUniquePath = gonetParticipant.fullUniquePathInSceneAtAwake;
                if (string.IsNullOrWhiteSpace(fullUniquePath))
                {
                    fullUniquePath = DesignTimeMetadata.GetFullUniquePathInScene(gonetParticipant);
                }
                //GONetLog.Debug($"InitDesignTimeMetadata_IfNeeded: Calling InitDesignTimeMetadata for '{gonetParticipant.gameObject.name}' with path: {fullUniquePath}, UnityGuid: '{gonetParticipant.UnityGuid}'");
                GONetSpawnSupport_Runtime.InitDesignTimeMetadata(fullUniquePath, gonetParticipant);
            }
        }

        private static readonly Dictionary<SyncBundleUniqueGrouping, long> autoSyncUniqueGroupingToLastElapsedTicks =
            new Dictionary<SyncBundleUniqueGrouping, long>();

        private static void AutoSyncProcessingSupport_AboutToProcess(in SyncBundleUniqueGrouping uniqueGrouping, long elapsedTicks)
        {
            if (!autoSyncUniqueGroupingToLastElapsedTicks.TryGetValue(uniqueGrouping, out long uniqueElapsedTicks_previous))
            {
                uniqueElapsedTicks_previous = elapsedTicks;
            }

            double uniqueElapsedSeconds = TimeSpan.FromTicks(elapsedTicks).TotalSeconds;
            double uniqueDeltaSeconds = TimeSpan.FromTicks(elapsedTicks - uniqueElapsedTicks_previous).TotalSeconds;

            { // account for some tick receivers adding or removing during a call to tick, which must avoid updating collection while enumerating it
                foreach (var tickReceiver in tickReceivers_awaitingAdd)
                {
                    tickReceivers.Add(tickReceiver);
                }
                tickReceivers_awaitingAdd.Clear();
                foreach (var tickReceiver in tickReceivers_awaitingRemove)
                {
                    tickReceivers.Remove(tickReceiver);
                }
                tickReceivers_awaitingRemove.Clear();
            }

            // PERFORMANCE FIX: Use GONet's ArrayPool to avoid GC from ToArray() - zero allocations after warmup
            // CRITICAL: The HashSet enumeration itself can throw if modified, so we try/catch it
            int tickReceiversCount = tickReceivers.Count;
            if (tickReceiversCount > 0)
            {
                GONetBehaviour[] tickReceiversSnapshot = tickReceivers_arrayPool.Borrow(tickReceiversCount);
                int actualCount = 0;
                try
                {
                    // Try to copy - if collection is modified during copy, we'll catch and skip this tick cycle
                    foreach (var tickReceiver in tickReceivers)
                    {
                        if (actualCount >= tickReceiversSnapshot.Length) break; // Safety check
                        tickReceiversSnapshot[actualCount++] = tickReceiver;
                    }

                    // Iterate using actual count (not array.Length, which may be larger than needed)
                    for (int i = 0; i < actualCount; i++)
                    {
                        tickReceiversSnapshot[i].Tick(uniqueGrouping.scheduleFrequencyHz, uniqueElapsedSeconds, uniqueDeltaSeconds);
                    }
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Collection was modified"))
                {
                    // Collection was modified during enumeration - this can happen if Tick() callbacks
                    // trigger add/remove that bypasses the deferred system. Skip this tick cycle.
                    GONetLog.Warning($"tickReceivers collection modified during Tick() - skipping this sync cycle for {uniqueGrouping}. This should be rare.");
                }
                finally
                {
                    // CRITICAL: Always return to pool, even if exception thrown
                    tickReceivers_arrayPool.Return(tickReceiversSnapshot);
                }
            }

            autoSyncUniqueGroupingToLastElapsedTicks[uniqueGrouping] = elapsedTicks;
        }

        /// <summary>
        /// auto-magical sync related housekeeping....essentially populating a shadow map that uses a different key that was not available with correct value when the first map was created
        /// </summary>
        private static void OnGONetIdAtInstantiationChanged_DoSomeMapMaintenanceForKeyLookupPerformanceLater(GONetParticipant gonetParticipant)
        {
            //GONetLog.Debug($"DREETSi update map. gnp.name: {gonetParticipant.name}, genId: {gonetParticipant.CodeGenerationId}, gonetid@instantiation: {gonetParticipant.GONetIdAtInstantiation}, now: {gonetParticipant.GONetId}");

            Dictionary<uint, GONetParticipant_AutoMagicalSyncCompanion_Generated> autoSyncCompanions_uintKeyForPerformance;
            if (!activeAutoSyncCompanionsByCodeGenerationIdMap_uintKeyForPerformance.TryGetValue(gonetParticipant.CodeGenerationId, out autoSyncCompanions_uintKeyForPerformance))
            {
                autoSyncCompanions_uintKeyForPerformance = new Dictionary<uint, GONetParticipant_AutoMagicalSyncCompanion_Generated>(1000);
                activeAutoSyncCompanionsByCodeGenerationIdMap_uintKeyForPerformance[gonetParticipant.CodeGenerationId] = autoSyncCompanions_uintKeyForPerformance; // NOTE: This is the only place we add to the outer dictionary and this is always run in the main unity thread, THEREFORE no need for Concurrent....just on the inner ones
            }

            Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> autoSyncCompanions = activeAutoSyncCompanionsByCodeGenerationIdMap[gonetParticipant.CodeGenerationId];
            autoSyncCompanions_uintKeyForPerformance[gonetParticipant.GONetIdAtInstantiation] = autoSyncCompanions[gonetParticipant]; // NOTE: This is the only place where the inner dictionary is added to and is ensured to run on unity main thread since OnEnable, so no need for concurrency as long as we can say the same about removes
        }

        public static bool IsChannelClientInitializationRelated(GONetChannelId channelId)
        {
            return
                channelId == GONetChannel.ClientInitialization_EventSingles_Reliable ||
                channelId == GONetChannel.ClientInitialization_CustomSerialization_Reliable ||
                channelId == GONetChannel.TimeSync_Unreliable; // CRITICAL: Time sync must happen during initialization!
        }

        public static bool WasDefinedInScene(GONetParticipant gonetParticipant)
        {
            return definedInSceneParticipantInstanceIDs.Contains(gonetParticipant.GetInstanceID());
        }

        internal static void Start_AutoPropagateInstantiation_IfAppropriate(GONetParticipant gonetParticipant)
        {
            if (Application.isPlaying)
            {
                bool isGONetLocal = gonetParticipant.GetComponent<GONetLocal>() != null;
                if (isGONetLocal && GONetConfig.LogSpawnDiagnostics)
                {
                    GONetLog.Debug($"[SPAWN-DIAG] Start_AutoPropagateInstantiation CALLED for GONetLocal - IsClientVsServerStatusKnown: {IsClientVsServerStatusKnown}, IsClient: {IsClient}, IsServer: {IsServer}");
                }

                if (IsClientVsServerStatusKnown)
                {
                    Start_AutoPropogateInstantiation_IfAppropriate_INTERNAL(gonetParticipant);
                }
                else
                {
                    GlobalSessionContext_Participant.StartCoroutine(AutoPropogateInstantiation_WhenAppropriate(gonetParticipant));
                }
            }
        }

        private static IEnumerator AutoPropogateInstantiation_WhenAppropriate(GONetParticipant gonetParticipant)
        {
            while (!IsClientVsServerStatusKnown)
            {
                yield return null;
            }

            Start_AutoPropogateInstantiation_IfAppropriate_INTERNAL(gonetParticipant);
        }

        private static void Start_AutoPropogateInstantiation_IfAppropriate_INTERNAL(GONetParticipant gonetParticipant)
        {
            bool isGONetLocal = gonetParticipant.GetComponent<GONetLocal>() != null;

            // POOLING: Pooled objects are initialized via pool events, not spawn propagation.
            if (gonetParticipant.isPooled)
            {
                return;
            }

            // SPECIAL CASE: GONetGlobal must NEVER be propagated via spawn events
            // GONetGlobal is instantiated locally on both server and clients (lobby pattern)
            // All instances use hardcoded GONetId (2047) to stay synchronized
            // Propagating spawn events would cause unnecessary network traffic and potential race conditions
            if (gonetParticipant.GetComponent<GONetGlobal>() != null)
            {
                //GONetLog.Debug($"[SPAWN] GONetGlobal detected - suppressing spawn event propagation (locally instantiated on all machines)");
                return; // Skip all spawn propagation logic for GONetGlobal
            }

            bool wasDefinedInScene = WasDefinedInScene(gonetParticipant);

            if (isGONetLocal && GONetConfig.LogSpawnDiagnostics)
            {
                GONetLog.Debug($"[SPAWN-DIAG] INTERNAL for GONetLocal - wasDefinedInScene: {wasDefinedInScene}, IsServer: {IsServer}, IsClient: {IsClient}, gonetId_raw: {gonetParticipant.gonetId_raw}, OwnerAuthorityId: {gonetParticipant.OwnerAuthorityId}");
            }
            //GONetLog.Info($"[SPAWN] Start_AutoPropogateInstantiation_IfAppropriate_INTERNAL - name: '{gonetParticipant.gameObject.name}', wasDefinedInScene: {wasDefinedInScene}, IsServer: {IsServer}, IsClient: {IsClient}");

            if (wasDefinedInScene)
            {
                //GONetLog.Info($"[SPAWN] '{gonetParticipant.gameObject.name}' was defined in scene - will only assign GONetId on server, NO spawn event propagation");

                // DISTRIBUTED HOST: Scene objects get SpawnerPersistentId = 0 (sentinel value)
                // This makes them immune to ProcessSpawnerDeath cleanup during failover
                gonetParticipant.SpawnerPersistentId = GONetParticipant.SpawnerPersistentId_NoSpawner;
                //GONetLog.Debug($"[SPAWN] Scene object '{gonetParticipant.name}' assigned SpawnerPersistentId=0 (immune to ProcessSpawnerDeath)");

                if (IsServer) // stuff defined in the scene will be owned by the server and therefore needs to be assigned a GONetId by server
                {
                    // CRITICAL: Set OwnerAuthorityId BEFORE AssignGONetIdRaw so GONetId is composed correctly
                    // This is a fallback for cases where GONetGlobal.OnSceneLoaded doesn't fire (e.g., duplicate GONetGlobal in scene)
                    // Normally AssignOwnerAuthorityIds_IfAppropriate handles this, but this ensures it happens even if that doesn't fire
                    if (gonetParticipant.OwnerAuthorityId == OwnerAuthorityId_Unset)
                    {
                        gonetParticipant.OwnerAuthorityId = MyAuthorityId;
                    }

                    AssignGONetIdRaw_IfAppropriate(gonetParticipant);
                }
                else if (IsClient)
                {
                    // LIFECYCLE GATE: Scene-defined objects on clients require DeserializeInitAllCompleted before OnGONetReady
                    // (They're receiving sync data from server, not local authority)
                    gonetParticipant.MarkRequiresDeserializeInit();
                }
            }
            else
            {
                bool isThisCondisideredTheMomentOfInitialInstantiation = !remoteSpawns_avoidAutoPropagateSupport.Contains(gonetParticipant);
                //GONetLog.Info($"[SPAWN] '{gonetParticipant.gameObject.name}' NOT defined in scene - isThisCondisideredTheMomentOfInitialInstantiation: {isThisCondisideredTheMomentOfInitialInstantiation}");

                if (isGONetLocal && GONetConfig.LogSpawnDiagnostics)
                {
                    GONetLog.Debug($"[SPAWN-DIAG] GONetLocal ELSE branch - isThisCondisideredTheMomentOfInitialInstantiation: {isThisCondisideredTheMomentOfInitialInstantiation}, IsMarkedForRemote: {GONetSpawnSupport_Runtime.IsMarkedToBeRemotelyControlled(gonetParticipant)}");
                }

                if (isThisCondisideredTheMomentOfInitialInstantiation)
                {
                    // DISTRIBUTED HOST: Runtime spawns get SpawnerPersistentId = local persistent ID
                    // This tracks who spawned the object for ProcessSpawnerDeath cleanup during failover
                    gonetParticipant.SpawnerPersistentId = GONetNodeIdentityManager.GetOrCreatePersistentId();
                    //GONetLog.Debug($"[SPAWN] Runtime object '{gonetParticipant.name}' assigned SpawnerPersistentId={gonetParticipant.SpawnerPersistentId:X16} (will be destroyed if spawner leaves)");

                    if (IsClient && GONetSpawnSupport_Runtime.IsMarkedToBeRemotelyControlled(gonetParticipant))
                    {
                        Client_DoAutoPropogateInstantiationPrep_RemotelyControlled(gonetParticipant);
                    }
                    else
                    {
                        gonetParticipant.OwnerAuthorityId = MyAuthorityId; // With the flow of methods and such, this looks like the first point in time we know to set this to my authority id
                    }

                    if (isGONetLocal && GONetConfig.LogSpawnDiagnostics)
                    {
                        GONetLog.Debug($"[SPAWN-DIAG] GONetLocal BEFORE AssignGONetIdRaw - gonetId_raw: {gonetParticipant.gonetId_raw}, OwnerAuthorityId: {gonetParticipant.OwnerAuthorityId}, GONetId: {gonetParticipant.GONetId}");
                    }

                    //GONetLog.Info($"[SPAWN] About to assign GONetId and publish spawn event for '{gonetParticipant.gameObject.name}'");
                    AssignGONetIdRaw_IfAppropriate(gonetParticipant);

                    if (isGONetLocal && GONetConfig.LogSpawnDiagnostics)
                    {
                        GONetLog.Debug($"[SPAWN-DIAG] GONetLocal AFTER AssignGONetIdRaw - gonetId_raw: {gonetParticipant.gonetId_raw}, GONetId: {gonetParticipant.GONetId}, DoesContainAllComponents: {gonetParticipant.DoesGONetIdContainAllComponents()}");
                    }

                    //GONetLog.Info($"[SPAWN] Assigned GONetId {gonetParticipant.GONetId} to '{gonetParticipant.gameObject.name}'");
                    AutoPropagateInitialInstantiation(gonetParticipant);

                    if (isGONetLocal && GONetConfig.LogSpawnDiagnostics)
                    {
                        GONetLog.Debug($"[SPAWN-DIAG] GONetLocal AFTER AutoPropagateInitialInstantiation - spawn event published");
                    }

                    //GONetLog.Info($"[SPAWN] Published spawn event for '{gonetParticipant.gameObject.name}' with GONetId {gonetParticipant.GONetId}");
                    OnWasInstantiatedKnown_StartMonitoringForAutoMagicalNetworking(gonetParticipant); // we now know this was instantiated (by local source...remote source is processed like this elsewhere)....scene stuff gets this called automatically elsewhere

                    // FIX (December 2025): Register client-spawned server-owned objects in SoA IMMEDIATELY.
                    // Problem: OnGONetReady doesn't reliably fire for these objects because IsGONetReady()
                    // returns false when IsInternallyConfigured=false (Awake coroutine not complete).
                    // This causes objects to never be registered in SoA lookup, so sync data is dropped.
                    // Solution: Register directly here since we know:
                    // 1. This is a client (IsClient=true)
                    // 2. This is server-owned (IsMarkedToBeRemotelyControlled=true, so IsMine=false)
                    // 3. GONetId is now assigned
                    // 4. Object needs blending (will receive sync data from server)
                    if (IsClient && GONetSpawnSupport_Runtime.IsMarkedToBeRemotelyControlled(gonetParticipant) &&
                        !gonetParticipant.v2_isRegisteredInSoA)
                    {
                        RegisterObjectInSoA(gonetParticipant);
#if GONet_SOA_TRACE
                        GONetLog.Debug($"[SoA-IMMEDIATE-REG] Registered client-spawned server-owned '{gonetParticipant.name}' (GONetId {gonetParticipant.GONetId}) in SoA immediately at spawn");
#endif
                    }
                }
                else
                {
                    // this data item has now served its purpose (i.e., avoid auto propagate since it already came from remote source!), so remove it
                    remoteSpawns_avoidAutoPropagateSupport.Remove(gonetParticipant);
                }
            }

            var startEvent = new GONetParticipantStartedEvent(gonetParticipant);
            PublishEventAsSoonAsSufficientInfoAvailable(startEvent, gonetParticipant);

            // REMOVED: Path 1 (Start) publication - this caused race conditions with GONetLocal.AddToLookupOnceAuthorityIdKnown
            // All IsMine participants are now published from GONetLocal.AddToLookupOnceAuthorityIdKnown (Path 3/4) - the definitive moment of readiness
            // Remote participants are published from deserialization path (Path 2)
            // This ensures 100% coverage with zero race conditions and zero duplicates

            // PATH 8: Client spawns remotely-controlled object (projectiles with server authority)
            // These participants have OwnerAuthorityId = server, so they won't be caught by Path 5 (IsRelatedToThisLocality fails)
            // The spawning client needs OnGONetReady even though they don't own it
            // CRITICAL: Require server's GONetLocal to be present - this ensures proper initialization synchronization
            // The server's GONetLocal is now properly sent to all clients via FilterPersistentEventsByLoadedScenes fix
            if (IsClient &&
                GONetSpawnSupport_Runtime.IsMarkedToBeRemotelyControlled(gonetParticipant) &&
                IsGONetReady(gonetParticipant))
            {
                // Deduplication check: Only publish if not already published
                if (TryMarkDeserializeInitPublished(gonetParticipant.GONetId))
                {
                    //GONetLog.Info($"[GONet] Publishing DeserializeInitAllCompleted for client-spawned remotely-controlled '{gonetParticipant.name}' (GONetId: {gonetParticipant.GONetId}, OwnerAuthorityId: {gonetParticipant.OwnerAuthorityId}) from Start path");
                    var deserializeInitEvent = new GONetParticipantDeserializeInitAllCompletedEvent(gonetParticipant);
                    PublishEventAsSoonAsSufficientInfoAvailable(deserializeInitEvent, gonetParticipant, isRelatedLocalContentRequired: true); // Wait for server GONetLocal - required for proper initialization
                }
                else
                {
                    //GONetLog.Info($"[GONet] Skipping duplicate DeserializeInitAllCompleted for client-spawned remotely-controlled '{gonetParticipant.name}' (GONetId: {gonetParticipant.GONetId}) - already published from another path");
                }
            }
        }

        /// <summary>
        /// PRE: Already known that <paramref name="gonetParticipant"/> has <see cref="GONetParticipant.IsMine_ToRemotelyControl"/> true.
        /// PRE: <see cref="MyAuthorityId"/> is set to final value and is not <see cref="OwnerAuthorityId_Unset"/> in case it is needed as a fallback (i.e., when not enough values in id batch from server).
        ///
        /// TODO: look into calling this method inside of <see cref="Client_InstantiateToBeRemotelyControlledByMe(GONetParticipant, Vector3, Quaternion)"/> instead of where it is called from now...this would allow for the final GONetId to be set/known immediately!
        /// </summary>
        private static void Client_DoAutoPropogateInstantiationPrep_RemotelyControlled(GONetParticipant gonetParticipant)
        {
            // Just set the authority - the actual ID allocation happens in GetNextAvailableGONetIdRaw
            // to avoid double-counting (allocating here and using there)
            gonetParticipant.OwnerAuthorityId = OwnerAuthorityId_Server;
        }

        /// <summary>
        /// PRE: <paramref name="event"/> must also implement <see cref="IHaveRelatedGONetId"/>.
        /// Sufficient Info: 
        /// -GONetId has all components (i.e., <see cref="GONetParticipant.DoesGONetIdContainAllComponents()"/>
        /// -if <paramref name="isRelatedLocalContentRequired"/> true, then <see cref="GONetLocal.LookupByAuthorityId"/> for <paramref name="gonetParticipant"/>'s <see cref="GONetParticipant.OwnerAuthorityId"/> is not default
        /// </summary>
        private static void PublishEventAsSoonAsSufficientInfoAvailable(IGONetEvent @event, GONetParticipant gonetParticipant, bool isRelatedLocalContentRequired = false)
        {
            if (!((object)@event is IHaveRelatedGONetId))
            {
                throw new ArgumentException("Argument must an event that implements IHaveRelatedGONetId for this to make any sense and work....the way the event classes/interfaces was implemented causes this unsightly inability to just use IHaveRelatedGONetId as the param type, but do it!", nameof(@event));
            }

            if (gonetParticipant.DoesGONetIdContainAllComponents() && gonetParticipantByGONetIdMap[gonetParticipant.GONetId] == gonetParticipant
                && (!isRelatedLocalContentRequired || GONetLocal.LookupByAuthorityId[gonetParticipant.OwnerAuthorityId] != default))
            {
                //GONetLog.Debug($"publishing event of type: {@event.GetType().Name}");
                EventBus.Publish<IGONetEvent>(@event);
            }
            else
            {
                //GONetLog.Debug($"MAYBE publish later once all info avail...event of type: {@event.GetType().Name}");
                GlobalSessionContext_Participant.StartCoroutine(PublishEventAsSoonAsGONetIdAssigned_Coroutine(@event, gonetParticipant, isRelatedLocalContentRequired));
            }
        }

        /// <summary>
        /// PRE: <paramref name="event"/> must also implement <see cref="IHaveRelatedGONetId"/>.
        /// This method should only ever be called on a client and as a result of having an event ready to go (e.g., <see cref="GONetParticipantStartedEvent"/> or <see cref="GONetParticipantEnabledEvent"/>)
        /// but since the associated <see cref="GONetParticipant"/> was defined in a unity scene and since the server will assign its <see cref="GONetParticipant.GONetId"/> and this client
        /// will get it momentarily after this initialization causing this event to be raised is processed...we need a mechanism to postpone the event publish until gonetid assigned so the
        /// event publish process of placing into an envelope with a reference to the actual GNP will find the GNP since the proper gonetid is known.
        /// </summary>
        private static IEnumerator PublishEventAsSoonAsGONetIdAssigned_Coroutine(IGONetEvent @event, GONetParticipant gonetParticipant, bool isRelatedLocalContentRequired = true)
        {
            // TODO [PERF] don't create a coroutine per event like this...just throw in a collection and check/process on a frequency elsewhere
            GONetParticipant mappedGNP;
            while (
                !gonetParticipant.DoesGONetIdContainAllComponents() ||
                !gonetParticipantByGONetIdMap.TryGetValue(gonetParticipant.GONetId, out mappedGNP) ||
                mappedGNP != gonetParticipant ||
                (isRelatedLocalContentRequired && GONetLocal.LookupByAuthorityId[gonetParticipant.OwnerAuthorityId] == default))
            {
                //GONetLog.Debug($"still waiting for all info to publish event of type: {@event.GetType().Name}.  gnp.idAll? {gonetParticipant.DoesGONetIdContainAllComponents()} key? {gonetParticipantByGONetIdMap.ContainsKey(gonetParticipant.GONetId)} req? {isRelatedLocalContentRequired}");
                yield return null;
            }

            //GONetLog.Debug($"done waiting for all info to publish event of type: {@event.GetType().Name} gonetId: {gonetParticipant.GONetId}");
            ((IHaveRelatedGONetId)@event).GONetId = gonetParticipant.GONetId;
            EventBus.Publish<IGONetEvent>(@event);
        }

        private static void AssignGONetIdRaw_IfAppropriate(GONetParticipant gonetParticipant, bool shouldForceChangeEventIfAlreadySet = false)
        {
            if (shouldForceChangeEventIfAlreadySet || gonetParticipant.gonetId_raw == GONetParticipant.GONetId_Unset) // TODO need to avoid this when this guy is coming from replay too! gonetParticipant.WasInstantiated true is all we have now...will have WasFromReplay later
            {
                if (lastAssignedGONetIdRaw < GONetParticipant.GONetId_Raw_MaxValue)
                {
                    uint gonetId_raw = GetNextAvailableGONetIdRaw(gonetParticipant);
                    gonetParticipant.GONetId = (gonetId_raw << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED) | gonetParticipant.OwnerAuthorityId;

                    // Track GONetId assignment in lifecycle
                    Core.SoA_LifecycleTracker.OnGONetIdAssigned(gonetParticipant.GONetId, gonetParticipant.name, gonetParticipant.OwnerAuthorityId);

                    // LIFECYCLE GATE: GONetId assigned - check if OnGONetReady can fire
                    CheckAndPublishOnGONetReady_IfAllConditionsMet(gonetParticipant);
                }
                else
                {
                    throw new OverflowException("Unable to assign a new GONetId, because lastAssignedGONetId has reached the max value of GONetParticipant.GONetId_Raw_MaxValue, which is: " + GONetParticipant.GONetId_Raw_MaxValue);
                }
            }
        }

        private static uint GetNextAvailableGONetIdRaw(GONetParticipant gonetParticipant)
        {
            // CLIENT: Use batch manager for remotely-controlled spawns
            // HOST MODE FIX: If we're the server, we don't need batch IDs - server assigns IDs directly.
            // Without this check, host mode would try to use client batch allocation for server-owned objects.
            if (IsClient && !IsServer && gonetParticipant.OwnerAuthorityId == OwnerAuthorityId_Server)
            {
                uint batchId;
                bool shouldRequestNewBatch;

                // GONetId Reuse Prevention: Loop until we find a batch ID that's not recently despawned
                int reusePrevention_attemptCount = 0;
                const int MAX_REUSE_PREVENTION_ATTEMPTS = 200; // Should never need this many, but prevent infinite loop

                do
                {
                    bool success = GONetIdBatchManager.Client_TryAllocateNextId(out batchId, out shouldRequestNewBatch);

                    if (!success)
                    {
                        // CRITICAL: This should NEVER be reached if using Client_TryInstantiate API correctly
                        // The dangerous fallback code has been removed and replaced with limbo mode system
                        // If you hit this exception, you are:
                        // 1. Using Client_InstantiateToBeRemotelyControlledByMe (old API) during batch exhaustion, OR
                        // 2. Calling Instantiate_MarkToBeRemotelyControlled directly (internal API - don't do this)
                        //
                        // SOLUTION: Use Client_TryInstantiateToBeRemotelyControlledByMe instead
                        // This will handle batch exhaustion gracefully via limbo mode
                        throw new InvalidOperationException(
                            "[GONetIdBatch] CRITICAL: No batch IDs available for client spawn! " +
                            "This means you're using the OLD API during batch exhaustion. " +
                            "REQUIRED FIX: Replace Client_InstantiateToBeRemotelyControlledByMe() with Client_TryInstantiateToBeRemotelyControlledByMe(). " +
                            "The Try version handles batch exhaustion via limbo mode. " +
                            $"Current state: {GONetIdBatchManager.Client_GetDiagnostics()}");
                    }

                    // Compose GONetId to check reuse eligibility (client-spawned objects get server authority)
                    uint composedGONetId = unchecked((uint)(batchId << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED)) | OwnerAuthorityId_Server;

                    if (CanReuseGONetId(composedGONetId))
                    {
                        // This ID is safe to use - not recently despawned
                        if (shouldRequestNewBatch)
                        {
                            Client_RequestNewGONetIdBatch();
                        }
                        return batchId;
                    }

                    // This ID is recently despawned - skip it and try next one
                    // (CanReuseGONetId already logged warning about skipping)
                    reusePrevention_attemptCount++;

                } while (reusePrevention_attemptCount < MAX_REUSE_PREVENTION_ATTEMPTS);

                // If we exhausted attempts, something is very wrong
                GONetLog.Error($"[GONetId-Reuse] CRITICAL: Exhausted {MAX_REUSE_PREVENTION_ATTEMPTS} batch IDs - all recently despawned! " +
                              $"This should NEVER happen. Batch size: {GONetIdBatchManager.Client_GetDiagnostics()}. " +
                              $"Using potentially unsafe ID: {batchId}");
                return batchId; // Return last attempted ID as fallback (better than crash)
            }

            // SERVER or CLIENT (non-remotely-controlled): Regular ID assignment
            ++lastAssignedGONetIdRaw;

            // CRITICAL: Skip reserved GONetGlobal raw ID (always 1)
            // GONetGlobal uses a hardcoded ID to ensure client/server consistency
            if (lastAssignedGONetIdRaw == GONetParticipant.GONetGlobal_GONetId_Raw)
            {
                ++lastAssignedGONetIdRaw;
            }

            if (IsServer)
            {
                // Skip any IDs that fall within client batches
                while (GONetIdBatchManager.Server_IsIdInAnyBatch(lastAssignedGONetIdRaw))
                {
                    ++lastAssignedGONetIdRaw;
                }

                // GONetId Reuse Prevention: Server-assigned IDs also need reuse checking
                uint composedGONetId_server = unchecked((uint)(lastAssignedGONetIdRaw << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED)) | OwnerAuthorityId_Server;
                while (!CanReuseGONetId(composedGONetId_server))
                {
                    ++lastAssignedGONetIdRaw;

                    // Skip reserved GONetGlobal ID (also inside reuse prevention loop)
                    if (lastAssignedGONetIdRaw == GONetParticipant.GONetGlobal_GONetId_Raw)
                    {
                        ++lastAssignedGONetIdRaw;
                    }

                    // Re-check batch collision after increment
                    while (GONetIdBatchManager.Server_IsIdInAnyBatch(lastAssignedGONetIdRaw))
                    {
                        ++lastAssignedGONetIdRaw;
                    }

                    composedGONetId_server = unchecked((uint)(lastAssignedGONetIdRaw << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED)) | OwnerAuthorityId_Server;
                }
            }
            else // CLIENT (for client-owned objects)
            {
                // CRITICAL FIX (December 2025): Skip any IDs that fall within batches allocated to this client.
                // Without this, client-owned objects could use the same raw ID as server-owned objects
                // from batches, causing sync failures (same raw ID with different owner bits).
                while (GONetIdBatchManager.Client_IsIdInAnyBatch(lastAssignedGONetIdRaw))
                {
                    ++lastAssignedGONetIdRaw;
                }
            }

            return lastAssignedGONetIdRaw;
        }

        /// <summary>
        /// SERVER ONLY: Allocate the next available raw GONetId for pooled objects.
        /// Mirrors server-side logic from <see cref="GetNextAvailableGONetIdRaw"/> without needing a participant instance.
        /// </summary>
        internal static uint AllocateNextServerGONetIdRaw()
        {
            if (!IsServer)
            {
                GONetLog.Warning("[POOL] AllocateNextServerGONetIdRaw called on non-server.");
            }

            if (lastAssignedGONetIdRaw >= GONetParticipant.GONetId_Raw_MaxValue)
            {
                throw new OverflowException("Unable to assign a new GONetIdRaw, because lastAssignedGONetIdRaw has reached max.");
            }

            ++lastAssignedGONetIdRaw;

            if (lastAssignedGONetIdRaw == GONetParticipant.GONetGlobal_GONetId_Raw)
            {
                ++lastAssignedGONetIdRaw;
            }

            while (GONetIdBatchManager.Server_IsIdInAnyBatch(lastAssignedGONetIdRaw))
            {
                ++lastAssignedGONetIdRaw;
            }

            uint composedGONetId = unchecked((uint)(lastAssignedGONetIdRaw << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED)) | OwnerAuthorityId_Server;
            while (!CanReuseGONetId(composedGONetId))
            {
                ++lastAssignedGONetIdRaw;

                if (lastAssignedGONetIdRaw == GONetParticipant.GONetGlobal_GONetId_Raw)
                {
                    ++lastAssignedGONetIdRaw;
                }

                while (GONetIdBatchManager.Server_IsIdInAnyBatch(lastAssignedGONetIdRaw))
                {
                    ++lastAssignedGONetIdRaw;
                }

                composedGONetId = unchecked((uint)(lastAssignedGONetIdRaw << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED)) | OwnerAuthorityId_Server;
            }

            return lastAssignedGONetIdRaw;
        }

        /// <summary>
        /// Assigns a specific GONetId to a participant directly (used for syncing scene-defined objects from server to client).
        /// </summary>
        internal static void AssignGONetIdRaw_Direct(GONetParticipant gonetParticipant, uint gonetId)
        {
            gonetParticipant.GONetId = gonetId;
            // REMOVED: Excessive logging (810 logs/frame) - GONetLog.Debug($"[GONetId] Directly assigned GONetId {gonetId} to '{gonetParticipant.gameObject.name}'");

            // Track GONetId assignment in lifecycle
            Core.SoA_LifecycleTracker.OnGONetIdAssigned(gonetId, gonetParticipant.name, gonetParticipant.OwnerAuthorityId);

            EnsureAutoMagicalSyncCompanionRegistered(gonetParticipant, "GONetId assignment");

            // LIFECYCLE GATE: GONetId assigned - check if OnGONetReady can fire
            CheckAndPublishOnGONetReady_IfAllConditionsMet(gonetParticipant);
        }

        /// <summary>
        /// Finds a GONetParticipant by its design-time location within a specific scene.
        /// </summary>
        internal static GONetParticipant FindParticipantByDesignTimeLocation(string designTimeLocation, string sceneName)
        {
            // Search all GONetParticipants in the scene (including those without GONetIds assigned yet)
            GONetParticipant[] allParticipants = UnityEngine.Object.FindObjectsOfType<GONetParticipant>();

            foreach (GONetParticipant participant in allParticipants)
            {
                if (participant == null)
                {
                    continue;
                }

                bool matchesDesignTime = participant.IsDesignTimeMetadataInitd &&
                                         participant.DesignTimeLocation == designTimeLocation;
                bool matchesCachedPath = !string.IsNullOrEmpty(participant.fullUniquePathInSceneAtAwake) &&
                                         participant.fullUniquePathInSceneAtAwake == designTimeLocation;

                if (!matchesDesignTime && !matchesCachedPath)
                {
                    continue;
                }

                if (matchesCachedPath || string.IsNullOrEmpty(sceneName))
                {
                    return participant;
                }

                // Verify it's in the correct scene
                string participantScene = GONetSceneManager.GetSceneIdentifier(participant.gameObject);
                if (participantScene == sceneName)
                {
                    return participant;
                }
            }

            return null;
        }

        private static void AutoPropagateInitialInstantiation(GONetParticipant gonetParticipant)
        {
            // CRITICAL: Ensure design-time metadata is initialized BEFORE creating the spawn event
            // This prevents the "TON CLEETLE!" error when DesignTimeLocation is accessed
            // The metadata must be initialized synchronously here because:
            // 1. GONetParticipant.Awake() initializes metadata in a coroutine (async)
            // 2. Start() is called before the coroutine completes
            // 3. We need DesignTimeLocation populated in the spawn event NOW
            InitDesignTimeMetadata_IfNeeded(gonetParticipant);

            InstantiateGONetParticipantEvent @event;

            string nonAuthorityDesignTimeLocation;
            if (GONetSpawnSupport_Runtime.TryGetNonAuthorityDesignTimeLocation(gonetParticipant, out nonAuthorityDesignTimeLocation))
            {
                @event = InstantiateGONetParticipantEvent.Create_WithNonAuthorityInfo(gonetParticipant, nonAuthorityDesignTimeLocation);
            }
            else if (GONetSpawnSupport_Runtime.IsMarkedToBeRemotelyControlled(gonetParticipant))
            {
                @event = InstantiateGONetParticipantEvent.Create_WithRemotelyControlledByInfo(gonetParticipant);
            }
            else
            {
                @event = InstantiateGONetParticipantEvent.Create(gonetParticipant);
            }

            // DIAGNOSTIC (December 2025): Log spawn event publication for client-spawned server-owned objects
            bool isClientSpawnedServerOwned = IsClient && GONetSpawnSupport_Runtime.IsMarkedToBeRemotelyControlled(gonetParticipant);
            if (isClientSpawnedServerOwned)
            {
                //GONetLog.Debug($"[SPAWN-PROPAGATE] CLIENT publishing spawn event: GONetId={gonetParticipant.GONetId}, name='{gonetParticipant.name}', ImmediatelyRelinquish={@event.ImmediatelyRelinquishAuthorityToServer_AndTakeRemoteControlAuthority}");
            }

            // DIAGNOSTIC: Log GONetLocal spawn event
            bool isGONetLocal = gonetParticipant.GetComponent<GONetLocal>() != null;
            if (isGONetLocal && GONetConfig.LogSpawnDiagnostics)
            {
                GONetLog.Debug($"[SPAWN-DIAG] AutoPropagateInitialInstantiation PUBLISHING GONetLocal spawn - GONetId: {@event.GONetId}, DesignTimeLocation: {@event.DesignTimeLocation}");
            }

            EventBus.Publish(@event); // this causes the auto propagation via local handler to send to all remotes (i.e., all clients if server, server if client)

            if (isGONetLocal && IsClient && !IsServer && gonetParticipant.IsMine)
            {
                ScheduleGONetLocalSpawnRetry(@event, gonetParticipant.OwnerAuthorityId);
            }

            gonetParticipant.IsOKToStartAutoMagicalProcessing = true; // VERY IMPORTANT that this comes AFTER publishing the event so the flood gates to start syncing data come AFTER other parties are made aware of the GNP in the above event!
        }

        private static void ScheduleGONetLocalSpawnRetry(InstantiateGONetParticipantEvent spawnEvent, ushort ownerAuthorityId)
        {
            if (GlobalSessionContext_Participant == null)
            {
                return;
            }

            if (isGONetLocalSpawnRetryActive && gonetLocalSpawnRetryGONetId == spawnEvent.GONetId)
            {
                return;
            }

            isGONetLocalSpawnRetryActive = true;
            gonetLocalSpawnRetryGONetId = spawnEvent.GONetId;
            GlobalSessionContext_Participant.StartCoroutine(RetryGONetLocalSpawn_Coroutine(spawnEvent, ownerAuthorityId));
        }

        private static IEnumerator RetryGONetLocalSpawn_Coroutine(InstantiateGONetParticipantEvent spawnEvent, ushort ownerAuthorityId)
        {
            float delaySeconds = GONETLOCAL_SPAWN_RETRY_INITIAL_DELAY_SECONDS;

            for (int attempt = 1; attempt <= GONETLOCAL_SPAWN_RETRY_MAX_ATTEMPTS; attempt++)
            {
                yield return new WaitForSeconds(delaySeconds);

                if (IsServer || GONetClient == null || !GONetClient.IsConnectedToServer)
                {
                    break;
                }

                spawnEvent.OccurredAtElapsedTicks = Time.ElapsedTicks;
                if (GONetConfig.LogSpawnDiagnostics)
                {
                    GONetLog.Debug($"[SPAWN-DIAG] Retrying GONetLocal spawn (attempt {attempt}/{GONETLOCAL_SPAWN_RETRY_MAX_ATTEMPTS}) " +
                                     $"- GONetId={spawnEvent.GONetId}, OwnerAuth={ownerAuthorityId}");
                }
                EventBus.Publish(spawnEvent);

                delaySeconds = Math.Min(delaySeconds * 2f, GONETLOCAL_SPAWN_RETRY_MAX_DELAY_SECONDS);
            }

            isGONetLocalSpawnRetryActive = false;
        }

        /// <summary>
        /// Determines if a GONetParticipant is being destroyed as a result of scene unloading.
        /// <para><b>Detection Logic:</b></para>
        /// <list type="bullet">
        /// <item>Checks AutoDontDestroyOnLoad flag first (most reliable)</item>
        /// <item>Falls back to runtime scene detection if flag not set</item>
        /// <item>Returns FALSE if object is in DontDestroyOnLoad scene (these objects survive scene unloads)</item>
        /// <item>Returns TRUE if application is quitting (everything being destroyed)</item>
        /// <item>Returns TRUE if object's scene is not loaded or is unloading</item>
        /// <item>Returns FALSE otherwise (true gameplay despawn)</item>
        /// </list>
        /// </summary>
        /// <param name="gonetParticipant">The GONetParticipant being destroyed</param>
        /// <returns>True if destruction is from scene unload, false if it's an intentional gameplay despawn</returns>
        private static bool IsDestroyFromSceneUnload(GONetParticipant gonetParticipant)
        {
            if (IsApplicationQuitting)
            {
                return true; // Application quitting - not a gameplay despawn
            }

            // Primary check: AutoDontDestroyOnLoad flag (most reliable)
            if (gonetParticipant.AutoDontDestroyOnLoad)
            {
                return false; // This object is marked as DDOL, so it's a true gameplay despawn
            }

            Scene objectScene = gonetParticipant.gameObject.scene;

            // Fallback: Runtime detection of DontDestroyOnLoad scene
            // This catches cases where users manually called DontDestroyOnLoad without setting the flag
            if (GONetSceneManager.IsDontDestroyOnLoad(gonetParticipant.gameObject))
            {
                return false; // True gameplay despawn (DontDestroyOnLoad objects aren't affected by scene unloads)
            }

            // Check if the object's scene is unloading or unloaded
            if (!objectScene.isLoaded)
            {
                return true; // Scene is unloaded - destruction is from scene lifecycle
            }

            // Check GONet's scene manager for scene unloading state
            if (SceneManager != null)
            {
                string sceneName = GONetSceneManager.GetSceneIdentifier(gonetParticipant.gameObject);
                if (!string.IsNullOrEmpty(sceneName) && SceneManager.IsSceneUnloading(sceneName))
                {
                    return true; // Scene is actively unloading
                }
            }

            return false; // None of the scene-unload conditions met - this is a true gameplay despawn
        }

        internal static void OnDestroy_AutoPropagateRemoval_IfAppropriate(GONetParticipant gonetParticipant)
        {
            if (Application.isPlaying)
            {
                if (gonetParticipant.isPooled)
                {
                    if (gonetParticipant.isPoolDestructionInProgress || gonetParticipant.isPoolReturnInProgress)
                    {
                        return;
                    }

                    if (IsDestroyFromSceneUnload(gonetParticipant))
                    {
                        return;
                    }

                    if (IsServer)
                    {
                        GONetLog.Error($"[POOL] Destroy called on pooled object '{gonetParticipant.name}' (GONetId {gonetParticipant.GONetId}).");
                        GONetPoolManager.Server_HandlePooledObjectDestroyed(gonetParticipant, PoolObjectDestroyedReason.DestroyCalled);
                    }
                    else
                    {
                        GONetLog.Error($"[POOL] Pooled object destroyed on client '{gonetParticipant.name}' (GONetId {gonetParticipant.GONetId}).");
                    }

                    return;
                }

                // When a destroy is triggered due to remote propagation (or an explicit failover reconciliation),
                // the receiver marks the id before calling Destroy(). In those cases we must NOT auto-propagate
                // another despawn event from OnDestroy().
                if (gonetIdsDestroyedViaPropagation.Contains(gonetParticipant.GONetId))
                {
                    return;
                }

                if (IsMine(gonetParticipant) || (IsServer && !Server_IsClientOwnerConnected(gonetParticipant)))
                {
                    // Determine if this is a true gameplay despawn or scene unload destruction
                    bool isSceneUnloadDestroy = IsDestroyFromSceneUnload(gonetParticipant);

                    if (!isSceneUnloadDestroy)
                    {
                        // True gameplay despawn: Send despawn event over network
                        AutoPropagateDespawn(gonetParticipant);
                    }
                    // else: Scene unload - don't send any event (coordinated via GONetSceneManager)
                }
                else
                {
                    // Check if this is a scene unload destroy (Unity automatically destroys all objects in unloading scenes)
                    bool isSceneUnloadDestroy = IsDestroyFromSceneUnload(gonetParticipant);

                    bool isExpected =
                        gonetIdsDestroyedViaPropagation.Contains(gonetParticipant.GONetId) ||
                        (IsClient && IsApplicationQuitting) ||
                        isSceneUnloadDestroy; // Scene unload destroys all objects - this is expected

                    if (!isExpected)
                    {
                        const string NOD = "GONetParticipant being destroyed and IsMine is false, which means the only other GONet-approved reason this should be destroyed is through automatic propagation over the network as a response to the owner destroying it OR a client just closed out; HOWEVER, that is not the case right now and the ASSumption is that you inadvertantly called UnityEngine.Object.Destroy() on something not owned by you.  GONetId: ";
                        GONetLog.Warning(string.Concat(NOD, gonetParticipant.GONetId));
                    }
                }
            }
        }

        /// <summary>
        /// Checks if the owner of a GONetParticipant is connected to the server.
        /// <para><b>IMPORTANT:</b> This method should only be called on the server.</para>
        /// <para>For server-owned objects (OwnerAuthorityId = 1023), this always returns true
        /// since the server is inherently "connected to itself".</para>
        /// </summary>
        /// <param name="gonetParticipant">The GONetParticipant to check.</param>
        /// <returns>True if the owner is connected (or if the object is server-owned), false otherwise.</returns>
        public static bool Server_IsClientOwnerConnected(GONetParticipant gonetParticipant)
        {
            // Server-owned objects (OwnerAuthorityId = 1023) are always "connected" -
            // the server is inherently connected to itself
            if (gonetParticipant.OwnerAuthorityId == OwnerAuthorityId_Server)
            {
                return true;
            }
            // Lossless handoff: treat the outgoing host as connected while we wait for its standby reconnect.
            if (GONetHostHandoffManager.Instance.TryGetPendingOutgoingHostAuthorityId(out ushort pendingOutgoingAuthorityId) &&
                gonetParticipant.OwnerAuthorityId == pendingOutgoingAuthorityId)
            {
                return true;
            }
            return gonetServer.TryGetRemoteClientByAuthorityId(gonetParticipant.OwnerAuthorityId, out _);
        }

        /// <summary>
        /// Publishes a <see cref="DespawnGONetParticipantEvent"/> for an intentional gameplay despawn.
        /// <para><b>PRE:</b> <paramref name="gonetParticipant"/> is owned by me.</para>
        /// <para>This is used when a GONetParticipant is destroyed through gameplay logic
        /// (e.g., projectile hits, enemy dies, player destroys object), NOT from scene unloading.</para>
        /// </summary>
        /// <param name="gonetParticipant">The GONetParticipant being despawned</param>
        private static void AutoPropagateDespawn(GONetParticipant gonetParticipant)
        {
            if (gonetParticipant.GONetId == GONetParticipant.GONetId_Unset)
            {
                const string NOID = "GONetParticipant that I own was despawned, but it has not been assigned a GONetId yet. Unable to propagate the despawn to others. GameObject.name: ";
                GONetLog.Error(string.Concat(NOID, gonetParticipant.gameObject.name));
                return;
            }

            DespawnGONetParticipantEvent @event = new DespawnGONetParticipantEvent() { GONetId = gonetParticipant.GONetId };
            //GONetLog.Warning($"[DESPAWN_SYNC] Publishing DespawnGONetParticipantEvent for GONetId {gonetParticipant.GONetId}, GameObject: '{gonetParticipant.gameObject.name}'");
            EventBus.Publish(@event);
        }

        static readonly HashSet<int> definedInSceneParticipantInstanceIDs = new HashSet<int>();

        /// <summary>
        /// Maps GONetParticipant instance IDs to the scene name they were spawned in or loaded with.
        /// Used for scene-based spawn tracking and late-joiner synchronization.
        /// </summary>
        static readonly Dictionary<int, string> participantInstanceID_to_SpawnSceneName = new Dictionary<int, string>();

        /// <summary>
        /// Tracks which clients are currently receiving a full AllCurrentValues sync.
        /// Prevents duplicate sync coroutines from piling up for the same client.
        /// </summary>
        static readonly HashSet<ushort> fullStateSyncInProgress = new HashSet<ushort>();

        /// <summary>
        /// Throttles repeated full state sync requests per client.
        /// </summary>
        private const float FULL_STATE_SYNC_REQUEST_THROTTLE_SECONDS = 5.0f;

        /// <summary>
        /// Last time a full state sync was requested for a client (raw ticks).
        /// </summary>
        static readonly Dictionary<ushort, long> lastFullStateSyncRequestRawTicks = new Dictionary<ushort, long>();

        /// <summary>
        /// Throttles client-side retry requests for full state sync after AllValues failures.
        /// </summary>
        private const float FULL_STATE_SYNC_RETRY_THROTTLE_SECONDS = 5.0f;

        /// <summary>
        /// Last time this client requested a full state sync retry (raw ticks).
        /// </summary>
        private static long lastFullStateSyncRetryRawTicks;

        /// <summary>
        /// Queue of spawn events waiting for their required scene to be loaded.
        /// <para>When a client receives a spawn for a scene they haven't loaded yet,
        /// the spawn is queued here and processed when the scene loads.</para>
        /// </summary>
        static readonly List<InstantiateGONetParticipantEvent> deferredSpawnEvents = new List<InstantiateGONetParticipantEvent>();

        /// <summary>
        /// Despawn events that arrived while spawns were deferred. These must be processed AFTER the deferred spawns complete.
        /// </summary>
        static readonly List<DespawnGONetParticipantEvent> deferredDespawnEvents = new List<DespawnGONetParticipantEvent>();

        private const int GONETLOCAL_SPAWN_RETRY_MAX_ATTEMPTS = 5;
        private const float GONETLOCAL_SPAWN_RETRY_INITIAL_DELAY_SECONDS = 0.5f;
        private const float GONETLOCAL_SPAWN_RETRY_MAX_DELAY_SECONDS = 5f;
        private static bool isGONetLocalSpawnRetryActive;
        private static uint gonetLocalSpawnRetryGONetId;

        internal static readonly ConcurrentDictionary<ushort, byte> serverReceivedGONetLocalSpawnAuthorities = new ConcurrentDictionary<ushort, byte>();

        /// <summary>
        /// Holds a deferred AllValues bundle that needs to be processed after spawns are complete.
        /// </summary>
        private struct DeferredAllValuesBundle
        {
            public byte[] RawBytes;
            public int BytesUsedCount;
            public GONetConnection RelatedConnection;
            public long ElapsedTicksAtSend;
            public string RequiredSceneName;
            public int RetryCount;
            public long FirstDeferralRawTicks;
        }

        /// <summary>
        /// Deferred AllValues bundles waiting for scene to be ready.
        /// <para>When a client receives AllValues bundles before scene is loaded or spawns are processed,
        /// they're queued here and processed after the scene is ready.</para>
        /// <para>CRITICAL: Changed from single bundle to List (November 2025) - under high load (810 GNPs),
        /// hundreds of AllValues bundles arrive during scene loading. Single-bundle storage was overwriting
        /// previous bundles, losing initialization data for all but the last bundle.</para>
        /// </summary>
        static List<DeferredAllValuesBundle> deferredAllValuesBundles = new List<DeferredAllValuesBundle>();

        /// <summary>
        /// Diagnostic counter for tracing AllValues bundle processing. Each bundle gets a unique sequential number.
        /// </summary>

        /// <summary>
        /// CRITICAL FIX (November 2025): Late-joiner initialization state tracking.
        /// Tracks expected AllValues bundle count for deterministic initialization completion.
        /// -1 = unknown/not set, 0+ = expected count from server.
        /// </summary>
        static int expectedAllValuesBundlesForScene = -1;

        /// <summary>
        /// Count of AllValues bundles received during late-joiner initialization.
        /// Used with expectedAllValuesBundlesForScene to know when initialization is complete.
        /// </summary>
        static int receivedAllValuesBundlesForLateJoinerInit = 0;

        /// <summary>
        /// Scene name for current late-joiner initialization.
        /// Used to match bundles to the scene being initialized.
        /// </summary>
        static string lateJoinerInitSceneName = "";

        /// <summary>
        /// Time when last AllValues bundle was received.
        /// Used for timeout-based fallback if expected count is never reached.
        /// </summary>
        static float timeOfLastAllValuesBundle = 0;

        /// <summary>
        /// Timeout in seconds after last AllValues bundle before forcing deferred bundle processing.
        /// Fallback mechanism if expected count is wrong or never set.
        /// </summary>
        const float ALLVALUES_BATCH_TIMEOUT = 5.0f;

        /// <summary>
        /// Safety limits to avoid infinite AllValues bundle re-deferral loops.
        /// </summary>
        const int DEFERRED_ALLVALUES_MAX_RETRY_COUNT = 3600;
        const float DEFERRED_ALLVALUES_MAX_TTL_SECONDS = 60.0f;

        /// <summary>
        /// Checks if a scene is currently loaded.
        /// </summary>
        private static bool IsSceneCurrentlyLoaded(string sceneIdentifier)
        {
            // DontDestroyOnLoad is always "loaded"
            if (sceneIdentifier == HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE)
                return true;

            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene.name == sceneIdentifier && scene.isLoaded)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryReDeferAllValuesBundle(DeferredAllValuesBundle bundle, string sceneName, Exception ex)
        {
            long nowTicks = HighResolutionTimeUtils.UtcNowTicks;
            if (bundle.FirstDeferralRawTicks == 0L)
            {
                bundle.FirstDeferralRawTicks = nowTicks;
            }

            bundle.RetryCount++;

            long elapsedTicks = nowTicks - bundle.FirstDeferralRawTicks;
            if (elapsedTicks < 0)
            {
                elapsedTicks = 0;
            }

            long maxTtlTicks = (long)(DEFERRED_ALLVALUES_MAX_TTL_SECONDS * TimeSpan.TicksPerSecond);
            double elapsedSeconds = elapsedTicks * HighResolutionTimeUtils.TICKS_TO_SECONDS;
            if (bundle.RetryCount > DEFERRED_ALLVALUES_MAX_RETRY_COUNT || elapsedTicks > maxTtlTicks)
            {
                GONetLog.Error($"[DEFERRED-EXPIRED] Dropping AllValues bundle for scene '{sceneName}' after {bundle.RetryCount} retries/{elapsedSeconds:F2}s ({ex.GetType().Name}: {ex.Message})");
                RequestFullStateSyncRetryIfNeeded($"Deferred AllValues expired for scene '{sceneName}'");
                return false;
            }

            deferredAllValuesBundles.Add(bundle);
            timeOfLastAllValuesBundle = UnityEngine.Time.time;
            GONetLog.Warning($"[DEFERRED-RETRY] Re-deferred AllValues bundle for scene '{sceneName}' (retry {bundle.RetryCount}/{DEFERRED_ALLVALUES_MAX_RETRY_COUNT}, age {elapsedSeconds:F2}s) ({ex.GetType().Name}: {ex.Message}). Pending: {deferredAllValuesBundles.Count}");
            return true;
        }

        /// <summary>
        /// Processes any deferred spawn events that were waiting for a scene to load.
        /// Called when a scene finishes loading.
        /// </summary>
        internal static void ProcessDeferredSpawnsForScene(string sceneName)
        {
            // CRITICAL: Always log this call to diagnose AllValues bundle processing issues
            //GONetLog.Warning($"[DEFERRED-PROCESSING] ========================================");
            //GONetLog.Warning($"[DEFERRED-PROCESSING] ProcessDeferredSpawnsForScene CALLED for '{sceneName}'");
            //GONetLog.Warning($"[DEFERRED-PROCESSING] DeferredSpawns={deferredSpawnEvents.Count}, DeferredAllValuesBundles={deferredAllValuesBundles.Count}");

            // Log all deferred bundle scene names
            if (deferredAllValuesBundles.Count > 0)
            {
                //GONetLog.Warning($"[DEFERRED-PROCESSING] Deferred bundles scene names:");
                for (int i = 0; i < deferredAllValuesBundles.Count; i++)
                {
                    //GONetLog.Warning($"[DEFERRED-PROCESSING]   Bundle {i}: RequiredSceneName='{deferredAllValuesBundles[i].RequiredSceneName}'");
                }
            }

            if (deferredSpawnEvents.Count == 0 && deferredAllValuesBundles.Count == 0)
            {
                //GONetLog.Warning($"[DEFERRED-PROCESSING] No deferred spawns or AllValues bundles to process for '{sceneName}' - early return");
                //GONetLog.Warning($"[DEFERRED-PROCESSING] ========================================");
                return;
            }

            List<InstantiateGONetParticipantEvent> toProcess = new List<InstantiateGONetParticipantEvent>();

            // Find all spawns that were waiting for this scene
            for (int i = deferredSpawnEvents.Count - 1; i >= 0; i--)
            {
                InstantiateGONetParticipantEvent spawnEvent = deferredSpawnEvents[i];
                if (spawnEvent.SceneIdentifier == sceneName)
                {
                    //GONetLog.Debug($"[SPAWN_SYNC] Found deferred spawn for GONetId {spawnEvent.GONetId} matching scene '{sceneName}'");
                    toProcess.Add(spawnEvent);
                    deferredSpawnEvents.RemoveAt(i);
                }
            }

            if (toProcess.Count > 0)
            {
                //GONetLog.Warning($"[SPAWN_SYNC] *** Processing {toProcess.Count} deferred spawns for scene '{sceneName}' ***");

                // Process each spawn
                foreach (InstantiateGONetParticipantEvent spawnEvent in toProcess)
                {
                    // LATE-JOINER / CROSS-CHANNEL ORDERING FIX (Dec 2025):
                    // Check tombstones before spawning - a despawn may have arrived before this deferred spawn was processed.
                    // This mirrors the check in OnInstantiationEvent_Remote (GONet.cs:2636).
                    if (TryConsumeDespawnTombstone(spawnEvent.GONetId))
                    {
                        GONetLog.Debug($"[SPAWN_SYNC] Suppressing deferred spawn for GONetId {spawnEvent.GONetId} - despawn tombstone present");
                        continue; // Spawn was already despawned before we processed it; skip this spawn.
                    }

                    // DUPLICATE SPAWN PREVENTION (Dec 2025):
                    // Check if a GONetParticipant with this GONetId already exists before instantiating.
                    // This mirrors the check in OnInstantiationEvent_Remote (GONet.cs:2788).
                    uint gonetId = spawnEvent.GONetId;
                    if (gonetId != GONetParticipant.GONetId_Unset && gonetParticipantByGONetIdMap.ContainsKey(gonetId))
                    {
                        GONetLog.Warning($"[SPAWN_SYNC] DUPLICATE deferred spawn ignored - GONetId {gonetId} already exists as '{gonetParticipantByGONetIdMap[gonetId].name}'");
                        continue;
                    }

                    //GONetLog.Debug($"[SPAWN_SYNC] Processing deferred spawn GONetId {spawnEvent.GONetId}, DesignTimeLocation: '{spawnEvent.DesignTimeLocation}'");
                    GONetParticipant instance = Instantiate_Remote(spawnEvent);
                    if (instance != null)
                    {
                        //GONetLog.Debug($"[SPAWN_SYNC] Successfully spawned deferred GONetId {spawnEvent.GONetId} as '{instance.gameObject.name}'");

                        // CRITICAL: Complete the post-instantiation setup that normally happens in OnInstantiationEvent_Remote
                        // Deferred spawns come from persistent events sent by server, so sourceAuthorityId is server
                        CompleteRemoteInstantiation(instance, spawnEvent, OwnerAuthorityId_Server);
                    }
                    else
                    {
                        GONetLog.Error($"[SPAWN_SYNC] FAILED to spawn deferred GONetId {spawnEvent.GONetId}!");
                    }
                }

                // Process any pending reparent events for this scene before applying AllValues.
                ProcessPendingReparentsForScene(sceneName);

                // IMPORTANT: After processing deferred spawns, check if we have deferred AllValues bundles waiting
                // The AllValues bundles must be processed AFTER spawns so the GONetParticipants exist in the lookup maps
                // CRITICAL: Process ALL matching bundles (November 2025 fix for high load scenarios with 810 GNPs)
                List<DeferredAllValuesBundle> bundlesToProcess = new List<DeferredAllValuesBundle>();
                for (int i = deferredAllValuesBundles.Count - 1; i >= 0; i--)
                {
                    if (deferredAllValuesBundles[i].RequiredSceneName == sceneName)
                    {
                        bundlesToProcess.Add(deferredAllValuesBundles[i]);
                        deferredAllValuesBundles.RemoveAt(i);
                    }
                }

                if (bundlesToProcess.Count > 0)
                {
                    GONetLog.Warning($"[INIT] Processing {bundlesToProcess.Count} deferred AllValues bundles after spawns completed for scene '{sceneName}'");

                    // CRITICAL FIX (November 2025): Reverse list to process in original arrival order
                    // (we built the list in reverse during the backwards loop above)
                    bundlesToProcess.Reverse();

                    foreach (var bundle in bundlesToProcess)
                    {
                        try
                        {
                            // Reconstruct the BitStream from the stored bytes using GetBuilder_WithNewData
                            using (BitByBitByteArrayBuilder reconstructedBitStream = BitByBitByteArrayBuilder.GetBuilder_WithNewData(bundle.RawBytes, bundle.BytesUsedCount))
                            {
                                DeserializeBody_AllValuesBundle(reconstructedBitStream, bundle.BytesUsedCount, bundle.RelatedConnection, bundle.ElapsedTicksAtSend);
                            }
                        }
                        catch (Exception ex)
                        {
                            // CRITICAL: If deserialization fails HERE (after deferral), that's a serious problem
                            // Don't re-defer indefinitely or we get infinite loop - log error and drop
                            // Check if this is an expected "despawned before init" case vs unexpected error
                            bool isExpectedDespawnCase = ex.Message.Contains("despawned before late-joiner");
                            bool isNotReady = ex is GONetParticipantNotReadyException || ex is KeyNotFoundException;
                            if (isExpectedDespawnCase)
                            {
                                GONetLog.Debug($"[DEFERRED-SKIP] Skipping deferred AllValues bundle for scene '{sceneName}': {ex.Message}");
                            }
                            else if (isNotReady)
                            {
                                TryReDeferAllValuesBundle(bundle, sceneName, ex);
                            }
                            else
                            {
                                GONetLog.Error($"[DEFERRED-FAIL] Failed to process previously deferred AllValues bundle for scene '{sceneName}': {ex.Message}. Dropping bundle to prevent infinite loop.");
                                GONetLog.Error($"[DEFERRED-FAIL] Stack trace: {ex.StackTrace}");
                            }
                        }
                    }

                    GONetLog.Warning($"[INIT] Deferred AllValues bundle processing complete ({bundlesToProcess.Count} bundles)");
                }

                // IMPORTANT: Process any deferred despawns AFTER spawns and AllValues complete
                // This ensures proper order: spawn -> initialize values -> despawn (if needed)
                if (deferredDespawnEvents.Count > 0)
                {
                    // Find despawns that match the spawns we just processed
                    List<DespawnGONetParticipantEvent> toProcessDespawns = new List<DespawnGONetParticipantEvent>();
                    foreach (var despawnEvent in deferredDespawnEvents)
                    {
                        // Check if this despawn's GONetId was in the spawns we just processed
                        if (toProcess.Exists(spawnEvent => spawnEvent.GONetId == despawnEvent.GONetId))
                        {
                            toProcessDespawns.Add(despawnEvent);
                        }
                    }

                    if (toProcessDespawns.Count > 0)
                    {
                        //GONetLog.Warning($"[SPAWN_SYNC] Processing {toProcessDespawns.Count} deferred despawns after spawns completed");

                        foreach (var despawnEvent in toProcessDespawns)
                        {
                            //GONetLog.Debug($"[SPAWN_SYNC] Processing deferred despawn for GONetId {despawnEvent.GONetId}");

                            // Look up the participant and destroy it
                            GONetParticipant gonetParticipant = null;
                            if (gonetParticipantByGONetIdMap.TryGetValue(despawnEvent.GONetId, out gonetParticipant))
                            {
                                gonetIdsDestroyedViaPropagation.Add(gonetParticipant.GONetId);

                                if (gonetParticipant != null && gonetParticipant.gameObject != null)
                                {
                                    //GONetLog.Debug($"[SPAWN_SYNC] Despawning '{gonetParticipant.gameObject.name}' (GONetId {despawnEvent.GONetId})");
                                    UnityEngine.Object.Destroy(gonetParticipant.gameObject);
                                }
                            }

                            // Remove from deferred list
                            deferredDespawnEvents.Remove(despawnEvent);
                        }

                        //GONetLog.Warning($"[SPAWN_SYNC] Deferred despawn processing complete");
                    }
                }
            }
            else
            {
                //GONetLog.Debug($"[SPAWN_SYNC] No deferred spawns matched scene '{sceneName}'");
            }

            // CRITICAL: Process deferred AllValues bundles even when no deferred spawns exist
            // This handles the case where client is loading scene (async Addressables) and receives
            // AllValues bundles before scene is ready - they get deferred and need processing here.
            // Moved outside the 'if (toProcess.Count > 0)' block because scene loading deferral
            // may happen independently of spawn deferral.
            // CRITICAL: Process ALL matching bundles (November 2025 fix for high load scenarios with 810 GNPs)
            //GONetLog.Warning($"[DEFERRED-PROCESSING] Checking {deferredAllValuesBundles.Count} deferred bundles for scene match '{sceneName}'...");
            ProcessPendingReparentsForScene(sceneName);
            List<DeferredAllValuesBundle> bundlesToProcessNoSpawns = new List<DeferredAllValuesBundle>();
            int matchedCount = 0;
            int nonMatchedCount = 0;
            for (int i = deferredAllValuesBundles.Count - 1; i >= 0; i--)
            {
                string bundleScene = deferredAllValuesBundles[i].RequiredSceneName;
                bool matches = bundleScene == sceneName;
                if (matches)
                {
                    matchedCount++;
                    bundlesToProcessNoSpawns.Add(deferredAllValuesBundles[i]);
                    deferredAllValuesBundles.RemoveAt(i);
                    //GONetLog.Warning($"[DEFERRED-PROCESSING]   Bundle {i}: MATCH - RequiredSceneName='{bundleScene}' (will process)");
                }
                else
                {
                    nonMatchedCount++;
                    //GONetLog.Warning($"[DEFERRED-PROCESSING]   Bundle {i}: NO MATCH - RequiredSceneName='{bundleScene}' vs sceneName='{sceneName}' (keeping in queue)");
                }
            }
            //GONetLog.Warning($"[DEFERRED-PROCESSING] Bundle matching complete: {matchedCount} matched, {nonMatchedCount} non-matched, {deferredAllValuesBundles.Count} remaining in queue");

            if (bundlesToProcessNoSpawns.Count > 0)
            {
                GONetLog.Warning($"[INIT] Processing {bundlesToProcessNoSpawns.Count} deferred AllValues bundles for scene '{sceneName}' (no deferred spawns - scene-loading deferral case)");

                // CRITICAL FIX (November 2025): Reverse list to process in original arrival order
                bundlesToProcessNoSpawns.Reverse();

                foreach (var bundle in bundlesToProcessNoSpawns)
                {
                    try
                    {
                        // Reconstruct the BitStream from the stored bytes using GetBuilder_WithNewData
                        using (BitByBitByteArrayBuilder reconstructedBitStream = BitByBitByteArrayBuilder.GetBuilder_WithNewData(bundle.RawBytes, bundle.BytesUsedCount))
                        {
                            DeserializeBody_AllValuesBundle(reconstructedBitStream, bundle.BytesUsedCount, bundle.RelatedConnection, bundle.ElapsedTicksAtSend);
                        }
                    }
                    catch (Exception ex)
                    {
                        // CRITICAL: If deserialization fails HERE (after deferral), that's a serious problem
                        // Don't re-defer indefinitely or we get infinite loop - log error and drop
                        bool isNotReady = ex is GONetParticipantNotReadyException || ex is KeyNotFoundException;
                        if (isNotReady)
                        {
                            TryReDeferAllValuesBundle(bundle, sceneName, ex);
                        }
                        else
                        {
                            GONetLog.Error($"[DEFERRED-FAIL] Failed to process previously deferred AllValues bundle for scene '{sceneName}': {ex.Message}. Dropping bundle to prevent infinite loop.");
                            GONetLog.Error($"[DEFERRED-FAIL] Stack trace: {ex.StackTrace}");
                        }
                    }
                }

                GONetLog.Warning($"[INIT] Deferred AllValues bundle processing complete (scene-loading deferral case, {bundlesToProcessNoSpawns.Count} bundles)");
            }
        }

        internal static void RecordParticipantsAsDefinedInScene(List<GONetParticipant> gonetParticipantsInLevel)
        {
            gonetParticipantsInLevel.ForEach(gonetParticipant => {
                // CRITICAL: Capture scene name BEFORE DontDestroyOnLoad moves the object
                // After DDOL, GetSceneIdentifier returns "DontDestroyOnLoad" instead of the original scene
                // Late-joiners need the original scene name to receive GONetId assignments
                string sceneName = GONetSceneManager.GetSceneIdentifier(gonetParticipant.gameObject);

                // Process AutoDontDestroyOnLoad flag for scene-defined objects
                // This happens AFTER capturing scene name to preserve original scene association
                if (gonetParticipant.AutoDontDestroyOnLoad)
                {
                    UnityEngine.Object.DontDestroyOnLoad(gonetParticipant.gameObject);
                    GONetLog.Debug($"[DDOL] Auto-applied DontDestroyOnLoad to scene-defined object: {gonetParticipant.gameObject.name} (original scene: {sceneName})");
                }

                definedInSceneParticipantInstanceIDs.Add(gonetParticipant.GetInstanceID());

                // Track which scene this GNP was defined in (using pre-DDOL scene name)
                if (!string.IsNullOrEmpty(sceneName))
                {
                    participantInstanceID_to_SpawnSceneName[gonetParticipant.GetInstanceID()] = sceneName;
                    //GONetLog.Debug($"[SceneTracking] Recorded GNP '{gonetParticipant.gameObject.name}' as defined in scene '{sceneName}'");
                }

                OnWasInstantiatedKnown_StartMonitoringForAutoMagicalNetworking(gonetParticipant);
                //GONetLog.Debug($" recording GNP defined in scene...go.Name: {gonetParticipant.gameObject.name} instanceId: {gonetParticipant.GetInstanceID()}");
            });
        }

        /// <summary>
        /// Records that a GONetParticipant was instantiated (spawned at runtime) in the specified scene.
        /// Called by spawn system when instantiating objects.
        /// </summary>
        internal static void RecordParticipantSpawnScene(GONetParticipant gonetParticipant, string sceneName)
        {
            if (gonetParticipant != null && !string.IsNullOrEmpty(sceneName))
            {
                participantInstanceID_to_SpawnSceneName[gonetParticipant.GetInstanceID()] = sceneName;
                //GONetLog.Debug($"[SceneTracking] Recorded spawned GNP '{gonetParticipant.gameObject.name}' in scene '{sceneName}'");
            }
        }

        /// <summary>
        /// Gets the scene name that a GONetParticipant was spawned in or defined in.
        /// Returns null if not tracked.
        /// </summary>
        public static string GetParticipantSpawnScene(GONetParticipant gonetParticipant)
        {
            if (gonetParticipant == null)
                return null;

            participantInstanceID_to_SpawnSceneName.TryGetValue(gonetParticipant.GetInstanceID(), out string sceneName);
            return sceneName;
        }

        /// <summary>
        /// Clears spawn scene tracking for a GONetParticipant (e.g., when destroyed).
        /// </summary>
        internal static void ClearParticipantSpawnScene(GONetParticipant gonetParticipant)
        {
            if (gonetParticipant != null)
            {
                participantInstanceID_to_SpawnSceneName.Remove(gonetParticipant.GetInstanceID());
            }
        }

        internal static void AssignOwnerAuthorityIds_IfAppropriate(List<GONetParticipant> gonetParticipantsInConsideration)
        {
            if (IsServer)
            {
                MyAuthorityId = OwnerAuthorityId_Server; // NOTE: at time of writing, MyAuthorityId is not set quite yet, which is why we go ahead and manually set here

                int count = gonetParticipantsInConsideration.Count;
                for (int i = 0; i < count; ++i)
                {
                    GONetParticipant item = gonetParticipantsInConsideration[i];

                    item.OwnerAuthorityId = MyAuthorityId;
                    AssignGONetIdRaw_IfAppropriate(item); // IMPORTANT: After setting OwnerAuthorityId, we need to assign the full GONetId (composite of raw + authority) to avoid partial GONetId
                }
            }
        }

        /// <summary>
        /// PRE: <paramref name="connectionToClient"/> already has been assigned a good value to <see cref="GONetConnection.OwnerAuthorityId"/>.
        /// </summary>
        static void Server_SendClientCurrentState_AllAutoMagicalSync(GONetConnection connectionToClient)
        {
            using (BitByBitByteArrayBuilder bitStream = BitByBitByteArrayBuilder.GetBuilder())
            {
                { // header...just message type/id...well, and now time
                    uint messageID = messageTypeToMessageIDMap[typeof(AutoMagicalSync_AllCurrentValues_Message)];
                    bitStream.WriteUInt(messageID);

                    bitStream.WriteLong(Time.ElapsedTicks);
                }

                GONetLog.Debug($"About to serialize all current values bundle for new client. activeAutoSyncCompanionsByCodeGenerationIdMap has {activeAutoSyncCompanionsByCodeGenerationIdMap.Count} code gen ID entries");
                SerializeBody_AllCurrentValuesBundle(bitStream); // body

                bitStream.WriteCurrentPartialByte();

                int bytesUsedCount = bitStream.Length_WrittenBytes;
                if (bytesUsedCount > SerializationUtils.MTU)
                {
                    GONetLog.Warning(string.Concat("Late joiner, here's how many bytes of automagical sync data I'm sending your way: ", bytesUsedCount));
                    // TODO break into smaller packets!!!
                }
                byte[] allValuesSerialized = mainThread_valueChangeSerializationArrayPool.Borrow(bytesUsedCount);
                Array.Copy(bitStream.GetBuffer(), 0, allValuesSerialized, 0, bytesUsedCount);

                //GONetLog.Debug($"[INIT] Sending {bytesUsedCount} bytes of current state to new client");
                GONetChannelId channelId = GetInitAwareCustomSerializationChannel(connectionToClient);
                SendBytesToRemoteConnection(connectionToClient, allValuesSerialized, bytesUsedCount, channelId); // NOT using GONetChannel.AutoMagicalSync_Reliable because that one is reserved for things as they are happening and not this one time blast to a new client for all things
                mainThread_valueChangeSerializationArrayPool.Return(allValuesSerialized);
                //GONetLog.Debug($"[INIT] Server_SendClientCurrentState_AllAutoMagicalSync completed");
            }

            // CRITICAL FIX (January 2026): Send BOTH sentinels together at the end of init message stream.
            // This ensures client doesn't ACK before all messages have been transmitted.
            // Sentinels only fire while init tracking is active.
            Server_SendBothInitTrackingMarkers(connectionToClient);
        }

        /// <summary>
        /// Chunked version of Server_SendClientCurrentState_AllAutoMagicalSync that sends sync bundles over multiple frames.
        /// CRITICAL FIX (November 2025): Prevents late-joiner initialization failures by spreading reliable message load.
        ///
        /// BENEFITS:
        /// - Avoids flooding reliable queue with 800+ sync bundles all at once
        /// - Allows backpressure system to suppress unreliable traffic when queue backs up
        /// - Client already initialized (InitComplete sent first), so progressive sync data arrival is fine
        /// - Value blending handles missing/delayed sync data gracefully
        ///
        /// IMPLEMENTATION:
        /// - Sends individual sync bundles per GONetParticipant (more granular than one giant bundle)
        /// - No artificial delays (frame yielding disabled - not needed with backpressure)
        /// - Backpressure system automatically throttles unreliable to keep reliable flowing
        /// </summary>
        /// <summary>
        /// CRITICAL FIX (November 2025): Coroutine version of AllValues sync.
        /// Sends sync bundles in batches (temporal chunking) to prevent flooding the reliable transport buffer.
        /// This ensures all 810+ bundles actually get queued and sent instead of being silently dropped at ~248.
        ///
        /// The "Drip Feed" pattern: Send 50 bundles per frame, yield to let transport flush buffer, repeat.
        /// At 60 FPS, 810 bundles takes ~16 frames (0.27 seconds). Client polling handles this perfectly.
        /// </summary>
        static System.Collections.IEnumerator Server_SendClientCurrentState_AllAutoMagicalSync_Coroutine(GONetConnection connectionToClient)
        {
            // CONFIGURATION: How many bundles to send per frame.
            // 50 bundles × ~1.2KB = ~60KB per frame. Safe for most transport layers.
            // 810 objects will take ~16 frames (0.27 seconds at 60FPS).
            const int BUNDLES_PER_FRAME = 50;

            int bundlesSentTotal = 0;
            int bundlesSentThisFrame = 0;
            int skippedIncomplete = 0;
            int totalBytes = 0;

            // Get the AuthorityID for validity checks
            ushort targetAuthorityId = connectionToClient.OwnerAuthorityId;

            GONetLog.Info($"[INIT-COROUTINE] Starting temporal sync stream for client {targetAuthorityId}...");

            // Iterate through all active sync companions
            // NOTE: ToList() on keys ensures thread safety/collection stability over multiple frames
            var codeGenIds = activeAutoSyncCompanionsByCodeGenerationIdMap.Keys.ToList();

            foreach (var codeGenId in codeGenIds)
            {
                if (!activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(codeGenId, out var companionDict)) continue;

                // Snapshot values to allow safe iteration over frames
                var companions = companionDict.Values.ToList();

                foreach (var companion in companions)
                {
                    // SAFETY 1: Check if client disconnected mid-stream
                    if (gonetServer == null || !gonetServer.TryGetRemoteClientByAuthorityId(targetAuthorityId, out _))
                    {
                        GONetLog.Warning($"[INIT-COROUTINE] Client {targetAuthorityId} disconnected mid-sync. Aborting after {bundlesSentTotal} bundles.");
                        yield break;
                    }

                    // SAFETY 2: Check if participant was destroyed mid-stream
                    if (companion == null || companion.gonetParticipant == null) continue;

                    GONetParticipant gonetParticipant = companion.gonetParticipant;

                    // CRITICAL FIX: Check for complete GONetId before serializing
                    bool hasAllComponents = gonetParticipant.DoesGONetIdContainAllComponents();
                    bool idIsNotZero = gonetParticipant.GONetId != GONetParticipant.GONetId_Unset;

                    if (!hasAllComponents || !idIsNotZero)
                    {
                        skippedIncomplete++;
                        continue;
                    }

                    // SERIALIZE AND SEND
                    using (BitByBitByteArrayBuilder bitStream = BitByBitByteArrayBuilder.GetBuilder())
                    {
                        // Header: message type + timestamp
                        uint messageID = messageTypeToMessageIDMap[typeof(AutoMagicalSync_AllCurrentValues_Message)];
                        bitStream.WriteUInt(messageID);
                        bitStream.WriteLong(Time.ElapsedTicks);

                        // GONetId (must be written BEFORE SerializeAll)
                        GONetParticipant.GONetId_InitialAssignment_CustomSerializer.Instance.Serialize(bitStream, gonetParticipant, gonetParticipant.GONetId);

                        // Values (everything except GONetId)
                        companion.SerializeAll(bitStream);

                        // End marker
                        bitStream.WriteBit(true);
                        bitStream.WriteBit(true); // isSameAsPrevious=true + isDiffNegative=true = end marker
                        bitStream.WriteCurrentPartialByte();

                        // Send
                        int bytesUsedCount = bitStream.Length_WrittenBytes;
                        totalBytes += bytesUsedCount;

                        byte[] bundleBytes = mainThread_valueChangeSerializationArrayPool.Borrow(bytesUsedCount);
                        Array.Copy(bitStream.GetBuffer(), 0, bundleBytes, 0, bytesUsedCount);

                        GONetChannelId channelId = GetInitAwareCustomSerializationChannel(connectionToClient);
                        SendBytesToRemoteConnection(connectionToClient, bundleBytes, bytesUsedCount, channelId);
                        mainThread_valueChangeSerializationArrayPool.Return(bundleBytes);

                        bundlesSentTotal++;
                        bundlesSentThisFrame++;
                    }

                    // FLOW CONTROL: Yield if we hit the batch limit
                    // This allows transport layer to flush buffer between batches
                    if (bundlesSentThisFrame >= BUNDLES_PER_FRAME)
                    {
                        bundlesSentThisFrame = 0;
                        GONetLog.Debug($"[INIT-COROUTINE] Yielding after {bundlesSentTotal} bundles sent (batch complete)");
                        yield return null; // Wait for next frame to flush buffers
                    }
                }
            }

            if (skippedIncomplete > 0)
            {
                GONetLog.Warning($"[INIT-COROUTINE] Skipped {skippedIncomplete} GNPs with incomplete GONetIds");
            }

            GONetLog.Info($"[INIT-COROUTINE] Completed. Sent {bundlesSentTotal} bundles ({totalBytes} bytes) to client {targetAuthorityId}. Client polling will process when stream finishes.");

            if (gonetServer != null && gonetServer.TryGetRemoteClientByAuthorityId(targetAuthorityId, out _))
            {
                // CRITICAL FIX (January 2026): Send BOTH sentinels together at the end of init message stream.
                // This ensures client doesn't ACK before all messages have been transmitted.
                // Sentinels only fire while init tracking is active.
                Server_SendBothInitTrackingMarkers(connectionToClient);
            }
        }

        /// <summary>
        /// Sends a full AllCurrentValues sync to a specific client (post-handoff recovery).
        /// </summary>
        internal static void Server_RequestFullStateSyncForClient(ushort clientAuthorityId, string reason)
        {
            if (!IsServer || gonetServer == null)
            {
                GONetLog.Warning("[Reconciliation] Cannot send full state sync - not the server");
                return;
            }

            if (!gonetServer.TryGetRemoteClientByAuthorityId(clientAuthorityId, out GONetRemoteClient remoteClient))
            {
                GONetLog.Warning($"[Reconciliation] Cannot send full state sync - client {clientAuthorityId} not found");
                return;
            }

            long nowRawTicks = Time.RawElapsedTicks;
            if (lastFullStateSyncRequestRawTicks.TryGetValue(clientAuthorityId, out long lastRequestRawTicks))
            {
                long throttleTicks = (long)(FULL_STATE_SYNC_REQUEST_THROTTLE_SECONDS * TimeSpan.TicksPerSecond);
                if (nowRawTicks - lastRequestRawTicks < throttleTicks)
                {
                    GONetLog.Warning($"[Reconciliation] Throttling full state sync for client {clientAuthorityId} (reason={reason})");
                    return;
                }
            }

            if (!fullStateSyncInProgress.Add(clientAuthorityId))
            {
                GONetLog.Warning($"[Reconciliation] Full state sync already in progress for client {clientAuthorityId} (reason={reason})");
                return;
            }

            lastFullStateSyncRequestRawTicks[clientAuthorityId] = nowRawTicks;

            if (persistentEventsThisSession.Count > 0)
            {
                GONetLog.Info($"[Reconciliation] Sending persistent events to client {clientAuthorityId} before full state sync (reason={reason})");
                Server_SendClientPersistentEventsSinceStart(remoteClient.ConnectionToClient);
            }

            GONetLog.Info($"[Reconciliation] Starting full state sync for client {clientAuthorityId} (reason={reason})");

            if (Global != null)
            {
                Global.StartCoroutine(Server_SendClientCurrentState_AllAutoMagicalSync_TrackedCoroutine(remoteClient.ConnectionToClient, clientAuthorityId, reason));
            }
            else
            {
                GONetLog.Error("[Reconciliation] Global (GONetGlobal) is null! Sending full state sync synchronously.");
                Server_SendClientCurrentState_AllAutoMagicalSync(remoteClient.ConnectionToClient);
                fullStateSyncInProgress.Remove(clientAuthorityId);
            }
        }

        private static System.Collections.IEnumerator Server_SendClientCurrentState_AllAutoMagicalSync_TrackedCoroutine(GONetConnection connectionToClient, ushort clientAuthorityId, string reason)
        {
            yield return Server_SendClientCurrentState_AllAutoMagicalSync_Coroutine(connectionToClient);
            fullStateSyncInProgress.Remove(clientAuthorityId);
            GONetLog.Info($"[Reconciliation] Completed full state sync for client {clientAuthorityId} (reason={reason})");
        }

        private static void RequestFullStateSyncRetryIfNeeded(string reason)
        {
            if (!IsClient || IsServer || HostEpoch == 0)
            {
                return;
            }

            if (GONetClient?.IsConnectedToServer != true || !GONetClient.IsInitializedWithServer)
            {
                return;
            }

            long nowRawTicks = Time.RawElapsedTicks;
            long throttleTicks = (long)(FULL_STATE_SYNC_RETRY_THROTTLE_SECONDS * TimeSpan.TicksPerSecond);
            if (nowRawTicks - lastFullStateSyncRetryRawTicks < throttleTicks)
            {
                return;
            }

            lastFullStateSyncRetryRawTicks = nowRawTicks;

            var request = new PostHandoffFullStateSyncRequestEvent(clientEpoch: HostEpoch)
            {
                OccurredAtElapsedTicks = Time.ElapsedTicks
            };
            EventBus.Publish(request);
            GONetLog.Warning($"[Reconciliation] Requested full state sync retry (reason={reason}, epoch={HostEpoch})");
        }

        /// <summary>
        /// CRITICAL FIX (November 2025): Sends AllCurrentValues for scene-defined objects in a specific scene.
        /// Called when a client finishes loading a scene AFTER their initial connection.
        ///
        /// Without this, scene-defined objects only receive delta sync updates but never get their
        /// initial position/rotation values from the server, causing them to appear at wrong positions.
        /// </summary>
        internal static void Server_SendClientCurrentState_ForSceneDefinedObjects(string sceneName, ushort clientAuthorityId)
        {
            GONetLog.Info($"[SCENE-SYNC-VALUES] ENTRY - sceneName: '{sceneName}', clientAuthorityId: {clientAuthorityId}, definedInSceneParticipantInstanceIDs.Count: {definedInSceneParticipantInstanceIDs.Count}, participantInstanceID_to_SpawnSceneName.Count: {participantInstanceID_to_SpawnSceneName.Count}");

            // Get the connection for this client
            if (!gonetServer.TryGetRemoteClientByAuthorityId(clientAuthorityId, out GONetRemoteClient remoteClient))
            {
                GONetLog.Warning($"[SCENE-SYNC-VALUES] Cannot send values - client {clientAuthorityId} not found");
                return;
            }

            GONetConnection connectionToClient = remoteClient.ConnectionToClient;
            int bundlesSent = 0;
            int totalBytes = 0;
            int matchedSceneCount = 0;
            int foundParticipantCount = 0;
            int hasGONetIdCount = 0;
            int hasCompanionCount = 0;

            // Find all scene-defined GONetParticipants in this scene and send their current values
            foreach (int instanceId in definedInSceneParticipantInstanceIDs)
            {
                if (!participantInstanceID_to_SpawnSceneName.TryGetValue(instanceId, out string participantScene) ||
                    participantScene != sceneName)
                {
                    continue; // Not in this scene
                }
                matchedSceneCount++;

                // Find the participant by instance ID
                GONetParticipant gonetParticipant = null;
                foreach (var kvp in gonetParticipantByGONetIdMap)
                {
                    if (kvp.Value != null && kvp.Value.GetInstanceID() == instanceId)
                    {
                        gonetParticipant = kvp.Value;
                        break;
                    }
                }

                if (gonetParticipant == null)
                {
                    continue; // Not found
                }
                foundParticipantCount++;

                if (gonetParticipant.GONetId == GONetParticipant.GONetId_Unset)
                {
                    continue; // Not initialized
                }
                hasGONetIdCount++;

                // Get sync companion
                if (!activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(gonetParticipant.CodeGenerationId, out var companionDict) ||
                    !companionDict.TryGetValue(gonetParticipant, out var companion))
                {
                    continue; // No sync companion
                }
                hasCompanionCount++;

                // Send individual sync bundle for this participant
                using (BitByBitByteArrayBuilder bitStream = BitByBitByteArrayBuilder.GetBuilder())
                {
                    // Header: message type + timestamp
                    uint messageID = messageTypeToMessageIDMap[typeof(AutoMagicalSync_AllCurrentValues_Message)];
                    bitStream.WriteUInt(messageID);
                    bitStream.WriteLong(Time.ElapsedTicks);

                    // Write GONetId BEFORE SerializeAll (SerializeAll skips GONetId by design)
                    GONetParticipant.GONetId_InitialAssignment_CustomSerializer.Instance.Serialize(bitStream, gonetParticipant, gonetParticipant.GONetId);

                    // Body: serialize this companion's current values (everything EXCEPT GONetId)
                    companion.SerializeAll(bitStream);

                    // End marker (required by DeserializeBody_AllValuesBundle)
                    bitStream.WriteBit(true);
                    bitStream.WriteBit(true); // isSameAsPrevious=true + isDiffNegative=true = end marker

                    bitStream.WriteCurrentPartialByte();

                    int bytesUsedCount = bitStream.Length_WrittenBytes;
                    totalBytes += bytesUsedCount;

                    byte[] bundleBytes = mainThread_valueChangeSerializationArrayPool.Borrow(bytesUsedCount);
                    Array.Copy(bitStream.GetBuffer(), 0, bundleBytes, 0, bytesUsedCount);

                    // Send this bundle (reliable)
                    GONetChannelId channelId = GetInitAwareCustomSerializationChannel(connectionToClient);
                    SendBytesToRemoteConnection(connectionToClient, bundleBytes, bytesUsedCount, channelId);
                    mainThread_valueChangeSerializationArrayPool.Return(bundleBytes);

                    bundlesSent++;
                }
            }

            GONetLog.Info($"[SCENE-SYNC-VALUES] RESULTS - scene '{sceneName}', client {clientAuthorityId}: matchedScene={matchedSceneCount}, foundParticipant={foundParticipantCount}, hasGONetId={hasGONetIdCount}, hasCompanion={hasCompanionCount}, bundlesSent={bundlesSent}, totalBytes={totalBytes}");
        }

        static void Server_SendClientIndicationOfInitializationCompletion(GONetConnection_ServerToClient connectionToClient)
        {
            using (BitByBitByteArrayBuilder bitStream = BitByBitByteArrayBuilder.GetBuilder())
            {
                { // header...just message type/id...well, and now time 
                    uint messageID = messageTypeToMessageIDMap[typeof(ServerSaysClientInitializationCompletion)];
                    bitStream.WriteUInt(messageID);

                    bitStream.WriteLong(Time.ElapsedTicks);
                }

                bitStream.WriteCurrentPartialByte();

                int bytesUsedCount = bitStream.Length_WrittenBytes;
                byte[] bytes = mainThread_valueChangeSerializationArrayPool.Borrow(bytesUsedCount);
                Array.Copy(bitStream.GetBuffer(), 0, bytes, 0, bytesUsedCount);

                SendBytesToRemoteConnection(connectionToClient, bytes, bytesUsedCount, GONetChannel.ClientInitialization_CustomSerialization_Reliable);
                mainThread_valueChangeSerializationArrayPool.Return(bytes);
            }
        }

        private static bool ShouldUseInitChannels(GONetConnection connectionToClient)
        {
            if (!IsServer || gonetServer == null)
            {
                return false;
            }

            if (connectionToClient is GONetConnection_ServerToClient serverConnection &&
                serverConnection.OwnerAuthorityId != OwnerAuthorityId_Unset &&
                gonetServer.TryGetRemoteClientByAuthorityId(serverConnection.OwnerAuthorityId, out GONetRemoteClient remoteClient))
            {
                return remoteClient.IsInitMessageTrackingActive;
            }

            return true;
        }

        private static GONetChannelId GetInitAwareEventSinglesChannel(GONetConnection connectionToClient)
        {
            return ShouldUseInitChannels(connectionToClient)
                ? GONetChannel.ClientInitialization_EventSingles_Reliable
                : GONetChannel.EventSingles_Reliable;
        }

        private static GONetChannelId GetInitAwareCustomSerializationChannel(GONetConnection connectionToClient)
        {
            return ShouldUseInitChannels(connectionToClient)
                ? GONetChannel.ClientInitialization_CustomSerialization_Reliable
                : GONetChannel.CustomSerialization_Reliable;
        }

        private static void Server_MarkInitMessageTrackingComplete(GONetConnection connectionToClient)
        {
            if (!IsServer || gonetServer == null)
            {
                return;
            }

            if (connectionToClient is GONetConnection_ServerToClient serverConnection &&
                serverConnection.OwnerAuthorityId != OwnerAuthorityId_Unset &&
                gonetServer.TryGetRemoteClientByAuthorityId(serverConnection.OwnerAuthorityId, out GONetRemoteClient remoteClient))
            {
                remoteClient.IsInitMessageTrackingActive = false;
            }
        }

        /// <summary>
        /// Server marker indicating all init-tracked custom serialization messages have been sent.
        /// Client waits for this before sending init acknowledgment.
        /// </summary>
        static void Server_SendClientInitMessageTrackingComplete(GONetConnection connectionToClient)
        {
            using (BitByBitByteArrayBuilder bitStream = BitByBitByteArrayBuilder.GetBuilder())
            {
                uint messageID = messageTypeToMessageIDMap[typeof(ServerSaysInitMessageTrackingComplete)];
                bitStream.WriteUInt(messageID);
                bitStream.WriteLong(Time.ElapsedTicks);

                bitStream.WriteCurrentPartialByte();

                int bytesUsedCount = bitStream.Length_WrittenBytes;
                byte[] bytes = mainThread_valueChangeSerializationArrayPool.Borrow(bytesUsedCount);
                Array.Copy(bitStream.GetBuffer(), 0, bytes, 0, bytesUsedCount);

                SendBytesToRemoteConnection(connectionToClient, bytes, bytesUsedCount, GONetChannel.ClientInitialization_CustomSerialization_Reliable);
                mainThread_valueChangeSerializationArrayPool.Return(bytes);
            }
        }

        /// <summary>
        /// Server marker indicating all init-tracked EventSingles (channel 8) messages have been sent.
        /// Sends an empty PersistentEvents_Bundle as the sentinel marker.
        ///
        /// CRITICAL FIX (January 2026): This sentinel must be sent at the SAME TIME as the channel 9
        /// sentinel (ServerSaysInitMessageTrackingComplete) to ensure both arrive AFTER all init
        /// messages have been transmitted. Previously this was sent immediately after persistent
        /// events, causing race conditions where client received sentinels before sync bundles.
        /// </summary>
        static void Server_SendClientInitTrackingMarker_EventSingles(GONetConnection connectionToClient)
        {
            PersistentEvents_Bundle markerBundle = new PersistentEvents_Bundle(Time.ElapsedTicks, new LinkedList<IPersistentEvent>());
            byte[] markerBytes = SerializationUtils.SerializeToBytes<IGONetEvent>(markerBundle, out int markerBytesUsedCount, out bool markerNeedsReturn);
            SendBytesToRemoteConnection(connectionToClient, markerBytes, markerBytesUsedCount, GONetChannel.ClientInitialization_EventSingles_Reliable);
            if (markerNeedsReturn)
            {
                SerializationUtils.ReturnByteArray(markerBytes);
            }
        }

        /// <summary>
        /// Sends both init channel sentinels together.
        /// CRITICAL: Both must be sent after ALL init messages are queued to prevent race conditions.
        /// </summary>
        static void Server_SendBothInitTrackingMarkers(GONetConnection connectionToClient)
        {
            if (!ShouldUseInitChannels(connectionToClient))
            {
                return;
            }

            // Channel 8 sentinel (empty PersistentEvents_Bundle)
            Server_SendClientInitTrackingMarker_EventSingles(connectionToClient);

            // Channel 9 sentinel (ServerSaysInitMessageTrackingComplete)
            Server_SendClientInitMessageTrackingComplete(connectionToClient);

            Server_MarkInitMessageTrackingComplete(connectionToClient);
        }

        /// <summary>
        /// Sends the init acknowledgment once both init channel sentinels are received.
        /// </summary>
        static void Client_TrySendInitializationAcknowledgment()
        {
            if (!IsClient || _gonetClient == null || _gonetClient.hasAcknowledgedInitMessages)
            {
                return;
            }

            if (!GONetClient.IsInitializedWithServer)
            {
                return;
            }

            if (!_gonetClient.hasReceivedInitTrackingMarker_EventSingles ||
                !_gonetClient.hasReceivedInitTrackingMarker_CustomSerialization)
            {
                return;
            }

            Client_SendInitializationAcknowledgment();
        }

        /// <summary>
        /// Sends an acknowledgment to the server indicating how many init messages were received.
        /// Called after receiving init channel sentinels to avoid early/mismatched counts.
        /// Enables server to detect Steamworks (or other transport) reliable message delivery failures.
        /// See: .claude/STEAMWORKS_INIT_MESSAGE_DELIVERY_SOLUTION.md
        /// </summary>
        static void Client_SendInitializationAcknowledgment()
        {
            if (!IsClient || _gonetClient == null)
            {
                GONetLog.Warning("[InitMsgTracker] CLIENT: Attempted to send acknowledgment but not a client or client not initialized");
                return;
            }

            // Calculate total received count and gather channel list
            int totalReceivedCount = 0;
            List<byte> receivedChannelsList = new List<byte>();

            lock (_gonetClient.receivedInitMessageChannels)
            {
                foreach (var kvp in _gonetClient.receivedInitMessageChannels)
                {
                    totalReceivedCount += kvp.Value;
                    receivedChannelsList.Add(kvp.Key);
                }
            }

#if GONet_INIT_TRACE
            GONetLog.Info($"[InitMsgTracker] CLIENT: Sending acknowledgment - Received {totalReceivedCount} init messages on channels [{string.Join(", ", receivedChannelsList)}]");
#endif

            // Serialize and send acknowledgment event
            ClientInitializationAcknowledgment ackEvent = new ClientInitializationAcknowledgment
            {
                ReceivedMessageCount = totalReceivedCount,
                ReceivedChannels = receivedChannelsList
            };

            int bytesUsedCount;
            bool doesNeedToReturn;
            byte[] bytes = SerializationUtils.SerializeToBytes<IGONetEvent>(ackEvent, out bytesUsedCount, out doesNeedToReturn);

            // Send on reliable channel back to server
            SendBytesToRemoteConnection(_gonetClient.connectionToServer, bytes, bytesUsedCount, GONetChannel.EventSingles_Reliable);

            if (doesNeedToReturn)
            {
                SerializationUtils.ReturnByteArray(bytes);
            }

#if GONet_INIT_TRACE
            GONetLog.Info($"[InitMsgTracker] CLIENT: Sent acknowledgment ({bytesUsedCount} bytes) to server");
#endif

            // Stop tracking init messages now that acknowledgment is sent
            // Prevents continual tracking of TimeSync_Unreliable (channel 0) throughout session
            _gonetClient.hasAcknowledgedInitMessages = true;
#if GONet_INIT_TRACE
            GONetLog.Debug($"[InitMsgTracker] CLIENT: Stopped tracking init messages (hasAcknowledgedInitMessages = true)");
#endif
        }

        /// <summary>
        /// For every unique combination encountered of the following values: 
        ///     <see cref="GONetAutoMagicalSyncAttribute.SyncChangesEverySeconds"/>, 
        ///     <see cref="GONetAutoMagicalSyncAttribute.MustRunOnUnityMainThread"/> and 
        ///     <see cref="GONetAutoMagicalSyncAttribute.Reliability"/> (i.e., as encapsulated in <see cref="SyncBundleUniqueGrouping"/>), 
        /// an instance of this class will be created and used to process only those fields/properties set to be sync'd on that frequency.
        /// </summary>
        internal sealed class AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable : IDisposable
        {
            bool isSetupToRunInSeparateThread;
            /// <summary>
            /// Only non-null when <see cref="isSetupToRunInSeparateThread"/> is true
            /// </summary>
            Thread thread;
            /// <summary>
            /// Can only be true when <see cref="isSetupToRunInSeparateThread"/> is true
            /// </summary>
            volatile bool isThreadRunning;

            volatile bool shouldProcessInSeparateThreadASAP = false;
            long lastScheduledProcessAtTicks;

            static readonly long END_OF_FRAME_IN_WHICH_CHANGE_OCCURS_TICKS = TimeSpan.FromSeconds(AutoMagicalSyncFrequencies.END_OF_FRAME_IN_WHICH_CHANGE_OCCURS_SECONDS).Ticks;

            internal delegate void ProcessContext(in SyncBundleUniqueGrouping uniqueGrouping, long elapsedTicks);
            internal event ProcessContext AboutToProcess;

            SyncBundleUniqueGrouping uniqueGrouping;
            long scheduleFrequencyTicks;
            Dictionary<byte, Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated>> everythingMap_evenStuffNotOnThisScheduleFrequency;
            QosType uniqueGrouping_qualityOfService;
            GONetChannelId uniqueGrouping_valueChanges_channelId;
            GONetChannelId uniqueGrouping_valuesNowAtRest_channelId;

            /// <summary>
            /// Indicates whether or not <see cref="ProcessASAP"/> must be called (manually) from an outside part in order for sync processing to occur.
            /// </summary>
            internal bool DoesRequireManualProcessInitiation => scheduleFrequencyTicks == END_OF_FRAME_IN_WHICH_CHANGE_OCCURS_TICKS || !isSetupToRunInSeparateThread;

            /// <summary>
            /// Just a helper data structure just for use in <see cref="ProcessAutoMagicalSyncStuffs(bool, ReliableEndpoint)"/>
            /// </summary>
            readonly List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue> syncValuesToSend = new List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue>(1000);

            readonly List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue> valuesNowAtRestToBroadcast = new List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue>(1000);
            bool signalNeedToResetAtRest_untilBetterWayToDealWithThisSituation;

            readonly ArrayPool<byte> myThread_valueChangeSerializationArrayPool;

            readonly SecretaryOfTemporalAffairs myThread_Time;

            /// <summary>
            /// IMPORTANT: If a value of <see cref="AutoMagicalSyncFrequencies.END_OF_FRAME_IN_WHICH_CHANGE_OCCURS"/> is passed in here for <paramref name="scheduleFrequency"/>,
            ///            then nothing will happen in here automatically....<see cref="GONetMain"/> or some other party will have to manually call <see cref="ProcessASAP"/>.
            /// </summary>
            internal AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable(SyncBundleUniqueGrouping uniqueGrouping, Dictionary<byte, Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated>> everythingMap_evenStuffNotOnThisScheduleFrequency)
            {
                autoSyncProcessThread_valueChangeSerializationArrayPool_ThreadMap[this] = myThread_valueChangeSerializationArrayPool = new ArrayPool<byte>(100, 10, 1024, 2048);

                this.uniqueGrouping = uniqueGrouping;
                scheduleFrequencyTicks = TimeSpan.FromSeconds(uniqueGrouping.scheduleFrequency).Ticks;
                uniqueGrouping_qualityOfService = uniqueGrouping.reliability == AutoMagicalSyncReliability.Reliable ? QosType.Reliable : QosType.Unreliable;
                uniqueGrouping_valueChanges_channelId = uniqueGrouping.reliability == AutoMagicalSyncReliability.Reliable ? GONetChannel.AutoMagicalSync_Reliable : GONetChannel.AutoMagicalSync_Unreliable;
                uniqueGrouping_valuesNowAtRest_channelId = GONetChannel.AutoMagicalSync_ValuesNowAtRest_Reliable;

                this.everythingMap_evenStuffNotOnThisScheduleFrequency = everythingMap_evenStuffNotOnThisScheduleFrequency;

                Time.TimeSetFromAuthority += Time_TimeSetFromAuthority;

                isSetupToRunInSeparateThread = !uniqueGrouping.mustRunOnUnityMainThread;
                if (isSetupToRunInSeparateThread)
                {
                    myThread_Time = new SecretaryOfTemporalAffairs(GONetMain.Time); // since not running on main thread, we need to use a new/separate instance to avoid cross thread access conflicts

                    thread = new Thread(ContinuallyProcess_NotMainThread);
                    thread.Name = string.Concat("GONet Auto-magical Sync - ", Enum.GetName(typeof(AutoMagicalSyncReliability), uniqueGrouping.reliability), " Freq: ", uniqueGrouping.scheduleFrequency);
                    thread.Priority = System.Threading.ThreadPriority.AboveNormal;
                    thread.IsBackground = true; // do not prevent process from exiting when foreground thread(s) end

                    events_AwaitingSendToOthersQueue_ByThreadMap[thread] = new Queue<IGONetEvent>(100); // we're on main thread, safe to deal with regular dict here
                    var ringBuffer = new RingBuffer<IGONetEvent>(); // Starts at 2048, auto-scales to 16384
                    ringBuffer.OnResized = OnRingBufferResized;
                    events_SendToOthersQueue_ByThreadMap[thread] = ringBuffer;

                    isThreadRunning = true;
                    thread.Start();
                    GONetLog.Debug($"[SYNC-THREAD-START] Started background sync thread: {thread.Name}");
                }
                else
                {
                    GONetLog.Debug($"[SYNC-MAINTHREAD] Sync scheduler configured for main thread: {uniqueGrouping.scheduleFrequency}/{uniqueGrouping.reliability}");
                    myThread_Time = Time; // if running on main thread, no need to use a different instance that will already be used on the main thread

                    if (!events_AwaitingSendToOthersQueue_ByThreadMap.ContainsKey(Thread.CurrentThread))
                    {
                        events_AwaitingSendToOthersQueue_ByThreadMap[Thread.CurrentThread] = new Queue<IGONetEvent>(100); // we're on main thread, safe to deal with regular dict here
                        var ringBuffer = new RingBuffer<IGONetEvent>(); // Starts at 2048, auto-scales to 16384
                        ringBuffer.OnResized = OnRingBufferResized;
                        events_SendToOthersQueue_ByThreadMap[Thread.CurrentThread] = ringBuffer;
                    }
                }

            }

            private void Time_TimeSetFromAuthority(double fromElapsedSeconds, double toElapsedSeconds, long fromElapsedTicks, long toElapsedTicks)
            {
                if (myThread_Time != Time) // avoid SetFromAuthority if the local time instance is the same as GONetMain instance since it will be already handled/set
                {
                    myThread_Time.SetFromAuthority(toElapsedTicks);
                }
            }

            /// <summary>
            /// Callback invoked when the ring buffer automatically resizes.
            /// Logs informative messages about buffer growth and capacity warnings.
            /// </summary>
            private void OnRingBufferResized(int oldCapacity, int newCapacity, int currentCount)
            {
                // Calculate memory usage (approximate)
                int memoryKB = (newCapacity * 8 + 128 + 24) / 1024; // Array + padding + overhead

                if (newCapacity > oldCapacity)
                {
                    // Successful resize
                    GONetLog.Info($"[GONet] Ring buffer auto-scaled: {oldCapacity} → {newCapacity} (memory: ~{memoryKB} KB, current fill: {currentCount}/{newCapacity})");

                    if (newCapacity >= 16384)
                    {
                        // Reached maximum capacity
                        GONetLog.Warning($"[GONet] Ring buffer reached maximum capacity ({newCapacity}). Consider optimizing spawn rate or implementing spatial culling. Current load: {currentCount} events.");
                    }
                }
                else
                {
                    // Failed to resize (already at max capacity and hitting 75% threshold)
                    float fillPercent = (float)currentCount / oldCapacity * 100f;
                    GONetLog.Warning($"[GONet] Ring buffer at maximum capacity ({oldCapacity}) and {fillPercent:F1}% full! Events may be dropped during high load. Consider:\n" +
                        $"  - Reducing spawn rate (spread spawns over multiple frames)\n" +
                        $"  - Implementing spatial culling (only sync nearby objects)\n" +
                        $"  - Contact GONet support if this persists\n" +
                        $"Current event count: {currentCount}/{oldCapacity}");
                }
            }

            ~AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable()
            {
                Dispose();
            }

            private static long lastBlockedLogTicks = 0;

            // HANDOFF-DIAG: Throttle diagnostic logging for client 3 sync tracking
            private static long _handoffDiag_lastLogTick = 0;

            private void ContinuallyProcess_NotMainThread()
            {
                bool doesRequireManualProcessInitiation = DoesRequireManualProcessInitiation;
                while (isThreadRunning)
                {
                    // DIAG: Log why sync is blocked (throttled to every 1 second)
                    bool notSafe = IsNotSafeToProcess();
                    bool manualBlocked = doesRequireManualProcessInitiation && !shouldProcessInSeparateThreadASAP;
                    long diagNowTicks = HighResolutionTimeUtils.UtcNowTicks;
                    if (IsServer && (notSafe || manualBlocked) && (diagNowTicks - lastBlockedLogTicks) > TimeSpan.TicksPerSecond)
                    {
                        lastBlockedLogTicks = diagNowTicks;
                        GONetLog.Debug($"[SYNC-BLOCKED] notSafe={notSafe}, manualBlocked={manualBlocked}, MyAuth={MyAuthorityId}, IsClientVsServerKnown={IsClientVsServerStatusKnown}, IsClient={IsClient}, IsConnected={GONetClient.IsConnectedToServer}");
                    }

                    if (notSafe || manualBlocked)
                    {
                        Thread.Sleep(1); // TODO come up with appropriate sleep time/value
                    }
                    else
                    {
                        lastScheduledProcessAtTicks = HighResolutionTimeUtils.UtcNowTicks;
                        Process();
                        shouldProcessInSeparateThreadASAP = false; // reset this

                        if (!doesRequireManualProcessInitiation)
                        { // (auto sync) frequency control:
                            long nowTicks = HighResolutionTimeUtils.UtcNowTicks;
                            long ticksToSleep = scheduleFrequencyTicks - (nowTicks - lastScheduledProcessAtTicks);
                            if (ticksToSleep > 0)
                            {
                                Thread.Sleep(TimeSpan.FromTicks(ticksToSleep));
                                //GONetLog.Debug("sleep ticks: " + ticksToSleep);
                            }
                            else
                            {
                                //GONetLog.Debug("scheduleFrequencyTicks: " + scheduleFrequencyTicks + ", sleep ticks: " + ticksToSleep);
                            }
                        }
                    }
                }
            }

            private bool IsNotSafeToProcess()
            {
                // In HOST mode (IsServer && IsClient), server-side sync should proceed even if
                // the HOST's client-side connection isn't established. Only pure clients should
                // wait for GONetClient.IsConnectedToServer.
                return MyAuthorityId == OwnerAuthorityId_Unset ||
                    !IsClientVsServerStatusKnown ||
                    (IsClient && !IsServer && !GONetClient.IsConnectedToServer);
            }

            /// <summary>
            /// Caller is responsible for knowing the value of and dealing with <see cref="IsNotSafeToProcess"/>.
            /// </summary>
            private void Process()
            {
                //long startTicks = HighResolutionTimeUtils.UtcNow.Ticks;
                int bundleFragmentsMadeCount = 0;

                try
                {
                    if (myThread_Time != Time) // avoid updating time if the local time instance is the same as GONetMain instance since it will be updated already
                    {
                        myThread_Time.Update();
                    }
                    long myTicks = myThread_Time.ElapsedTicks;

                    // DIAG: Log every Process() call
                    if (IsServer)
                    {
                        //GONetLog.Debug($"[SYNC-PROCESS] grp={uniqueGrouping.scheduleFrequency}/{uniqueGrouping.reliability}");
                    }

                    AboutToProcess?.Invoke(uniqueGrouping, myTicks);

                    // loop over everythingMap_evenStuffNotOnThisScheduleFrequency only processing the items inside that match scheduleFrequency
                    syncValuesToSend.Clear();
                    valuesNowAtRestToBroadcast.Clear();

                    // OPTIMIZATION: Calculate once per Process() call instead of per-participant in inner loop
                    bool isPhysicsSyncGrouping = uniqueGrouping.Equals(grouping_physics_unreliable); // FIXED: struct comparison

                    // DIAG: Log participant counts
                    if (IsServer)
                    {
                        int totalParticipants = 0;
                        foreach (var kvp in everythingMap_evenStuffNotOnThisScheduleFrequency)
                            if (kvp.Value != null) totalParticipants += kvp.Value.Count;
                        //GONetLog.Debug($"[SYNC-ENTRY] grp={uniqueGrouping.scheduleFrequency}/{uniqueGrouping.reliability}, maps={everythingMap_evenStuffNotOnThisScheduleFrequency.Count}, total={totalParticipants}");
                    }

                    using (var enumeratorOuter = everythingMap_evenStuffNotOnThisScheduleFrequency.GetEnumerator())
                    {
                        while (enumeratorOuter.MoveNext())
                        {
                            Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> currentMap = enumeratorOuter.Current.Value;
                            if (currentMap == null)
                            {
                                GONetLog.Error("currentMap == null");
                            }
                            using (var enumeratorInner = currentMap.GetEnumerator())
                            {
                                while (enumeratorInner.MoveNext())
                                {
                                    GONetParticipant participant = enumeratorInner.Current.Key;
                                    GONetParticipant_AutoMagicalSyncCompanion_Generated monitoringSupport = enumeratorInner.Current.Value;

                                    if (monitoringSupport == null)
                                    {
                                        GONetLog.Error("monitoringSupport == null");
                                        continue;
                                    }

                                    // CRITICAL FIX: Skip destroyed/despawned participants to prevent sending sync data for dead objects
                                    // This fixes the "white beacon/stuck projectile" bug where sync bundles included despawned objects for 30+ seconds
                                    // causing GetCurrentGONetIdByIdAtInstantiation() to return 0 and abort entire bundles on receiver side.
                                    //
                                    // Root cause: everythingMap not cleaned up when participants despawn, so sync thread keeps iterating
                                    // over dead participants and including their (now invalid) InstantiationIds in outgoing bundles.
                                    //
                                    // This check works for ALL authority scenarios (client-authority, server-authority, etc.) because:
                                    // - gonetParticipantByGONetIdMap is static (shared across all instances)
                                    // - Participants removed from map in OnDisable() on BOTH authority and non-authority sides
                                    // - Unity fake null pattern catches destroyed GameObjects
                                    //
                                    // FIX (Nov 2025): THREAD-SAFETY - Check BOTH maps (GONetId AND InstantiationId)
                                    // Background sync threads enumerate activeAutoSyncCompanionsByCodeGenerationIdMap concurrently while
                                    // main thread removes entries in OnDisable(). C# Dictionary is NOT thread-safe for concurrent read/write.
                                    // If participant was removed from one map but sync thread's enumerator captured stale state,
                                    // checking BOTH maps ensures we skip it. This prevents persistent BUNDLE-ABORT errors for
                                    // destroyed objects like GONetGlobal duplicates (auto-destroyed in Awake() during scene transitions).
                                    if (participant == null ||  // Unity fake null - GameObject destroyed
                                        participant.GONetId == GONetParticipant.GONetId_Unset ||  // GONetId was reset/unset
                                        participant.GONetIdAtInstantiation == GONetParticipant.GONetId_Unset ||  // InstantiationId was reset/unset
                                        !gonetParticipantByGONetIdMap.ContainsKey(participant.GONetId) ||  // Participant despawned - removed from GONetId map
                                        !gonetParticipantByGONetIdAtInstantiationMap.ContainsKey(participant.GONetIdAtInstantiation))  // Participant despawned - removed from InstantiationId map (catches race conditions)
                                    {
                                        continue; // Skip this participant - it's destroyed or despawned
                                    }

                                    // PHYSICS SYNC SEPARATION: Skip physics objects in non-physics pipeline, skip non-physics objects in physics pipeline
                                    // This prevents double-syncing position/rotation (once from regular 24Hz pipeline, once from physics 50Hz pipeline).
                                    // Physics pipeline (grouping_physics_unreliable): ONLY process physics objects owned by this authority
                                    // All other pipelines: ONLY process non-physics objects OR objects not owned by this authority
                                    bool isPhysicsObject = participant.IsRigidBodyOwnerOnlyControlled && participant.myRigidBody != null;
                                    bool shouldProcessInPhysicsPipeline = isPhysicsObject && participant.IsMine; // Only send physics updates if I own the object

                                    // DIAG: Log for GONetId 413695
                                    if (IsServer && participant.GONetId == 413695)
                                    {
                                        //GONetLog.Debug($"[SYNC-413695] ITER: grp={uniqueGrouping.scheduleFrequency}/{uniqueGrouping.reliability}, IsMine={participant.IsMine}, Owner={participant.OwnerAuthorityId}, isPhys={isPhysicsObject}, pos={participant.transform.position}");
                                    }

                                    if (isPhysicsSyncGrouping)
                                    {
                                        // In physics sync grouping: ONLY process objects I own that are physics objects
                                        if (!shouldProcessInPhysicsPipeline)
                                        {
                                            continue; // Skip: not my physics object
                                        }
                                        // PHYSICS SYNC FREQUENCY GATING: Now handled per-value in IsPositionNotSyncd/IsRotationNotSyncd
                                        // This allows position and rotation to have independent PhysicsUpdateInterval settings
                                    }
                                    // NOTE: For non-physics groupings (like 0.05/Unreliable for animator params), we DON'T skip
                                    // physics objects entirely. We let the per-value matching logic in DoesMatchUniqueGrouping
                                    // handle which values get synced. This ensures animator params on physics objects are still
                                    // synced via their appropriate scheduler, while position/rotation are handled by physics pipeline.
                                    // The physics pipeline (0/Unreliable with mustRunOnUnityMainThread=true) handles position/rotation,
                                    // but animator params have their own frequencies (0.05 for floats, 0 for int/bool) and must
                                    // be processed by their respective schedulers.

                                    if (signalNeedToResetAtRest_untilBetterWayToDealWithThisSituation)
                                    {
                                        monitoringSupport.ResetAtRestValues(uniqueGrouping);
                                    }

                                    // need to call this for every single one to keep track of changes, BUT we only want to consider/process ones that match the current frequency:
                                    monitoringSupport.UpdateLastKnownValues(uniqueGrouping); // IMPORTANT: passing in the frequency here narrows down what gets appended to only ones with frequency match
                                    bool hasChanges = monitoringSupport.HaveAnyValuesChangedSinceLastCheck_AppendNewlyAtRest(uniqueGrouping, myTicks, valuesNowAtRestToBroadcast);

                                    // DIAG: Log change detection for GONetId 413695
                                    if (IsServer && participant.GONetId == 413695)
                                    {
                                        //GONetLog.Debug($"[SYNC-413695] DETECT: hasChanges={hasChanges}, toSend={syncValuesToSend.Count}, atRest={valuesNowAtRestToBroadcast.Count}");
                                    }

                                    if (hasChanges) // IMPORTANT: passing in the frequency here narrows down what gets appended to only ones with frequency match
                                    {
                                        monitoringSupport.AnnotateMyBaselineValuesNeedingAdjustment();
                                        monitoringSupport.AppendListWithChangesSinceLastCheck(syncValuesToSend, uniqueGrouping); // IMPORTANT: passing in the frequency here narrows down what gets appended to only ones with frequency match
                                        monitoringSupport.OnValueChangeCheck_Reset(uniqueGrouping); // IMPORTANT: passing in the frequency here narrows down what gets appended to only ones with frequency match
                                    }
                                }
                            }
                        }
                    }

                    bundleFragmentsMadeCount += SendSyncValueBundlesToRelevantParties_IfAppropriate(syncValuesToSend, myTicks, typeof(AutoMagicalSync_ValueChanges_Message));
                    bundleFragmentsMadeCount += SendSyncValueBundlesToRelevantParties_IfAppropriate(valuesNowAtRestToBroadcast, myTicks, typeof(AutoMagicalSync_ValuesNowAtRest_Message));

                    { // all this to call ApplyAnnotatedBaselineValueAdjustments()
                        Queue<IGONetEvent> baselineAdjustmentsEventQueue = events_AwaitingSendToOthersQueue_ByThreadMap[Thread.CurrentThread];
                        using (var enumeratorOuter = everythingMap_evenStuffNotOnThisScheduleFrequency.GetEnumerator())
                        {
                            while (enumeratorOuter.MoveNext())
                            {
                                Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> currentMap = enumeratorOuter.Current.Value;
                                if (currentMap == null)
                                {
                                    GONetLog.Error("currentMap == null");
                                }

                                using (var enumeratorInner = currentMap.GetEnumerator())
                                {
                                    while (enumeratorInner.MoveNext())
                                    {
                                        GONetParticipant participant = enumeratorInner.Current.Key;
                                        GONetParticipant_AutoMagicalSyncCompanion_Generated monitoringSupport = enumeratorInner.Current.Value;

                                        if (monitoringSupport == null)
                                        {
                                            GONetLog.Error("monitoringSupport == null");
                                            continue;
                                        }

                                        // CRITICAL FIX: Skip destroyed/despawned participants (same check as main sync loop above)
                                        // FIX (Nov 2025): Check BOTH maps for thread-safety (see main sync loop for detailed explanation)
                                        if (participant == null ||
                                            participant.GONetId == GONetParticipant.GONetId_Unset ||
                                            participant.GONetIdAtInstantiation == GONetParticipant.GONetId_Unset ||
                                            !gonetParticipantByGONetIdMap.ContainsKey(participant.GONetId) ||
                                            !gonetParticipantByGONetIdAtInstantiationMap.ContainsKey(participant.GONetIdAtInstantiation))
                                        {
                                            continue; // Skip this participant - it's destroyed or despawned
                                        }

                                        monitoringSupport.ApplyAnnotatedBaselineValueAdjustments(baselineAdjustmentsEventQueue); // we figured out when this needs to get called....and it is now, AFTER the send of the changes accumulated herein to avoid using new baseline incorrectly
                                    }
                                }
                            }
                        }
                    }

                    PublishEvents_SyncValueChangesSentToOthers_ASAP();
                    signalNeedToResetAtRest_untilBetterWayToDealWithThisSituation = false;
                }
                catch (InvalidOperationException ioe)
                {
                    const string ENUMERATION_WHILE_MODOFYING = "Collection was modified; enumeration operation may not execute.";
                    bool willWeASSumeThisIsExpected = !uniqueGrouping.mustRunOnUnityMainThread && ioe.Message == ENUMERATION_WHILE_MODOFYING; // TODO need to add in a clause for only happening early on during a new GNP cycle this could happen when running in separate thread (ie., not unity main thread)
                    if (willWeASSumeThisIsExpected)
                    {
                        const string SEMI = "Semi-expected error attempting to process auto-magical syncs on separate thread (i.e., not unity main thread).  It is only expected when a new GNP is being processed and some internal Dictionary is updated in main thread while we are processing here in this separate thread, in fact a bit prematurely.";
                        GONetLog.Warning(string.Concat(SEMI));

                        signalNeedToResetAtRest_untilBetterWayToDealWithThisSituation = true; // we want to reset any stuff marked as at rest above so it does not get stuck in bad state, but since we are already in a bad enumeration state not going to attempt an enumeration over what is likely the same data here...try it later
                    }
                    else
                    {
                        GONetLog.Error(string.Concat("Unexpected error attempting to process auto-magical syncs.  Exception.Type: ", ioe.GetType().Name, " Exception.Message: ", ioe.Message, " \nException.StackTrace: ", ioe.StackTrace));
                    }
                }
                catch (Exception e)
                {
                    GONetLog.Error(string.Concat("Unexpected error attempting to process auto-magical syncs.  Exception.Type: ", e.GetType().Name, " Exception.Message: ", e.Message, " \nException.StackTrace: ", e.StackTrace));
                }

                /*
                if (bundleFragmentsMadeCount > 0)
                {
                    long endTicks = HighResolutionTimeUtils.UtcNow.Ticks;
                    GONetLog.Debug("[DREETS] bundleFragmentsMadeCount: " + bundleFragmentsMadeCount + " duration(ms): " + TimeSpan.FromTicks(endTicks - startTicks).TotalMilliseconds);
                }
                */
            }

            /// <returns>The number of bundle fragments made</returns>
            private int SendSyncValueBundlesToRelevantParties_IfAppropriate(List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue> syncValuesForBundles, long relatedElapsedTicks, Type chosenBundleType)
            {
                int bundleFragmentsMadeCount = 0;
                int count = syncValuesForBundles.Count;
                //GONetLog.Debug($"????????send changed auto-magical sync values to all connections..count: {count}");
                if (count > 0)
                {
                    GONetChannelId useThisChannelId = chosenBundleType == typeof(AutoMagicalSync_ValueChanges_Message) ? uniqueGrouping_valueChanges_channelId : uniqueGrouping_valuesNowAtRest_channelId;  // TODO this is fairly hardcoded and limited in terms of options, but right now this is all...and need to just move on to test how it will work before making this more configurable

                    //GONetLog.Debug("sending changed auto-magical sync values to all connections");
                    if (IsServer)
                    {
                        // if its the server, we have to consider who we are sending to and ensure we do not send then changes that initially came from them!
                        if (_gonetServer != null)
                        {
                            // CRITICAL FIX (Dec 2025): Early exit if no real clients exist.
                            // In HOST mode with only loopback connection (or no connections), skip serialization entirely.
                            // Without this, sync bundles serialize ~180 changes per frame even with no recipients,
                            // contributing to severe frame rate degradation (1 FPS).
                            int numConnections = (int)_gonetServer.numConnections;
                            bool hasRealClient = false;
                            for (int i = 0; i < numConnections; i++)
                            {
                                var conn = _gonetServer.remoteClients[i]?.ConnectionToClient;
                                if (conn != null &&
                                    !(conn is GONetConnection_ClientHostLoopback) &&
                                    conn.OwnerAuthorityId != OwnerAuthorityId_Server)
                                {
                                    hasRealClient = true;
                                    break;
                                }
                            }
                            if (!hasRealClient)
                            {
                                return bundleFragmentsMadeCount; // No real clients - skip expensive serialization
                            }

                            WholeBundleOfChoiceFragments bundleFragments;

                            // Only send out changes I own as server (i.e., passing in MyAuthorityId for filter).
                            // Clients will get other clients' changes they own from server auto-forward elsewhere
                            //  (see ProcessIncomingBytes_QueuedNetworkData_MainThread_INTERNAL-Server_SendBytesToNonSourceClients).
                            SerializeWhole_BundleOfChoice(syncValuesForBundles, myThread_valueChangeSerializationArrayPool, MyAuthorityId, relatedElapsedTicks, chosenBundleType, out bundleFragments);

                            // PHASE 2 FIX: Round-robin client processing to distribute server-side delay fairly
                            // NOTE: numConnections already declared above in early-exit check
                            int startIndex = _gonetServer.nextClientProcessingStartIndex;
                            if (numConnections > 0)
                            {
                                _gonetServer.nextClientProcessingStartIndex = (startIndex + 1) % numConnections;
                            }

                            for (int offset = 0; offset < numConnections; ++offset)
                            {
                                int iConnection = (startIndex + offset) % numConnections;
                                GONetConnection_ServerToClient gONetConnection_ServerToClient = _gonetServer.remoteClients[iConnection].ConnectionToClient;

                                // HOST MODE CPU OPTIMIZATION: Skip sending sync bundles to loopback connection.
                                // Host already has ALL sync data locally (it's the server!) - sending through loopback
                                // is pure redundant overhead (serialize → send → deserialize same data in same process).
                                // This reduces host CPU by ~35% (from 3x to 2x compared to pure client).
                                if (gONetConnection_ServerToClient is GONetConnection_ClientHostLoopback)
                                {
                                    continue; // Skip loopback - host already has this data
                                }

                                // HOT STANDBY HARDENING: No real gameplay client should ever have server authority (1023).
                                // If this happens it is almost certainly a stale standby mesh link preserved across promotion.
                                // Skip to avoid sync spam/retransmits and let promotion cleanup disconnect it.
                                if (gONetConnection_ServerToClient.OwnerAuthorityId == OwnerAuthorityId_Server)
                                {
                                    continue;
                                }

                                GONetRemoteClient remoteClient = _gonetServer.GetRemoteClientByAuthorityId(gONetConnection_ServerToClient.OwnerAuthorityId);
                                bool isInitialized = remoteClient.IsInitializedWithServer;

                                // DIAGNOSTIC: Log sync bundle gate checks (blocked AND allowed)
                                // Enhanced post-handoff diagnostic to trace why client 3 might not be receiving sync
                                if (bundleFragments.fragmentCount > 0)
                                {
                                    if (!isInitialized)
                                    {
                                        bool hasGONetLocalSpawn = serverReceivedGONetLocalSpawnAuthorities.ContainsKey(gONetConnection_ServerToClient.OwnerAuthorityId);
                                        GONetLog.Warning($"[SYNC-BLOCKED] ⛔ Server NOT sending {bundleFragments.fragmentCount} sync bundle fragments to AuthorityId {gONetConnection_ServerToClient.OwnerAuthorityId} - " +
                                                         $"IsInitializedWithServer: {isInitialized}, GONetLocalSpawnReceived: {hasGONetLocalSpawn} at {Time.ElapsedSeconds:F3}s");
                                    }
                                }

                                // HANDOFF-DIAG: Track sync send status to demoted host (authority 3 in typical voluntary handoff)
                                if (gONetConnection_ServerToClient.OwnerAuthorityId == 3 && _handoffDiag_lastLogTick + 1200000000 < Time.ElapsedTicks) // ~120 frames
                                {
                                    _handoffDiag_lastLogTick = Time.ElapsedTicks;
                                    GONetLog.Warning($"[HANDOFF-SYNC-DIAG] Client 3 status: isInit={isInitialized}, fragments={bundleFragments.fragmentCount}, loopback={gONetConnection_ServerToClient is GONetConnection_ClientHostLoopback}, serverAuth={gONetConnection_ServerToClient.OwnerAuthorityId == OwnerAuthorityId_Server}");
                                }

                                for (int iFragment = 0; iFragment < bundleFragments.fragmentCount; ++iFragment)
                                {
                                    //GONetLog.Debug("AutoMagicalSync_ValueChanges_Message sending right after this. bytesUsedCount: " + bundleFragments.fragmentBytesUsedCount[iFragment]);  /////////////////////////// DREETS!
                                    if (isInitialized) // only send to client initialized with server!
                                    {
                                        SendBytesToRemoteConnection(gONetConnection_ServerToClient, bundleFragments.fragmentBytes[iFragment], bundleFragments.fragmentBytesUsedCount[iFragment], useThisChannelId);
                                    }
                                }
                            }

                            for (int iFragment = 0; iFragment < bundleFragments.fragmentCount; ++iFragment)
                            {
                                myThread_valueChangeSerializationArrayPool.Return(bundleFragments.fragmentBytes[iFragment]);
                            }

                            bundleFragmentsMadeCount += bundleFragments.fragmentCount;
                        }
                    }
                    else
                    {
                        WholeBundleOfChoiceFragments bundleFragments;
                        if (chosenBundleType == typeof(AutoMagicalSync_ValuesNowAtRest_Message))
                        {
                            SerializeWhole_NowAtRestBundle(syncValuesForBundles, myThread_valueChangeSerializationArrayPool, MyAuthorityId, relatedElapsedTicks, out bundleFragments);
                        }
                        else
                        {
                            SerializeWhole_ChangesBundle(syncValuesForBundles, myThread_valueChangeSerializationArrayPool, MyAuthorityId, relatedElapsedTicks, out bundleFragments);
                        }

                        for (int iFragment = 0; iFragment < bundleFragments.fragmentCount; ++iFragment)
                        {
                            byte[] changesSerialized = bundleFragments.fragmentBytes[iFragment];
                            SendBytesToRemoteConnections(changesSerialized, bundleFragments.fragmentBytesUsedCount[iFragment], useThisChannelId);
                            myThread_valueChangeSerializationArrayPool.Return(changesSerialized);
                        }

                        bundleFragmentsMadeCount += bundleFragments.fragmentCount;
                    }

                    if (chosenBundleType == typeof(AutoMagicalSync_ValuesNowAtRest_Message))
                    {
                        for (int i = 0; i < count; ++i)
                        {
                            AutoMagicalSync_ValueMonitoringSupport_ChangedValue valueMonitoringSupport = syncValuesForBundles[i];
                            valueMonitoringSupport.syncCompanion.IndicateAtRestBroadcasted(valueMonitoringSupport.index);
                        }
                    }
                }

                return bundleFragmentsMadeCount;
            }

            /// <summary>
            /// Promote Local Thread Events To Main Thread For Publishing since calling <see cref="GONetEventBus.Publish{T}(T, uint?)"/> is not to be called from multiple threads!
            /// </summary>
            private void PublishEvents_SyncValueChangesSentToOthers_ASAP()
            {
                Queue<IGONetEvent> queueAwaiting = events_AwaitingSendToOthersQueue_ByThreadMap[Thread.CurrentThread];
                RingBuffer<IGONetEvent> queueSend = events_SendToOthersQueue_ByThreadMap[Thread.CurrentThread];
                while (queueAwaiting.Count > 0)
                {
                    var @event = queueAwaiting.Dequeue();
                    if (!queueSend.TryWrite(@event))
                    {
                        // Handle buffer full scenario (e.g., retry or log an error)
                        GONetLog.Error($"Ring buffer is full! Could not publish event!  Event type: {@event.GetType().Name}");
                    }
                }
            }

            /// <summary>
            /// IMPORTANT: When <see cref="isSetupToRunInSeparateThread"/> is false, calling this will NOT yield a call to <see cref="Process"/> and caller must keep calling this method each frame until proper schedule cycle permits the call to <see cref="Process"/> to go through.
            /// </summary>
            internal void ProcessASAP()
            {
                if (isSetupToRunInSeparateThread)
                {
                    shouldProcessInSeparateThreadASAP = true;
                }
                else if (!IsNotSafeToProcess())
                {
                    if (scheduleFrequencyTicks == END_OF_FRAME_IN_WHICH_CHANGE_OCCURS_TICKS)
                    {
                        Process();
                    }
                    else
                    {
                        long nowTicks = HighResolutionTimeUtils.UtcNowTicks;

                        bool isFirstTimeThrough = lastScheduledProcessAtTicks == 0;
                        if (isFirstTimeThrough)
                        {
                            lastScheduledProcessAtTicks = nowTicks; // This value needs an initialization or else Process_ASAP will always processs EVERY frame unintentionally
                        }

                        bool isASAPNow = (nowTicks - lastScheduledProcessAtTicks) > scheduleFrequencyTicks;
                        if (isASAPNow)
                        {
                            Process();
                            lastScheduledProcessAtTicks += scheduleFrequencyTicks;
                        }
                    }
                }
            }

            public void Dispose()
            {
                if (isSetupToRunInSeparateThread)
                {
                    isThreadRunning = false;
                    thread.Abort();
                }
            }
        }

        /// <summary>
        /// this is used as changes are taking place over time....unlike <see cref="mainThread_valueChangeSerializationArrayPool"/>
        /// </summary>
        static readonly ConcurrentDictionary<AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable, ArrayPool<byte>> autoSyncProcessThread_valueChangeSerializationArrayPool_ThreadMap =
            new ConcurrentDictionary<AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable, ArrayPool<byte>>();
        /// <summary>
        /// This is used when sending currente state to newly connecting clients unlike <see cref="autoSyncProcessThread_valueChangeSerializationArrayPool_ThreadMap"/>
        /// </summary>
        static readonly ArrayPool<byte> mainThread_valueChangeSerializationArrayPool = new ArrayPool<byte>(100, 10, 1024, 2048);

        static readonly ArrayPool<byte> mainThread_miscSerializationArrayPool = new ArrayPool<byte>(100, 10, 1024, 2048);


        static readonly Dictionary<Type, uint> messageTypeToMessageIDMap = new Dictionary<Type, uint>(4096);
        static readonly Dictionary<uint, Type> messageTypeByMessageIDMap = new Dictionary<uint, Type>(4096);
        static uint nextMessageID;

        private static readonly byte[] EMPTY_CHANGES_BUNDLE = null;

        private static void InitMessageTypeToMessageIDMap()
        {
            try
            {
                foreach (var types in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.FullName)
                        .Select(a => a.GetLoadableTypes().Where(t => TypeUtils.IsTypeAInstanceOfTypeB(t, typeof(IGONetEvent)) && !t.IsAbstract).OrderBy(t2 => t2.FullName)))
                {
                    foreach (var type in types)
                    {
                        uint messageID = nextMessageID++;
                        messageTypeToMessageIDMap[type] = messageID;
                        messageTypeByMessageIDMap[messageID] = type;
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError(ex); // since our log stuffs does not work in static context within unity editor, use unity logging for this one
            }
        }

        /// <summary>
        /// <para>PRE: <paramref name="changes"/> size is greater than 0</para>
        /// <para>PRE: <paramref name="filterUsingOwnerAuthorityId"/> is not <see cref="OwnerAuthorityId_Unset"/> otherwise an exception is thrown</para>
        /// <para>POST: return a serialized packet with only the stuff that excludes <paramref name="filterUsingOwnerAuthorityId"/> as to not send to them (i.e., likely because they are the one who owns this data in the first place and already know this change occurred!)</para>
        /// <para>IMPORTANT: The caller is responsible for returning the returned byte[] to <paramref name="byteArrayPool"/></para>
        /// </summary>
        private static void SerializeWhole_ChangesBundle(List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue> changes, ArrayPool<byte> byteArrayPool, ushort filterUsingOwnerAuthorityId, long elapsedTicksAtCapture, out WholeBundleOfChoiceFragments bundleFragments)
        {
            SerializeWhole_BundleOfChoice(changes, byteArrayPool, filterUsingOwnerAuthorityId, elapsedTicksAtCapture, typeof(AutoMagicalSync_ValueChanges_Message), out bundleFragments);
        }

        private static void SerializeWhole_NowAtRestBundle(List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue> changes, ArrayPool<byte> byteArrayPool, ushort filterUsingOwnerAuthorityId, long elapsedTicksAtCapture, out WholeBundleOfChoiceFragments bundleFragments)
        {
            SerializeWhole_BundleOfChoice(changes, byteArrayPool, filterUsingOwnerAuthorityId, elapsedTicksAtCapture, typeof(AutoMagicalSync_ValuesNowAtRest_Message), out bundleFragments);
        }


        internal class WholeBundleOfChoiceFragments
        {
            public const int FRAGMENT_MAX_COUNT = 256;

            internal int fragmentCount;
            internal readonly byte[][] fragmentBytes;
            internal readonly int[] fragmentBytesUsedCount;

            internal WholeBundleOfChoiceFragments()
            {
                fragmentCount = 0;
                fragmentBytes = new byte[FRAGMENT_MAX_COUNT][];
                fragmentBytesUsedCount = new int[FRAGMENT_MAX_COUNT];
            }
        }

        static readonly ConcurrentDictionary<Thread, WholeBundleOfChoiceFragments> wholeBundleOfChoiceBuffersByThread = new ConcurrentDictionary<Thread, WholeBundleOfChoiceFragments>(4, 4);

        /// <summary>
        /// Velocity-augmented sync: Tracks bundle alternation between VALUE (even) and VELOCITY (odd).
        /// Thread-safe via Interlocked.Increment.
        /// </summary>
        // NOTE: Removed velocityBundleCounter - using time-based alternation instead (see SerializeWhole_BundleOfChoice)

        /// <param name="filterUsingOwnerAuthorityId">NOTE: pass in <see cref="OwnerAuthorityId_Unset"/> to NOT filter</param>
        private static void SerializeWhole_BundleOfChoice(
            List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue> changes, 
            ArrayPool<byte> byteArrayPool, 
            ushort filterUsingOwnerAuthorityId, 
            long elapsedTicksAtCapture, 
            Type chosenBundleType, 
            out WholeBundleOfChoiceFragments bundleFragments)
        {
            if (!wholeBundleOfChoiceBuffersByThread.TryGetValue(Thread.CurrentThread, out bundleFragments))
            {
                wholeBundleOfChoiceBuffersByThread[Thread.CurrentThread] = bundleFragments = new WholeBundleOfChoiceFragments();
            }

            int countTotal = changes.Count;
            int countFiltered = SerializeBody_ChangesBundle_PRE_OrderAndCountFiltered(changes, filterUsingOwnerAuthorityId);
            //GONetLog.Debug($"mikkyu magoo...countFilteres: {countFiltered}");
            int individualChangesCountRemaining = countFiltered;
            bundleFragments.fragmentCount = 0;

            if (countFiltered == 0)
            {
                return;
            }

            // VELOCITY-AUGMENTED SYNC: Partition changes by velocity magnitude + periodic anchoring
            // - Values within range → VELOCITY bundle (jitter elimination)
            // - Values exceeding range → VALUE bundle (fallback)
            // - Periodic forced anchors → VALUE bundle (drift prevention)
            // Result: Send up to TWO bundles this frame
            long currentTimeMs = elapsedTicksAtCapture / System.TimeSpan.TicksPerMillisecond;

            List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue> shouldSendAsVelocity_withinQuantizationRangeChanges = new(); // TODO pool!!!
            List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue> shouldSendAsValue_outOfQuantizationRangeChanges = new(); // TODO pool!!!

            int velocityEligibleCount = 0;
            int nonVelocityEligibleCount = 0;

            bool isAtRestBundle = chosenBundleType == typeof(AutoMagicalSync_ValuesNowAtRest_Message);

            // Partition changes based on velocity eligibility, range, and anchor timing
            for (int i = 0; i < countTotal; ++i)
            {
                AutoMagicalSync_ValueMonitoringSupport_ChangedValue change = changes[i];
                if (!ShouldSendChange(change, filterUsingOwnerAuthorityId))
                {
                    continue; // Skip filtered changes
                }

                // CRITICAL: Skip values with PENDING at-rest broadcast (will be sent via reliable AT-REST message)
                // Only skip if NEEDS_TO_BROADCAST (AT-REST message is queued), NOT if ALREADY_BROADCASTED (past event)
                // ALREADY_BROADCASTED is the initial state to avoid spam, but we MUST still send regular updates!
                var changesSupport = change.syncCompanion.valuesChangesSupport[change.index];
                if (!isAtRestBundle && change.syncCompanion.IsValuePendingAtRestBroadcast(change.index))
                {
                    continue; // Skip - at-rest message will be sent this frame
                }

                // Check if this value is velocity-eligible
                bool isVelocityEligible = changesSupport.isVelocityEligible;

                if (isVelocityEligible && !isAtRestBundle)
                {
                    velocityEligibleCount++;

                    // QUANTIZATION-AWARE ANCHORING: Send VALUE anchors intelligently
                    // Instead of time-based forced anchors, send anchors when actualValue ≈ quantizedValue
                    // This provides drift correction with ZERO visual jitter (imperceptible snaps)
                    bool shouldSendQuantizationAnchor = false;

                    // Check if velocity fits in quantization range (needed for both paths)
                    bool velocityWithinRange = change.syncCompanion.IsVelocityWithinQuantizationRange(change.index);

                    if (velocityWithinRange)
                    {
                        // Velocity within range - consider smart anchoring
                        if (changesSupport.codeGenerationMemberType == GONetSyncableValueTypes.UnityEngine_Quaternion)
                        {
                            // Get current rotation and calculate quantization error
                            Quaternion currentRotation = change.lastKnownValue.UnityEngine_Quaternion;

                            // Get quantization bits from settings (default: 9 bits for quaternions)
                            byte quantizeBits = (byte)changesSupport.syncAttribute_QuantizerSettingsGroup.quantizeToBitCount;
                            if (quantizeBits == 0) quantizeBits = 9; // Default for quaternions

                            float quantizationError = Utils.QuantizationUtils.GetQuaternionQuantizationError(
                                currentRotation,
                                quantizeBits);

                            // HYBRID ANCHORING STRATEGY:
                            // 1. PRIMARY: Quantization-aware anchors (optimal - zero visual snap)
                            // 2. FALLBACK: Time-based anchors (safety - prevent unbounded drift)

                            // Quantization-aware threshold: MUST be sub-quantization
                            // We're looking for moments when rotation is NEAR quantization grid boundaries
                            // 0% error = on boundary (perfect), 50% error = between boundaries (worst)
                            // Use 30% threshold = accept anchors when reasonably close to boundaries
                            float quantizationStepDegrees = 1.41421356f / (float)((1 << quantizeBits) - 1) * (180f / MathF.PI); // Smallest3 range / steps * rad2deg
                            float MAX_ANCHOR_ERROR_DEGREES = quantizationStepDegrees * 0.30f; // 30% of step (close to boundary)

                            // Time-based fallback threshold (from profile or global setting)
                            float maxTimeWithoutAnchor = changesSupport.syncAttribute_VelocityAnchorIntervalSeconds;
                            if (maxTimeWithoutAnchor == 0f)
                            {
                                maxTimeWithoutAnchor = GONetGlobal.Instance.velocityAnchorIntervalSeconds;
                            }

                            long timeSinceLastAnchor = elapsedTicksAtCapture - changesSupport.lastAnchorTimeTicks;
                            float timeSinceLastAnchorSeconds = timeSinceLastAnchor / (float)TimeSpan.TicksPerSecond;

                            // RATE LIMITING: Use fallback interval as minimum (consistent with Vector3 Phase 2)
                            // Prevents VALUE spam - max 1 anchor/sec (default)
                            bool timingAllowsAnchor = timeSinceLastAnchorSeconds >= maxTimeWithoutAnchor;

                            // DECISION: Only send anchor if timing allows (prevents VALUE spam)
                            if (timingAllowsAnchor)
                            {
                                shouldSendQuantizationAnchor = true;
                            }
                        }
                        else if (changesSupport.codeGenerationMemberType == GONetSyncableValueTypes.UnityEngine_Vector3)
                        {
                            // Get current position
                            Vector3 currentValue = change.lastKnownValue.UnityEngine_Vector3;
                            Vector3 previousValue = change.lastKnownValue_previous.UnityEngine_Vector3;

                            // Get quantization settings
                            byte quantizeBits = (byte)changesSupport.syncAttribute_QuantizerSettingsGroup.quantizeToBitCount;
                            float lowerBound = changesSupport.syncAttribute_QuantizerSettingsGroup.lowerBound;
                            float upperBound = changesSupport.syncAttribute_QuantizerSettingsGroup.upperBound;

                            if (quantizeBits > 0) // Only if quantization is enabled
                            {
                                // PER-COMPONENT BOUNDARY CHECK: All components must be within 30% for clean anchor
                                const float THRESHOLD_FRACTION = 0.30f;
                                bool allComponentsNearBoundary = Utils.QuantizationUtils.IsVector3NearQuantizationBoundary(
                                    currentValue,
                                    lowerBound,
                                    upperBound,
                                    quantizeBits,
                                    THRESHOLD_FRACTION,
                                    out float errorX,
                                    out float errorY,
                                    out float errorZ,
                                    out float threshold);

                                // PHASE 1: MOTION DETECTION (Observational Only - No Behavior Change)
                                // Calculate per-component delta to identify which components are moving
                                Vector3 delta = currentValue - previousValue;

                                // Motion epsilon: 1% of quantization step (filters float precision noise)
                                float range = upperBound - lowerBound;
                                float quantizationStep = range / (float)((1 << quantizeBits) - 1);
                                float motionEpsilon = quantizationStep * 0.01f;  // e.g., 0.15mm for 14-bit

                                // Determine which components are "moving" (above epsilon threshold)
                                bool xMoving = Mathf.Abs(delta.x) >= motionEpsilon;
                                bool yMoving = Mathf.Abs(delta.y) >= motionEpsilon;
                                bool zMoving = Mathf.Abs(delta.z) >= motionEpsilon;

                                // RATE LIMITING: Use fallback interval as minimum for BOTH anchor types
                                // This prevents VALUE spam even if we find "perfect" moments frequently
                                float maxTimeWithoutAnchor = changesSupport.syncAttribute_VelocityAnchorIntervalSeconds;
                                if (maxTimeWithoutAnchor == 0f)
                                {
                                    maxTimeWithoutAnchor = GONetGlobal.Instance.velocityAnchorIntervalSeconds;
                                }

                                long timeSinceLastAnchor = elapsedTicksAtCapture - changesSupport.lastAnchorTimeTicks;
                                float timeSinceLastAnchorSeconds = timeSinceLastAnchor / (float)TimeSpan.TicksPerSecond;
                                bool timingAllowsAnchor = timeSinceLastAnchorSeconds >= maxTimeWithoutAnchor;

                                // DECISION: Only send anchor if timing allows (prevents VALUE spam)
                                if (timingAllowsAnchor)
                                {
                                    shouldSendQuantizationAnchor = true;
                                }
                            }
                        }
                        else if (changesSupport.codeGenerationMemberType == GONetSyncableValueTypes.UnityEngine_Vector2)
                        {
                            // Get current value
                            Vector2 currentValue = change.lastKnownValue.UnityEngine_Vector2;
                            Vector2 previousValue = change.lastKnownValue_previous.UnityEngine_Vector2;

                            // Get quantization settings
                            byte quantizeBits = (byte)changesSupport.syncAttribute_QuantizerSettingsGroup.quantizeToBitCount;
                            float lowerBound = changesSupport.syncAttribute_QuantizerSettingsGroup.lowerBound;
                            float upperBound = changesSupport.syncAttribute_QuantizerSettingsGroup.upperBound;

                            if (quantizeBits > 0) // Only if quantization is enabled
                            {
                                // PER-COMPONENT BOUNDARY CHECK
                                const float THRESHOLD_FRACTION = 0.30f;
                                bool allComponentsNearBoundary = Utils.QuantizationUtils.IsVector2NearQuantizationBoundary(
                                    currentValue,
                                    lowerBound,
                                    upperBound,
                                    quantizeBits,
                                    THRESHOLD_FRACTION,
                                    out float errorX,
                                    out float errorY,
                                    out float threshold);

                                // PHASE 1: MOTION DETECTION
                                Vector2 delta = currentValue - previousValue;
                                float range = upperBound - lowerBound;
                                float quantizationStep = range / (float)((1 << quantizeBits) - 1);
                                float motionEpsilon = quantizationStep * 0.01f;
                                bool xMoving = Mathf.Abs(delta.x) >= motionEpsilon;
                                bool yMoving = Mathf.Abs(delta.y) >= motionEpsilon;

                                // RATE LIMITING
                                float maxTimeWithoutAnchor = changesSupport.syncAttribute_VelocityAnchorIntervalSeconds;
                                if (maxTimeWithoutAnchor == 0f)
                                {
                                    maxTimeWithoutAnchor = GONetGlobal.Instance.velocityAnchorIntervalSeconds;
                                }

                                long timeSinceLastAnchor = elapsedTicksAtCapture - changesSupport.lastAnchorTimeTicks;
                                float timeSinceLastAnchorSeconds = timeSinceLastAnchor / (float)TimeSpan.TicksPerSecond;
                                bool timingAllowsAnchor = timeSinceLastAnchorSeconds >= maxTimeWithoutAnchor;

                                // DECISION: Only send anchor if timing allows (prevents VALUE spam)
                                if (timingAllowsAnchor)
                                {
                                    shouldSendQuantizationAnchor = true;
                                }
                            }
                        }
                        else if (changesSupport.codeGenerationMemberType == GONetSyncableValueTypes.UnityEngine_Vector4)
                        {
                            // Get current value
                            Vector4 currentValue = change.lastKnownValue.UnityEngine_Vector4;
                            Vector4 previousValue = change.lastKnownValue_previous.UnityEngine_Vector4; // PHASE 1: For motion detection

                            // Get quantization settings
                            byte quantizeBits = (byte)changesSupport.syncAttribute_QuantizerSettingsGroup.quantizeToBitCount;
                            float lowerBound = changesSupport.syncAttribute_QuantizerSettingsGroup.lowerBound;
                            float upperBound = changesSupport.syncAttribute_QuantizerSettingsGroup.upperBound;

                            if (quantizeBits > 0)
                            {
                                // PER-COMPONENT BOUNDARY CHECK
                                const float THRESHOLD_FRACTION = 0.30f;
                                bool allComponentsNearBoundary = Utils.QuantizationUtils.IsVector4NearQuantizationBoundary(
                                    currentValue,
                                    lowerBound,
                                    upperBound,
                                    quantizeBits,
                                    THRESHOLD_FRACTION,
                                    out float errorX,
                                    out float errorY,
                                    out float errorZ,
                                    out float errorW,
                                    out float threshold);

                                // PHASE 1: MOTION DETECTION (Observational Only - No Behavior Change)
                                Vector4 delta = currentValue - previousValue;

                                // Motion epsilon: 1% of quantization step (filters float precision noise)
                                float range = upperBound - lowerBound;
                                float quantizationStep = range / (float)((1 << quantizeBits) - 1);
                                float motionEpsilon = quantizationStep * 0.01f;

                                // Determine which components are "moving" (above epsilon threshold)
                                bool xMoving = Mathf.Abs(delta.x) >= motionEpsilon;
                                bool yMoving = Mathf.Abs(delta.y) >= motionEpsilon;
                                bool zMoving = Mathf.Abs(delta.z) >= motionEpsilon;
                                bool wMoving = Mathf.Abs(delta.w) >= motionEpsilon;

                                // RATE LIMITING
                                float maxTimeWithoutAnchor = changesSupport.syncAttribute_VelocityAnchorIntervalSeconds;
                                if (maxTimeWithoutAnchor == 0f)
                                {
                                    maxTimeWithoutAnchor = GONetGlobal.Instance.velocityAnchorIntervalSeconds;
                                }

                                long timeSinceLastAnchor = elapsedTicksAtCapture - changesSupport.lastAnchorTimeTicks;
                                float timeSinceLastAnchorSeconds = timeSinceLastAnchor / (float)TimeSpan.TicksPerSecond;
                                bool timingAllowsAnchor = timeSinceLastAnchorSeconds >= maxTimeWithoutAnchor;

                                // DECISION: Only send anchor if timing allows (prevents VALUE spam)
                                if (timingAllowsAnchor)
                                {
                                    shouldSendQuantizationAnchor = true;
                                }
                            }
                        }
                        else if (changesSupport.codeGenerationMemberType == GONetSyncableValueTypes.System_Single)
                        {
                            // Get current float value
                            float currentValue = change.lastKnownValue.System_Single;

                            // Get quantization settings
                            byte quantizeBits = (byte)changesSupport.syncAttribute_QuantizerSettingsGroup.quantizeToBitCount;
                            float lowerBound = changesSupport.syncAttribute_QuantizerSettingsGroup.lowerBound;
                            float upperBound = changesSupport.syncAttribute_QuantizerSettingsGroup.upperBound;

                            if (quantizeBits > 0)
                            {
                                // BOUNDARY CHECK (single component)
                                const float THRESHOLD_FRACTION = 0.30f;
                                bool nearBoundary = Utils.QuantizationUtils.IsFloatNearQuantizationBoundary(
                                    currentValue,
                                    lowerBound,
                                    upperBound,
                                    quantizeBits,
                                    THRESHOLD_FRACTION,
                                    out float error,
                                    out float threshold);

                                // RATE LIMITING
                                float maxTimeWithoutAnchor = changesSupport.syncAttribute_VelocityAnchorIntervalSeconds;
                                if (maxTimeWithoutAnchor == 0f)
                                {
                                    maxTimeWithoutAnchor = GONetGlobal.Instance.velocityAnchorIntervalSeconds;
                                }

                                long timeSinceLastAnchor = elapsedTicksAtCapture - changesSupport.lastAnchorTimeTicks;
                                float timeSinceLastAnchorSeconds = timeSinceLastAnchor / (float)TimeSpan.TicksPerSecond;
                                bool timingAllowsAnchor = timeSinceLastAnchorSeconds >= maxTimeWithoutAnchor;

                                // DECISION
                                if (timingAllowsAnchor)
                                {
                                    shouldSendQuantizationAnchor = true;
                                }
                            }
                        }

                        if (shouldSendQuantizationAnchor)
                        {
                            // Send VALUE anchor (actualValue ≈ quantizedValue = zero visual snap)
                            shouldSendAsValue_outOfQuantizationRangeChanges.Add(change);
                            changesSupport.lastAnchorTimeTicks = elapsedTicksAtCapture;
                        }
                        else
                        {
                            // Send VELOCITY bundle (smooth synthesis)
                            shouldSendAsVelocity_withinQuantizationRangeChanges.Add(change);
                        }
                    }
                    else
                    {
                        // Velocity out of range → VALUE anchor (automatic drift correction)
                        shouldSendAsValue_outOfQuantizationRangeChanges.Add(change);
                        changesSupport.lastAnchorTimeTicks = elapsedTicksAtCapture;
                    }
                }
                else
                {
                    nonVelocityEligibleCount++;
                    // Non-velocity-eligible values always go in VALUE bundle
                    shouldSendAsValue_outOfQuantizationRangeChanges.Add(change);
                }
            }

            // Send VELOCITY bundle for values within range
            if (shouldSendAsVelocity_withinQuantizationRangeChanges.Count > 0)
            {
                SerializeWhole_BundleOfChoice_Internal(shouldSendAsVelocity_withinQuantizationRangeChanges, byteArrayPool, filterUsingOwnerAuthorityId, elapsedTicksAtCapture, chosenBundleType, true, ref bundleFragments);
            }

            // Send VALUE bundle for values out of range, forced anchors, and non-velocity-eligible values
            if (shouldSendAsValue_outOfQuantizationRangeChanges.Count > 0)
            {
                SerializeWhole_BundleOfChoice_Internal(shouldSendAsValue_outOfQuantizationRangeChanges, byteArrayPool, filterUsingOwnerAuthorityId, elapsedTicksAtCapture, chosenBundleType, false, ref bundleFragments);
            }
        }

        /// <summary>
        /// Internal helper for serializing a bundle with specified changes and velocity mode.
        /// Used by SerializeWhole_BundleOfChoice to send separate VELOCITY and VALUE bundles on VELOCITY frames.
        /// </summary>
        private static void SerializeWhole_BundleOfChoice_Internal(
            List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue> changes,
            ArrayPool<byte> byteArrayPool,
            ushort filterUsingOwnerAuthorityId,
            long elapsedTicksAtCapture,
            Type chosenBundleType,
            bool isVelocityBundle,
            ref WholeBundleOfChoiceFragments bundleFragments)
        {
            int countTotal = changes.Count;
            int individualChangesCountRemaining = countTotal;
            int lastIndexUsed = 0;

            while (individualChangesCountRemaining > 0)
            {
                using (BitByBitByteArrayBuilder bitStream = BitByBitByteArrayBuilder.GetBuilder())
                {
                    { // header...just message type/id...well, and now time...and velocity flag
                        uint messageID = messageTypeToMessageIDMap[chosenBundleType];
                        bitStream.WriteUInt(messageID);

                        bitStream.WriteLong(elapsedTicksAtCapture);

                        // VELOCITY-AUGMENTED SYNC: Write velocity bit (ONE bit for entire bundle)
                        bitStream.WriteBit(isVelocityBundle);
                    }

                    // body
                    int changesInBundleCount = SerializeBody_ChangesBundle(changes, bitStream, filterUsingOwnerAuthorityId, ref lastIndexUsed, isVelocityBundle);

                    if (changesInBundleCount > 0)
                    {
                        bitStream.WriteCurrentPartialByte();

                        var byteCount = bitStream.Length_WrittenBytes;
                        bundleFragments.fragmentBytesUsedCount[bundleFragments.fragmentCount] = byteCount;
                        byte[] bytes = byteArrayPool.Borrow(byteCount);
                        Array.Copy(bitStream.GetBuffer(), 0, bytes, 0, byteCount);
                        bundleFragments.fragmentBytes[bundleFragments.fragmentCount] = bytes;

                        individualChangesCountRemaining -= changesInBundleCount;
                        bundleFragments.fragmentCount++;
                    }
                    else
                    {
                        if (individualChangesCountRemaining > 0)
                        {
                            GONetLog.Warning("Why mismatch in remaining expected versus actual.  This could be serious!");
                        }
                        break;
                    }
                }
            }
        }

        class AutoMagicalSyncChangePriorityComparer : IComparer<AutoMagicalSync_ValueMonitoringSupport_ChangedValue>
        {
            internal static readonly AutoMagicalSyncChangePriorityComparer Instance = new AutoMagicalSyncChangePriorityComparer();

            private AutoMagicalSyncChangePriorityComparer() { }

            public int Compare(AutoMagicalSync_ValueMonitoringSupport_ChangedValue x, AutoMagicalSync_ValueMonitoringSupport_ChangedValue y)
            {
                int xPriority = x.syncAttribute_ProcessingPriority_GONetInternalOverride != 0 ? x.syncAttribute_ProcessingPriority_GONetInternalOverride : x.syncAttribute_ProcessingPriority;
                int yPriority = y.syncAttribute_ProcessingPriority_GONetInternalOverride != 0 ? y.syncAttribute_ProcessingPriority_GONetInternalOverride : y.syncAttribute_ProcessingPriority;

                int priorityComparison = yPriority.CompareTo(xPriority); // descending...highest priority first!

                if (priorityComparison == 0)
                { // if the priority is the same, then we want to put the most recent (i.e., highest value) changes in authority last as to not possibly cause issue during deserialize of an entire bundle because the owner authority change has not been processed yet!
                    return x.syncCompanion.gonetParticipant.OwnerAuthorityId_LastChangedElapsedSeconds
                        .CompareTo(y.syncCompanion.gonetParticipant.OwnerAuthorityId_LastChangedElapsedSeconds);
                }

                return priorityComparison;
            }
        }

        private static void SerializeBody_AllCurrentValuesBundle(Utils.BitByBitByteArrayBuilder bitStream_headerAlreadyWritten)
        {
            int totalGNPs = 0;
            int serializedGNPs = 0;
            int excludedGNPs = 0;

            // IMPORTANT: Create a snapshot to avoid InvalidOperationException if the collection is modified during iteration
            // This can happen when a new client connects and their GONetLocal is spawned while we're serializing
            var enumeratorOuter = activeAutoSyncCompanionsByCodeGenerationIdMap.ToList().GetEnumerator();
            while (enumeratorOuter.MoveNext())
            {
                //GONetLog.Debug($"[INIT] SerializeBody: Processing code generation ID {enumeratorOuter.Current.Key}");
                Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> currentMap = enumeratorOuter.Current.Value;
               // GONetLog.Debug($"[INIT] SerializeBody: Code gen ID {enumeratorOuter.Current.Key} has {currentMap.Count} GNPs");

                // IMPORTANT: Also snapshot the inner dictionary to prevent concurrent modification
                var snapshot = currentMap.ToList();
                //GONetLog.Debug($"[INIT] SerializeBody: ToList() created snapshot with {snapshot.Count} items (original had {currentMap.Count})");
                var enumeratorInner = snapshot.GetEnumerator();
                int innerIterationCount = 0;
                while (enumeratorInner.MoveNext())
                {
                    var current = enumeratorInner.Current;
                    innerIterationCount++;
                    totalGNPs++;

                    // IMPORTANT: Check for null GNP or destroyed GameObject before accessing properties
                    if (current.Key == null || current.Key.gameObject == null)
                    {
                        GONetLog.Warning($"[INIT] SerializeBody: Iteration {innerIterationCount}/{currentMap.Count} - GNP is null or destroyed, skipping");
                        continue;
                    }

                    //GONetLog.Debug($"[INIT] SerializeBody: Iteration {innerIterationCount}/{currentMap.Count} - GNP: '{current.Key.gameObject.name}'");

                    GONetParticipant gonetParticipant = current.Key;
                    // IMPORTANT: Check both that all components are set AND that GONetId is not 0
                    // This can happen if a client connects after OnEnable but before Start assigns the GONetId
                    bool hasAllComponents = gonetParticipant.DoesGONetIdContainAllComponents();
                    bool idIsNotZero = gonetParticipant.GONetId != GONetParticipant.GONetId_Unset;

                    // DIAGNOSTIC: Capture GONetId at validation time to detect race conditions
                    uint gonetId_atValidation = gonetParticipant.GONetId;
                    uint gonetIdRaw_atValidation = gonetParticipant.gonetId_raw;
                    ushort authority_atValidation = gonetParticipant.OwnerAuthorityId;

                    if (hasAllComponents && idIsNotZero)
                    {
                        // DEFENSIVE: Re-check GONetId immediately before serialization to catch race conditions
                        uint gonetId_beforeSerialize = gonetParticipant.GONetId;
                        uint gonetIdRaw_beforeSerialize = gonetParticipant.gonetId_raw;
                        ushort authority_beforeSerialize = gonetParticipant.OwnerAuthorityId;

                        // CRITICAL: Check if GONetId changed between validation and serialization (RACE CONDITION!)
                        if (gonetId_beforeSerialize != gonetId_atValidation ||
                            gonetIdRaw_beforeSerialize != gonetIdRaw_atValidation ||
                            authority_beforeSerialize != authority_atValidation)
                        {
                            GONetLog.Error($"[RACE-CONDITION] GONetId CHANGED between validation and serialization! " +
                                          $"GNP: '{gonetParticipant.gameObject.name}' " +
                                          $"At validation: GONetId={gonetId_atValidation} (raw={gonetIdRaw_atValidation}, auth={authority_atValidation}) " +
                                          $"Before serialize: GONetId={gonetId_beforeSerialize} (raw={gonetIdRaw_beforeSerialize}, auth={authority_beforeSerialize}) " +
                                          $"WasDefinedInScene: {WasDefinedInScene(gonetParticipant)}");
                        }

                        // DEFENSIVE: Re-validate before serialization (catch race condition)
                        bool stillHasAllComponents = gonetParticipant.DoesGONetIdContainAllComponents();
                        bool stillNotZero = gonetParticipant.GONetId != GONetParticipant.GONetId_Unset;

                        if (!stillHasAllComponents || !stillNotZero)
                        {
                            // RACE CONDITION DETECTED: GONetId became incomplete between check and serialization!
                            GONetLog.Error($"[RACE-CONDITION-PREVENTED] GONetId became incomplete after validation! " +
                                          $"GNP: '{gonetParticipant.gameObject.name}' " +
                                          $"At validation: GONetId={gonetId_atValidation} (raw={gonetIdRaw_atValidation}, auth={authority_atValidation}, valid=true) " +
                                          $"Before serialize: GONetId={gonetId_beforeSerialize} (raw={gonetIdRaw_beforeSerialize}, auth={authority_beforeSerialize}, hasAll={stillHasAllComponents}, notZero={stillNotZero}) " +
                                          $"SKIPPING serialization to prevent client deserialization error!");
                            excludedGNPs++;
                            continue; // Skip this participant - don't serialize incomplete GONetId
                        }

                        // ULTRA-DIAGNOSTIC: Capture EXACT value being serialized
                        uint gonetId_toSerialize = gonetParticipant.GONetId;
                        uint rawId_toSerialize = gonetParticipant.gonetId_raw;
                        ushort authority_toSerialize = gonetParticipant.OwnerAuthorityId;

                        // CRITICAL SAFETY: Final sanity check on the EXACT value we're about to serialize
                        if (rawId_toSerialize == GONetParticipant.GONetIdRaw_Unset)
                        {
                            GONetLog.Error($"[SERIALIZE-BUG-CAUGHT] About to serialize GONetId with raw=0! " +
                                          $"GNP: '{gonetParticipant.gameObject.name}' " +
                                          $"GONetId: {gonetId_toSerialize} (raw={rawId_toSerialize}, auth={authority_toSerialize}) " +
                                          $"WasDefinedInScene: {WasDefinedInScene(gonetParticipant)} " +
                                          $"GONetIdAtInstantiation: {gonetParticipant.GONetIdAtInstantiation} " +
                                          $"PREVENTING serialization of incomplete GONetId!");
                            excludedGNPs++;
                            continue; // ABORT - don't serialize incomplete GONetId!
                        }

                        GONetParticipant.GONetId_InitialAssignment_CustomSerializer.Instance.Serialize(bitStream_headerAlreadyWritten, gonetParticipant, gonetParticipant.GONetId);

                        GONetParticipant_AutoMagicalSyncCompanion_Generated monitoringSupport = current.Value;
                        //GONetLog.Debug($"[INIT] About to call SerializeAll() for GNP '{gonetParticipant.gameObject.name}'");
                        try
                        {
                            monitoringSupport.SerializeAll(bitStream_headerAlreadyWritten);
                            //GONetLog.Debug($"[INIT] Completed SerializeAll() for GNP '{gonetParticipant.gameObject.name}'");
                            serializedGNPs++;
                        }
                        catch (System.Exception ex)
                        {
                            GONetLog.Error($"[INIT] Exception during SerializeAll() for GNP '{gonetParticipant.gameObject.name}': {ex.Message}\n{ex.StackTrace}");
                            throw; // Re-throw to preserve stack trace
                        }
                    }
                    else
                    {
                        excludedGNPs++;
                        GONetLog.Error($"Excluding GNP '{gonetParticipant.gameObject.name}' with partial GONetId: {gonetParticipant.GONetId} (raw: {gonetParticipant.gonetId_raw}, authority: {gonetParticipant.OwnerAuthorityId}) hasAllComponents: {hasAllComponents} idIsNotZero: {idIsNotZero} from all current values bundle.  WasDefinedInScene: {WasDefinedInScene(gonetParticipant)}");
                    }
                }
            }

            //GONetLog.Debug($"Serialization complete. Total GNPs: {totalGNPs}, Serialized: {serializedGNPs}, Excluded: {excludedGNPs}");
        }

        /// <summary>
        /// This is to be called only ONCE prior to possible multiple calls to <see cref="SerializeBody_ChangesBundle(List{AutoMagicalSync_ValueMonitoringSupport_ChangedValue}, BitByBitByteArrayBuilder, ushort)"/>
        /// since ordering and filtering count only needs to be done once.
        /// POST: <paramref name="changes"/> are ordered in place
        /// </summary>
        /// <returns>the filtered count of items in <paramref name="changes"/> using <paramref name="filterUsingOwnerAuthorityId"/> as the filtering out criteria</returns>
        /// <param name="filterUsingOwnerAuthorityId">NOTE: pass in <see cref="OwnerAuthorityId_Unset"/> to NOT filter</param>
        private static int SerializeBody_ChangesBundle_PRE_OrderAndCountFiltered(List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue> changes, ushort filterUsingOwnerAuthorityId)
        {
            int countMinus1 = changes.Count - 1;
            int countFiltered = 0;
            for (int iSort = 0; iSort < countMinus1; ++iSort) // manual sort to avoid GC
            {
                var changeA = changes[iSort];
                var changeB = changes[iSort + 1];
                if (AutoMagicalSyncChangePriorityComparer.Instance.Compare(changeA, changeB) > 0)
                {
                    changes[iSort + 1] = changeA;
                    changes[iSort] = changeB;
                }

                if (ShouldSendChange(changes[iSort], filterUsingOwnerAuthorityId)) // use this manual check to avoid Linq.Count(....) GC/perf hit
                {
                    ++countFiltered;
                }
            }
            if (ShouldSendChange(changes[countMinus1], filterUsingOwnerAuthorityId)) // use this manual check to avoid Linq.Count(....) GC/perf hit
            {
                ++countFiltered;
            }

            if (countFiltered == 0)
            {
                return 0; // <<<<<<<<<<<============================================================================  bail out early if there is nothing to add to bundle!!!!
            }

            return countFiltered;
        }

        /// <summary>
        /// Returns the number of changes actually included in/added to the <paramref name="bitStream_headerAlreadyWritten"/> AFTER any filtering this method does (e.g., checking <paramref name="filterUsingOwnerAuthorityId"/>).
        /// </summary>
        /// <param name="filterUsingOwnerAuthorityId">NOTE: pass in <see cref="OwnerAuthorityId_Unset"/> to NOT filter</param>
        /// <param name="isVelocityBundle">TRUE if this bundle contains velocity data, FALSE for value data</param>
        private static int SerializeBody_ChangesBundle(List<AutoMagicalSync_ValueMonitoringSupport_ChangedValue> changes, Utils.BitByBitByteArrayBuilder bitStream_headerAlreadyWritten, ushort filterUsingOwnerAuthorityId, ref int lastIndexUsed, bool isVelocityBundle)
        {
            //GONetLog.Debug("mikkyu magoo");

            int countTotal = changes.Count;
            int changesInBundle = 0;

            uint gonetId_previous = 0;
            Queue<IGONetEvent> syncEventQueue = events_AwaitingSendToOthersQueue_ByThreadMap[Thread.CurrentThread];
            const int CUTOFF = SerializationUtils.MTU; // TODO we can go higher when compression used
            for (int i = lastIndexUsed; i < countTotal && bitStream_headerAlreadyWritten.Length_WrittenBytes < CUTOFF; ++i) // only keep going on this if under MTU so the bundle does not get turned into fragments inside the low level networking layer...we handle at higher level in gonet now
            {
                AutoMagicalSync_ValueMonitoringSupport_ChangedValue change = changes[i];
                if (!ShouldSendChange(change, filterUsingOwnerAuthorityId))
                {
                    continue; // skip this guy (i.e., apply the "filter")
                }

                lastIndexUsed = i;
                ++changesInBundle;

#if !PERF_NO_PROCESS_SYNC_EVENTS
                syncEventQueue.Enqueue(GONet_SyncEvent_ValueChangeProcessed_Generated_Factory.CreateInstance(SyncEvent_ValueChangeProcessedExplanation.OutboundToOthers, Time.ElapsedTicks, filterUsingOwnerAuthorityId, change.syncCompanion, change.index));
#endif

                if (change.syncCompanion.gonetParticipant.gonetId_raw == GONetParticipant.GONetId_Unset)
                {
                    const string SNAFU = "Snafoo....gonetid 0.....why are we about to send change? ...makes no sense! ShouldSendChange(change, filterUsingOwnerAuthorityId): ";
                    const string FUOA = " filterUsingOwnerAuthorityId: ";
                    GONetLog.Error(string.Concat(SNAFU, ShouldSendChange(change, filterUsingOwnerAuthorityId), FUOA, filterUsingOwnerAuthorityId));
                    continue; // FIX (Dec 2025): Skip serializing incomplete GONetId to prevent client lookup failure
                }

                if (change.syncCompanion.gonetParticipant.GONetIdAtInstantiation == GONetParticipant.GONetId_Unset)
                {
                    const string SNAFU = "Snafoo....gonetIdAtInstantiation 0.....how is this possible? gnp.gonetId: ";
                    GONetLog.Error(string.Concat(SNAFU, change.syncCompanion.gonetParticipant.GONetId));
                    continue; // FIX (Dec 2025): Skip serializing without valid InstantiationId
                }

                { // have to write the gonetid first before each changed value
                    //GONetLog.Append(change.syncCompanion.gonetParticipant.GONetIdAtInstantiation + ", ");
                    uint gonetId = change.syncCompanion.gonetParticipant.GONetIdAtInstantiation;

                    long diffFromPrevious = gonetId - gonetId_previous;
                    bool isSameAsPrevious = diffFromPrevious == 0;
                    bitStream_headerAlreadyWritten.WriteBit(isSameAsPrevious);
                    if (isSameAsPrevious)
                    {
                        bool isDiffNegative_mustBeFalseToIndicateNormalFlowToContinueProcessingAsRealData = false;
                        bitStream_headerAlreadyWritten.WriteBit(isDiffNegative_mustBeFalseToIndicateNormalFlowToContinueProcessingAsRealData);
                    }
                    else
                    {
                        bool isDiffNegative = diffFromPrevious < 0;
                        uint gonetId_diff_unsigned = isDiffNegative ? (uint)(-diffFromPrevious) : (uint)diffFromPrevious;

                        uint gonetIdByteCount;
                        if (gonetId_diff_unsigned < 0x1_00_00)
                        {
                            if (gonetId_diff_unsigned < 0x1_00) gonetIdByteCount = 1;
                            else gonetIdByteCount = 2;
                        }
                        else if (gonetId_diff_unsigned < 0x1_00_00_00) gonetIdByteCount = 3;
                        else gonetIdByteCount = 4;

                        bitStream_headerAlreadyWritten.WriteBit(isDiffNegative);
                        bitStream_headerAlreadyWritten.WriteUInt(gonetIdByteCount - 1, 2); // since gonetId usually will take a smaller number of bytes than all 4 allotted, we can save some space like this
                        bitStream_headerAlreadyWritten.WriteUInt(gonetId_diff_unsigned, gonetIdByteCount << 3);
                    }

                    gonetId_previous = gonetId;
                }

                bitStream_headerAlreadyWritten.WriteByte(change.index); // then have to write the index, otherwise other end does not know which index to deserialize
                //GONetLog.AppendLine($"serialize change index: {change.index}");

                change.syncCompanion.SerializeSingle(bitStream_headerAlreadyWritten, change.index, isVelocityBundle);
            }
            //GONetLog.Append_FlushDebug();

            { // indicates end of bundle!  we write regardless of if changes added up top or not...no real harm
                bitStream_headerAlreadyWritten.WriteBit(true);
                bitStream_headerAlreadyWritten.WriteBit(true); // true here for isDiffNegative coming right after true for isSameAsPrevious is all it takes to indicate an impossible normal state, which is the end of the content!
            }

            return changesInBundle;
        }

        /// <param name="filterUsingOwnerAuthorityId">NOTE: pass in <see cref="OwnerAuthorityId_Unset"/> to NOT filter</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldSendChange(AutoMagicalSync_ValueMonitoringSupport_ChangedValue change, ushort filterUsingOwnerAuthorityId)
        {
            // DIAGNOSTIC (Oct 2025): Calculate result first so we can log it
            // FIX (Dec 2025): Must check gonetId_raw, NOT GONetId!
            // GONetId is composite: (gonetId_raw << bits) | OwnerAuthorityId
            // If gonetId_raw=0 but OwnerAuthorityId=1023, GONetId=1023 (non-zero) but object not properly initialized!
            // This caused sync bundles to be sent for scene objects before GONetId assignment, causing client freeze.

            // INSTRUMENTATION (Dec 2025): Log when gonetId_raw=0 blocks a send
            var gnp = change.syncCompanion.gonetParticipant;
            if (gnp.gonetId_raw == GONetParticipant.GONetIdRaw_Unset)
            {
                GONetLog.Warning($"[SHOULD-SEND-BLOCKED] gonetId_raw=0! GONetId={gnp.GONetId}, InstantiationId={gnp.GONetIdAtInstantiation}, Auth={gnp.OwnerAuthorityId}, Name={gnp.gameObject?.name}");
            }

            bool willSend =
                gnp.gonetId_raw != GONetParticipant.GONetIdRaw_Unset &&
                (IsServer
                    ? (filterUsingOwnerAuthorityId == OwnerAuthorityId_Unset || // the unset value is now possible to send here to indicate no filtering!
                       (filterUsingOwnerAuthorityId == OwnerAuthorityId_Server && change.syncCompanion.gonetParticipant.OwnerAuthorityId == filterUsingOwnerAuthorityId) || // if it comes from the server itself
                        (filterUsingOwnerAuthorityId != OwnerAuthorityId_Server && _gonetServer.GetRemoteClientByAuthorityId(filterUsingOwnerAuthorityId).IsInitializedWithServer && // only send to a client if that client is considered initialized with the server
                        (change.syncCompanion.gonetParticipant.OwnerAuthorityId != filterUsingOwnerAuthorityId // In most circumstances, the server should send every change except for changes back to the owner itself
                                                                                                               // TODO try to make this work as an option: || IsThisChangeTheMomentOfInception(change)
                            || change.index == GONetParticipant.ASSumed_GONetId_INDEX))) // this is the one exception, if the server is assigning the instantiator/owner its GONetId for the first time, it DOES need to get sent back to itself
                    : change.syncCompanion.gonetParticipant.OwnerAuthorityId == filterUsingOwnerAuthorityId); // clients should only send out changes it owns

            /*
            // DIAGNOSTIC (Oct 2025): Log every check for position/rotation fields to understand client send bug
            // This will show us what values are being checked and whether the filter is working correctly
            if (!IsServer && (change.memberName == "position" || change.memberName == "rotation"))
            {
                GONetLog.Debug($"[SHOULD-SEND-CHECK] GONetId={change.syncCompanion.gonetParticipant.GONetId} " +
                              $"field={change.memberName} " +
                              $"OwnerAuthorityId={change.syncCompanion.gonetParticipant.OwnerAuthorityId} " +
                              $"filterUsingOwnerAuthorityId={filterUsingOwnerAuthorityId} " +
                              $"MyAuthorityId={MyAuthorityId} " +
                              $"willSend={willSend}");
            }
            */

            return willSend;
        }

        /* the initial idea behind this was good for a test, but the more I thought about it, the impl details did not actually make sense (perf and functionality)....keeping for now as reference
        private static bool IsThisChangeTheMomentOfInception(AutoMagicalSync_ValueMonitoringSupport_ChangedValue change)
        {
            bool shouldConsiderOlderItems = true;
            var enumerator = persistentEventsThisSession.GetEnumerator();

            Type syncEventBaseType = typeof(SyncEvent_ValueChangeProcessed);
            long tooOldTicks = TimeSpan.FromSeconds(0.5f).Ticks;

            while (shouldConsiderOlderItems && enumerator.MoveNext())
            {
                var lastConsideredEvent = enumerator.Current;
                if (TypeUtils.IsTypeAInstanceOfTypeB(lastConsideredEvent.GetType(), syncEventBaseType))
                {
                    SyncEvent_ValueChangeProcessed syncEvent = (SyncEvent_ValueChangeProcessed)lastConsideredEvent;
                    dynamic syncEventDynamic = syncEvent;
                    if (syncEvent.GONetId == change.syncCompanion.gonetParticipant.GONetId &&
                        syncEvent.CodeGenerationId == change.syncCompanion.CodeGenerationId &&
                        syncEvent.SyncMemberIndex == change.index &&
                        syncEventDynamic.valueNew == change.lastKnownValue)
                    {
                        return true;
                    }
                }

                shouldConsiderOlderItems = Time.ElapsedTicks - lastConsideredEvent.OccurredAtElapsedTicks < tooOldTicks;
            }

            return false;
        }
        */

        private static void DeserializeBody_AllValuesBundle(Utils.BitByBitByteArrayBuilder bitStream_headerAlreadyRead, int bytesUsedCount, GONetConnection sourceOfChangeConnection, long elapsedTicksAtSend)
        {
            //GONetLog.Debug($"Starting deserialization of all values bundle. bytesUsedCount: {bytesUsedCount}, stream position: {bitStream_headerAlreadyRead.Position_Bytes}");

            int deserializedCount = 0;
            int streamPositionBytes_preGonetId;
            // IMPORTANT: Use <= to ensure we don't read past the last complete byte
            // The WriteCurrentPartialByte() on serialization side means bytesUsedCount includes the final partial byte
            // We need to leave enough room for at least a GONetId (minimum 4 bytes) to avoid reading garbage
            const int MIN_GONETID_BYTES = 4; // GONetId is a uint, minimum 4 bytes
            while ((streamPositionBytes_preGonetId = bitStream_headerAlreadyRead.Position_Bytes) + MIN_GONETID_BYTES <= bytesUsedCount) // while more data to read/process
            {
                uint gonetId = GONetParticipant.GONetId_InitialAssignment_CustomSerializer.Instance.Deserialize(bitStream_headerAlreadyRead).System_UInt32;

                //GONetLog.Debug($"Deserialized GONetId: {gonetId} at stream position (pre: {streamPositionBytes_preGonetId}, post: {bitStream_headerAlreadyRead.Position_Bytes})");

                if (GONetParticipant.DoesGONetIdContainAllComponents(gonetId))
                {
                    // LATE-JOINER GRACEFUL HANDLING (Dec 2025):
                    // GONetId may not exist in map if the object was despawned before this late-joiner connected.
                    // In that case, the despawn created a tombstone. We keep tombstones for a short TTL to drop late bundles.
                    if (!gonetParticipantByGONetIdMap.TryGetValue(gonetId, out GONetParticipant gonetParticipant))
                    {
                        // Object doesn't exist - check if it was despawned (tombstoned)
                        if (TryConsumeDespawnTombstone(gonetId))
                        {
                            GONetLog.Debug($"[AllValues] Skipping GONetId {gonetId} - despawn tombstone present (dropping AllValues bundle for despawned object)");
                            return; // Cannot skip unknown data length; drop the remainder of this bundle.
                        }

                        RequestFullStateSyncRetryIfNeeded($"AllValues missing GONetId {gonetId}");
                        GONetLog.Warning($"[AllValues] Skipping GONetId {gonetId} - not found in map and no tombstone (unexpected state)");
                        // CRITICAL: We cannot skip the data in the stream because we don't know its length
                        // without the participant's sync companion. This bundle will fail.
                        // Throw to allow caller to re-defer (tail race) or log a hard failure if this was already deferred.
                        throw new KeyNotFoundException($"GONetId {gonetId} not found in gonetParticipantByGONetIdMap (no despawn tombstone)");
                    }

                    if (HasPendingReparentEventForTarget(gonetId))
                    {
                        throw new GONetParticipantNotReadyException(
                            $"AllValues deferred - pending reparent event for GONetId {gonetId}",
                            gonetId);
                    }

                    if (!activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(gonetParticipant.CodeGenerationId, out var companionDict) ||
                        !companionDict.TryGetValue(gonetParticipant, out var syncCompanion))
                    {
                        throw new GONetParticipantNotReadyException(
                            $"AllValues deferred - sync companion not registered for GONetId {gonetId} (CodeGenId {gonetParticipant.CodeGenerationId})",
                            gonetId);
                    }

                    if (!gonetParticipant.didAwakeComplete || syncCompanion == null)
                    {
                        throw new GONetParticipantNotReadyException(
                            $"AllValues deferred - participant not ready for GONetId {gonetId} (didAwakeComplete={gonetParticipant.didAwakeComplete}, syncCompanionNull={syncCompanion == null})",
                            gonetId);
                    }

                    GONetLog.Debug($"Successfully deserialized GNP '{gonetParticipant.gameObject.name}' with GONetId: {gonetId}");
                    syncCompanion.DeserializeInitAll(bitStream_headerAlreadyRead, elapsedTicksAtSend);

                    if (gonetParticipant.v2_isRegisteredInSoA && SoAData.IsInitialized)
                    {
                        bool hasPosition = false;
                        bool hasRotation = false;
                        var changesSupport = syncCompanion.valuesChangesSupport;
                        for (int i = 0; i < changesSupport.Length; i++)
                        {
                            var changeSupport = changesSupport[i];
                            if (changeSupport == null)
                            {
                                continue;
                            }

                            string memberName = changeSupport.memberName;
                            if (!IsTransformSyncMember(memberName))
                            {
                                continue;
                            }

                            if (memberName == "position")
                            {
                                hasPosition = true;
                            }
                            else if (memberName == "rotation")
                            {
                                hasRotation = true;
                            }

                            if (hasPosition && hasRotation)
                            {
                                break;
                            }
                        }

                        if (hasPosition || hasRotation)
                        {
                            SoA_ResetTransformHistoryFromDeserializeInit(gonetParticipant, hasPosition, hasRotation);
                        }

                        long receiverTimestamp = Time.ElapsedTicks;
                        if (hasPosition)
                        {
                            SoA_WritePositionUpdate(gonetParticipant.GONetId, gonetParticipant.transform.position, receiverTimestamp, false);
                        }
                        if (hasRotation)
                        {
                            SoA_WriteRotationUpdate(gonetParticipant.GONetId, gonetParticipant.transform.rotation, receiverTimestamp, false);
                        }
                    }

                    // LATE-JOINER PHYSICS SNAPPING: If this is a physics object, trigger physics snapping
                    // to eliminate quantization error for objects at rest (position ~0.95mm → sub-mm, rotation ~0.3° → sub-0.01°)
                    bool isPhysicsObject = gonetParticipant.IsRigidBodyOwnerOnlyControlled &&
                                           gonetParticipant.myRigidBody != null &&
                                           !gonetParticipant.IsMine;

                    if (isPhysicsObject)
                    {
                        // Check if this object has position or rotation sync by scanning all indices for matching function pointers
                        // This relies on the implementation detail that position/rotation use IsPositionNotSyncd/IsRotationNotSyncd delegates
                        bool hasPositionOrRotation = false;
                        for (byte i = 0; i < syncCompanion.valuesChangesSupport.Length; i++)
                        {
                            AutoMagicalSync_ValueMonitoringSupport_ChangedValue changedValue = syncCompanion.valuesChangesSupport[i];
                            if (changedValue.syncAttribute_ShouldSkipSync == IsPositionNotSyncd ||
                                changedValue.syncAttribute_ShouldSkipSync == IsRotationNotSyncd)
                            {
                                hasPositionOrRotation = true;
                                break;
                            }
                        }

                        if (hasPositionOrRotation)
                        {
                            // Get current transform values (just applied via DeserializeInitAll)
                            Vector3 position = gonetParticipant.transform.position;
                            Quaternion rotation = gonetParticipant.transform.rotation;

                            // Trigger physics snapping to improve final resting accuracy
                            gonetParticipant.TriggerPhysicsSnapToRest(position, rotation);
                        }
                    }

                    // Deduplication check: Only publish if not already published
                    if (TryMarkDeserializeInitPublished(gonetId))
                    {
                        //GONetLog.Info($"[GONet] Publishing DeserializeInitAllCompleted for '{gonetParticipant.name}' (GONetId: {gonetId}, IsMine: {gonetParticipant.IsMine}) from deserialization path");
                        PublishEventAsSoonAsSufficientInfoAvailable(
                            new GONetParticipantDeserializeInitAllCompletedEvent(gonetParticipant),
                            gonetParticipant,
                            isRelatedLocalContentRequired: true);
                    }
                    else
                    {
                        //GONetLog.Info($"[GONet] Skipping duplicate DeserializeInitAllCompleted for '{gonetParticipant.name}' (GONetId: {gonetId}) - already published from another path");
                    }

                    deserializedCount++;

                    // END MARKER CHECK (December 2025): After successfully deserializing an object,
                    // check for the end marker (isSameAsPrevious=true + isDiffNegative=true).
                    // Each AllCurrentValues bundle contains exactly ONE GONetParticipant followed by
                    // this 2-bit end marker + padding. Without this check, the while loop continues
                    // and tries to read the end marker bits as another GONetId, causing "gonetId value (0)" errors.
                    bool isSameAsPrevious;
                    bitStream_headerAlreadyRead.ReadBit(out isSameAsPrevious);
                    bool isDiffNegative;
                    bitStream_headerAlreadyRead.ReadBit(out isDiffNegative);

                    if (isSameAsPrevious && isDiffNegative)
                    {
                        // End marker detected - this bundle is complete
                        //GONetLog.Debug($"[AllValues] End marker detected after deserializing {deserializedCount} object(s)");
                        break;
                    }
                    else
                    {
                        // Not an end marker - unexpected for single-object AllCurrentValues bundles
                        // Log warning but continue to allow multi-object bundles (future-proofing)
                        GONetLog.Warning($"[AllValues] Expected end marker after object, but got isSameAsPrevious={isSameAsPrevious}, isDiffNegative={isDiffNegative}. Continuing to next object.");
                    }
                }
                else
                {
                    // DIAGNOSTIC: Decompose the incomplete GONetId to show what we received
                    uint gonetId_raw = (gonetId >> GONetParticipant.GONET_ID_BIT_COUNT_UNUSED);
                    ushort ownerAuthorityId = (ushort)((gonetId << GONetParticipant.GONET_ID_BIT_COUNT_USED) >> GONetParticipant.GONET_ID_BIT_COUNT_USED);

                    GONetLog.Error($"Deserialized a gonetId value ({gonetId}) that is not complete, which will cause reading the rest of the values to fail in mysterious ways...so, will STOP deserializing now!  " +
                                  $"GONetId decomposed: raw={gonetId_raw}, authority={ownerAuthorityId} " +
                                  $"(raw is unset: {gonetId_raw == GONetParticipant.GONetId_Unset}, authority is unset: {ownerAuthorityId == OwnerAuthorityId_Unset}) " +
                                  $"stream.Position_Bytes: (pre:{streamPositionBytes_preGonetId}, post:{bitStream_headerAlreadyRead.Position_Bytes}) bytesUsedCount: {bytesUsedCount}");
                    return;
                }
            }

            //GONetLog.Debug($"Deserialization complete. Total GONetIds deserialized: {deserializedCount}");
        }

        /// <summary>
        /// Awaiting to not be unity null and to have an entry in the corresponding entry/map in <see cref="activeAutoSyncCompanionsByCodeGenerationIdMap"/> for its codeGenerationId.
        /// </summary>
        static readonly List<GONetParticipant> gnpsAwaitingCompanion = new List<GONetParticipant>(1000);

        private static void DeserializeBody_BundleOfChoice(Utils.BitByBitByteArrayBuilder bitStream_headerAlreadyRead, GONetConnection sourceOfChangeConnection, GONetChannelId channelId, long elapsedTicksAtSend, Type chosenBundleType, bool isVelocityBundle = false)
        {
            //if (chosenBundleType == typeof(AutoMagicalSync_ValuesNowAtRest_Message)) GONetLog.Debug($"remote source sent us at rest bundle.");

            uint gonetId_previous = 0;
            bool reportedInboundSync = false;

            while (true)
            {
                bool isSameAsPrevious;
                bitStream_headerAlreadyRead.ReadBit(out isSameAsPrevious);

                bool isDiffNegative;
                bitStream_headerAlreadyRead.ReadBit(out isDiffNegative);

                uint gonetIdAtInstantiation;

                if (isSameAsPrevious)
                {
                    if (isDiffNegative) // this essentially impossible combination is the signal of end of content!
                    {
                        break;
                    }

                    gonetIdAtInstantiation = gonetId_previous;
                }
                else
                {
                    uint gonetIdByteCount;
                    bitStream_headerAlreadyRead.ReadUInt(out gonetIdByteCount, 2);

                    uint gonetId_diff_unsigned;
                    bitStream_headerAlreadyRead.ReadUInt(out gonetId_diff_unsigned, (gonetIdByteCount + 1) << 3);

                    long gonetId_diff = isDiffNegative ? -gonetId_diff_unsigned : gonetId_diff_unsigned;
                    gonetIdAtInstantiation = (uint)(gonetId_previous + gonetId_diff);
                    gonetId_previous = gonetIdAtInstantiation;

                    //GONetLog.Append(gonetIdAtInstantiation + ", ");
                }

                GONetParticipant gonetParticipant = null;
                uint gonetId = GetCurrentGONetIdByIdAtInstantiation(gonetIdAtInstantiation);

                // IMPROVED: Try multiple lookup strategies with better diagnostics
                // CRITICAL: Check instantiation map FIRST if current GONetId is 0 (unset/reset during scene changes)
                if (gonetId == GONetParticipant.GONetId_Unset && gonetParticipantByGONetIdAtInstantiationMap.ContainsKey(gonetIdAtInstantiation))
                {
                    // Participant exists but GONetId is unset - happens during scene transitions
                    gonetParticipant = gonetParticipantByGONetIdAtInstantiationMap[gonetIdAtInstantiation];
                    GONetLog.Debug($"GONetId lookup: Participant found with unset GONetId (instantiation: {gonetIdAtInstantiation}). Likely during scene transition.");
                }
                else if (gonetParticipantByGONetIdMap.ContainsKey(gonetId))
                {
                    gonetParticipant = gonetParticipantByGONetIdMap[gonetId];
                }
                else if (gonetParticipantByGONetIdMap.ContainsKey(gonetIdAtInstantiation))
                {
                    gonetParticipant = gonetParticipantByGONetIdMap[gonetIdAtInstantiation];
                    GONetLog.Debug($"GONetId lookup: Found by instantiation ID in main map (current: {gonetId}, instantiation: {gonetIdAtInstantiation})");
                }
                else if (gonetParticipantByGONetIdAtInstantiationMap.ContainsKey(gonetIdAtInstantiation))
                {
                    gonetParticipant = gonetParticipantByGONetIdAtInstantiationMap[gonetIdAtInstantiation];
                    // CRITICAL: Do NOT access gonetParticipant.name here - participant may be destroyed
                    GONetLog.Debug($"GONetId lookup: Found in instantiation map (current: {gonetId}, instantiation: {gonetIdAtInstantiation}), IsInitialized: {(IsClient ? GONetClient.IsInitializedWithServer : true)}");
                }

                if ((object)gonetParticipant == null)
                {
                    QosType channelQuality = GONetChannel.ById(channelId).QualityOfService;
                    if (channelQuality == QosType.Reliable)
                    {
                        // FAILOVER / DESPAWN STALE-BUNDLE GUARD (Dec 2025):
                        // Reliable bundles can arrive after a reliable despawn (queue lag / cross-channel ordering).
                        // If we've already tombstoned this id, drop the bundle quietly instead of erroring and poisoning processing.
                        if (TryConsumeDespawnTombstone(gonetIdAtInstantiation) ||
                            (gonetId != GONetParticipant.GONetId_Unset && TryConsumeDespawnTombstone(gonetId)))
                        {
                            return;
                        }

                        // Enhanced diagnostics for debugging lookup failures
                        // INSTRUMENTATION (Dec 2025): More detailed lookup failure info
                        string mapSample = "";
                        int sampleCount = 0;
                        foreach (var kvp in gonetParticipantByGONetIdMap)
                        {
                            if (sampleCount++ < 5) mapSample += $"{kvp.Key},";
                        }
                        GONetLog.Error($"RELIABLE sync bundle - GONetParticipant NOT FOUND. Current GONetId: {gonetId}, InstantiationId: {gonetIdAtInstantiation}. " +
                                      $"Maps contain - byGONetId: {gonetParticipantByGONetIdMap.Count} entries (sample: {mapSample}), byInstantiationId: {gonetParticipantByGONetIdAtInstantiationMap.Count} entries. " +
                                      $"IsInitialized: {(IsClient ? GONetClient.IsInitializedWithServer : true)}. " +
                                      $"This indicates spawn event not received or participant destroyed.");

                        // Treat as "not ready" so deferral can recover once spawn/registration completes.
                        throw new GONetParticipantNotReadyException(
                            $"Reliable sync bundle received for missing participant (GONetId: {gonetId}, Instantiation: {gonetIdAtInstantiation})",
                            gonetIdAtInstantiation);
                    }
                    else
                    {
                        // DIAGNOSTIC LOGGING: Track which GONetIds cause bundle aborts/deferrals
//                        string deferralStatus = GONetGlobal.Instance.deferSyncBundlesWaitingForGONetReady
//                            ? $"Will DEFER (timeout: {GONetGlobal.Instance.maxSecondsToWaitForMissingParticipant}s)"
//                            : "Will DROP (deferral disabled)";

                        // Changed from Warning to Debug (November 2025) - expected behavior for unreliable traffic
                        // when spawn messages are delayed. Warning level caused excessive log spam.
                        //GONetLog.Debug($"[BUNDLE-MISSING-PARTICIPANT] ⚠️ Sync bundle for unknown participant - {deferralStatus} ⚠️\n" +
//                                        $"  GONetId: {gonetId}, " +
//                                        $"InstantiationId: {gonetIdAtInstantiation}, " +
//                                        $"ChannelId: {channelId}, " +
//                                        $"QoS: {GONetChannel.ById(channelId).QualityOfService}, " +
//                                        $"IsClient: {IsClient}, " +
//                                        $"MyAuthorityId: {MyAuthorityId}\n" +
//                                        $"  InGONetIdMap: {gonetParticipantByGONetIdMap.ContainsKey(gonetId)}, " +
//                                        $"InInstantiationMap: {gonetParticipantByGONetIdAtInstantiationMap.ContainsKey(gonetIdAtInstantiation)}\n" +
//                                        $"  TotalInGONetIdMap: {gonetParticipantByGONetIdMap.Count}, " +
//                                        $"TotalInInstantiationMap: {gonetParticipantByGONetIdAtInstantiationMap.Count}\n" +
//                                        $"  LIKELY CAUSE: Spawn message delayed/incomplete - sync arrived before participant ready");

                        // CRITICAL: Throw exception to trigger deferral system for unreliable bundles too
                        // Original approach: `return` aborted ENTIRE bundle, losing sync data for ALL subsequent participants
                        // Cannot use `continue`: Bitstream has unread value data (index + value bytes), skipping causes desync
                        //
                        // PROBLEM: Sync bundles pack hundreds of participants. If ONE participant is missing:
                        //   - Using `return`: Drops entire bundle → hundreds of objects never get position/rotation updates
                        //   - Using `continue`: Desyncs bitstream → corrupts ALL subsequent reads in bundle
                        //
                        // SOLUTION: Defer ENTIRE bundle (even for unreliable) and retry next frame when participant likely ready.
                        // User symptoms with `return`: White beacons (color never syncs), projectiles stuck at origin (position never syncs).
                        //
                        // Exception caught in ProcessIncomingBytes - will defer if GONetGlobal.deferSyncBundlesWaitingForGONetReady enabled.
                        throw new GONetParticipantNotReadyException(
                            $"Unreliable sync bundle received for missing participant (GONetId: {gonetId}, Instantiation: {gonetIdAtInstantiation})",
                            gonetIdAtInstantiation);
                    }
                }


                // CRITICAL FIX: Check if Unity object was destroyed before accessing properties
                // Unity's overloaded == operator detects destroyed objects, while (object)cast checks C# reference
                if (gonetParticipant == null)
                {
                    bool isCSharpReferenceNull = (object)gonetParticipant == null;

                    // IMPORTANT: Do NOT access any Unity properties of gonetParticipant here!
                    // Even though C# reference may not be null, Unity object is destroyed and accessing Unity properties throws MissingReferenceException
                    // Pure C# properties (like GONetId) may work, but should be avoided for consistency - use cached values instead
                    GONetLog.Error($"GONetParticipant Unity object destroyed but still in maps. C# reference null: {isCSharpReferenceNull}, GONetIdAtInstantiation: {gonetIdAtInstantiation}. Skipping this sync bundle item.");

                    // Skip processing this destroyed participant - do NOT add to awaiting or continue processing
                    continue;
                }

                //Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> companionMap = activeAutoSyncCompanionsByCodeGenerationIdMap[gonetParticipant.codeGenerationId];

                // CRITICAL FIX: Defensive check for sync companion availability during rapid spawning
                // During high spawn rates, sync bundles can arrive BEFORE GONetParticipant.Awake() completes
                // (Awake runs as coroutine and yields). This causes NullReferenceException when trying to
                // access sync companion that hasn't been registered yet.
                Dictionary<uint, GONetParticipant_AutoMagicalSyncCompanion_Generated> companionMap;
                if (!activeAutoSyncCompanionsByCodeGenerationIdMap_uintKeyForPerformance.TryGetValue(gonetParticipant.CodeGenerationId, out companionMap))
                {
                    // Sync companion map not created yet for this CodeGenerationId
                    // This happens when spawn event was received but participant hasn't finished initializing
                    QosType channelQuality = GONetChannel.ById(channelId).QualityOfService;
                    // DIAGNOSTIC LOGGING: Track companion map issues
                    GONetLog.Error($"[BUNDLE-ABORT-COMPANION-MAP] ⚠️ BUNDLE ABORTED - No companion map ⚠️ " +
                                  $"GONetId: {gonetParticipant.GONetId}, " +
                                  $"InstantiationId: {gonetIdAtInstantiation}, " +
                                  $"CodeGenerationId: {gonetParticipant.CodeGenerationId}, " +
                                  $"Channel: {(channelQuality == QosType.Reliable ? "Reliable" : "Unreliable")}, " +
                                  $"IsClient: {IsClient}, " +
                                  $"MyAuthorityId: {MyAuthorityId}");

                    // CRITICAL: Throw exception to trigger deferral system instead of aborting entire bundle
                    // Using `return` here drops ENTIRE bundle, losing sync data for all subsequent participants
                    throw new GONetParticipantNotReadyException(
                        $"Sync companion map not created yet for CodeGenerationId {gonetParticipant.CodeGenerationId}",
                        gonetIdAtInstantiation);
                }

                GONetParticipant_AutoMagicalSyncCompanion_Generated syncCompanion;
                if (!companionMap.TryGetValue(gonetParticipant._GONetIdAtInstantiation, out syncCompanion))
                {
                    // RACE CONDITION FIX: Uint-keyed map not populated yet, try fallback to GNP-keyed map
                    // During rapid spawning, sync companion is registered in GNP-keyed map (OnEnable_StartMonitoringForAutoMagicalNetworking)
                    // but uint-keyed map population is deferred until GONetIdAtInstantiation is assigned (OnGONetIdAtInstantiationChanged event).
                    // If sync bundles arrive in this narrow window, uint lookup fails even though companion exists.
                    // SOLUTION: Fallback to GNP-keyed map (always populated at companion creation time).
                    Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> gnpKeyedMap;
                    if (activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(gonetParticipant.CodeGenerationId, out gnpKeyedMap) &&
                        gnpKeyedMap.TryGetValue(gonetParticipant, out syncCompanion))
                    {
                        // FOUND via fallback! Companion exists, just not in uint-keyed map yet
                        // This is expected during rapid spawning when GONetIdAtInstantiation assignment is delayed
                        GONetLog.Debug($"[BUNDLE-FALLBACK] ✓ Used GNP-keyed fallback for participant " +
                                      $"GONetId: {gonetParticipant.GONetId}, " +
                                      $"InstantiationId: {gonetParticipant._GONetIdAtInstantiation} " +
                                      $"(uint-keyed map not populated yet - race condition during rapid spawning)");
                        // Continue processing with the fallback companion (syncCompanion now set)
                    }
                    else
                    {
                        // Companion genuinely not registered yet - this is the real "not ready" case
                        QosType channelQuality = GONetChannel.ById(channelId).QualityOfService;

                        // DIAGNOSTIC LOGGING: Track companion not in map
                        GONetLog.Error($"[BUNDLE-ABORT-COMPANION-MISSING] ⚠️ BUNDLE ABORTED - Companion not in any map ⚠️ " +
                                      $"GONetId: {gonetParticipant.GONetId}, " +
                                      $"InstantiationId: {gonetParticipant._GONetIdAtInstantiation}, " +
                                      $"Channel: {(channelQuality == QosType.Reliable ? "Reliable" : "Unreliable")}, " +
                                      $"IsClient: {IsClient}, " +
                                      $"MyAuthorityId: {MyAuthorityId}");

                        // CRITICAL: Throw exception to trigger deferral system instead of aborting entire bundle
                        throw new GONetParticipantNotReadyException(
                            $"Sync companion not registered for GONetId {gonetParticipant.GONetId}",
                            gonetIdAtInstantiation);
                    }
                }

                // DEFENSIVE CHECK (NEW - SYNC BUNDLE GONETREADY RACE CONDITION FIX):
                // Participant must have completed Awake() and have syncCompanion ready before deserialization.
                // Even though we fetched syncCompanion from the map above, during rapid spawning scenarios:
                // - The companion might have been registered to the map BUT
                // - The participant's Awake() coroutine is still running (didAwakeComplete=false)
                // - Accessing syncCompanion methods can cause NullReferenceException or unexpected behavior
                //
                // This is a RACE CONDITION between:
                // 1. Network thread processing sync bundles (this code)
                // 2. Main thread running GONetParticipant.AwakeCoroutine()
                //
                // Solution: Throw descriptive exception that calling code will catch and defer/drop based on config.
                if (!gonetParticipant.didAwakeComplete || syncCompanion == null)
                {
                    // CRITICAL: Do NOT access gonetParticipant.name here - participant may be destroyed
                    throw new GONetParticipantNotReadyException(
                        $"GONetParticipant {gonetIdAtInstantiation} exists but not ready for deserialization. " +
                        $"didAwakeComplete: {gonetParticipant.didAwakeComplete}, " +
                        $"syncCompanion null: {syncCompanion == null}",
                        gonetIdAtInstantiation);
                }

                try
                {
                    // CRITICAL: Re-check if Unity object still exists before accessing properties
                    // Object could have been destroyed during bundle processing (mid-loop through multiple participants)
                    if (gonetParticipant == null)
                    {
                        GONetLog.Warning($"[SYNC] GONetParticipant was destroyed during bundle processing. Skipping this sync data item.");
                        continue;
                    }

                    bool isBundleTypeValueChanges = chosenBundleType == typeof(AutoMagicalSync_ValueChanges_Message);

                    byte index = (byte)bitStream_headerAlreadyRead.ReadByte();

                    if (gonetParticipant.IsMine) // with recent changes, bundles all all the same for all clients, which means you will receive your own stuff too...essentially want to skip, but have to move the bit reader forward!
                    {
                        // VELOCITY-AUGMENTED SYNC: Always pass isVelocityBundle to keep bitstream in sync
                        // (even when skipping, we must read the same number of bits)
                        syncCompanion.DeserializeInitSingle_ReadOnlyNotApply(bitStream_headerAlreadyRead, index, isVelocityBundle);

                        // HOST MODE DIAGNOSTIC: Log when IsMine causes skip
                        // COMMENTED OUT (Dec 2025): This diagnostic caused 8,500+ warnings during handoff, killing frame rate
                        //if (IsClient && gonetParticipant.OwnerAuthorityId == OwnerAuthorityId_Server)
                        //{
                        //    GONetLog.Warning($"[SYNC-SKIP-ISMINE] Skipping sync for server-owned object! GONetId={gonetParticipant.GONetId}, Name='{gonetParticipant.name}', IsMine={gonetParticipant.IsMine}, OwnerAuthorityId={gonetParticipant.OwnerAuthorityId}, MyAuthorityId={MyAuthorityId}");
                        //}
                    }
                    else
                    {
                        if (!reportedInboundSync)
                        {
                            GONetMain.NotifyInboundSyncProcessed();
                            reportedInboundSync = true;
                        }

                        if (isBundleTypeValueChanges)
                        {
                            // IMPORTANT: Log value change application for Client:2
                            //if (IsClient)
                            //{
                                // DEFENSIVE: Check again before accessing properties (object could be destroyed mid-processing)
                                //string logName = (gonetParticipant != null && gonetParticipant.gameObject != null) ? gonetParticipant.gameObject.name : "<destroyed>";
                                //uint logId = (gonetParticipant != null) ? gonetParticipant.GONetId : GONetParticipant.GONetId_Unset;
                                //GONetLog.Info($"[SYNC-APPLY] Client applying value change - GONetId: {logId}, GameObject: '{logName}', index: {index}");
                            //}

                            // VELOCITY-AUGMENTED SYNC: Handle VELOCITY vs VALUE bundles
                            // [SoA-DIAG] Log every sync bundle received for transform members - DISABLED
                            // if (IsTransformSyncMember(syncCompanion.valuesChangesSupport[index].memberName))
                            // {
                            //     GONetLog.Debug(string.Format("[SoA-DIAG] Transform sync for {0} (GONetId {1}): isVelocityBundle={2}, memberName={3}, v2_isRegisteredInSoA={4}",
                            //         syncCompanion.gonetParticipant.gameObject.name, syncCompanion.gonetParticipant.GONetId,
                            //         isVelocityBundle, syncCompanion.valuesChangesSupport[index].memberName, syncCompanion.gonetParticipant.v2_isRegisteredInSoA));
                            // }

                            if (isVelocityBundle)
                            {
                                // VELOCITY BUNDLE: Deserialize velocity, synthesize position
                                GONetSyncableValue velocityValue = syncCompanion.DeserializeInitSingle_ReadOnlyNotApply(bitStream_headerAlreadyRead, index, true);

                                var changesSupport = syncCompanion.valuesChangesSupport[index];

                                // CRITICAL: Store velocity and timestamp for future VALUE bundle synthesis
                                changesSupport.lastReceivedVelocity = velocityValue;
                                changesSupport.lastVelocityTimestamp = Time.ElapsedTicks;

                                int recentChangesCount = changesSupport.mostRecentChanges_usedSize;

                                // [DIAG] Track VELOCITY bundle processing - uncomment to debug
                                // GONetLog.Debug($"[VEL-DIAG] VELOCITY bundle: GONetId={syncCompanion.gonetParticipant.GONetId} member={changesSupport.memberName} recentCount={recentChangesCount} v2Reg={syncCompanion.gonetParticipant.v2_isRegisteredInSoA}");

                                if (recentChangesCount >= 1)
                                {
                                    // Use MOST RECENT snapshot (synthesized or not) as baseline
                                    // We MUST use the most recent to avoid compounding errors when multiple VELOCITY bundles
                                    // arrive before a VALUE bundle (otherwise we'd keep using a stale VALUE from seconds ago)
                                    NumericValueChangeSnapshot lastSnapshot = changesSupport.mostRecentChanges[0];

                                    // Calculate ACTUAL elapsed time since last snapshot (not just sync interval!)
                                    // This is critical: velocity = units/sec, so we need ACTUAL seconds elapsed
                                    long ticksSinceLastSnapshot = elapsedTicksAtSend - lastSnapshot.elapsedTicksAtChange;
                                    float deltaTime = (float)ticksSinceLastSnapshot * (float)GONet.Utils.HighResolutionTimeUtils.TICKS_TO_SECONDS;

                                    // Synthesize new position from velocity
                                    GONetSyncableValue synthesizedValue = SynthesizeValueFromVelocity(
                                        lastSnapshot.numericValue,
                                        velocityValue,
                                        deltaTime);

                                    // Store synthesized position as snapshot WITH velocity metadata for velocity-aware blending
                                    changesSupport.AddToMostRecentChangeQueue_IfAppropriate_WithVelocity(elapsedTicksAtSend, synthesizedValue, velocityValue);

                                    // GONet v2 SoA: Write synthesized value to SoA ring buffer for high-performance blending
                                    // [DIAG] Log VELOCITY bundle SoA write decisions - uncomment to debug
                                    // GONetLog.Debug($"[SoA-VEL] GONetId={syncCompanion.gonetParticipant.GONetId} member={changesSupport.memberName} isTransform={IsTransformSyncMember(changesSupport.memberName)} v2Reg={syncCompanion.gonetParticipant.v2_isRegisteredInSoA} type={synthesizedValue.GONetSyncType}");
                                    if (syncCompanion.gonetParticipant.v2_isRegisteredInSoA && IsTransformSyncMember(changesSupport.memberName))
                                    {
                                        // TIME BASE FIX: Use receiver's current time, NOT sender's elapsedTicksAtSend
                                        // The blending job targets (Time.ElapsedTicks - bufferLead), so samples MUST be in receiver's time base.
                                        // Using sender's time base causes massive extrapolation (dtTarget = +50 seconds instead of -0.15).
                                        long receiverTimestamp = Time.ElapsedTicks;
                                        if (synthesizedValue.GONetSyncType == GONetSyncableValueTypes.UnityEngine_Vector3)
                                        {
                                            //GONetLog.Debug(string.Format("[SoA-WRITE] VELOCITY path: Writing position for GONetId {0}", syncCompanion.gonetParticipant.GONetId));
                                            SoA_WritePositionUpdate(syncCompanion.gonetParticipant.GONetId, synthesizedValue.UnityEngine_Vector3, receiverTimestamp, false);
                                        }
                                        else if (synthesizedValue.GONetSyncType == GONetSyncableValueTypes.UnityEngine_Quaternion)
                                        {
                                            //GONetLog.Debug(string.Format("[SoA-WRITE] VELOCITY path: Writing rotation for GONetId {0}", syncCompanion.gonetParticipant.GONetId));
                                            SoA_WriteRotationUpdate(syncCompanion.gonetParticipant.GONetId, synthesizedValue.UnityEngine_Quaternion, receiverTimestamp, false);
                                        }
                                    }
                                }
                                else
                                {
                                    // No recent changes in queue - use current transform value as baseline
                                    // CRITICAL: NEVER use velocity as position (would cause jumps to origin/weird positions)!
                                    // This can happen when:
                                    // - AT-REST message cleared the queue
                                    // - First VELOCITY bundle received after spawn
                                    // - Queue was cleared for other reasons
                                    // Solution: Use current transform value (maintains continuity)
                                    GONetSyncableValue currentValue = syncCompanion.GetAutoMagicalSyncValue(index);

                                    // isAnchor=false: Single-write preserves temporal history for smooth blending
                                    // (double-write from isAnchor=true caused dtSamples≈0 → snapping instead of interpolation)
                                    syncCompanion.InitSingle(currentValue, index, elapsedTicksAtSend, false);
                                }
                            }
                            else
                            {
                                // VALUE BUNDLE: Check if we should synthesize from velocity or use received VALUE
                                var changesSupport = syncCompanion.valuesChangesSupport[index];

                                // Check if velocity is still valid (time-based expiration)
                                long currentTicks = Time.ElapsedTicks;
                                long velocityAgeTicks = currentTicks - changesSupport.lastVelocityTimestamp;
                                long velocityValidDurationTicks = AutoMagicalSync_ValueMonitoringSupport_ChangedValue.VELOCITY_VALID_DURATION_MS * TimeSpan.TicksPerMillisecond;

                                // CRITICAL FIX: Also check if lastReceivedVelocity has a valid type (not default System_Boolean)
                                // Late-joiners receive VALUE bundles before any VELOCITY bundles, so lastReceivedVelocity
                                // is uninitialized (defaults to System_Boolean). Using it causes VelocitySync errors.
                                bool velocityHasValidType = changesSupport.lastReceivedVelocity.GONetSyncType != GONetSyncableValueTypes.System_Boolean;
                                bool hasRecentVelocity = (velocityAgeTicks < velocityValidDurationTicks) && changesSupport.isVelocityEligible && velocityHasValidType;

                                // [SoA-DIAG] Log VALUE bundle velocity decision for transform members - DISABLED
                                // if (IsTransformSyncMember(changesSupport.memberName))
                                // {
                                //     GONetLog.Debug(string.Format("[SoA-DIAG] VALUE bundle for {0}: hasRecentVelocity={1} (ageOK={2}, eligible={3}, typeOK={4})",
                                //         syncCompanion.gonetParticipant.gameObject.name, hasRecentVelocity,
                                //         velocityAgeTicks < velocityValidDurationTicks, changesSupport.isVelocityEligible, velocityHasValidType));
                                // }

                                if (hasRecentVelocity)
                                {
                                    // SYNTHESIZE from last received velocity to avoid ping-ponging with quantized VALUE
                                    // This is the key to eliminating jitter: we ignore the quantized VALUE and keep
                                    // synthesizing smooth positions from velocity until velocity expires

                                    // Read VALUE from bitstream but DON'T apply it (keep bitstream in sync)
                                    GONetSyncableValue receivedValue = syncCompanion.DeserializeInitSingle_ReadOnlyNotApply(bitStream_headerAlreadyRead, index, false);

                                    int recentChangesCount = changesSupport.mostRecentChanges_usedSize;
                                    if (recentChangesCount >= 1)
                                    {
                                        /* we the critical fix below was made, these calculations are no longer needed:
                                        // Use MOST RECENT snapshot (synthesized or not) as baseline
                                        // We MUST use the most recent to avoid compounding errors when multiple VELOCITY bundles
                                        // arrive before a VALUE bundle (otherwise we'd keep using a stale VALUE from seconds ago)
                                        NumericValueChangeSnapshot lastSnapshot = changesSupport.mostRecentChanges[0];

                                        // Calculate ACTUAL elapsed time since last snapshot (not just sync interval!)
                                        // This is critical: velocity = units/sec, so we need ACTUAL seconds elapsed
                                        long ticksSinceLastSnapshot = elapsedTicksAtSend - lastSnapshot.elapsedTicksAtChange;
                                        float deltaTime = (float)ticksSinceLastSnapshot * (float)GONet.Utils.HighResolutionTimeUtils.TICKS_TO_SECONDS;

                                        // Synthesize position from last received velocity (ignore received VALUE)
                                        GONetSyncableValue synthesizedValue = SynthesizeValueFromVelocity(
                                            lastSnapshot.numericValue,
                                            changesSupport.lastReceivedVelocity,
                                            deltaTime);
                                        */

                                        // CRITICAL FIX: Store RECEIVED VALUE with velocity metadata for smooth extrapolation
                                        // InitSingle stores WITHOUT velocity → blending falls back to standard interpolation
                                        // We must store WITH velocity so TryGetBlendedValue can use velocity-aware extrapolation
                                        // Between VALUE updates, extrapolation will be smooth using velocity data
                                        changesSupport.AddToMostRecentChangeQueue_IfAppropriate_WithVelocity(
                                            elapsedTicksAtSend,
                                            receivedValue,
                                            changesSupport.lastReceivedVelocity);

                                        // GONet v2 SoA: Write anchor value to SoA ring buffer for high-performance blending
                                        //GONetLog.Debug(string.Format("[SoA-VAL-SYNC] VALUE bundle with velocity for {0} (GONetId {1}): v2_isRegisteredInSoA={2}, memberName={3}, isTransform={4}, type={5}",
                                            //syncCompanion.gonetParticipant.gameObject.name, syncCompanion.gonetParticipant.GONetId,
                                            //syncCompanion.gonetParticipant.v2_isRegisteredInSoA, changesSupport.memberName, IsTransformSyncMember(changesSupport.memberName), receivedValue.GONetSyncType));
                                        if (syncCompanion.gonetParticipant.v2_isRegisteredInSoA && IsTransformSyncMember(changesSupport.memberName))
                                        {
                                            // VALUE anchor during velocity-augmented sync: Write single sample to preserve temporal history
                                            // The blending job will smoothly interpolate toward this VALUE anchor using the ring buffer history.
                                            // Anchor double-write was causing dtSamples=0 which made the blending job snap directly to VALUE.
                                            //
                                            // IMPORTANT: isAnchor=false ensures single-write behavior, preserving temporal history for smooth blending.
                                            //
                                            // TIME BASE FIX: Use receiver's current time (see VELOCITY path comment for details).
                                            long receiverTimestamp = Time.ElapsedTicks;
                                            if (receivedValue.GONetSyncType == GONetSyncableValueTypes.UnityEngine_Vector3)
                                            {
                                                SoA_WritePositionUpdate(syncCompanion.gonetParticipant.GONetId, receivedValue.UnityEngine_Vector3, receiverTimestamp, false);
                                            }
                                            else if (receivedValue.GONetSyncType == GONetSyncableValueTypes.UnityEngine_Quaternion)
                                            {
                                                SoA_WriteRotationUpdate(syncCompanion.gonetParticipant.GONetId, receivedValue.UnityEngine_Quaternion, receiverTimestamp, false);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // No previous snapshot - use received VALUE as fallback
                                        // isAnchor=false: Single-write preserves temporal history for smooth blending
                                        syncCompanion.InitSingle(receivedValue, index, elapsedTicksAtSend, false);

                                        // CRITICAL FIX (Dec 2025): Also write to SoA when no recent changes exist.
                                        // After demotion, blend buffers are reset (recentChangesCount=0), causing VALUE bundles
                                        // to fall into this fallback path. Without SoA writes, objects remain stuck.
                                        if (syncCompanion.gonetParticipant.v2_isRegisteredInSoA && IsTransformSyncMember(changesSupport.memberName))
                                        {
                                            long receiverTimestamp = Time.ElapsedTicks;
                                            if (receivedValue.GONetSyncType == GONetSyncableValueTypes.UnityEngine_Vector3)
                                            {
                                                SoA_WritePositionUpdate(syncCompanion.gonetParticipant.GONetId, receivedValue.UnityEngine_Vector3, receiverTimestamp, false);
                                            }
                                            else if (receivedValue.GONetSyncType == GONetSyncableValueTypes.UnityEngine_Quaternion)
                                            {
                                                SoA_WriteRotationUpdate(syncCompanion.gonetParticipant.GONetId, receivedValue.UnityEngine_Quaternion, receiverTimestamp, false);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    // VALUE-only mode (no recent VELOCITY bundles received)
                                    // BOTH physics and non-physics need single-write to preserve temporal history!
                                    //
                                    // The anchor double-write was designed for: "reset velocity when VALUE anchor arrives
                                    // during velocity-augmented sync". But in VALUE-only mode, there's NO velocity to reset.
                                    // Double-write just destroys temporal history, making dtSamples≈0 and preventing blending.
                                    //
                                    // With single-write, the blending job can interpolate between successive VALUE samples.
                                    GONetSyncableValue receivedValue = syncCompanion.DeserializeInitSingle_ReadOnlyNotApply(bitStream_headerAlreadyRead, index, false);
                                    syncCompanion.InitSingle(receivedValue, index, elapsedTicksAtSend, false); // isAnchor=false - preserve temporal history!

                                    // DIAG: Trace animator param delta sync arrival on client (only when ValueBlendUtils.ShouldLog is enabled)
                                    // var valueOnlySupport = syncCompanion.valuesChangesSupport[index];
                                    // if (ValueBlendUtils.ShouldLog && IsClient && !string.IsNullOrEmpty(valueOnlySupport.animatorParameterName) && Time.ElapsedSeconds > 3)
                                    // {
                                    //     GONetLog.Debug($"[ANIMATOR-SYNC-CLIENT] RECV delta: param='{valueOnlySupport.animatorParameterName}' value={receivedValue.System_Single} GONetId={gonetParticipant.GONetId} bufferSize={valueOnlySupport.mostRecentChanges_usedSize}");
                                    // }

                                    // CRITICAL FIX (Dec 2025): Also write to SoA in VALUE-only mode.
                                    // After demotion or for objects without velocity bundles, this path is taken.
                                    // Without SoA writes, demoted host objects remain stuck.
                                    var valueOnlyChangesSupport = syncCompanion.valuesChangesSupport[index];
                                    if (syncCompanion.gonetParticipant.v2_isRegisteredInSoA && IsTransformSyncMember(valueOnlyChangesSupport.memberName))
                                    {
                                        long receiverTimestamp = Time.ElapsedTicks;
                                        if (receivedValue.GONetSyncType == GONetSyncableValueTypes.UnityEngine_Vector3)
                                        {
                                            SoA_WritePositionUpdate(syncCompanion.gonetParticipant.GONetId, receivedValue.UnityEngine_Vector3, receiverTimestamp, false);
                                        }
                                        else if (receivedValue.GONetSyncType == GONetSyncableValueTypes.UnityEngine_Quaternion)
                                        {
                                            SoA_WriteRotationUpdate(syncCompanion.gonetParticipant.GONetId, receivedValue.UnityEngine_Quaternion, receiverTimestamp, false);
                                        }
                                    }
                                }
                            }

                            AutoMagicalSync_ValueMonitoringSupport_ChangedValue changedValue = syncCompanion.valuesChangesSupport[index];

#if !PERF_NO_PROCESS_SYNC_EVENTS
                            syncValueChanges_ReceivedFromOtherQueue.Enqueue(GONet_SyncEvent_ValueChangeProcessed_Generated_Factory.CreateInstance(SyncEvent_ValueChangeProcessedExplanation.InboundFromOther, elapsedTicksAtSend, sourceOfChangeConnection.OwnerAuthorityId, changedValue.syncCompanion, changedValue.index));
#endif
                        }
                        else // ASSume values now at rest bundle
                        {
                            //GONetLog.Debug($"remote source says now at rest.  index: {index}. elapsedMsAtSend: {TimeSpan.FromTicks(elapsedTicksAtSend).TotalMilliseconds}  time Now-Source: {TimeSpan.FromTicks(Time.ElapsedTicks - elapsedTicksAtSend).TotalMilliseconds}ms, time remaining before buffer lead time elapsed: {TimeSpan.FromTicks(elapsedTicksAtSend + valueBlendingBufferLeadTicks - Time.ElapsedTicks).TotalMilliseconds}");

                            // clear out the value blending buffer if appropriate and also ensure the value gets set instead of only added to blending buffer!
                            if (syncCompanion.valuesChangesSupport[index].syncAttribute_ShouldBlendBetweenValuesReceived)
                            {
                                // Deserializing from the bit stream has to happen now before waiting in coroutine becuase the rest of the bit stream processing happens immediately hereafter!
                                // We don't want it applied immediately, so we have to setup a coroutine to
                                // apply the value AFTER we ensure the value blending buffer time has elapsed to avoid applying too soon!

                                // VELOCITY-AUGMENTED SYNC: For ValuesNowAtRest, isVelocityBundle should typically be false
                                // (resting values don't have velocity), but pass it for bitstream consistency
                                GONetSyncableValue value = syncCompanion.DeserializeInitSingle_ReadOnlyNotApply(bitStream_headerAlreadyRead, index, isVelocityBundle);

                                // CRITICAL FIX: Save network value BEFORE smart selection for SoA writes
                                // The network value is authoritative - local value may have drifted due to SoA extrapolation
                                GONetSyncableValue networkValue = value;

                                // STAGE 2: Smart at-rest value selection for velocity-eligible fields (Oct 2025)
                                // Non-authority compares received quantized value to local extrapolated value.
                                // If distance < quantization step, keep local value to avoid visual snapping.
                                // Otherwise, use received value for correction.
                                if (syncCompanion.valuesChangesSupport[index].isVelocityEligible && !gonetParticipant.v2_isRegisteredInSoA)
                                {
                                    GONetSyncableValue localValue = syncCompanion.GetAutoMagicalSyncValue(index);
                                    float quantizationStep = syncCompanion.GetQuantizationStepForValue((byte)index);
                                    float distance = syncCompanion.CalculateDistanceBetweenValues(localValue, value);

                                    if (distance < quantizationStep)
                                    {
                                        // Local extrapolation is close enough - keep it to avoid snap
                                        value = localValue;
                                    }
                                    //else
                                    //{
                                        // Extrapolation was off - use received value to correct
                                    //}
                                }

                                syncCompanion.valuesChangesSupport[index].hasAwaitingAtRest = true;
                                long assumedInitialRestElapsedTicks = elapsedTicksAtSend - TimeSpan.FromSeconds(syncCompanion.valuesChangesSupport[index].syncAttribute_SyncChangesEverySeconds).Ticks; // need to subtract the sync rate off of this to know when the value actually first arrived at rest value
                                syncCompanion.valuesChangesSupport[index].hasAwaitingAtRest_assumedInitialRestElapsedTicks = assumedInitialRestElapsedTicks;
                                syncCompanion.valuesChangesSupport[index].hasAwaitingAtRest_sinceLeadTimeAdjustedElapsedTicks = Time.ElapsedTicks - valueBlendingBufferLeadTicks;
                                syncCompanion.valuesChangesSupport[index].hasAwaitingAtRest_value = value;

                                // GONet v2 SoA: Immediately write at-rest value to ring buffer to stop extrapolation drift
                                // Without this, SoA Burst job keeps extrapolating from old velocity data during buffer lead time
                                // isAnchor=false: Single-write preserves temporal history for smooth interpolation to rest position
                                // (double-write from isAnchor=true caused dtSamples≈0 → snapping instead of interpolation)
                                // CRITICAL: Use networkValue (not value) - smart selection may have replaced value with local
                                // TIME BASE FIX: Use receiver's current time (see VELOCITY path comment for details).
                                if (gonetParticipant.v2_isRegisteredInSoA && IsTransformSyncMember(syncCompanion.valuesChangesSupport[index].memberName))
                                {
                                    long receiverTimestamp = Time.ElapsedTicks;
                                    if (networkValue.GONetSyncType == GONetSyncableValueTypes.UnityEngine_Vector3)
                                    {
                                        SoA_WritePositionUpdate(gonetParticipant.GONetId, networkValue.UnityEngine_Vector3, receiverTimestamp, false);
                                    }
                                    else if (networkValue.GONetSyncType == GONetSyncableValueTypes.UnityEngine_Quaternion)
                                    {
                                        SoA_WriteRotationUpdate(gonetParticipant.GONetId, networkValue.UnityEngine_Quaternion, receiverTimestamp, false);
                                    }
                                }

                                // NEW: Check if this is a physics object at-rest (position or rotation sync)
                                // Physics snapping eliminates quantization error: position ~0.95mm → sub-mm, rotation ~0.3° → sub-0.01°
                                bool isPhysicsObject = gonetParticipant.IsRigidBodyOwnerOnlyControlled &&
                                                       gonetParticipant.myRigidBody != null &&
                                                       !gonetParticipant.IsMine;

                                if (isPhysicsObject)
                                {
                                    // Check if this is position or rotation by matching the ShouldSkipSync function pointer
                                    // This relies on the implementation detail that position/rotation use IsPositionNotSyncd/IsRotationNotSyncd delegates
                                    AutoMagicalSync_ValueMonitoringSupport_ChangedValue changedValue = syncCompanion.valuesChangesSupport[index];
                                    bool isPosition = changedValue.syncAttribute_ShouldSkipSync == IsPositionNotSyncd;
                                    bool isRotation = changedValue.syncAttribute_ShouldSkipSync == IsRotationNotSyncd;
                                    bool isPositionOrRotation = isPosition || isRotation;

                                    if (isPositionOrRotation)
                                    {
                                        // PHYSICS SNAPPING: Mark this value as needing physics snap when applied
                                        syncCompanion.valuesChangesSupport[index].hasAwaitingAtRest_needsPhysicsSnap = true;
                                    }
                                }

                                /*{
                                    GONetLog.Debug($"[AT-REST-RECEIVED] GNP:{gonetParticipant.GONetId} index:{index} " +
                                        $"atRestValue:{value} currentValue:{syncCompanion.GetAutoMagicalSyncValue(index)} " +
                                        $"bufferSize:{syncCompanion.valuesChangesSupport[index].mostRecentChanges_usedSize} " +
                                        $"willApplyAt:{TimeSpan.FromTicks(assumedInitialRestElapsedTicks + valueBlendingBufferLeadTicks).TotalSeconds}s");

                                    // Log the buffer contents before clearing
                                    var changes = syncCompanion.valuesChangesSupport[index];
                                    for (int j = 0; j < changes.mostRecentChanges_usedSize; j++)
                                    {
                                        GONetLog.Debug($"  Buffer[{j}]: time:{TimeSpan.FromTicks(changes.mostRecentChanges[j].elapsedTicksAtChange).TotalSeconds}s " +
                                            $"value:{changes.mostRecentChanges[j].numericValue}");
                                    }
                                }*/

                                long assumedOneWayAtRestDelayTicks = Time.ElapsedTicks - assumedInitialRestElapsedTicks;
                                long easingDurationTicks = assumedOneWayAtRestDelayTicks - valueBlendingBufferLeadTicks;

                                // NOTE: This will run immediately if one-way network time exceeds valueBlendingBufferLeadTicks (i.e. non-owner will always be extrapolating!)
                                Global.StartCoroutine(DoAtOrAfterElapsedTicks(() => 
                                {
                                    /* BEFORE
                                    syncCompanion.valuesChangesSupport[index].hasAwaitingAtRest = false;
                                    // Clearing the recent changes buffer effectively ensures that the new value at rest value is the one applied and no
                                    // blending will occur since this is the only value in the blending buffer....neat trick to not need additional code
                                    // to make sure we apply this new value at rest now!
                                    var mostRecentQueuedValue = syncCompanion.valuesChangesSupport[index].mostRecentChanges[0];
                                    syncCompanion.valuesChangesSupport[index].ClearMostRecentChanges();
                                    //GONetLog.Debug($"just cleared most recent changes due to at rest....easingDuration: {TimeSpan.FromTicks(easingDurationTicks).TotalSeconds}\n(OLD) recent buffered:{mostRecentQueuedValue.numericValue} \n(OLD) current value: {syncCompanion.GetAutoMagicalSyncValue(index)}, \n(NEW) at rest value: {value}");
                                    syncCompanion.InitSingle(value, index, assumedInitialRestElapsedTicks);
                                    
                                    //syncCompanion.valuesChangesSupport[index].hasAwaitingAtRest_easeUntilElapsedTicks = ;
                                    */

                                    // AFTER:
                                    var mostRecentQueuedValue = syncCompanion.valuesChangesSupport[index].mostRecentChanges_usedSize > 0
                                        ? syncCompanion.valuesChangesSupport[index].mostRecentChanges[0]
                                        : default;

                                    /*
                                     GONetLog.Debug($"[AT-REST-APPLYING] GNP:{gonetParticipant.GONetId} index:{index} " +
                                        $"clearingBuffer:{syncCompanion.valuesChangesSupport[index].mostRecentChanges_usedSize} items " +
                                        $"lastBufferedValue:{mostRecentQueuedValue.numericValue} " +
                                        $"currentValue:{syncCompanion.GetAutoMagicalSyncValue(index)} " +
                                        $"newAtRestValue:{value}");
                                    */

                                    syncCompanion.valuesChangesSupport[index].hasAwaitingAtRest = false;
                                    syncCompanion.valuesChangesSupport[index].ClearMostRecentChanges();
                                    // isAnchor=false: Single-write preserves temporal history for smooth blending
                                    syncCompanion.InitSingle(value, index, assumedInitialRestElapsedTicks, false);

                                    //GONetLog.Debug($"[AT-REST-APPLIED] GNP:{gonetParticipant.GONetId} index:{index} finalValue:{syncCompanion.GetAutoMagicalSyncValue(index)}");

                                    // NEW: Trigger physics snapping if needed (after value is applied)
                                    if (syncCompanion.valuesChangesSupport[index].hasAwaitingAtRest_needsPhysicsSnap)
                                    {
                                        syncCompanion.valuesChangesSupport[index].hasAwaitingAtRest_needsPhysicsSnap = false;

                                        // Get both position and rotation (may be same or different index)
                                        // Physics snapping requires both to achieve sub-mm position and sub-0.01° rotation
                                        Vector3 position = gonetParticipant.transform.position;
                                        Quaternion rotation = gonetParticipant.transform.rotation;

                                        gonetParticipant.TriggerPhysicsSnapToRest(position, rotation);
                                    }
                                }, assumedInitialRestElapsedTicks + valueBlendingBufferLeadTicks));
                            }
                            else
                            {
                                // VELOCITY-AUGMENTED SYNC: For ValuesNowAtRest (no blending), pass isVelocityBundle
                                // Should typically be false since resting values don't have velocity
                                GONetSyncableValue value = syncCompanion.DeserializeInitSingle_ReadOnlyNotApply(bitStream_headerAlreadyRead, index, isVelocityBundle);

                                GONetLog.Debug($"[AT-REST-NONBLENDED] GNP:{gonetParticipant.GONetId} index:{index} " +
                                    $"receivedValue:{value} isVelocityEligible:{syncCompanion.valuesChangesSupport[index].isVelocityEligible}");

                                // STAGE 2: Smart at-rest value selection for velocity-eligible fields (Oct 2025)
                                // Non-authority compares received quantized value to local extrapolated value.
                                // If distance < quantization step, keep local value to avoid visual snapping.
                                // Otherwise, use received value for correction.
                                if (syncCompanion.valuesChangesSupport[index].isVelocityEligible)
                                {
                                    GONetSyncableValue localValue = syncCompanion.GetAutoMagicalSyncValue(index);
                                    float quantizationStep = syncCompanion.GetQuantizationStepForValue((byte)index);
                                    float distance = syncCompanion.CalculateDistanceBetweenValues(localValue, value);

                                    if (distance < quantizationStep)
                                    {
                                        // Local extrapolation is close enough - keep it to avoid snap
                                        value = localValue;
                                        GONetLog.Debug($"[SMART-AT-REST] GNP:{gonetParticipant.GONetId} index:{index} " +
                                            $"keeping local value (distance={distance:F6} < step={quantizationStep:F6})");
                                    }
                                    else
                                    {
                                        // Extrapolation was off - use received value to correct
                                        GONetLog.Debug($"[SMART-AT-REST] GNP:{gonetParticipant.GONetId} index:{index} " +
                                            $"using received value (distance={distance:F6} >= step={quantizationStep:F6})");
                                    }
                                }

                                // Apply the chosen value (either received quantized OR local extrapolated)
                                // isAnchor=false: Single-write preserves temporal history for smooth blending
                                syncCompanion.InitSingle(value, index, elapsedTicksAtSend, false);

                                // NEW: Immediate physics snap for non-blended physics objects at rest
                                bool isPhysicsObject = gonetParticipant.IsRigidBodyOwnerOnlyControlled &&
                                                       gonetParticipant.myRigidBody != null &&
                                                       !gonetParticipant.IsMine;

                                if (isPhysicsObject)
                                {
                                    // Check if this is position or rotation by matching the ShouldSkipSync function pointer
                                    // This relies on the implementation detail that position/rotation use IsPositionNotSyncd/IsRotationNotSyncd delegates
                                    AutoMagicalSync_ValueMonitoringSupport_ChangedValue changedValue = syncCompanion.valuesChangesSupport[index];
                                    bool isPosition = changedValue.syncAttribute_ShouldSkipSync == IsPositionNotSyncd;
                                    bool isRotation = changedValue.syncAttribute_ShouldSkipSync == IsRotationNotSyncd;
                                    bool isPositionOrRotation = isPosition || isRotation;

                                    if (isPositionOrRotation)
                                    {
                                        // Get current transform values (just applied via DeserializeInitSingle)
                                        Vector3 position = gonetParticipant.transform.position;
                                        Quaternion rotation = gonetParticipant.transform.rotation;

                                        // Trigger physics snapping immediately (no coroutine delay for non-blended values)
                                        gonetParticipant.TriggerPhysicsSnapToRest(position, rotation);
                                    }
                                }
                            }

                            /* TODO change this to an at rest message?  probably not needed...leave commented out for now until deemed useful
                            AutoMagicalSync_ValueMonitoringSupport_ChangedValue changedValue = syncCompanion.valuesChangesSupport[index];

                            syncValueChanges_ReceivedFromOtherQueue.Enqueue(GONet_SyncEvent_ValueChangeProcessed_Generated_Factory.CreateInstance(SyncEvent_ValueChangeProcessedExplanation.InboundFromOther, elapsedTicksAtSend, sourceOfChangeConnection.OwnerAuthorityId, changedValue.syncCompanion, changedValue.index));
                            */
                        }
                    }
                }
                catch (Exception e)
                {
                    // CRITICAL FIX: Defensive property access - object could be destroyed, causing the original exception!
                    // Accessing gonetParticipant properties here would throw ANOTHER NullRef, hiding the original error
                    string logName = "<error-accessing-name>";
                    uint logIdInstantiation = GONetParticipant.GONetId_Unset;
                    uint logIdCurrent = GONetParticipant.GONetId_Unset;
                    uint logCodeGenId = 0;
                    bool logContainsKey = false;

                    try
                    {
                        if (gonetParticipant != null)
                        {
                            logName = gonetParticipant.name;
                            logIdInstantiation = gonetParticipant._GONetIdAtInstantiation;
                            logIdCurrent = gonetParticipant.GONetId;
                            logCodeGenId = gonetParticipant.CodeGenerationId;
                            if (companionMap != null)
                            {
                                logContainsKey = companionMap.ContainsKey(logIdCurrent);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore errors during error logging - we just want to get as much info as possible
                    }

                    GONetLog.Error($"name: {logName} _GONetIdAtInstantiation: {logIdInstantiation}, now: {logIdCurrent}, contains.now? {logContainsKey}, genId: {logCodeGenId}");
                    GONetLog.Error("BOOM! bitStream_headerAlreadyRead  " + e.StackTrace + "  position_bytes: " + bitStream_headerAlreadyRead.Position_Bytes + " Length_WrittenBytes: " + bitStream_headerAlreadyRead.Length_WrittenBytes);

                    throw e;
                }
            }
            //GONetLog.Append_FlushDebug("\n************done reading changes bundle");
        }

        static IEnumerator DoAtOrAfterElapsedTicks(Action doAction, long atOrAfterElapsedTicks)
        {
            while (Time.ElapsedTicks < atOrAfterElapsedTicks)
            {
                yield return null;
            }
            doAction();
        }

        #endregion

    }
}
