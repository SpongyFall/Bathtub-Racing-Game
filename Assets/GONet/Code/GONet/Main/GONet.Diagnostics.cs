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
        #region Scene Loading History Tracker

        /// <summary>
        /// Tracks scene loading history for debugging message flow.
        /// Thread-safe for concurrent access from network/main threads.
        /// Format: "GONetSample → RPCPlayground → ProjectileTest"
        /// </summary>
        private static readonly List<string> sceneLoadHistory = new List<string>();
        private static readonly object sceneHistoryLock = new object();

        /// <summary>
        /// Gets scene loading history as a single string for log prefixes.
        /// Thread-safe. Returns empty string if no scenes loaded yet.
        /// </summary>
        internal static string GetSceneHistory()
        {
            lock (sceneHistoryLock)
            {
                if (sceneLoadHistory.Count == 0)
                    return string.Empty;

                return string.Join(" → ", sceneLoadHistory);
            }
        }

        /// <summary>
        /// Records a scene load in the history tracker.
        /// Called automatically by GONetSceneManager when scenes load.
        /// Thread-safe.
        /// </summary>
        internal static void RecordSceneLoad(string sceneName)
        {
            lock (sceneHistoryLock)
            {
                sceneLoadHistory.Add(sceneName);
                GONetLog.Debug($"[SceneHistory] Scene loaded: {sceneName} (history now: {GetSceneHistory()})");
            }
        }

        /// <summary>
        /// Clears scene loading history. Used when starting new game sessions.
        /// Thread-safe.
        /// </summary>
        internal static void ClearSceneHistory()
        {
            lock (sceneHistoryLock)
            {
                sceneLoadHistory.Clear();
            }
        }


        #region Ring Buffer Metrics

        /// <summary>
        /// Snapshot of ring buffer metrics for a specific thread's event queue.
        /// Used by the inspector and debugging tools to monitor buffer health.
        /// </summary>
        public struct RingBufferMetrics
        {
            public int Capacity;
            public int Count;
            public int PeakCount;
            public int ResizeCount;
            public float FillPercentage;
            public string ThreadName;
        }

        /// <summary>
        /// Gets metrics for all ring buffers (one per sync thread).
        /// Thread-safe. Returns empty array if not initialized yet.
        /// Used by GONetGlobalCustomInspector for live metrics display.
        /// </summary>
        public static RingBufferMetrics[] GetRingBufferMetrics()
        {
            if (events_SendToOthersQueue_ByThreadMap == null || events_SendToOthersQueue_ByThreadMap.Count == 0)
            {
                return Array.Empty<RingBufferMetrics>();
            }

            var metrics = new List<RingBufferMetrics>();

            // Thread-safe iteration (dictionary keys added only from main thread, but ring buffers accessed from multiple threads)
            lock (events_SendToOthersQueue_ByThreadMap)
            {
                foreach (var kvp in events_SendToOthersQueue_ByThreadMap)
                {
                    var buffer = kvp.Value;
                    metrics.Add(new RingBufferMetrics
                    {
                        Capacity = buffer.Capacity,
                        Count = buffer.Count,
                        PeakCount = buffer.PeakCount,
                        ResizeCount = buffer.ResizeCount,
                        FillPercentage = buffer.FillPercentage,
                        ThreadName = kvp.Key.Name ?? "Main Thread"
                    });
                }
            }

            return metrics.ToArray();
        }


        #region Resource Diagnostics

        /// <summary>
        /// Snapshot of GONet resource usage for diagnostics.
        /// Used by GONetGlobal for periodic health logging.
        /// </summary>
        public struct ResourceDiagnostics
        {
            public int ActiveSyncCompanionCount;
            public int NullParticipantCount;
            public int DeferredRpcCount;
            public int ActiveGONetParticipantCount;
            public int RecentlyDespawnedCount;
            public int EventBusSubscriptionCount;
            public int PoolBorrowedCount;
            public int PersistenceQueueSize;
        }

        /// <summary>
        /// Gets diagnostic snapshot of GONet resource usage.
        /// Used by GONetGlobal for periodic health logging.
        /// </summary>
        public static ResourceDiagnostics GetResourceDiagnostics()
        {
            var diagnostics = new ResourceDiagnostics();

            // Count active sync companions and null participants
            int totalCompanions = 0;
            int nullParticipants = 0;

            foreach (var codeGenEntry in activeAutoSyncCompanionsByCodeGenerationIdMap)
            {
                foreach (var companionEntry in codeGenEntry.Value)
                {
                    totalCompanions++;
                    if (companionEntry.Value.gonetParticipant == null)
                    {
                        nullParticipants++;
                    }
                }
            }

            diagnostics.ActiveSyncCompanionCount = totalCompanions;
            diagnostics.NullParticipantCount = nullParticipants;

            // Deferred RPC count
            diagnostics.DeferredRpcCount = GONetEventBus.GetDeferredRpcCount();

            // Active GONetParticipant count
            diagnostics.ActiveGONetParticipantCount = gonetParticipantByGONetIdMap.Count;

            // Recently despawned count
            diagnostics.RecentlyDespawnedCount = recentlyDespawnedGONetIds.Count;

            // Event bus subscription count
            diagnostics.EventBusSubscriptionCount = EventBus != null ? EventBus.GetSubscriptionCount() : 0;

            // Pool borrowed count
            int poolBorrowed = 0;
            foreach (var kvp in singleProducerSendQueuesByThread)
            {
                poolBorrowed += kvp.Value.resourcePool.BorrowedCount;
            }
            diagnostics.PoolBorrowedCount = poolBorrowed;

            // Sync event persistence queue sizes
            diagnostics.PersistenceQueueSize = GetPersistenceQueueSize();

            return diagnostics;
        }

        /// <summary>
        /// Gets the total size of persistence queues for sync events.
        /// Used to detect unbounded queue growth (the CPU leak!).
        /// </summary>
        private static int GetPersistenceQueueSize()
        {
#if !PERF_NO_PROCESS_SYNC_EVENTS
            int totalSize = 0;
            foreach (var kvp in syncEventsToSaveQueueByEventType)
            {
                totalSize += kvp.Value.queue_needsSavingASAP.Count;
                totalSize += kvp.Value.queue_needsSaving.Count;
            }
            return totalSize;
#else
            return 0;
#endif
        }

        /// <summary>
        /// Estimated memory size per sync event (conservative estimate).
        /// Used for approximate memory tracking of persistence queues.
        /// </summary>
        private const int ESTIMATED_EVENT_SIZE_BYTES = 200;

        /// <summary>
        /// Last measured processing time for persistence queue (milliseconds).
        /// Updated during Process_SyncEvents_ForPersistence().
        /// Used by GONetGlobal for CPU threshold monitoring.
        /// </summary>
        internal static double persistenceQueueLastProcessingTimeMs = 0;

        /// <summary>
        /// Gets approximate memory usage of persistence queues in megabytes.
        /// Uses conservative estimate of ESTIMATED_EVENT_SIZE_BYTES per event.
        /// Used by GONetGlobal for memory threshold monitoring.
        /// </summary>
        internal static int GetApproximateQueueMemoryMB()
        {
#if !PERF_NO_PROCESS_SYNC_EVENTS
            int totalEvents = 0;
            foreach (var kvp in syncEventsToSaveQueueByEventType)
            {
                totalEvents += kvp.Value.queue_needsSavingASAP.Count;
                totalEvents += kvp.Value.queue_needsSaving.Count;
            }

            return (totalEvents * ESTIMATED_EVENT_SIZE_BYTES) / (1024 * 1024);
#else
            return 0;
#endif
        }


        #region Persistent Event History Export

        /// <summary>
        /// Controls whether event history export is enabled.
        /// Set via GONetGlobal inspector or code before initialization.
        /// </summary>
        public static bool EnableEventHistoryExport { get; set; } = true;

        /// <summary>
        /// If true, only server exports event history.
        /// If false, all machines (server + clients) export their own copies.
        /// Default: false (all machines export for maximum debugging capability)
        /// </summary>
        public static bool EventHistoryExport_ServerOnly { get; set; } = false;

        /// <summary>
        /// Exports the complete persistent event history to a human-readable file.
        /// Called automatically on application quit if EnableEventHistoryExport is true.
        /// File format: gonet-events-YYYY-MM-DD-HHmmss-[Server|ClientN].txt
        /// </summary>
        private static void ExportPersistentEventHistory()
        {
            if (!EnableEventHistoryExport)
            {
                GONetLog.Debug("[EventHistory] Export disabled (EnableEventHistoryExport=false)");
                return;
            }

            if (EventHistoryExport_ServerOnly && !IsServer)
            {
                GONetLog.Debug("[EventHistory] Export skipped (EventHistoryExport_ServerOnly=true and this is a client)");
                return;
            }

            try
            {
                // Determine role identifier for filename
                string roleIdentifier = IsServer ? "Server" : $"Client{MyAuthorityId}";

                // Create filename with timestamp
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
                string filename = $"gonet-events-{timestamp}-{roleIdentifier}.txt";

                // Use same directory as GONetLog (Application.persistentDataPath/logs)
                string logDirectory = Path.Combine(Application.persistentDataPath, "logs");
                Directory.CreateDirectory(logDirectory); // Ensure directory exists

                string filepath = Path.Combine(logDirectory, filename);

                int eventCount = persistentEventsArchive_CompleteHistory.Count;
                GONetLog.Info($"[EventHistory] Exporting {eventCount} persistent events to: {filepath}");

                using (StreamWriter writer = new StreamWriter(filepath, append: false, Encoding.UTF8))
                {
                    // Write header
                    writer.WriteLine("================================================================================");
                    writer.WriteLine($"GONet Persistent Event History Export");
                    writer.WriteLine($"Role: {roleIdentifier}");
                    writer.WriteLine($"Authority ID: {MyAuthorityId}");
                    writer.WriteLine($"Session GUID: {SessionGUID}");
                    writer.WriteLine($"Export Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                    writer.WriteLine($"Event Count: {eventCount}");
                    writer.WriteLine($"Scene History: {GetSceneHistory()}");
                    writer.WriteLine("================================================================================");
                    writer.WriteLine();

                    // Write event index for quick navigation
                    writer.WriteLine("EVENT INDEX (for grep searching):");
                    writer.WriteLine("  InstantiateGONetParticipantEvent - Spawns");
                    writer.WriteLine("  SyncEvent_ValueChangeProcessed - Value changes (Note: Transient, may not appear in persistent archive)");
                    writer.WriteLine("  SceneLoadEvent - Scene loads");
                    writer.WriteLine("  SceneUnloadEvent - Scene unloads");
                    writer.WriteLine();
                    writer.WriteLine("GREP EXAMPLES:");
                    writer.WriteLine("  grep 'GONetId=3072' gonet-events-*.txt  # All events for specific participant");
                    writer.WriteLine("  grep 'InstantiateGONetParticipantEvent' gonet-events-*.txt  # All spawns");
                    writer.WriteLine("  grep 'Authority1' gonet-events-*.txt  # All events involving client 1");
                    writer.WriteLine("================================================================================");
                    writer.WriteLine();

                    // Write events in chronological order
                    int eventIndex = 0;
                    foreach (var persistentEvent in persistentEventsArchive_CompleteHistory)
                    {
                        eventIndex++;

                        // Extract common properties from event
                        string eventTypeName = persistentEvent.GetType().Name;
                        uint gonetId = 0;
                        ushort ownerAuthority = 0;
                        long elapsedTicks = persistentEvent.OccurredAtElapsedTicks;

                        // Try to extract GONetId and OwnerAuthorityId from common event types
                        if (persistentEvent is InstantiateGONetParticipantEvent instantiateEvent)
                        {
                            gonetId = instantiateEvent.GONetId;
                            ownerAuthority = instantiateEvent.OwnerAuthorityId;
                        }
                        else if (persistentEvent is SyncEvent_ValueChangeProcessed valueChangeEvent)
                        {
                            gonetId = valueChangeEvent.GONetId;
                        }

                        // Format timestamp
                        double elapsedSeconds = elapsedTicks * GONet.Utils.HighResolutionTimeUtils.TICKS_TO_SECONDS;

                        // Write event entry
                        writer.WriteLine($"[Event {eventIndex:D6}] Type={eventTypeName}");
                        writer.WriteLine($"  Timestamp: Ticks={elapsedTicks} ({elapsedSeconds:F3}s)");

                        if (gonetId != 0)
                            writer.WriteLine($"  GONetId: {gonetId}");

                        if (ownerAuthority != 0)
                            writer.WriteLine($"  Owner: Authority{ownerAuthority}");

                        // Add event-specific details
                        writer.WriteLine($"  Details: {GetEventDetailsString(persistentEvent)}");
                        writer.WriteLine();
                    }

                    writer.WriteLine("================================================================================");
                    writer.WriteLine($"END OF EVENT HISTORY - Total Events: {eventCount}");
                    writer.WriteLine("================================================================================");
                }

                GONetLog.Info($"[EventHistory] Export complete: {filepath} ({eventCount} events)");
            }
            catch (Exception ex)
            {
                GONetLog.Error($"[EventHistory] Export failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Gets a human-readable string with event-specific details.
        /// Used for event history export.
        /// </summary>
        private static string GetEventDetailsString(IPersistentEvent persistentEvent)
        {
            try
            {
                // Use ToString() as base, then add type-specific details
                string baseString = persistentEvent.ToString();

                if (persistentEvent is InstantiateGONetParticipantEvent instantiateEvent)
                {
                    return $"{baseString} | DesignTimeLocation={instantiateEvent.DesignTimeLocation} | Position={instantiateEvent.Position} | Rotation={instantiateEvent.Rotation}";
                }
                else if (persistentEvent is SyncEvent_ValueChangeProcessed valueChangeEvent)
                {
                    return $"{baseString} | SyncMemberIndex={valueChangeEvent.SyncMemberIndex} | GONetId={valueChangeEvent.GONetId}";
                }

                return baseString;
            }
            catch (Exception ex)
            {
                return $"[Error getting details: {ex.Message}]";
            }
        }

        #endregion
        #endregion
        #endregion
        #endregion

        #region Collection Growth Instrumentation

        /// <summary>
        /// Snapshot of a single collection's capacity and count for growth tracking.
        /// Used to diagnose performance issues from dynamic collection resizing.
        /// </summary>
        public struct CollectionStats
        {
            public string Name;
            public int Count;
            public int Capacity;
            public float UtilizationPercent;

            public CollectionStats(string name, int count, int capacity)
            {
                Name = name;
                Count = count;
                Capacity = capacity;
                UtilizationPercent = capacity > 0 ? (count / (float)capacity * 100f) : 0f;
            }
        }

        /// <summary>
        /// Comprehensive snapshot of all major GONet collections for growth analysis.
        /// Includes GC statistics to correlate collection growth with GC pressure.
        /// </summary>
        public struct CollectionGrowthSnapshot
        {
            public string Context;
            public double TimestampSeconds;
            public List<CollectionStats> Collections;

            // GC statistics
            public int Gen0Collections;
            public int Gen1Collections;
            public int Gen2Collections;
            public long TotalMemoryBytes;
        }

        /// <summary>
        /// Captures a snapshot of all major GONet collection sizes and GC stats.
        /// Call this before/after scene load to track growth over time.
        ///
        /// USAGE:
        ///   var before = GONetMain.CaptureCollectionGrowthSnapshot("Before Scene Load");
        ///   // ... scene loads ...
        ///   var after = GONetMain.CaptureCollectionGrowthSnapshot("After Scene Load");
        ///   GONetMain.LogCollectionGrowthComparison(before, after);
        /// </summary>
        public static CollectionGrowthSnapshot CaptureCollectionGrowthSnapshot(string context)
        {
            var snapshot = new CollectionGrowthSnapshot
            {
                Context = context,
                TimestampSeconds = Time.ElapsedSeconds,
                Collections = new List<CollectionStats>(),

                // Capture GC statistics
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                TotalMemoryBytes = GC.GetTotalMemory(false)
            };

            // Capture participant dictionaries
            snapshot.Collections.Add(new CollectionStats(
                "gonetParticipantByGONetIdMap",
                gonetParticipantByGONetIdMap.Count,
                GetDictionaryCapacity(gonetParticipantByGONetIdMap)));

            snapshot.Collections.Add(new CollectionStats(
                "gonetParticipantByGONetIdAtInstantiationMap",
                gonetParticipantByGONetIdAtInstantiationMap.Count,
                GetDictionaryCapacity(gonetParticipantByGONetIdAtInstantiationMap)));

            snapshot.Collections.Add(new CollectionStats(
                "recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map",
                recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map.Count,
                GetDictionaryCapacity(recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map)));

            // Capture auto-sync companion dictionaries
            int totalAutoSyncCompanions = 0;
            int totalAutoSyncCompanions_uint = 0;
            foreach (var codeGenEntry in activeAutoSyncCompanionsByCodeGenerationIdMap)
            {
                totalAutoSyncCompanions += codeGenEntry.Value.Count;
            }
            foreach (var codeGenEntry in activeAutoSyncCompanionsByCodeGenerationIdMap_uintKeyForPerformance)
            {
                totalAutoSyncCompanions_uint += codeGenEntry.Value.Count;
            }

            snapshot.Collections.Add(new CollectionStats(
                "activeAutoSyncCompanions (total across all code gen IDs)",
                totalAutoSyncCompanions,
                -1)); // Capacity not easily accessible for nested dictionaries

            snapshot.Collections.Add(new CollectionStats(
                "activeAutoSyncCompanions_uintKey (total across all code gen IDs)",
                totalAutoSyncCompanions_uint,
                -1));

            // Capture server-side collections (if server)
            if (IsServer && gonetServer != null)
            {
                snapshot.Collections.Add(new CollectionStats(
                    "gonetServer.remoteClients",
                    gonetServer.remoteClients.Count,
                    GetListCapacity(gonetServer.remoteClients)));
            }

            // Capture recently disabled GONetId tracking (used for late-joiner spawn suppression)
            snapshot.Collections.Add(new CollectionStats(
                "recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map",
                recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map.Count,
                GetDictionaryCapacity(recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map)));

            // Capture sync event persistence queues
            int totalPersistenceQueueSize = 0;
#if !PERF_NO_PROCESS_SYNC_EVENTS
            foreach (var kvp in syncEventsToSaveQueueByEventType)
            {
                totalPersistenceQueueSize += kvp.Value.queue_needsSavingASAP.Count;
                totalPersistenceQueueSize += kvp.Value.queue_needsSaving.Count;
            }
#endif
            snapshot.Collections.Add(new CollectionStats(
                "syncEventsToSaveQueue (total)",
                totalPersistenceQueueSize,
                -1));

            // Capture thread-specific queues
            snapshot.Collections.Add(new CollectionStats(
                "events_AwaitingSendToOthersQueue (threads)",
                events_AwaitingSendToOthersQueue_ByThreadMap.Count,
                GetDictionaryCapacity(events_AwaitingSendToOthersQueue_ByThreadMap)));

            snapshot.Collections.Add(new CollectionStats(
                "events_SendToOthersQueue (threads)",
                events_SendToOthersQueue_ByThreadMap.Count,
                GetDictionaryCapacity(events_SendToOthersQueue_ByThreadMap)));

            return snapshot;
        }

        /// <summary>
        /// Logs a collection growth snapshot to the GONet log.
        /// </summary>
        public static void LogCollectionGrowthSnapshot(CollectionGrowthSnapshot snapshot)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[COLLECTION-GROWTH] {snapshot.Context} @ {snapshot.TimestampSeconds:F2}s");
            sb.AppendLine($"  GC: Gen0={snapshot.Gen0Collections} Gen1={snapshot.Gen1Collections} Gen2={snapshot.Gen2Collections} Memory={snapshot.TotalMemoryBytes / (1024 * 1024)}MB");
            sb.AppendLine($"  Collections:");

            foreach (var stats in snapshot.Collections)
            {
                if (stats.Capacity >= 0)
                {
                    sb.AppendLine($"    {stats.Name,-50} Count={stats.Count,6} Capacity={stats.Capacity,6} ({stats.UtilizationPercent:F1}%)");
                }
                else
                {
                    sb.AppendLine($"    {stats.Name,-50} Count={stats.Count,6} (nested/unknown capacity)");
                }
            }

            // GONetLog.Info(sb.ToString()); // DISABLED - spam
        }

        /// <summary>
        /// Logs the difference between two snapshots to identify growth patterns.
        /// Useful for understanding what changed during scene load or other operations.
        /// </summary>
        public static void LogCollectionGrowthComparison(CollectionGrowthSnapshot before, CollectionGrowthSnapshot after)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[COLLECTION-GROWTH] Comparison: '{before.Context}' → '{after.Context}'");
            sb.AppendLine($"  Time: {before.TimestampSeconds:F2}s → {after.TimestampSeconds:F2}s (Δ {after.TimestampSeconds - before.TimestampSeconds:F2}s)");
            sb.AppendLine($"  GC Changes:");
            sb.AppendLine($"    Gen0: {before.Gen0Collections} → {after.Gen0Collections} (Δ {after.Gen0Collections - before.Gen0Collections})");
            sb.AppendLine($"    Gen1: {before.Gen1Collections} → {after.Gen1Collections} (Δ {after.Gen1Collections - before.Gen1Collections})");
            sb.AppendLine($"    Gen2: {before.Gen2Collections} → {after.Gen2Collections} (Δ {after.Gen2Collections - before.Gen2Collections})");
            sb.AppendLine($"    Memory: {before.TotalMemoryBytes / (1024 * 1024)}MB → {after.TotalMemoryBytes / (1024 * 1024)}MB (Δ {(after.TotalMemoryBytes - before.TotalMemoryBytes) / (1024 * 1024)}MB)");
            sb.AppendLine($"  Collection Changes:");

            // Match collections by name
            var beforeDict = new Dictionary<string, CollectionStats>();
            foreach (var stats in before.Collections)
            {
                beforeDict[stats.Name] = stats;
            }

            foreach (var afterStats in after.Collections)
            {
                if (beforeDict.TryGetValue(afterStats.Name, out var beforeStats))
                {
                    int countDelta = afterStats.Count - beforeStats.Count;
                    int capacityDelta = afterStats.Capacity - beforeStats.Capacity;

                    if (countDelta != 0 || capacityDelta != 0)
                    {
                        if (afterStats.Capacity >= 0 && beforeStats.Capacity >= 0)
                        {
                            sb.AppendLine($"    {afterStats.Name,-50} Count: {beforeStats.Count,6} → {afterStats.Count,6} (Δ {countDelta,+6}) | Capacity: {beforeStats.Capacity,6} → {afterStats.Capacity,6} (Δ {capacityDelta,+6})");
                        }
                        else
                        {
                            sb.AppendLine($"    {afterStats.Name,-50} Count: {beforeStats.Count,6} → {afterStats.Count,6} (Δ {countDelta,+6})");
                        }
                    }
                }
            }

            // GONetLog.Info(sb.ToString()); // DISABLED - spam
        }

        /// <summary>
        /// Gets the capacity of a Dictionary using reflection.
        /// Returns -1 if capacity cannot be determined.
        /// </summary>
        private static int GetDictionaryCapacity<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
        {
            try
            {
                // Dictionary capacity is accessible via reflection or EnsureCapacity behavior
                // For now, we estimate based on power-of-2 growth pattern
                int count = dictionary.Count;
                if (count == 0) return 0;

                // Dictionary doubles capacity when it exceeds current capacity
                // Find next power of 2 >= count
                int capacity = 1;
                while (capacity < count)
                {
                    capacity *= 2;
                }

                // Dictionary actually grows when count exceeds capacity * loadFactor (0.75 for Dictionary)
                // So if we're at count N, capacity is likely the next power of 2 after N / 0.75
                int estimatedCapacity = capacity;
                if (count > capacity * 0.75f)
                {
                    estimatedCapacity = capacity * 2;
                }

                return estimatedCapacity;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Gets the capacity of a List using reflection.
        /// Returns -1 if capacity cannot be determined.
        /// </summary>
        private static int GetListCapacity<T>(List<T> list)
        {
            try
            {
                // List.Capacity is a public property in .NET
                return list.Capacity;
            }
            catch
            {
                return -1;
            }
        }

        #endregion

        #region Persistence Queue Diagnostics

        /// <summary>
        /// Logs detailed breakdown of the persistence queue by event type.
        /// Shows what's accumulating in the queue and at what rate.
        /// </summary>
        public static void LogPersistenceQueueBreakdown()
        {
#if PERF_NO_PROCESS_SYNC_EVENTS
            // GONetLog.Info("[PERSISTENCE-QUEUE] Sync event processing disabled (PERF_NO_PROCESS_SYNC_EVENTS)"); // DISABLED - spam
            return;
#else
            var sb = new StringBuilder();
            sb.AppendLine("[PERSISTENCE-QUEUE] Breakdown by Event Type:");

            int totalASAP = 0;
            int totalSaving = 0;
            int totalReturnPool = 0;

            if (syncEventsToSaveQueueByEventType.Count == 0)
            {
                sb.AppendLine("  (No event types registered yet)");
            }
            else
            {
                // Collect stats for each event type
                var eventTypeStats = new List<(string typeName, int asapCount, int savingCount, int returnPoolCount)>();

                foreach (var kvp in syncEventsToSaveQueueByEventType)
                {
                    string typeName = kvp.Key.Name;
                    var saveSupport = kvp.Value;

                    int asapCount = saveSupport.queue_needsSavingASAP.Count;
                    int savingCount = saveSupport.queue_needsSaving.Count;
                    int returnPoolCount = saveSupport.queue_needsReturnToPool.Count;

                    totalASAP += asapCount;
                    totalSaving += savingCount;
                    totalReturnPool += returnPoolCount;

                    // Only log event types that have items
                    if (asapCount > 0 || savingCount > 0 || returnPoolCount > 0)
                    {
                        eventTypeStats.Add((typeName, asapCount, savingCount, returnPoolCount));
                    }
                }

                // Sort by total count (ASAP + Saving) descending
                eventTypeStats.Sort((a, b) => (b.asapCount + b.savingCount).CompareTo(a.asapCount + a.savingCount));

                // Log each event type
                foreach (var stat in eventTypeStats)
                {
                    int total = stat.asapCount + stat.savingCount;
                    sb.AppendLine($"  {stat.typeName,-50} ASAP={stat.asapCount,5} Saving={stat.savingCount,5} ReturnPool={stat.returnPoolCount,5} (Total={total,5})");
                }

                // Summary
                sb.AppendLine($"  ---");
                sb.AppendLine($"  TOTALS: ASAP={totalASAP,5} Saving={totalSaving,5} ReturnPool={totalReturnPool,5} (QueueTotal={totalASAP + totalSaving,5})");
            }

            // GONetLog.Info(sb.ToString()); // DISABLED - spam
#endif
        }

        #endregion

    }
}
