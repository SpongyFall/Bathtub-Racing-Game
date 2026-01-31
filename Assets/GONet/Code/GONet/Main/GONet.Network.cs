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
        /// <summary>
        /// Reusable stopwatch for CPU budget tracking (avoids 40-byte allocation per queue per frame).
        /// Used in ProcessIncomingBytes_QueuedNetworkData_MainThread for per-queue time boxing.
        /// </summary>
        private static readonly System.Diagnostics.Stopwatch _cpuBudgetStopwatch = new System.Diagnostics.Stopwatch();
        private static long _timeSyncIgnoreLogRawTicks;

        // DIAGNOSTIC: Track timestamp path usage for RTT debugging
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static long _timestampFallbackCount;
        private static long _timestampProvidedCount;
        /// <summary>
        /// Returns ratio of messages with transport timestamp vs fallback to current time.
        /// Values near 1.0 = good (transport timestamps flowing through).
        /// Values near 0.0 = bad (falling back to current time, RTT will be inflated).
        /// </summary>
        public static float TimestampProvidedRatio =>
            (_timestampProvidedCount + _timestampFallbackCount) > 0
                ? (float)_timestampProvidedCount / (_timestampProvidedCount + _timestampFallbackCount)
                : 0f;
        public static (long provided, long fallback) GetTimestampCounts() => (_timestampProvidedCount, _timestampFallbackCount);
        #else
        public static float TimestampProvidedRatio => 1f; // Assume good in release builds (no tracking)
        #endif

        internal struct NetworkData
        {
            public GONetConnection relatedConnection;
            public Thread messageBytesBorrowedOnThread;
            public byte[] messageBytes;
            public int bytesUsedCount;
            public GONetChannelId channelId;
            /// <summary>
            /// High-resolution timestamp (ticks) when this bundle was first deferred waiting for participant.
            /// Used to enforce <see cref="GONetGlobal.maxSecondsToWaitForMissingParticipant"/> timeout.
            /// 0 = not yet deferred or tracking not needed.
            /// </summary>
            public long deferredAtTicks;

            /// <summary>
            /// High-resolution timestamp (raw ticks) when this packet was received on the network thread.
            /// CRITICAL for time sync: Captures t2 immediately when packet arrives, BEFORE main thread queuing delays.
            /// This eliminates RTT inflation caused by packets waiting in queue while main thread is busy.
            /// </summary>
            public long receivedAtRawTicks;
        }

        // Constants for exception message parsing in drop logging (zero allocation)
        private const string EXCEPTION_MSG_STALE_GONETID = "GONetId: 0";
        private const string EXCEPTION_MSG_MISSING_PARTICIPANT = "missing participant";

        static readonly ConcurrentDictionary<Thread, SingleProducerQueues> singleProducerReceiveQueuesByThread = new ConcurrentDictionary<Thread, SingleProducerQueues>();
        static readonly ConcurrentDictionary<Thread, SingleProducerQueues> singleProducerSendQueuesByThread = new ConcurrentDictionary<Thread, SingleProducerQueues>();

        /// <summary>
        /// Each <see cref="Thread"/> that ends up calling either 
        /// 
        /// A) For Sends:
        /// <see cref="SendBytesToRemoteConnections(GONetCodeGenerationId[], int, GONetCodeGenerationId)"/> or 
        /// <see cref="SendBytesToRemoteConnection(GONetConnection, GONetCodeGenerationId[], int, GONetCodeGenerationId)"/> (i.e., the producer)
        /// 
        /// B) For Receives:
        /// <see cref="ProcessIncomingBytes_TriageFromAnyThread(GONetConnection, GONetCodeGenerationId[], int, GONetCodeGenerationId)"/> (i.e., the producer)
        /// 
        /// will have an instance of this to keep track of related stuffs as it moves between the producer and consumer thread (i.e., "end of the line" for sends).
        /// </summary>
        internal sealed class SingleProducerQueues
        {
            /// <summary>
            /// DEPRECATED: Use GONetGlobal.Instance.maxPacketsPerTick instead.
            /// Kept for backward compatibility with code that references this constant.
            /// </summary>
            internal const int MAX_PACKETS_PER_TICK = 10 * 100;

            internal readonly ConcurrentQueue<NetworkData> queueForWork = new ConcurrentQueue<NetworkData>();
            internal readonly ConcurrentQueue<NetworkData> queueForPostWorkResourceReturn = new ConcurrentQueue<NetworkData>();

            /// <summary>
            /// CRITICAL IMPROVEMENT: Switched from ArrayPool to TieredArrayPool (October 2025).
            ///
            /// OLD PROBLEM:
            /// - ArrayPool allocated fixed-size arrays (1400-11200 bytes minimum)
            /// - Small RPC messages (10-50 bytes) wasted 95%+ memory
            /// - Pool exhaustion at ~1000 packets regardless of actual data size
            ///
            /// NEW SOLUTION:
            /// - TieredArrayPool routes requests to appropriately-sized pools
            /// - Small messages use tiny arrays (8-128 bytes)
            /// - 95% memory reduction for typical traffic patterns
            /// - 10-20x more headroom before congestion
            ///
            /// PERFORMANCE:
            /// - Zero performance penalty (inlined tier routing)
            /// - Reduced GC pressure (fewer large array allocations)
            /// - Better cache locality (smaller arrays fit in L1/L2)
            /// </summary>
            internal readonly TieredArrayPool<byte> resourcePool = new TieredArrayPool<byte>();
        }
        /// <summary>
        /// Overload that accepts an optional transport-level receive timestamp.
        /// If transportReceiveTicks is 0, captures the timestamp now (legacy behavior).
        /// If transportReceiveTicks is provided, uses it for accurate RTT calculations.
        ///
        /// <para>
        /// HIGH-LOAD OPTIMIZATION (December 2025): For Steamworks, the transport provides
        /// an accurate timestamp from <c>SteamNetworkingMessage_t.m_usecTimeReceived</c>,
        /// which is when Steam's networking layer actually received the packet (NOT when
        /// we processed it). During high-load scenarios, there can be 50-500ms between
        /// Steam receiving a packet and our code processing it.
        /// </para>
        /// </summary>
        internal static void ProcessIncomingBytes_TriageFromAnyThread(GONetConnection sourceConnection, byte[] messageBytes, int bytesUsedCount, GONetChannelId channelId, long transportReceiveTicks = 0)
        {
            //GONetLog.Debug("received something.... size: " + bytesUsedCount);

            // CRITICAL DEBUG: Log all DistributedHost messages when they first arrive from network thread
            if (channelId == GONetChannel.DistributedHost_Reliable.Id || channelId == GONetChannel.DistributedHost_Unreliable.Id)
            {
                byte msgType = bytesUsedCount > 0 ? messageBytes[0] : (byte)0;
//                GONetLog.Warning($"[Triage-ENQUEUE] DistributedHost arrived from network: channel={channelId}, size={bytesUsedCount}, " +
//                    $"msgType={msgType}, connection={sourceConnection?.GetType().Name ?? "null"}, thread={Thread.CurrentThread.ManagedThreadId}");
            }

            // DEBUG: Log ALL messages on Channel 8 immediately when received
            // NOTE: This logs every network message on chunk channel (hundreds per second)
            // To enable, add LOG_NETWORK_VERBOSE to Player Settings → Scripting Define Symbols
            #if LOG_NETWORK_VERBOSE
            if (channelId == 8 && IsClient)
            {
                GONetLog.Warning($"[CHUNK_TRACE] NETWORK ENTRY - Channel: {channelId}, Bytes: {bytesUsedCount}, Thread: {Thread.CurrentThread.ManagedThreadId}");
            }
            #endif

            SingleProducerQueues singleProducerReceiveQueues = ReturnSingleProducerResources_IfAppropriate(singleProducerReceiveQueuesByThread, Thread.CurrentThread);

            // HIGH-LOAD OPTIMIZATION (December 2025): Use transport-level receive timestamp if available
            // This is t3 for time sync - must be as accurate as possible
            // For Steamworks: Uses m_usecTimeReceived (when Steam actually received the packet)
            // For NetcodeIO: Uses processing time (no transport-level timestamp available)
            // Legacy path: Capture now if no transport timestamp provided
            long nowTicks = SecretaryOfTemporalAffairs.GetRawElapsedTicksStatic();
            long receivedAtRawTicks = (transportReceiveTicks > 0)
                ? transportReceiveTicks
                : nowTicks;

            // DIAGNOSTIC: Track timestamp usage for RTT debugging
            #if UNITY_EDITOR || DEVELOPMENT_BUILD
            GONetChannelId timeSyncChannelId = GONetChannel.TimeSync_Unreliable;
            bool isTimeSyncChannel = channelId == timeSyncChannelId;

            // Log EVERY TimeSync message to trace timestamp flow
            // COMMENTED (log cleanup) - very spammy, fires multiple times per second
            /*if (isTimeSyncChannel)
            {
                double transportSec = transportReceiveTicks / (double)TimeSpan.TicksPerSecond;
                double nowSec = nowTicks / (double)TimeSpan.TicksPerSecond;
                double usedSec = receivedAtRawTicks / (double)TimeSpan.TicksPerSecond;
                long ageMs = transportReceiveTicks > 0 ? (nowTicks - transportReceiveTicks) / TimeSpan.TicksPerMillisecond : -1;
                GONetLog.Warning($"[TimeSync-TRACE] channel={channelId} transportTs={transportSec:F3}s nowTs={nowSec:F3}s usedTs={usedSec:F3}s ageMs={ageMs} isServer={IsServer}");
            }*/

            if (transportReceiveTicks == 0)
            {
                // Fallback to current time - this inflates RTT under load
                System.Threading.Interlocked.Increment(ref _timestampFallbackCount);

                // CRITICAL: Log if TimeSync specifically is missing timestamp
                // COMMENTED (log cleanup) - can fire frequently in dev builds
                /*if (isTimeSyncChannel)
                {
                    GONetLog.Warning($"[TimeSync-TS-MISSING] TimeSync message has NO transport timestamp! Using current time = {nowTicks / (double)TimeSpan.TicksPerSecond:F3}s. This will inflate RTT!");
                }*/
            }
            else
            {
                // Using transport timestamp - good path
                System.Threading.Interlocked.Increment(ref _timestampProvidedCount);

                // Check staleness: if transport timestamp is too old, something is wrong
                long ageMs = (nowTicks - transportReceiveTicks) / TimeSpan.TicksPerMillisecond;
                if (ageMs > 500)
                {
                    GONetLog.Warning($"[RTT-DIAG] Transport timestamp is {ageMs}ms old - possible queue delay (channel={channelId}, isTimeSync={isTimeSyncChannel})");
                }

                // Log TimeSync specifically when stale
                // COMMENTED (log cleanup) - can fire frequently in dev builds under load
                /*if (isTimeSyncChannel && ageMs > 100)
                {
                    GONetLog.Warning($"[TimeSync-TS-STALE] TimeSync message timestamp is {ageMs}ms old! transport={transportReceiveTicks / (double)TimeSpan.TicksPerSecond:F3}s, now={nowTicks / (double)TimeSpan.TicksPerSecond:F3}s");
                }*/
            }
            #endif

            NetworkData networkData = new NetworkData()
            {
                relatedConnection = sourceConnection,
                messageBytes = singleProducerReceiveQueues.resourcePool.Borrow(bytesUsedCount),
                messageBytesBorrowedOnThread = Thread.CurrentThread,
                bytesUsedCount = bytesUsedCount,
                channelId = channelId,
                receivedAtRawTicks = receivedAtRawTicks
            };

            Buffer.BlockCopy(messageBytes, 0, networkData.messageBytes, 0, bytesUsedCount);

            // DIAGNOSTIC (December 2025): Log ENQUEUE for spawn-sized messages to trace message loss
            // To enable, add GONet_SPAWN_TRACE to Player Settings → Scripting Define Symbols
            #if GONet_SPAWN_TRACE
            if (bytesUsedCount >= 75 && bytesUsedCount <= 115)
            {
                // Extract GONetId from the COPIED data (offset 4 for decompressed spawn message)
                uint copiedGONetId = 0;
                if (bytesUsedCount >= 8)
                {
                    copiedGONetId = (uint)(
                        networkData.messageBytes[4] |
                        (networkData.messageBytes[5] << 8) |
                        (networkData.messageBytes[6] << 16) |
                        (networkData.messageBytes[7] << 24)
                    );
                }
                string firstBytesHex = bytesUsedCount >= 12
                    ? System.BitConverter.ToString(networkData.messageBytes, 0, 12).Replace("-", "")
                    : System.BitConverter.ToString(networkData.messageBytes, 0, bytesUsedCount).Replace("-", "");

                int queueCountBefore = singleProducerReceiveQueues.queueForWork.Count;
                GONetLog.Debug($"[ENQUEUE-PRE] Spawn-sized message: bytes={bytesUsedCount}, GONetId={copiedGONetId}, channel={channelId}, queueCount={queueCountBefore}, firstBytes={firstBytesHex}");
            }
            #endif

            singleProducerReceiveQueues.queueForWork.Enqueue(networkData);

            // DIAGNOSTIC (December 2025): Log AFTER enqueue to verify item was added
            // To enable, add GONet_SPAWN_TRACE to Player Settings → Scripting Define Symbols
            #if GONet_SPAWN_TRACE
            if (bytesUsedCount >= 75 && bytesUsedCount <= 115)
            {
                int queueCountAfter = singleProducerReceiveQueues.queueForWork.Count;
                GONetLog.Debug($"[ENQUEUE-POST] queueCount={queueCountAfter}");
            }
            #endif

            // DEBUG: Confirm enqueue
            // NOTE: This logs every network message enqueue (hundreds per second)
            // To enable, add LOG_NETWORK_VERBOSE to Player Settings → Scripting Define Symbols
            #if LOG_NETWORK_VERBOSE
            if (channelId == 8 && IsClient)
            {
                GONetLog.Warning($"[CHUNK_TRACE] ENQUEUED - Channel: {channelId}, Bytes: {bytesUsedCount}, QueueCount: {singleProducerReceiveQueues.queueForWork.Count}");
            }
            #endif
        }

        #region private methods

        /// <summary>
        /// CRITICAL FIX (December 2025): Process ONLY DistributedHost channel messages.
        /// Called EARLY in frame (priority -32000) BEFORE GONetGossipIntegration.Update().
        /// This ensures SessionPromote and other failover messages are processed immediately
        /// when they arrive, rather than waiting until end-of-frame which causes split-brain.
        /// </summary>
        private static void ProcessIncomingBytes_DistributedHostOnly_MainThread()
        {
            using (var enumerator = singleProducerReceiveQueuesByThread.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    SingleProducerQueues singleProducerReceiveQueues = enumerator.Current.Value;
                    ConcurrentQueue<NetworkData> incomingNetworkData = singleProducerReceiveQueues.queueForWork;

                    // Use a temporary list to hold non-DistributedHost messages for re-enqueueing
                    // We need to peek at messages without losing them if they're not DistributedHost
                    int readyCount = incomingNetworkData.Count;
                    if (readyCount == 0) continue;

                    // PERF: Allocate temp list only when needed (thread-local would be better but this is fine for now)
                    List<NetworkData> requeue = null;

                    for (int i = 0; i < readyCount && incomingNetworkData.TryDequeue(out NetworkData networkData); i++)
                    {
                        bool isDistributedHostChannel =
                            networkData.channelId == GONetChannel.DistributedHost_Reliable.Id ||
                            networkData.channelId == GONetChannel.DistributedHost_Unreliable.Id;

                        if (isDistributedHostChannel)
                        {
                            // Process immediately
                            byte msgType = networkData.bytesUsedCount > 0 ? networkData.messageBytes[0] : (byte)0;
//                            GONetLog.Warning($"[DistHost-EARLY] Processing DistributedHost early: channel={networkData.channelId}, " +
//                                $"size={networkData.bytesUsedCount}, msgType={msgType}");

                            ProcessIncomingBytes_QueuedNetworkData_MainThread_INTERNAL(networkData);
                        }
                        else
                        {
                            // Not DistributedHost - save for later re-enqueueing
                            if (requeue == null) requeue = new List<NetworkData>();
                            requeue.Add(networkData);
                        }
                    }

                    // Re-enqueue non-DistributedHost messages back to the queue
                    if (requeue != null)
                    {
                        for (int i = 0; i < requeue.Count; i++)
                        {
                            incomingNetworkData.Enqueue(requeue[i]);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// This is where ***all*** incoming message are run through the handling/processing logic.
        /// Call this from the main Unity thread!
        /// </summary>
        private static void ProcessIncomingBytes_QueuedNetworkData_MainThread()
        {
            // CPU TIME-BOXING: Get CPU budget configuration (applied PER-QUEUE, not cumulative)
            float cpuBudgetMs = GONetGlobal.Instance != null ? GONetGlobal.Instance.queueProcessingCpuBudgetMs : 0f;
            bool isCpuBudgetEnabled = cpuBudgetMs > 0f;

            using (var enumerator = singleProducerReceiveQueuesByThread.GetEnumerator())
            {
                int threadQueueIndex = 0;
                while (enumerator.MoveNext())
                {
                    threadQueueIndex++;
                    SingleProducerQueues singleProducerReceiveQueues = enumerator.Current.Value;
                    ConcurrentQueue<NetworkData> incomingNetworkData = singleProducerReceiveQueues.queueForWork;
                    NetworkData networkData;
                    int readyCount = incomingNetworkData.Count;

                    // CRITICAL FIX (November 2025): Use static stopwatch with Restart() to avoid 40-byte allocation per queue
                    // Previous bug: Stopwatch.StartNew() allocated 40 bytes per queue per frame
                    // Each queue gets independent 2.5ms CPU budget (reset via Restart())
                    if (isCpuBudgetEnabled)
                    {
                        _cpuBudgetStopwatch.Restart();
                    }

                    // RECEIVE-SIDE TEMPORAL THINNING: CHECK QUEUE COUNT TRIGGER
                    // Trigger 1: Queue count exceeds threshold (burst traffic protection)
                    // Check this BEFORE processing to prevent queue explosion
                    bool shouldThinByCount = readyCount > GONetGlobal.Instance.receiveQueueThinningTriggerCount;

                    if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableTemporalThinning && shouldThinByCount)
                    {
                        // Calculate congestion severity for adaptive thinning
                        double countOverage = (double)readyCount / GONetGlobal.Instance.receiveQueueThinningTriggerCount;

                        // COMMENTED OUT (Dec 2025): This diagnostic caused 900+ warnings during handoff, killing frame rate
                        //GONetLog.Warning($"[COUNT-TRIGGER] Queue #{threadQueueIndex} has {readyCount} messages (threshold: {GONetGlobal.Instance.receiveQueueThinningTriggerCount}, overage: {countOverage:F1}x) - thinning before processing");
                        ThinReceiveQueue(incomingNetworkData, singleProducerReceiveQueues, countOverage);
                        readyCount = incomingNetworkData.Count; // Update count after thinning
                    }

                    // RECEIVE-SIDE RELIABLE MESSAGE FRAME SPREADING: CHECK QUEUE COUNT TRIGGER
                    // IMPORTANT: This runs AFTER temporal thinning (which removes unreliable messages)
                    // Frame spreading applies to remaining messages (mostly reliable after thinning)
                    // PURPOSE: Prevent Unity main thread stutter by spreading processing across frames
                    int processingLimit = readyCount; // Default: process all messages
                    bool shouldSpreadByCount = GONetGlobal.Instance != null
                        && GONetGlobal.Instance.frameSpreadingSettings.enableReliableFrameSpreading
                        && readyCount > GONetGlobal.Instance.frameSpreadingSettings.reliableProcessingThreshold;

                    if (shouldSpreadByCount)
                    {
                        // Calculate congestion severity for adaptive frame spreading
                        double countOverage = (double)readyCount / GONetGlobal.Instance.frameSpreadingSettings.reliableProcessingThreshold;
                        processingLimit = CalculateReliableProcessingLimit(countOverage);

                        if (GONetGlobal.Instance.frameSpreadingSettings.enableFrameSpreadingLogging)
                        {
                            GONetLog.Warning($"[RECV-SPREAD] Main thread queue #{threadQueueIndex} has {readyCount} messages, spreading across frames: processing {processingLimit}/{readyCount} this frame (trigger: COUNT, severity: {countOverage:F1}x)");
                        }
                    }

                    // DIAGNOSTIC: Log queue backup (only when queue > 10 to avoid spam)
                    // COMMENTED OUT (Dec 2025): This diagnostic caused 1,300+ warnings during handoff, killing frame rate
                    //if (readyCount > 50 && IsClient) // Raised threshold from 10 to 50 (November 2025) to reduce log spam
                    //{
                    //    GONetLog.Warning($"[QUEUE-BACKUP] Thread queue #{threadQueueIndex} has {readyCount} messages ready (potential processing bottleneck)");
                    //}

                    // DEBUG: Log queue stats
                    // NOTE: This logs every frame when messages are ready (60+ times per second)
                    // To enable, add LOG_NETWORK_VERBOSE to Player Settings → Scripting Define Symbols
                    #if LOG_NETWORK_VERBOSE
                    if (readyCount > 0 && IsClient)
                    {
                        GONetLog.Info($"[DEBUG] Processing thread queue #{threadQueueIndex} - {readyCount} messages ready, processingLimit: {processingLimit}");
                    }
                    #endif
                    int processedCount = 0;
                    while (processedCount < processingLimit && processedCount < readyCount && incomingNetworkData.TryDequeue(out networkData))
                    {
                        ++processedCount;

                        // DIAGNOSTIC (December 2025): Log DEQUEUE for spawn-sized messages to trace message loss
                        // To enable, add GONet_SPAWN_TRACE to Player Settings → Scripting Define Symbols
                        #if GONet_SPAWN_TRACE
                        if (networkData.bytesUsedCount >= 75 && networkData.bytesUsedCount <= 115)
                        {
                            // Extract GONetId from dequeued message
                            uint dequeuedGONetId = 0;
                            if (networkData.bytesUsedCount >= 8)
                            {
                                dequeuedGONetId = (uint)(
                                    networkData.messageBytes[4] |
                                    (networkData.messageBytes[5] << 8) |
                                    (networkData.messageBytes[6] << 16) |
                                    (networkData.messageBytes[7] << 24)
                                );
                            }
                            string firstBytesHex = networkData.bytesUsedCount >= 12
                                ? System.BitConverter.ToString(networkData.messageBytes, 0, 12).Replace("-", "")
                                : System.BitConverter.ToString(networkData.messageBytes, 0, networkData.bytesUsedCount).Replace("-", "");

                            GONetLog.Debug($"[DEQUEUE] Spawn-sized message: bytes={networkData.bytesUsedCount}, GONetId={dequeuedGONetId}, channel={networkData.channelId}, processedCount={processedCount}/{readyCount}, firstBytes={firstBytesHex}");
                        }
                        #endif

                        // RECEIVE-SIDE TEMPORAL THINNING + FRAME SPREADING: CHECK CPU BUDGET PERIODICALLY DURING PROCESSING
                        // Trigger 2: CPU time exceeds budget (frame stutter protection)
                        // Check every 10 messages to avoid overhead of constant stopwatch checks
                        // CRITICAL FIX (December 2025): Handle current dequeued message properly based on reliability
                        // - Unreliable: Can be dropped (return buffer to pool, break)
                        // - Reliable: Must be processed, then break to defer remaining to next frame
                        bool shouldBreakAfterCurrentMessage = false;
                        if (isCpuBudgetEnabled && processedCount % 10 == 0)
                        {
                            double elapsedMs = _cpuBudgetStopwatch.Elapsed.TotalMilliseconds;
                            if (elapsedMs > cpuBudgetMs)
                            {
                                // Check if current dequeued message is reliable
                                GONetChannel currentChannel = GONetChannel.ById(networkData.channelId);
                                bool currentMessageIsReliable = currentChannel.QualityOfService == QosType.Reliable;

                                // CPU budget exceeded - FIRST thin unreliable, THEN spread reliable
                                // STEP 1: Thin unreliable messages (lossy - drops unreliable, keeps reliable)
                                if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableTemporalThinning)
                                {
                                    int remainingCount = incomingNetworkData.Count;

                                    // Calculate congestion severity for adaptive thinning
                                    double cpuOverage = elapsedMs / cpuBudgetMs;

                                    if (GONetGlobal.Instance.enableCongestionLogging)
                                    {
                                        GONetLog.Warning($"[CPU-TRIGGER] Queue #{threadQueueIndex} processing exceeded budget after {processedCount} messages: {elapsedMs:F2}ms > {cpuBudgetMs}ms (overage: {cpuOverage:F1}x, thinning {remainingCount} remaining)");
                                    }
                                    ThinReceiveQueue(incomingNetworkData, singleProducerReceiveQueues, cpuOverage);
                                    readyCount = incomingNetworkData.Count; // Update readyCount after thinning
                                }

                                // STEP 2: Apply frame spreading to remaining messages (lossless - defers to next frame)
                                // After thinning, queue may still have reliable messages that need spreading
                                if (GONetGlobal.Instance != null && GONetGlobal.Instance.frameSpreadingSettings.enableReliableFrameSpreading)
                                {
                                    int remainingCount = incomingNetworkData.Count;
                                    if (remainingCount > 0)
                                    {
                                        // Calculate congestion severity for adaptive frame spreading
                                        double cpuOverage = elapsedMs / cpuBudgetMs;
                                        int adaptiveLimit = CalculateReliableProcessingLimit(cpuOverage);

                                        // Set processing limit to current progress + adaptive limit
                                        // Example: Processed 120, adaptive says 50 more, new limit = 170
                                        int newLimit = processedCount + adaptiveLimit;
                                        processingLimit = Math.Min(processingLimit, newLimit);

                                        if (GONetGlobal.Instance.frameSpreadingSettings.enableFrameSpreadingLogging)
                                        {
                                            int willDefer = readyCount - processingLimit;
                                            GONetLog.Warning($"[RECV-SPREAD-CPU] Main thread queue #{threadQueueIndex} CPU budget exceeded after {processedCount} messages ({elapsedMs:F2}ms > {cpuBudgetMs}ms), limiting to {processingLimit} total this frame (trigger: CPU, severity: {cpuOverage:F1}x, {remainingCount} remain in queue, will defer {willDefer} to next frame)");
                                        }
                                    }
                                }

                                // CRITICAL FIX (December 2025): Handle current dequeued message based on reliability
                                if (currentMessageIsReliable)
                                {
                                    // Reliable message: MUST process it, then break to defer remaining to next frame
                                    shouldBreakAfterCurrentMessage = true;
                                }
                                else
                                {
                                    // Unreliable message: Can be dropped - return buffer to pool and break
                                    // We break here because CPU budget is exceeded and remaining reliable messages
                                    // have already been preserved in the queue by ThinReceiveQueue (will process next frame)
                                    // Return the byte[] to the proper pool on the proper thread
                                    singleProducerReceiveQueues.queueForPostWorkResourceReturn.Enqueue(networkData);
                                    break;
                                }
                            }
                        }

                        // DEBUG: Log EVERY dequeued message on Channel 8 (chunk channel)
                        // NOTE: This logs every dequeued network message (hundreds per second)
                        // To enable, add LOG_NETWORK_VERBOSE to Player Settings → Scripting Define Symbols
                        #if LOG_NETWORK_VERBOSE
                        if (networkData.channelId == 8)
                        {
                            GONetLog.Warning($"[CHUNK_TRACE] DEQUEUED - Channel: {networkData.channelId}, Bytes: {networkData.bytesUsedCount}, ProcessedCount: {processedCount}/{readyCount}");
                        }

                        // DEBUG: Log EVERY dequeued message on Channel 9
                        if (networkData.channelId == 9)
                        {
                            GONetLog.Info($"[DEBUG] DEQUEUED - Channel: {networkData.channelId}, Bytes: {networkData.bytesUsedCount}, ProcessedCount: {processedCount}/{readyCount}");
                        }
                        #endif

                        try
                        {
                            // DEBUG: Track entry to try block for Channel 8
                            // NOTE: This logs every message processing (hundreds per second)
                            // To enable, add LOG_NETWORK_VERBOSE to Player Settings → Scripting Define Symbols
                            #if LOG_NETWORK_VERBOSE
                            if (networkData.channelId == 8)
                            {
                                GONetLog.Error($"[CHUNK_TRACE] ENTERED TRY BLOCK - Channel: {networkData.channelId}, Bytes: {networkData.bytesUsedCount}");
                            }
                            #endif

                            // IMPORTANT: This check must come first as it exits early if condition met!
                            // HOST MODE FIX (December 2025): Do NOT apply client-init queueing to messages from REMOTE clients.
                            // In HOST mode, IsClient=true AND IsServer=true. Before the HOST's loopback client is initialized,
                            // this check would incorrectly queue events from remote clients (like their GONetLocal spawn).
                            // But the remote client's GONetLocal spawn is what triggers Server_OnNewClientInstantiatedItsGONetLocal,
                            // which sets the remote client's IsInitializedWithServer=true. Blocking it creates a deadlock.
                            // Solution: Only apply this queue logic for messages from the server (OwnerAuthorityId_Server).
                            bool isMessageFromServer = networkData.relatedConnection.OwnerAuthorityId == OwnerAuthorityId_Server;
                            bool isDistributedHostChannel =
                                networkData.channelId == GONetChannel.DistributedHost_Reliable.Id ||
                                networkData.channelId == GONetChannel.DistributedHost_Unreliable.Id;
                            bool shouldQueueForProcessingAfterInitialization =
                                !isDistributedHostChannel &&
                                isMessageFromServer &&
                                !IsChannelClientInitializationRelated(networkData.channelId) &&
                                IsClient &&
                                _gonetClient != null &&
                                !_gonetClient.IsInitializedWithServer;

                            if (IsClient)
                            {
                                bool isInitRelated = IsChannelClientInitializationRelated(networkData.channelId);
                                bool isInitialized = _gonetClient != null && _gonetClient.IsInitializedWithServer;

                                // DEBUG: Log channel 8 queueing decision
                                // NOTE: This logs every message queueing decision (hundreds per second)
                                // To enable, add LOG_NETWORK_VERBOSE to Player Settings → Scripting Define Symbols
                                #if LOG_NETWORK_VERBOSE
                                if (networkData.channelId == 8)
                                {
                                    GONetLog.Warning($"[CHUNK_TRACE] QUEUEING DECISION - Channel: {networkData.channelId}, IsInitRelated: {isInitRelated}, IsInitialized: {isInitialized}, WillQueue: {shouldQueueForProcessingAfterInitialization}");
                                }
                                #endif

                                //GONetLog.Debug($"[MSG] Received message - channel: {networkData.channelId}, size: {networkData.bytesUsedCount}, isInitRelated: {isInitRelated}, isInitialized: {isInitialized}, willQueue: {shouldQueueForProcessingAfterInitialization}");
                            }

                            if (shouldQueueForProcessingAfterInitialization)
                            {
                                // Try to identify the message type being queued
                                string messageInfo = "unknown";
                                try
                                {
                                    if (GONetChannel.IsGONetCoreChannel(networkData.channelId) && networkData.bytesUsedCount >= 4)
                                    {
                                        using (var tempStream = BitByBitByteArrayBuilder.GetBuilder_WithNewData(networkData.messageBytes, networkData.bytesUsedCount))
                                        {
                                            uint msgID;
                                            tempStream.ReadUInt(out msgID);
                                            if (messageTypeByMessageIDMap.TryGetValue(msgID, out Type msgType))
                                            {
                                                messageInfo = msgType.Name;
                                            }
                                        }
                                    }
                                }
                                catch { }

                                // NOTE: This logs every deferred message (can be hundreds during connection)
                                // To enable, add LOG_NETWORK_VERBOSE to Player Settings → Scripting Define Symbols
                                #if LOG_NETWORK_VERBOSE
                                GONetLog.Warning($"[MSG] QUEUING message for later (client not initialized yet) - Channel: {networkData.channelId}, MessageType: {messageInfo}, IsInitRelated: {IsChannelClientInitializationRelated(networkData.channelId)}");
                                #endif
                                GONetClient.incomingNetworkData_mustProcessAfterClientInitialized.Enqueue(networkData);
                                // NOTE: We intentionally DON'T return the byte array to pool here - it's queued for later processing
                                // The byte array will be returned when the queued message is eventually processed
                                continue;
                            }
                        }
                        catch (Exception e)
                        {
                            GONetLog.Error(string.Concat("Error Message: ", e.Message, "\nError Stacktrace:\n", e.StackTrace));
                        }

                        // DEBUG: Log channel 8 before calling INTERNAL
                        // NOTE: This logs every message processing (hundreds per second)
                        // To enable, add LOG_NETWORK_VERBOSE to Player Settings → Scripting Define Symbols
                        #if LOG_NETWORK_VERBOSE
                        if (networkData.channelId == 8)
                        {
                            GONetLog.Warning($"[CHUNK_TRACE] CALLING INTERNAL - Channel: {networkData.channelId}, Bytes: {networkData.bytesUsedCount}");
                        }
                        #endif

                        ProcessIncomingBytes_QueuedNetworkData_MainThread_INTERNAL(networkData);

                        // CRITICAL FIX (December 2025): NOW check if we should break after processing
                        // This ensures the current message is fully processed before exiting the loop
                        if (shouldBreakAfterCurrentMessage)
                        {
                            break;
                        }
                    }

                    // FRAME SPREADING: Log deferred messages when spreading was active
                    if (GONetGlobal.Instance != null &&
                        GONetGlobal.Instance.frameSpreadingSettings.enableFrameSpreadingLogging &&
                        processedCount < readyCount)
                    {
                        int deferred = readyCount - processedCount;
                        GONetLog.Info($"[RECV-SPREAD] Deferred {deferred} messages to next frame (processed {processedCount}/{readyCount}, limit: {processingLimit})");
                    }
                }
            }
        }

        public delegate void CustomChannelPayloadHandler(GONetChannelId channelId, GONetConnection relatedConnection, byte[] messageBytes, int bytesUsedCount);
        public static event CustomChannelPayloadHandler OnCustomChannelPayloadReceived;

        /// <summary>
        /// POST: <paramref name="networkData"/> is returned to the associated/proper queue in <see cref="singleProducerSendQueuesByThread"/>
        /// </summary>
        private static void ProcessIncomingBytes_QueuedNetworkData_MainThread_INTERNAL(NetworkData networkData, bool isProcessingFromQueue = false)
        {
            // DIAGNOSTIC: Track incoming packet processing by channel
            // Added 2025-10-11 to investigate packet saturation during rapid spawning
            GONetChannel channel = GONetChannel.ById(networkData.channelId);
            bool isReliable = channel.QualityOfService == QosType.Reliable;
            IncrementIncomingPacketCounter(isReliable);

            // DEBUG: Log EVERY message that enters this function on Channel 8
            // NOTE: This logs every message processing (hundreds per second)
            // To enable, add LOG_NETWORK_VERBOSE to Player Settings → Scripting Define Symbols
            #if LOG_NETWORK_VERBOSE
            if (networkData.channelId == 8) // ClientInitialization_EventSingles_Reliable
            {
                GONetLog.Warning($"[CHUNK_TRACE] INTERNAL ENTRY - Channel: {networkData.channelId}, Bytes: {networkData.bytesUsedCount}, isProcessingFromQueue: {isProcessingFromQueue}");
            }

            // DEBUG: Log EVERY message that enters this function
            if (networkData.channelId == 9) // ClientInitialization_CustomSerialization_Reliable
            {
                GONetLog.Info($"[DEBUG] ProcessIncomingBytes ENTRY - Channel: {networkData.channelId}, Bytes: {networkData.bytesUsedCount}, isProcessingFromQueue: {isProcessingFromQueue}, _gonetClient null: {_gonetClient == null}, IsClient: {IsClient}");
            }
            #endif

            // INIT MESSAGE TRACKING: Count received init messages for acknowledgment
            // Added 2025-11-06 to detect Steamworks reliable message delivery failures
            // See: .claude/STEAMWORKS_INIT_MESSAGE_DELIVERY_SOLUTION.md
            // IMPORTANT: Only track RELIABLE init messages (TimeSync_Unreliable excluded - drops expected)
            // IMPORTANT: Stop tracking after acknowledgment is sent (prevents continual tracking)
            // IMPORTANT: Don't count reprocessed messages from deferred queues (prevents double-counting)
            if (IsClient && !isProcessingFromQueue && GONetChannel.IsChannelTrackedForInitValidation(networkData.channelId) && _gonetClient != null && !_gonetClient.hasAcknowledgedInitMessages)
            {
                lock (_gonetClient.receivedInitMessageChannels)
                {
                    if (!_gonetClient.receivedInitMessageChannels.ContainsKey(networkData.channelId))
                    {
                        _gonetClient.receivedInitMessageChannels[networkData.channelId] = 0;
                    }
                    _gonetClient.receivedInitMessageChannels[networkData.channelId]++;
#if GONet_INIT_TRACE
                    GONetLog.Debug($"[InitMsgTracker] CLIENT: Received init message on channel {networkData.channelId} (total on this channel: {_gonetClient.receivedInitMessageChannels[networkData.channelId]})");
#endif
                }
            }

            bool shouldReturnToPool = true; // Track whether message should be returned to pool (false if queued elsewhere)
            try
            {
                if (networkData.channelId == GONetChannel.ClientInitialization_EventSingles_Reliable.Id || networkData.channelId == GONetChannel.EventSingles_Reliable.Id || networkData.channelId == GONetChannel.EventSingles_Unreliable.Id)
                {
                    // COMPREHENSIVE LOGGING - Receive EventSingle
                    LogMessageReceive(networkData.messageBytes, networkData.bytesUsedCount, networkData.channelId, networkData.relatedConnection, 0);

                    //if (networkData.channelId == GONetChannel.ClientInitialization_EventSingles_Reliable.Id)
                    //{
                        //GONetLog.Warning($"[SPAWN_SYNC] CLIENT: Received message on ClientInitialization_EventSingles_Reliable - Size: {networkData.bytesUsedCount} bytes, From: AuthorityId {networkData.relatedConnection.OwnerAuthorityId}");
                    //}

                    DeserializeBody_EventSingle(networkData.messageBytes, networkData.bytesUsedCount, networkData.relatedConnection);
                }
                else if (GONetChannel.IsGONetCoreChannel(networkData.channelId))
                {
                    // IMPORTANT: Extract elapsedTicksAtSend for logging BEFORE creating the main processing bitStream
                    // because LogMessageReceive uses the same thread-local builder and would reset the position!
                    long elapsedTicksAtSend = 0;
                    if (networkData.bytesUsedCount >= 12) // Ensure we have at least messageID (4) + timestamp (8)
                    {
                        using (var tempStream = BitByBitByteArrayBuilder.GetBuilder_WithNewData(networkData.messageBytes, networkData.bytesUsedCount))
                        {
                            uint tempMsgId;
                            tempStream.ReadUInt(out tempMsgId);
                            tempStream.ReadLong(out elapsedTicksAtSend);
                        }
                    }

                    // COMPREHENSIVE LOGGING - Receive GONet core channel message (BEFORE main processing to avoid position reset)
                    LogMessageReceive(networkData.messageBytes, networkData.bytesUsedCount, networkData.channelId, networkData.relatedConnection, elapsedTicksAtSend);

                    using (var bitStream = BitByBitByteArrayBuilder.GetBuilder_WithNewData(networkData.messageBytes, networkData.bytesUsedCount))
                    {
                        Type messageType;
                        ////////////////////////////////////////////////////////////////////////////
                        // header...just message type/id...well, now it is send time too
                        uint messageID;
                        bitStream.ReadUInt(out messageID);

                        if (!messageTypeByMessageIDMap.TryGetValue(messageID, out messageType))
                        {
                            GONetLog.Error($"[GONet] Unknown messageID {messageID} in messageTypeByMessageIDMap (count: {messageTypeByMessageIDMap.Count}). Channel: {networkData.channelId}");
                            return;
                        }

                        bitStream.ReadLong(out elapsedTicksAtSend);

                        // VELOCITY-AUGMENTED SYNC: Read velocity bit for VALUE/VELOCITY bundle type
                        // This bit determines whether all values in this bundle are serialized as velocities or values
                        // CRITICAL: Must be read BEFORE DeserializeBody_BundleOfChoice to stay in sync with serialization
                        bool isVelocityBundle = false;
                        if (messageType == typeof(AutoMagicalSync_ValueChanges_Message) ||
                            messageType == typeof(AutoMagicalSync_ValuesNowAtRest_Message))
                        {
                            bitStream.ReadBit(out isVelocityBundle);
                            // if (isVelocityBundle)
                            // {
                            //     GONetLog.Debug("[SoA-DIAG] Received VELOCITY bundle (isVelocityBundle=true)");
                            // }
                        }

                        // CLIENT SAFETY: Defer sync bundles until scene-defined GONetIds are assigned.
                        bool shouldDeferForGONetIds = !isProcessingFromQueue && ShouldDeferSyncBundlesForGONetIdSync();
                        if (shouldDeferForGONetIds)
                        {
                            bool requiresSceneGONetIds =
                                messageType == typeof(AutoMagicalSync_ValueChanges_Message) ||
                                messageType == typeof(AutoMagicalSync_ValuesNowAtRest_Message) ||
                                messageType == typeof(AutoMagicalSync_AllCurrentValues_Message);

                            if (requiresSceneGONetIds)
                            {
                                if (isReliable)
                                {
                                    if (_gonetClient.incomingNetworkData_waitingForGONetIds.Count < GONetClient.MAX_GONETID_QUEUE_SIZE)
                                    {
                                        _gonetClient.incomingNetworkData_waitingForGONetIds.Enqueue(networkData);
                                        //GONetLog.Debug($"[GONETID-QUEUE] Queued sync bundle (message: {messageType.Name}, channel: {networkData.channelId}) waiting for GONetId assignment. Queue size: {_gonetClient.incomingNetworkData_waitingForGONetIds.Count}"); // COMMENTED - spammy log (log cleanup)
                                        shouldReturnToPool = false;
                                    }
                                    else
                                    {
                                        GONetLog.Error($"[GONETID-QUEUE] Queue full ({GONetClient.MAX_GONETID_QUEUE_SIZE} messages)! Dropping oldest message. This indicates a problem with GONetId synchronization.");
                                        NetworkData droppedMessage = _gonetClient.incomingNetworkData_waitingForGONetIds.Dequeue();

                                        SingleProducerQueues droppedQueues = singleProducerReceiveQueuesByThread[droppedMessage.messageBytesBorrowedOnThread];
                                        droppedQueues.queueForPostWorkResourceReturn.Enqueue(droppedMessage);

                                        _gonetClient.incomingNetworkData_waitingForGONetIds.Enqueue(networkData);
                                        shouldReturnToPool = false;
                                    }
                                }

                                // Unreliable bundles are dropped while waiting for GONetIds.
                                return;
                            }
                        }

                        // DEBUG: Log position after reading header for OwnerAuthorityIdAssignmentEvent
                        //if (messageType == typeof(OwnerAuthorityIdAssignmentEvent))
                        //{
                            //GONetLog.Info($"[INIT] CLIENT: After reading header - MessageID: {messageID}, ElapsedTicks: {elapsedTicksAtSend}, BitStream Position: {bitStream.Position_Bytes} bytes {bitStream.Position_Bits} bits");
                        //}
                        ////////////////////////////////////////////////////////////////////////////

                        // DEBUG: Log every message type received
                        //if (messageType == typeof(OwnerAuthorityIdAssignmentEvent) || messageType == typeof(ServerSaysClientInitializationCompletion))
                        //{
                            //GONetLog.Info($"[INIT] Received {messageType.Name} - MessageID: {messageID}, Channel: {networkData.channelId}, IsServer: {IsServer}, MyAuthorityId: {MyAuthorityId}");
                        //}

                        //GONetLog.Debug($"received something....networkData.bytesUsedCount: {networkData.bytesUsedCount}, messageType: {messageType.Name}, IsServer? {IsServer} (isServerOverride: {isServerOverride}, MyAuthorityId: {MyAuthorityId}/Server: {OwnerAuthorityId_Server}), IsClient? {IsClient}");

                        {  // body:
                            if (messageType == typeof(AutoMagicalSync_ValueChanges_Message) ||
                                messageType == typeof(AutoMagicalSync_ValuesNowAtRest_Message))
                            {
                                // DIAGNOSTIC: Count sync packets received
                                System.Threading.Interlocked.Increment(ref _diagnosticSyncPacketsReceived_Total);
                                bool isReliableChannel = GONetChannel.ById(networkData.channelId).QualityOfService == QosType.Reliable;
                                if (isReliableChannel)
                                {
                                    System.Threading.Interlocked.Increment(ref _diagnosticSyncPacketsReceived_Reliable);
                                }
                                else
                                {
                                    System.Threading.Interlocked.Increment(ref _diagnosticSyncPacketsReceived_Unreliable);
                                }

                                // DIAGNOSTIC: Log packet rates every 5 seconds
                                // COMMENTED (log cleanup) - fires every 5 seconds per client, too spammy
                                /*long currentTime = _diagnosticSyncPacketTimer.ElapsedMilliseconds;
                                if (IsClient && currentTime - _diagnosticLastLogTime_Ms > 5000)
                                {
                                    long elapsed = currentTime - _diagnosticLastLogTime_Ms;
                                    double totalRate = (_diagnosticSyncPacketsReceived_Total * 1000.0) / elapsed;
                                    double unreliableRate = (_diagnosticSyncPacketsReceived_Unreliable * 1000.0) / elapsed;
                                    double reliableRate = (_diagnosticSyncPacketsReceived_Reliable * 1000.0) / elapsed;

                                    // HANDOFF-DIAG: Track client-side sync reception for post-handoff debugging
                                    GONetLog.Warning($"[SYNC-PACKET-RATE] Total: {_diagnosticSyncPacketsReceived_Total} ({totalRate:F1}/sec), " +
                                                     $"Unreliable: {_diagnosticSyncPacketsReceived_Unreliable} ({unreliableRate:F1}/sec), " +
                                                     $"Reliable: {_diagnosticSyncPacketsReceived_Reliable} ({reliableRate:F1}/sec)");

                                    // Reset counters for next interval
                                    System.Threading.Interlocked.Exchange(ref _diagnosticSyncPacketsReceived_Total, 0);
                                    System.Threading.Interlocked.Exchange(ref _diagnosticSyncPacketsReceived_Unreliable, 0);
                                    System.Threading.Interlocked.Exchange(ref _diagnosticSyncPacketsReceived_Reliable, 0);
                                    _diagnosticLastLogTime_Ms = currentTime;
                                }*/

                                try
                                {
                                    DeserializeBody_BundleOfChoice(bitStream, networkData.relatedConnection, networkData.channelId, elapsedTicksAtSend, messageType, isVelocityBundle);
                                    if (IsServer)
                                    {
                                        /*
                                         * When dealing with client -> server -> client experience, which is to say the server needs to re broadcast this "values now at rest bundle"
                                         * since we piggy backed this "at rest" impl off of the value change impl where the re broadcast pretty much happens automatically through the
                                         * changed value, but things are a little different for "at rest" seeing as how the server receiving the initiating client's "at rest" message
                                         * could already have that same "at rest" value as its latest in the buffer prior to receiving the "at rest" message when it clears out the buffer
                                         * except for the at rest value and that means the server would not realize or have a mechanism to turn around and send "at rest" to other clients,
                                         * which is the remaining issue in long drawn out Shaun speak.
                                         */
                                        Server_SendBytesToNonSourceClients(networkData.messageBytes, networkData.bytesUsedCount, networkData.relatedConnection, networkData.channelId);
                                    }
                                }
                                catch (GONetParticipantNotReadyException notReadyEx)
                                {
                                    // Participant missing or Awake incomplete - handle based on channel quality and config
                                    QosType channelQuality = GONetChannel.ById(networkData.channelId).QualityOfService;

                                    // DESPAWN STALE-BUNDLE GUARD (Dec 2025):
                                    // If the missing participant is already despawn-tombstoned, this bundle will NEVER become processable.
                                    // Drop it instead of deferring/re-deferring, otherwise the GONetReady queue can fill with stale bundles after failover.
                                    if (TryConsumeDespawnTombstone(notReadyEx.GONetId))
                                    {
                                        if (isProcessingFromQueue)
                                        {
                                            //GONetLog.Debug($"[GONETREADY-QUEUE] Dropping {channelQuality} deferred sync bundle - participant {notReadyEx.GONetId} has despawn tombstone (stale after despawn)");
                                        }
                                    }
                                    else if (isProcessingFromQueue)
                                    {
                                        // Processing a previously-deferred bundle and the participant is STILL not ready.
                                        // Re-defer (bounded by queue size + timeout) instead of dropping, because a single bundle can
                                        // reference multiple newly-spawned participants that may become ready over multiple frames.
                                        if (GONetGlobal.Instance.deferSyncBundlesWaitingForGONetReady)
                                        {
                                            DeferSyncBundleWaitingForGONetReady(networkData, elapsedTicksAtSend, messageType);
                                            shouldReturnToPool = false; // Queue owns byte array now

                                            //GONetLog.Debug($"[GONETREADY-QUEUE] Re-deferred {channelQuality} sync bundle - participant {notReadyEx.GONetId} still missing/not ready. " +
//                                                          $"QueueSize: {incomingNetworkData_waitingForGONetReady.Count}, " +
//                                                          $"Timeout: {GONetGlobal.Instance.maxSecondsToWaitForMissingParticipant}s");
                                        }
                                        else
                                        {
                                            // Deferral disabled - drop after retry
                                            GONetLog.Error($"[GONETREADY-QUEUE] Sync bundle still has missing/unready participant (GONetId: {notReadyEx.GONetId}) after retry. " +
                                                          $"Channel: {networkData.channelId}. Dropping bundle (deferral disabled).");
                                            // Falls through to pool return (shouldReturnToPool=true)
                                        }
                                    }
                                    else if (GONetGlobal.Instance.deferSyncBundlesWaitingForGONetReady)
                                    {
                                        // IMPROVED: Defer for BOTH reliable AND unreliable when enabled
                                        // Handles race condition: sync bundles arriving before spawn message completes under load
                                        DeferSyncBundleWaitingForGONetReady(networkData, elapsedTicksAtSend, messageType);
                                        shouldReturnToPool = false; // Queue owns byte array now

                                        //GONetLog.Debug($"[GONETREADY-QUEUE] Deferred {channelQuality} sync bundle - participant {notReadyEx.GONetId} missing/not ready. " +
//                                                      $"QueueSize: {incomingNetworkData_waitingForGONetReady.Count}, " +
//                                                      $"Timeout: {GONetGlobal.Instance.maxSecondsToWaitForMissingParticipant}s");
                                    }
                                    else
                                    {
                                        // DEFAULT: Drop the bundle (user disabled deferral)
                                        // DIAGNOSTIC IMPROVEMENT: Distinguish stale bundles (after despawn) from real initialization failures
                                        // NOTE: notReadyEx.GONetId contains instantiation ID, not current GONetId, so we parse the exception message.
                                        // - Message contains "GONetId: 0" or "missing participant": Stale bundle after despawn (expected, Debug level)
                                        // - Message contains "didAwakeComplete: False": Real race condition during Awake (unexpected, Warning level)
                                        // PERFORMANCE: Uses const strings (zero allocation), executes only in exception handler (already error path).
                                        bool isStaleBundle = notReadyEx.Message.Contains(EXCEPTION_MSG_STALE_GONETID) ||
                                                            notReadyEx.Message.Contains(EXCEPTION_MSG_MISSING_PARTICIPANT);

                                        if (isStaleBundle)
                                        {
                                            // Stale bundle - participant already despawned, network queue lag is expected
                                            // This is normal during rapid spawn/despawn cycles, no logging needed
                                        }
                                        else
                                        {
                                            // Real initialization failure - participant exists but not ready
                                            GONetLog.Warning($"[GONETREADY-DROP-REAL] ⚠️ Dropped {channelQuality} sync bundle - participant {notReadyEx.GONetId} exists but not ready (initialization race condition). " +
                                                            $"Channel: {networkData.channelId}. " +
                                                            $"RECOMMENDATION: Enable deferral (GONetGlobal.deferSyncBundlesWaitingForGONetReady=true) to handle sync bundles arriving before spawn completes. " +
                                                            $"NOTE: Unity may still have an older serialized FALSE in your GONetGlobal prefab/scene; keep GONetGlobal.forceEnableSyncBundleDeferral=true to override. " +
                                                            $"Exception: {notReadyEx.Message}");
                                        }
                                        // Falls through to pool return (shouldReturnToPool=true)
                                    }
                                }
                            }
                            else if (messageType == typeof(RequestMessage))
                            {
                                long requestUID;
                                bitStream.ReadLong(out requestUID);

                                byte domainVersion = 0;
                                long domainSessionGuid = 0;
                                uint domainHostEpoch = 0;
                                ushort domainHostAuthorityId = 0;
                                bool hasDomain = TryReadTimeSyncDomain(bitStream, out domainVersion, out domainSessionGuid, out domainHostEpoch, out domainHostAuthorityId);

                                string domainReason = null;
                                if (!hasDomain || !ValidateTimeSyncDomain(domainVersion, domainSessionGuid, domainHostEpoch, domainHostAuthorityId, out domainReason))
                                {
                                    long nowRawTicks = Time.RawElapsedTicks;
                                    if (ShouldLogTimeSyncDomainIssue(nowRawTicks))
                                    {
                                        GetExpectedTimeSyncDomain(out long expectedSessionGuid, out uint expectedEpoch, out ushort expectedHostAuthority);
                                        string reason = hasDomain ? domainReason : "missing time sync domain";
                                        GONetLog.Warning($"[TimeSync] Dropping request - invalid time sync domain ({reason}). " +
                                                         $"recvVersion={domainVersion}, recvSession={domainSessionGuid}, recvEpoch={domainHostEpoch}, recvHostAuth={domainHostAuthorityId}. " +
                                                         $"localSession={expectedSessionGuid}, localEpoch={expectedEpoch}, localHostAuth={expectedHostAuthority}");
                                    }
                                    return;
                                }

                                //GONetLog.Info($"[TimeSync] SERVER: Received time sync request - UID: {requestUID}, elapsedTicksAtSend: {elapsedTicksAtSend}");

                                if (requestUID == 0)
                                {
                                    GONetLog.Error($"[TimeSync] SERVER: CRITICAL - Received RequestMessage with UID=0! elapsedTicksAtSend: {elapsedTicksAtSend}, bitStream position: {bitStream.Position_Bytes}");
                                }

                                // CRITICAL: Pass network-thread receive timestamp as t1
                                // This eliminates RTT inflation from server main thread queue delays
                                Server_SyncTimeWithClient_Respond(requestUID, networkData.relatedConnection, networkData.receivedAtRawTicks);
                            }
                            else if (messageType == typeof(ResponseMessage))
                            {
                                // NTP-style 4-timestamp protocol:
                                // - elapsedTicksAtSend = t1 = server receive time (already read from header)
                                // - serverSendTicks = t2 = server send time (read now from header)
                                // - networkData.receivedAtRawTicks = t3 = client receive time (captured on network thread)
                                long serverSendTicks;
                                bitStream.ReadLong(out serverSendTicks);

                                long requestUID;
                                bitStream.ReadLong(out requestUID);

                                byte domainVersion = 0;
                                long domainSessionGuid = 0;
                                uint domainHostEpoch = 0;
                                ushort domainHostAuthorityId = 0;
                                TryReadTimeSyncDomain(bitStream, out domainVersion, out domainSessionGuid, out domainHostEpoch, out domainHostAuthorityId);

                                if (!IsClient || _gonetClient?.connectionToServer == null)
                                {
                                    GONetLog.Warning("[TimeSync] Ignoring response - client not initialized or missing connection");
                                }
                                else if (!ReferenceEquals(networkData.relatedConnection, _gonetClient.connectionToServer))
                                {
                                    long nowRawTicks = Time.RawElapsedTicks;
                                    if (nowRawTicks - _timeSyncIgnoreLogRawTicks >= TimeSpan.TicksPerSecond)
                                    {
                                        _timeSyncIgnoreLogRawTicks = nowRawTicks;
                                        string relatedId = networkData.relatedConnection?.ConnectionId ?? "null";
                                        string activeId = _gonetClient.connectionToServer?.ConnectionId ?? "null";
                                        ushort relatedAuth = networkData.relatedConnection?.OwnerAuthorityId ?? 0;
                                        ushort activeAuth = _gonetClient.connectionToServer?.OwnerAuthorityId ?? 0;
                                        GONetLog.Warning($"[TimeSync] Ignoring response from non-active connection (conn={relatedId}, auth={relatedAuth}, activeConn={activeId}, activeAuth={activeAuth})");
                                    }
                                }
                                else
                                {
                                    // DIAGNOSTIC: Check if t3 (receivedAtRawTicks) is using transport timestamp or fallback
                                    #if UNITY_EDITOR || DEVELOPMENT_BUILD
                                    long nowTicks = SecretaryOfTemporalAffairs.GetRawElapsedTicksStatic();
                                    long t3Age = nowTicks - networkData.receivedAtRawTicks;
                                    double t3AgeMs = t3Age / (double)TimeSpan.TicksPerMillisecond;
                                    if (t3AgeMs > 100) // More than 100ms old = probably queued
                                    {
                                        GONetLog.Warning($"[TimeSync-T3-DIAG] t3 is {t3AgeMs:F0}ms old! t3={networkData.receivedAtRawTicks / (double)TimeSpan.TicksPerSecond:F3}s, now={nowTicks / (double)TimeSpan.TicksPerSecond:F3}s - timestamp may not be transport-level");
                                    }
                                    #endif

                                    // Pass all timestamps for proper NTP offset calculation
                                    // t1 = elapsedTicksAtSend (server receive), t2 = serverSendTicks (server send), t3 = receivedAtRawTicks (client receive)
                                    Client_SyncTimeWithServer_ProcessResponse(
                                        requestUID,
                                        elapsedTicksAtSend,
                                        serverSendTicks,
                                        networkData.receivedAtRawTicks,
                                        domainVersion,
                                        domainSessionGuid,
                                        domainHostEpoch,
                                        domainHostAuthorityId);
                                }
                            }
                            else if (messageType == typeof(OwnerAuthorityIdAssignmentEvent)) // this should be the first message ever received....but since only sent once per client, do not put it first in the if statements list of message type check
                            {
                                // Dump the raw bytes received
                                //string hex = System.BitConverter.ToString(networkData.messageBytes, 0, networkData.bytesUsedCount);
                                //GONetLog.Info($"[INIT] CLIENT: Received message - Bytes: {hex}");

                                // Show bytes 12-14 where OwnerAuthorityId should be
                                //string authBytes = System.BitConverter.ToString(networkData.messageBytes, 12, 3);
                                //GONetLog.Info($"[INIT] CLIENT: Bytes 12-14 (where AuthorityId should be): {authBytes}");

                                //GONetLog.Info($"[INIT] CLIENT: About to read OwnerAuthorityId - BitCount: {GONetParticipant.OWNER_AUTHORITY_ID_BIT_COUNT_USED}, BitStream Position Before: {bitStream.Position_Bytes} bytes {bitStream.Position_Bits} bits, TotalBytes: {networkData.bytesUsedCount}");

                                ushort ownerAuthorityId;
                                bitStream.ReadUShort(out ownerAuthorityId, GONetParticipant.OWNER_AUTHORITY_ID_BIT_COUNT_USED);

                                long sessionGUIDremote;
                                bitStream.ReadLong(out sessionGUIDremote);
                                SessionGUID = sessionGUIDremote;

                                if (!IsServer) // this only applied to clients....should NEVER happen on server
                                {
                                    MyAuthorityId = ownerAuthorityId;

                                    RefreshRigidBodySettingsForAuthorityChange("AuthorityAssigned");

                                    // DEBUG: Set connection ID for ACK instrumentation logging
                                    if (_gonetClient?.connectionToServer != null)
                                    {
                                        _gonetClient.connectionToServer.ConnectionId = $"C{ownerAuthorityId}->S";

                                        // PHASE 7 FIX (December 2025): Clear pre-authority state to prevent false ACKs.
                                        //
                                        // Before authority assignment, the client sends messages on a "pre-authority" connection
                                        // (identified as Client:0 in logs). The ackBuffer stores packet-to-message mappings.
                                        // After authority assignment, the same MessageChannel is reused but the ackBuffer
                                        // still contains stale mappings. If a cross-connection ACK arrives (from mesh peers
                                        // in hot standby topology), it can trigger ackPacket() with an old packet sequence,
                                        // falsely marking critical messages (like SceneLoadComplete) as ACKed.
                                        //
                                        // This call clears the stale ackBuffer while preserving pending messages in sendBuffer
                                        // that need to be retransmitted on the post-authority connection.
                                        _gonetClient.connectionToServer.ClearPreAuthorityState();
                                        GONetLog.Info($"[PHASE7] Authority assigned (id={ownerAuthorityId}), cleared pre-authority reliable state");
                                    }
                                } // else log warning?
                            }
                            else if (messageType == typeof(AutoMagicalSync_AllCurrentValues_Message))
                            {
                                // DIAGNOSTIC LOGGING: Track every AllValues bundle arrival

                                // IMPORTANT: If we have deferred spawns waiting for scene load, we must also defer the AllValues bundle
                                // Otherwise we'll try to apply values to GameObjects that haven't been spawned yet (causing dictionary lookup failures)
                                //
                                // ALSO: If client is currently loading a scene (async Addressables), defer AllValues bundles
                                // because they may contain scene-defined objects that don't exist yet in the target scene.
                                // HierarchyUtils.FindByFullUniquePath would fail with "The scene is invalid" error.
                                //
                                // CRITICAL FIX (November 2025): Also defer if we have ANY deferred bundles for this scene already!
                                // This prevents race condition where:
                                // 1. Server sends 810 AllValues bundles (takes multiple frames to arrive)
                                // 2. First 225 bundles arrive WHILE scene loading → deferred
                                // 3. Scene finishes loading, IsCurrentlyLoadingScene becomes false
                                // 4. Remaining 585 bundles arrive AFTER scene loaded → would process immediately (WRONG!)
                                // 5. But participants aren't ready yet (OnGONetReady not fired) → processing fails
                                //
                                // Solution: If ANY bundles already deferred for this scene, continue deferring ALL bundles
                                // until ProcessDeferredSpawnsForScene processes them all together.
                                bool clientCurrentlyLoadingScene = IsClient && SceneManager != null && SceneManager.IsCurrentlyLoadingScene;

                                // Get scene name for deferral check
                                // If loading, use CurrentlyLoadingSceneName. If NOT loading but have deferred bundles, use their scene name.
                                string currentSceneName = "";
                                if (clientCurrentlyLoadingScene)
                                {
                                    currentSceneName = SceneManager.CurrentlyLoadingSceneName;
                                }
                                else if (deferredAllValuesBundles.Count > 0)
                                {
                                    // Not currently loading, but we have deferred bundles - use the scene from those bundles
                                    currentSceneName = deferredAllValuesBundles[0].RequiredSceneName;
                                }
                                else
                                {
                                }

                                bool hasExistingDeferredBundlesForScene = false;

                                // Check if we already have deferred bundles for the scene we're interested in
                                if (!string.IsNullOrEmpty(currentSceneName) && deferredAllValuesBundles.Count > 0)
                                {
                                    foreach (var deferredBundle in deferredAllValuesBundles)
                                    {
                                        if (deferredBundle.RequiredSceneName == currentSceneName)
                                        {
                                            hasExistingDeferredBundlesForScene = true;
                                            break;
                                        }
                                    }
                                    if (!hasExistingDeferredBundlesForScene)
                                    {
                                    }
                                }
                                else
                                {
                                }


                                if (deferredSpawnEvents.Count > 0 || clientCurrentlyLoadingScene || hasExistingDeferredBundlesForScene)
                                {
                                    string deferReason = clientCurrentlyLoadingScene
                                        ? $"client loading scene '{SceneManager.CurrentlyLoadingSceneName}'"
                                        : hasExistingDeferredBundlesForScene
                                            ? $"existing deferred bundles for scene '{currentSceneName}' (race condition prevention)"
                                            : $"{deferredSpawnEvents.Count} spawns waiting";
                                    //GONetLog.Warning($"[INIT] Deferring AllValues bundle processing - {deferReason}");

                                    // IMPORTANT: Copy only the remaining bytes AFTER the header (which has already been read)
                                    // The bitStream position is currently at the start of the body, after reading messageID and elapsedTicks
                                    int currentPosition = bitStream.Position_Bytes;
                                    int remainingBytes = networkData.bytesUsedCount - currentPosition;
                                    byte[] deferredBytes = new byte[remainingBytes];
                                    Array.Copy(bitStream.GetBuffer(), currentPosition, deferredBytes, 0, remainingBytes);

                                    // Store which scene we're waiting for
                                    // If client is loading scene, use that; otherwise use deferred spawn's scene
                                    string requiredScene = clientCurrentlyLoadingScene
                                        ? SceneManager.CurrentlyLoadingSceneName
                                        : (deferredSpawnEvents.Count > 0 ? deferredSpawnEvents[0].SceneIdentifier : "");

                                    DeferredAllValuesBundle bundle = new DeferredAllValuesBundle
                                    {
                                        RawBytes = deferredBytes,
                                        BytesUsedCount = remainingBytes,
                                        RelatedConnection = networkData.relatedConnection,
                                        ElapsedTicksAtSend = elapsedTicksAtSend,
                                        RequiredSceneName = requiredScene,
                                        RetryCount = 0,
                                        FirstDeferralRawTicks = HighResolutionTimeUtils.UtcNowTicks
                                    };

                                    deferredAllValuesBundles.Add(bundle); // Add to list instead of overwriting

                                    // CRITICAL FIX (November 2025): Track bundle reception for late-joiner init completion
                                    timeOfLastAllValuesBundle = UnityEngine.Time.time;
                                    receivedAllValuesBundlesForLateJoinerInit++;
                                    lateJoinerInitSceneName = requiredScene; // Track which scene we're initializing

                                    //GONetLog.Warning($"[LATE-JOINER-TRACK] Deferred bundle {receivedAllValuesBundlesForLateJoinerInit}/{(expectedAllValuesBundlesForScene >= 0 ? expectedAllValuesBundlesForScene.ToString() : "?")} for scene '{requiredScene}'");
//                                    GONetLog.Warning($"[INIT] AllValues bundle deferred - waiting for scene '{requiredScene}' ({deferReason}) (bytes: {remainingBytes}, total deferred: {deferredAllValuesBundles.Count})");
                                }
                                else
                                {
                                    // CRITICAL FIX (November 2025): Track bundle reception for late-joiner init completion
                                    timeOfLastAllValuesBundle = UnityEngine.Time.time;
                                    receivedAllValuesBundlesForLateJoinerInit++;

                                    //GONetLog.Warning($"[LATE-JOINER-TRACK] Received bundle {receivedAllValuesBundlesForLateJoinerInit}/{(expectedAllValuesBundlesForScene >= 0 ? expectedAllValuesBundlesForScene.ToString() : "?")} for scene '{lateJoinerInitSceneName}'");

                                    try
                                    {
                                        DeserializeBody_AllValuesBundle(bitStream, networkData.bytesUsedCount, networkData.relatedConnection, elapsedTicksAtSend);
                                    }
                                    catch (Exception ex) when (ex is KeyNotFoundException || ex.GetType().Name == "GONetParticipantNotReadyException")
                                    {
                                        // CRITICAL FIX (November 2025): "Tail race" - scene loaded but participants not ready yet
                                        // This happens when: scene finishes → ProcessDeferredSpawnsForScene fires → list cleared →
                                        // remaining bundles arrive → process immediately → but participants still initializing!
                                        // SOLUTION: Re-defer the bundle for polling system to retry when participants are ready.

                                        GONetLog.Warning($"[TAIL-RACE-CAUGHT] AllValues deserialization failed ({ex.GetType().Name}). Re-deferring bundle for polling retry. Message: {ex.Message}");

                                        // Re-construct the raw bytes for deferral
                                        // bitStream has already read messageID (4 bytes) + elapsedTicks (8 bytes) = 12 byte header
                                        // We need to defer the BODY (everything after header)
                                        int headerSize = 12; // uint messageID + long elapsedTicks
                                        int bodyStartPos = headerSize;
                                        int remainingBytes = networkData.bytesUsedCount - bodyStartPos;
                                        byte[] deferredBytes = new byte[remainingBytes];

                                        // Copy from original message bytes (networkData.messageBytes contains full message)
                                        Array.Copy(bitStream.GetBuffer(), bodyStartPos, deferredBytes, 0, remainingBytes);

                                        // Guess the scene name - use active scene since we're not in "loading" state
                                        string requiredScene = SceneManager?.ActiveSceneName ?? lateJoinerInitSceneName;
                                        if (string.IsNullOrEmpty(requiredScene))
                                        {
                                            requiredScene = "UnknownScene"; // Fallback
                                            GONetLog.Warning($"[TAIL-RACE-FIX] Could not determine scene name for re-deferred bundle, using '{requiredScene}'");
                                        }

                                        DeferredAllValuesBundle bundle = new DeferredAllValuesBundle
                                        {
                                            RawBytes = deferredBytes,
                                            BytesUsedCount = remainingBytes,
                                            RelatedConnection = networkData.relatedConnection,
                                            ElapsedTicksAtSend = elapsedTicksAtSend,
                                            RequiredSceneName = requiredScene,
                                            RetryCount = 1,
                                            FirstDeferralRawTicks = HighResolutionTimeUtils.UtcNowTicks
                                        };

                                        deferredAllValuesBundles.Add(bundle);
                                        GONetLog.Warning($"[TAIL-RACE-FIX] Re-deferred bundle for scene '{requiredScene}'. Total deferred: {deferredAllValuesBundles.Count}. Polling system will retry.");

                                        // IMPORTANT: Check if this is a repeated failure (prevent infinite re-defer loop)
                                        // If we're already processing from deferred queue and it fails again, that's a critical error
                                        QosType channelQuality = GONetChannel.ById(networkData.channelId).QualityOfService;

                                        if (isProcessingFromQueue)
                                        {
                                            GONetLog.Error($"[TAIL-RACE-CRITICAL] Bundle failed AGAIN after re-deferral! This indicates participants are never becoming ready. Dropping to prevent infinite loop.");
                                            // Message will be returned to pool via shouldReturnToPool=true
                                        }
                                        else if (IsClient && channelQuality == QosType.Reliable)
                                        {
                                            // Queue this message for retry after GONetId sync completes
                                            if (_gonetClient.incomingNetworkData_waitingForGONetIds.Count < GONetClient.MAX_GONETID_QUEUE_SIZE)
                                            {
                                                _gonetClient.incomingNetworkData_waitingForGONetIds.Enqueue(networkData);
                                                //GONetLog.Debug($"[GONETID-QUEUE] Queued reliable message (channel: {networkData.channelId}) waiting for GONetId assignment. Queue size: {_gonetClient.incomingNetworkData_waitingForGONetIds.Count}"); // COMMENTED - spammy log (log cleanup)

                                                // Skip processing, but DON'T return to pool - it's now owned by the queue
                                                shouldReturnToPool = false;
                                            }
                                            else
                                            {
                                                // Queue full - log error and drop oldest
                                                GONetLog.Error($"[GONETID-QUEUE] Queue full ({GONetClient.MAX_GONETID_QUEUE_SIZE} messages)! Dropping oldest message. This indicates a problem with GONetId synchronization.");
                                                NetworkData droppedMessage = _gonetClient.incomingNetworkData_waitingForGONetIds.Dequeue();

                                                // Return dropped message to pool
                                                SingleProducerQueues droppedQueues = singleProducerReceiveQueuesByThread[droppedMessage.messageBytesBorrowedOnThread];
                                                droppedQueues.queueForPostWorkResourceReturn.Enqueue(droppedMessage);

                                                // Queue current message
                                                _gonetClient.incomingNetworkData_waitingForGONetIds.Enqueue(networkData);
                                                shouldReturnToPool = false;
                                            }
                                        }
                                        else
                                        {
                                            // Unreliable message or not a client - just drop it
                                            //GONetLog.Debug($"[GONETID-QUEUE] Dropping unreliable message (channel: {networkData.channelId}) due to missing GONetId - as designed"); // COMMENTED - spammy log (log cleanup)
                                        }
                                        // Let it fall through to the finally block for cleanup
                                    }
                                }
                            }
                            else if (messageType == typeof(ServerSaysClientInitializationCompletion))
                            {
                                if (IsClient)
                                {
                                    GONetLog.Info($"[INIT-TIMELINE] CLIENT T+0: RECEIVED ServerSaysClientInitializationCompletion at {Time.ElapsedSeconds:F3}s");

                                    GONetLog.Info($"[INIT-TIMELINE] CLIENT T+0: Setting GONetClient.IsInitializedWithServer=true at {Time.ElapsedSeconds:F3}s");
                                    GONetClient.IsInitializedWithServer = true;
                                    GONetLog.Info($"[INIT-TIMELINE] CLIENT T+0: GONetClient.IsInitializedWithServer flag set (InitializedWithServer event will fire) at {Time.ElapsedSeconds:F3}s");

                                    // INIT MESSAGE ACKNOWLEDGMENT: Sent only after both init channel markers arrive
                                    Client_TrySendInitializationAcknowledgment();

                                    // IMPORTANT: Log registered sync companions for debugging
                                    int totalCompanions = 0;
                                    foreach (var codeGenEntry in activeAutoSyncCompanionsByCodeGenerationIdMap)
                                    {
                                        totalCompanions += codeGenEntry.Value.Count;
                                        //GONetLog.Debug($"[AUTOMAGIC] Client has {codeGenEntry.Value.Count} sync companions registered for CodeGenId {codeGenEntry.Key}");
                                    }
                                    //GONetLog.Debug($"[AUTOMAGIC] Client total sync companions registered: {totalCompanions}");
                                }
                            } // else?  TODO lookup proper deserialize method instead of if-else-if statement(s)
                            else if (messageType == typeof(ServerSaysInitMessageTrackingComplete))
                            {
                                if (IsClient && _gonetClient != null)
                                {
                                    _gonetClient.hasReceivedInitTrackingMarker_CustomSerialization = true;
                                    Client_TrySendInitializationAcknowledgment();
                                }
                            } // else?  TODO lookup proper deserialize method instead of if-else-if statement(s)
                        }
                    }
                }
                else
                {
                    // Custom channel (including DistributedHost channels)
                    // Debug: Log custom channel messages on DistributedHost channels
                    if (networkData.channelId == GONetChannel.DistributedHost_Reliable.Id ||
                        networkData.channelId == GONetChannel.DistributedHost_Unreliable.Id)
                    {
                        bool hasSubscribers = OnCustomChannelPayloadReceived != null;
                        // CRITICAL DEBUG: Log all DistributedHost messages at dequeue time
                        // COMMENTED OUT (Dec 2025): This diagnostic caused 5,000+ warnings during handoff, killing frame rate
                        //byte msgType = networkData.bytesUsedCount > 0 ? networkData.messageBytes[0] : (byte)0;
                        //GONetLog.Warning($"[CustomChannel-DEQUEUE] DistributedHost dequeued: channel={networkData.channelId}, " +
                        //    $"size={networkData.bytesUsedCount}, msgType={msgType}, hasSubscribers={hasSubscribers}, " +
                        //    $"connection={networkData.relatedConnection?.GetType().Name ?? \"null\"}");
                    }
                    OnCustomChannelPayloadReceived?.Invoke(networkData.channelId, networkData.relatedConnection, networkData.messageBytes, networkData.bytesUsedCount);
                }

                // TODO this should only deserialize the message....and then send over to an EventBus where subscribers to that event/message from the bus can process accordingly
            }
            catch (GONetOutOfOrderHorseDickoryException outOfOrderException)
            {
                GONetLog.Error(outOfOrderException.Message);
            }
            catch (Exception e)
            {
                GONetLog.Error(string.Concat("Error Message: ", e.Message, "\nError Stacktrace:\n", e.StackTrace));
            }
            finally
            {
                // Only return to pool if message wasn't queued elsewhere (e.g., waiting for GONetIds)
                if (shouldReturnToPool)
                {
                    // set things up so the byte[] on networkData can be returned to the proper pool AND on the proper thread on which is was initially borrowed!
                    SingleProducerQueues singleProducerReceiveQueues = singleProducerReceiveQueuesByThread[networkData.messageBytesBorrowedOnThread];
                    singleProducerReceiveQueues.queueForPostWorkResourceReturn.Enqueue(networkData);
                }
            }
        }

        private static void DeserializeBody_EventSingle(byte[] messageBytes, int bytesUsedCount, GONetConnection relatedConnection)
        {
            // DIAGNOSTIC (December 2025): Log message entry for spawn loss investigation
            // To enable, add GONet_SPAWN_TRACE to Player Settings → Scripting Define Symbols
            #if GONet_SPAWN_TRACE
            string firstBytesHex = "";
            if (bytesUsedCount >= 12)
            {
                firstBytesHex = System.BitConverter.ToString(messageBytes, 0, 12).Replace("-", "");
            }
            else if (bytesUsedCount > 0)
            {
                firstBytesHex = System.BitConverter.ToString(messageBytes, 0, bytesUsedCount).Replace("-", "");
            }

            // Log entry for spawn-sized messages (80-110 bytes typical for spawn events)
            if (bytesUsedCount >= 75 && bytesUsedCount <= 115)
            {
                GONetLog.Debug($"[DESER-ENTRY] EventSingle entry: bytes={bytesUsedCount}, from={relatedConnection.OwnerAuthorityId}, isServer={IsServer}, firstBytes={firstBytesHex}");
            }
            #endif

            try
            {
                // PERFORMANCE: Use ReadOnlySpan to deserialize only the actual message bytes (zero allocation, stack-only)
                // This is faster than ArraySegment<byte> (no heap allocation) and safer than raw byte[] (bounds-checked slice)
                IGONetEvent @event = SerializationUtils.DeserializeFromBytes<IGONetEvent>(
                    messageBytes.AsSpan(0, bytesUsedCount));

                // DIAGNOSTIC (December 2025): Log ALL InstantiateGONetParticipantEvent to track spawn loss
                // To enable, add GONet_SPAWN_TRACE to Player Settings → Scripting Define Symbols
                #if GONet_SPAWN_TRACE
                if (@event is InstantiateGONetParticipantEvent spawnEvent)
                {
                    if (spawnEvent.ImmediatelyRelinquishAuthorityToServer_AndTakeRemoteControlAuthority)
                    {
                        GONetLog.Debug($"[SPAWN-DESER] Deserialized spawn event (client-spawned server-owned): GONetId={spawnEvent.GONetId}, From={relatedConnection.OwnerAuthorityId}, IsServer={IsServer}, firstBytes={firstBytesHex}");
                    }
                    else
                    {
                        GONetLog.Debug($"[SPAWN-DESER-OTHER] Deserialized spawn event (normal): GONetId={spawnEvent.GONetId}, From={relatedConnection.OwnerAuthorityId}, IsServer={IsServer}, ImmediatelyRelinquish={spawnEvent.ImmediatelyRelinquishAuthorityToServer_AndTakeRemoteControlAuthority}");
                    }
                }
                #endif

                // HOST MODE FIX: Prevent event feedback loop through loopback connection.
                // In host mode, the same process is BOTH server and client. Without this filter,
                // events would echo infinitely: Server publishes → Loopback → Client receives → Re-publishes → Loop!
                // Rule: "Don't re-publish events received from loopback when we're the server"
                // See GONetConnection_ClientHostLoopback documentation for full analysis.
                if (relatedConnection is GONetConnection_ClientHostLoopback && IsServer)
                {
                    // Host already has this event locally - skip re-publishing to prevent feedback loop
                    return;
                }

                //GONetLog.Warning($"[DESER_DEBUG] About to publish event to EventBus");
                EventBus.Publish(@event, relatedConnection.OwnerAuthorityId);
                //GONetLog.Warning($"[DESER_DEBUG] EventBus.Publish completed");
                // SPAM: Commented out - creates 2,777+ log entries during stress testing, mostly ValueMonitoringSupport events
                //GONetLog.Debug($"Incoming event being published.  Type: {@event.GetType().Name}");
            }
            catch (System.Exception ex)
            {
                GONetLog.Error($"[SPAWN_SYNC] CLIENT: FAILED to deserialize EventSingle - Size: {bytesUsedCount} bytes (array capacity: {messageBytes.Length}), From: AuthorityId {relatedConnection.OwnerAuthorityId}, Error: {ex.Message}\nStackTrace: {ex.StackTrace}");
                throw;
            }
        }

        static bool isCurrentlyProcessingInstantiateGNPEvent;
        /// <summary>
        /// only relevant while <see cref="isCurrentlyProcessingInstantiateGNPEvent"/> is true.
        /// </summary>
        static InstantiateGONetParticipantEvent currentlyProcessingInstantiateGNPEvent;

        /// <summary>
        /// Process instantiation event from remote source.
        /// </summary>
        /// <param name="instantiateEvent"></param>
        private static GONetParticipant Instantiate_Remote(InstantiateGONetParticipantEvent instantiateEvent)
        {
            isCurrentlyProcessingInstantiateGNPEvent = true;
            currentlyProcessingInstantiateGNPEvent = instantiateEvent;

            //GONetLog.Debug($"instantiation.location: {instantiateEvent.DesignTimeLocation}, parent.fullPath: {instantiateEvent.ParentFullUniquePath}");

            // CRITICAL: Validate DesignTimeLocation is not empty before attempting to instantiate
            // If it's empty, the spawn event was created before metadata was initialized (timing issue)
            if (string.IsNullOrWhiteSpace(instantiateEvent.DesignTimeLocation))
            {
                GONetLog.Error($"Cannot instantiate remote GONetParticipant - DesignTimeLocation is empty/null! GONetId: {instantiateEvent.GONetId}, InstanceName: {instantiateEvent.InstanceName}. This indicates the spawn event was created before metadata initialization completed. The spawn will be skipped.");
                isCurrentlyProcessingInstantiateGNPEvent = false;
                return null;
            }

            GONetParticipant template = GONetSpawnSupport_Runtime.LookupTemplateFromDesignTimeMetadata(instantiateEvent.DesignTimeLocation);

            // CRITICAL: Get and set metadata on the TEMPLATE before instantiating
            // Unity calls Awake() DURING Instantiate(), so we must prepare the template first
            // The instance will inherit this metadata when it's created
            DesignTimeMetadata templateMetadata = GONetSpawnSupport_Runtime.GetDesignTimeMetadata(instantiateEvent.DesignTimeLocation);
            bool templateMetadataWasAlreadySet = false;

            if (templateMetadata != null && !string.IsNullOrWhiteSpace(templateMetadata.Location))
            {
                //GONetLog.Debug($"Instantiate_Remote: Pre-setting metadata on template '{template.name}' - Location: '{templateMetadata.Location}', CodeGenId: {templateMetadata.CodeGenerationId}");

                // Check if template already has metadata to avoid overwriting
                if (!template.IsDesignTimeMetadataInitd)
                {
                    DesignTimeMetadata metadataToSet = new DesignTimeMetadata
                    {
                        Location = templateMetadata.Location,
                        CodeGenerationId = templateMetadata.CodeGenerationId,
                        UnityGuid = templateMetadata.UnityGuid
                    };
                    GONetSpawnSupport_Runtime.SetDesignTimeMetadata(template, metadataToSet);
                    template.IsDesignTimeMetadataInitd = true;
                }
                else
                {
                    templateMetadataWasAlreadySet = true;
                }
            }

            template.wasInstantiatedForce = true; // the instantiated one will get this
            GONetParticipant instance;
            try
            {
                instance = string.IsNullOrWhiteSpace(instantiateEvent.ParentFullUniquePath)
                    ? UnityEngine.Object.Instantiate(template, instantiateEvent.Position, instantiateEvent.Rotation)
                    : UnityEngine.Object.Instantiate(template, instantiateEvent.Position, instantiateEvent.Rotation, HierarchyUtils.FindByFullUniquePath(instantiateEvent.ParentFullUniquePath).transform);
            }
            catch (UnityException ex) when (ex.Message.Contains("clone was destroyed during creation"))
            {
                template.wasInstantiatedForce = false;
                // This happens when a singleton (like GONetGlobal) destroys itself in Awake() because an instance already exists.
                // For late-joiners receiving persistent spawn events, this is expected for singletons - not an error.
                bool isLikelySingleton = template.GetComponent<GONetGlobal>() != null;
                if (isLikelySingleton)
                {
                    GONetLog.Debug($"Instantiate_Remote: Singleton '{instantiateEvent.DesignTimeLocation}' self-destroyed (duplicate prevention) - this is expected for late-joiners.");
                }
                else
                {
                    GONetLog.Warning($"Instantiate_Remote: Prefab '{instantiateEvent.DesignTimeLocation}' was destroyed during Instantiate (DestroyImmediate in Awake?). GONetId: {instantiateEvent.GONetId}");
                }
                isCurrentlyProcessingInstantiateGNPEvent = false;
                return null;
            }
            template.wasInstantiatedForce = false; // be safe and set back to false

            // Deserialize custom spawn data from IGONetSyncdBehaviourInitializer components
            DeserializeCustomSpawnData(instance, instantiateEvent.CustomSpawnData);

            // The instance should have inherited the metadata from the template during Instantiate()
            // But we still need to mark it as initialized to be safe
            if (templateMetadata != null && !string.IsNullOrWhiteSpace(templateMetadata.Location))
            {
                // Verify the instance has the correct metadata
                if (!instance.IsDesignTimeMetadataInitd)
                {
                    GONetLog.Warning($"Instantiate_Remote: Instance '{instance.gameObject.name}' did NOT inherit metadata initialization flag from template - setting it now");
                    instance.IsDesignTimeMetadataInitd = true;
                }

                //GONetLog.Debug($"Instantiate_Remote: Instance '{instance.gameObject.name}' metadata - Location: '{instance.DesignTimeLocation}', CodeGenId: {instance.CodeGenerationId}, IsInitd: {instance.IsDesignTimeMetadataInitd}");
            }

            if (!string.IsNullOrWhiteSpace(instantiateEvent.InstanceName))
            {
                instance.gameObject.name = instantiateEvent.InstanceName;
            }

            //const string INSTANTIATE = "Instantiate_Remote, Instantiate complete....go.name: ";
            //const string ID = " event.gonetId: ";
            //const string FORCE = " wasInstantiatedForce: ";
            //GONetLog.Debug(string.Concat(INSTANTIATE, instance.gameObject.name, ID, instantiateEvent.GONetId, FORCE, instance.wasInstantiatedForce));

            instance.OwnerAuthorityId = instantiateEvent.OwnerAuthorityId;
            instance.SpawnerPersistentId = instantiateEvent.SpawnerPersistentId;
            if (instantiateEvent.GONetId != GONetParticipant.GONetId_Unset)
            {
                instance.SetGONetIdFromRemoteInstantiation(instantiateEvent);
            }
            remoteSpawns_avoidAutoPropagateSupport.Add(instance);
            instance.IsOKToStartAutoMagicalProcessing = true;

            // LIFECYCLE GATE: Remote spawns require DeserializeInitAllCompleted before OnGONetReady
            // CRITICAL FIX 2025-10-11: Authority instances (IsMine=True) do NOT need deserialization!
            // They are the source of truth, not receiving sync data from elsewhere.
            // Only non-authority instances (IsMine=False) need to wait for initial sync data from remote authority.
            //
            // Example: Client spawns projectile with server authority
            //   - Client's instance: IsMine=False (not authority) → MUST wait for server sync data
            //   - Server's instance: IsMine=True (IS authority) → NO deserialization needed!
            //
            // Before this fix: Server instances got stuck waiting for events that would never come
            // After this fix: Server instances skip deserialization, OnGONetReady fires immediately after Start()
            if (!instance.IsMine)
            {
                instance.MarkRequiresDeserializeInit();
            }

            // Track which scene this GNP was spawned in
            string spawnSceneName = GONetSceneManager.GetSceneIdentifier(instance.gameObject);
            if (!string.IsNullOrEmpty(spawnSceneName))
            {
                RecordParticipantSpawnScene(instance, spawnSceneName);
            }

            isCurrentlyProcessingInstantiateGNPEvent = false;

            return instance;
        }

        /// <summary>
        /// Serializes initialization data from all <see cref="IGONetSyncdBehaviourInitializer"/> components on the given GONetParticipant.
        /// Used for both runtime spawns and scene-defined object synchronization.
        /// </summary>
        /// <param name="gonetParticipant">The participant to serialize initialization data from</param>
        /// <returns>Serialized initialization data byte array, or null if no initializers found</returns>
        internal static byte[] SerializeSceneObjectInitData(GONetParticipant gonetParticipant)
        {
            if (gonetParticipant == null)
            {
                return null;
            }

            // Find all IGONetSyncdBehaviourInitializer components on the same GameObject
            IGONetSyncdBehaviourInitializer[] providers = gonetParticipant.GetComponents<IGONetSyncdBehaviourInitializer>();

            if (providers == null || providers.Length == 0)
            {
                return null; // No initialization data providers
            }

            // Create builder for serialization
            Utils.BitByBitByteArrayBuilder builder = Utils.BitByBitByteArrayBuilder.GetBuilder();

            // Write provider count (for deserialization validation)
            builder.WriteUInt((uint)providers.Length, 8); // Max 255 providers

            // Call each provider's serialization method
            foreach (IGONetSyncdBehaviourInitializer provider in providers)
            {
                provider.Spawner_SerializeSpawnData(builder);
            }

            // CRITICAL: Flush any remaining bits from scratch buffer to memory!
            // Without this, the last byte(s) of data remain in BitWriter's scratch buffer
            // and never get copied to the result array, causing deserialization to read garbage (zeros).
            // This happens because WriteFloat() writes 32 bits at a time, and when combined with
            // the 8-bit provider count, the last 8 bits of the 4th float stay in scratch.
            builder.WriteCurrentPartialByte();

            // Return serialized byte array (copy only the written bytes, not the full buffer)
            int bytesWritten = builder.Length_WrittenBytes;
            byte[] result = new byte[bytesWritten];
            Array.Copy(builder.GetBuffer(), 0, result, 0, bytesWritten);

            // HEX DUMP: Log raw bytes for debugging serialization issue
            // COMMENTED (log cleanup) - fires for each scene object, spammy with hex dump
            /*string hexDump = System.BitConverter.ToString(result).Replace("-", " ");
            GONetLog.Debug($"[SceneInitData] Serialized initialization data for '{gonetParticipant.gameObject.name}' ({providers.Length} providers, {bytesWritten} bytes) - RAW BYTES: {hexDump}");*/

            return result;
        }

        /// <summary>
        /// Deserializes initialization data and calls <see cref="IGONetSyncdBehaviourInitializer.Receiver_DeserializeSpawnData"/> on all providers.
        /// Used for scene-defined object synchronization.
        /// </summary>
        /// <param name="participant">The scene-defined GONetParticipant</param>
        /// <param name="initData">Serialized initialization data from the RPC (or null if no providers)</param>
        internal static void DeserializeSceneObjectInitData(GONetParticipant participant, byte[] initData)
        {
            if (initData == null || initData.Length == 0)
            {
                return; // No initialization data to deserialize
            }

            if (participant == null)
            {
                GONetLog.Error($"[SceneInitData] Cannot deserialize init data - participant is null");
                return;
            }

            // Find all IGONetSyncdBehaviourInitializer components on the same GameObject
            IGONetSyncdBehaviourInitializer[] providers = participant.GetComponents<IGONetSyncdBehaviourInitializer>();

            if (providers == null || providers.Length == 0)
            {
                GONetLog.Warning($"[SceneInitData] Received initialization data ({initData.Length} bytes) but no IGONetSyncdBehaviourInitializer components found on '{participant.gameObject.name}' (GONetId: {participant.GONetId})");
                return;
            }

            // HEX DUMP: Log raw bytes for debugging serialization issue
            // COMMENTED (log cleanup) - fires for each scene object, spammy with hex dump
            /*string hexDump = System.BitConverter.ToString(initData).Replace("-", " ");
            GONetLog.Debug($"[SceneInitData] RAW BYTES for '{participant.gameObject.name}': {hexDump}");*/

            // Create builder for deserialization
            Utils.BitByBitByteArrayBuilder builder = Utils.BitByBitByteArrayBuilder.GetBuilder_WithNewData(initData, initData.Length);

            // Read provider count (for validation)
            uint providerCount;
            builder.ReadUInt(out providerCount, 8);

            if (providerCount != providers.Length)
            {
                GONetLog.Error($"[SceneInitData] Provider count mismatch on '{participant.gameObject.name}': Expected {providerCount} providers (from init data), found {providers.Length} components. Deserialization may fail!");
            }

            // Call each provider's deserialization method
            foreach (IGONetSyncdBehaviourInitializer provider in providers)
            {
                provider.Receiver_DeserializeSpawnData(builder);
            }

            //GONetLog.Debug($"[SceneInitData] Deserialized initialization data for '{participant.gameObject.name}' ({providers.Length} providers, {initData.Length} bytes)"); // COMMENTED - spammy log (log cleanup)
        }

        /// <summary>
        /// Deserializes custom spawn data and calls <see cref="IGONetSyncdBehaviourInitializer.Receiver_DeserializeSpawnData"/> on all providers.
        /// </summary>
        /// <param name="instance">The instantiated GONetParticipant</param>
        /// <param name="customSpawnData">Serialized spawn data from the spawn event (or null if no providers)</param>
        private static void DeserializeCustomSpawnData(GONetParticipant instance, byte[] customSpawnData)
        {
            if (customSpawnData == null || customSpawnData.Length == 0)
            {
                return; // No spawn data to deserialize
            }

            // Find all IGONetSyncdBehaviourInitializer components on the same GameObject
            IGONetSyncdBehaviourInitializer[] providers = instance.GetComponents<IGONetSyncdBehaviourInitializer>();

            if (providers == null || providers.Length == 0)
            {
                GONetLog.Warning($"[SpawnData] Received custom spawn data ({customSpawnData.Length} bytes) but no IGONetSyncdBehaviourInitializer components found on '{instance.gameObject.name}' (GONetId: {instance.GONetId})");
                return;
            }

            // Create builder for deserialization
            Utils.BitByBitByteArrayBuilder builder = Utils.BitByBitByteArrayBuilder.GetBuilder_WithNewData(customSpawnData, customSpawnData.Length);

            // Read provider count (for validation)
            uint providerCount;
            builder.ReadUInt(out providerCount, 8);

            if (providerCount != providers.Length)
            {
                GONetLog.Error($"[SpawnData] Provider count mismatch on '{instance.gameObject.name}': Expected {providerCount} providers (from spawn data), found {providers.Length} components. Deserialization may fail!");
            }

            // Call each provider's deserialization method
            foreach (IGONetSyncdBehaviourInitializer provider in providers)
            {
                provider.Receiver_DeserializeSpawnData(builder);
            }

            // GONetLog.Debug($"[SpawnData] Deserialized spawn data for '{instance.gameObject.name}' ({providers.Length} providers, {customSpawnData.Length} bytes)");
        }

        private static void Server_OnClientConnected_SendClientCurrentState(GONetConnection_ServerToClient connectionToClient)
        {
            //GONetLog.Debug($"[INIT] Server_OnClientConnected_SendClientCurrentState: Starting initialization for newly connected client (AuthorityId will be assigned)");

            Server_AssignNewClientAuthorityId(connectionToClient);
            //GONetLog.Debug($"[INIT] Assigned AuthorityId: {connectionToClient.OwnerAuthorityId}");

            Server_AssignNewClientGONetIdRawBatch(connectionToClient);
            //GONetLog.Debug($"[INIT] Assigned GONetId batch");

            Server_SendClientPersistentEventsSinceStart(connectionToClient);
            //GONetLog.Debug($"[INIT] Sent persistent events");

            // CRITICAL FIX (November 2025): Send InitComplete FIRST, before auto-magical sync bundles
            //
            // WHY THIS MATTERS:
            // - Late-joiners fail when InitComplete message gets stuck behind 800+ sync bundles in reliable queue
            // - Sending InitComplete FIRST ensures it arrives BEFORE unreliable flood backs up the queue
            // - Client becomes "initialized" immediately, can start receiving chunked sync data over time
            //
            // OLD ORDER (BROKEN for late-joiners with 800 objects):
            //   1. Send 800 sync bundles (reliable) → floods queue
            //   2. Send InitComplete (reliable) → stuck behind sync bundles → timeout
            //
            // NEW ORDER (WORKS for late-joiners):
            //   1. Send InitComplete (reliable) → arrives immediately (queue empty)
            //   2. Chunk sync bundles at controlled rate → spreads load over multiple frames
            //   3. Client initializes fast, sync data arrives progressively
            GONetLog.Info($"[INIT-TIMELINE] SERVER: Sending ServerSaysClientInitializationCompletion to AuthorityId {connectionToClient.OwnerAuthorityId} at {Time.ElapsedSeconds:F3}s");
            Server_SendClientIndicationOfInitializationCompletion(connectionToClient); // NOTE: sending this will cause the client to instantiate its GONetLocal
            GONetLog.Info($"[INIT-TIMELINE] SERVER: Sent ServerSaysClientInitializationCompletion to AuthorityId {connectionToClient.OwnerAuthorityId} at {Time.ElapsedSeconds:F3}s");

            // Send current state AFTER InitComplete (allows chunking without blocking initialization)
            // Use coroutine version to prevent transport buffer exhaustion (drip feed pattern)
            if (Global != null)
            {
                Global.StartCoroutine(Server_SendClientCurrentState_AllAutoMagicalSync_Coroutine(connectionToClient));
            }
            else
            {
                GONetLog.Error("[INIT-FATAL] Global (GONetGlobal) is null! Cannot start sync coroutine. Falling back to synchronous send (may drop bundles at high load).");
                Server_SendClientCurrentState_AllAutoMagicalSync(connectionToClient); // Fallback to old synchronous method
            }
            //GONetLog.Debug($"[INIT] Sent current state (all auto-magical sync values)");
        }

        /// <summary>
        /// Cleanup handler for client disconnection. Removes congestion state to prevent memory leak.
        /// </summary>
        private static void Server_OnClientDisconnected_Cleanup(GONetConnection_ServerToClient connectionToClient)
        {
            if (connectionToClient != null)
            {
                ushort authorityId = connectionToClient.OwnerAuthorityId;
                RemoveCongestionState(authorityId);
                fullStateSyncInProgress.Remove(authorityId);
                lastFullStateSyncRequestRawTicks.Remove(authorityId);
                serverReceivedGONetLocalSpawnAuthorities.TryRemove(authorityId, out _);

                // CRITICAL FIX (Dec 2025): Notify gossip system to remove the disconnected client.
                // Without this, the gossip table retains stale entries forever, logging
                // "Node X metrics are stale" warnings every frame indefinitely.
                if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableDistributedHostAuthority)
                {
                    DistributedHost.GONetGossipIntegration.OnClientDisconnected(authorityId);
                }

                if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableCongestionStateLogging)
                {
                    GONetLog.Info($"[BACKPRESSURE] Client {authorityId} disconnected - congestion state removed");
                }

                // CRITICAL FIX (Dec 2025): Clear "at rest needs to broadcast" bits when client disconnects.
                // When values go to "at rest", they're marked as needing to broadcast. If a client disconnects
                // before receiving this broadcast, the bits are never cleared. When the value changes again,
                // this triggers massive log spam: "Value was 'At Rest' but it was not broadcasted!"
                // Clearing these bits on disconnect prevents the warning spam and associated FPS degradation.
                ClearPendingAtRestBroadcastsForAllObjects();
            }
        }

        private static void Server_OnNewClientInstantiatedItsGONetLocal(GONetLocal newClientGONetLocal)
        {
            ushort authorityId = newClientGONetLocal.GONetParticipant.OwnerAuthorityId;
            GONetLog.Info($"[INIT-TIMELINE] SERVER: Server_OnNewClientInstantiatedItsGONetLocal() CALLED for AuthorityId {authorityId} at {Time.ElapsedSeconds:F3}s");

            GONetRemoteClient remoteClient = _gonetServer.GetRemoteClientByAuthorityId(authorityId);
            GONetLog.Info($"[INIT-TIMELINE] SERVER: About to set remoteClient.IsInitializedWithServer=true for AuthorityId {authorityId} at {Time.ElapsedSeconds:F3}s");

            remoteClient.IsInitializedWithServer = true;

            GONetLog.Info($"[INIT-TIMELINE] SERVER: ✅ remoteClient.IsInitializedWithServer=true SET for AuthorityId {authorityId} at {Time.ElapsedSeconds:F3}s - Client can now receive continuous sync bundles!");

            // REMOVED: Scene-defined object ID sync no longer happens immediately
            // Instead, it's triggered by SceneLoadCompleteEvent from the client after each scene finishes loading
            // This fixes race condition where scene load was async and objects didn't exist yet
            // See Server_OnClientSceneLoadComplete() and Server_SendClientSceneDefinedObjectIds_ForSpecificScene()

            // Mesh topology sync: MOVED to HandleStandbyHello (Dec 2025)
            // The broadcast now happens when we receive StandbyHello from the new client, which is when we
            // actually know their dormant server port. Calling it here was premature - the snapshot sent to
            // existing clients was empty because we didn't have the new client's dormant server info yet.
            // See GONetHotStandby.HandleStandbyHello for the new implementation.
            // if (GONetGlobal.Instance.enableDistributedHostAuthority)
            // {
            //     DistributedHost.GONetGossipIntegration.SendMeshTopologyToClient(authorityId);
            // }
        }

        /// <summary>
        /// Server-side handler for client initialization acknowledgment.
        /// Validates that the client received all expected init messages.
        /// Added 2025-11-06 to detect Steamworks (or other transport) reliable message delivery failures.
        /// See: .claude/STEAMWORKS_INIT_MESSAGE_DELIVERY_SOLUTION.md
        /// </summary>
        private static void Server_OnClientInitializationAcknowledgment(GONetEventEnvelope<ClientInitializationAcknowledgment> eventEnvelope)
        {
            if (!IsServer)
            {
                return; // Safety check - should never happen due to filter in Subscribe call
            }

            ClientInitializationAcknowledgment ackEvent = eventEnvelope.Event;
            ushort clientAuthorityId = eventEnvelope.SourceAuthorityId;

#if GONet_INIT_TRACE
            GONetLog.Info($"[InitMsgTracker] SERVER: Received acknowledgment from client {clientAuthorityId} - Client reports {ackEvent.ReceivedMessageCount} messages on channels [{string.Join(", ", ackEvent.ReceivedChannels)}]");
#endif

            // Get the tracker for this client
            GONetInitMessageTracker tracker = _gonetServer.GetOrCreateInitMessageTracker(clientAuthorityId);

            // Process acknowledgment
            tracker.ProcessAcknowledgment(ackEvent.ReceivedMessageCount, ackEvent.ReceivedChannels);

            // Validate delivery
            bool isValid = tracker.ValidateDelivery();

            if (!isValid)
            {
                // FAILURE DETECTED: Client did not receive all expected messages
                // This indicates Steamworks (or other transport) dropped reliable messages despite flags being set correctly

                // Get list of missing channels for retry
                List<GONetChannelId> missingChannels = tracker.GetMissingChannels();

                // TODO: Implement retry logic (Layer 3)
                GONetLog.Warning($"[InitMsgTracker] SERVER: Retry mechanism not yet implemented. Missing channels: [{string.Join(", ", missingChannels)}]");
            }
            else
            {
                // SUCCESS: All init messages delivered correctly
                // Dispose tracker immediately to stop recording (time sync continues but shouldn't be tracked)
                _gonetServer.RemoveInitMessageTracker(clientAuthorityId);
            }

            if (_gonetServer.TryGetRemoteClientByAuthorityId(clientAuthorityId, out GONetRemoteClient remoteClient))
            {
                remoteClient.IsInitMessageTrackingActive = false;
            }
        }

        /// <summary>
        /// Sends GONetId assignments for all scene-defined objects in currently loaded scenes to a newly connected client.
        /// This ensures late-joining clients receive the same GONetIds that were assigned to scene objects on the server.
        /// </summary>
        private static void Server_SendClientSceneDefinedObjectIds(GONetConnection_ServerToClient gonetConnection_ServerToClient)
        {
            //GONetLog.Warning($"[SCENE_SYNC] Server_SendClientSceneDefinedObjectIds - START for AuthorityId {gonetConnection_ServerToClient.OwnerAuthorityId}");
            //GONetLog.Warning($"[SCENE_SYNC] definedInSceneParticipantInstanceIDs.Count: {definedInSceneParticipantInstanceIDs.Count}");
            //GONetLog.Warning($"[SCENE_SYNC] participantInstanceID_to_SpawnSceneName.Count: {participantInstanceID_to_SpawnSceneName.Count}");
            //GONetLog.Warning($"[SCENE_SYNC] gonetParticipantByGONetIdMap.Count: {gonetParticipantByGONetIdMap.Count}");

            // Get all currently loaded scenes
            HashSet<string> loadedScenes = new HashSet<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                {
                    loadedScenes.Add(scene.name);
                    //GONetLog.Warning($"[SCENE_SYNC] Detected loaded scene: '{scene.name}'");
                }
            }

            // For each loaded scene, collect scene-defined object GONetIds
            foreach (string sceneName in loadedScenes)
            {
                //GONetLog.Warning($"[SCENE_SYNC] Processing scene '{sceneName}' for scene-defined objects...");
                List<string> designTimeLocations = new List<string>();
                List<uint> gonetIds = new List<uint>();
                List<byte[]> customInitDataList = new List<byte[]>();

                int matchedInstanceIds = 0;
                int foundParticipants = 0;

                // Find all GONetParticipants that were defined in this scene
                foreach (int instanceId in definedInSceneParticipantInstanceIDs)
                {
                    if (participantInstanceID_to_SpawnSceneName.TryGetValue(instanceId, out string participantScene) &&
                        participantScene == sceneName)
                    {
                        matchedInstanceIds++;

                        // Find the actual participant
                        foreach (var kvp in gonetParticipantByGONetIdMap)
                        {
                            GONetParticipant participant = kvp.Value;
                            if (participant != null &&
                                participant.GetInstanceID() == instanceId &&
                                participant.IsDesignTimeMetadataInitd &&
                                participant.GONetId != 0 &&
                                !string.IsNullOrEmpty(participant.DesignTimeLocation))
                            {
                                foundParticipants++;
                                designTimeLocations.Add(participant.DesignTimeLocation);
                                gonetIds.Add(participant.GONetId);

                                // Get initialization data from cache (populated during initial scene load)
                                // This ensures late-joiners receive IDENTICAL data to early joiners (no re-randomization!)
                                byte[] initData = GONetGlobal.GetCachedSceneObjectInitData(sceneName, participant.DesignTimeLocation);
                                customInitDataList.Add(initData); // Can be null if no IGONetSyncdBehaviourInitializer components

                                //GONetLog.Debug($"[SCENE_SYNC] Found scene-defined participant: GONetId {participant.GONetId}, Location: {participant.DesignTimeLocation}, Scene: {sceneName}");
                                break;
                            }
                        }
                    }
                }

                //GONetLog.Warning($"[SCENE_SYNC] Scene '{sceneName}': matchedInstanceIds={matchedInstanceIds}, foundParticipants={foundParticipants}, sending={designTimeLocations.Count}");

                if (designTimeLocations.Count > 0)
                {
                    //GONetLog.Info($"[INIT] Sending {designTimeLocations.Count} scene-defined object GONetIds for scene '{sceneName}' to newly connected client AuthorityId: {gonetConnection_ServerToClient.OwnerAuthorityId}");
                    Global.SendSceneDefinedObjectIdSync_ToSpecificClient(sceneName, designTimeLocations.ToArray(), gonetIds.ToArray(), customInitDataList.ToArray(), gonetConnection_ServerToClient.OwnerAuthorityId);
                }
                else
                {
                    Global.SendSceneDefinedObjectIdSync_ToSpecificClient(sceneName, Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<byte[]>(), gonetConnection_ServerToClient.OwnerAuthorityId);
                    GONetLog.Info($"[GONETID-EMPTY] SERVER sending empty GONetId sync for '{sceneName}' to client {gonetConnection_ServerToClient.OwnerAuthorityId} (no scene-defined objects)");
                }
            }

            //GONetLog.Warning($"[SCENE_SYNC] Server_SendClientSceneDefinedObjectIds - END for AuthorityId {gonetConnection_ServerToClient.OwnerAuthorityId}");
        }

        /// <summary>
        /// Sends GONetId assignments for scene-defined objects in a SPECIFIC scene to a client.
        /// Called when a client notifies the server that a scene load has completed.
        /// This ensures the client has fully loaded the scene before receiving GONetIds.
        /// </summary>
        private static void Server_SendClientSceneDefinedObjectIds_ForSpecificScene(string sceneName, ushort clientAuthorityId)
        {
            GONetLog.Debug($"[GONETID-REACTIVE-START] Searching for scene-defined objects in '{sceneName}' for client {clientAuthorityId} (total tracked: {definedInSceneParticipantInstanceIDs.Count}, scenes: {participantInstanceID_to_SpawnSceneName.Count})");

            List<string> designTimeLocations = new List<string>();
            List<uint> gonetIds = new List<uint>();
            List<byte[]> customInitDataList = new List<byte[]>();

            int sceneNameMismatchCount = 0;
            // Find all GONetParticipants that were defined in this specific scene
            foreach (int instanceId in definedInSceneParticipantInstanceIDs)
            {
                if (!participantInstanceID_to_SpawnSceneName.TryGetValue(instanceId, out string participantScene))
                {
                    GONetLog.Debug($"[GONETID-REACTIVE-MISS] No scene name tracked for instanceId {instanceId}");
                    continue;
                }

                if (participantScene != sceneName)
                {
                    sceneNameMismatchCount++;
                    // Only log first few mismatches to avoid spam
                    if (sceneNameMismatchCount <= 3)
                    {
                        GONetLog.Debug($"[GONETID-REACTIVE-MISMATCH] instanceId {instanceId} is in '{participantScene}', not '{sceneName}'");
                    }
                    continue;
                }

                // Find the actual participant
                foreach (var kvp in gonetParticipantByGONetIdMap)
                {
                    GONetParticipant participant = kvp.Value;
                    if (participant != null &&
                        participant.GetInstanceID() == instanceId &&
                        participant.IsDesignTimeMetadataInitd &&
                        participant.GONetId != 0 &&
                        !string.IsNullOrEmpty(participant.DesignTimeLocation))
                    {
                        designTimeLocations.Add(participant.DesignTimeLocation);
                        gonetIds.Add(participant.GONetId);

                        // Get initialization data from cache (populated during initial scene load)
                        // This ensures late-joiners receive IDENTICAL data to early joiners (no re-randomization!)
                        byte[] initData = GONetGlobal.GetCachedSceneObjectInitData(sceneName, participant.DesignTimeLocation);
                        customInitDataList.Add(initData); // Can be null if no IGONetSyncdBehaviourInitializer components

                        GONetLog.Debug($"[GONETID-REACTIVE-FOUND] Found participant for '{sceneName}': GONetId {participant.GONetId}, Location: {participant.DesignTimeLocation}");
                        break;
                    }
                }
            }

            if (sceneNameMismatchCount > 3)
            {
                GONetLog.Debug($"[GONETID-REACTIVE-MISMATCH] ... and {sceneNameMismatchCount - 3} more scene name mismatches (total: {sceneNameMismatchCount})");
            }

            if (designTimeLocations.Count == 0)
            {
                Global.SendSceneDefinedObjectIdSync_ToSpecificClient(sceneName, Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<byte[]>(), clientAuthorityId);
                GONetLog.Warning($"[GONETID-REACTIVE-EMPTY] SERVER sending empty GONetId sync for '{sceneName}' to client {clientAuthorityId} - no scene-defined objects found! (total tracked: {definedInSceneParticipantInstanceIDs.Count}, mismatches: {sceneNameMismatchCount})");
                return;
            }

            // STEP 1: Try to build compressed message using indices
            ushort[] locationIndices = new ushort[designTimeLocations.Count];
            bool canUseCompressed = true;

            for (int i = 0; i < designTimeLocations.Count; i++)
            {
                ushort locationIndex = GONetSpawnSupport_Runtime.GetDesignTimeLocationIndex(designTimeLocations[i]);

                if (locationIndex == ushort.MaxValue)
                {
                    canUseCompressed = false;
                    GONetLog.Warning($"[GONETID-COMPRESS-REACTIVE] Failed to get location index for '{designTimeLocations[i]}'");
                }

                locationIndices[i] = locationIndex;
            }

            // STEP 2: Get metadata count for client validation
            ushort expectedMetadataCount = (ushort)GONetSpawnSupport_Runtime.GetTotalMetadataCount();

            // STEP 3: Send compressed or fallback to full paths
            if (canUseCompressed)
            {
                GONetLog.Info($"[GONETID-COMPRESS-REACTIVE] SERVER sending COMPRESSED GONetId sync for '{sceneName}' to client {clientAuthorityId} - {designTimeLocations.Count} objects, {locationIndices.Length * 2} bytes indices, metadata count: {expectedMetadataCount}");
                Global.SendSceneDefinedObjectIdSync_Compressed_ToSpecificClient(sceneName, expectedMetadataCount, locationIndices, gonetIds.ToArray(), customInitDataList.ToArray(), clientAuthorityId);
            }
            else
            {
                // Fallback to full paths (current system)
                GONetLog.Info($"[GONETID-REACTIVE] SERVER sending GONetId sync for '{sceneName}' to client {clientAuthorityId} using full paths - {designTimeLocations.Count} objects (indexing failed)");
                Global.SendSceneDefinedObjectIdSync_ToSpecificClient(sceneName, designTimeLocations.ToArray(), gonetIds.ToArray(), customInitDataList.ToArray(), clientAuthorityId);
            }
        }

        /// <summary>
        /// Server-side handler for when a client notifies that a scene has finished loading.
        /// This is the CORRECT time to send scene-defined object GONetId assignments.
        /// </summary>
        private static void Server_OnClientSceneLoadComplete(GONetEventEnvelope<SceneLoadCompleteEvent> eventEnvelope)
        {
            // DIAGNOSTIC: SceneLoadComplete trace - Stage 10: Server handler invoked
            GONetLog.Info($"[SLC-TRACE-10] STAGE10_SERVER_HANDLER scene='{eventEnvelope.Event.SceneName}' srcAuth={eventEnvelope.SourceAuthorityId} isRemote={eventEnvelope.IsSourceRemote} isServer={IsServer} time={Time.ElapsedSeconds:F3}");
            GONetLog.Info($"[SceneLoadComplete] SERVER received SceneLoadCompleteEvent - IsServer={IsServer}, SourceAuthorityId={eventEnvelope.SourceAuthorityId}, IsSourceRemote={eventEnvelope.IsSourceRemote}");

            if (!IsServer)
                return;

            SceneLoadCompleteEvent evt = eventEnvelope.Event;
            ushort clientAuthorityId = eventEnvelope.SourceAuthorityId;

            // DEDUPLICATION CHECK FIRST: Need to know if early-joiner or late-joiner to decide when to resume sync
            // Proactive flow: Server broadcasts GONetIds when server loads scene (early-joiners receive this)
            // Reactive flow: Server sends GONetIds when client notifies scene load complete (late-joiners receive this)
            bool isEarlyJoiner = GONetGlobal.HasClientReceivedProactiveGonetIds(evt.SceneName, clientAuthorityId);

            // SCENE LOADING SUPPRESSION: Check loading state
            // For LATE-JOINERS: DON'T resume sync yet - wait until AFTER GONetId sync is sent to avoid race condition
            // For EARLY-JOINERS: DON'T resume sync yet - proactive coroutine will resume after sending AllCurrentValues
            // FIX (Dec 2025): Also check if proactive flow already completed (IsCurrentlyLoadingScene=false) to avoid re-suppressing
            // FIX (Jan 2026): Moved late-joiner sync resume to AFTER GONetId sync to prevent race condition
            bool shouldResumeSync = false;
            GONetRemoteClient remoteClient = null;
            if (gonetServer.TryGetRemoteClientByAuthorityId(clientAuthorityId, out remoteClient))
            {
                // FIX (Dec 2025): If IsCurrentlyLoadingScene is already false, proactive flow already completed - don't do anything
                // This fixes a race condition where SceneLoadComplete arrives (or retries) AFTER PROACTIVE-COMPLETE
                if (!remoteClient.IsCurrentlyLoadingScene)
                {
                    GONetLog.Debug($"[SCENE-LOADING-COMPLETE] Client {clientAuthorityId} finished loading '{evt.SceneName}' - sync already resumed (proactive flow completed)");
                }
                else if (!isEarlyJoiner)
                {
                    // Late-joiner: Mark for sync resume AFTER GONetId sync is sent (prevents race condition)
                    // Previously this resumed sync immediately, but unreliable bundles could arrive before GONetId sync RPC
                    shouldResumeSync = true;
                    GONetLog.Info($"[SCENE-LOADING-COMPLETE] Client {clientAuthorityId} finished loading '{evt.SceneName}' - will resume unreliable sync AFTER GONetId sync (late-joiner)");
                }
                else
                {
                    // Early-joiner: Keep sync suppressed until proactive coroutine completes
                    GONetLog.Info($"[SCENE-LOADING-COMPLETE] Client {clientAuthorityId} finished loading '{evt.SceneName}' - keeping sync suppressed (early-joiner, waiting for proactive flow)");
                }
            }

            // Early-joiners: Allow resend only if client keeps retrying (indicates missing GONetId sync)
            if (isEarlyJoiner)
            {
                int receiptCount = GONetGlobal.IncrementSceneLoadCompleteReceiptCount(evt.SceneName, clientAuthorityId);
                if (receiptCount <= 1)
                {
                    GONetLog.Debug($"[REACTIVE-SKIP] Client {clientAuthorityId} already received proactive GONetIds for '{evt.SceneName}' - skipping duplicate reactive send (early-joiner)");
                    return;
                }

                if (!GONetGlobal.ShouldResendProactiveGonetIds(evt.SceneName, clientAuthorityId))
                {
                    GONetLog.Debug($"[REACTIVE-SKIP] Client {clientAuthorityId} re-requested GONetIds for '{evt.SceneName}' too soon (early-joiner, cooldown active)");
                    return;
                }

                GONetLog.Warning($"[REACTIVE-RESEND] Client {clientAuthorityId} re-requested GONetIds for '{evt.SceneName}' ({receiptCount} SceneLoadComplete events). Resending GONetIds + AllCurrentValues.");
                Server_SendClientSceneDefinedObjectIds_ForSpecificScene(evt.SceneName, clientAuthorityId);
                Server_SendClientCurrentState_ForSceneDefinedObjects(evt.SceneName, clientAuthorityId);
                GONetGlobal.RecordProactiveGonetIdSyncSent(evt.SceneName, clientAuthorityId);
                return;
            }

            // Late-joiner path: Client connected AFTER server loaded the scene, so proactive flow didn't reach them
            GONetLog.Debug($"[REACTIVE-SEND] Client {clientAuthorityId} finished loading scene '{evt.SceneName}' - sending scene-defined object IDs (late-joiner)");

            // Send scene-defined object GONetIds for this specific scene now that client has loaded it
            Server_SendClientSceneDefinedObjectIds_ForSpecificScene(evt.SceneName, clientAuthorityId);

            // CRITICAL FIX (November 2025): Also send AllCurrentValues for scene-defined objects in this scene
            // Without this, clients that load a scene AFTER initial connection won't receive the initial position/rotation
            // values - they only get delta updates, causing objects to appear at wrong positions
            Server_SendClientCurrentState_ForSceneDefinedObjects(evt.SceneName, clientAuthorityId);

            // FIX (January 2026): Resume sync AFTER GONetId sync is sent, not before
            // This prevents a race condition where unreliable sync bundles (referencing GONetIds) arrive
            // at the client before the GONetId sync RPC, causing "not found in map" errors.
            // The GONetId sync is reliable and will be queued, but we need to ensure no unreliable
            // bundles are sent until it's been queued.
            if (shouldResumeSync && remoteClient != null)
            {
                remoteClient.IsCurrentlyLoadingScene = false;
                remoteClient.CurrentlyLoadingSceneName = null;
                GONetLog.Info($"[SCENE-LOADING-RESUME] Client {clientAuthorityId} - resuming unreliable sync NOW (after GONetId sync sent for '{evt.SceneName}')");
            }
        }

        /// <summary>
        /// CRITICAL FIX (January 2026): Server-side timeout fallback for SceneLoadCompleteEvent reliability.
        ///
        /// Under extreme network conditions (poor WiFi + CPU throttle), SceneLoadCompleteEvent can be lost
        /// despite reliable channel retries. This creates a deadlock where:
        /// - Client waits indefinitely for GONetId sync from server
        /// - Server waits indefinitely for SceneLoadCompleteEvent from client
        ///
        /// This method checks for clients that have been stuck loading a scene for too long
        /// and proactively sends GONetId assignments without waiting for the event.
        /// </summary>
        /// <param name="timeoutSeconds">Maximum time to wait for SceneLoadCompleteEvent before sending fallback (default: 15s)</param>
        internal static void Server_CheckClientLoadingTimeouts(double timeoutSeconds = 15.0)
        {
            if (!IsServer || _gonetServer == null)
                return;

            double currentTime = Time.ElapsedSeconds;

            foreach (var remoteClient in _gonetServer.remoteClients)
            {
                // Skip clients not loading or already processed
                if (!remoteClient.IsCurrentlyLoadingScene || remoteClient.HasSentLoadingTimeoutFallback)
                    continue;

                // Skip if not timed out yet
                double loadingDuration = currentTime - remoteClient.LoadingStartedTime;
                if (loadingDuration < timeoutSeconds)
                    continue;

                string sceneName = remoteClient.CurrentlyLoadingSceneName;
                ushort clientAuthorityId = remoteClient.ConnectionToClient.OwnerAuthorityId;

                GONetLog.Warning($"[SCENE-LOADING-TIMEOUT] Client {clientAuthorityId} has been stuck loading scene '{sceneName}' for {loadingDuration:F1}s (timeout: {timeoutSeconds}s). Sending fallback GONetId sync.");

                // Mark as fallback sent to prevent repeated sends
                remoteClient.HasSentLoadingTimeoutFallback = true;

                // Send GONetIds proactively without waiting for SceneLoadCompleteEvent
                Server_SendClientSceneDefinedObjectIds_ForSpecificScene(sceneName, clientAuthorityId);
                Server_SendClientCurrentState_ForSceneDefinedObjects(sceneName, clientAuthorityId);

                // Resume sync for this client (they'll get caught up)
                remoteClient.IsCurrentlyLoadingScene = false;
                remoteClient.CurrentlyLoadingSceneName = null;
                GONetLog.Info($"[SCENE-LOADING-TIMEOUT] Client {clientAuthorityId} sync resumed via fallback - scene '{sceneName}'");
            }
        }

        private static void Server_SendClientPersistentEventsSinceStart(GONetConnection_ServerToClient gonetConnection_ServerToClient)
        {
            //GONetLog.Warning($"[SPAWN_SYNC] Server_SendClientPersistentEventsSinceStart - Total persistent events: {persistentEventsThisSession.Count}");

            if (persistentEventsThisSession.Count > 0)
            {
                // TEMPORARY DEBUG: Log ALL persistent events before filtering
                /*
                GONetLog.Error($"[SPAWN_SYNC] ===== DUMPING ALL {persistentEventsThisSession.Count} PERSISTENT EVENTS BEFORE FILTERING =====");
                int debugIndex = 0;
                foreach (var evt in persistentEventsThisSession)
                {
                    string eventType = evt.GetType().Name;
                    string details = "";

                    if (evt is InstantiateGONetParticipantEvent spawnEvt)
                    {
                        details = $"InstId: {spawnEvt.GONetIdAtInstantiation}, GONetId: {spawnEvt.GONetId}, Scene: '{spawnEvt.SceneIdentifier}', DesignTimeLocation: '{spawnEvt.DesignTimeLocation}'";
                    }
                    else if (evt is DespawnGONetParticipantEvent despawnEvt)
                    {
                        details = $"GONetId: {despawnEvt.GONetId}";
                    }
                    else if (evt is SceneLoadEvent sceneLoadEvt)
                    {
                        details = $"SceneName: '{sceneLoadEvt.SceneName}', LoadType: {sceneLoadEvt.LoadType}, Mode: {sceneLoadEvt.Mode}";
                    }
                    else if (evt is SceneUnloadEvent sceneUnloadEvt)
                    {
                        details = $"SceneName: '{sceneUnloadEvt.SceneName}'";
                    }
                    else if (evt is ValueMonitoringSupport_NewBaselineEvent baselineEvt)
                    {
                        details = $"GONetId: {baselineEvt.GONetId}";
                    }
                    else if (evt is ValueMonitoringSupport_BaselineExpiredEvent expiredEvt)
                    {
                        details = $"GONetId: {expiredEvt.GONetId}";
                    }
                    else if (evt is OwnerAuthorityIdAssignmentEvent)
                    {
                        details = "(OwnerAuthorityIdAssignmentEvent - minimal data)";
                    }
                    else if (evt is PersistentRpcEvent rpcEvt)
                    {
                        details = $"RpcId: {rpcEvt.RpcId}, GONetId: {rpcEvt.GONetId}, SourceAuthority: {rpcEvt.SourceAuthorityId}, Target: {rpcEvt.OriginalTarget}";
                    }

                    GONetLog.Error($"[SPAWN_SYNC]   [{debugIndex}] {eventType} - {details}");
                    debugIndex++;
                }
                GONetLog.Error($"[SPAWN_SYNC] ===== END DUMP =====");
                */

                // Filter persistent events to only those relevant to currently loaded scenes
                //GONetLog.Error($"[SPAWN_SYNC] BEFORE FilterPersistentEventsByLoadedScenes - Count: {persistentEventsThisSession.Count}");
                LinkedList<IPersistentEvent> filteredEvents = FilterPersistentEventsByLoadedScenes(persistentEventsThisSession);
                LinkedList<IPersistentEvent> orderedEvents = OrderPersistentEventsForLateJoinerInit(filteredEvents);
                //GONetLog.Error($"[SPAWN_SYNC] AFTER FilterPersistentEventsByLoadedScenes - Filtered count: {filteredEvents.Count}");

                int totalCount = persistentEventsThisSession.Count;
                int filteredCount = orderedEvents.Count;
                //GONetLog.Warning($"[SPAWN_SYNC] *** Sending {filteredCount} of {totalCount} persistent events to newly connected client (filtered by loaded scenes) ***");

                // Log details of what we're sending
                /*
                int spawnCount = 0;
                foreach (var evt in filteredEvents)
                {
                    if (evt is InstantiateGONetParticipantEvent spawnEvt)
                    {
                        spawnCount++;
                        GONetLog.Debug($"[SPAWN_SYNC] - Sending spawn: GONetId {spawnEvt.GONetId}, InstId {spawnEvt.GONetIdAtInstantiation}, Scene: '{spawnEvt.SceneIdentifier}', DesignTimeLocation: '{spawnEvt.DesignTimeLocation}'");
                    }
                }
                GONetLog.Debug($"[SPAWN_SYNC] Total spawn events being sent: {spawnCount}");
                */

                if (filteredCount > 0)
                {
                    GONetChannelId channelId = GetInitAwareEventSinglesChannel(gonetConnection_ServerToClient);
                    PersistentEvents_Bundle bundle = new PersistentEvents_Bundle(Time.ElapsedTicks, orderedEvents);
                    int returnBytesUsedCount;

                    byte[] bytes = SerializationUtils.SerializeToBytes<IGONetEvent>(bundle, out returnBytesUsedCount, out bool doesNeedToReturn); // EXTREMELY important to include the <IGONetEvent> because there are multiple options for MessagePack to serialize this thing based on BobWad_Generated.cs' usage of [MemoryPack.MemoryPackUnion] for relevant interfaces this concrete class implements and the other end's call to deserialize will be to DeserializeBody_EventSingle and <IGONetEvent> will be used there too!!!

                    const int MAX_SERIALIZED_CHUNK_SIZE = 12 * 1024; // 12 KB per serialized chunk - safe within 16 KB transport limit
                    const int CHUNK_OVERHEAD_ESTIMATE = 32; // Overhead for PersistentEvents_BundleChunk wrapper (ChunkId, ChunkIndex, TotalChunks, OriginalBundleSize, MemoryPack metadata)
                    const int MAX_CHUNK_DATA_SIZE = MAX_SERIALIZED_CHUNK_SIZE - CHUNK_OVERHEAD_ESTIMATE; // ~12,256 bytes of actual data per chunk

                    if (returnBytesUsedCount > MAX_CHUNK_DATA_SIZE)
                    {
                        // CHUNKING PATH: Bundle too large for single message - split into chunks
                        ushort totalChunks = (ushort)((returnBytesUsedCount + MAX_CHUNK_DATA_SIZE - 1) / MAX_CHUNK_DATA_SIZE);
                        uint chunkId = GenerateUniqueChunkId();
                        /*
                        GONetLog.Warning(
                            $"[SPAWN_SYNC] SERVER: Large persistent events bundle ({returnBytesUsedCount} bytes, {filteredCount} events) " +
                            $"will be split into {totalChunks} chunks for client AuthorityId {gonetConnection_ServerToClient.OwnerAuthorityId}. " +
                            $"PERFORMANCE WARNING: Large bundles may impact network performance. " +
                            $"Consider: 1) More aggressive scene filtering, 2) Event cleanup on scene changes, 3) Shorter session duration.");
                        */
                        for (ushort chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                        {
                            int offset = chunkIndex * MAX_CHUNK_DATA_SIZE;
                            int chunkDataSize = System.Math.Min(MAX_CHUNK_DATA_SIZE, returnBytesUsedCount - offset);

                            byte[] chunkData = new byte[chunkDataSize];
                            System.Buffer.BlockCopy(bytes, offset, chunkData, 0, chunkDataSize);

                            var chunkEvent = new PersistentEvents_BundleChunk(chunkId, chunkIndex, totalChunks, chunkData, returnBytesUsedCount);

                            int chunkBytesUsedCount;
                            // CRITICAL: Must include <IGONetEvent> type parameter to ensure MemoryPack union type tag is serialized!
                            // Without this, deserialization will fail or deserialize as wrong type (see line 4361 comment)
                            byte[] chunkBytes = SerializationUtils.SerializeToBytes<IGONetEvent>(chunkEvent, out chunkBytesUsedCount, out bool doesNeedToReturnChunk);

                            // Validate chunk size is within limits
                            if (chunkBytesUsedCount > MAX_SERIALIZED_CHUNK_SIZE)
                            {
                                GONetLog.Error($"[SPAWN_SYNC] CRITICAL: Serialized chunk {chunkIndex + 1}/{totalChunks} exceeds MAX_SERIALIZED_CHUNK_SIZE! " +
                                    $"Actual: {chunkBytesUsedCount} bytes, Max: {MAX_SERIALIZED_CHUNK_SIZE} bytes. " +
                                    $"ChunkDataSize: {chunkDataSize}, Overhead: {chunkBytesUsedCount - chunkDataSize}. " +
                                    $"This will likely cause message corruption or delivery failure.");
                            }

                            //GONetLog.Info($"[SPAWN_SYNC] SERVER: Sending chunk {chunkIndex + 1}/{totalChunks} ({chunkBytesUsedCount} bytes, {chunkDataSize} data + {chunkBytesUsedCount - chunkDataSize} overhead) to AuthorityId {gonetConnection_ServerToClient.OwnerAuthorityId}");

                            SendBytesToRemoteConnection(gonetConnection_ServerToClient, chunkBytes, chunkBytesUsedCount, channelId);

                            if (doesNeedToReturnChunk)
                            {
                                SerializationUtils.ReturnByteArray(chunkBytes);
                            }
                        }

                        //GONetLog.Warning($"[SPAWN_SYNC] SERVER: All {totalChunks} chunks SENT to AuthorityId {gonetConnection_ServerToClient.OwnerAuthorityId} (ChunkId: {chunkId})");
                    }
                    else
                    {
                        // NORMAL PATH: Bundle fits in single message (< ~12 KB)
                        //GONetLog.Warning($"[SPAWN_SYNC] SERVER: Serialized PersistentEvents_Bundle - Size: {returnBytesUsedCount} bytes, Events: {filteredCount}, Channel: {GONetChannel.ClientInitialization_EventSingles_Reliable}, Target: AuthorityId {gonetConnection_ServerToClient.OwnerAuthorityId}");

                        SendBytesToRemoteConnection(gonetConnection_ServerToClient, bytes, returnBytesUsedCount, channelId);

                        //GONetLog.Warning($"[SPAWN_SYNC] SERVER: PersistentEvents_Bundle SENT to AuthorityId {gonetConnection_ServerToClient.OwnerAuthorityId}");
                    }

                    if (doesNeedToReturn)
                    {
                        SerializationUtils.ReturnByteArray(bytes);
                    }
                }
            }

            // NOTE: Channel 8 sentinel (empty bundle) is now sent via Server_SendBothInitTrackingMarkers
            // alongside the channel 9 sentinel after all init messages are queued.
            // This prevents race conditions where client receives sentinels before all init messages arrive.
            // Sentinels are only sent while init tracking is active.
            // See: INIT-MSG-FAILURE fix (January 2026)
        }

        /// <summary>
        /// Filters persistent events to only include those relevant to currently loaded scenes.
        /// <para>This prevents late-joining clients from receiving spawn events for objects in unloaded scenes.</para>
        /// </summary>
        private static LinkedList<IPersistentEvent> FilterPersistentEventsByLoadedScenes(LinkedList<IPersistentEvent> allEvents)
        {
            LinkedList<IPersistentEvent> filteredEvents = new LinkedList<IPersistentEvent>();

            // Get currently loaded scenes from server's scene manager
            HashSet<string> loadedScenes = new HashSet<string>();
            if (SceneManager != null)
            {
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (scene.isLoaded)
                    {
                        loadedScenes.Add(scene.name);
                        //GONetLog.Warning($"[SPAWN_SYNC] FilterPersistentEvents: Detected loaded scene '{scene.name}'");
                    }
                }
            }

            // Always include DontDestroyOnLoad scene (persistent across scene changes)
            loadedScenes.Add(HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE);

            // CRITICAL FIX: Track which scene load events to send based on current loaded scenes
            // We should only send SceneLoadEvent for scenes that are CURRENTLY loaded, not the entire history
            // Otherwise late-joiners receive all scene transitions and end up in wrong scenes
            HashSet<string> sceneLoadEventsSent = new HashSet<string>();

            // CRITICAL: Track which GONetIds have spawn events being sent
            // Value baseline events should ONLY be sent if the corresponding spawn is also being sent
            HashSet<uint> gonetIdsWithSpawnsBeingSent = new HashSet<uint>();

            //GONetLog.Warning($"[SPAWN_SYNC] FilterPersistentEvents: About to filter {allEvents.Count} events. Loaded scenes: {string.Join(", ", loadedScenes)}");

            // Filter events based on scene
            foreach (IPersistentEvent persistentEvent in allEvents)
            {
                bool shouldInclude = true;
                IPersistentEvent eventToAdd = persistentEvent;

                // CRITICAL: Filter SceneLoadEvent to only send for currently loaded scenes
                if (persistentEvent is SceneLoadEvent sceneLoadEvent)
                {
                    // Only include scene load if:
                    // 1. The scene is currently loaded on the server
                    // 2. We haven't already sent a load event for this scene (avoid duplicates from scene history)
                    if (loadedScenes.Contains(sceneLoadEvent.SceneName) && !sceneLoadEventsSent.Contains(sceneLoadEvent.SceneName))
                    {
                        shouldInclude = true;
                        sceneLoadEventsSent.Add(sceneLoadEvent.SceneName);
                        //GONetLog.Warning($"[SPAWN_SYNC] Including SceneLoadEvent for '{sceneLoadEvent.SceneName}' - currently loaded on server");
                    }
                    else
                    {
                        shouldInclude = false;
                        //GONetLog.Warning($"[SPAWN_SYNC] EXCLUDING SceneLoadEvent for '{sceneLoadEvent.SceneName}' - not currently loaded or already sent");
                    }
                }
                // CRITICAL: Exclude SceneUnloadEvent - these are historical and not needed for late-joiners
                // Late-joiners should only receive the CURRENT scene state, not the unload history
                else if (persistentEvent is SceneUnloadEvent sceneUnloadEvent)
                {
                    shouldInclude = false;
                    //GONetLog.Warning($"[SPAWN_SYNC] EXCLUDING SceneUnloadEvent for '{sceneUnloadEvent.SceneName}' - late-joiners only need current state");
                }
                else if (persistentEvent is PoolInitializationEvent poolInitEvent)
                {
                    List<PoolIdRangeEntry> filteredRanges = FilterPoolRanges(poolInitEvent.Ranges, loadedScenes);
                    if (filteredRanges.Count == 0)
                    {
                        shouldInclude = false;
                    }
                    else if (filteredRanges.Count != poolInitEvent.Ranges.Count)
                    {
                        eventToAdd = new PoolInitializationEvent
                        {
                            OccurredAtElapsedTicks = poolInitEvent.OccurredAtElapsedTicks,
                            Ranges = filteredRanges
                        };
                    }
                }
                else if (persistentEvent is PoolGrowthEvent poolGrowthEvent)
                {
                    List<PoolIdRangeEntry> filteredRanges = FilterPoolRanges(poolGrowthEvent.Ranges, loadedScenes);
                    if (filteredRanges.Count == 0)
                    {
                        shouldInclude = false;
                    }
                    else if (filteredRanges.Count != poolGrowthEvent.Ranges.Count)
                    {
                        eventToAdd = new PoolGrowthEvent
                        {
                            OccurredAtElapsedTicks = poolGrowthEvent.OccurredAtElapsedTicks,
                            Ranges = filteredRanges
                        };
                    }
                }
                // Check if this is a spawn event with scene information
                else if (persistentEvent is InstantiateGONetParticipantEvent spawnEvent)
                {
                    // Only include spawns from currently loaded scenes
                    if (!string.IsNullOrEmpty(spawnEvent.SceneIdentifier))
                    {
                        shouldInclude = loadedScenes.Contains(spawnEvent.SceneIdentifier);
                        if (shouldInclude)
                        {
                            gonetIdsWithSpawnsBeingSent.Add(spawnEvent.GONetId);
                            //GONetLog.Debug($"[SPAWN_SYNC] INCLUDING spawn: InstId {spawnEvent.GONetIdAtInstantiation}, Scene '{spawnEvent.SceneIdentifier}' (matches loaded scenes)");
                        }
                        else
                        {
                            //GONetLog.Warning($"[SPAWN_SYNC] EXCLUDING spawn: InstId {spawnEvent.GONetIdAtInstantiation}, Scene '{spawnEvent.SceneIdentifier}' (NOT in loaded scenes)");
                        }
                    }
                    // If no scene identifier, include it (backward compatibility for old events)
                    else
                    {
                        gonetIdsWithSpawnsBeingSent.Add(spawnEvent.GONetId);
                        //GONetLog.Debug($"[SPAWN_SYNC] INCLUDING spawn: InstId {spawnEvent.GONetIdAtInstantiation}, No SceneIdentifier (backward compat)");
                    }
                }
                else if (persistentEvent is PoolObjectBorrowEvent poolBorrowEvent)
                {
                    if (GONetPoolManager.TryGetPoolSceneIdentifier(poolBorrowEvent.GONetId, out string poolScene))
                    {
                        if (!string.IsNullOrEmpty(poolScene) && !loadedScenes.Contains(poolScene))
                        {
                            shouldInclude = false;
                        }
                    }
                }
                else if (persistentEvent is PoolObjectDestroyedEvent poolDestroyedEvent)
                {
                    if (GONetPoolManager.TryGetPoolSceneIdentifier(poolDestroyedEvent.GONetId, out string poolScene))
                    {
                        if (!string.IsNullOrEmpty(poolScene) && !loadedScenes.Contains(poolScene))
                        {
                            shouldInclude = false;
                        }
                    }
                }
                // CRITICAL: Filter value baseline events - only send if corresponding spawn is also being sent
                else if (persistentEvent is ValueMonitoringSupport_NewBaselineEvent baselineEvent)
                {
                    // ONLY send value baseline if we're also sending the spawn for this GONetId
                    uint gonetId = baselineEvent.GONetId;
                    if (!gonetIdsWithSpawnsBeingSent.Contains(gonetId))
                    {
                        shouldInclude = false;
                        //GONetLog.Warning($"[SPAWN_SYNC] EXCLUDING ValueBaseline for GONetId {gonetId} - spawn not being sent");
                    }
                }
                else if (persistentEvent is ValueMonitoringSupport_BaselineExpiredEvent expiredEvent)
                {
                    // ONLY send expired baseline if we're also sending the spawn for this GONetId
                    uint gonetId = expiredEvent.GONetId;
                    if (!gonetIdsWithSpawnsBeingSent.Contains(gonetId))
                    {
                        shouldInclude = false;
                        //GONetLog.Warning($"[SPAWN_SYNC] EXCLUDING ExpiredBaseline for GONetId {gonetId} - spawn not being sent");
                    }
                }
                // NOTE: Persistent RPCs are NOT filtered here because:
                // - GONet_GlobalContext is used as a "bucket" for RPCs without specific participant context
                // - Scene-specific components can be added to GONet_GlobalContext via GONetRuntimeComponentInitializer
                // - Filtering would break legitimate global RPCs
                // - Component-not-ready exceptions will defer RPCs until timeout (handled by deferred RPC system)
                // All other persistent events (OwnerAuthorityIdAssignment, etc.) are always included

                if (shouldInclude)
                {
                    filteredEvents.AddLast(eventToAdd);
                }
            }

            // CRITICAL FIX: Reorder events to ensure SceneLoadEvents come FIRST
            // Late-joining clients MUST receive and process SceneLoadEvent before any spawn events for that scene
            // Otherwise spawns get deferred indefinitely waiting for the scene to load
            LinkedList<IPersistentEvent> reorderedEvents = new LinkedList<IPersistentEvent>();

            // First pass: Add all SceneLoadEvents
            foreach (IPersistentEvent evt in filteredEvents)
            {
                if (evt is SceneLoadEvent)
                {
                    reorderedEvents.AddLast(evt);
                    //GONetLog.Warning($"[SPAWN_SYNC] Prioritizing SceneLoadEvent to front of bundle");
                }
            }

            // Second pass: Add all other events (preserving their relative order)
            foreach (IPersistentEvent evt in filteredEvents)
            {
                if (!(evt is SceneLoadEvent))
                {
                    reorderedEvents.AddLast(evt);
                }
            }

            //GONetLog.Warning($"[SPAWN_SYNC] FilterPersistentEvents: Reordered {filteredEvents.Count} events - SceneLoadEvents now at front");

            return reorderedEvents;
        }

        private static List<PoolIdRangeEntry> FilterPoolRanges(List<PoolIdRangeEntry> ranges, HashSet<string> loadedScenes)
        {
            var filtered = new List<PoolIdRangeEntry>(ranges.Count);
            for (int i = 0; i < ranges.Count; i++)
            {
                PoolIdRangeEntry range = ranges[i];
                if (string.IsNullOrEmpty(range.SceneIdentifier) || loadedScenes.Contains(range.SceneIdentifier))
                {
                    filtered.Add(range);
                }
            }

            return filtered;
        }

        /// <summary>
        /// Orders persistent events for late-joiner initialization to ensure deterministic processing.
        /// SceneLoad -> Instantiate -> Reparent -> Other (relative order preserved within each group).
        /// </summary>
        private static LinkedList<IPersistentEvent> OrderPersistentEventsForLateJoinerInit(LinkedList<IPersistentEvent> events)
        {
            LinkedList<IPersistentEvent> orderedEvents = new LinkedList<IPersistentEvent>();
            if (events == null || events.Count == 0)
            {
                return orderedEvents;
            }

            List<IPersistentEvent> sceneLoads = new List<IPersistentEvent>();
            List<IPersistentEvent> poolInits = new List<IPersistentEvent>();
            List<IPersistentEvent> poolGrowths = new List<IPersistentEvent>();
            List<IPersistentEvent> spawns = new List<IPersistentEvent>();
            List<IPersistentEvent> poolBorrows = new List<IPersistentEvent>();
            List<IPersistentEvent> poolDestroyed = new List<IPersistentEvent>();
            List<IPersistentEvent> reparents = new List<IPersistentEvent>();
            List<IPersistentEvent> others = new List<IPersistentEvent>();

            foreach (var persistentEvent in events)
            {
                if (persistentEvent is SceneLoadEvent)
                {
                    sceneLoads.Add(persistentEvent);
                }
                else if (persistentEvent is PoolInitializationEvent)
                {
                    poolInits.Add(persistentEvent);
                }
                else if (persistentEvent is PoolGrowthEvent)
                {
                    poolGrowths.Add(persistentEvent);
                }
                else if (persistentEvent is InstantiateGONetParticipantEvent)
                {
                    spawns.Add(persistentEvent);
                }
                else if (persistentEvent is PoolObjectBorrowEvent)
                {
                    poolBorrows.Add(persistentEvent);
                }
                else if (persistentEvent is PoolObjectDestroyedEvent)
                {
                    poolDestroyed.Add(persistentEvent);
                }
                else if (persistentEvent is ReparentGONetParticipantEvent)
                {
                    reparents.Add(persistentEvent);
                }
                else
                {
                    others.Add(persistentEvent);
                }
            }

            foreach (var persistentEvent in sceneLoads) orderedEvents.AddLast(persistentEvent);
            foreach (var persistentEvent in poolInits) orderedEvents.AddLast(persistentEvent);
            foreach (var persistentEvent in poolGrowths) orderedEvents.AddLast(persistentEvent);
            foreach (var persistentEvent in spawns) orderedEvents.AddLast(persistentEvent);
            foreach (var persistentEvent in poolBorrows) orderedEvents.AddLast(persistentEvent);
            foreach (var persistentEvent in poolDestroyed) orderedEvents.AddLast(persistentEvent);
            foreach (var persistentEvent in reparents) orderedEvents.AddLast(persistentEvent);
            foreach (var persistentEvent in others) orderedEvents.AddLast(persistentEvent);

            return orderedEvents;
        }

        private static void Server_AssignNewClientAuthorityId(GONetConnection_ServerToClient connectionToClient)
        {
            // first assign locally
            connectionToClient.OwnerAuthorityId = ++server_lastAssignedAuthorityId;
            _gonetServer.OnConnectionToClientAuthorityIdAssigned(connectionToClient, connectionToClient.OwnerAuthorityId); // TODO this should automatically happen via event...i.e., update the setter above to do event stuff on change!

            // then send the assignment to the client
            using (BitByBitByteArrayBuilder bitStream = BitByBitByteArrayBuilder.GetBuilder())
            {
                { // header...just message type/id...well, and now time 
                    uint messageID = messageTypeToMessageIDMap[typeof(OwnerAuthorityIdAssignmentEvent)];
                    bitStream.WriteUInt(messageID);

                    bitStream.WriteLong(Time.ElapsedTicks);
                }

                { // body
                    //GONetLog.Info($"[INIT] SERVER: About to write OwnerAuthorityId - Value: {connectionToClient.OwnerAuthorityId}, BitCount: {GONetParticipant.OWNER_AUTHORITY_ID_BIT_COUNT_USED}, BitStream Position Before: {bitStream.Position_Bytes} bytes {bitStream.Position_Bits} bits");
                    bitStream.WriteUShort(connectionToClient.OwnerAuthorityId, GONetParticipant.OWNER_AUTHORITY_ID_BIT_COUNT_USED);
                    //GONetLog.Info($"[INIT] SERVER: After write OwnerAuthorityId - BitStream Position: {bitStream.Position_Bytes} bytes {bitStream.Position_Bits} bits");
                    bitStream.WriteLong(SessionGUID);
                    //GONetLog.Info($"[INIT] SERVER: After write SessionGUID - BitStream Position: {bitStream.Position_Bytes} bytes {bitStream.Position_Bits} bits");
                }

                //GONetLog.Info($"[INIT] SERVER: About to WriteCurrentPartialByte - BitStream Position: {bitStream.Position_Bytes} bytes {bitStream.Position_Bits} bits");
                bitStream.WriteCurrentPartialByte();
                //GONetLog.Info($"[INIT] SERVER: After WriteCurrentPartialByte - BitStream Position: {bitStream.Position_Bytes} bytes {bitStream.Position_Bits} bits, TotalBytes: {bitStream.Length_WrittenBytes}");

                // Dump the raw bytes being sent
                byte[] buffer = bitStream.GetBuffer();
                string hex = System.BitConverter.ToString(buffer, 0, bitStream.Length_WrittenBytes);
                //GONetLog.Info($"[INIT] SERVER: Sending message - Bytes: {hex}");

                SendBytesToRemoteConnection(connectionToClient, buffer, bitStream.Length_WrittenBytes, GONetChannel.ClientInitialization_CustomSerialization_Reliable);
            }
        }

        private static void Server_AssignNewClientGONetIdRawBatch(GONetConnection_ServerToClient connectionToClient)
        {
            var @event = new ClientRemotelyControlledGONetIdServerBatchAssignmentEvent();
            uint batchStart = GONetIdBatchManager.Server_AllocateNewBatch(lastAssignedGONetIdRaw);
            @event.GONetIdRawBatchStart = batchStart;

            lastAssignedGONetIdRaw = batchStart + (uint)GONetIdBatchManager.GetBatchSize() - 1; // Skip to end of batch to prevent ID collision

            // Include server's lastAssignedGONetIdRaw so client can sync its own value
            // This prevents client-owned objects from using IDs that might collide with future batches
            @event.ServerLastAssignedGONetIdRaw = lastAssignedGONetIdRaw;

            // HOST MODE FIX: For loopback connections, directly add the batch to the client-side batch manager
            // instead of going through EventBus.Publish which may not route correctly in host mode.
            // The loopback connection shares the same process, so we can call the batch manager directly.
            if (connectionToClient is GONetConnection_ClientHostLoopback)
            {
                GONetLog.Info($"[GONetIdBatch] HOST: Directly assigning batch [{batchStart}] to local client (loopback optimization)");
                GONetIdBatchManager.Client_AddBatch(batchStart);
                Client_OnBatchReceived_ProcessDeferredSpawns();
                return;
            }

            EventBus.Publish(@event, targetClientAuthorityId: connectionToClient.OwnerAuthorityId);
        }

        private static void Client_AssignNewClientGONetIdRawBatch(
            GONetEventEnvelope<ClientRemotelyControlledGONetIdServerBatchAssignmentEvent> eventEnvelope)
        {
            if (IsClient)
            {
                uint batchStart = eventEnvelope.Event.GONetIdRawBatchStart;
                GONetIdBatchManager.Client_AddBatch(batchStart);

                // CRITICAL FIX (December 2025): Sync client's lastAssignedGONetIdRaw with server's value.
                // This ensures client-owned objects use IDs AFTER the batch range,
                // preventing collisions between client-owned and server-owned objects.
                // The server includes its lastAssignedGONetIdRaw which is set to the batch end,
                // so the client's next ID will be batch_end + 1.
                uint serverLastId = eventEnvelope.Event.ServerLastAssignedGONetIdRaw;
                if (lastAssignedGONetIdRaw < serverLastId)
                {
                    //GONetLog.Debug($"[GONetIdBatch] CLIENT syncing lastAssignedGONetIdRaw: {lastAssignedGONetIdRaw} → {serverLastId} (from server)");
                    lastAssignedGONetIdRaw = serverLastId;
                }

                // CRITICAL: Process limbo queue when batch arrives
                Client_OnBatchReceived_ProcessDeferredSpawns();
            }
        }

        /// <summary>
        /// CLIENT: Requests a new GONetId batch from the server when running low on IDs.
        /// Called automatically when remaining IDs drop below threshold (< 20).
        /// </summary>
        private static void Client_RequestNewGONetIdBatch()
        {
            if (!IsClient || GONetClient == null || GONetClient.connectionToServer == null)
            {
                GONetLog.Error("[GONetIdBatch] CLIENT: Cannot request new batch - not connected to server");
                return;
            }

            GONetLog.Info("[GONetIdBatch] CLIENT requesting new batch from server due to low ID count");

            // Create request event to send to server
            var requestEvent = new ClientRemotelyControlledGONetIdServerBatchRequestEvent();
            EventBus.Publish(requestEvent, targetClientAuthorityId: OwnerAuthorityId_Server);
        }

        /// <summary>
        /// SERVER: Handles client request for additional GONetId batch when running low.
        /// </summary>
        private static void Server_HandleClientBatchRequest(
            GONetEventEnvelope<ClientRemotelyControlledGONetIdServerBatchRequestEvent> eventEnvelope)
        {
            if (!IsServer || _gonetServer == null)
            {
                return; // Ignore if not server
            }

            ushort requestingClientAuthorityId = eventEnvelope.SourceAuthorityId;
            GONetLog.Info($"[GONetIdBatch] SERVER received batch request from client {requestingClientAuthorityId}");

            // HOST MODE FIX: In host mode, the client side has MyAuthorityId == OwnerAuthorityId_Server (1023)
            // but the loopback connection was assigned a different authority ID (the first sequential ID).
            // Check for loopback connection when request comes from server's own authority ID.
            if (requestingClientAuthorityId == OwnerAuthorityId_Server && IsHost)
            {
                // For host mode, directly assign batch to local client (same process, no network needed)
                uint batchStart = GONetIdBatchManager.Server_AllocateNewBatch(lastAssignedGONetIdRaw);
                lastAssignedGONetIdRaw = batchStart + (uint)GONetIdBatchManager.GetBatchSize() - 1;

                GONetLog.Info($"[GONetIdBatch] HOST: Directly assigning additional batch [{batchStart}] to local client");
                GONetIdBatchManager.Client_AddBatch(batchStart);
                Client_OnBatchReceived_ProcessDeferredSpawns();
                return;
            }

            // Find the connection for this client
            GONetConnection_ServerToClient connectionToClient = null;
            uint numConnections = _gonetServer.numConnections;

            for (int i = 0; i < numConnections; ++i)
            {
                GONetConnection_ServerToClient connection = _gonetServer.remoteClients[i].ConnectionToClient;
                if (connection.OwnerAuthorityId == requestingClientAuthorityId)
                {
                    connectionToClient = connection;
                    break;
                }
            }

            if (connectionToClient != null)
            {
                Server_AssignNewClientGONetIdRawBatch(connectionToClient);
            }
            else
            {
                GONetLog.Error($"[GONetIdBatch] SERVER could not find connection for client {requestingClientAuthorityId}");
            }
        }

        private class ChunkReassemblyState
        {
            public byte[] CompleteData;
            public bool[] ReceivedChunks;
            public ushort TotalChunks;
            public int ReceivedCount;
            public double TimeStarted;
            public int OriginalSize;
        }

        static readonly System.Collections.Generic.Dictionary<uint, ChunkReassemblyState> pendingChunkReassembly = new System.Collections.Generic.Dictionary<uint, ChunkReassemblyState>();

        /// <summary>
        /// CLIENT: Handles incoming chunk of a large persistent events bundle.
        /// Reassembles chunks and processes the complete bundle when all chunks received.
        /// </summary>
        private static void OnPersistentEventsChunkReceived(GONetEventEnvelope<PersistentEvents_BundleChunk> envelope)
        {
            if (!IsClient)
            {
                return; // Only clients receive chunks from server
            }

            var chunk = envelope.Event;
            uint chunkId = chunk.ChunkId;

            //GONetLog.Info($"[SPAWN_SYNC] CLIENT: Received chunk {chunk.ChunkIndex + 1}/{chunk.TotalChunks} (ChunkId: {chunkId}, Size: {chunk.ChunkData.Length} bytes)");

            ChunkReassemblyState reassembly;
            if (!pendingChunkReassembly.TryGetValue(chunkId, out reassembly))
            {
                // First chunk for this message - initialize reassembly state
                reassembly = new ChunkReassemblyState
                {
                    CompleteData = new byte[chunk.OriginalBundleSize],
                    ReceivedChunks = new bool[chunk.TotalChunks],
                    TotalChunks = chunk.TotalChunks,
                    ReceivedCount = 0,
                    TimeStarted = Time.ElapsedSeconds,
                    OriginalSize = chunk.OriginalBundleSize
                };
                pendingChunkReassembly[chunkId] = reassembly;

                //GONetLog.Info($"[SPAWN_SYNC] CLIENT: Started reassembly for ChunkId {chunkId} ({chunk.TotalChunks} total chunks, {chunk.OriginalBundleSize} bytes)");
            }

            // Validate chunk consistency
            if (chunk.TotalChunks != reassembly.TotalChunks)
            {
                GONetLog.Error($"[SPAWN_SYNC] CLIENT: Chunk TotalChunks mismatch! Expected {reassembly.TotalChunks}, got {chunk.TotalChunks}. ChunkId: {chunkId}");
                pendingChunkReassembly.Remove(chunkId); // Abort this reassembly
                return;
            }

            if (chunk.OriginalBundleSize != reassembly.OriginalSize)
            {
                GONetLog.Error($"[SPAWN_SYNC] CLIENT: Chunk OriginalSize mismatch! Expected {reassembly.OriginalSize}, got {chunk.OriginalBundleSize}. ChunkId: {chunkId}");
                pendingChunkReassembly.Remove(chunkId);
                return;
            }

            // Check for duplicate chunk
            if (reassembly.ReceivedChunks[chunk.ChunkIndex])
            {
                GONetLog.Warning($"[SPAWN_SYNC] CLIENT: Duplicate chunk {chunk.ChunkIndex} received for ChunkId {chunkId} - ignoring");
                return;
            }

            // Copy chunk data into complete buffer
            // CRITICAL FIX: Must match server's MAX_CHUNK_DATA_SIZE calculation!
            // Server uses: MAX_SERIALIZED_CHUNK_SIZE (12KB) - CHUNK_OVERHEAD_ESTIMATE (32 bytes) = 12,256 bytes per chunk
            // Old code used 12,288 bytes (12KB), causing 32-byte misalignment that corrupted reassembled data!
            const int MAX_CHUNK_DATA_SIZE = (12 * 1024) - 32; // 12,256 bytes - MUST match server's chunking logic
            int offset = chunk.ChunkIndex * MAX_CHUNK_DATA_SIZE;

            System.Buffer.BlockCopy(chunk.ChunkData, 0, reassembly.CompleteData, offset, chunk.ChunkData.Length);

            // Mark chunk as received
            reassembly.ReceivedChunks[chunk.ChunkIndex] = true;
            reassembly.ReceivedCount++;

            //GONetLog.Info($"[SPAWN_SYNC] CLIENT: Reassembly progress: {reassembly.ReceivedCount}/{reassembly.TotalChunks} chunks received (ChunkId: {chunkId})");

            // Check if reassembly complete
            if (reassembly.ReceivedCount == reassembly.TotalChunks)
            {
                double reassemblyTime = Time.ElapsedSeconds - reassembly.TimeStarted;
                //GONetLog.Warning($"[SPAWN_SYNC] CLIENT: Reassembly COMPLETE for ChunkId {chunkId} ({reassembly.TotalChunks} chunks, {reassembly.OriginalSize} bytes, {reassemblyTime:F2}s)");

                // Deserialize the complete bundle
                // CRITICAL: Must deserialize as IGONetEvent (matching server's serialization on line 4369)
                // then cast to PersistentEvents_Bundle, because the original bundle was serialized with union type tags
                try
                {
                    IGONetEvent deserializedEvent = SerializationUtils.DeserializeFromBytes<IGONetEvent>(reassembly.CompleteData);

                    if (deserializedEvent is PersistentEvents_Bundle completeBundle)
                    {
                        //GONetLog.Warning($"[SPAWN_SYNC] CLIENT: Successfully deserialized reassembled bundle ({completeBundle.PersistentEvents.Count} events)");

                        // Process the complete bundle through the normal persistent events handler
                        var bundleEnvelope = GONetEventEnvelope<PersistentEvents_Bundle>.Borrow(completeBundle, envelope.SourceAuthorityId, null);
                        OnPersistentEventsBundle_ProcessAll_Remote(bundleEnvelope);
                        GONetEventEnvelope<PersistentEvents_Bundle>.Return(bundleEnvelope);
                    }
                    else
                    {
                        GONetLog.Error($"[SPAWN_SYNC] CLIENT: Reassembled event is not PersistentEvents_Bundle! Type: {deserializedEvent?.GetType().Name ?? "null"}, ChunkId: {chunkId}");
                    }
                }
                catch (System.Exception ex)
                {
                    GONetLog.Error($"[SPAWN_SYNC] CLIENT: FAILED to deserialize reassembled bundle! ChunkId: {chunkId}, Size: {reassembly.OriginalSize} bytes, Error: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    // Clean up reassembly state
                    pendingChunkReassembly.Remove(chunkId);
                }
            }
        }

        /// <summary>
        /// For every runtime instance of <see cref="GONetParticipant"/>, there will be one and only one item in one and only one of the <see cref="activeAutoSyncCompanionsByCodeGenerationIdMap"/>'s <see cref="Dictionary{TKey, TValue}.Values"/>.
        /// The key into this is the <see cref="GONetParticipant.CodeGenerationId"/>.
        /// </summary>
        static readonly Dictionary<GONetCodeGenerationId, Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated>> activeAutoSyncCompanionsByCodeGenerationIdMap =
            new Dictionary<GONetCodeGenerationId, Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated>>(byte.MaxValue);
        static readonly Dictionary<GONetCodeGenerationId, Dictionary<uint, GONetParticipant_AutoMagicalSyncCompanion_Generated>> activeAutoSyncCompanionsByCodeGenerationIdMap_uintKeyForPerformance =
            new Dictionary<GONetCodeGenerationId, Dictionary<uint, GONetParticipant_AutoMagicalSyncCompanion_Generated>>(byte.MaxValue);

        static readonly Dictionary<SyncBundleUniqueGrouping, AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable> autoSyncProcessingSupportByFrequencyMap =
            new Dictionary<SyncBundleUniqueGrouping, AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable>(5);

        static readonly List<AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable> autoSyncProcessingSupports_UnityMainThread =
            new List<AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable>(5);

        // TODO FIXME make internal just for editor!
        public static GONetParticipant_AutoMagicalSyncCompanion_Generated GetSyncCompanionByGNP(GONetParticipant gnp)
        {
            GONetParticipant_AutoMagicalSyncCompanion_Generated syncCompanion = null;

            Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> collection;
            if (activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(gnp.CodeGenerationId, out collection))
            {
                collection.TryGetValue(gnp, out syncCompanion);
            }

            return syncCompanion;
        }

        #endregion

    }
}
