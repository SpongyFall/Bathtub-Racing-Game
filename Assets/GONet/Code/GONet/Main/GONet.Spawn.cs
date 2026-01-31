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
        #region GONetId Reuse Prevention (TTL Tracking)

        /// <summary>
        /// Tracks GONetIds that were recently despawned with their despawn timestamp.
        /// Prevents GONetId reuse while despawn messages are still in flight across the network.
        ///
        /// KEY: GONetId (composed value with authority)
        /// VALUE: Despawn time in seconds (GONetMain.Time.ElapsedSeconds)
        ///
        /// TTL is configured via GONetGlobal.gonetIdReuseDelaySeconds (default: 5 seconds).
        /// IDs are removed from this map after TTL expires during periodic cleanup.
        /// </summary>
        private static readonly Dictionary<uint, double> recentlyDespawnedGONetIds = new Dictionary<uint, double>(200);

        /// <summary>
        /// Tracks last cleanup time for <see cref="recentlyDespawnedGONetIds"/>.
        /// Cleanup runs periodically (every 30 seconds) to remove expired entries.
        /// </summary>
        private static double? _lastGONetIdReuseCleanupTime;


        /// <summary>
        /// Count of unreliable packets dropped since last log message.
        /// Resets to 0 after logging (every 100 drops).
        /// </summary>
        private static int _unreliablePacketDropCount_sinceLastLog = 0;

        /// <summary>
        /// Total count of successful packet sends (for calculating drop rate).
        /// Incremented in SendBytesToRemoteConnection when packets are successfully queued.
        /// </summary>
        private static long _successfulPacketSendCount = 0;

        #region CLIENT LIMBO MODE SUPPORT

        /// <summary>
        /// CLIENT ONLY: Queue of GONetParticipants that were spawned in limbo state (no GONetId batch available).
        /// These will be "graduated" to full networked status when a new batch arrives from server.
        /// </summary>
        private static readonly Queue<GONetParticipant> client_deferredSpawnsAwaitingBatch = new Queue<GONetParticipant>();

        /// <summary>
        /// CLIENT ONLY: Event raised when a spawn enters limbo state due to batch exhaustion.
        /// Subscribe to this to implement custom UI notifications (e.g., "Out of spawn capacity").
        /// </summary>
        public static event Action<Client_SpawnLimboEventArgs> Client_OnSpawnEnteredLimbo;

        /// <summary>
        /// CLIENT ONLY: Gets a read-only collection of participants currently in limbo state.
        /// For use by editor inspectors and debugging tools.
        /// </summary>
        public static IEnumerable<GONetParticipant> Client_GetLimboParticipants()
        {
            return client_deferredSpawnsAwaitingBatch;
        }

        /// <summary>
        /// CLIENT ONLY: Gets the count of participants currently in limbo state.
        /// </summary>
        public static int Client_GetLimboCount()
        {
            return client_deferredSpawnsAwaitingBatch.Count;
        }

        /// <summary>
        /// CLIENT ONLY: Instantiates an object in limbo state (no GONetId assigned).
        /// Object will be queued for graduation when batch arrives.
        /// </summary>
        private static GONetParticipant Client_InstantiateInLimbo(
            GONetParticipant prefab,
            Vector3 position,
            Quaternion rotation,
            Client_GONetIdBatchLimboMode limboMode)
        {
            GONetParticipant instance;

            switch (limboMode)
            {
                case Client_GONetIdBatchLimboMode.InstantiateInLimboWithAutoDisableAll:
                    instance = Client_InstantiateInLimbo_DisableAll(prefab, position, rotation);
                    break;

                case Client_GONetIdBatchLimboMode.InstantiateInLimboWithAutoDisableRenderingAndPhysics:
                    instance = Client_InstantiateInLimbo_DisableRenderingAndPhysics(prefab, position, rotation);
                    break;

                case Client_GONetIdBatchLimboMode.InstantiateInLimbo:
                    instance = Client_InstantiateInLimbo_NoDisable(prefab, position, rotation);
                    break;

                default:
                    GONetLog.Error($"[ClientLimbo] Unknown limbo mode: {limboMode}");
                    return null;
            }

            // Mark as in limbo and add to deferred queue
            instance.client_isInLimbo = true;
            instance.RemotelyControlledByAuthorityId = MyAuthorityId;
            client_deferredSpawnsAwaitingBatch.Enqueue(instance);

            uint remainingIds = GONetIdBatchManager.Client_GetRemainingIds();
            GONetLog.Warning($"[ClientLimbo] Spawned '{prefab.name}' in LIMBO mode {limboMode} (remaining IDs: {remainingIds}, limbo queue size: {client_deferredSpawnsAwaitingBatch.Count})");

            // Raise event
            Client_OnSpawnEnteredLimbo?.Invoke(new Client_SpawnLimboEventArgs
            {
                Participant = instance,
                Prefab = prefab,
                LimboMode = limboMode,
                RemainingIds = remainingIds,
                Position = position,
                Rotation = rotation
            });

            return instance;
        }

        /// <summary>
        /// CLIENT ONLY: Limbo Mode 1 - Disable ALL MonoBehaviours (except GONetParticipant).
        /// Object is completely frozen until batch arrives.
        /// </summary>
        private static GONetParticipant Client_InstantiateInLimbo_DisableAll(
            GONetParticipant prefab,
            Vector3 position,
            Quaternion rotation)
        {
            // Instantiate normally first
            GONetParticipant instance = UnityEngine.Object.Instantiate(prefab, position, rotation);

            // Copy design time metadata from prefab (same as Instantiate_MarkToBeRemotelyControlled)
            DesignTimeMetadata prefabMetadata = GONetSpawnSupport_Runtime.GetDesignTimeMetadata(prefab, force: true);
            if (prefabMetadata != null && !string.IsNullOrWhiteSpace(prefabMetadata.Location))
            {
                DesignTimeMetadata instanceMetadata = new DesignTimeMetadata
                {
                    Location = prefabMetadata.Location,
                    CodeGenerationId = prefabMetadata.CodeGenerationId,
                    UnityGuid = prefabMetadata.UnityGuid
                };
                GONetSpawnSupport_Runtime.SetDesignTimeMetadata(instance, instanceMetadata);
            }

            // Disable all MonoBehaviours except GONetParticipant
            instance.client_limboDisabledComponents = new List<MonoBehaviour>();
            MonoBehaviour[] allComponents = instance.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
            foreach (MonoBehaviour component in allComponents)
            {
                if (component != null && !(component is GONetParticipant) && component.enabled)
                {
                    component.enabled = false;
                    instance.client_limboDisabledComponents.Add(component);
                }
            }

            GONetLog.Info($"[ClientLimbo] DisableAll mode: Disabled {instance.client_limboDisabledComponents.Count} components on '{instance.name}'");
            return instance;
        }

        /// <summary>
        /// CLIENT ONLY: Limbo Mode 2 - Disable ONLY rendering and physics components.
        /// MonoBehaviours still run (Start/Update) but object is invisible/non-physical.
        /// RECOMMENDED DEFAULT: Good balance of safety and flexibility.
        /// </summary>
        private static GONetParticipant Client_InstantiateInLimbo_DisableRenderingAndPhysics(
            GONetParticipant prefab,
            Vector3 position,
            Quaternion rotation)
        {
            // Instantiate normally first
            GONetParticipant instance = UnityEngine.Object.Instantiate(prefab, position, rotation);

            // Copy design time metadata from prefab
            DesignTimeMetadata prefabMetadata = GONetSpawnSupport_Runtime.GetDesignTimeMetadata(prefab, force: true);
            if (prefabMetadata != null && !string.IsNullOrWhiteSpace(prefabMetadata.Location))
            {
                DesignTimeMetadata instanceMetadata = new DesignTimeMetadata
                {
                    Location = prefabMetadata.Location,
                    CodeGenerationId = prefabMetadata.CodeGenerationId,
                    UnityGuid = prefabMetadata.UnityGuid
                };
                GONetSpawnSupport_Runtime.SetDesignTimeMetadata(instance, instanceMetadata);
            }

            // Disable rendering components
            instance.client_limboDisabledRenderers = new List<Renderer>();
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(includeInactive: true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer != null && renderer.enabled)
                {
                    renderer.enabled = false;
                    instance.client_limboDisabledRenderers.Add(renderer);
                }
            }

            // Disable 3D colliders
            instance.client_limboDisabledColliders = new List<Collider>();
            Collider[] colliders = instance.GetComponentsInChildren<Collider>(includeInactive: true);
            foreach (Collider collider in colliders)
            {
                if (collider != null && collider.enabled)
                {
                    collider.enabled = false;
                    instance.client_limboDisabledColliders.Add(collider);
                }
            }

            // Disable 2D colliders
            instance.client_limboDisabledColliders2D = new List<Collider2D>();
            Collider2D[] colliders2D = instance.GetComponentsInChildren<Collider2D>(includeInactive: true);
            foreach (Collider2D collider in colliders2D)
            {
                if (collider != null && collider.enabled)
                {
                    collider.enabled = false;
                    instance.client_limboDisabledColliders2D.Add(collider);
                }
            }

            // Make Rigidbody kinematic (if present)
            instance.client_limboRigidbody = instance.GetComponentInChildren<Rigidbody>();
            if (instance.client_limboRigidbody != null)
            {
                instance.client_limboRigidbodyWasKinematic = instance.client_limboRigidbody.isKinematic;
                instance.client_limboRigidbody.isKinematic = true;
            }

            // Make Rigidbody2D kinematic (if present)
            instance.client_limboRigidbody2D = instance.GetComponentInChildren<Rigidbody2D>();
            if (instance.client_limboRigidbody2D != null)
            {
                instance.client_limboRigidbody2DOriginalType = instance.client_limboRigidbody2D.bodyType;
                instance.client_limboRigidbody2D.bodyType = RigidbodyType2D.Kinematic;
            }

            GONetLog.Info($"[ClientLimbo] DisableRenderingAndPhysics mode: Disabled {instance.client_limboDisabledRenderers.Count} renderers, {instance.client_limboDisabledColliders.Count} colliders, {instance.client_limboDisabledColliders2D.Count} colliders2D on '{instance.name}'");
            return instance;
        }

        /// <summary>
        /// CLIENT ONLY: Limbo Mode 3 - No automatic disabling.
        /// Object runs normally, user must check Client_IsInLimbo themselves.
        /// </summary>
        private static GONetParticipant Client_InstantiateInLimbo_NoDisable(
            GONetParticipant prefab,
            Vector3 position,
            Quaternion rotation)
        {
            // Instantiate normally - no component disabling
            GONetParticipant instance = UnityEngine.Object.Instantiate(prefab, position, rotation);

            // Copy design time metadata from prefab
            DesignTimeMetadata prefabMetadata = GONetSpawnSupport_Runtime.GetDesignTimeMetadata(prefab, force: true);
            if (prefabMetadata != null && !string.IsNullOrWhiteSpace(prefabMetadata.Location))
            {
                DesignTimeMetadata instanceMetadata = new DesignTimeMetadata
                {
                    Location = prefabMetadata.Location,
                    CodeGenerationId = prefabMetadata.CodeGenerationId,
                    UnityGuid = prefabMetadata.UnityGuid
                };
                GONetSpawnSupport_Runtime.SetDesignTimeMetadata(instance, instanceMetadata);
            }

            GONetLog.Info($"[ClientLimbo] NoDisable mode: '{instance.name}' spawned normally, user must check Client_IsInLimbo");
            return instance;
        }

        /// <summary>
        /// CLIENT ONLY: Processes deferred spawns (limbo queue) when a new batch arrives.
        /// Called automatically by Client_AssignNewClientGONetIdRawBatch.
        /// Graduates limbo objects to full networked status by assigning GONetIds and re-enabling components.
        /// </summary>
        private static void Client_OnBatchReceived_ProcessDeferredSpawns()
        {
            if (client_deferredSpawnsAwaitingBatch.Count == 0)
            {
                return; // Nothing to process
            }

            int processedCount = 0;
            int failedCount = 0;

            GONetLog.Info($"[ClientLimbo] Processing {client_deferredSpawnsAwaitingBatch.Count} deferred spawns from limbo queue");

            // Process all limbo spawns that can be assigned IDs
            while (client_deferredSpawnsAwaitingBatch.Count > 0 && GONetIdBatchManager.Client_HasAvailableIds())
            {
                GONetParticipant participant = client_deferredSpawnsAwaitingBatch.Dequeue();

                if (participant == null || participant.gameObject == null)
                {
                    GONetLog.Warning($"[ClientLimbo] Skipping null/destroyed participant in limbo queue");
                    failedCount++;
                    continue;
                }

                // Exit limbo and assign GONetId
                bool success = Client_ExitLimbo(participant);
                if (success)
                {
                    processedCount++;
                }
                else
                {
                    failedCount++;
                }
            }

            uint remainingIds = GONetIdBatchManager.Client_GetRemainingIds();
            GONetLog.Info($"[ClientLimbo] Batch processing complete: {processedCount} graduated, {failedCount} failed, {client_deferredSpawnsAwaitingBatch.Count} still in limbo, {remainingIds} IDs remaining");
        }

        /// <summary>
        /// CLIENT ONLY: Exits limbo state for a participant.
        /// Re-enables disabled components and assigns GONetId from batch.
        /// </summary>
        private static bool Client_ExitLimbo(GONetParticipant participant)
        {
            if (participant == null || !participant.Client_IsInLimbo)
            {
                GONetLog.Warning($"[ClientLimbo] Cannot exit limbo - participant is null or not in limbo");
                return false;
            }

            string participantName = participant.gameObject.name;
            GONetLog.Info($"[ClientLimbo] Exiting limbo for '{participantName}'");

            // Re-enable components based on which mode was used
            if (participant.client_limboDisabledComponents != null && participant.client_limboDisabledComponents.Count > 0)
            {
                // Mode 1: DisableAll - Re-enable all MonoBehaviours
                foreach (MonoBehaviour component in participant.client_limboDisabledComponents)
                {
                    if (component != null)
                    {
                        component.enabled = true;
                    }
                }
                GONetLog.Info($"[ClientLimbo] Re-enabled {participant.client_limboDisabledComponents.Count} components on '{participantName}'");
                participant.client_limboDisabledComponents.Clear();
                participant.client_limboDisabledComponents = null;
            }
            else if (participant.client_limboDisabledRenderers != null)
            {
                // Mode 2: DisableRenderingAndPhysics - Re-enable rendering/physics
                foreach (Renderer renderer in participant.client_limboDisabledRenderers)
                {
                    if (renderer != null)
                    {
                        renderer.enabled = true;
                    }
                }

                foreach (Collider collider in participant.client_limboDisabledColliders)
                {
                    if (collider != null)
                    {
                        collider.enabled = true;
                    }
                }

                foreach (Collider2D collider in participant.client_limboDisabledColliders2D)
                {
                    if (collider != null)
                    {
                        collider.enabled = true;
                    }
                }

                if (participant.client_limboRigidbody != null)
                {
                    participant.client_limboRigidbody.isKinematic = participant.client_limboRigidbodyWasKinematic;

                    // Enable interpolation for smooth rendering if non-authority
                    if (!participant.IsMine)
                    {
                        participant.client_limboRigidbody.interpolation = RigidbodyInterpolation.Interpolate;
                    }
                }

                if (participant.client_limboRigidbody2D != null)
                {
                    participant.client_limboRigidbody2D.bodyType = participant.client_limboRigidbody2DOriginalType;

                    // Enable interpolation for smooth rendering if non-authority
                    if (!participant.IsMine)
                    {
                        participant.client_limboRigidbody2D.interpolation = RigidbodyInterpolation2D.Interpolate;
                    }
                }

                GONetLog.Info($"[ClientLimbo] Re-enabled rendering/physics on '{participantName}'");

                // Clear references
                participant.client_limboDisabledRenderers.Clear();
                participant.client_limboDisabledColliders.Clear();
                participant.client_limboDisabledColliders2D.Clear();
                participant.client_limboDisabledRenderers = null;
                participant.client_limboDisabledColliders = null;
                participant.client_limboDisabledColliders2D = null;
                participant.client_limboRigidbody = null;
                participant.client_limboRigidbody2D = null;
            }
            // Mode 3: NoDisable - nothing to re-enable

            // Assign GONetId from batch
            AssignGONetIdRaw_IfAppropriate(participant, shouldForceChangeEventIfAlreadySet: false);

            // Mark as no longer in limbo BEFORE triggering OnGONetReady
            participant.client_isInLimbo = false;

            // LIFECYCLE GATE: Graduated from limbo - check if OnGONetReady can fire
            // This replaces the old direct broadcast - now uses the centralized gate check
            GONetLog.Info($"[ClientLimbo] '{participantName}' graduated from limbo - GONetId: {participant.GONetId} - checking OnGONetReady gate");
            CheckAndPublishOnGONetReady_IfAllConditionsMet(participant);

            return true;
        }

        #region GONetId Reuse Prevention Methods

        /// <summary>
        /// Marks a GONetId as recently despawned, preventing immediate reuse.
        /// Called automatically from OnDisable_StopMonitoringForAutoMagicalNetworking.
        ///
        /// The GONetId will remain unavailable for reuse until the configured TTL expires
        /// (GONetGlobal.gonetIdReuseDelaySeconds, default 5 seconds).
        /// </summary>
        /// <param name="gonetId">The GONetId being despawned</param>
        internal static void MarkGONetIdDespawned(uint gonetId)
        {
            if (gonetId == GONetParticipant.GONetId_Unset)
            {
                return; // Don't track unset IDs
            }

            double despawnTime = Time.ElapsedSeconds;
            recentlyDespawnedGONetIds[gonetId] = despawnTime;

            //GONetLog.Debug($"[GONetId-Reuse] Marked GONetId {gonetId} as despawned at {despawnTime:F3}s (TTL: {GetGONetIdReuseDelay():F1}s)");
        }

        /// <summary>
        /// Checks if a GONetId can be safely reused (TTL has expired).
        /// Used by GONetIdBatchManager during ID allocation.
        /// </summary>
        /// <param name="gonetId">The GONetId to check</param>
        /// <returns>True if ID can be reused, false if still in cooldown period</returns>
        internal static bool CanReuseGONetId(uint gonetId)
        {
            if (!recentlyDespawnedGONetIds.TryGetValue(gonetId, out double despawnTime))
            {
                return true; // Not in recently despawned map, safe to reuse
            }

            double elapsed = Time.ElapsedSeconds - despawnTime;
            double reuseDelay = GetGONetIdReuseDelay();

            if (elapsed >= reuseDelay)
            {
                // TTL expired, remove from map and allow reuse
                recentlyDespawnedGONetIds.Remove(gonetId);
                GONetLog.Debug($"[GONetId-Reuse] GONetId {gonetId} TTL expired ({elapsed:F3}s >= {reuseDelay:F1}s), allowing reuse");
                return true;
            }

            // Still in cooldown period
            GONetLog.Warning($"[GONetId-Reuse] ⚠️  GONetId {gonetId} reuse prevented - TTL not expired ({elapsed:F3}s / {reuseDelay:F1}s remaining: {reuseDelay - elapsed:F3}s)");
            return false;
        }

        /// <summary>
        /// Gets the configured GONetId reuse delay from GONetGlobal.
        /// Falls back to 5 seconds if GONetGlobal not available.
        /// </summary>
        private static double GetGONetIdReuseDelay()
        {
            var gonetGlobal = GONetGlobal.Instance;
            if (gonetGlobal != null)
            {
                return gonetGlobal.gonetIdReuseDelaySeconds;
            }
            return 5.0; // Default fallback
        }

        /// <summary>
        /// Periodic cleanup of expired GONetIds from recentlyDespawnedGONetIds map.
        /// Runs every 30 seconds to prevent unbounded growth.
        /// Called from Update() main loop.
        /// </summary>
        internal static void CleanupExpiredDespawnedGONetIds()
        {
            const double CLEANUP_INTERVAL_SECONDS = 30.0;

            double now = Time.ElapsedSeconds;

            // Initialize or check cleanup interval
            if (!_lastGONetIdReuseCleanupTime.HasValue ||
                (now - _lastGONetIdReuseCleanupTime.Value) >= CLEANUP_INTERVAL_SECONDS)
            {
                _lastGONetIdReuseCleanupTime = now;

                if (recentlyDespawnedGONetIds.Count == 0)
                {
                    return; // Nothing to clean
                }

                double reuseDelay = GetGONetIdReuseDelay();
                List<uint> toRemove = new List<uint>(recentlyDespawnedGONetIds.Count);

                foreach (var kvp in recentlyDespawnedGONetIds)
                {
                    double elapsed = now - kvp.Value;
                    if (elapsed >= reuseDelay)
                    {
                        toRemove.Add(kvp.Key);
                    }
                }

                if (toRemove.Count > 0)
                {
                    foreach (uint id in toRemove)
                    {
                        recentlyDespawnedGONetIds.Remove(id);
                    }
                    GONetLog.Info($"[GONetId-Reuse] Cleaned up {toRemove.Count} expired despawned GONetIds (map size now: {recentlyDespawnedGONetIds.Count})");
                }
            }
        }


        /// <summary>
        /// Call me in the <paramref name="gonetParticipant"/>'s OnDisable method.
        /// </summary>
        internal static void OnDisable_StopMonitoringForAutoMagicalNetworking(GONetParticipant gonetParticipant)
        {
            if (Application.isPlaying && gonetParticipant.IsInternallyConfigured) // now that [ExecuteInEditMode] was added to GONetParticipant for OnDestroy, we have to guard this to only run in play
            {
                bool isPoolDestruction = gonetParticipant.isPooled && gonetParticipant.isPoolDestructionInProgress;

                if (gonetParticipant.isPooled && !isPoolDestruction)
                {
                    // Pooled lifecycle: keep auto-sync companions/maps intact to avoid missing-companion bundle aborts.
                    // We only tear down transient runtime state (SoA registration, deferred RPCs).
                    GONetEventBus.ClearDeferredRpcsForGONetId(gonetParticipant.GONetId);

                    if (gonetParticipant.v2_isRegisteredInSoA)
                    {
                        UnregisterObjectFromSoA(gonetParticipant);
                    }

                    return;
                }

                { // auto-magical sync related housekeeping
                    Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> autoSyncCompanions = activeAutoSyncCompanionsByCodeGenerationIdMap[gonetParticipant.CodeGenerationId];
                    GONetParticipant_AutoMagicalSyncCompanion_Generated syncCompanion;
                    if (!autoSyncCompanions.TryGetValue(gonetParticipant, out syncCompanion) || !autoSyncCompanions.Remove(gonetParticipant)) // NOTE: This is the only place where the inner dictionary is removed from and is ensured to run on unity main thread since OnDisable, so no need for concurrency as long as we can say the same about adds
                    {
                        const string PORK = "Expecting to find active auto-sync companion in order to de-active/remove it upon gonetParticipant.OnDisable, but did not. gonetParticipant.GONetId: ";
                        const string NAME = " gonetParticipant.gameObject.name: ";
                        GONetLog.Warning(string.Concat(PORK, gonetParticipant.GONetId, NAME, gonetParticipant.gameObject.name));
                    }
                    if (syncCompanion != null)
                    {
                        syncCompanion.Dispose();
                    }

                    if (activeAutoSyncCompanionsByCodeGenerationIdMap_uintKeyForPerformance.TryGetValue(gonetParticipant.CodeGenerationId, out var autoSyncCompanions_uintKeyForPerformance))
                    {
                        autoSyncCompanions_uintKeyForPerformance.Remove(gonetParticipant.GONetIdAtInstantiation);
                    }

                    gonetParticipant.DidStartMonitoringForAutoMagicalNetworking = false;
                }

                var disabledEvent = new GONetParticipantDisabledEvent(gonetParticipant);
                EventBus.Publish<IGONetEvent>(disabledEvent); // make sure this comes before gonetParticipantByGONetIdMap.Remove(gonetParticipant.GONetId); or else the GNP will not be found to attach to the envelope and the subscription handlers will not have what they are expecing

                // Remove from lookup maps
                bool removedFromGONetIdMap = gonetParticipantByGONetIdMap.Remove(gonetParticipant.GONetId);
                bool removedFromInstantiationMap = gonetParticipantByGONetIdAtInstantiationMap.Remove(gonetParticipant.GONetIdAtInstantiation);

                if (GONetConfig.LogParticipantMapDiagnostics)
                {
                    string parentPath = gonetParticipant.transform.parent == null
                        ? "WorldRoot"
                        : GONet.Utils.HierarchyUtils.GetFullUniquePath(gonetParticipant.transform.parent.gameObject);
                    GONetLog.Warning($"[PARTICIPANT-REMOVED] '{gonetParticipant.name}' " +
                                     $"GONetId={gonetParticipant.GONetId} InstantiationId={gonetParticipant.GONetIdAtInstantiation} " +
                                     $"RemovedFromGONetIdMap={removedFromGONetIdMap} RemovedFromInstantiationMap={removedFromInstantiationMap} " +
                                     $"ActiveInHierarchy={gonetParticipant.gameObject.activeInHierarchy} Enabled={gonetParticipant.enabled} " +
                                     $"Parent='{parentPath}' IsServer={IsServer} OwnerAuthorityId={gonetParticipant.OwnerAuthorityId}");
                }

                // Cleanup: Remove from deduplication tracking to allow GONetId reuse
                deserializeInitPublishedGONetIds.Remove(gonetParticipant.GONetId);

                // Cleanup: Clear any deferred RPCs for this GONetId to prevent infinite defer loops on GONetId reuse
                GONetEventBus.ClearDeferredRpcsForGONetId(gonetParticipant.GONetId);

                // GONet v2: Unregister from SoA blending streams if this was a non-authority object
                // CRITICAL FIX (December 2025): This was missing - objects were registered but never unregistered,
                // causing orphaned SoA entries that continue receiving blend updates but no DATA_IN.
                if (gonetParticipant.v2_isRegisteredInSoA)
                {
                    UnregisterObjectFromSoA(gonetParticipant);
                }

                // GONetId Reuse Prevention: Mark this GONetId as recently despawned to prevent immediate reuse
                if (!isPoolDestruction)
                {
                    MarkGONetIdDespawned(gonetParticipant.GONetId);
                }
            }
        }

        #endregion
        #endregion
        #endregion

    }
}
