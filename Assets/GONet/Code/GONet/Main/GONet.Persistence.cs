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
        internal class SyncEventsSaveSupport
        {
            internal readonly Queue<SyncEvent_ValueChangeProcessed> queue_needsSavingASAP = new Queue<SyncEvent_ValueChangeProcessed>(SYNC_EVENT_QUEUE_SAVE_WHEN_FULL_SIZE);
            internal readonly Queue<SyncEvent_ValueChangeProcessed> queue_needsSaving = new Queue<SyncEvent_ValueChangeProcessed>(SYNC_EVENT_QUEUE_SAVE_WHEN_FULL_SIZE);
            internal readonly ConcurrentQueue<SyncEvent_ValueChangeProcessed> queue_needsReturnToPool = new ConcurrentQueue<SyncEvent_ValueChangeProcessed>();

            internal int maxToReturnPerFrame = STARTING_MAX_SYNC_EVENTS_RETURN_PER_FRAME;
            internal volatile bool IsSaving;
            internal readonly AutoResetEvent IsSavingMutex = new AutoResetEvent(true);

            internal SyncEventsSaveSupport()
            {
                { // just ensure this data structure has enough internal memory stuffs now so no allocations and GC crap has to happen later!
                    for (int i = 0; i < SYNC_EVENT_QUEUE_SAVE_WHEN_FULL_SIZE; ++i)
                    {
                        var randomlySelectedType = new SyncEvent_Time_ElapsedTicks_SetFromAuthority();
                        queue_needsReturnToPool.Enqueue(randomlySelectedType);
                    }

                    SyncEvent_ValueChangeProcessed item;
                    for (int i = 0; i < SYNC_EVENT_QUEUE_SAVE_WHEN_FULL_SIZE; ++i)
                    {
                        queue_needsReturnToPool.TryDequeue(out item);
                    }
                }
            }

            /// <summary>
            /// Transfer queued ASAP items to save queue that will be processed in another thread.
            /// IMPORTANT: Call this from main Unity thread!
            /// POST: <see cref="IsSaving"/> will be true.
            /// </summary>
            internal void InitiateSave_MainUnityThread()
            {
                IsSavingMutex.WaitOne();
                IsSaving = true;

                lock (queue_needsSaving)
                {
                    var enumeratorASAP = queue_needsSavingASAP.GetEnumerator();
                    while (enumeratorASAP.MoveNext())
                    {
                        queue_needsSaving.Enqueue(enumeratorASAP.Current);
                    }
                }
                queue_needsSavingASAP.Clear();
            }

            /// <summary>
            /// PRE: This is expected to NOT be called from Main Unity thread, but rather from what we will call the "save thread" (which at time of writing is the <see cref="databaseSaveThread"/>)
            /// POST: <see cref="queue_needsSaving"/> will be cleared and <see cref="queue_needsReturnToPool"/> will contain all items previously in <see cref="queue_needsSaving"/> and <see cref="IsSaving"/> will be false.
            /// </summary>
            internal void OnAfterAllSaved_SaveThread()
            {
                lock (queue_needsSaving)
                {
                    SyncEvent_ValueChangeProcessed syncEvent;
                    while (queue_needsSaving.Count > 0 && (syncEvent = queue_needsSaving.Dequeue()) != null)
                    {
                        queue_needsReturnToPool.Enqueue(syncEvent);
                    }
                }

                IsSavingMutex.Set();
                IsSaving = false;
            }

            internal void ReturnSaved_SpreadOverFrames_MainUnityThread()
            {
                int queueCount = queue_needsReturnToPool.Count;
                if (queueCount > MAX_SYNC_EVENTS_RETURN_PER_FRAME_THRESHOLD)
                {
                    maxToReturnPerFrame += MAX_SYNC_EVENTS_RETURN_PER_FRAME_INCREASEBY_WHENBUSY; // TODO try a better calculation for what actually makes sense here
                }

                int actualReturnCount = maxToReturnPerFrame;
                int remainingCount = actualReturnCount;
                SyncEvent_ValueChangeProcessed syncEventToReturn;
                while (remainingCount > 0 && queue_needsReturnToPool.TryDequeue(out syncEventToReturn))
                {
                    syncEventToReturn.Return();
                    --remainingCount;
                }

                //if (actualReturnCount > 0) GONetLog.Debug("just returned "+actualReturnCount+", how many remain? queue_needsReturnToPool.Count: " + queue_needsReturnToPool.Count);
            }

            /// <summary>
            /// Sets the drain rate to a new value, respecting the minimum floor.
            /// Called when scene changes to adjust for expected sync event volume.
            /// </summary>
            internal void SetDrainRate(int newRate)
            {
                maxToReturnPerFrame = Math.Max(STARTING_MAX_SYNC_EVENTS_RETURN_PER_FRAME, newRate);
            }
        }
        #region Persistence Queue Thinning (Reliability-Aware Temporal Sampling)

        /// <summary>
        /// Cache for reliability lookups (CodeGenId, SyncMemberIndex) → isReliable.
        /// Speeds up repeated lookups by ~20x (single dictionary access vs multiple lookups + array access).
        /// </summary>
        private static readonly Dictionary<ulong, bool> reliabilityCacheByCodeGenIdAndValueIndex = new Dictionary<ulong, bool>(200);

        /// <summary>
        /// Generates unique cache key for (CodeGenId, SyncMemberIndex) pair.
        /// Uses bit shifting to combine: (CodeGenId << 8) | SyncMemberIndex.
        /// </summary>
        private static ulong GetReliabilityCacheKey(ushort codeGenId, byte syncMemberIndex)
        {
            return ((ulong)codeGenId << 8) | syncMemberIndex;
        }

        /// <summary>
        /// Checks if sync event is reliable (should never be dropped).
        /// Uses caching for performance (~99.9% hit rate after warm-up).
        /// Defensive fallback: Returns TRUE when can't determine (safer to keep than drop).
        ///
        /// NOTE: Current implementation uses defensive fallback (returns true) since SyncEvent_ValueChangeProcessed
        /// doesn't carry channel information. For full implementation, need to track channel ID in events.
        /// This is intentional - better to keep events than accidentally drop critical state.
        /// </summary>
        private static bool IsEventReliable(SyncEvent_ValueChangeProcessed evt)
        {
            if (evt == null) return true; // Defensive: assume reliable

            // Try cache first (fast path)
            ulong cacheKey = GetReliabilityCacheKey(evt.CodeGenerationId, evt.SyncMemberIndex);
            if (reliabilityCacheByCodeGenIdAndValueIndex.TryGetValue(cacheKey, out bool cachedReliability))
            {
                return cachedReliability;
            }

            // Cache miss - lookup reliability from sync profile (slow path)
            bool isReliable = true; // Defensive fallback

            // Get GONetParticipant from GONetId
            if (gonetParticipantByGONetIdMap.TryGetValue(evt.GONetId, out GONetParticipant gnp) && gnp != null)
            {
                var syncCompanion = GetSyncCompanionByGNP(gnp);
                if (syncCompanion != null && syncCompanion.valuesChangesSupport != null)
                {
                    if (evt.SyncMemberIndex < syncCompanion.valuesChangesSupport.Length)
                    {
                        var support = syncCompanion.valuesChangesSupport[evt.SyncMemberIndex];
                        if (support != null)
                        {
                            // Check reliability from sync attribute
                            isReliable = support.syncAttribute_Reliability == AutoMagicalSyncReliability.Reliable;
                        }
                    }
                }
            }

            // Cache result for future lookups
            reliabilityCacheByCodeGenIdAndValueIndex[cacheKey] = isReliable;

            return isReliable;
        }

        /// <summary>
        /// Tries to drop oldest unreliable event from queue.
        /// Returns true if dropped, false if queue empty or only reliable events remain.
        /// </summary>
        private static bool TryDropOldestUnreliableEvent(Queue<SyncEvent_ValueChangeProcessed> queue)
        {
            if (queue == null || queue.Count == 0)
            {
                return false;
            }

            // Single-pass scan for oldest unreliable
            List<SyncEvent_ValueChangeProcessed> tempList = new List<SyncEvent_ValueChangeProcessed>(queue.Count);
            SyncEvent_ValueChangeProcessed oldestUnreliable = null;

            while (queue.Count > 0)
            {
                var evt = queue.Dequeue();
                tempList.Add(evt);

                if (!IsEventReliable(evt) && oldestUnreliable == null)
                {
                    oldestUnreliable = evt; // First unreliable = oldest (FIFO)
                }
            }

            // Re-enqueue all except the dropped one
            bool didDrop = false;
            foreach (var evt in tempList)
            {
                if (evt == oldestUnreliable && !didDrop)
                {
                    didDrop = true; // Drop this one
                    continue;
                }
                queue.Enqueue(evt);
            }

            return didDrop;
        }

        /// <summary>
        /// Temporal thinning for persistence queue (reliability-aware sampling).
        /// Instead of dropping oldest N events (catastrophic gaps), spreads drops evenly across timeline.
        /// ALWAYS preserves reliable events (critical for debugging).
        /// </summary>
        private static void ThinPersistenceQueue(Queue<SyncEvent_ValueChangeProcessed> queue)
        {
            if (queue == null || queue.Count == 0)
            {
                return;
            }

            // Get thinning configuration from GONetGlobal
            var gonetGlobal = GONetGlobal.Instance;
            if (gonetGlobal == null)
            {
                return; // Can't thin without configuration
            }

            int keepEveryNth = gonetGlobal.persistenceQueueThinningKeepEveryNth;
            if (keepEveryNth < 2)
            {
                return; // No thinning if keepEveryNth < 2
            }

            // Dequeue all events
            List<SyncEvent_ValueChangeProcessed> allEvents = new List<SyncEvent_ValueChangeProcessed>(queue.Count);
            while (queue.Count > 0)
            {
                allEvents.Add(queue.Dequeue());
            }

            // Temporal thinning: Keep every Nth unreliable, always keep reliable
            for (int i = 0; i < allEvents.Count; i++)
            {
                var evt = allEvents[i];
                bool isReliable = IsEventReliable(evt);

                // Keep if: reliable OR matches temporal sampling pattern
                bool shouldKeep = isReliable || (i % keepEveryNth == 0);

                if (shouldKeep)
                {
                    queue.Enqueue(evt);
                }
                // else: dropped (temporal thinning)
            }
        }

        /// <summary>
        /// Thins all persistence queues when CPU/memory thresholds are exceeded.
        /// Called from persistence processing when resource limits are hit.
        /// </summary>
        private static void ThinAllPersistenceQueues()
        {
#if !PERF_NO_PROCESS_SYNC_EVENTS
            foreach (var kvp in syncEventsToSaveQueueByEventType)
            {
                ThinPersistenceQueue(kvp.Value.queue_needsSavingASAP);
                // DON'T thin queue_needsSaving - it's actively being processed by save thread
            }
#endif
        }

        #endregion

    }
}
