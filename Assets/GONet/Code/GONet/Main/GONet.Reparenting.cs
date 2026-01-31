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
 * -The ability to commercialize products built on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using System.Collections.Generic;
using UnityEngine;
using GONet.Utils;

namespace GONet
{
    /// <summary>
    /// Partial class for GONetMain containing reparenting support.
    /// </summary>
    public static partial class GONetMain
    {
        #region Reparenting Support

        /// <summary>
        /// Queue of pending reparent events waiting for object or parent to spawn.
        /// Key: Target GONetId, Value: List of pending events targeting this GONetId.
        /// </summary>
        private static readonly Dictionary<uint, List<ReparentGONetParticipantEvent>> pendingReparentEventsByTargetId =
            new Dictionary<uint, List<ReparentGONetParticipantEvent>>();

        /// <summary>
        /// Queue of pending reparent events waiting for parent GONetId to be available.
        /// Key: Parent GONetId, Value: List of pending events waiting for this parent.
        /// </summary>
        private static readonly Dictionary<uint, List<ReparentGONetParticipantEvent>> pendingReparentEventsByParentId =
            new Dictionary<uint, List<ReparentGONetParticipantEvent>>();

        /// <summary>
        /// Queue of pending reparent events waiting for a non-GNP parent path to be available.
        /// Key: Parent full unique path, Value: List of pending events waiting for this parent path.
        /// </summary>
        private static readonly Dictionary<string, List<ReparentGONetParticipantEvent>> pendingReparentEventsByParentPath =
            new Dictionary<string, List<ReparentGONetParticipantEvent>>();

        /// <summary>
        /// Tracks when pending events were enqueued for timeout management.
        /// Key: Event, Value: EnqueueTime (Time.unscaledTime).
        /// </summary>
        private static readonly Dictionary<ReparentGONetParticipantEvent, float> pendingReparentEventEnqueueTimes =
            new Dictionary<ReparentGONetParticipantEvent, float>();

        /// <summary>
        /// Reverse lookup: All pending events indexed by their TARGET GONetId.
        /// Used for O(1) lookup in RemovePendingReparentsForTarget instead of O(n) iteration.
        /// An event is added here regardless of which queue it's in (ByTargetId, ByParentId, ByParentPath).
        /// Key: Target GONetId, Value: Set of events targeting this GONetId.
        /// </summary>
        private static readonly Dictionary<uint, HashSet<ReparentGONetParticipantEvent>> allPendingReparentsByTargetGONetId =
            new Dictionary<uint, HashSet<ReparentGONetParticipantEvent>>();

        /// <summary>
        /// Rate limiting: Tracks reparent count per authority per second.
        /// Key: SourceAuthorityId, Value: (LastSecondStartTime, CountThisSecond).
        /// </summary>
        private static readonly Dictionary<ushort, (float lastSecondStart, int count)> reparentRateLimits =
            new Dictionary<ushort, (float, int)>();

        private static bool isReparentEventHandlerSubscribed = false;

        /// <summary>
        /// Traverses the hierarchy starting from the given transform to find an ancestor GONetParticipant
        /// that has position or rotation sync enabled. This is used to determine if a child object's
        /// transform sync should be suspended when parented to an intermediate non-GNP transform
        /// (e.g., itemHoldSocket) that is itself a child of a synced GONetParticipant (e.g., player).
        /// </summary>
        /// <param name="startTransform">The transform to start searching from (the new parent).</param>
        /// <returns>The ancestor GONetParticipant with synced transform, or null if none found.</returns>
        private static GONetParticipant FindAncestorGNPWithSyncedTransform(Transform startTransform)
        {
            Transform current = startTransform;
            while (current != null)
            {
                GONetParticipant gnp = current.GetComponent<GONetParticipant>();
                if (gnp != null && (gnp.IsPositionSyncd || gnp.IsRotationSyncd))
                {
                    return gnp;
                }
                current = current.parent;
            }
            return null;
        }

        /// <summary>
        /// Initialize reparenting event subscriptions. Called during GONet initialization.
        /// </summary>
        internal static void InitReparentingSupport()
        {
            if (isReparentEventHandlerSubscribed) return;

            EventBus.Subscribe<ReparentGONetParticipantEvent>(OnReparentEventReceived);
            isReparentEventHandlerSubscribed = true;

            if (GONetConfig.LogReparentDiagnostics)
            {
                GONetLog.Debug("[REPARENT] Reparenting event handler subscribed");
            }
        }

        /// <summary>
        /// Gets all registered GONetParticipants.
        /// </summary>
        public static IEnumerable<GONetParticipant> GetAllGONetParticipants()
        {
            return gonetParticipantByGONetIdMap.Values;
        }

