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

using System.Collections.Generic;

namespace GONet
{
    public static partial class GONetMain
    {
        #region Animator Trigger Sync Support

        /// <summary>
        /// Queue of pending AnimatorTriggerFiredEvent events waiting for their target GONetParticipant to spawn.
        /// Key: GONetId, Value: List of (TriggerNameHash, OccurredAtElapsedTicks)
        /// </summary>
        private static readonly Dictionary<uint, List<(int TriggerNameHash, long OccurredAtElapsedTicks)>> pendingAnimatorTriggerEvents
            = new Dictionary<uint, List<(int, long)>>();

        private static bool isAnimatorTriggerEventHandlerSubscribed = false;

        /// <summary>
        /// Initialize animator trigger event subscriptions. Called during GONet initialization.
        /// </summary>
        internal static void InitAnimatorTriggerSupport()
        {
            if (isAnimatorTriggerEventHandlerSubscribed) return;

            // Subscribe to AnimatorTriggerFiredEvent from remote sources only
            // Local triggers are applied immediately by SetAnimatorTrigger
            EventBus.Subscribe<AnimatorTriggerFiredEvent>(OnAnimatorTriggerFiredEvent_Remote, envelope => envelope.IsSourceRemote);

            isAnimatorTriggerEventHandlerSubscribed = true;

            if (GONetConfig.LogAnimatorTriggerDiagnostics)
            {
                GONetLog.Debug("[ANIMATOR-TRIGGER] Animator trigger event handler subscribed");
            }
        }

        /// <summary>
        /// Handler for remote AnimatorTriggerFiredEvent.
        /// Applies the trigger to the local Animator on non-authority machines.
        /// </summary>
        private static void OnAnimatorTriggerFiredEvent_Remote(GONetEventEnvelope<AnimatorTriggerFiredEvent> eventEnvelope)
        {
            AnimatorTriggerFiredEvent triggerEvent = eventEnvelope.Event;

            if (GONetConfig.LogAnimatorTriggerDiagnostics)
            {
                GONetLog.Debug($"[ANIMATOR-TRIGGER] Received remote AnimatorTriggerFiredEvent - GONetId:{triggerEvent.GONetId} TriggerHash:{triggerEvent.TriggerNameHash} SourceAuth:{triggerEvent.SourceAuthorityId}");
            }

            // Try to find the target GONetParticipant
            GONetParticipant target = GetGONetParticipantById(triggerEvent.GONetId);

            if (target != null)
            {
                // Skip if this is our own object (authority already applied locally)
                if (IsMine(target))
                {
                    if (GONetConfig.LogAnimatorTriggerDiagnostics)
                    {
                        GONetLog.Debug($"[ANIMATOR-TRIGGER] Skipping trigger for owned object '{target.name}' (GONetId {target.GONetId})");
                    }
                    return;
                }

                // Apply the trigger on non-authority
                target.ApplyNetworkedTrigger(triggerEvent.TriggerNameHash);
            }
            else
            {
                // Target not yet spawned - queue the trigger for later processing
                QueuePendingAnimatorTrigger(triggerEvent.GONetId, triggerEvent.TriggerNameHash, triggerEvent.OccurredAtElapsedTicks);

                if (GONetConfig.LogAnimatorTriggerDiagnostics)
                {
                    GONetLog.Debug($"[ANIMATOR-TRIGGER] Queued trigger for unspawned GONetId:{triggerEvent.GONetId} TriggerHash:{triggerEvent.TriggerNameHash}");
                }
            }
        }

        /// <summary>
        /// Queue a pending animator trigger for a target that hasn't spawned yet.
        /// </summary>
        private static void QueuePendingAnimatorTrigger(uint gonetId, int triggerNameHash, long occurredAtElapsedTicks)
        {
            if (!pendingAnimatorTriggerEvents.TryGetValue(gonetId, out var pendingList))
            {
                pendingList = new List<(int, long)>();
                pendingAnimatorTriggerEvents[gonetId] = pendingList;
            }

            // Add to pending list (will be processed when target spawns)
            pendingList.Add((triggerNameHash, occurredAtElapsedTicks));
        }

        /// <summary>
        /// Process any pending animator trigger events for a newly spawned GONetParticipant.
        /// Called from OnStartedGNPEvent or similar spawn completion handlers.
        /// </summary>
        internal static void ProcessPendingAnimatorTriggers(GONetParticipant participant)
        {
            if (participant == null || participant.GONetId == GONetParticipant.GONetId_Unset)
                return;

            if (pendingAnimatorTriggerEvents.TryGetValue(participant.GONetId, out var pendingList))
            {
                // Remove from pending map first
                pendingAnimatorTriggerEvents.Remove(participant.GONetId);

                // Skip if this is our own object (authority already applied locally)
                if (IsMine(participant))
                {
                    if (GONetConfig.LogAnimatorTriggerDiagnostics)
                    {
                        GONetLog.Debug($"[ANIMATOR-TRIGGER] Discarding {pendingList.Count} queued triggers for owned object '{participant.name}' (GONetId {participant.GONetId})");
                    }
                    return;
                }

                // Apply all pending triggers
                foreach (var (triggerNameHash, occurredAt) in pendingList)
                {
                    participant.ApplyNetworkedTrigger(triggerNameHash);

                    if (GONetConfig.LogAnimatorTriggerDiagnostics)
                    {
                        GONetLog.Debug($"[ANIMATOR-TRIGGER] Applied queued trigger to '{participant.name}' (GONetId {participant.GONetId}) TriggerHash:{triggerNameHash}");
                    }
                }
            }
        }

        /// <summary>
        /// Clears any pending animator trigger events for a despawned object.
        /// Called from OnDespawnGNPEvent_Remote or similar despawn handlers.
        /// </summary>
        internal static void ClearPendingAnimatorTriggers(uint gonetId)
        {
            if (pendingAnimatorTriggerEvents.Remove(gonetId))
            {
                if (GONetConfig.LogAnimatorTriggerDiagnostics)
                {
                    GONetLog.Debug($"[ANIMATOR-TRIGGER] Cleared pending triggers for despawned GONetId:{gonetId}");
                }
            }
        }

        /// <summary>
        /// Resets animator trigger state for Fast Iteration Mode.
        /// CRITICAL: Must be called when event subscriptions are cleared to ensure
        /// InitAnimatorTriggerSupport() will re-subscribe handlers in the next session.
        /// </summary>
        internal static void ResetAnimatorTriggerStateForNewSession()
        {
            // Clear the subscription flag so InitAnimatorTriggerSupport() will re-subscribe
            isAnimatorTriggerEventHandlerSubscribed = false;

            // Clear any pending triggers from the previous session
            int pendingCount = pendingAnimatorTriggerEvents.Count;
            pendingAnimatorTriggerEvents.Clear();

            GONetLog.Debug($"[ANIMATOR-TRIGGER] Reset for new session - cleared subscription flag and {pendingCount} pending trigger entries.");
        }

        #endregion
    }
}
