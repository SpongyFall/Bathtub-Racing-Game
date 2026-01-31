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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace GONet
{
    /// <summary>
    /// One of these is automatically spawned for each machine in the network game during GONet initialization.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    [RequireComponent(typeof(GONetParticipant))]
    [RequireComponent(typeof(GONetSessionContext))] // NOTE: requiring GONetSessionContext will thereby get the DontDestroyOnLoad behavior
    public class GONetLocal : GONetParticipantCompanionBehaviour, IEnumerable<KeyValuePair<ushort, GONetLocal>>
    {
        private static readonly Dictionary<ushort, GONetLocal> localsByAuthorityId = new Dictionary<ushort, GONetLocal>(1024);

        /// <summary>
        /// High water mark: the highest client authority ID ever seen this session.
        /// This value only increases, never decreases - even when clients disconnect.
        /// Used during failover promotion to initialize the server's authority ID counter.
        /// </summary>
        private static ushort highestClientAuthorityIdEverSeen = 0;

        private static GONetLocal lookupByAuthorityId;
        /// <summary>
        /// This is only to be used as a means by which to statically use the indexer in order to look up the instance of this class that "belongs to" the authority id passed in as the index.
        /// </summary>
        public static GONetLocal LookupByAuthorityId => lookupByAuthorityId;

        public GONetLocal this[ushort authorityId]
        {
            get
            {
                GONetLocal local;
                if (localsByAuthorityId.TryGetValue(authorityId, out local))
                {
                    Dictionary<ushort, GONetLocal>.Enumerator enumerator = localsByAuthorityId.GetEnumerator();
                    return local;
                }
                return null;
            }
        }

        /// <summary>
        /// This will enumerator over all <see cref="GONetLocal"/> instances in the game (i.e., from server and each client connected).
        /// </summary>
        public static IEnumerator<KeyValuePair<ushort, GONetLocal>> GetEnumerator_AllGONetLocals()
        {
            return LookupByAuthorityId.GetEnumerator();
        }

        #region IEnumerable impl

        /// <summary>
        /// This will enumerator over all <see cref="GONetLocal"/> instances in the game (i.e., from server and each client connected).
        /// </summary>
        public IEnumerator<KeyValuePair<ushort, GONetLocal>> GetEnumerator()
        {
            return localsByAuthorityId.GetEnumerator();
        }

        /// <summary>
        /// This will enumerator over all <see cref="GONetLocal"/> instances in the game (i.e., from server and each client connected).
        /// </summary>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return localsByAuthorityId.GetEnumerator();
        }

        #endregion

        /// <summary>
        /// Returns all authority IDs currently registered in the lookup (for diagnostics).
        /// </summary>
        public ushort[] GetAllKeys()
        {
            var keys = new ushort[localsByAuthorityId.Count];
            localsByAuthorityId.Keys.CopyTo(keys, 0);
            return keys;
        }

        #region Failover Promotion Support

        /// <summary>
        /// Called during failover self-promotion to add the server authority ID (1023) to the lookup.
        /// This allows IsGONetReady() to return true for server-owned objects after a client promotes itself.
        /// The promoting client's GONetLocal is registered under both its original authority ID and the new server authority ID.
        /// </summary>
        /// <param name="myGONetLocal">The promoting client's GONetLocal instance</param>
        /// <param name="newServerAuthorityId">The server authority ID (typically 1023)</param>
        internal static void AddServerAuthorityMapping(GONetLocal myGONetLocal, ushort newServerAuthorityId)
        {
            if (myGONetLocal == null)
            {
                GONetLog.Warning("[GONetLocal] AddServerAuthorityMapping called with null GONetLocal");
                return;
            }

            // Add mapping for server authority ID pointing to this client's GONetLocal
            // This ensures IsGONetReady() returns true for objects with OwnerAuthorityId = 1023
            if (!localsByAuthorityId.ContainsKey(newServerAuthorityId))
            {
                localsByAuthorityId[newServerAuthorityId] = myGONetLocal;
                GONetLog.Info($"[GONetLocal] Added server authority mapping: {newServerAuthorityId} -> GONetLocal (original authority: {myGONetLocal.OwnerAuthorityId})");
            }
            else
            {
                GONetLog.Info($"[GONetLocal] Server authority mapping already exists for {newServerAuthorityId}, updating reference");
                localsByAuthorityId[newServerAuthorityId] = myGONetLocal;
            }
        }

        /// <summary>
        /// Removes the temporary server authority mapping added during failover self-promotion.
        /// Safe to call even if the mapping does not exist or points to a different instance.
        /// </summary>
        internal static void RemoveServerAuthorityMapping(GONetLocal myGONetLocal, ushort serverAuthorityId)
        {
            if (myGONetLocal == null)
            {
                GONetLog.Warning("[GONetLocal] RemoveServerAuthorityMapping called with null GONetLocal");
                return;
            }

            if (localsByAuthorityId.TryGetValue(serverAuthorityId, out GONetLocal mapped) && ReferenceEquals(mapped, myGONetLocal))
            {
                localsByAuthorityId.Remove(serverAuthorityId);
                GONetLog.Info($"[GONetLocal] Removed server authority mapping: {serverAuthorityId} -> GONetLocal (original authority: {myGONetLocal.OwnerAuthorityId})");
            }
        }

        /// <summary>
        /// Updates the authority lookup mapping when a GONetLocal changes ownership.
        /// </summary>
        internal static void UpdateAuthorityMapping(GONetLocal local, ushort previousAuthorityId, ushort newAuthorityId)
        {
            if (local == null)
            {
                GONetLog.Warning("[GONetLocal] UpdateAuthorityMapping called with null GONetLocal");
                return;
            }

            if (previousAuthorityId != GONetMain.OwnerAuthorityId_Unset &&
                localsByAuthorityId.TryGetValue(previousAuthorityId, out GONetLocal mapped) &&
                ReferenceEquals(mapped, local))
            {
                localsByAuthorityId.Remove(previousAuthorityId);
            }

            if (newAuthorityId != GONetMain.OwnerAuthorityId_Unset)
            {
                localsByAuthorityId[newAuthorityId] = local;

                if (newAuthorityId != GONetMain.OwnerAuthorityId_Server && newAuthorityId > highestClientAuthorityIdEverSeen)
                {
                    highestClientAuthorityIdEverSeen = newAuthorityId;
                    GONetLog.Debug($"[GONetLocal] Updated highestClientAuthorityIdEverSeen to {highestClientAuthorityIdEverSeen}");
                }
            }
        }

        /// <summary>
        /// Gets the highest client authority ID ever seen this session (high water mark).
        /// Used during failover promotion to initialize the server's authority ID counter.
        /// This value never decreases, even when clients disconnect, ensuring no ID collisions.
        /// </summary>
        /// <returns>Highest client authority ID ever seen, or 0 if no clients have connected</returns>
        internal static ushort GetHighestKnownClientAuthorityId()
        {
            return highestClientAuthorityIdEverSeen;
        }

        #endregion

        #region Session Reset Support

        /// <summary>
        /// Clears all static state for session reset (Fast Iteration Mode or lobby flow).
        /// Called by GONetMain.ResetForNewSession() to prevent stale authority lookups.
        /// NOTE: highestClientAuthorityIdEverSeen is intentionally preserved for failover scenarios.
        /// </summary>
        internal static void ClearStaticState()
        {
            localsByAuthorityId.Clear();
            lookupByAuthorityId = null;
            // NOTE: highestClientAuthorityIdEverSeen is NOT reset - it's used for failover authority ID initialization
            // and should persist across sessions within the same process lifetime
            GONetLog.Debug("[GONetLocal] Static state cleared for session reset.");
        }

        #endregion

        public ushort OwnerAuthorityId => gonetParticipant.OwnerAuthorityId;

        private readonly List<GONetParticipant> myEnabledGONetParticipants = new List<GONetParticipant>(200);
        /// <summary>
        /// <para>A convenient collection of all the <see cref="GONetParticipant"/> instances that are currently enabled and share the same <see cref="GONetParticipant.OwnerAuthorityId"/> value as <see cref="gonetParticipant"/>.</para>
        /// <para>Elements are added here once Start() was called on the <see cref="GONetParticipant"/> and removed once OnDisable() is called.</para>
        /// <para>Do NOT attempt to modify this collection as to avoid creating issues for yourself/others.</para>
        /// </summary>
        public IEnumerable<GONetParticipant> MyEnabledGONetParticipants => myEnabledGONetParticipants;

        public GONetLocal()
        {
            if ((object)lookupByAuthorityId == null)
            {
                lookupByAuthorityId = this;
            }
        }

        protected override void Awake()
        {
            base.Awake();

            myEnabledGONetParticipants.Clear();

            foreach (GONetParticipant gnp in GameObject.FindObjectsOfType<GONetParticipant>()) // since GONetLocal is spawned in at runtime (unlike GONetGlobal), go ahead and add all the ones that are present now
            {
                AddIfAppropriate(gnp);
            }

            GONetMain.EventBus.Subscribe(SyncEvent_GeneratedTypes.SyncEvent_GONetParticipant_OwnerAuthorityId, OnGNPAuthorityChanged_CheckIfStilllMine);

            StartCoroutine(AddToLookupOnceAuthorityIdKnown(this));
        }

        private IEnumerator AddToLookupOnceAuthorityIdKnown(GONetLocal gonetLocal)
        {
            while (gonetLocal.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Unset)
            {
                yield return null;
            }

            localsByAuthorityId[gonetLocal.OwnerAuthorityId] = gonetLocal;

            GONetMain.TryMapServerAuthorityToHostLocal(GONetMain.CurrentHostIdentity.HostAuthorityId);

            // Track high water mark for failover authority ID initialization
            ushort authorityId = gonetLocal.OwnerAuthorityId;
            if (authorityId != GONetMain.OwnerAuthorityId_Server && authorityId > highestClientAuthorityIdEverSeen)
            {
                highestClientAuthorityIdEverSeen = authorityId;
                GONetLog.Debug($"[GONetLocal] Updated highestClientAuthorityIdEverSeen to {highestClientAuthorityIdEverSeen}");
            }

            // CRITICAL: Now that GONetLocal is in the lookup, check ALL IsMine participants that are ready
            // This includes GONetLocal itself AND scene-defined participants like GONetGlobal that may have
            // started before GONetLocal was ready
            GONetLog.Info($"[GONetLocal] GONetLocal added to lookup (OwnerAuthorityId: {gonetLocal.OwnerAuthorityId}), checking all IsMine participants for readiness");

            // Check GONetLocal itself - publish for ALL (IsMine AND remote) once added to lookup
            // This ensures Client #1 sees OnGONetReady when Client #2's GONetLocal is added to lookup
            if (GONetMain.IsGONetReady(gonetLocal.GONetParticipant))
            {
                // Deduplication check: Only publish if not already published
                if (GONetMain.TryMarkDeserializeInitPublished(gonetLocal.GONetParticipant.GONetId))
                {
                    GONetLog.Info($"[GONetLocal] GONetLocal is ready (IsMine: {gonetLocal.GONetParticipant.IsMine}), publishing DeserializeInitAllCompleted");
                    var deserializeInitEvent = new GONetParticipantDeserializeInitAllCompletedEvent(gonetLocal.GONetParticipant);
                    GONetMain.EventBus.Publish<IGONetEvent>(deserializeInitEvent);
                    GONetMain.IncrementDeserializeInitEventCounter(); // DIAGNOSTIC: Track event publication rate
                }
                else
                {
                    GONetLog.Info($"[GONetLocal] Skipping duplicate DeserializeInitAllCompleted for GONetLocal (GONetId: {gonetLocal.GONetParticipant.GONetId}) - already published from another path");
                }
            }

            // Check all other IsMine participants (like GONet_GlobalContext) that may now be ready
            // IMPORTANT: Only publish for IsMine participants - remote participants get this event from deserialization path
            foreach (var gnp in gonetLocal.myEnabledGONetParticipants)
            {
                if (gnp != gonetLocal.GONetParticipant && gnp.IsMine && GONetMain.IsGONetReady(gnp))
                {
                    // Deduplication check: Only publish if not already published
                    if (GONetMain.TryMarkDeserializeInitPublished(gnp.GONetId))
                    {
                        GONetLog.Info($"[GONetLocal] Participant '{gnp.name}' (GONetId: {gnp.GONetId}) is now ready after GONetLocal became available, publishing DeserializeInitAllCompleted");
                        var deserializeInitEvent = new GONetParticipantDeserializeInitAllCompletedEvent(gnp);
                        GONetMain.EventBus.Publish<IGONetEvent>(deserializeInitEvent);
                        GONetMain.IncrementDeserializeInitEventCounter(); // DIAGNOSTIC: Track event publication rate
                    }
                    else
                    {
                        GONetLog.Info($"[GONetLocal] Skipping duplicate DeserializeInitAllCompleted for '{gnp.name}' (GONetId: {gnp.GONetId}) - already published from another path");
                    }
                }
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            // CRITICAL FIX (Dec 2025): Only remove if the mapping still points to THIS instance.
            // During failover, AddServerAuthorityMapping may have updated the mapping for authority 1023
            // to point to the promoted client's GONetLocal. When the OLD server's GONetLocal is
            // destroyed, we must NOT remove the mapping if it now points to a different instance.
            // Without this check, the promoted host's server-owned objects fail IsGONetReady because
            // "GONetLocal not found for OwnerAuthorityId=1023".
            if (localsByAuthorityId.TryGetValue(OwnerAuthorityId, out GONetLocal mapped) && ReferenceEquals(mapped, this))
            {
                localsByAuthorityId.Remove(OwnerAuthorityId);
            }
        }

        public override void OnGONetParticipantEnabled(GONetParticipant gonetParticipant)
        {
            base.OnGONetParticipantEnabled(gonetParticipant);

            AddIfAppropriate(gonetParticipant);
        }

        public override void OnGONetParticipantStarted(GONetParticipant gonetParticipant)
        {
            base.OnGONetParticipantStarted(gonetParticipant);

            AddIfAppropriate(gonetParticipant);
        }

        private void AddIfAppropriate(GONetParticipant gonetParticipant)
        {
            if (IsRelatedToThisLocality(gonetParticipant) &&
                !myEnabledGONetParticipants.Contains(gonetParticipant)) // may have already been added elsewhere
            {
                myEnabledGONetParticipants.Add(gonetParticipant);

                // Path 5: If GONetLocal is already in lookup (server/client running for a while),
                // publish DeserializeInitAllCompleted for runtime-spawned IsMine participants
                // This handles the case where participants spawn AFTER GONetLocal's AddToLookupOnceAuthorityIdKnown has completed
                if (localsByAuthorityId.ContainsKey(OwnerAuthorityId) && // GONetLocal is in lookup
                    gonetParticipant != this.gonetParticipant && // Not GONetLocal itself (handled by Path 3)
                    GONetMain.IsGONetReady(gonetParticipant)) // Has GONetId and GONetLocal in lookup
                {
                    // Deduplication check: Only publish if not already published
                    if (GONetMain.TryMarkDeserializeInitPublished(gonetParticipant.GONetId))
                    {
                        GONetLog.Info($"[GONetLocal] Runtime-spawned participant '{gonetParticipant.name}' (GONetId: {gonetParticipant.GONetId}) is ready, publishing DeserializeInitAllCompleted");
                        var deserializeInitEvent = new GONetParticipantDeserializeInitAllCompletedEvent(gonetParticipant);
                        GONetMain.EventBus.Publish<IGONetEvent>(deserializeInitEvent);
                        GONetMain.IncrementDeserializeInitEventCounter(); // DIAGNOSTIC: Track event publication rate
                    }
                    else
                    {
                        GONetLog.Info($"[GONetLocal] Skipping duplicate DeserializeInitAllCompleted for '{gonetParticipant.name}' (GONetId: {gonetParticipant.GONetId}) - already published from another path");
                    }
                }
            }
        }

        public override void OnGONetParticipantDisabled(GONetParticipant gonetParticipant)
        {
            myEnabledGONetParticipants.Remove(gonetParticipant); // regardless of whether or not it was present before this call, it will not be present afterward
        }

        private void LateUpdate()
        {
            GONetMain.Update_DoTheHeavyLifting_IfAppropriate(this, true);
        }

        /// <summary>
        /// PRE: <paramref name="someGNP"/> known to not be null!
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsRelatedToThisLocality(GONetParticipant someGNP)
        {
            return someGNP.OwnerAuthorityId == gonetParticipant.OwnerAuthorityId;
        }

        private void OnGNPAuthorityChanged_CheckIfStilllMine(GONetEventEnvelope<SyncEvent_ValueChangeProcessed> eventEnvelope)
        {
            if ((object)eventEnvelope.GONetParticipant != null && // not sure why this would be the case there, but have to double check..no likie the null
                IsRelatedToThisLocality(eventEnvelope.GONetParticipant))
            {
                // since we have a list and not a hashset, we need to double check we do not already have this stored
                if (!myEnabledGONetParticipants.Contains(eventEnvelope.GONetParticipant))
                {
                    myEnabledGONetParticipants.Add(eventEnvelope.GONetParticipant);
                }
            }
            else
            {
                myEnabledGONetParticipants.Remove(eventEnvelope.GONetParticipant);
            }
        }
    }
}