        /// <summary>
        /// Called when a ReparentGONetParticipantEvent self-cancels (object returned to original parent).
        /// Removes any persisted reparent event for this object.
        /// </summary>
        internal static void OnReparentSelfCancel(GONetParticipant participant)
        {
            if (participant == null || participant.GONetId == GONetParticipant.GONetId_Unset)
                return;

            // Remove from persistent events (late-joiner delivery)
            if (IsServer)
            {
                RemoveReparentEventFromPersistence(participant.GONetId);
            }

            if (GONetConfig.LogReparentDiagnostics)
            {
                GONetLog.Debug($"[REPARENT] Self-cancel processed for GONetId {participant.GONetId}");
            }
        }

        /// <summary>
        /// Handler for ReparentGONetParticipantEvent.
        /// </summary>
        private static void OnReparentEventReceived(GONetEventEnvelope<ReparentGONetParticipantEvent> eventEnvelope)
        {
            ReparentGONetParticipantEvent @event = eventEnvelope.Event;
            if (@event == null) return;

            // DETACH FIX (Jan 2026): Removed ShouldSelfCancel check on the RECEIVING side.
            // The self-cancel logic should only be applied on the SENDING side (in OnTransformParentChanged).
            // If an event was published and sent over the network, the receiver MUST process it.
            // Previously, detach events (world root → parent → world root) were incorrectly rejected here
            // because ShouldSelfCancel looked at original (spawn-time) parent vs new parent, without
            // knowing about the intermediate parent that was legitimately set and now being detached from.
            if (GONetConfig.LogReparentDiagnostics)
            {
                GONetLog.Debug($"[REPARENT] Received event: GONetId={@event.GONetId}, NewParentGONetId={@event.NewParentGONetId}, NewParentRelPath={@event.NewParentRelativePath}, OrigParentGONetId={@event.OriginalParentGONetId}, OrigParentRelPath={@event.OriginalParentRelativePath}, IsSourceRemote={eventEnvelope.IsSourceRemote}");
            }

            // Server-side validation
            if (IsServer && eventEnvelope.IsSourceRemote)
            {
                if (!ValidateReparentEvent(@event, eventEnvelope))
                {
                    GONetLog.Warning($"[REPARENT] Rejected invalid reparent event from authority {@event.SourceAuthorityId} for GONetId {@event.GONetId}");
                    return;
                }
            }

            // Find target participant
            GONetParticipant target = GetGONetParticipantById(@event.GONetId);
            if (target == null)
            {
                // Target not yet spawned - queue for later
                EnqueuePendingReparentForTarget(@event);
                return;
            }

            if (target.IsPooledInactive)
            {
                if (GONetConfig.LogReparentDiagnostics)
                {
                    GONetLog.Debug($"[REPARENT-SKIP] Ignoring reparent event for pooled inactive '{target.name}' GONetId {@event.GONetId}");
                }
                return;
            }

            // If we're the authority, we already did the reparent locally
            if (target.IsMine)
            {
                if (GONetConfig.LogReparentDiagnostics)
                {
                    GONetLog.Debug($"[REPARENT-SKIP] Ignoring own reparent event for '{target.name}' GONetId {@event.GONetId} (IsMine=true, OwnerAuthorityId={target.OwnerAuthorityId})");
                }
                return;
            }

            // Apply the reparent on non-authority
            ApplyReparentEvent(target, @event);
        }

        /// <summary>
        /// Validates a reparent event on the server.
        /// </summary>
        private static bool ValidateReparentEvent(ReparentGONetParticipantEvent @event, GONetEventEnvelope<ReparentGONetParticipantEvent> envelope)
        {
            // Rate limiting
            if (GONetConfig.MaxReparentsPerSecondPerAuthority > 0)
            {
                float now = UnityEngine.Time.unscaledTime;
                if (!reparentRateLimits.TryGetValue(@event.SourceAuthorityId, out var limit))
                {
                    limit = (now, 0);
                }

                if (now - limit.lastSecondStart >= 1.0f)
                {
                    // New second - reset counter
                    limit = (now, 1);
                }
                else
                {
                    limit.count++;
                    if (limit.count > GONetConfig.MaxReparentsPerSecondPerAuthority)
                    {
                        GONetLog.Warning($"[REPARENT] Rate limit exceeded for authority {@event.SourceAuthorityId}: {limit.count} reparents/sec (max: {GONetConfig.MaxReparentsPerSecondPerAuthority})");
                        return false;
                    }
                }

                reparentRateLimits[@event.SourceAuthorityId] = limit;
            }

            // Verify source has authority over the object
            GONetParticipant target = GetGONetParticipantById(@event.GONetId);
            if (target != null)
            {
                if (target.OwnerAuthorityId != @event.SourceAuthorityId)
                {
                    GONetLog.Warning($"[REPARENT] Authority mismatch: event from {@event.SourceAuthorityId} but object owned by {target.OwnerAuthorityId}");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Applies a reparent event to a target participant.
        /// </summary>
        private static bool ApplyReparentEvent(GONetParticipant target, ReparentGONetParticipantEvent @event)
        {
            if (target == null) return false;

            // Suppress local reparent event generation during remote apply
            target.suppressReparentEvent = true;

            try
            {
                Transform newParent = null;

                // Find new parent
                if (@event.NewParentGONetId != GONetParticipant.GONetId_Unset)
                {
                    // Parent is a GONetParticipant (or anchor for relative path)
                    GONetParticipant parentGNP = GetGONetParticipantById(@event.NewParentGONetId);
                    if (parentGNP == null)
                    {
                        // Parent not yet spawned - queue for later
                        EnqueuePendingReparentForParent(@event);
                        return false;
                    }

                    if (!string.IsNullOrEmpty(@event.NewParentRelativePath))
                    {
                        GameObject parentGO = HierarchyUtils.FindByRelativeUniquePath(parentGNP.gameObject, @event.NewParentRelativePath);
                        if (parentGO == null)
                        {
                            if (GONetConfig.LogReparentDiagnostics)
                            {
                                GONetLog.Warning($"[REPARENT] Failed to resolve relative parent path '{@event.NewParentRelativePath}' under anchor {parentGNP.GONetId} for GONetId {@event.GONetId}");
                            }

                            if (!string.IsNullOrEmpty(@event.NewParentFullUniquePath))
                            {
                                parentGO = HierarchyUtils.FindByFullUniquePath(@event.NewParentFullUniquePath);
                                if (parentGO == null)
                                {
                                    EnqueuePendingReparentForParentPath(@event);
                                    return false;
                                }
                            }
                            else
                            {
                                return false;
                            }
                        }
                        newParent = parentGO.transform;
                    }
                    else
                    {
                        newParent = parentGNP.transform;
                    }
                }
                else if (!string.IsNullOrEmpty(@event.NewParentFullUniquePath))
                {
                    // Parent is non-GNP container - find by path
                    GameObject parentGO = HierarchyUtils.FindByFullUniquePath(@event.NewParentFullUniquePath);
                    if (parentGO == null)
                    {
                        EnqueuePendingReparentForParentPath(@event);
                        return false;
                    }
                    newParent = parentGO.transform;
                }
                // else newParent = null (world root)

                // Log detachment for diagnostics
                if (newParent == null && GONetConfig.LogReparentDiagnostics)
                {
                    GONetLog.Debug($"[REPARENT] Applying DETACH for '{target.name}' (GONetId {@event.GONetId}) - setting parent to NULL (world root)");
                }

                // CLIENT DROP FIX (Jan 2026): When detaching an item that the local client initiated
                // a drop for, preserve the client's position instead of using the server's position.
                // The server has a delayed view of where the client's player is, so its drop position
                // would be wrong. The client knows the correct position (captured when drop was initiated).
                bool preserveLocalDropPosition = false;
                Vector3 localDropWorldPosition = Vector3.zero;
                Quaternion localDropWorldRotation = Quaternion.identity;

                // DIAGNOSTIC: Log all state for drop analysis
                if (newParent == null && GONetConfig.LogReparentDiagnostics)
                {
                    string currentParentName = target.transform.parent != null ? target.transform.parent.name : "NULL";
                    GONetLog.Debug($"[REPARENT-DROP-DIAG] '{target.name}' (GONetId {@event.GONetId}) DETACH STATE: " +
                                  $"currentParent='{currentParentName}' " +
                                  $"localClientInitiatedDrop={target.localClientInitiatedDrop} " +
                                  $"IsSuspended={target.IsTransformSyncSuspendedDueToParenting} " +
                                  $"currentWorldPos={target.transform.position} " +
                                  $"preservedPos={target.localDropPreservedPosition} " +
                                  $"eventLocalPos={@event.LocalPositionOffset}");
                }

                // PRIMARY CHECK: Did the local client initiate this drop?
                // This flag is set by calling MarkLocalDropInitiated() before the drop RPC.
                if (newParent == null && target.localClientInitiatedDrop)
                {
                    preserveLocalDropPosition = true;
                    localDropWorldPosition = target.localDropPreservedPosition;
                    localDropWorldRotation = target.localDropPreservedRotation;
                    GONetLog.Debug($"[REPARENT-DROP-FIX] LOCAL DROP DETECTED: '{target.name}' - " +
                                  $"using preserved client pos={localDropWorldPosition} (event had {@event.LocalPositionOffset})");
                    target.ClearLocalDropFlag();
                }
                // FALLBACK: Check if item was suspended and already unparented locally
                else if (newParent == null && target.IsTransformSyncSuspendedDueToParenting && target.transform.parent == null)
                {
                    preserveLocalDropPosition = true;
                    localDropWorldPosition = target.transform.position;
                    localDropWorldRotation = target.transform.rotation;
                    GONetLog.Debug($"[REPARENT-DROP-FIX] FALLBACK CASE: '{target.name}' - suspended + already unparented - " +
                                  $"preserving current pos={localDropWorldPosition}");
                }
                else if (newParent == null && GONetConfig.LogReparentDiagnostics)
                {
                    GONetLog.Debug($"[REPARENT-DROP-FIX] NO FIX APPLIED: '{target.name}' - using event position {@event.LocalPositionOffset}");
                }

                // Apply reparent
                target.transform.SetParent(newParent);

                // NON-AUTHORITY PHYSICS HANDLING: Disable physics on child when reparenting to a parent.
                // Save original state BEFORE modifying so it can be restored correctly on detach.
                // This runs before SuspendTransformSync() to ensure state is saved with original values.
                if (newParent != null)
                {
                    Rigidbody rb = target.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        // Save original state BEFORE modifying (only if not already saved)
                        if (!target.hasReparentPhysicsStateSaved)
                        {
                            target.suspendedRigidbodyWasKinematic = rb.isKinematic;
                            target.suspendedRigidbodyWasUseGravity = rb.useGravity;
                            target.hasReparentPhysicsStateSaved = true;
                        }

                        // Disable physics completely
                        // IMPORTANT: Zero velocities BEFORE setting kinematic (Unity doesn't allow setting velocity on kinematic bodies)
                        if (!rb.isKinematic)
                        {
#if UNITY_6000_0_OR_NEWER
                            rb.linearVelocity = Vector3.zero;
#else
                            rb.velocity = Vector3.zero;
#endif
                            rb.angularVelocity = Vector3.zero;
                            rb.isKinematic = true;
                        }
                        rb.useGravity = false;

                        if (GONetConfig.LogReparentDiagnostics)
                        {
                            GONetLog.Debug($"[REPARENT] Disabled physics on '{target.name}' after reparenting to '{newParent.name}' - " +
                                          $"saved original: Kinematic={target.suspendedRigidbodyWasKinematic}, UseGravity={target.suspendedRigidbodyWasUseGravity}");
                        }
                    }
                }

                // Apply local offsets (or preserved world position for client drops)
                if (preserveLocalDropPosition)
                {
                    // CLIENT DROP: Use the client's captured world position instead of server's
                    // When parent is null, localPosition == world position
                    target.transform.localPosition = localDropWorldPosition;
                    target.transform.localRotation = localDropWorldRotation;
                    target.SetReparentGuardValues(localDropWorldPosition, localDropWorldRotation);
                }
                else
                {
                    target.transform.localPosition = @event.LocalPositionOffset;
                    target.transform.localRotation = @event.LocalRotationOffset;
                    // REPARENT POSITION GUARD (Jan 2026): Store the intended local offsets for enforcement.
                    // This ensures any late sync/blending that overwrites these values will be corrected.
                    target.SetReparentGuardValues(@event.LocalPositionOffset, @event.LocalRotationOffset);
                }

                // Check if transform sync should be suspended (nested GNP)
                // FIX (Jan 2026): Traverse hierarchy to find ancestor GONetParticipant with synced transform.
                // The immediate parent might be a non-GNP transform (e.g., itemHoldSocket) that is a child
                // of a GONetParticipant (e.g., player). We need to check ALL ancestors, not just immediate parent.
                if (GONetConfig.EnableTransformSyncSuspensionForNestedGNPs && newParent != null)
                {
                    GONetParticipant ancestorGNP = FindAncestorGNPWithSyncedTransform(newParent);
                    if (ancestorGNP != null)
                    {
                        target.SuspendTransformSync();
                        if (GONetConfig.LogReparentDiagnostics)
                        {
                            GONetLog.Debug($"[REPARENT] Suspended transform sync for '{target.name}' - found ancestor GNP '{ancestorGNP.name}' with synced transform");
                        }
                    }
                }

                // Resume transform sync and physics if unparented (detached back to world root)
                if (newParent == null)
                {
                    if (target.IsTransformSyncSuspendedDueToParenting)
                    {
                        // ResumeTransformSync handles both sync resumption AND physics restoration
                        target.ResumeTransformSync();
                    }
                    else if (target.hasReparentPhysicsStateSaved)
                    {
                        // Non-synced parent case: ResumeTransformSync wasn't called, restore physics using saved state
                        Rigidbody rb = target.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            rb.isKinematic = target.suspendedRigidbodyWasKinematic;
                            rb.useGravity = target.suspendedRigidbodyWasUseGravity;

                            if (GONetConfig.LogReparentDiagnostics)
                            {
                                GONetLog.Debug($"[REPARENT] Restored physics on '{target.name}' after detaching - " +
                                              $"Kinematic={target.suspendedRigidbodyWasKinematic}, UseGravity={target.suspendedRigidbodyWasUseGravity}");
                            }
                        }
                        target.hasReparentPhysicsStateSaved = false;
                    }

                    // Clear guard values when detached so world-root items can simulate freely.
                    if (target.hasReparentGuardValues)
                    {
                        target.hasReparentGuardValues = false;
                        target.reparentGuardParent = null;
                        if (GONetConfig.LogReparentDiagnostics)
                        {
                            GONetLog.Debug($"[REPARENT-GUARD] Cleared guard values for '{target.name}' (GONetId {@event.GONetId}) after detaching to WorldRoot");
                        }
                    }
                }

                if (GONetConfig.LogReparentDiagnostics)
                {
                    string parentDesc = newParent != null ? newParent.name : "WorldRoot";
                    GONetLog.Debug($"[REPARENT] Applied reparent for '{target.name}' (GONetId {@event.GONetId}) to parent '{parentDesc}'");
                }

                // DIAGNOSTIC: Log final position after all reparent operations complete
                if (newParent == null)
                {
                    //GONetLog.Debug($"[REPARENT-FINAL-POS] '{target.name}' (GONetId {@event.GONetId}) FINAL STATE after detach: " +
                    //              $"worldPos={target.transform.position} localPos={target.transform.localPosition} " +
                    //              $"parent='{(target.transform.parent != null ? target.transform.parent.name : "NULL")}' " +
                    //              $"eventPos={@event.LocalPositionOffset}");
                }
            }
            finally
            {
                target.suppressReparentEvent = false;
            }

            return true;
        }

        /// <summary>
        /// Enqueues a reparent event waiting for the target object to spawn.
        /// </summary>
        private static void EnqueuePendingReparentForTarget(ReparentGONetParticipantEvent @event)
        {
            RemovePendingReparentsForTarget(@event.GONetId);

            if (!pendingReparentEventsByTargetId.TryGetValue(@event.GONetId, out var list))
            {
                list = new List<ReparentGONetParticipantEvent>();
                pendingReparentEventsByTargetId[@event.GONetId] = list;
            }

            // Replace any existing reparent for same target (ICancelOutOtherEvents behavior)
            if (list.Count > 0)
            {
                foreach (var existing in list)
                {
                    pendingReparentEventEnqueueTimes.Remove(existing);
                    RemoveFromReverseLookup(existing);
                }
                list.Clear();
            }
            list.Add(@event);
            pendingReparentEventEnqueueTimes[@event] = UnityEngine.Time.unscaledTime;
            AddToReverseLookup(@event);

            if (GONetConfig.LogReparentDiagnostics)
            {
                GONetLog.Debug($"[REPARENT] Queued event waiting for target GONetId {@event.GONetId} to spawn");
            }
        }

        /// <summary>
        /// Enqueues a reparent event waiting for the parent object to spawn.
        /// </summary>
        private static void EnqueuePendingReparentForParent(ReparentGONetParticipantEvent @event)
        {
            if (@event.NewParentGONetId == GONetParticipant.GONetId_Unset) return;

            RemovePendingReparentsForTarget(@event.GONetId);

            if (!pendingReparentEventsByParentId.TryGetValue(@event.NewParentGONetId, out var list))
            {
                list = new List<ReparentGONetParticipantEvent>();
                pendingReparentEventsByParentId[@event.NewParentGONetId] = list;
            }

            if (!list.Contains(@event))
            {
                list.Add(@event);
            }
            if (!pendingReparentEventEnqueueTimes.ContainsKey(@event))
            {
                pendingReparentEventEnqueueTimes[@event] = UnityEngine.Time.unscaledTime;
                AddToReverseLookup(@event);
            }

            if (GONetConfig.LogReparentDiagnostics)
            {
                GONetLog.Debug($"[REPARENT] Queued event for GONetId {@event.GONetId} waiting for parent {@event.NewParentGONetId} to spawn");
            }
        }

        /// <summary>
        /// Enqueues a reparent event waiting for a non-GNP parent path to resolve.
        /// </summary>
        private static void EnqueuePendingReparentForParentPath(ReparentGONetParticipantEvent @event)
        {
            if (string.IsNullOrEmpty(@event.NewParentFullUniquePath))
            {
                return;
            }

            RemovePendingReparentsForTarget(@event.GONetId);

            if (!pendingReparentEventsByParentPath.TryGetValue(@event.NewParentFullUniquePath, out var list))
            {
                list = new List<ReparentGONetParticipantEvent>();
                pendingReparentEventsByParentPath[@event.NewParentFullUniquePath] = list;
            }

            if (!list.Contains(@event))
            {
                list.Add(@event);
            }
            if (!pendingReparentEventEnqueueTimes.ContainsKey(@event))
            {
                pendingReparentEventEnqueueTimes[@event] = UnityEngine.Time.unscaledTime;
                AddToReverseLookup(@event);
            }

            if (GONetConfig.LogReparentDiagnostics)
            {
                GONetLog.Debug($"[REPARENT] Queued event for GONetId {@event.GONetId} waiting for parent path '{@event.NewParentFullUniquePath}'");
            }
        }

        /// <summary>
        /// Removes any pending reparent events for the given target GONetId.
        /// Called when a newer reparent event arrives or when the target is processed.
        ///
        /// PERFORMANCE (Jan 2026): Uses O(1) lookup via allPendingReparentsByTargetGONetId reverse index.
        /// </summary>
        private static void RemovePendingReparentsForTarget(uint gonetId)
        {
            if (!allPendingReparentsByTargetGONetId.TryGetValue(gonetId, out var eventsForTarget))
            {
                return; // No pending events for this target - O(1) check
            }

            // Copy to list since we'll modify the collection during iteration
            var toRemove = new List<ReparentGONetParticipantEvent>(eventsForTarget);

            foreach (var pendingEvent in toRemove)
            {
                RemovePendingReparentEvent(pendingEvent);
            }
        }

        /// <summary>
        /// Called when a GONetParticipant is registered. Processes any pending reparent events.
        /// </summary>
        internal static void OnParticipantRegistered_ProcessPendingReparents(uint gonetId)
        {
            // Process events waiting for this object as target
            if (pendingReparentEventsByTargetId.TryGetValue(gonetId, out var targetEvents))
            {
                pendingReparentEventsByTargetId.Remove(gonetId);

                GONetParticipant target = GetGONetParticipantById(gonetId);
                if (target != null && !target.IsMine)
                {
                    foreach (var @event in targetEvents)
                    {
                        if (ApplyReparentEvent(target, @event))
                        {
                            RemovePendingReparentEvent(@event);
                        }
                    }
                }
            }

            // Process events waiting for this object as parent
            if (pendingReparentEventsByParentId.TryGetValue(gonetId, out var parentEvents))
            {
                pendingReparentEventsByParentId.Remove(gonetId);

                foreach (var @event in parentEvents)
                {
                    GONetParticipant target = GetGONetParticipantById(@event.GONetId);
                    if (target != null && !target.IsMine)
                    {
                        if (ApplyReparentEvent(target, @event))
                        {
                            RemovePendingReparentEvent(@event);
                        }
                    }
                }
            }

            GONetParticipant registeredParticipant = GetGONetParticipantById(gonetId);
            if (registeredParticipant != null)
            {
                string sceneName = GONetSceneManager.GetSceneIdentifier(registeredParticipant.gameObject);
                if (!string.IsNullOrEmpty(sceneName))
                {
                    ProcessPendingReparentsForScene(sceneName);
                }
            }
        }

        /// <summary>
        /// Returns true if there is a pending reparent event for the specified target GONetId.
        /// Used to defer AllValues processing until reparent is applied.
        ///
        /// PERFORMANCE (Jan 2026): Uses O(1) lookup via allPendingReparentsByTargetGONetId reverse index.
        /// </summary>
        internal static bool HasPendingReparentEventForTarget(uint gonetId)
        {
            return allPendingReparentsByTargetGONetId.ContainsKey(gonetId);
        }

        internal static void ClearPendingReparentsForTarget(uint gonetId)
        {
            RemovePendingReparentsForTarget(gonetId);
        }

        /// <summary>
        /// Attempts to process any pending reparent events for targets in the specified scene.
        /// Returns the number of events applied.
        /// </summary>
        internal static int ProcessPendingReparentsForScene(string sceneName)
        {
            if (pendingReparentEventEnqueueTimes.Count == 0) return 0;

            List<ReparentGONetParticipantEvent> toProcess = new List<ReparentGONetParticipantEvent>();
            foreach (var kvp in pendingReparentEventEnqueueTimes)
            {
                ReparentGONetParticipantEvent pendingEvent = kvp.Key;
                GONetParticipant target = GetGONetParticipantById(pendingEvent.GONetId);
                if (target == null || target.IsMine)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(sceneName))
                {
                    string targetScene = GONetSceneManager.GetSceneIdentifier(target.gameObject);
                    if (targetScene != sceneName)
                    {
                        continue;
                    }
                }

                toProcess.Add(pendingEvent);
            }

            if (toProcess.Count == 0) return 0;

            int appliedCount = 0;
            foreach (var pendingEvent in toProcess)
            {
                GONetParticipant target = GetGONetParticipantById(pendingEvent.GONetId);
                if (target == null || target.IsMine)
                {
                    continue;
                }

                if (ApplyReparentEvent(target, pendingEvent))
                {
                    RemovePendingReparentEvent(pendingEvent);
                    appliedCount++;
                }
            }

            if (appliedCount > 0 && GONetConfig.LogReparentDiagnostics)
            {
                GONetLog.Debug($"[REPARENT] Processed {appliedCount} pending reparent event(s) for scene '{sceneName}'");
            }

            return appliedCount;
        }

        private static void RemovePendingReparentEvent(ReparentGONetParticipantEvent @event)
        {
            pendingReparentEventEnqueueTimes.Remove(@event);
            RemoveFromReverseLookup(@event);

            if (pendingReparentEventsByTargetId.TryGetValue(@event.GONetId, out var targetList))
            {
                targetList.RemoveAll(existing => existing == @event);
                if (targetList.Count == 0)
                {
                    pendingReparentEventsByTargetId.Remove(@event.GONetId);
                }
            }

            if (@event.NewParentGONetId != GONetParticipant.GONetId_Unset &&
                pendingReparentEventsByParentId.TryGetValue(@event.NewParentGONetId, out var parentList))
            {
                parentList.RemoveAll(existing => existing == @event);
                if (parentList.Count == 0)
                {
                    pendingReparentEventsByParentId.Remove(@event.NewParentGONetId);
                }
            }

            if (!string.IsNullOrEmpty(@event.NewParentFullUniquePath) &&
                pendingReparentEventsByParentPath.TryGetValue(@event.NewParentFullUniquePath, out var parentPathList))
            {
                parentPathList.RemoveAll(existing => existing == @event);
                if (parentPathList.Count == 0)
                {
                    pendingReparentEventsByParentPath.Remove(@event.NewParentFullUniquePath);
                }
            }
        }

        /// <summary>
        /// Adds an event to the reverse lookup for O(1) removal by target GONetId.
        /// </summary>
        private static void AddToReverseLookup(ReparentGONetParticipantEvent @event)
        {
            if (!allPendingReparentsByTargetGONetId.TryGetValue(@event.GONetId, out var set))
            {
                set = new HashSet<ReparentGONetParticipantEvent>();
                allPendingReparentsByTargetGONetId[@event.GONetId] = set;
            }
            set.Add(@event);
        }

        /// <summary>
        /// Removes an event from the reverse lookup.
        /// </summary>
        private static void RemoveFromReverseLookup(ReparentGONetParticipantEvent @event)
        {
            if (allPendingReparentsByTargetGONetId.TryGetValue(@event.GONetId, out var set))
            {
                set.Remove(@event);
                if (set.Count == 0)
                {
                    allPendingReparentsByTargetGONetId.Remove(@event.GONetId);
                }
            }
        }

        /// <summary>
        /// Removes a reparent event from persistence (late-joiner delivery).
        /// Called when an object returns to its original parent (self-cancel) or is despawned.
        ///
        /// REPARENTING FIX (Jan 2026): Actually implement the removal from persistentEventsThisSession.
        /// Previously this was a no-op, causing stale reparent events to be sent to late joiners
        /// for objects that no longer exist, resulting in timeouts and wasted bandwidth.
        /// </summary>
        private static void RemoveReparentEventFromPersistence(uint gonetId)
        {
            // Iterate through persistentEventsThisSession and remove any ReparentGONetParticipantEvent
            // matching this GONetId. There should be at most one per GONetId due to ICancelOutOtherEvents.
            var node = persistentEventsThisSession.First;
            while (node != null)
            {
                var next = node.Next;
                if (node.Value is ReparentGONetParticipantEvent reparentEvent &&
                    reparentEvent.GONetId == gonetId)
                {
                    persistentEventsThisSession.Remove(node);
                    if (GONetConfig.LogReparentDiagnostics)
                    {
                        GONetLog.Debug($"[REPARENT] Removed persisted reparent event for GONetId {gonetId}");
                    }
                    break; // Only one reparent event per GONetId due to ICancelOutOtherEvents
                }
                node = next;
            }
        }

        /// <summary>
        /// Update loop for reparenting - processes timeouts and auto-publishes.
        /// Called from GONetMain.Update().
        /// NOTE: EnforceReparentPositionGuards() is called separately from Update_DoTheHeavyLifting_IfAppropriate()
        /// (in LateUpdate) so it runs AFTER all sync/blending has been applied.
        /// </summary>
        internal static void Update_Reparenting()
        {
            // Process pending reparent timeouts
            UpdatePendingReparentTimeouts();

            // Auto-publish pending reparent events for authority participants
            UpdateAutoPendingReparentPublishes();
        }

        /// <summary>
        /// Checks for and handles timed out pending reparent events.
        /// </summary>
        private static void UpdatePendingReparentTimeouts()
        {
            if (pendingReparentEventEnqueueTimes.Count == 0) return;

            float now = UnityEngine.Time.unscaledTime;
            float timeoutSeconds = GONetConfig.PendingReparentTimeoutSeconds;

            List<ReparentGONetParticipantEvent> timedOut = null;

            foreach (var kvp in pendingReparentEventEnqueueTimes)
            {
                float waitedSeconds = now - kvp.Value;
                if (waitedSeconds > timeoutSeconds)
                {
                    timedOut ??= new List<ReparentGONetParticipantEvent>();
                    timedOut.Add(kvp.Key);

                    GONetLog.Warning($"[REPARENT] Pending event for GONetId {kvp.Key.GONetId} timed out after {waitedSeconds:F2}s");
                    GONetConfig.RaiseReparentTimeout(kvp.Key.GONetId, kvp.Key.NewParentGONetId, waitedSeconds);
                }
            }

            if (timedOut != null)
            {
                foreach (var @event in timedOut)
                {
                    RemovePendingReparentEvent(@event);
                }
            }
        }

        /// <summary>
        /// Auto-publishes pending reparent events for authority participants.
        /// </summary>
        private static void UpdateAutoPendingReparentPublishes()
        {
            foreach (var gnp in gonetParticipantByGONetIdMap.Values)
            {
                if (gnp != null && gnp.pendingReparentEvent != null && gnp.IsMine)
                {
                    gnp.AutoPublishPendingReparentIfNeeded();
                }
            }
        }

        /// <summary>
        /// REPARENT POSITION GUARD (Jan 2026): Enforces correct local offsets for reparented children.
        /// This is a safety net that runs after all sync/blending and re-applies the intended local
        /// position/rotation if they have drifted beyond a threshold.
        ///
        /// Why this is needed:
        /// - We've added suspension checks to V1 blending, SoA apply, and legacy SoA apply
        /// - But there may be edge cases or race conditions where sync data is applied before
        ///   the suspension flag is set, or from a code path we haven't identified
        /// - This guard ensures 100% reliability by checking every frame and correcting drift
        ///
        /// Performance: Only iterates GNPs with hasReparentGuardValues=true (children of reparented objects).
        /// These are typically a small subset of all GNPs.
        ///
        /// OPT-OUT: This guard can be disabled globally via GONetConfig.EnableReparentPositionGuard = false,
        /// or per-instance via GONetParticipant.disableReparentPositionGuard = true.
        /// Disable the guard when you need to manually sync or animate the child's local position/rotation.
        /// </summary>
        private static void EnforceReparentPositionGuards()
        {
            // Global opt-out check
            if (!GONetConfig.EnableReparentPositionGuard)
                return;

            const float POSITION_TOLERANCE = 0.001f; // 1mm tolerance
            const float ROTATION_DOT_TOLERANCE = 0.9999f; // ~0.8 degrees tolerance

            foreach (var gnp in gonetParticipantByGONetIdMap.Values)
            {
                // Only check GNPs that have guard values set (reparented children on non-authority machines)
                if (gnp == null || !gnp.hasReparentGuardValues || gnp.IsMine)
                    continue;

                // Per-instance opt-out check
                if (gnp.disableReparentPositionGuard)
                    continue;

                // PARENT MISMATCH CHECK: If the current parent doesn't match what the guard was set for,
                // the guard values are invalid and should not be enforced. This happens when a client
                // locally unparents (e.g., drops an item) before the server's reparent event arrives.
                // Enforcing the old guard values (which were relative to the old parent) would cause
                // the item to jump to the wrong world position.
                if (gnp.transform.parent != gnp.reparentGuardParent)
                {
                    if (GONetConfig.LogReparentDiagnostics)
                    {
                        string currentParentName = gnp.transform.parent != null ? gnp.transform.parent.name : "NULL";
                        string guardParentName = gnp.reparentGuardParent != null ? gnp.reparentGuardParent.name : "NULL";
                        GONetLog.Debug($"[REPARENT-GUARD] SKIPPING enforcement for '{gnp.name}' (GONetId {gnp.GONetId}) - " +
                                      $"parent mismatch: current='{currentParentName}' vs guard='{guardParentName}' - " +
                                      $"client likely unparented locally before server event arrived");
                    }
                    continue;
                }

                // Check if position has drifted
                Vector3 currentLocalPos = gnp.transform.localPosition;
                float posDist = Vector3.Distance(currentLocalPos, gnp.reparentGuardLocalPosition);

                if (posDist > POSITION_TOLERANCE)
                {
                    // Position drifted - correct it
                    gnp.transform.localPosition = gnp.reparentGuardLocalPosition;

                    if (GONetConfig.LogReparentDiagnostics)
                    {
                        GONetLog.Debug($"[REPARENT-GUARD] CORRECTED position drift for '{gnp.name}' (GONetId {gnp.GONetId}): " +
                                      $"was {currentLocalPos}, corrected to {gnp.reparentGuardLocalPosition}, drift={posDist:F4}");
                    }
                }

                // Check if rotation has drifted
                Quaternion currentLocalRot = gnp.transform.localRotation;
                float rotDot = Quaternion.Dot(currentLocalRot, gnp.reparentGuardLocalRotation);

                if (rotDot < ROTATION_DOT_TOLERANCE)
                {
                    // Rotation drifted - correct it
                    gnp.transform.localRotation = gnp.reparentGuardLocalRotation;

                    if (GONetConfig.LogReparentDiagnostics)
                    {
                        GONetLog.Debug($"[REPARENT-GUARD] CORRECTED rotation drift for '{gnp.name}' (GONetId {gnp.GONetId}): " +
                                      $"was {currentLocalRot.eulerAngles}, corrected to {gnp.reparentGuardLocalRotation.eulerAngles}, dot={rotDot:F6}");
                    }
                }
            }
        }

        /// <summary>
        /// Resets reparenting state for Fast Iteration Mode.
        /// CRITICAL: Must be called when event subscriptions are cleared to ensure
        /// InitReparentingSupport() will re-subscribe handlers in the next session.
        /// </summary>
        internal static void ResetReparentingStateForNewSession()
        {
            // Clear the subscription flag so InitReparentingSupport() will re-subscribe
            isReparentEventHandlerSubscribed = false;

            // Clear rate limit tracking from previous session
            reparentRateLimits.Clear();

            GONetLog.Debug("[REPARENT] Reset for new session - cleared subscription flag and rate limits.");
        }

        #endregion
    }
}
