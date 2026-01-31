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

using GONet.Core;
using GONet.Generation;
using GONet.Jobs;
using GONet.Utils;
using ReliableNetcode;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;
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
        public const ulong noIdeaWhatThisShouldBe_CopiedFromTheirUnitTest = 0x1122334455667788L;

        public static readonly byte[] _privateKey = new byte[] // TODO generate this!?
        {
            0x60, 0x6a, 0xbe, 0x6e, 0xc9, 0x19, 0x10, 0xea,
            0x9a, 0x65, 0x62, 0xf6, 0x6f, 0x2b, 0x30, 0xe4,
            0x43, 0x71, 0xd6, 0x2c, 0xd1, 0x99, 0x27, 0x26,
            0x6b, 0x3c, 0x60, 0xf4, 0xb7, 0x15, 0xab, 0xa1,
        };


        public static GONetGlobal Global { get; private set; }

        /// <summary>
        /// Adaptive pool scaler manages dynamic pool sizing based on network demand.
        /// Initialized during InitOnUnityMainThread() with settings from GONetGlobal.
        /// </summary>
        private static GONetAdaptivePoolScaler adaptivePoolScaler;

#if UNITY_EDITOR
        #region Runtime GNP Validation (Editor Only)

        /// <summary>
        /// [EDITOR ONLY] Whether to validate GONetParticipants at runtime against dirty reasons.
        /// True if the dirty warnings file exists, indicating design-time changes since last build.
        /// </summary>
        private static bool shouldValidateUnknownGNPs = false;

        /// <summary>
        /// [EDITOR ONLY] Path to the dirty warnings file. Cached once at startup.
        /// </summary>
        private static string dirtyWarningsFilePath = null;

        /// <summary>
        /// [EDITOR ONLY] Set of problematic GNP paths parsed from the dirty reasons file.
        /// Paths use GONet's location format: scene://, project://, addressables://
        /// </summary>
        private static HashSet<string> problematicGNPPaths = null;

        /// <summary>
        /// [EDITOR ONLY] Maps problematic paths to their specific dirty reason for better error messages.
        /// </summary>
        private static Dictionary<string, string> problematicGNPReasons = null;

        /// <summary>
        /// [EDITOR ONLY] Structured dirty reason info for smarter validation.
        /// </summary>
        private class DirtyReasonInfo
        {
            public string Path;
            public string UniqueScenePath; // The guaranteed unique scene path from [UniqueScenePath=...] tag
            public string FullReason;
            public DirtyActionType ActionType;
            public bool IsScenePath;
            public bool IsProjectPath;
            public bool IsAddressablePath;
            public bool WasValidated;
            public bool IsStillValid;
        }

        /// <summary>
        /// [EDITOR ONLY] Types of dirty actions that can be detected.
        /// </summary>
        private enum DirtyActionType
        {
            Unknown,
            Added,
            Removed,
            Deleted,
            Modified,
            Awakened,
            Enabled,
            Disabled,
            MovedOut
        }

        /// <summary>
        /// [EDITOR ONLY] List of structured dirty reason info for smart validation.
        /// </summary>
        private static List<DirtyReasonInfo> dirtyReasonInfos = null;

        /// <summary>
        /// [EDITOR ONLY] Count of non-path-specific dirty reasons (build settings changes, etc.)
        /// </summary>
        private static int nonPathSpecificDirtyReasonCount = 0;

        #endregion
#endif

        /// <summary>
        /// Manages networked scene loading and unloading.
        /// Server-authoritative: only server can initiate scene changes.
        /// Access from GONetBehaviour via this.SceneManager property.
        /// </summary>
        public static GONetSceneManager SceneManager { get; private set; }

        /// <summary>
        /// GONet v2: Structure-of-Arrays for high-performance non-authority object blending.
        /// Lock-free ring buffers + Burst-compiled parallel jobs + batched Transform writes.
        /// Replaces v1 per-object event-driven blending (6-9× CPU reduction for 100+ objects).
        /// NOTE: Must be a field (not property) because struct members need to be modified.
        /// </summary>
        public static NonAuthorityBlendingSoA_Final SoAData;

        /// <summary>
        /// DIAGNOSTIC (December 2025): Enable detailed reliable transport logging.
        /// When enabled, logs sequence numbers, ACKs, retransmissions at the ReliableNetcode layer.
        /// Use this to diagnose spawn event loss or other reliable message delivery issues.
        /// WARNING: High-volume logging - only enable for debugging specific issues!
        /// </summary>
        public static bool EnableDetailedReliableTransportLogging
        {
            get => ReliableNetcode.ReliableMessageChannel.EnableDetailedReliableLogging;
            set => ReliableNetcode.ReliableMessageChannel.EnableDetailedReliableLogging = value;
        }

        /// <summary>
        /// DIAGNOSTIC (December 2025): Enable SceneLoadComplete event tracing.
        /// Traces the full path of SceneLoadCompleteEvent through the transport layer.
        /// Auto-enabled - set to false to disable.
        /// </summary>
        public static bool EnableSceneLoadCompleteTracing = true;

        /// <summary>
        /// <para><b>🔥 CRITICAL API FOR HIGH-LOAD SCENARIOS (December 2025)</b></para>
        /// <para>
        /// Process transport-level network events immediately. Call this during heavy operations
        /// to prevent RTT inflation and message queue backup.
        /// </para>
        ///
        /// <para><b>TRANSPORT-AGNOSTIC:</b> This method automatically routes to the correct
        /// transport implementation (Steamworks or NetcodeIO).</para>
        ///
        /// <para><b>WHY THIS MATTERS:</b></para>
        /// <list type="bullet">
        ///   <item><description>Steamworks is main-thread bound - callbacks only process when Unity updates</description></item>
        ///   <item><description>During blocking operations (scene loads, heavy instantiation), internal buffers back up</description></item>
        ///   <item><description>Calling this method during heavy loops keeps the network alive</description></item>
        /// </list>
        ///
        /// <para><b>WHEN TO USE:</b></para>
        /// <list type="bullet">
        ///   <item><description>Inside async scene loading loops (<c>while (!asyncOp.isDone)</c>)</description></item>
        ///   <item><description>During time-sliced object instantiation</description></item>
        ///   <item><description>Any loop that blocks the main thread for &gt;50ms</description></item>
        /// </list>
        ///
        /// <para><b>EXAMPLE - Async Scene Loading:</b></para>
        /// <code>
        /// IEnumerator LoadLevel() {
        ///     // Use GONetSceneManager for networked scene loading
        ///     var asyncOp = GONetSceneManager.LoadSceneAsync("Game");
        ///     while (!asyncOp.isDone) {
        ///         GONetMain.ProcessNetworkEvents(); // Keep network alive!
        ///         yield return null;
        ///     }
        /// }
        /// </code>
        ///
        /// <para><b>EXAMPLE - Time-Sliced Instantiation:</b></para>
        /// <code>
        /// IEnumerator SpawnObjects(List&lt;GameObject&gt; prefabs) {
        ///     // Use GONet's high-resolution time for accurate frame budgeting
        ///     long startTicks = GONetMain.Time.ElapsedTicks;
        ///     long frameBudgetTicks = 8 * TimeSpan.TicksPerMillisecond; // 8ms
        ///     foreach (var prefab in prefabs) {
        ///         Instantiate(prefab);
        ///         // Yield every 8ms to keep main thread responsive
        ///         if (GONetMain.Time.ElapsedTicks - startTicks > frameBudgetTicks) {
        ///             GONetMain.ProcessNetworkEvents();
        ///             yield return null;
        ///             startTicks = GONetMain.Time.ElapsedTicks;
        ///         }
        ///     }
        /// }
        /// </code>
        ///
        /// <para><b>THREAD SAFETY:</b> Must be called from main thread only.</para>
        /// <para><b>PERFORMANCE:</b> Safe to call frequently - no-op if no events pending.</para>
        /// </summary>
        public static void ProcessNetworkEvents()
        {
            // Steamworks transport: Process Steam callbacks
            // This is the most important one since Steamworks is main-thread bound
#if !UNITY_WEBGL
            try
            {
                if (GONetSteamManager.IsInitialized)
                {
                    GONetSteamManager.ProcessNetworkEvents();
                }
            }
            catch
            {
                // Steamworks not available or not initialized - ignore
            }
#endif

            // NetcodeIO: No explicit polling needed - runs on separate thread
            // But we can process any queued main-thread actions
            if (GONetClient != null)
            {
                try
                {
                    GONetClient.Transport?.Update();
                }
                catch
                {
                    // Transport not ready - ignore
                }
            }

            if (_gonetServer != null)
            {
                try
                {
                    _gonetServer.Transport?.Update();
                }
                catch
                {
                    // Transport not ready - ignore
                }
            }
        }

        /// <summary>
        /// DIAGNOSTIC: Unique ID generator for tracking messages through the system.
        /// </summary>
        private static long _diagnosticMessageIdCounter = 0;
        internal static long GetNextDiagnosticMessageId() => System.Threading.Interlocked.Increment(ref _diagnosticMessageIdCounter);

        /// <summary>
        /// GONet v2: O(1) lookup for position stream registration.
        /// Maps GONetId → (streamIndex, objectIndex). Eliminates O(n) linear search in SoA_WritePositionUpdate.
        /// </summary>
        private static Dictionary<uint, (int streamIndex, int objectIndex)> soaPositionLookup;

        /// <summary>
        /// GONet v2: O(1) lookup for rotation stream registration.
        /// Maps GONetId → (streamIndex, objectIndex). Eliminates O(n) linear search in SoA_WriteRotationUpdate.
        /// </summary>
        private static Dictionary<uint, (int streamIndex, int objectIndex)> soaRotationLookup;

        /// <summary>
        /// GONet v2: O(1) lookup for scalar stream registration.
        /// Maps GONetId → (streamIndex, objectIndex). Eliminates O(n) linear search in SoA_WriteScalarUpdate.
        /// </summary>
        private static Dictionary<uint, (int streamIndex, int objectIndex)> soaScalarLookup;

        /// <summary>
        /// GONet v2: O(1) lookup for Vector2 stream registration.
        /// Maps (GONetId, memberIndex) → (streamIndex, objectIndex).
        /// </summary>
        private static Dictionary<(uint gonetId, byte memberIndex), (int streamIndex, int objectIndex)> soaVector2Lookup;

        /// <summary>
        /// GONet v2: O(1) lookup for Vector4 stream registration.
        /// Maps (GONetId, memberIndex) → (streamIndex, objectIndex).
        /// </summary>
        private static Dictionary<(uint gonetId, byte memberIndex), (int streamIndex, int objectIndex)> soaVector4Lookup;

        /// <summary>
        /// Queue for delayed SoA re-registration after transform mapping mismatches.
        /// Prevents modifying SoA streams while Apply() is iterating.
        /// </summary>
        private static readonly Queue<GONetParticipant> soaReRegisterQueue = new Queue<GONetParticipant>();
        private static readonly HashSet<GONetParticipant> soaReRegisterSet = new HashSet<GONetParticipant>();
        private const int MAX_SOA_REREG_PER_FRAME = 16;

        private static GONetSessionContext globalSessionContext;
        public static GONetSessionContext GlobalSessionContext
        {
            get { return globalSessionContext; }
            private set
            {
                globalSessionContext = value;
                GlobalSessionContext_Participant = (object)globalSessionContext == null ? null : globalSessionContext.gameObject.GetComponent<GONetParticipant>();
            }
        }

        public static GONetParticipant GlobalSessionContext_Participant { get; private set; }

        public static GONetLocal myLocal;
        public static GONetLocal MyLocal // TODO FIXME: setting this will be a problem  when a server can be/have a client as well!!!
        {
            get => myLocal;
            private set
            {
                myLocal = value;
                MySessionContext = value == null ? null : value.GetComponent<GONetSessionContext>();
            }
        }

        /// <summary>
        /// When a <see cref="GONetParticipant"/> could not be looked up with <paramref name="currentGONetId"/>, then we will try another way here with all info passed in.
        /// </summary>
        internal static GONetParticipant DeriveGNPFromCurrentAndPreviousValues(uint currentGONetId, ushort previousOwnerAuthorityId, ushort currentOwnerAuthorityId)
        {
            uint presumedGONetIdThatWillBeFound = (currentGONetId ^ currentOwnerAuthorityId) | previousOwnerAuthorityId;
            if (gonetParticipantByGONetIdMap.ContainsKey(presumedGONetIdThatWillBeFound))
            {
                return gonetParticipantByGONetIdMap[presumedGONetIdThatWillBeFound];
            }
            else if (gonetParticipantByGONetIdAtInstantiationMap.ContainsKey(presumedGONetIdThatWillBeFound))
            {
                return gonetParticipantByGONetIdAtInstantiationMap[presumedGONetIdThatWillBeFound];
            }
            return null;
        }

        public const long SessionGUID_Unset = default;
        static long sessionGUID = SessionGUID_Unset;
        public static long SessionGUID
        {
            get => sessionGUID;
            private set
            {
                if (sessionGUID == SessionGUID_Unset)
                {
                    sessionGUID = value;
                }
                else
                {
                    const string SUIDX = "For some reason, something is attempting to change the SessionGUID; however this is not allowed.  This could be due to host migration, which is not currently support...so, Hmmm....";
                    GONetLog.Warning(SUIDX);
                }
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Resets static state when entering play mode in Fast Iteration Mode (domain reload disabled).
        /// Without this, static fields like sessionGUID persist across play sessions causing sync issues.
        ///
        /// IMPORTANT: Uses [InitializeOnEnterPlayMode] instead of [RuntimeInitializeOnLoadMethod(SubsystemRegistration)]
        /// because SubsystemRegistration only runs once per domain load. With domain reload disabled,
        /// subsequent play mode entries would not trigger the reset. InitializeOnEnterPlayMode runs
        /// on EVERY play mode entry, ensuring clean state for each session.
        /// </summary>
        [UnityEditor.InitializeOnEnterPlayMode]
        private static void ResetStaticsOnPlayMode()
        {
            // Reset GONetLog first to ensure logging works for the rest of the reset
            GONetLog.ResetForNewSession();

            // Reset time utilities (HighResolutionTimeUtils handles its own SubsystemRegistration
            // but we call it here explicitly for Fast Iteration Mode)
            HighResolutionTimeUtils.ResetOnPlayMode();

            // Reset GONetThreading to clear any pending callbacks
            GONetThreading.ResetForNewSession();

            // Reset the time secretary (SecretaryOfTemporalAffairs)
            SecretaryOfTemporalAffairs.ResetStaticsForTesting();

            // Reset main GONet state
            ResetForNewSession();
        }

        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterPlayModeExitPoolSummary()
        {
            UnityEditor.EditorApplication.playModeStateChanged -= OnEditorPlayModeStateChanged_PoolSummary;
            UnityEditor.EditorApplication.playModeStateChanged += OnEditorPlayModeStateChanged_PoolSummary;
        }

        private static void OnEditorPlayModeStateChanged_PoolSummary(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
            {
                GONetPoolManager.LogPoolSummary("ExitPlayMode");
            }
        }
#endif

        /// <summary>
        /// Stops background threads and disconnects transports before clearing static state.
        /// This avoids cross-session leaks in Fast Iteration Mode and lobby flow resets.
        /// </summary>
        private static void ShutdownForNewSession()
        {
            // Emit pool summary before teardown to capture end-of-session metrics.
            GONetPoolManager.LogPoolSummary("ShutdownForNewSession");

            // Unsubscribe per-session Unity events to prevent duplicate handlers across sessions
            Application.quitting -= Application_quitting_TakeNote;

            // Stop distributed host systems (safe to call even if not initialized)
            DistributedHost.GONetGossipIntegration.Shutdown();

            // Stop auto-sync processing threads early to avoid queue races during reset
            foreach (var kvp in autoSyncProcessingSupportByFrequencyMap)
            {
                kvp.Value?.Dispose();
            }

            // Stop network worker threads
            isRunning_endOfTheLineSend_Thread = false;
#if !PERF_NO_PROCESS_SYNC_EVENTS
            isRunning_databaseSave_Thread = false;
#endif

            if (endOfLineSendThread != null)
            {
                if (endOfLineSendThread.IsAlive)
                {
                    endOfLineSendThread.Join(250);
                }
                if (!endOfLineSendThread.IsAlive)
                {
                    endOfLineSendThread = null;
                }
            }

#if !PERF_NO_PROCESS_SYNC_EVENTS
            if (databaseSaveThread != null)
            {
                if (databaseSaveThread.IsAlive)
                {
                    databaseSaveThread.Join(250);
                }
                if (!databaseSaveThread.IsAlive)
                {
                    databaseSaveThread = null;
                }
            }
#endif

            // Disconnect server/client and shutdown transports (safe to call multiple times)
            if (_gonetServer != null)
            {
                _gonetServer.ClientConnected -= Server_OnClientConnected_SendClientCurrentState;
                _gonetServer.ClientDisconnected -= Server_OnClientDisconnected_Cleanup;

                try { _gonetServer.Stop(); }
                catch (Exception ex) { GONetLog.Warning($"[GONet] ResetForNewSession server stop failed: {ex.Message}"); }

                try
                {
                    _gonetServer.Transport?.Shutdown();
                    _gonetServer.Transport?.Dispose();
                }
                catch (Exception ex)
                {
                    GONetLog.Warning($"[GONet] ResetForNewSession server transport shutdown failed: {ex.Message}");
                }
            }

            if (_gonetClient != null)
            {
                _gonetClient.InitializedWithServer -= Client_gonetClient_InitializedWithServer;
                _gonetClient.ClientDisconnected -= Client_gonetClient_Disconnected;

                try { _gonetClient.Disconnect(); }
                catch (Exception ex) { GONetLog.Warning($"[GONet] ResetForNewSession client disconnect failed: {ex.Message}"); }

                try
                {
                    _gonetClient.Transport?.Shutdown();
                    _gonetClient.Transport?.Dispose();
                }
                catch (Exception ex)
                {
                    GONetLog.Warning($"[GONet] ResetForNewSession client transport shutdown failed: {ex.Message}");
                }
            }

            // Cancel pending async RPC TaskCompletionSources to unblock awaiting threads on shutdown.
            // Without this, application quit hangs if any async RPCs are still awaiting responses.
            GONetEventBus.ResetDeferredRpcStateForNewSession();

            // Shutdown SoA systems to prevent stale registries across sessions
            SoA_BlendingPipeline.Shutdown();
            SoA_ValueApplicator.Shutdown();
            SoA_StreamRegistry.Shutdown();
            SoA_BlendingDiagnostics.Shutdown();
            SoA_ObjectHealthMonitor.Shutdown();
            SoA_LifecycleTracker.Shutdown();
        }

        /// <summary>
        /// Resets all GONet static state for starting a new session.
        /// Call this when:
        /// - Fast Iteration Mode (domain reload disabled) in editor
        /// - Runtime lobby flow: switching between server/client roles without closing the game
        /// - Disconnecting from one session and joining another
        ///
        /// This method is safe to call at runtime and will properly reset all state
        /// needed to start fresh with a new network session.
        /// </summary>
        public static void ResetForNewSession()
        {
            GONetLog.Debug("[GONet] ResetForNewSession() - Clearing all static state for new session.");

            ShutdownForNewSession();
            ResetTimeSyncStateForNewSession();

            // ==== 1. Clear EventBus subscriptions first (prevents duplicate handlers) ====
            GONetEventBus.Instance.ClearAllSubscriptions();
            // NOTE: ResetDeferredRpcStateForNewSession() is called inside ShutdownForNewSession() above
            ResetAnimatorTriggerStateForNewSession();
            ResetReparentingStateForNewSession();

            // ==== 2. Reset session identity ====
            sessionGUID = SessionGUID_Unset;
            HostEpoch = 0;
            CurrentHostIdentity = default;
            IsApplicationQuitting = false;

            // ==== 3. Reset GONet ID assignment counters ====
            lastAssignedGONetIdRaw = GONetParticipant.GONetIdRaw_Unset;
            client_lastServerGONetIdRawForRemoteControl = GONetParticipant.GONetIdRaw_Unset;
            lastAssignedChunkId = 0;

            // ==== 4. Reset server/client state ====
            MyAuthorityId = OwnerAuthorityId_Unset;
            isServerOverride = isServer_asIndicatedByCommandLineArgs;
            server_lastAssignedAuthorityId = OwnerAuthorityId_Unset;
            _gonetServer = null;
            _gonetClient = null;

            // ==== 5. Reset frame tracking ====
            lastCalledFrame_Update_DoTheHeavyLifting = -1;

            // ==== 6. Clear GONetParticipant tracking collections ====
            gonetParticipantByGONetIdMap?.Clear();
            gonetParticipantByGONetIdAtInstantiationMap?.Clear();
            recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map?.Clear();
            missingGONetParticipantWarningSuppressionMap?.Clear();
            missingGONetParticipantRecoveryLastAttempt?.Clear();
            remoteSpawns_avoidAutoPropagateSupport?.Clear();
            soaReRegisterQueue?.Clear();
            soaReRegisterSet?.Clear();

            // ==== 7. Clear persistent events ====
            persistentEventsThisSession?.Clear();
            persistentEventsArchive_CompleteHistory?.Clear();
            gonetIdsDestroyedViaPropagation?.Clear();
            persistentEventsCancelledOut?.Clear();
            persistentEventNodesCancelledOut?.Clear();
            persistentEventDiag_eventTypeCountsSinceLastLog?.Clear();
            publishEventsDiag_eventTypeCountsSinceLastLog?.Clear();

            // ==== 8. Clear event queues ====
            incomingNetworkData_waitingForGONetReady?.Clear();
            foreach (var kvp in events_AwaitingSendToOthersQueue_ByThreadMap)
            {
                kvp.Value?.Clear();
            }
            events_AwaitingSendToOthersQueue_ByThreadMap?.Clear();
            foreach (var kvp in events_SendToOthersQueue_ByThreadMap)
            {
                // RingBuffer doesn't have Clear(), drain it instead
                if (kvp.Value != null)
                {
                    while (kvp.Value.TryRead(out _)) { }
                }
            }
            events_SendToOthersQueue_ByThreadMap?.Clear();

            // ==== 9. Clear GONetBehaviour tracking ====
            tickReceivers?.Clear();
            tickReceivers_awaitingAdd?.Clear();
            tickReceivers_awaitingRemove?.Clear();
            allGONetBehaviours?.Clear();
            deserializeInitPublishedGONetIds?.Clear();

            // ==== 10. Clear tombstones ====
            despawnTombstoneByGONetId?.Clear();
            despawnTombstoneRemovalBuffer?.Clear();

            // ==== 11. Clear Network thread queues ====
            ClearNetworkQueues();

            // ==== 12. Clear AutoSync state ====
            ClearAutoSyncState();

            // ==== 13. Clear SoA lookup dictionaries ====
            soaPositionLookup?.Clear();
            soaRotationLookup?.Clear();
            soaScalarLookup?.Clear();
            soaVector2Lookup?.Clear();
            soaVector4Lookup?.Clear();

            // ==== 14. Clear sync value change queue ====
            syncValueChanges_ReceivedFromOtherQueue?.Clear();

            // ==== 15. Clear support class state ====
            GONetLocal.ClearStaticState();
            GONetIdBatchManager.ResetForNewSession();
            GONetSpawnSupport_Runtime.ClearAllCachesForSessionReset();
            GONetGlobal.ClearSessionState();
            GONetPoolManager.ResetForNewSession();

            // ==== 16. Reset instance references ====
            GlobalSessionContext = null;
            myLocal = null;

            if (Application.isPlaying && Global != null)
            {
                InitEventSubscriptions();
            }

            GONetLog.Debug("[GONet] ResetForNewSession() complete - ready for new session.");
        }

        /// <summary>
        /// Clears network thread queues. Part of ResetForNewSession().
        /// </summary>
        private static void ClearNetworkQueues()
        {
            // Clear receive queues
            foreach (var kvp in singleProducerReceiveQueuesByThread)
            {
                // Clear pending work items
                while (kvp.Value?.queueForWork?.TryDequeue(out _) == true) { }
                while (kvp.Value?.queueForPostWorkResourceReturn?.TryDequeue(out _) == true) { }
            }
            singleProducerReceiveQueuesByThread?.Clear();

            // Clear send queues
            foreach (var kvp in singleProducerSendQueuesByThread)
            {
                while (kvp.Value?.queueForWork?.TryDequeue(out _) == true) { }
                while (kvp.Value?.queueForPostWorkResourceReturn?.TryDequeue(out _) == true) { }
            }
            singleProducerSendQueuesByThread?.Clear();
        }

        /// <summary>
        /// Clears AutoSync static state. Part of ResetForNewSession().
        /// </summary>
        private static void ClearAutoSyncState()
        {
            autoSyncProcessingSupportByFrequencyMap?.Clear();
            autoSyncProcessingSupports_UnityMainThread?.Clear();
            autoSyncProcessThread_valueChangeSerializationArrayPool_ThreadMap?.Clear();
            activeAutoSyncCompanionsByCodeGenerationIdMap?.Clear();
            activeAutoSyncCompanionsByCodeGenerationIdMap_uintKeyForPerformance?.Clear();
            uniqueSyncGroupings?.Clear();
            autoSyncUniqueGroupingToLastElapsedTicks?.Clear();
            pendingAutoSyncCompanionRecovery?.Clear();
            pendingAutoSyncCompanionRecoveryScratch?.Clear();
            definedInSceneParticipantInstanceIDs?.Clear();
            participantInstanceID_to_SpawnSceneName?.Clear();
            fullStateSyncInProgress?.Clear();
            lastFullStateSyncRequestRawTicks?.Clear();
            deferredSpawnEvents?.Clear();
            deferredDespawnEvents?.Clear();
            serverReceivedGONetLocalSpawnAuthorities?.Clear();
            deferredAllValuesBundles?.Clear();
            gnpsAwaitingCompanion?.Clear();

            lastFullStateSyncRetryRawTicks = 0;
            isGONetLocalSpawnRetryActive = false;
            gonetLocalSpawnRetryGONetId = 0;
            expectedAllValuesBundlesForScene = -1;
            receivedAllValuesBundlesForLateJoinerInit = 0;
            lateJoinerInitSceneName = string.Empty;
            timeOfLastAllValuesBundle = 0f;
        }

        #region Distributed Host Authority - Host Epoch System

        /// <summary>
        /// Monotonically increasing epoch counter that increments on each host migration.
        /// Used for split-brain prevention: messages with stale epochs are rejected.
        /// Epoch 0 = initial host (first server), increments on each migration.
        /// </summary>
        public static uint HostEpoch { get; private set; } = 0;

        /// <summary>
        /// Current host identity combining SessionGUID, epoch, and authority IDs.
        /// When distributed host is disabled, this returns default values with current server as host.
        /// </summary>
        public static HostIdentity CurrentHostIdentity { get; private set; }

        /// <summary>
        /// Increments the host epoch during host migration.
        /// Called internally by the host migration system.
        /// </summary>
        internal static void IncrementHostEpoch(ushort newHostAuthorityId, ushort newViceHostAuthorityId)
        {
            HostEpoch++;
            CurrentHostIdentity = new HostIdentity(SessionGUID, HostEpoch, newHostAuthorityId, newViceHostAuthorityId);
            GONetLog.Info($"[DistributedHost] Host epoch incremented to {HostEpoch}. New host: {newHostAuthorityId}, Vice host: {newViceHostAuthorityId}");
        }

        /// <summary>
        /// Adopts a host identity received from the network (e.g., host heartbeat or promotion).
        /// Unlike <see cref="IncrementHostEpoch"/>, this can advance by more than 1 in partition-heal scenarios.
        /// </summary>
        internal static void AdoptHostIdentity(uint newHostEpoch, ushort newHostAuthorityId, ushort newViceHostAuthorityId)
        {
            if (newHostEpoch < HostEpoch)
            {
                GONetLog.Debug($"[DistributedHost] Ignoring stale host identity adoption (epoch {newHostEpoch} < {HostEpoch})");
                return;
            }

            uint previousEpoch = HostEpoch;
            HostEpoch = newHostEpoch;
            CurrentHostIdentity = new HostIdentity(SessionGUID, HostEpoch, newHostAuthorityId, newViceHostAuthorityId);
            GONetLog.Info($"[DistributedHost] Host identity adopted: epoch={HostEpoch}. Host={newHostAuthorityId}, ViceHost={newViceHostAuthorityId}");

            // Map server authority (1023) to the current host's GONetLocal on clients.
            TryMapServerAuthorityToHostLocal(newHostAuthorityId);

            // Request reconciliation if this is a client that just learned about a failover
            // This handles the case where client connects and THEN learns the epoch via heartbeat
            if (!IsServer &&
                _gonetClient != null &&
                _gonetClient.IsInitializedWithServer &&
                newHostEpoch > 1 &&
                previousEpoch < newHostEpoch &&
                GONetGlobal.Instance != null &&
                GONetGlobal.Instance.enableDistributedHostAuthority)
            {
                GONetLog.Info($"[Reconciliation] Client requesting reconciliation after epoch change (prevEpoch={previousEpoch}, newEpoch={newHostEpoch})");
                var request = new ReconciliationRequestEvent(clientEpoch: newHostEpoch);
                request.OccurredAtElapsedTicks = Time.ElapsedTicks;
                EventBus.Publish(request);
            }
        }

        /// <summary>
        /// Client-side helper to map server authority (1023) to the current host's GONetLocal.
        /// Needed after host migrations where the host's original authority ID differs from 1023.
        /// </summary>
        internal static void TryMapServerAuthorityToHostLocal(ushort hostAuthorityId)
        {
            if (IsServer ||
                hostAuthorityId == OwnerAuthorityId_Unset ||
                hostAuthorityId == OwnerAuthorityId_Server ||
                GONetGlobal.Instance == null ||
                !GONetGlobal.Instance.enableDistributedHostAuthority ||
                GONetLocal.LookupByAuthorityId == null)
            {
                // Expected behavior when distributed host authority isn't enabled - use Debug to avoid spam
                GONetLog.Debug($"[Handoff] TryMapServerAuthorityToHostLocal({hostAuthorityId}) skipped: IsServer={IsServer}, hostAuth={hostAuthorityId}, global={(GONetGlobal.Instance != null)}, distributed={(GONetGlobal.Instance?.enableDistributedHostAuthority ?? false)}, lookup={(GONetLocal.LookupByAuthorityId != null)}");
                return;
            }

            // Prefer local mapping on pure clients to avoid referencing a remote GONetLocal that can be destroyed by reconciliation.
            if (IsClient && !IsServer && MyAuthorityId != OwnerAuthorityId_Unset)
            {
                GONetLocal myLocal = GONetLocal.LookupByAuthorityId[MyAuthorityId];
                if (myLocal != null)
                {
                    GONetLocal.AddServerAuthorityMapping(myLocal, OwnerAuthorityId_Server);
                    GONetLog.Info($"[Handoff] TryMapServerAuthorityToHostLocal({hostAuthorityId}) CLIENT-LOCAL: using MyAuthorityId={MyAuthorityId}'s GONetLocal for 1023 mapping");
                    return;
                }
            }

            GONetLocal hostLocal = GONetLocal.LookupByAuthorityId[hostAuthorityId];
            if (hostLocal == null)
            {
                // FALLBACK: Pure clients don't have GONetLocal entries for other clients.
                // Use OUR OWN GONetLocal for the server authority mapping.
                // For IsGONetReady(), we just need ANY valid GONetLocal to satisfy the lookup check.
                GONetLocal myLocal = GONetLocal.LookupByAuthorityId[MyAuthorityId];
                if (myLocal != null)
                {
                    GONetLocal.AddServerAuthorityMapping(myLocal, OwnerAuthorityId_Server);
                    GONetLog.Info($"[Handoff] TryMapServerAuthorityToHostLocal({hostAuthorityId}) FALLBACK: No GONetLocal for host {hostAuthorityId}, using MyAuthorityId={MyAuthorityId}'s GONetLocal for 1023 mapping");
                    return;
                }

                // If even our own GONetLocal isn't available yet, log diagnostic and return
                var availableAuthorities = GONetLocal.LookupByAuthorityId?.GetAllKeys() ?? System.Array.Empty<ushort>();
                GONetLog.Warning($"[Handoff] TryMapServerAuthorityToHostLocal({hostAuthorityId}) FAILED: No GONetLocal for host {hostAuthorityId} or self {MyAuthorityId}. Available: [{string.Join(", ", availableAuthorities)}]");
                return;
            }

            GONetLocal.AddServerAuthorityMapping(hostLocal, OwnerAuthorityId_Server);
            GONetLog.Info($"[Handoff] TryMapServerAuthorityToHostLocal({hostAuthorityId}) SUCCESS: Mapped 1023 -> authority {hostAuthorityId}'s GONetLocal");
        }

        /// <summary>
        /// Updates the vice host authority ID without advancing the epoch.
        /// Use this when the host designates a new vice host.
        /// </summary>
        internal static void UpdateViceHostAuthority(ushort newViceHostAuthorityId)
        {
            if (CurrentHostIdentity.ViceHostAuthorityId == newViceHostAuthorityId)
            {
                return;
            }

            ushort previousViceHost = CurrentHostIdentity.ViceHostAuthorityId;
            CurrentHostIdentity = new HostIdentity(SessionGUID, HostEpoch, CurrentHostIdentity.HostAuthorityId, newViceHostAuthorityId);
            GONetLog.Info($"[DistributedHost] Vice host updated: {previousViceHost} -> {newViceHostAuthorityId} (epoch {HostEpoch})");
        }

        /// <summary>
        /// Initializes the host identity for the initial host (server).
        /// Called when the session starts.
        /// </summary>
        internal static void InitializeHostIdentity(ushort initialHostAuthorityId)
        {
            HostEpoch = 0;
            CurrentHostIdentity = new HostIdentity(SessionGUID, 0, initialHostAuthorityId, 0);
        }

        /// <summary>
        /// Checks if a received epoch is stale (behind our current known epoch).
        /// </summary>
        /// <param name="receivedEpoch">The epoch from a received message</param>
        /// <returns>True if the received epoch is stale and should be ignored</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static bool IsEpochStale(uint receivedEpoch) => receivedEpoch < HostEpoch;

        /// <summary>
        /// Checks if a received host identity is valid (not stale).
        /// </summary>
        /// <param name="receivedIdentity">The host identity from a received message</param>
        /// <returns>True if the identity is valid and should be processed</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static bool IsHostIdentityValid(in HostIdentity receivedIdentity)
        {
            // Same session and equal or newer epoch
            return receivedIdentity.SessionGUID == SessionGUID && receivedIdentity.HostEpoch >= HostEpoch;
        }

        #endregion

        #region Voluntary Host Migration

        /// <summary>
        /// Result of attempting to initiate a voluntary host migration.
        /// </summary>
        public enum VoluntaryMigrationResult
        {
            Success,
            NotHost,
            NoViceHost,
            CandidateNotStable,
            AlreadyMigrating,
            CandidateNotReachable,
            ViceHostStateStale,
            CooldownActive,
            NotSafeToMigrate,
            CandidateRejected,
            PrepareTimeout
        }

        /// <summary>
        /// Initiates voluntary host migration to the current vice host.
        /// Only callable by the current host.
        /// </summary>
        public static VoluntaryMigrationResult Server_InitiateVoluntaryHostMigration()
        {
            if (!IsServer)
            {
                return VoluntaryMigrationResult.NotHost;
            }

            var global = GONetGlobal.Instance;
            if (global == null || !global.enableDistributedHostAuthority)
            {
                return VoluntaryMigrationResult.NotSafeToMigrate;
            }

            if (global.isPinnedHost)
            {
                return VoluntaryMigrationResult.NotSafeToMigrate;
            }

            if (DistributedHost.GONetHostHandoffManager.Instance.IsHandoffInProgress ||
                DistributedHost.GONetHostFailoverManager.Instance.IsFailoverInProgress)
            {
                return VoluntaryMigrationResult.AlreadyMigrating;
            }

            if (!global.enableViceHost)
            {
                return VoluntaryMigrationResult.NoViceHost;
            }

            var viceHostManager = DistributedHost.GONetViceHostManager.Instance;
            ushort viceHostId = viceHostManager.ViceHostAuthorityId;
            if (viceHostId == 0)
            {
                return VoluntaryMigrationResult.NoViceHost;
            }

            if (!viceHostManager.TryGetStableBetterHostCandidate(out ushort stableCandidateId) ||
                stableCandidateId != viceHostId)
            {
                return VoluntaryMigrationResult.CandidateNotStable;
            }

            if (!DistributedHost.GONetGossipManager.Instance.TryGetNodeMetrics(viceHostId, out _))
            {
                return VoluntaryMigrationResult.CandidateNotReachable;
            }

            float now = (float)Time.ElapsedSeconds;
            float cooldownSeconds = global.hostMigrationCooldownSeconds;
            if (cooldownSeconds > 0f &&
                viceHostManager.LastVoluntaryMigrationTime > 0f &&
                now - viceHostManager.LastVoluntaryMigrationTime < cooldownSeconds)
            {
                return VoluntaryMigrationResult.CooldownActive;
            }

            if (global.betterHostCanMigrateNowCallback != null &&
                !global.betterHostCanMigrateNowCallback())
            {
                return VoluntaryMigrationResult.NotSafeToMigrate;
            }

            float maxSyncStaleSeconds = global.viceHostSyncStaleSeconds;
            if (!viceHostManager.IsViceHostStateCurrentEnough(maxSyncStaleSeconds, out float secondsSinceAck, out ulong lastAckSequence))
            {
                string reason = lastAckSequence == 0
                    ? "no sync acknowledgements yet"
                    : $"last ack {secondsSinceAck:F2}s ago (max {maxSyncStaleSeconds:F2}s)";

                GONetLog.Warning($"[ViceHost] Voluntary migration blocked - vice host state stale ({reason})");
                return VoluntaryMigrationResult.ViceHostStateStale;
            }

            var hotStandby = DistributedHost.GONetHotStandbyManager.Instance;
            if (hotStandby == null)
            {
                return VoluntaryMigrationResult.CandidateNotReachable;
            }

            if (!hotStandby.TryGetStandbyConnectionActivity(viceHostId, out var state, out float secondsSinceActivity))
            {
                return VoluntaryMigrationResult.CandidateNotReachable;
            }

            if (state != DistributedHost.StandbyConnectionState.Connected &&
                state != DistributedHost.StandbyConnectionState.Active)
            {
                return VoluntaryMigrationResult.CandidateNotReachable;
            }

            float maxStaleSeconds = DistributedHost.GONetHotStandbyManager.KEEPALIVE_INTERVAL_SECONDS * 2f;
            if (secondsSinceActivity > maxStaleSeconds)
            {
                return VoluntaryMigrationResult.ViceHostStateStale;
            }

            if (!DistributedHost.GONetHostHandoffManager.Instance.InitiateGracefulHandoff(viceHostId))
            {
                return VoluntaryMigrationResult.CandidateRejected;
            }

            viceHostManager.RecordVoluntaryMigrationInitiated(now);
            return VoluntaryMigrationResult.Success;
        }

        #endregion

        private static GONetSessionContext mySessionContext;
        public static GONetSessionContext MySessionContext
        {
            get { return mySessionContext; }
            internal set
            {
                mySessionContext = value;
                MySessionContext_Participant = (object)mySessionContext == null ? null : mySessionContext.gameObject.GetComponent<GONetParticipant>();
            }
        }

        /// <summary>
        /// <para>This is used to automatically to compress **EVERYTHING** GONet sends!</para>
        /// <para>Default is LZ4 compression.</para>
        /// <para>Set to null if you prefer not to use compression.</para>
        /// <para>WARNING: We will open up this API soon...as of now, to chan change this value during runtime, you would have to be very cautious as to the timing and ensure it is not somehow changed between calls to compress/uncompress...since we are not going to figure the timing of all that right now, we will leave setter private.</para>
        /// </summary>
        public static IByteArrayCompressionSupport AutoCompressEverything { get; private set; } = LZ4CompressionSupport.Instance;

        static long ticksAtLastInit_UtcNow;

        internal static void InitOnUnityMainThread(GONetGlobal gONetGlobal, GONetSessionContext gONetSessionContext, int valueBlendingBufferLeadTimeMilliseconds)
        {
            const string ENV = "Environment.ProcessorCount: ";
            GONetLog.Info(ENV + Environment.ProcessorCount);

            //IsUnityApplicationEditor = Application.isEditor;
            mainUnityThread = Thread.CurrentThread;
            Application.quitting += Application_quitting_TakeNote;

            // CRITICAL: Register Unity main thread in event queues IMMEDIATELY
            // This prevents KeyNotFoundException when PublishEvents_SentToOthers() is called
            // before any AutoMagicalSyncProcessingEngine is created (e.g., dedicated server with no synced objects yet)
            if (!events_AwaitingSendToOthersQueue_ByThreadMap.ContainsKey(mainUnityThread))
            {
                events_AwaitingSendToOthersQueue_ByThreadMap[mainUnityThread] = new Queue<IGONetEvent>(100);
                var ringBuffer = new RingBuffer<IGONetEvent>(); // Starts at 2048, auto-scales to 16384
                events_SendToOthersQueue_ByThreadMap[mainUnityThread] = ringBuffer;
                GONetLog.Debug($"[GONet] Registered Unity main thread (ID: {mainUnityThread.ManagedThreadId}) in event queue maps during initialization");
            }

            Global = gONetGlobal;
            GlobalSessionContext = gONetSessionContext;
            SetValueBlendingBufferLeadTimeFromMilliseconds(valueBlendingBufferLeadTimeMilliseconds);
            InitEventSubscriptions();
            InitPersistence();
            InitQuantizers();
            InitObjectPools();
            InitClientTime();
            InitSceneManager();
            InitAdaptivePoolScaler(); // Initialize adaptive scaling system
            InitSoA(); // Initialize GONet v2 Structure-of-Arrays system
            InitReparentingSupport(); // Initialize reparenting event handling
            InitAnimatorTriggerSupport(); // Initialize animator trigger sync support
#if UNITY_EDITOR
            InitUnknownGNPValidation(); // Check if we need to validate GNPs at runtime
#endif

            ticksAtLastInit_UtcNow = DateTime.UtcNow.Ticks;

            // NOTE: Distributed host gossip initialization is deferred until server/client role is determined
            // See gonetServer setter (for server) and Client_gonetClient_InitializedWithServer (for client)
        }

        /// <summary>
        /// Initializes the distributed host gossip system if enabled in GONetGlobal.
        /// Called from gonetServer setter (for server/host) and Client_gonetClient_InitializedWithServer (for clients).
        /// </summary>
        /// <param name="isHost">True if this node is the initial host (server), false for clients</param>
        /// <param name="transport">The main transport for this session (needed for Steamworks virtual ports)</param>
        private static void InitDistributedHostIfEnabled(bool isHost, Transport.IGONetTransport transport)
        {
            if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableDistributedHostAuthority)
            {
                DistributedHost.GONetGossipIntegration.Initialize(isHost, transport);
            }
        }

#if UNITY_EDITOR
        #region Runtime GNP Validation Methods (Editor Only)

        /// <summary>
        /// [EDITOR ONLY] Initializes the unknown GNP validation system.
        /// Parses the dirty warnings file to build a set of problematic GNP paths.
        /// This enables targeted validation: only GNPs matching known problematic paths are checked.
        /// </summary>
        private static void InitUnknownGNPValidation()
        {
            // Build the path to the dirty warnings file
            // Path: Assets/GONet/Code/GONet/Editor/Generation/GONetDesignTimeDirtyReasons.log
            dirtyWarningsFilePath = System.IO.Path.Combine(
                Application.dataPath, "GONet", "Code", "GONet", "Editor", "Generation", "GONetDesignTimeDirtyReasons.log");

            if (!System.IO.File.Exists(dirtyWarningsFilePath))
            {
                shouldValidateUnknownGNPs = false;
                return;
            }

            // Parse the dirty reasons file to extract problematic paths
            ParseDirtyReasonsFile();

            // Enable validation if we found any problematic paths or non-path-specific issues
            shouldValidateUnknownGNPs = (problematicGNPPaths != null && problematicGNPPaths.Count > 0) || nonPathSpecificDirtyReasonCount > 0;

            if (shouldValidateUnknownGNPs)
            {
                int pathCount = problematicGNPPaths?.Count ?? 0;
                GONetLog.Warning($"[GONet] Design-time changes detected since last build. Runtime validation ENABLED.\n" +
                    $"  Problematic GNP paths: {pathCount}\n" +
                    $"  Other issues: {nonPathSpecificDirtyReasonCount}\n" +
                    $"  GNPs matching these paths may be disabled to prevent errors.\n" +
                    $"  TO FIX: Create a full game build (File → Build and Run) to sync all GONet metadata.");
            }
        }

        /// <summary>
        /// [EDITOR ONLY] Parses the dirty reasons file to extract problematic GNP paths.
        /// Format: "YYYY-MM-DD HH:MM:SS: reason message containing path"
        /// Paths are identified by prefixes: scene://, project://, addressables://
        /// Now also performs smart validation to filter out stale/incorrect dirty reasons.
        /// </summary>
        private static void ParseDirtyReasonsFile()
        {
            problematicGNPPaths = new HashSet<string>();
            problematicGNPReasons = new Dictionary<string, string>();
            dirtyReasonInfos = new List<DirtyReasonInfo>();
            nonPathSpecificDirtyReasonCount = 0;

            try
            {
                string[] lines = System.IO.File.ReadAllLines(dirtyWarningsFilePath);

                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Extract the reason part (after the timestamp "YYYY-MM-DD HH:MM:SS: ")
                    string reason = line;
                    int colonIndex = line.IndexOf(": ", 17); // Skip past timestamp
                    if (colonIndex > 0 && colonIndex < line.Length - 2)
                    {
                        reason = line.Substring(colonIndex + 2);
                    }

                    // Try to extract path from the reason
                    string path = ExtractGNPPathFromReason(reason);

                    if (!string.IsNullOrEmpty(path))
                    {
                        problematicGNPPaths.Add(path);
                        // Store the most recent reason for this path (in case of duplicates)
                        problematicGNPReasons[path] = reason;

                        // Extract the UniqueScenePath if present (format: [UniqueScenePath=scene://...])
                        string uniqueScenePath = ExtractUniqueScenePathFromReason(reason);

                        // Create structured info for smart validation
                        var info = new DirtyReasonInfo
                        {
                            Path = path,
                            UniqueScenePath = uniqueScenePath,
                            FullReason = reason,
                            ActionType = DetermineActionType(reason),
                            IsScenePath = path.StartsWith(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX),
                            IsProjectPath = path.StartsWith(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX),
                            IsAddressablePath = path.StartsWith(GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX),
                            WasValidated = false,
                            IsStillValid = true // Assume valid until proven otherwise
                        };
                        dirtyReasonInfos.Add(info);
                    }
                    else
                    {
                        // Non-path-specific dirty reason (e.g., build settings changes)
                        nonPathSpecificDirtyReasonCount++;
                    }
                }

                // Now validate each dirty reason to check if it's still applicable
                ValidateDirtyReasons();
            }
            catch (System.Exception ex)
            {
                GONetLog.Warning($"[GONet] Failed to parse dirty reasons file: {ex.Message}. Falling back to blanket validation.");
                problematicGNPPaths = null;
                problematicGNPReasons = null;
                dirtyReasonInfos = null;
            }
        }

        /// <summary>
        /// [EDITOR ONLY] Determines the action type from a dirty reason message.
        /// </summary>
        private static DirtyActionType DetermineActionType(string reason)
        {
            string lowerReason = reason.ToLowerInvariant();

            // Order matters - check more specific patterns first
            if (lowerReason.Contains("deleted"))
                return DirtyActionType.Deleted;
            if (lowerReason.Contains("removed"))
                return DirtyActionType.Removed;
            if (lowerReason.Contains("moved out"))
                return DirtyActionType.MovedOut;
            if (lowerReason.Contains("awakened"))
                return DirtyActionType.Awakened;
            if (lowerReason.Contains("added"))
                return DirtyActionType.Added;
            if (lowerReason.Contains("enabled"))
                return DirtyActionType.Enabled;
            if (lowerReason.Contains("disabled"))
                return DirtyActionType.Disabled;
            if (lowerReason.Contains("modified") || lowerReason.Contains("changed"))
                return DirtyActionType.Modified;

            return DirtyActionType.Unknown;
        }

        /// <summary>
        /// [EDITOR ONLY] Validates dirty reasons to check if they're still applicable.
        /// For example, if a dirty reason says a prefab was deleted but the prefab still exists,
        /// that dirty reason is no longer valid (it was likely a scene instance deletion misreported).
        /// </summary>
        private static void ValidateDirtyReasons()
        {
            if (dirtyReasonInfos == null) return;

            int invalidatedCount = 0;
            foreach (var info in dirtyReasonInfos)
            {
                info.WasValidated = true;
                info.IsStillValid = IsDirtyReasonStillValid(info);

                if (!info.IsStillValid)
                {
                    invalidatedCount++;
                    GONetLog.Debug($"[GONet] Dirty reason invalidated (resource still exists): {info.FullReason}");
                }
            }

            if (invalidatedCount > 0)
            {
                GONetLog.Info($"[GONet] Smart validation: {invalidatedCount} of {dirtyReasonInfos.Count} dirty reasons were invalidated because the referenced resources still exist.");
            }
        }

        /// <summary>
        /// [EDITOR ONLY] Checks if a dirty reason is still valid by verifying the referenced resource.
        /// </summary>
        private static bool IsDirtyReasonStillValid(DirtyReasonInfo info)
        {
            // For "removed" or "deleted" actions on project:// paths, verify the prefab no longer exists
            if (info.IsProjectPath && (info.ActionType == DirtyActionType.Removed || info.ActionType == DirtyActionType.Deleted))
            {
                // Extract the asset path from the project:// path
                string assetPath = info.Path.Substring(GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX.Length);

                // Check if the prefab still exists
                if (UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(assetPath) != null)
                {
                    // Prefab still exists! This dirty reason is stale.
                    // This typically happens when a scene INSTANCE of a prefab was deleted,
                    // but GetFullPath() incorrectly returned the project:// path.
                    return false;
                }
            }

            // For addressables:// paths with removed/deleted actions, we could add similar validation
            // but addressables are more complex - leave as valid for now

            // All other dirty reasons are assumed valid
            return true;
        }

        /// <summary>
        /// [EDITOR ONLY] Extracts a GNP path from a dirty reason message.
        /// Looks for scene://, project://, or addressables:// prefixes.
        /// </summary>
        private static string ExtractGNPPathFromReason(string reason)
        {
            // Path prefixes we're looking for
            string[] prefixes = new[] {
                GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX,      // "scene://"
                GONetSpawnSupport_Runtime.PROJECT_HIERARCHY_PREFIX,   // "project://"
                GONetSpawnSupport_Runtime.ADDRESSABLES_HIERARCHY_PREFIX // "addressables://"
            };

            foreach (string prefix in prefixes)
            {
                int startIndex = reason.IndexOf(prefix);
                if (startIndex >= 0)
                {
                    // Find the end of the path (space, newline, or end of string)
                    int endIndex = reason.Length;
                    for (int i = startIndex + prefix.Length; i < reason.Length; i++)
                    {
                        char c = reason[i];
                        // Path ends at whitespace or certain punctuation that wouldn't be in a path
                        if (char.IsWhiteSpace(c) || c == ',' || c == ';' || c == ')' || c == ']')
                        {
                            endIndex = i;
                            break;
                        }
                    }

                    string path = reason.Substring(startIndex, endIndex - startIndex).Trim();
                    if (!string.IsNullOrEmpty(path))
                    {
                        return path;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// [EDITOR ONLY] Extracts the UniqueScenePath from a dirty reason message.
        /// Format: [UniqueScenePath=scene://SceneName/Path/To/Object]
        /// This provides a guaranteed-unique scene path for precise matching.
        /// </summary>
        private static string ExtractUniqueScenePathFromReason(string reason)
        {
            const string marker = "[UniqueScenePath=";
            int startIndex = reason.IndexOf(marker);
            if (startIndex < 0) return null;

            startIndex += marker.Length;
            int endIndex = reason.IndexOf(']', startIndex);
            if (endIndex < 0) return null;

            return reason.Substring(startIndex, endIndex - startIndex);
        }

        /// <summary>
        /// [EDITOR ONLY] Checks if a GNP's path matches any known problematic path.
        /// Returns the matching reason if found, null otherwise.
        ///
        /// SMART VALIDATION APPROACH:
        /// 1. Exact UniqueScenePath match is highest confidence (if available)
        /// 2. Exact scene path match is next highest confidence
        /// 3. For "removed/deleted" actions, require EXACT path match (different instances with same name are OK)
        /// 4. For "added/awakened" actions, name-based matching is appropriate
        /// 5. Skip invalidated dirty reasons (e.g., prefab reported as deleted but still exists)
        /// 6. Prefer scene:// matches over project:// matches for scene objects
        /// </summary>
        private static string GetProblematicReasonForGNP(GONetParticipant gnp)
        {
            if (dirtyReasonInfos == null || dirtyReasonInfos.Count == 0)
            {
                return null;
            }

            // Get the GNP's current path in GONet's format (guaranteed unique)
            string gnpScenePath = GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX + GONet.Utils.HierarchyUtils.GetFullUniquePath(gnp.gameObject);
            string gnpName = gnp.gameObject.name;
            string gnpBaseName = StripUnityObjectSuffixes(gnpName);

            // PASS 0: Look for exact UniqueScenePath match (HIGHEST confidence - guaranteed unique)
            // This uses the [UniqueScenePath=...] tag embedded in dirty reasons for precise matching
            foreach (var info in dirtyReasonInfos)
            {
                if (!info.IsStillValid) continue;

                // If we have a UniqueScenePath and it matches exactly, this is definitive
                if (!string.IsNullOrEmpty(info.UniqueScenePath) &&
                    info.UniqueScenePath.Equals(gnpScenePath, System.StringComparison.OrdinalIgnoreCase))
                {
                    // IMPORTANT: For "Removed" or "Deleted" actions, if we FIND a matching GNP,
                    // that means this GNP WASN'T the one removed - it's a sibling that remains.
                    // The removed object is gone. Don't disable surviving siblings.
                    if (info.ActionType == DirtyActionType.Removed || info.ActionType == DirtyActionType.Deleted)
                    {
                        // Log a warning so the user has visibility into what's happening
                        string displayPath = GetDisplayFriendlyPath(gnpScenePath.Replace(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX, ""));
                        GONetLog.Warning($"[GONet] Surviving sibling detected - NOT disabling\n" +
                            $"  GameObject: '{displayPath}'\n" +
                            $"  Dirty Reason: {info.FullReason}\n" +
                            $"  Explanation: A sibling with the same name was removed/deleted, but THIS GONetParticipant still exists in the Editor.\n" +
                            $"               Since it exists at runtime, it wasn't the one that was removed - it's a surviving sibling.\n" +
                            $"               GONet will leave this component ENABLED and assume it's valid.\n" +
                            $"  WARNING: In deployed builds, the DELETED sibling still exists, causing a mismatch with the Editor.\n" +
                            $"           The different sibling order will cause GONet IDs to be assigned differently, so:\n" +
                            $"           - The WRONG sibling may be synchronized between Editor and builds\n" +
                            $"           - Objects may appear deleted or duplicated on the wrong machines\n" +
                            $"  TO FIX: Create a new build (File → Build and Run) so that all clients and the server share the same GONet-related content.\n" +
                            $"          This ensures every networked object is recognized consistently across all machines.");

                        // This GNP exists, so it wasn't the one removed. Skip this dirty reason.
                        continue;
                    }

                    return info.FullReason;
                }
            }

            // PASS 0b: If UniqueScenePath exists but DOESN'T match, skip that dirty reason for "removed" actions
            // This prevents matching CornLiqourBottle to a dirty reason for CornLiqourBottle (1)
            // because the UniqueScenePath provides definitive identification

            // PASS 1: Look for exact scene path match (high confidence)
            foreach (var info in dirtyReasonInfos)
            {
                if (!info.IsStillValid) continue;

                // Skip if this dirty reason has a UniqueScenePath that doesn't match
                // (we already checked exact matches in PASS 0)
                if (!string.IsNullOrEmpty(info.UniqueScenePath))
                {
                    continue; // Already handled in PASS 0
                }

                if (info.IsScenePath && info.Path.Equals(gnpScenePath, System.StringComparison.OrdinalIgnoreCase))
                {
                    return info.FullReason;
                }
            }

            // PASS 2: Look for scene:// matches with "added" or "awakened" actions using name-based matching
            // These indicate NEW GNPs that weren't in the last build - name matching is appropriate
            // Only for dirty reasons WITHOUT UniqueScenePath (older format)
            foreach (var info in dirtyReasonInfos)
            {
                if (!info.IsStillValid) continue;
                if (!info.IsScenePath) continue;

                // Skip if this has a UniqueScenePath - we already handled it precisely
                if (!string.IsNullOrEmpty(info.UniqueScenePath)) continue;

                // Only use name-based matching for "added" or "awakened" actions
                // "removed" actions should NOT match other instances with the same name
                if (info.ActionType != DirtyActionType.Added &&
                    info.ActionType != DirtyActionType.Awakened &&
                    info.ActionType != DirtyActionType.Modified &&
                    info.ActionType != DirtyActionType.Enabled)
                {
                    continue;
                }

                // Check if the dirty path contains GONet's unique sibling identifier (_+3...N06)
                // If so, this IS a unique identifier and we should do exact path matching, not name-based
                bool dirtyPathHasGONetSiblingId = info.Path.Contains("_+3") && info.Path.Contains("N06");
                if (dirtyPathHasGONetSiblingId)
                {
                    // Exact path match required - this dirty reason has a unique sibling identifier
                    if (info.Path.Equals(gnpScenePath, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return info.FullReason;
                    }
                    // Doesn't match exactly - skip this dirty reason for this GNP
                    continue;
                }

                string pathObjectName = ExtractObjectNameFromPath(info.Path);
                string pathBaseName = StripUnityObjectSuffixes(pathObjectName);

                // Check if the dirty path has a Unity-style suffix like (1), (2), etc.
                bool dirtyPathHasUnitySuffix = pathObjectName != pathBaseName;

                // If the dirty path has a Unity suffix like (1), require EXACT name match.
                // The user may have renamed the object, and we don't want stale dirty entries
                // matching objects that no longer have that name.
                if (dirtyPathHasUnitySuffix)
                {
                    // Only match if the GNP's actual name matches exactly (including suffix)
                    if (pathObjectName.Equals(gnpName, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return info.FullReason;
                    }
                    // Dirty path has (1) but GNP doesn't have that exact name - skip
                    continue;
                }

                // No Unity suffix in dirty path - use base name matching
                if (!string.IsNullOrEmpty(pathBaseName) && pathBaseName.Equals(gnpBaseName, System.StringComparison.OrdinalIgnoreCase))
                {
                    bool gnpHasSuffix = gnpName != gnpBaseName;

                    // If neither has a suffix, or the exact names match, it's the same object
                    if (!gnpHasSuffix || pathObjectName.Equals(gnpName, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return info.FullReason;
                    }
                    // GNP has a suffix but dirty path doesn't - different instances, skip
                }
            }

            // PASS 3: Look for project:// prefab matches for "added" actions only
            // A new prefab instance in scene should match if the prefab was added after build
            foreach (var info in dirtyReasonInfos)
            {
                if (!info.IsStillValid) continue;
                if (!info.IsProjectPath) continue;

                // Only match project:// paths for "added" actions, not "removed/deleted"
                // If a project:// path says "removed" but the prefab exists, it was already invalidated
                if (info.ActionType != DirtyActionType.Added &&
                    info.ActionType != DirtyActionType.Modified)
                {
                    continue;
                }

                string pathObjectName = ExtractObjectNameFromPath(info.Path);
                string pathBaseName = StripUnityObjectSuffixes(pathObjectName);

                if (!string.IsNullOrEmpty(pathBaseName) && pathBaseName.Equals(gnpBaseName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return info.FullReason;
                }
            }

            // No match found - this GNP is not problematic
            return null;
        }

        /// <summary>
        /// [EDITOR ONLY] Strips Unity's automatic suffixes from object names: (Clone), (1), (2), etc.
        /// </summary>
        private static string StripUnityObjectSuffixes(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return objectName;

            string result = objectName;

            // Strip (Clone) suffix
            if (result.EndsWith("(Clone)"))
            {
                result = result.Substring(0, result.Length - 7).Trim();
            }

            // Strip numeric suffixes like (1), (2), (10), etc.
            // Pattern: ends with space followed by (digits)
            int lastParen = result.LastIndexOf('(');
            if (lastParen > 0 && result.EndsWith(")"))
            {
                string potentialNumber = result.Substring(lastParen + 1, result.Length - lastParen - 2);
                if (int.TryParse(potentialNumber, out _))
                {
                    // It's a numeric suffix like (1), strip it
                    result = result.Substring(0, lastParen).Trim();
                }
            }

            return result;
        }

        /// <summary>
        /// [EDITOR ONLY] Extracts the object name from a GONet path.
        /// e.g., "scene://MainScene/Player/Weapon" → "Weapon"
        /// e.g., "project://Assets/Prefabs/Enemy.prefab" → "Enemy"
        /// </summary>
        private static string ExtractObjectNameFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            // Find the last path separator
            int lastSlash = path.LastIndexOf('/');
            if (lastSlash < 0) return path;

            string lastSegment = path.Substring(lastSlash + 1);

            // If it's a .prefab file, strip the extension
            if (lastSegment.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                lastSegment = lastSegment.Substring(0, lastSegment.Length - 7);
            }

            return lastSegment;
        }

        /// <summary>
        /// [EDITOR ONLY] Converts a GONet unique hierarchy path to a display-friendly path.
        /// Strips GONet's internal unique identifiers (sibling indices like _+31N06 and GONetId markers like ~3y123|]).
        /// This is for user-facing error messages where the technical identifiers would be confusing.
        /// e.g., "GameWorld_01/CornLiqourBottle_+31N06" → "GameWorld_01/CornLiqourBottle"
        /// </summary>
        private static string GetDisplayFriendlyPath(string uniquePath)
        {
            if (string.IsNullOrEmpty(uniquePath)) return uniquePath;

            // Use the same constants as HierarchyUtils
            const string SAME_NAME_SIBLING_PREFIX = "_+3";
            const string SAME_NAME_SIBLING_SUFFIX = "N06";
            const string GONETID_PREFIX = "~3y";
            const string GONETID_SUFFIX = "3|]";

            string result = uniquePath;

            // Strip all sibling index markers: _+3<digits>N06
            while (true)
            {
                int prefixIndex = result.IndexOf(SAME_NAME_SIBLING_PREFIX);
                if (prefixIndex == -1) break;

                int suffixIndex = result.IndexOf(SAME_NAME_SIBLING_SUFFIX, prefixIndex);
                if (suffixIndex == -1) break;

                // Verify there are digits between prefix and suffix
                int suffixLen = SAME_NAME_SIBLING_SUFFIX.Length;
                result = result.Remove(prefixIndex, (suffixIndex + suffixLen) - prefixIndex);
            }

            // Strip all GONetId markers: ~3y<digits>3|]
            while (true)
            {
                int prefixIndex = result.IndexOf(GONETID_PREFIX);
                if (prefixIndex == -1) break;

                int suffixIndex = result.IndexOf(GONETID_SUFFIX, prefixIndex);
                if (suffixIndex == -1) break;

                int suffixLen = GONETID_SUFFIX.Length;
                result = result.Remove(prefixIndex, (suffixIndex + suffixLen) - prefixIndex);
            }

            return result;
        }

        /// <summary>
        /// [EDITOR ONLY] Validates a GONetParticipant against known problematic paths from the dirty reasons file.
        /// Called from GONetParticipant.AwakeCoroutine() for early detection of problematic GNPs.
        /// This provides earlier error reporting than the OnEnable validation.
        /// Note: For scene-defined GNPs, this may not run before StartMonitoringForAutoMagicalNetworking
        /// because they are bulk-processed via RecordParticipantsAsDefinedInScene.
        /// </summary>
        /// <param name="gnp">The GONetParticipant to validate</param>
        /// <returns>True if the GNP is valid and should continue, false if it's problematic and was handled</returns>
        internal static bool ValidateGNPAgainstDirtyReasons_Awake(GONetParticipant gnp)
        {
            if (!shouldValidateUnknownGNPs || gnp == null)
            {
                return true; // No validation needed or GNP is null
            }

            string gnpHierarchyPath = GONet.Utils.HierarchyUtils.GetFullUniquePath(gnp.gameObject);

            // Check if this GNP matches a known problematic path
            string pathMatchReason = GetProblematicReasonForGNP(gnp);
            if (pathMatchReason != null)
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

                GONetLog.Error($"[GONet] PROBLEMATIC GONetParticipant DETECTED in Awake\n" +
                    $"  GameObject: '{displayPath}'\n" +
                    $"  CodeGenerationId: {gnp.CodeGenerationId}\n" +
                    $"  Why: {pathMatchReason}\n" +
                    $"  (Note: Paths in 'Why' may contain special characters like _+3...N06 that GONet uses internally to identify siblings with the same name.)\n" +
                    $"  Action: {actionMessage}\n" +
                    $"  TO FIX: Create a new build (File → Build and Run) so that all clients and the server share the same GONet-related content.\n" +
                    $"          This ensures every networked object is recognized consistently across all machines.\n" +
                    $"  CONFIG: To change this behavior, adjust 'Problematic GNP Handling' on GONetGlobal in your scene.\n" +
                    $"  NOTE: If this detection seems incorrect (false positive), set 'Problematic GNP Handling' to 'LogOnly' on GONetGlobal.");

                if (shouldDisable)
                {
                    // Disable the GONetParticipant
                    gnp.enabled = false;

                    // Also disable all GONetParticipantCompanionBehaviour components on the same GameObject
                    var companionBehaviours = gnp.GetComponents<GONetParticipantCompanionBehaviour>();
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

                    return false;
                }

                // If LogOnly mode, return true to continue processing (may cause errors, but user chose this)
                return true;
            }

            return true;
        }

        #endregion
#endif

        private static void InitSceneManager()
        {
            SceneManager = new GONetSceneManager(Global);

            // Subscribe to scene load completion to process deferred spawns
            SceneManager.OnSceneLoadCompleted += OnSceneLoadCompleted_ProcessDeferredSpawns;

            // Subscribe to scene load completion to recalculate sync event drain rate
            SceneManager.OnSceneLoadCompleted += OnSceneLoadCompleted_RecalculateDrainRate;

            // Pool scene lifecycle handling
            SceneManager.OnSceneLoadCompleted += GONetPoolManager.OnSceneLoadCompleted;
            SceneManager.OnSceneUnloadStarted += GONetPoolManager.OnSceneUnloadStarted;

            GONetLog.Debug("[GONetMain] Scene manager initialized");
        }

        private static void InitAdaptivePoolScaler()
        {
            adaptivePoolScaler = new GONetAdaptivePoolScaler(Global);
            GONetLog.Debug("[GONetMain] Adaptive pool scaler initialized");
        }

        private static void InitSoA()
        {
            // Create SoA using design-time generated descriptor (Hz-agnostic)
            SoAData = GONet.Generation.GONet_SoA_Descriptor.CreateSoA();

            // Initialize O(1) lookup dictionaries for network deserialization (eliminates O(n) linear search)
            soaPositionLookup = new Dictionary<uint, (int streamIndex, int objectIndex)>(256);
            soaRotationLookup = new Dictionary<uint, (int streamIndex, int objectIndex)>(256);
            soaScalarLookup = new Dictionary<uint, (int streamIndex, int objectIndex)>(256);
            soaVector2Lookup = new Dictionary<(uint gonetId, byte memberIndex), (int streamIndex, int objectIndex)>(128);
            soaVector4Lookup = new Dictionary<(uint gonetId, byte memberIndex), (int streamIndex, int objectIndex)>(128);

            GONetLog.Info("[GONetMain] GONet v2 SoA initialized (Hz-agnostic):");

            // Log all position streams
            if (SoAData.positionStreams != null)
            {
                for (int i = 0; i < SoAData.positionStreamInfos.Length; i++)
                {
                    var info = SoAData.positionStreamInfos[i];
                    int hz = Mathf.RoundToInt(1f / info.updateInterval);
                    GONetLog.Info($"  - VECTOR3 @ {hz}Hz: capacity {info.capacity}");
                }
            }

            // Log all rotation streams
            if (SoAData.rotationStreams != null)
            {
                for (int i = 0; i < SoAData.rotationStreamInfos.Length; i++)
                {
                    var info = SoAData.rotationStreamInfos[i];
                    int hz = Mathf.RoundToInt(1f / info.updateInterval);
                    GONetLog.Info($"  - QUATERNION @ {hz}Hz: capacity {info.capacity}");
                }
            }

            // Log all scalar streams
            if (SoAData.scalarStreams != null)
            {
                for (int i = 0; i < SoAData.scalarStreamInfos.Length; i++)
                {
                    var info = SoAData.scalarStreamInfos[i];
                    int hz = Mathf.RoundToInt(1f / info.updateInterval);
                    GONetLog.Info($"  - SCALAR @ {hz}Hz: capacity {info.capacity}");
                }
            }

            // Log shadow buffer capacities
            GONetLog.Info($"  - Shadow buffers: ({SoAData.shadowPositionsA.Length} positions, {SoAData.shadowRotationsA.Length} rotations)");
            // Initialize unified SoA blending components (Phase 1: feature-flagged)
            SoA_StreamRegistry.Initialize(ref SoAData);
            SoA_BlendingPipeline.Initialize(ref SoAData);
            SoA_ValueApplicator.Initialize(ref SoAData);
            SoA_BlendingDiagnostics.Initialize(); // LOG_BLEND_DIAG conditional
            SoA_ObjectHealthMonitor.Initialize(); // Active health monitoring for stuck object detection
            SoA_LifecycleTracker.Initialize(); // Complete spawn→ready→SoA lifecycle tracking

            GONetLog.Info($"  - Unified SoA blending: {(GONetFeatureFlags.UseUnifiedSoABlending ? "ENABLED" : "disabled (legacy path)")}");
        }

        private static void OnSceneLoadCompleted_ProcessDeferredSpawns(string sceneName, LoadSceneMode mode)
        {
            // CRITICAL FIX (October 2025): NEVER reset batch tracking on scene change
            //
            // WHY: Server and clients must have symmetric behavior - both persist batches across scenes.
            // If server resets but clients don't, late-joining clients can receive overlapping batches
            // because server "forgets" about batches allocated before the scene change.
            //
            // EXAMPLE BUG SCENARIO:
            // 1. Server allocates batch [604-803] to Client 2 before scene change
            // 2. Scene changes, server resets batch tracking (forgets [604-803])
            // 3. Client 2 keeps batch [604-803] (by design)
            // 4. Client 3 joins late, server allocates [704-903] (overlaps with Client 2's [604-803]!)
            // 5. Both clients allocate raw ID 704 → same GONetId → zombie objects
            //
            // MEMORY/ID SPACE ANALYSIS:
            // - Memory cost: ~8 bytes per batch (1 uint), trivial even for 1000+ clients
            // - GONetId space: 4,194,304 IDs available (22 bits), 200 IDs per batch = 20,971 max batches
            // - Realistic usage: 100 clients × 1000 spawns = 100K IDs used (2.5% of space)
            // - Batches released on client disconnect (natural recycling in long-running servers)
            //
            // DECISION: Server batch tracking persists across scenes, matching client behavior.
            // Only reset on full disconnect/shutdown.
            if (mode == LoadSceneMode.Single)
            {
                if (IsServer)
                {
                    // REMOVED: Server batch reset on scene change (caused overlapping batch bug)
                    // GONetIdBatchManager.Server_ResetAllBatches();
                    GONetLog.Info($"[GONetIdBatch] SERVER kept batches on scene change (LoadSceneMode.Single): {sceneName}");
                }
                if (IsClient)
                {
                    // DO NOT reset client batches - they persist across scenes
                    // GONetIdBatchManager.Client_ResetAllBatches();
                    client_lastServerGONetIdRawForRemoteControl = GONetParticipant.GONetIdRaw_Unset;
                    GONetLog.Info($"[GONetIdBatch] CLIENT kept batches on scene change (LoadSceneMode.Single): {sceneName}");
                }
            }

            // When a scene loads, process any deferred spawns that were waiting for it
            ProcessDeferredSpawnsForScene(sceneName);
        }

        /// <summary>
        /// Recalculates the sync event drain rate based on the newly loaded scene's expected sync values.
        /// Uses DesignTimeMetadata and ValuesCountByCodeGenerationId to estimate required drain rate.
        /// </summary>
        private static void OnSceneLoadCompleted_RecalculateDrainRate(string sceneName, LoadSceneMode mode)
        {
            int expectedSyncValues = Generation.GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory
                .GetExpectedSyncValuesForScene(sceneName);

            if (expectedSyncValues > 0)
            {
                // Use expected sync values directly - actual queue depth is lower since
                // values only generate events when they change. The adaptive +5 when busy
                // logic handles burst scenarios.
                foreach (var kvp in syncEventsToSaveQueueByEventType)
                {
                    kvp.Value.SetDrainRate(expectedSyncValues);
                }
            }
            // else: No metadata available (first scene, or metadata not yet loaded)
            // Keep existing drain rate (will be minimum or previously calculated)
        }

        private static void InitClientTime()
        {
            Global.StartCoroutine(InitClientTimeCoroutine());
        }

        private static IEnumerator InitClientTimeCoroutine()
        {
            while (!IsClientVsServerStatusKnown)
            {
                yield return null;
            }

            if (IsClient)
            {
                Time.TimeSetFromAuthority += Client_TimeSetFromAuthority;
            }
        }

        private static void Client_TimeSetFromAuthority(double fromElapsedSeconds, double toElapsedSeconds, long fromElapsedTicks, long toElapsedTicks)
        {
            // This is called by the high-perf time sync when time is adjusted
            OnSyncValueChangeProcessed_Persist_Local(
                SyncEvent_Time_ElapsedTicks_SetFromAuthority.Borrow(
                    fromElapsedTicks,
                    toElapsedTicks,
                    GONetClient.connectionToServer.RTT_Latest,
                    GONetClient.connectionToServer.RTT_RecentAverage,
                    GONetClient.connectionToServer.RTTMilliseconds_LowLevelTransportProtocol),
                false); // NOTE: false is to indicate no copy needed

            // Log significant adjustments
            double adjustmentSeconds = toElapsedSeconds - fromElapsedSeconds;
            if (Math.Abs(adjustmentSeconds) > 0.01) // More than 10ms adjustment
            {
                GONetLog.Debug($"Local time adjusted from authority (i.e., server) by {adjustmentSeconds:F3} seconds (from {fromElapsedSeconds:F3} to {toElapsedSeconds:F3}), which is more than expected, but not necessarily a bad thing.");
            }
        }

        private static void Application_quitting_TakeNote()
        {
            IsApplicationQuitting = true;

            // Shutdown health monitor with final report
            SoA_ObjectHealthMonitor.Shutdown();
            SoA_LifecycleTracker.Shutdown();

            // Export persistent event history before shutdown
            ExportPersistentEventHistory();

            // Emit a single pool summary at shutdown to confirm usage without spam
            GONetPoolManager.LogPoolSummary("ApplicationQuit");
        }

        /// <summary>
        /// Need to create an instance of each generated child class of <see cref="GONetParticipant_AutoMagicalSyncCompanion_Generated"/> in order to get access to each of its unique <see cref="QuantizerSettingsGroup"/> values
        /// to ensure a corresponding Quantizer instance is created here in the main thread to avoid using ConcurrentDictionary (i.e. runtime GC) for runtime adds and lookups.
        /// </summary>
        private static void InitQuantizers()
        {
            foreach (QuantizerSettingsGroup settings in GONetParticipant_AutoMagicalSyncCompanion_Generated_Factory.GetAllPossibleUniqueQuantizerSettingsGroups())
            {
                // not all quantizer settings that are generated will actually equate to a quantizer being used since everything gets a quantizer setting..so check before causing exception
                bool canBeUsedForQuantization = settings.quantizeToBitCount > 0;
                if (canBeUsedForQuantization)
                {
                    Quantizer.EnsureQuantizerExistsForGroup(settings);
                }
            }
        }

        /// <summary>
        /// Have to ensure all these static object pools are initialized for all the (generated) child classes of 
        /// <see cref="SyncEvent_ValueChangeProcessed"/>
        /// </summary>
        private static void InitObjectPools()
        {
            List<Type> syncEventTypes = TypeUtils.GetAllTypesInheritingFrom<SyncEvent_ValueChangeProcessed>(isConcreteClassRequired: true);
            //UnityEngine.Debug.Log($"[DREETS] Start init'ing sync event class object pools (class count: {syncEventTypes.Count})...");
            foreach (Type syncEventType in syncEventTypes)
            {
                RuntimeHelpers.RunClassConstructor(syncEventType.TypeHandle);
            }
            //UnityEngine.Debug.Log("[DREETS] ...end (init'ing sync event class object pools)!");
        }

        internal const int SYNC_EVENT_QUEUE_SAVE_WHEN_FULL_SIZE = 1000;
        const int MAX_SYNC_EVENTS_RETURN_PER_FRAME_THRESHOLD = SYNC_EVENT_QUEUE_SAVE_WHEN_FULL_SIZE * 2;
        const int STARTING_MAX_SYNC_EVENTS_RETURN_PER_FRAME = 100; // Minimum floor; dynamically adjusted per-scene via RecalculateSyncEventDrainRate
        const int MAX_SYNC_EVENTS_RETURN_PER_FRAME_INCREASEBY_WHENBUSY = 5;
        private static string persistenceFilePath;
        private static FileStream persistenceFileStream;
        const string DATE_FORMAT = "yyyy_MM_dd___HH-mm-ss-fff";
        const string TRIPU = "___";
        const string SGUID = "SGUID";
        const string MOAId = "MOAId";
        const string DB_EXT = ".mpb";
        const string DATABASE_PATH_RELATIVE = "database";
        private static void InitPersistence()
        {
            persistenceFilePath = Path.Combine(Application.persistentDataPath, DATABASE_PATH_RELATIVE, string.Concat(Math.Abs(Application.productName.GetHashCode()), TRIPU, DateTime.Now.ToString(DATE_FORMAT), TRIPU, SGUID, TRIPU, MOAId, DB_EXT));
            Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, DATABASE_PATH_RELATIVE));
            if (File.Exists(persistenceFilePath))
            {
                persistenceFilePath = persistenceFilePath.Replace(DB_EXT, string.Concat(GUID.Generate().AsInt64(), DB_EXT)); // Appending a guid to ensure the file is unique....this should only be a problem when running multiple instances on a single machine during development/testing
            }
            persistenceFileStream = new FileStream(persistenceFilePath, FileMode.Append);

            IEnumerable<Type> syncEventTypes = GONet_SyncEvent_ValueChangeProcessed_Generated_Factory.GetAllUniqueSyncEventTypes();
            foreach (Type syncEventType in syncEventTypes)
            {
                syncEventsToSaveQueueByEventType[syncEventType] = new SyncEventsSaveSupport();
            }
        }

        public static GONetParticipant MySessionContext_Participant { get; private set; } // TODO FIXME need to spawn this for everyone and set it here!
        public static ushort MyAuthorityId { get; private set; }

        /// <summary>
        /// Called during self-promotion to update our authority ID to the server authority.
        /// This ensures IsMine checks work correctly for objects we now own as the new host.
        /// Also adds the server authority ID to the GONetLocal lookup so IsGONetReady() returns true
        /// for server-owned objects.
        /// </summary>
        /// <summary>
        /// CRITICAL (December 2025): Sets the promoted dormant server as the active GONet server.
        /// Unlike the gonetServer property setter, this does NOT:
        /// - Generate a new SessionGUID (we keep the existing session)
        /// - Reset time baseline (time sync should continue)
        /// - Instantiate MyLocal (already exists from when we were a client)
        /// - Reinitialize distributed host (already initialized)
        ///
        /// This DOES:
        /// - Set the server reference so SendBytesToAllClients works
        /// - Subscribe to client connect/disconnect events
        /// </summary>
        internal static void SetPromotedServer(GONetServer promotedServer)
        {
            if (promotedServer == null)
            {
                GONetLog.Error("[Failover] SetPromotedServer called with null server");
                return;
            }

            GONetLog.Info($"[Failover] Setting promoted dormant server as active GONet server (connections: {promotedServer.numConnections})");

            // Set the server reference - this enables heartbeat sending via gonetServer.SendBytesToAllClients()
            _gonetServer = promotedServer;

            // Subscribe to client connection events for proper state management
            // These are important for:
            // 1. Sending initial state to NEW clients that connect after promotion
            // 2. Cleaning up when clients disconnect
            _gonetServer.ClientConnected += Server_OnClientConnected_SendClientCurrentState;
            _gonetServer.ClientDisconnected += Server_OnClientDisconnected_Cleanup;

            // CRITICAL FAILOVER FIX: Subscribe GONetGlobal's cleanup handler.
            // Normally this is done in GONetGlobal.OnGONetClientVsServerStatusKnown() when isServer=true,
            // but that method is NOT called again after failover promotion.
            // Without this, client-owned GNPs (like GONetLocal) won't be destroyed when clients disconnect.
            GONetGlobal.Instance?.SubscribeClientDisconnectedHandlerForPromotion();

            // CRITICAL FAILOVER FIX #1: Populate connection OwnerAuthorityIds from the hot standby authority map.
            // Dormant server connections have OwnerAuthorityId=0 because they bypass normal server assignment.
            // The authority map knows the correct mapping from ConnectionUID → AuthorityId.
            // This MUST happen BEFORE marking clients as initialized.
            DistributedHost.GONetHotStandbyManager.Instance?.PopulateDormantServerConnectionAuthorities();

            // CRITICAL FAILOVER FIX #2: Mark all existing hot standby clients as initialized.
            // During normal operation, IsInitializedWithServer is set by Server_OnNewClientInstantiatedItsGONetLocal()
            // when the server receives a client's GONetLocal spawn. But hot standby clients connected to the
            // dormant server before promotion, so this never happened. Without this fix, sync data is blocked
            // at GONet.AutoSync.cs line 2746: "if (isInitialized) // only send to client initialized with server!"
            int initializedCount = 0;
            for (int i = 0; i < promotedServer.numConnections; i++)
            {
                GONetRemoteClient remoteClient = promotedServer.remoteClients[i];
                if (remoteClient == null || remoteClient.IsInitializedWithServer)
                {
                    continue;
                }

                remoteClient.IsInitializedWithServer = true;
                initializedCount++;
                GONetLog.Info($"[Failover] Marked remote client AuthorityId={remoteClient.ConnectionToClient?.OwnerAuthorityId} as IsInitializedWithServer=true");
            }
            if (initializedCount > 0)
            {
                GONetLog.Info($"[Failover] Marked {initializedCount} hot standby clients as initialized for sync data delivery");
            }

            // CRITICAL FAILOVER FIX #3: Initialize authority ID counter.
            // Without this, new clients connecting to the promoted host would be assigned authority IDs
            // starting from 1, which could collide with existing clients still connected.
            //
            // GONetLocal tracks the "high water mark" - the highest client authority ID ever seen
            // this session. This value never decreases (even when clients disconnect), ensuring
            // that new clients always get unique IDs higher than any ever assigned.
            ushort highestKnownAuthority = GONetLocal.GetHighestKnownClientAuthorityId();

            if (highestKnownAuthority > server_lastAssignedAuthorityId)
            {
                server_lastAssignedAuthorityId = highestKnownAuthority;
                GONetLog.Info($"[Failover] Initialized server_lastAssignedAuthorityId={server_lastAssignedAuthorityId} (high water mark from GONetLocal)");
            }
            else
            {
                GONetLog.Info($"[Failover] server_lastAssignedAuthorityId remains {server_lastAssignedAuthorityId} (high water mark={highestKnownAuthority})");
            }

            GONetLog.Info($"[Failover] Promoted server activated - heartbeats will now flow to {promotedServer.numConnections} connected clients");
        }

        /// <summary>
        /// HANDOFF FIX (January 2026): Ensures server_lastAssignedAuthorityId is at least the given value.
        /// Called after handoff commit to prevent the new server from reusing authority IDs
        /// that were reserved for the demoted host.
        /// </summary>
        internal static void EnsureServerAuthorityHighWaterMark(ushort reservedAuthorityId)
        {
            if (!IsServer)
            {
                GONetLog.Warning($"[Handoff] EnsureServerAuthorityHighWaterMark called when not server - ignoring");
                return;
            }

            if (reservedAuthorityId > server_lastAssignedAuthorityId)
            {
                ushort oldValue = server_lastAssignedAuthorityId;
                server_lastAssignedAuthorityId = reservedAuthorityId;
                GONetLog.Info($"[Handoff] Updated server_lastAssignedAuthorityId: {oldValue} -> {reservedAuthorityId} (reserved for demoted host)");
            }
            else
            {
                GONetLog.Debug($"[Handoff] server_lastAssignedAuthorityId ({server_lastAssignedAuthorityId}) already >= reserved authority ({reservedAuthorityId})");
            }
        }

        internal static void PromoteToServerAuthority()
        {
            ushort oldAuthority = MyAuthorityId;
            MyAuthorityId = OwnerAuthorityId_Server;
            GONetLog.Info($"[Failover] Authority promoted from {oldAuthority} to {MyAuthorityId} (server)");
            GONetLog.Warning($"[Authority] MyAuthorityId changed: {oldAuthority} -> {MyAuthorityId} (reason: PromoteToServerAuthority)");

            // Add server authority ID to GONetLocal lookup
            // This is critical for IsGONetReady() to return true for objects with OwnerAuthorityId = 1023
            // Without this, UpdateAfterGONetReady() won't be called for server-owned objects
            if (myLocal != null)
            {
                GONetLocal.AddServerAuthorityMapping(myLocal, OwnerAuthorityId_Server);
            }
            else
            {
                GONetLog.Warning("[Failover] myLocal is null during PromoteToServerAuthority - GONetLocal lookup may not work correctly");
            }

            // CRITICAL FAILOVER FIX: Ensure new host won't reuse existing server-owned GONetIds.
            // A self-promoted host previously ran as a client and may have lastAssignedGONetIdRaw unset/low.
            // If left unchanged, server-owned spawns can collide with existing IDs from the prior host session.
            InitializeLastAssignedGONetIdRaw_FromExistingParticipants();

            RefreshRigidBodySettingsForAuthorityChange("PromoteToServerAuthority");
        }

        private static void InitializeLastAssignedGONetIdRaw_FromExistingParticipants()
        {
            uint maxObservedRaw = 0;
            foreach (var kvp in gonetParticipantByGONetIdMap)
            {
                GONetParticipant participant = kvp.Value;
                if (participant == null || participant.GONetId == GONetParticipant.GONetId_Unset || !participant.DoesGONetIdContainAllComponents())
                {
                    continue;
                }

                uint raw = participant.GONetId >> GONetParticipant.GONET_ID_BIT_COUNT_UNUSED;
                if (raw > maxObservedRaw)
                {
                    maxObservedRaw = raw;
                }
            }

            if (maxObservedRaw == 0)
            {
                return;
            }

            if (lastAssignedGONetIdRaw == GONetParticipant.GONetIdRaw_Unset || maxObservedRaw > lastAssignedGONetIdRaw)
            {
                uint previous = lastAssignedGONetIdRaw == GONetParticipant.GONetIdRaw_Unset ? 0 : lastAssignedGONetIdRaw;
                lastAssignedGONetIdRaw = maxObservedRaw;
                GONetLog.Info($"[Failover] Initialized lastAssignedGONetIdRaw={lastAssignedGONetIdRaw} (was {previous}) from existing participants");
            }
        }

        /// <summary>
        /// Reverts a self-promoted client-host back to its original client authority ID.
        /// Used to step down when split-brain is resolved in favor of a different host.
        /// </summary>
        internal static void DemoteFromServerAuthority(ushort restoredAuthorityId)
        {
            if (restoredAuthorityId == OwnerAuthorityId_Server)
            {
                GONetLog.Error("[Failover] Cannot demote to server authority ID (1023)");
                return;
            }

            ushort oldAuthority = MyAuthorityId;
            MyAuthorityId = restoredAuthorityId;
            isServerOverride = false;

            GONetLog.Warning($"[Failover] Authority demoted from {oldAuthority} to {MyAuthorityId} (client)");
            GONetLog.Warning($"[Authority] MyAuthorityId changed: {oldAuthority} -> {MyAuthorityId} (reason: DemoteFromServerAuthority)");

            // Remove the temporary server-authority mapping used to make IsGONetReady() work while promoted.
            if (myLocal != null)
            {
                GONetLocal.RemoveServerAuthorityMapping(myLocal, OwnerAuthorityId_Server);
            }

            TryMapServerAuthorityToHostLocal(CurrentHostIdentity.HostAuthorityId);

            RefreshRigidBodySettingsForAuthorityChange("DemoteFromServerAuthority");

            // CRITICAL FIX (January 2026): Clear pending GONetId sync tracking.
            // After demotion, redundant scene load notifications from the new host can populate
            // scenesAwaitingGONetIdSync, causing HasPendingGONetIdSync to return true AFTER
            // the grace period ends. This would block all sync bundles indefinitely.
            SceneManager?.ClearPendingGONetIdSyncForDemotion();

            // CRITICAL FIX (Dec 2025): Register objects in SoA after demotion.
            // Objects that were IsMine=true (we owned them as host) are now IsMine=false.
            // They need to be registered in SoA to receive sync from the new host.
            RegisterNonMineObjectsInSoAAfterDemotion();
        }

        /// <summary>
        /// Demotes the original server to a client after graceful handoff.
        /// Unlike DemoteFromServerAuthority (used for promoted clients stepping down),
        /// this handles the case where the ORIGINAL server hands off to a new host.
        /// 
        /// CRITICAL (December 2025): The original server has no "previous" client authority
        /// to restore to, so we set MyAuthorityId to Unset (0) temporarily.
        /// The new host should send a proper client authority assignment during state sync.
        /// </summary>
        internal static void DemoteOriginalServerAfterHandoff(ushort assignedAuthorityId = OwnerAuthorityId_Unset)
        {
            ushort oldAuthority = MyAuthorityId;
            isServerOverride = false;

            if (assignedAuthorityId != OwnerAuthorityId_Unset && assignedAuthorityId != OwnerAuthorityId_Server)
            {
                MyAuthorityId = assignedAuthorityId;
                GONetLog.Warning($"[Handoff] Original server demoted from authority {oldAuthority} to client authority {assignedAuthorityId}");
            }
            else
            {
                // For an original server, there's no previous authority to restore.
                // Set to Unset temporarily - new host should assign a proper authority.
                // This ensures IsServer returns false (since 0 != 1023).
                MyAuthorityId = OwnerAuthorityId_Unset;
                GONetLog.Warning($"[Handoff] Original server demoted from authority {oldAuthority} to Unset (awaiting new authority from new host)");
            }

            GONetLog.Warning($"[Authority] MyAuthorityId changed: {oldAuthority} -> {MyAuthorityId} (reason: DemoteOriginalServerAfterHandoff)");

            if (_gonetClient?.connectionToServer != null && MyAuthorityId != OwnerAuthorityId_Unset)
            {
                _gonetClient.connectionToServer.ConnectionId = $"C{MyAuthorityId}->S";
                _gonetClient.connectionToServer.ClearPreAuthorityState();
                GONetLog.Info($"[Handoff] Cleared pre-authority state after demotion (authority {MyAuthorityId})");
            }

            // Remove the server-authority mapping so IsGONetReady() works correctly.
            if (myLocal != null)
            {
                GONetLocal.RemoveServerAuthorityMapping(myLocal, OwnerAuthorityId_Server);

                if (MyAuthorityId != OwnerAuthorityId_Unset && myLocal.GONetParticipant.OwnerAuthorityId != MyAuthorityId)
                {
                    ushort previousOwner = myLocal.GONetParticipant.OwnerAuthorityId;
                    myLocal.GONetParticipant.OwnerAuthorityId = MyAuthorityId;
                    GONetLocal.UpdateAuthorityMapping(myLocal, previousOwner, MyAuthorityId);
                    GONetLog.Info($"[Handoff] Updated local GONetLocal owner {previousOwner} -> {MyAuthorityId} after demotion");
                }
            }

            // CRITICAL FIX (January 2026): Use the handoff target's ORIGINAL authority ID, not their
            // current server authority (1023). TryMapServerAuthorityToHostLocal needs to look up the
            // new host's GONetLocal by their original client authority ID (e.g., 2), not 1023.
            // Without this, server-owned objects (OwnerAuthorityId=1023) have no GONetLocal mapping,
            // so IsGONetReady() returns false and sync data never reaches the SoA.
            ushort newHostOriginalAuthorityId = DistributedHost.GONetHostFailoverManager.Instance?.VoluntaryHandoffTargetAuthorityId ?? 0;
            if (newHostOriginalAuthorityId != OwnerAuthorityId_Unset)
            {
                TryMapServerAuthorityToHostLocal(newHostOriginalAuthorityId);
            }
            else
            {
                GONetLog.Warning("[Handoff] Could not get handoff target authority ID - server authority mapping may not work");
            }

            // Clear server reference since we're no longer the server.
            // The server instance was stopped by GONetHotStandbyManager.OnDemotedFromHost().
            _gonetServer = null;

            RefreshRigidBodySettingsForAuthorityChange("DemoteOriginalServerAfterHandoff");

            // CRITICAL FIX (January 2026): Clear pending GONetId sync tracking.
            // After demotion, redundant scene load notifications from the new host can populate
            // scenesAwaitingGONetIdSync, causing HasPendingGONetIdSync to return true AFTER
            // the grace period ends. This would block all sync bundles indefinitely.
            SceneManager?.ClearPendingGONetIdSyncForDemotion();

            // CRITICAL FIX (Dec 2025): Register objects in SoA after demotion.
            // Objects that were IsMine=true (we owned them as host) are now IsMine=false.
            // They need to be registered in SoA to receive sync from the new host.
            RegisterNonMineObjectsInSoAAfterDemotion();
        }

        internal static ushort ReserveClientAuthorityIdForHandoff()
        {
            if (!IsServer)
            {
                GONetLog.Warning("[Handoff] ReserveClientAuthorityIdForHandoff called but not server");
                return OwnerAuthorityId_Unset;
            }

            ushort highestKnownAuthority = GONetLocal.GetHighestKnownClientAuthorityId();
            if (highestKnownAuthority > server_lastAssignedAuthorityId)
            {
                server_lastAssignedAuthorityId = highestKnownAuthority;
            }

            ushort nextAuthorityId = (ushort)(server_lastAssignedAuthorityId + 1);
            if (nextAuthorityId >= OwnerAuthorityId_Server)
            {
                GONetLog.Error("[Handoff] Cannot reserve client authority - no IDs remaining");
                return OwnerAuthorityId_Unset;
            }

            server_lastAssignedAuthorityId = nextAuthorityId;
            GONetLog.Info($"[Handoff] Reserved client authority {nextAuthorityId} for outgoing host");
            return nextAuthorityId;
        }

        internal static bool Server_ReassignOutgoingHostAuthority(ushort newAuthorityId)
        {
            if (!IsServer)
            {
                GONetLog.Warning("[Handoff] Cannot reassign outgoing host authority - not server");
                return false;
            }

            if (newAuthorityId == OwnerAuthorityId_Unset || newAuthorityId == OwnerAuthorityId_Server)
            {
                GONetLog.Warning($"[Handoff] Invalid outgoing host authority {newAuthorityId} - skipping reassignment");
                return false;
            }

            GONetLocal outgoingLocal = GONetLocal.LookupByAuthorityId?[OwnerAuthorityId_Server];
            if (outgoingLocal == null)
            {
                GONetLog.Warning("[Handoff] Outgoing host GONetLocal not found - cannot reassign authority");
                return false;
            }

            ushort previousOwner = outgoingLocal.GONetParticipant.OwnerAuthorityId;
            if (previousOwner == newAuthorityId)
            {
                GONetLog.Info($"[Handoff] Outgoing host authority already set to {newAuthorityId}");
                return true;
            }

            outgoingLocal.GONetParticipant.OwnerAuthorityId = newAuthorityId;
            GONetLocal.UpdateAuthorityMapping(outgoingLocal, previousOwner, newAuthorityId);

            GONetLog.Info($"[Handoff] Reassigned outgoing host GONetLocal owner {previousOwner} -> {newAuthorityId}");
            return true;
        }

        /// <summary>
        /// INTEGRATION GLUE: Establishes client-host loopback connection after failover promotion.
        ///
        /// When a client self-promotes to host during failover, they become a client-host (IsServer && IsClient).
        /// The client side still needs to think it's "connected to server" and "initialized with server"
        /// for IsGONetReady() to return true and sync data to flow.
        ///
        /// This method:
        /// 1. Creates a GONetConnection_ClientHostLoopback for efficient in-process communication
        /// 2. Registers it with the server's remote clients list
        /// 3. Sets the client's ConnectionState to Connected
        /// 4. Sets the client's IsInitializedWithServer to true
        ///
        /// After this call, the promoted host looks identical to a host that started in client-host mode.
        /// </summary>
        internal static void EstablishClientHostLoopbackForFailover()
        {
            GONetLog.Info("[Failover] Establishing client-host loopback connection...");

            if (_gonetClient == null)
            {
                GONetLog.Error("[Failover] Cannot establish loopback - GONetClient is null");
                return;
            }

            if (_gonetServer == null)
            {
                GONetLog.Error("[Failover] Cannot establish loopback - GONetServer is null");
                return;
            }

            // Check if server has transport (new path) or is using old NetcodeIO path
            if (_gonetServer.Transport == null)
            {
                // OLD PATH (NetcodeIO): Skip loopback connection creation
                // The loopback is an optimization - without it, the host still works but
                // server<->client communication goes through the network stack.
                // This is acceptable for failover where we just need things to work.
                GONetLog.Warning("[Failover] Transport is null (NetcodeIO path) - skipping loopback connection. " +
                               "Client state will be updated but loopback optimization is not available.");

                // Still update client state so IsGONetReady() returns true
                _gonetClient.SetConnectionStateForFailover(NetcodeIO.NET.ClientState.Connected);
                _gonetClient.IsInitializedWithServer = true;

                GONetLog.Info($"[Failover] Client state updated (no loopback): ConnectionState={_gonetClient.ConnectionState}, IsInitializedWithServer={_gonetClient.IsInitializedWithServer}");
                GONetLog.Warning($"[Failover] Failover complete (no loopback) - IsServer={IsServer}, IsClient={IsClient}, IsHost={IsHost}");
                return;
            }

            // Step 1: Create loopback connection for the host player
            // This bypasses the network stack for server<->client communication on the same process
            var loopbackConnection = new GONetConnection_ClientHostLoopback(
                _gonetServer.Transport,
                null, // No actual transport connection - this is in-process
                _gonetClient,
                2000); // Default max queue size

            // Step 2: Set the loopback connection's authority ID to server (1023)
            // This identifies the "client" side of the host as the server authority
            loopbackConnection.OwnerAuthorityId = OwnerAuthorityId_Server;

            // Step 3: Register loopback connection with server's remote clients
            // This allows server-side code to send to "itself" via the loopback
            var remoteClient = new GONetRemoteClient(null, loopbackConnection);
            _gonetServer.AddRemoteClientForFailover(remoteClient);

            GONetLog.Info($"[Failover] Created loopback connection with OwnerAuthorityId={loopbackConnection.OwnerAuthorityId}");

            // Step 4: Update client state to "connected" and "initialized"
            // This is what makes IsGONetReady() return true for IsClient checks
            _gonetClient.SetConnectionStateForFailover(NetcodeIO.NET.ClientState.Connected);
            _gonetClient.IsInitializedWithServer = true;

            GONetLog.Info($"[Failover] Client state updated: ConnectionState={_gonetClient.ConnectionState}, IsInitializedWithServer={_gonetClient.IsInitializedWithServer}");

            // Step 5: Log diagnostic state after loopback establishment
            GONetLog.Warning($"[Failover] Loopback established - IsServer={IsServer}, IsClient={IsClient}, IsHost={IsHost}");
        }

        /// <summary>
        /// FAILOVER: Synthesizes persistent events for a promoted host.
        /// When a client promotes to server, it doesn't have the persistent events (like SceneLoadEvent)
        /// that the original server had. This method creates synthetic events for all currently loaded
        /// scenes so late-joining clients can receive scene load instructions.
        ///
        /// Called from GONetHostFailoverManager.CompleteSelfPromotion() after the host has been promoted.
        /// </summary>
        internal static void SynthesizePersistentEventsForPromotedHost()
        {
            GONetLog.Info("[Failover] Synthesizing persistent events for promoted host...");

            // Get all currently loaded scenes
            int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            int synthesizedCount = 0;

            for (int i = 0; i < sceneCount; i++)
            {
                UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                // Skip the lobby scene - late-joiners start there and need to load the game scene
                // Skip DontDestroyOnLoad pseudo-scene
                if (scene.name == "GONetLobby" || scene.name == "DontDestroyOnLoad") continue;

                // Check if we already have a SceneLoadEvent for this scene
                bool alreadyHasEvent = false;
                foreach (var evt in persistentEventsThisSession)
                {
                    if (evt is SceneLoadEvent existingSceneLoad && existingSceneLoad.SceneName == scene.name)
                    {
                        alreadyHasEvent = true;
                        GONetLog.Debug($"[Failover] Scene '{scene.name}' already has SceneLoadEvent in persistent events");
                        break;
                    }
                }

                if (!alreadyHasEvent)
                {
                    // Create synthetic SceneLoadEvent
                    var sceneLoadEvent = new SceneLoadEvent
                    {
                        SceneName = scene.name,
                        SceneBuildIndex = scene.buildIndex,
                        LoadType = SceneLoadType.BuildSettings, // Assume normal build settings load
                        Mode = UnityEngine.SceneManagement.LoadSceneMode.Single, // Assume single mode
                        ActivateOnLoad = true,
                        Priority = 0,
                        OccurredAtElapsedTicks = Time.ElapsedTicks
                    };

                    // Add to persistent events so late-joiners receive it
                    persistentEventsThisSession.AddFirst(sceneLoadEvent); // Add first so it's processed before spawns
                    synthesizedCount++;

                    GONetLog.Info($"[Failover] Synthesized SceneLoadEvent for scene '{scene.name}' (buildIndex={scene.buildIndex})");
                }
            }

            GONetLog.Info($"[Failover] Synthesized {synthesizedCount} SceneLoadEvent(s) for promoted host.");

            // CRITICAL FIX (Dec 2025): Synthesize InstantiateGONetParticipantEvent for ALL existing runtime-spawned GONetParticipants.
            // Without this, late-joiners connecting to a promoted host receive sync data for GONetIds they don't have,
            // causing "key not found" errors and partial sync (position/rotation works from unreliable, but scale/color don't).
            int spawnEventCount = 0;
            foreach (var gnp in gonetParticipantByGONetIdMap.Values)
            {
                if (gnp == null || gnp.GONetId == GONetParticipant.GONetId_Unset) continue;

                // Skip scene-hierarchy objects - they exist in the scene and don't need spawn events
                if (gnp.WasInstantiated == false) continue;

                // Use the factory method to create the event with all proper fields
                var spawnEvent = InstantiateGONetParticipantEvent.Create(gnp);
                spawnEvent.OccurredAtElapsedTicks = Time.ElapsedTicks;

                persistentEventsThisSession.AddLast(spawnEvent);
                spawnEventCount++;

                GONetLog.Debug($"[Failover] Synthesized InstantiateGONetParticipantEvent for GONetId={gnp.GONetId}, " +
                    $"Owner={gnp.OwnerAuthorityId}, Location='{gnp.DesignTimeLocation}'");
            }

            GONetLog.Info($"[Failover] Synthesized {spawnEventCount} InstantiateGONetParticipantEvent(s) for promoted host. " +
                $"Total persistent events: {persistentEventsThisSession.Count}");

            // Pooling: synthesize pool init/borrow state so late-joiners rebuild pools correctly.
            GONetPoolManager.SynthesizePersistentEventsForPromotedHost();
        }

        /// <summary>
        /// CRITICAL FAILOVER FIX (Dec 2025): Sends synthesized persistent events to already-connected hot-standby clients.
        ///
        /// During normal operation, persistent events are sent to clients when they go through the initialization flow
        /// (Server_SendClientPersistentEventsSinceStart). However, hot-standby clients connected to the dormant server
        /// BEFORE promotion and are marked IsInitializedWithServer=true immediately during failover - they never go
        /// through the normal init flow that would deliver persistent events.
        ///
        /// Without this fix, hot-standby clients miss the synthesized spawn events and end up with fewer objects
        /// than the server (e.g., projectiles exist on server but not on the owning client).
        /// </summary>
        internal static void Server_SendPersistentEventsToExistingClients()
        {
            if (!IsServer || gonetServer == null)
            {
                GONetLog.Warning("[Failover] Cannot send persistent events - not server or server is null");
                return;
            }

            int clientCount = 0;
            int eventCount = persistentEventsThisSession.Count;

            if (eventCount == 0)
            {
                GONetLog.Info("[Failover] No persistent events to send to existing clients");
                return;
            }

            for (int i = 0; i < gonetServer.numConnections; i++)
            {
                GONetRemoteClient remoteClient = gonetServer.remoteClients[i];
                if (remoteClient == null || !remoteClient.IsInitializedWithServer)
                {
                    continue;
                }

                var connection = remoteClient.ConnectionToClient;
                if (connection == null)
                {
                    continue;
                }

                ushort clientAuthority = connection.OwnerAuthorityId;

                // Skip sending to ourselves (the client-host loopback)
                if (clientAuthority == OwnerAuthorityId_Server)
                {
                    continue;
                }

                GONetLog.Info($"[Failover] Sending {eventCount} persistent events to existing client AuthorityId={clientAuthority}");
                Server_SendClientPersistentEventsSinceStart(connection);
                clientCount++;
            }

            GONetLog.Info($"[Failover] Sent persistent events to {clientCount} existing hot-standby client(s)");
        }

        private static readonly bool isServer_asIndicatedByCommandLineArgs = Environment.GetCommandLineArgs().Contains("-server");
        private static readonly bool isClient_asIndicatedByCommandLineArgs = Environment.GetCommandLineArgs().Contains("-client");

        internal static bool isServerOverride = isServer_asIndicatedByCommandLineArgs;

        /// <summary>
        /// IMPORTANT: This can be true even when <see cref="IsClient"/> is also true.
        ///            At time of writing, the case for that would be when <see cref="clientTypeFlags"/> has <see cref="ClientTypeFlags.ServerHost"/> set.
        /// </summary>
        public static bool IsServer => isServerOverride || MyAuthorityId == OwnerAuthorityId_Server; // TODO cache this since it will not change and too much processing to get now

        /// <summary>
        /// IMPORTANT: This can return true even when <see cref="IsServer"/> is also true.
        ///            At time of writing, the case for that would be when <see cref="clientTypeFlags"/> has <see cref="ClientTypeFlags.ServerHost"/> set.
        /// </summary>
        public static bool IsClientType(ClientTypeFlags requiredFlags)
        {
            return (MyClientTypeFlags & requiredFlags) == requiredFlags;
        }

        public static ClientTypeFlags MyClientTypeFlags => _gonetClient == null ? ClientTypeFlags.None : _gonetClient.ClientTypeFlags;

        /// <summary>
        /// IMPORTANT: This can be true even when <see cref="IsServer"/> is also true.
        ///            At time of writing, the case for that would be when <see cref="clientTypeFlags"/> has <see cref="ClientTypeFlags.ServerHost"/> set.
        /// </summary>
        public static bool IsClient => _gonetClient == null ? isClient_asIndicatedByCommandLineArgs : _gonetClient.ClientTypeFlags != ClientTypeFlags.None;

        /// <summary>
        /// Is a client host in peer-to-peer or non-dedicated server setup?
        /// <see cref="ClientTypeFlags.ServerHost"/>.
        /// </summary>
        public static bool IsHost => IsServer && IsClient /* TODO && _gonetClient.ClientTypeFlags == ClientTypeFlags.ServerHost */;

        /// <summary>
        /// Since the value of <see cref="GONetParticipant.GONetId"/> can change (i.e., <see cref="Server_AssumeAuthorityOver(GONetParticipant)"/> called),
        /// this is the mechanism to find the original value at time of initial instantiation.  Not sure how this helps others, but internally to GONet it is useful.
        /// </summary>
        public static uint GetGONetIdAtInstantiation(uint currentGONetId)
        {
            GONetParticipant gonetParticipant;
            uint gonetIdAtInstantiation;
            if (gonetParticipantByGONetIdMap.TryGetValue(currentGONetId, out gonetParticipant))
            {
                return gonetParticipant.GONetIdAtInstantiation;
            }
            else if (recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map.TryGetValue(currentGONetId, out gonetIdAtInstantiation))
            {
                return gonetIdAtInstantiation;
            }
            else
            {
                return GONetParticipant.GONetId_Unset;
            }
        }

        public static uint GetCurrentGONetIdByIdAtInstantiation(uint gonetIdAtInstantiation)
        {
            GONetParticipant gonetParticipant = null;
            if (gonetParticipantByGONetIdAtInstantiationMap.TryGetValue(gonetIdAtInstantiation, out gonetParticipant))
            {
                return gonetParticipant.GONetId;
            }
            return GONetParticipant.GONetId_Unset;
        }

        /// <summary>
        /// IMPORTANT: Prior to things being initialized with network connection(s), we may not know if we are a client or a server...in which case, this will return false!
        /// </summary>
        public static bool IsClientVsServerStatusKnown => IsServer || IsClient;

        // NOTE: TimestampProvidedRatio is defined in GONet.Network.cs (same partial class)

        /// <summary>
        /// CRITICAL FIX (Dec 2025): Post-handoff grace period for demoted host.
        /// After voluntary handoff, the demoted host's blend buffers are reset (historyCount=0).
        /// The STALE_DATA check (historyCount <= 2) would reject the first sync samples from the new host,
        /// causing objects to stay stuck for ~24 seconds until historyCount > 2.
        /// During this grace period, bypass the historyCount check to apply sync immediately.
        /// </summary>
        private static long postHandoffGraceEndTicks = 0;
        private static bool postHandoffSyncWatchdogActive = false;
        private static long postHandoffSyncWatchdogStartTicks = 0;
        private static long postHandoffSyncWatchdogLastInboundTicks = 0;
        private static long postHandoffSyncWatchdogLastLogTicks = 0;

        /// <summary>
        /// Duration of post-handoff grace period in seconds.
        /// Objects need to receive at least 3 sync samples to pass the historyCount > 2 check.
        /// At typical 20 ticks/sec sync rate, 3 samples arrive in ~150ms.
        /// Use 2 seconds to be safe with network delays and to cover the initial sync burst.
        /// </summary>
        public const float POST_HANDOFF_GRACE_PERIOD_SECONDS = 2.0f;
        private const float POST_HANDOFF_SYNC_WATCHDOG_WINDOW_SECONDS = 20.0f;
        private const float POST_HANDOFF_SYNC_WATCHDOG_STALL_SECONDS = 2.0f;
        private const float POST_HANDOFF_SYNC_WATCHDOG_LOG_THROTTLE_SECONDS = 2.0f;

        /// <summary>
        /// Returns true if we're in the post-handoff grace period where STALE_DATA checks should be bypassed.
        /// This allows the demoted host to immediately apply sync from the new host.
        /// </summary>
        public static bool IsInPostHandoffGracePeriod =>
            postHandoffGraceEndTicks > 0 && GONetMain.Time.RawElapsedTicks < postHandoffGraceEndTicks;

        /// <summary>
        /// Starts the post-handoff grace period. Called after ResetBlendBuffersForDemotedHost().
        /// </summary>
        internal static void StartPostHandoffGracePeriod()
        {
            long durationTicks = (long)(POST_HANDOFF_GRACE_PERIOD_SECONDS * TimeSpan.TicksPerSecond);
            postHandoffGraceEndTicks = GONetMain.Time.RawElapsedTicks + durationTicks;
            GONetLog.Info($"[Handoff] Post-handoff grace period started ({POST_HANDOFF_GRACE_PERIOD_SECONDS}s) - STALE_DATA check bypassed");
            StartPostHandoffSyncWatchdog();
        }

        /// <summary>
        /// Ends the post-handoff grace period early. Called when sync is stable.
        /// </summary>
        internal static void EndPostHandoffGracePeriod()
        {
            if (postHandoffGraceEndTicks > 0)
            {
                postHandoffGraceEndTicks = 0;
                GONetLog.Info("[Handoff] Post-handoff grace period ended - STALE_DATA check re-enabled");
            }
        }

        internal static void StartPostHandoffSyncWatchdog()
        {
            if (!IsClient || IsServer)
            {
                return;
            }

            postHandoffSyncWatchdogActive = true;
            long now = Time.RawElapsedTicks;
            postHandoffSyncWatchdogStartTicks = now;
            Interlocked.Exchange(ref postHandoffSyncWatchdogLastInboundTicks, now);
            postHandoffSyncWatchdogLastLogTicks = 0;
            GONetLog.Info($"[Handoff] Post-handoff sync watchdog armed ({POST_HANDOFF_SYNC_WATCHDOG_WINDOW_SECONDS}s window, {POST_HANDOFF_SYNC_WATCHDOG_STALL_SECONDS}s stall)");
        }

        internal static void NotifyInboundSyncProcessed()
        {
            if (!postHandoffSyncWatchdogActive)
            {
                return;
            }

            Interlocked.Exchange(ref postHandoffSyncWatchdogLastInboundTicks, Time.RawElapsedTicks);
        }

        private static void UpdatePostHandoffSyncWatchdog()
        {
            if (!postHandoffSyncWatchdogActive)
            {
                return;
            }

            long now = Time.RawElapsedTicks;
            long elapsedSinceStart = now - postHandoffSyncWatchdogStartTicks;
            long windowTicks = (long)(POST_HANDOFF_SYNC_WATCHDOG_WINDOW_SECONDS * TimeSpan.TicksPerSecond);
            if (elapsedSinceStart > windowTicks)
            {
                postHandoffSyncWatchdogActive = false;
                return;
            }

            long lastInboundTicks = Interlocked.Read(ref postHandoffSyncWatchdogLastInboundTicks);
            long stallTicks = (long)(POST_HANDOFF_SYNC_WATCHDOG_STALL_SECONDS * TimeSpan.TicksPerSecond);
            if (now - lastInboundTicks < stallTicks)
            {
                return;
            }

            long logThrottleTicks = (long)(POST_HANDOFF_SYNC_WATCHDOG_LOG_THROTTLE_SECONDS * TimeSpan.TicksPerSecond);
            if (postHandoffSyncWatchdogLastLogTicks > 0 && now - postHandoffSyncWatchdogLastLogTicks < logThrottleTicks)
            {
                return;
            }

            postHandoffSyncWatchdogLastLogTicks = now;
            int deferredCount = incomingNetworkData_waitingForGONetReady.Count;
            if (deferredCount > 0)
            {
                GONetLog.Warning($"[Handoff] Processing deferred sync bundles after stall (queued={deferredCount})");
                ProcessDeferredSyncBundlesWaitingForGONetReady();
            }
            double secondsWithoutInbound = (now - lastInboundTicks) / (double)TimeSpan.TicksPerSecond;
            GONetLog.Warning($"[Handoff] No inbound sync processed for {secondsWithoutInbound:F2}s after demotion (epoch={HostEpoch}, myAuth={MyAuthorityId})");
        }

        private static GONetServer _gonetServer;
        /// <summary>
        /// This will be set internally only on the server side.  Do NOT set yourself!
        /// </summary>
        public static GONetServer gonetServer
        {
            get { return _gonetServer; }
            internal set
            {
                if (value != null)
                {
                    SessionGUID = GUID.Generate().AsInt64();
                }

                MyAuthorityId = OwnerAuthorityId_Server;
                _gonetServer = value;
                _gonetServer.ClientConnected += Server_OnClientConnected_SendClientCurrentState;
                _gonetServer.ClientDisconnected += Server_OnClientDisconnected_Cleanup;

                // CRITICAL: Reset time baseline when transitioning from lobby to active server
                // Without this, lobby startup time accumulates and causes time sync mismatches
                Time.ResetTimeBaseline();

                MyLocal = UnityEngine.Object.Instantiate(Global.gonetLocalPrefab);

                //const string INSTANTIATE = "Just called Instantiate server local context and now it has gonetId: ";
                //GONetLog.Debug(string.Concat(INSTANTIATE, MyLocal.GONetParticipant.GONetId));

                // Initialize distributed host gossip system if enabled (server is the initial host)
                // Pass the server's transport so hot standby can use virtual ports for Steamworks
                InitDistributedHostIfEnabled(isHost: true, transport: _gonetServer.Transport);
            }
        }

        public static GONetEventBus EventBus => GONetEventBus.Instance;
        private static bool areEventSubscriptionsInitialized;

        internal static void MarkEventSubscriptionsCleared()
        {
            areEventSubscriptionsInitialized = false;
        }

        public const string REQUIRED_CALL_UNITY_MAIN_THREAD = "Not allowed to call this from any other thread than the main Unity thread.";
        private static Thread mainUnityThread;
        public static bool IsUnityMainThread => mainUnityThread == Thread.CurrentThread;

        public static bool IsApplicationQuitting { get; private set; }

        /// <summary>
        /// Throws an exception if not called from main Unity thread (see <see cref="IsUnityMainThread"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnsureMainThread_IfPlaying()
        {
            if (Application.isPlaying && !IsUnityMainThread)
            {
                throw new InvalidOperationException(REQUIRED_CALL_UNITY_MAIN_THREAD);
            }
        }

        public static bool IsUnityApplicationEditor => Application.isEditor;

        /// <summary>
        /// Late-joiner synchronization storage: "Current state" list of persistent events.
        /// This will NOT include ALL events that implement <see cref="IPersistentEvent"/> if anything
        /// cancelled out another/previous event (i.e., <see cref="ICancelOutOtherEvents"/>).
        ///
        /// ⚠️  CRITICAL DESIGN CONSTRAINT: EVENTS STORED BY REFERENCE
        ///
        /// This LinkedList stores DIRECT REFERENCES to persistent event objects for the entire
        /// session duration (minutes to hours). When late-joining clients connect, these exact
        /// references are serialized and transmitted (see Server_SendClientPersistentEventsSinceStart:4355).
        ///
        /// IMPLICATIONS FOR EVENT CLASSES:
        /// Any class that goes into this list (implements IPersistentEvent) MUST NOT use object pooling.
        /// If pooled events were stored here:
        ///   1. Event created and added to this list by reference
        ///   2. Event.Return() called → data cleared, returned to pool
        ///   3. Pool reuses object → stored reference now contains WRONG data
        ///   4. Late-joiner connects → receives corrupted data from stored reference
        ///   5. CATASTROPHIC: Game state desynchronization, crashes, invisible bugs
        ///
        /// Classes that CORRECTLY avoid pooling (do NOT implement ISelfReturnEvent):
        /// - PersistentRpcEvent (see GONetRpcs.cs:912 for detailed rationale)
        /// - PersistentRoutedRpcEvent (TargetRpc variant)
        /// - InstantiateGONetParticipantEvent (spawn events)
        /// - DespawnGONetParticipantEvent (despawn with cancellation)
        /// - SceneLoadEvent (networked scene management)
        ///
        /// Memory cost: ~1-10 KB per session (acceptable for data integrity guarantee)
        ///
        /// See also:
        /// - OnPersistentEvent_KeepTrack() at line 1549 - where events are added to this list
        /// - Server_SendClientPersistentEventsSinceStart() at line 4355 - where stored references are transmitted
        /// - GONetRpcs.cs:912 - PersistentRpcEvent class documentation for full pooling rationale
        /// </summary>
        static readonly LinkedList<IPersistentEvent> persistentEventsThisSession = new LinkedList<IPersistentEvent>();

        /// <summary>
        /// RECORD AND REPLAY ARCHIVE: Complete historical record of ALL persistent events that occurred during this session.
        /// Unlike <see cref="persistentEventsThisSession"/>, this list is NEVER modified - events are only added, never removed.
        /// This preserves the full event timeline including cancelled events for future record/replay functionality.
        ///
        /// Use cases:
        /// - Session replay: Replay the exact sequence of events that occurred
        /// - Debugging: Analyze full event history including cancelled events
        /// - Analytics: Track complete session timeline
        ///
        /// NOTE: This archive is currently NOT used for late-joiner synchronization - that uses persistentEventsThisSession.
        /// </summary>
        static readonly LinkedList<IPersistentEvent> persistentEventsArchive_CompleteHistory = new LinkedList<IPersistentEvent>();

        #region Persistent Event Diagnostics
        // DIAGNOSTIC TRACKING (Dec 2025): Track persistent event accumulation and performance
        private static long persistentEventDiag_lastLogTicks = 0;
        private static int persistentEventDiag_eventsProcessedSinceLastLog = 0;
        private static long persistentEventDiag_totalIterationsSinceLastLog = 0;
        private static long persistentEventDiag_totalCancelCheckTimeTicks = 0;
        private static readonly Dictionary<string, int> persistentEventDiag_eventTypeCountsSinceLastLog = new Dictionary<string, int>(32);
        private const long PERSISTENT_EVENT_DIAG_LOG_INTERVAL_TICKS = 20_000_000; // 2 seconds

        // DIAGNOSTIC TRACKING: PublishEvents_SentToOthers queue monitoring
        private static long publishEventsDiag_lastLogTicks = 0;
        private static int publishEventsDiag_totalEventsPublished = 0;
        private static int publishEventsDiag_maxQueueSizeSeen = 0;
        private static int publishEventsDiag_publishCallCount = 0;
        private static long publishEventsDiag_totalPublishTimeTicks = 0;
        private static readonly Dictionary<string, int> publishEventsDiag_eventTypeCountsSinceLastLog = new Dictionary<string, int>(32);
        #endregion

        /// <summary>
        /// PUBLIC API: Access the complete historical archive of all persistent events.
        /// This is a read-only view of the full event timeline including cancelled events.
        ///
        /// IMPORTANT: This returns the internal list - do NOT modify it! Only use for reading/iteration.
        ///
        /// Example usage for future record/replay:
        /// <code>
        /// // Save complete session history to file
        /// var allEvents = GONetMain.PersistentEventsArchive_CompleteHistory;
        /// SaveToFile(allEvents);
        ///
        /// // Later: Replay the exact sequence
        /// foreach (var evt in LoadFromFile())
        /// {
        ///     ReplayEvent(evt);
        /// }
        /// </code>
        /// </summary>
        public static IEnumerable<IPersistentEvent> PersistentEventsArchive_CompleteHistory => persistentEventsArchive_CompleteHistory;

        static readonly List<uint> gonetIdsDestroyedViaPropagation = new List<uint>(500);

        /// <summary>
        /// Marks a GONetId as expected to be destroyed locally without triggering ownership warnings.
        /// Used when a node must destroy a non-owned GNP as part of failover reconciliation.
        /// </summary>
        internal static void MarkGONetIdDestroyedViaPropagation(uint gonetId)
        {
            if (gonetId == GONetParticipant.GONetId_Unset)
            {
                return;
            }

            if (!gonetIdsDestroyedViaPropagation.Contains(gonetId))
            {
                gonetIdsDestroyedViaPropagation.Add(gonetId);
            }
        }


        static readonly Dictionary<Type, SyncEventsSaveSupport> syncEventsToSaveQueueByEventType = new Dictionary<Type, SyncEventsSaveSupport>(100);

        /// <summary>
        /// The keys are only added from main unity thread...the value queues are only added to on the other thread.
        /// At last time of updating this declaration, this will be used to store <see cref="SyncEvent_ValueChangeProcessed"/>, <see cref="ValueMonitoringSupport_BaselineExpiredEvent"/> and <see cref="ValueMonitoringSupport_NewBaselineEvent"/> child classes.
        /// </summary>
        static readonly Dictionary<Thread, Queue<IGONetEvent>> events_AwaitingSendToOthersQueue_ByThreadMap = new Dictionary<Thread, Queue<IGONetEvent>>(12);

        /// <summary>
        /// The keys are only added from main unity thread...the value queues are only added to on the other thread (i.e., transfer data from <see cref="events_AwaitingSendToOthersQueue_ByThreadMap"/> once the time is right) but also read from and dequeued from the main unity thread when time to publish the events!
        /// </summary>
        static readonly Dictionary<Thread, RingBuffer<IGONetEvent>> events_SendToOthersQueue_ByThreadMap = new Dictionary<Thread, RingBuffer<IGONetEvent>>(12);

        /// <summary>
        /// The keys are only added from main unity thread...the value queues are only added to on the other thread (i.e., transfer data from <see cref="events_AwaitingSendToOthersQueue_ByThreadMap"/> once the time is right) but also read from and dequeued from the main unity thread when time to publish the events!
        /// </summary>
        static readonly Queue<SyncEvent_ValueChangeProcessed> syncValueChanges_ReceivedFromOtherQueue = new Queue<SyncEvent_ValueChangeProcessed>(100);

        /// <summary>
        /// AUTHORITY-AGNOSTIC: Queue for sync bundles deferred due to participants still in Awake/initialization.
        /// Used by BOTH authority owners (clients/server spawning objects) AND non-authority receivers.
        /// Only populated when GONetGlobal.deferSyncBundlesWaitingForGONetReady == true.
        /// Processed incrementally when any participant completes OnGONetReady (up to maxBundlesProcessedPerGONetReadyCallback per callback).
        /// </summary>
        internal static readonly Queue<NetworkData> incomingNetworkData_waitingForGONetReady = new Queue<NetworkData>(100);

        internal static GONetClient _gonetClient;
        /// <summary>
        /// This will be set internally only on the client side.  Do NOT set yourself!
        /// </summary>
        public static GONetClient GONetClient
        {
            get => _gonetClient;

            internal set
            {
                ClientTypeFlags flagsPrevious = MyClientTypeFlags;
                GONetClient previousClient = _gonetClient;

                _gonetClient = value;

                ClientTypeFlags flagsNow = MyClientTypeFlags;

                if (flagsNow != flagsPrevious)
                {
                    EventBus.Publish(new ClientTypeFlagsChangedEvent(Time.ElapsedTicks, MyAuthorityId, flagsPrevious, flagsNow));
                }

                bool shouldResetTimeBaseline = true;
                bool isVoluntaryDemotionActive = false;
                if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableDistributedHostAuthority)
                {
                    var failoverManager = DistributedHost.GONetHostFailoverManager.Instance;
                    isVoluntaryDemotionActive = failoverManager != null && failoverManager.DidVoluntarilyDemote;
                    if (isVoluntaryDemotionActive)
                    {
                        shouldResetTimeBaseline = false;
                    }
                }

                ulong previousClientUid = previousClient?.InitiatingClientConnectionUID ?? 0UL;
                ulong newClientUid = value?.InitiatingClientConnectionUID ?? 0UL;
                if (shouldResetTimeBaseline)
                {
                    // CRITICAL: Reset time baseline when transitioning from lobby to active client
                    // Without this, lobby startup time accumulates and causes time sync mismatches
                    Time.ResetTimeBaseline();
                }

                // Reset gap-closing interval initialization to allow re-reading config on reconnect
                ResetGapClosingIntervalInitialization();

                _gonetClient.InitializedWithServer += Client_gonetClient_InitializedWithServer;
                _gonetClient.ClientDisconnected += Client_gonetClient_Disconnected;
            }
        }

        /// <summary>
        /// Handler for when the main client connection disconnects.
        /// NOTE: This handler only logs the disconnect. Failover is triggered EXCLUSIVELY
        /// by the heartbeat timeout system in <see cref="DistributedHost.GONetHostFailoverManager"/>.
        /// </summary>
        private static void Client_gonetClient_Disconnected(GONetClient client)
        {
            // IMPORTANT: This handler ONLY logs the disconnect event.
            // Failover is triggered EXCLUSIVELY by the heartbeat timeout system in GONetHostFailoverManager.
            //
            // Rationale: Connection-loss events from the transport layer are inherently unreliable,
            // especially on real internet connections. Spurious disconnect events can occur even when
            // the host is still alive and sending heartbeats. The heartbeat timeout system provides
            // a robust, deterministic way to detect host death that works consistently across
            // different network conditions.
            //
            // The heartbeat system handles failover via:
            // 1. CheckForHostHeartbeatTimeout() detects stale heartbeats
            // 2. BeginFailover() starts the failover process
            // 3. CompleteSelfPromotion() promotes to host if we're the lowest authority

            GONetLog.Info($"[Connection] Main client connection disconnected (client was connected: {client?.connectionToServer != null})");

            // Log additional context for debugging
            if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableDistributedHostAuthority)
            {
                var failoverManager = DistributedHost.GONetHostFailoverManager.Instance;
                if (failoverManager != null)
                {
                    float timeSinceHeartbeat = failoverManager.TimeSinceLastHeartbeat;
                    GONetLog.Info($"[Connection] Distributed host enabled. Time since last heartbeat: {timeSinceHeartbeat:F2}s. " +
                                  $"Failover will be triggered by heartbeat timeout system if host is truly dead.");
                }
            }
        }

        /// <summary>
        /// Switches the active client to a standby connection during hot standby failover.
        /// CRITICAL: This is the heart of traffic switchover - all game traffic instantly
        /// flows through the new connection which was already established to the new host.
        /// </summary>
        /// <param name="standbyClient">The standby client that has an active connection to the new host</param>
        /// <param name="newHostAuthorityId">Authority ID of the new host</param>
        internal static void SwitchToStandbyClient(GONetClient standbyClient, ushort newHostAuthorityId)
        {
            if (standbyClient == null)
            {
                GONetLog.Error("[HotStandby] Cannot switch - standby client is null");
                return;
            }

            var oldClient = _gonetClient;

            GONetLog.Info($"[HotStandby] Switching from old client to standby client (new host: {newHostAuthorityId})");

            // Store the old connection authority for logging
            ushort oldServerAuthority = oldClient?.connectionToServer?.OwnerAuthorityId ?? 0;

            // Update the server authority ID on the standby connection
            // The standby connection was pointing to the dormant server which is now the active host
            standbyClient.connectionToServer.OwnerAuthorityId = OwnerAuthorityId_Server;

            // Switch to the standby client
            // NOTE: We don't go through the property setter to avoid re-initialization logic
            // that's only appropriate for fresh connections
            _gonetClient = standbyClient;

            // CRITICAL (December 2025): The standby client was connected to a dormant server and never
            // went through normal initialization. We need to:
            // 1. Set IsInitializedWithServer = true so the UI shows "Connected (Initialized)"
            // 2. Subscribe to client events for proper state management
            // Without #1, the UI shows incorrect status. Without #2, disconnects won't be handled.

            // Mark the standby client as initialized - it's connected to the promoted dormant server
            // which has all the game state (the client already had state from the original server)
            if (!standbyClient.IsInitializedWithServer)
            {
                GONetLog.Info($"[HotStandby] Setting IsInitializedWithServer=true on standby client for proper status display");
                standbyClient.IsInitializedWithServer = true;
            }

            // Subscribe to client events - IMPORTANT: Don't subscribe to InitializedWithServer since
            // that would try to instantiate a new MyLocal (which already exists from original connection)
            // Only subscribe to disconnect handling
            standbyClient.ClientDisconnected += Client_gonetClient_Disconnected;

            // Publish event for switchover (game code can react)
            EventBus.Publish(new HostSwitchoverEvent(
                Time.ElapsedTicks,
                MyAuthorityId,
                oldServerAuthority,
                newHostAuthorityId
            ));

            GONetLog.Info($"[HotStandby] Client switchover complete: old host {oldServerAuthority} -> new host {newHostAuthorityId}");
            GONetLog.Warning($"[TimeSync] Standby switchover time state: raw={(Time.RawElapsedTicks / (double)TimeSpan.TicksPerSecond):F3}s, " +
                             $"elapsed={(Time.ElapsedTicks / (double)TimeSpan.TicksPerSecond):F3}s, " +
                             $"offset={(Time.GetEffectiveOffsetTicks_Internal() / (double)TimeSpan.TicksPerSecond):F3}s, " +
                             $"firstSync={client_isFirstTimeSync}, gapClosed={client_hasClosedTimeSyncGapWithServer}, myAuth={MyAuthorityId}");

            // Clean up old client connection (don't disconnect immediately - let it timeout naturally)
            // This prevents duplicate disconnect messages
            if (oldClient != null)
            {
                // Unsubscribe the old client's events to prevent duplicate callbacks and re-triggering failover
                oldClient.InitializedWithServer -= Client_gonetClient_InitializedWithServer;
                oldClient.ClientDisconnected -= Client_gonetClient_Disconnected;
            }
        }

        private static void Client_gonetClient_InitializedWithServer(GONetClient client)
        {
            GONetLog.Info($"[INIT-TIMELINE] CLIENT T+1: Client_gonetClient_InitializedWithServer() CALLED at {Time.ElapsedSeconds:F3}s - MyAuthorityId={MyAuthorityId}");
            GONetLog.Info($"[INIT-TIMELINE] CLIENT T+1: About to instantiate GONetLocal prefab at {Time.ElapsedSeconds:F3}s");

            MyLocal = UnityEngine.Object.Instantiate(Global.gonetLocalPrefab);

            GONetLog.Info($"[INIT-TIMELINE] CLIENT T+2: GONetLocal instantiated (Awake/OnEnable completed) at {Time.ElapsedSeconds:F3}s");

            // CRITICAL: Set OwnerAuthorityId AFTER instantiation but BEFORE Start() is called
            // The GONetParticipant.Start() sends spawn event, so OwnerAuthorityId must be correct by then
            MyLocal.GONetParticipant.OwnerAuthorityId = MyAuthorityId;

            // CRITICAL: Move GONetLocal to DontDestroyOnLoad scene IMMEDIATELY after instantiation
            // This prevents it from being incorrectly recorded as "defined in scene" if a scene load is in progress
            UnityEngine.Object.DontDestroyOnLoad(MyLocal.gameObject);
            GONetLog.Info($"[INIT-TIMELINE] CLIENT T+2: GONetLocal setup complete (OwnerAuthorityId set, moved to DontDestroyOnLoad) at {Time.ElapsedSeconds:F3}s - MyLocal GONetId: {(MyLocal?.GONetParticipant?.GONetId ?? 0)}, OwnerAuthorityId: {MyLocal?.GONetParticipant?.OwnerAuthorityId ?? 0}");

            // Initialize distributed host gossip system if enabled (client is NOT the host)
            // Pass the client's transport so hot standby can use virtual ports for Steamworks
            InitDistributedHostIfEnabled(isHost: false, transport: client.Transport);

            // Request reconciliation if mesh is enabled and a failover has occurred
            // This is the client-pull model: each client requests when THEY are ready
            if (GONetGlobal.Instance != null &&
                GONetGlobal.Instance.enableDistributedHostAuthority &&
                HostEpoch > 1)
            {
                GONetLog.Info($"[Reconciliation] Client requesting reconciliation (epoch={HostEpoch})");
                var request = new ReconciliationRequestEvent(clientEpoch: HostEpoch);
                request.OccurredAtElapsedTicks = Time.ElapsedTicks;
                EventBus.Publish(request);
            }

            // CRITICAL FIX: Process queued messages in TWO PASSES to ensure correct ordering
            // Pass 1: Process EVENT messages first (SceneLoadEvent, etc.) to establish scene state
            // Pass 2: Process VALUE messages (AllValues bundles) AFTER scene state is known
            // Without this ordering, AllValues bundles may be processed before SceneLoadEvent,
            // causing "scene is invalid" errors because scenesLoading is empty.
            var deferredValueMessages = new List<NetworkData>();

            // Pass 1: Process events first, defer value messages
            while (client.incomingNetworkData_mustProcessAfterClientInitialized.Count > 0)
            {
                NetworkData item = client.incomingNetworkData_mustProcessAfterClientInitialized.Dequeue();

                // Check if this is an event channel or a value channel
                bool isEventChannel = item.channelId == GONetChannel.ClientInitialization_EventSingles_Reliable.Id ||
                                     item.channelId == GONetChannel.EventSingles_Reliable.Id ||
                                     item.channelId == GONetChannel.EventSingles_Unreliable.Id;

                if (isEventChannel)
                {
                    // Process events immediately to establish scene state
                    ProcessIncomingBytes_QueuedNetworkData_MainThread_INTERNAL(item);
                }
                else
                {
                    // Defer value messages until after events are processed
                    deferredValueMessages.Add(item);
                }
            }

            // Pass 2: Now process deferred value messages (scenesLoading should be populated)
            foreach (NetworkData item in deferredValueMessages)
            {
                ProcessIncomingBytes_QueuedNetworkData_MainThread_INTERNAL(item);
            }

            // If client already has scenes loaded (no SceneLoadEvent needed), notify server now.
            SceneManager?.NotifyServerOfLoadedScenesIfClientInitialized();

            // HOST MODE FIX: Only pure clients should send initial time sync barrage.
            // Host IS the time authority - it should never request time sync from itself.
            if (!IsServer)
            {
                Client_SyncTimeWithServer_SendInitialBarrage();
            }
        }

        /// <summary>
        /// Returns true when sync bundles should be deferred until scene-defined GONetIds are available.
        /// </summary>
        internal static bool ShouldDeferSyncBundlesForGONetIdSync()
        {
            if (!IsClient || IsServer || _gonetClient == null)
                return false;

            // CRITICAL FIX (January 2026): Demoted hosts already have the scene loaded and GONetIds assigned.
            // During post-handoff grace period, bypass the GONetId sync deferral - the demoted host
            // receives redundant scene load notifications that would otherwise block sync bundles
            // from being processed, causing "No inbound sync processed" and frozen objects.
            if (IsInPostHandoffGracePeriod)
                return false;

            GONetSceneManager sceneManager = SceneManager;
            if (sceneManager == null)
                return !_gonetClient.areSceneDefinedObjectIdsReady;

            return sceneManager.IsCurrentlyLoadingScene || sceneManager.HasPendingGONetIdSync;
        }

        internal static bool AreSceneDefinedObjectIdsReady()
        {
            if (!IsClient || _gonetClient == null)
                return true;

            return !ShouldDeferSyncBundlesForGONetIdSync();
        }

        internal static void Client_UpdateSceneDefinedObjectIdsReadyFlag()
        {
            if (!IsClient || _gonetClient == null)
                return;

            bool shouldDefer = ShouldDeferSyncBundlesForGONetIdSync();
            bool isReady = !shouldDefer;
            if (_gonetClient.areSceneDefinedObjectIdsReady == isReady)
                return;

            _gonetClient.areSceneDefinedObjectIdsReady = isReady;

            if (isReady)
            {
                GONetLog.Info("[GONETID-READY] Scene-defined GONetIds ready; processing queued sync bundles");
                ProcessQueuedMessagesWaitingForGONetIds();
            }
            else
            {
                GONetLog.Info("[GONETID-READY] Scene-defined GONetIds not ready; deferring sync bundles");
            }
        }

        /// <summary>
        /// Processes messages that were queued because they referenced GONetIds that weren't assigned yet.
        /// Called after scene-defined object GONetIds have been synchronized from the server.
        /// </summary>
        internal static void ProcessQueuedMessagesWaitingForGONetIds()
        {
            if (!IsClient || _gonetClient == null)
                return;

            if (ShouldDeferSyncBundlesForGONetIdSync())
            {
                GONetLog.Debug("[GONETID-QUEUE] Deferring queued message processing until scene-defined GONetIds are ready");
                return;
            }

            int queueSize = _gonetClient.incomingNetworkData_waitingForGONetIds.Count;
            if (queueSize == 0)
            {
                GONetLog.Debug("[GONETID-QUEUE] No queued messages to process");
                return;
            }

            GONetLog.Info($"[GONETID-QUEUE] Processing {queueSize} queued messages that were waiting for GONetId assignments");

            int processedCount = 0;
            int failedCount = 0;

            while (_gonetClient.incomingNetworkData_waitingForGONetIds.Count > 0)
            {
                NetworkData item = _gonetClient.incomingNetworkData_waitingForGONetIds.Dequeue();
                try
                {
                    ProcessIncomingBytes_QueuedNetworkData_MainThread_INTERNAL(item, isProcessingFromQueue: true);
                    processedCount++;
                }
                catch (Exception e)
                {
                    failedCount++;
                    GONetLog.Error($"[GONETID-QUEUE] Failed to process queued message: {e.Message}");
                    // Still need to return the message to the pool
                    SingleProducerQueues queues = singleProducerReceiveQueuesByThread[item.messageBytesBorrowedOnThread];
                    queues.queueForPostWorkResourceReturn.Enqueue(item);
                }
            }

            GONetLog.Info($"[GONETID-QUEUE] Finished processing queued messages - Processed: {processedCount}, Failed: {failedCount}");
        }

        /// <summary>
        /// Defers a sync bundle for retry after participant completes OnGONetReady.
        /// FIFO queue with size limit - oldest bundles dropped if queue full.
        /// </summary>
        private static void DeferSyncBundleWaitingForGONetReady(NetworkData networkData, long elapsedTicksAtSend, Type messageType)
        {
            int maxQueueSize = Math.Max(1, GONetGlobal.Instance.maxSyncBundlesWaitingForGONetReady);

            // Set deferral timestamp if not already set (first deferral)
            if (networkData.deferredAtTicks == 0)
            {
                networkData.deferredAtTicks = HighResolutionTimeUtils.UtcNowTicks;
            }

            if (incomingNetworkData_waitingForGONetReady.Count < maxQueueSize)
            {
                incomingNetworkData_waitingForGONetReady.Enqueue(networkData);
            }
            else
            {
                // Queue full - drop OLDEST bundle and queue newest (FIFO policy)
                // COMMENTED OUT (Dec 2025): This diagnostic caused 2,400+ warnings during handoff, killing frame rate
                //GONetLog.Warning($"[GONETREADY-QUEUE] Queue full ({maxQueueSize} bundles)! " +
                //                $"Dropping OLDEST deferred bundle to make room. " +
                //                $"Consider increasing GONetGlobal.maxSyncBundlesWaitingForGONetReady or disabling deferral. " +
                //                $"MessageType: {messageType.Name}");

                NetworkData droppedMessage = incomingNetworkData_waitingForGONetReady.Dequeue();

                // Return dropped message's byte array to pool (critical for memory management)
                SingleProducerQueues droppedQueues = singleProducerReceiveQueuesByThread[droppedMessage.messageBytesBorrowedOnThread];
                droppedQueues.queueForPostWorkResourceReturn.Enqueue(droppedMessage);

                // Queue current message
                incomingNetworkData_waitingForGONetReady.Enqueue(networkData);
            }
        }

        /// <summary>
        /// Processes deferred sync bundles waiting for participants to complete OnGONetReady.
        /// Called automatically when any participant completes OnGONetReady.
        /// Processes up to maxBundlesProcessedPerGONetReadyCallback bundles per call to prevent frame stutter.
        /// </summary>
        internal static void ProcessDeferredSyncBundlesWaitingForGONetReady()
        {
            // Feature disabled - nothing to process
            if (!GONetGlobal.Instance.deferSyncBundlesWaitingForGONetReady)
                return;

            int queueSize = incomingNetworkData_waitingForGONetReady.Count;
            if (queueSize == 0)
                return; // Nothing queued

            int processedCount = 0;
            int failedCount = 0;

            // PERFORMANCE: Limit processing per callback to prevent frame stutter during mass spawns
            // OnGONetReady fires for EVERY participant - processing all queued bundles would cause spikes
            int maxPerCallback = Math.Max(1, GONetGlobal.Instance.maxBundlesProcessedPerGONetReadyCallback);

            // CRITICAL: Snapshot the count up-front so bundles that re-defer while processing
            // are not immediately re-processed in the same callback (avoids tight retry loops).
            int itemsToAttempt = Math.Min(incomingNetworkData_waitingForGONetReady.Count, maxPerCallback);
            for (int i = 0; i < itemsToAttempt; ++i)
            {
                NetworkData item = incomingNetworkData_waitingForGONetReady.Dequeue();

                // Check timeout for missing participants
                bool isTimedOut = false;
                if (item.deferredAtTicks > 0 && GONetGlobal.Instance.maxSecondsToWaitForMissingParticipant > 0)
                {
                    long nowTicks = HighResolutionTimeUtils.UtcNowTicks;
                    double elapsedSeconds = TimeSpan.FromTicks(nowTicks - item.deferredAtTicks).TotalSeconds;
                    isTimedOut = elapsedSeconds > GONetGlobal.Instance.maxSecondsToWaitForMissingParticipant;
                }

                if (isTimedOut)
                {
                    // Timeout exceeded - drop bundle with diagnostic
                    failedCount++;
                    double waitedSeconds = TimeSpan.FromTicks(HighResolutionTimeUtils.UtcNowTicks - item.deferredAtTicks).TotalSeconds;
                    GONetLog.Warning($"[GONETREADY-TIMEOUT] Dropping deferred bundle - participant never spawned after {waitedSeconds:F2}s " +
                                    $"(timeout: {GONetGlobal.Instance.maxSecondsToWaitForMissingParticipant}s). " +
                                    $"Channel: {item.channelId}, QueueSize: {incomingNetworkData_waitingForGONetReady.Count}");

                    // Return byte array to pool
                    SingleProducerQueues queues = singleProducerReceiveQueuesByThread[item.messageBytesBorrowedOnThread];
                    queues.queueForPostWorkResourceReturn.Enqueue(item);
                    continue;
                }

                try
                {
                    // CRITICAL: Pass isProcessingFromQueue=true to prevent infinite retry loops
                    // If participant STILL not ready after retry, exception handler will DROP (not requeue)
                    ProcessIncomingBytes_QueuedNetworkData_MainThread_INTERNAL(item, isProcessingFromQueue: true);
                    processedCount++;
                }
                catch (GONetParticipantNotReadyException notReadyEx)
                {
                    // Participant STILL not ready after 1+ frames - DROP (don't requeue)
                    // Exception handler in ProcessIncomingBytes already logged error via isProcessingFromQueue check
                    failedCount++;

                    // Return byte array to pool
                    SingleProducerQueues queues = singleProducerReceiveQueuesByThread[item.messageBytesBorrowedOnThread];
                    queues.queueForPostWorkResourceReturn.Enqueue(item);
                }
                catch (Exception e)
                {
                    // Unexpected failure - log and drop
                    failedCount++;
                    GONetLog.Error($"[GONETREADY-QUEUE] Failed to process deferred bundle: {e.Message}\n{e.StackTrace}");

                    // Return byte array to pool
                    SingleProducerQueues queues = singleProducerReceiveQueuesByThread[item.messageBytesBorrowedOnThread];
                    queues.queueForPostWorkResourceReturn.Enqueue(item);
                }
            }

            // Diagnostic logging (only if something happened)
            if (processedCount > 0 || failedCount > 0)
            {
                //GONetLog.Debug($"[GONETREADY-QUEUE] Processed {processedCount} deferred bundles, " +
//                              $"{failedCount} dropped (still not ready after retry), " +
//                              $"{incomingNetworkData_waitingForGONetReady.Count} remaining in queue");
            }
        }

        internal static readonly Dictionary<uint, GONetParticipant> gonetParticipantByGONetIdMap = new Dictionary<uint, GONetParticipant>(1000);
        internal static readonly Dictionary<uint, GONetParticipant> gonetParticipantByGONetIdAtInstantiationMap = new Dictionary<uint, GONetParticipant>(5000);
        internal static readonly Dictionary<uint, uint> recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map = new Dictionary<uint, uint>(1000);

        /// <summary>
        /// Tracks last warning time for each GONetId to suppress excessive "Unable to find GONetParticipant" warnings.
        /// Key: GONetId, Value: Time.ElapsedTicks when warning was last logged.
        /// Prevents log spam when unreliable sync events arrive after despawn (expected race condition).
        /// </summary>
        private static readonly Dictionary<uint, long> missingGONetParticipantWarningSuppressionMap = new Dictionary<uint, long>(100);

        /// <summary>
        /// Tracks last lookup recovery attempt time for each GONetId to avoid repeated full-scene scans.
        /// Key: GONetId, Value: raw stopwatch ticks of last recovery attempt.
        /// </summary>
        private static readonly Dictionary<uint, long> missingGONetParticipantRecoveryLastAttempt = new Dictionary<uint, long>(128);

        /// <summary>
        /// Minimum seconds between lookup recovery scans for the same GONetId.
        /// </summary>
        private const double MissingParticipantRecoveryRetrySeconds = 1.0;
        private static readonly long MissingParticipantRecoveryRetryTicks = (long)(MissingParticipantRecoveryRetrySeconds * Stopwatch.Frequency);

        /// <summary>
        /// Tracks last cleanup time for <see cref="missingGONetParticipantWarningSuppressionMap"/>.
        /// Cleanup runs once every 10 seconds to prevent unbounded dictionary growth.
        /// </summary>
        private static long? _lastWarningSuppressionCleanupTicks;

        /// <summary>
        /// Total count of unreliable packets dropped due to send buffer full (BorrowedCount > MAX_PACKETS_PER_TICK - 10).
        /// Incremented in SendBytesToRemoteConnection when flow control throttles unreliable messages.
        /// </summary>
        private static long _unreliablePacketDropCount = 0;


        public const ushort OwnerAuthorityId_Unset = 0;
        public const ushort OwnerAuthorityId_Server = unchecked((ushort)(ushort.MaxValue << GONetParticipant.OWNER_AUTHORITY_ID_BIT_COUNT_UNUSED)) >> GONetParticipant.OWNER_AUTHORITY_ID_BIT_COUNT_UNUSED;

        /// <summary>
        /// Only used/applicable if <see cref="IsServer"/> is true.
        /// </summary>
        private static ushort server_lastAssignedAuthorityId = OwnerAuthorityId_Unset;

        /// <summary>
        /// <para>IMPORTANT: Up until some time during <see cref="GONetParticipant.Start"/>, the value of <see cref="GONetParticipant.OwnerAuthorityId"/> will be <see cref="GONetMain.OwnerAuthorityId_Unset"/> and the owner is essentially unknown, which means this method will return false for everyone (even the actual owner).  Once the owner is known, <see cref="GONetParticipant.OwnerAuthorityId"/> value will change and the <see cref="SyncEvent_GONetParticipant_OwnerAuthorityId"/> event will fire (i.e., you should call <see cref="GONetEventBus.Subscribe{T}(GONetEventBus.HandleEventDelegate{T}, GONetEventBus.EventFilterDelegate{T})"/> on <see cref="EventBus"/>)</para>
        /// <para>Use this to write code that does one thing if you are the owner and another thing if not.</para>
        /// <para>From a GONet perspective, this checks if the <paramref name="gameObject"/> has a <see cref="GONetParticipant"/> and if so, whether or not you own it.</para>
        /// <para>If you already have access to the <see cref="GONetParticipant"/> associated with this <paramref name="gameObject"/>, then use the sister method instead: <see cref="IsMine(GONetParticipant)"/></para>
        /// </summary>
        public static bool IsMine(GameObject gameObject)
        {
            return gameObject.GetComponent<GONetParticipant>()?.OwnerAuthorityId == MyAuthorityId; // TODO cache instead of lookup/get each time!
        }

        /// <summary>
        /// <para>IMPORTANT: Up until some time during <see cref="GONetParticipant.Start"/>, the value of <see cref="GONetParticipant.OwnerAuthorityId"/> will be <see cref="GONetMain.OwnerAuthorityId_Unset"/> and the owner is essentially unknown, which means this method will return false for everyone (even the actual owner).  Once the owner is known, <see cref="GONetParticipant.OwnerAuthorityId"/> value will change and the <see cref="SyncEvent_GONetParticipant_OwnerAuthorityId"/> event will fire (i.e., you should call <see cref="GONetEventBus.Subscribe{T}(GONetEventBus.HandleEventDelegate{T}, GONetEventBus.EventFilterDelegate{T})"/> on <see cref="EventBus"/>)</para>
        /// <para>Use this to write code that does one thing if you are the owner and another thing if not.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsMine(GONetParticipant gonetParticipant)
        {
            return gonetParticipant.OwnerAuthorityId == MyAuthorityId;
        }

        /// <summary>
        /// <para>IMPORTANT: Keep in mind if <paramref name="gonetParticipant"/> has many auto sync members, *ALL* of them have to have enough values in history to support a smooth assumption of authority.</para>
        /// <para>           Even if only transform position and rotation are auto sync'd, both of them have had to changed at least <see cref="ValueBlendUtils.VALUE_COUNT_NEEDED_TO_EXTRAPOLATE"/> times in order for this to return true.</para>
        /// <para>           Therefore, this method is only to be required to return true prior to calling <see cref="Server_AssumeAuthorityOver(GONetParticipant)"/> when it is known to make sense!</para>
        /// <para>           This is up to you to use or not.  You can still call <see cref="Server_AssumeAuthorityOver(GONetParticipant)"/> when this return false and the world will not end and the assumption of authority will still occur (ASSuming <see cref="Server_AssumeAuthorityOver(GONetParticipant)"/> returns true)!</para>
        /// </summary>
        /// <param name="gonetParticipant"></param>
        /// <returns></returns>
        public static bool Server_HasEnoughValueBlendHistoryToSmoothly_AssumeAuthorityOver(GONetParticipant gonetParticipant)
        {
            if (!IsServer || gonetParticipant.OwnerAuthorityId == OwnerAuthorityId_Unset || IsMine(gonetParticipant))
            {
                return false;
            }

            Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> autoSyncCompanions;
            GONetParticipant_AutoMagicalSyncCompanion_Generated autoSyncCompanion;
            if (activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(gonetParticipant.CodeGenerationId, out autoSyncCompanions) &&
                autoSyncCompanions.TryGetValue(gonetParticipant, out autoSyncCompanion))
            {
                byte valuesCount = autoSyncCompanion.valuesCount;
                for (int i = 0; i < valuesCount; ++i)
                {
                    AutoMagicalSync_ValueMonitoringSupport_ChangedValue valueChangesSupport = autoSyncCompanion.valuesChangesSupport[i];
                    if (valueChangesSupport.mostRecentChanges != null) // TODO FIXME have to include a check for the IsPositionSyncd and IsRotationSyncd check dealios
                    {
                        if (valueChangesSupport.mostRecentChanges_usedSize < ValueBlendUtils.VALUE_COUNT_NEEDED_TO_EXTRAPOLATE)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// <para>If you on running on the server and need to "assume ownership" over something (e.g., a client instantiated projectile), this is the method to do so.</para>
        /// <para>
        /// If you want the value blending to go smoothly, you can ensure only to call this once <see cref="Server_HasEnoughValueBlendHistoryToSmoothly_AssumeAuthorityOver(GONetParticipant)"/> returns true.
        /// This can still be called and work out just ~fine when that method returns false, but there might be a one-frame warp/teleport from old values to new for the original/previous owner
        /// 
        /// This method will result in the extrapolation of synced GONetAutoMagicalSync values from <paramref name="gonetParticipant"/> that employ value blending techniques on the client side.
        /// This implies that no lead time buffer will be utilized within the value blending technique causing the best effort to match the value on the server/owner machine at the current time.
        /// </para>
        /// <para>POST: *if* this method returns true, the value of <paramref name="gonetParticipant"/>'s <see cref="GONetParticipant.OwnerAuthorityId"/> will be changed to <see cref="MyAuthorityId"/> AND a new value for <see cref="GONetParticipant.gonetId_raw"/> will be assigned.</para>
        /// </summary>
        public static bool Server_AssumeAuthorityOver(GONetParticipant gonetParticipant)
        {
            //GONetLog.Debug("Server assuming authority over GNP.  Is Mine Already (i.e., client used server assigned GONetIdRaw batch)? " + IsMine(gonetParticipant));
            // TODO need to implement the logic for automatically getting the initiating client a new batch if it is running low

            if (IsServer && gonetParticipant.OwnerAuthorityId != OwnerAuthorityId_Unset && !IsMine(gonetParticipant))
            {
                Server_AssumeAuthorityOver_MakeCurrentAndStopValueBlending(gonetParticipant);

                gonetParticipant.OwnerAuthorityId = MyAuthorityId; // NOTE: this will propagate to all other parties through auto sync support

                // GONet v2 Fix: Only assign raw ID if not already set.
                // Client-spawned server-owned objects use batch-allocated IDs that are already valid.
                // Reassigning would cause GONetId mismatch between client's SoA lookup and server's sync data,
                // resulting in stuck objects that never receive network updates.
                // The original comment about "only valid for previous owner" predates the batch system -
                // batch IDs are server-allocated and valid for server use.
                if (gonetParticipant.gonetId_raw == GONetParticipant.GONetIdRaw_Unset)
                {
                    AssignGONetIdRaw_IfAppropriate(gonetParticipant, true);
                }

                OnGONetIdComponentChanged_EnsureMapKeysUpdated(gonetParticipant, gonetParticipant.GONetId); // NOTE: yes, this will also be handled via subscribers to SyncEvent_GONetParticipant_GONetId AND SyncEvent_GONetParticipant_OwnerAuthorityId, but it is best to do it immediately here since we already know it changed and those events are fired at end of frame!

                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Helper method to <see cref="Server_AssumeAuthorityOver(GONetParticipant)"/>.
        /// Clear out all value blending data/support from previous owner since I/server will now be the owner and having this value blending data around could be problematic:
        /// </summary>
        private static void Server_AssumeAuthorityOver_MakeCurrentAndStopValueBlending(GONetParticipant gonetParticipant)
        {
            Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> autoSyncCompanions;
            GONetParticipant_AutoMagicalSyncCompanion_Generated autoSyncCompanion;
            if (activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(gonetParticipant.CodeGenerationId, out autoSyncCompanions) &&
                autoSyncCompanions.TryGetValue(gonetParticipant, out autoSyncCompanion))
            {
                byte valuesCount = autoSyncCompanion.valuesCount;
                for (int i = 0; i < valuesCount; ++i)
                {
                    AutoMagicalSync_ValueMonitoringSupport_ChangedValue valueChangesSupport = autoSyncCompanion.valuesChangesSupport[i];
                    if (valueChangesSupport.mostRecentChanges != null)
                    {
                        GONetSyncableValue valueBefore = valueChangesSupport.syncCompanion.GetAutoMagicalSyncValue((byte)i);

                        /*This happens every time on at least one property/index...so it seems spammy:
                        if (valueChangesSupport.mostRecentChanges_usedSize < ValueBlendUtils.VALUE_COUNT_NEEDED_TO_EXTRAPOLATE)
                        {
                            const string NO_EXTRAP = "While transferring ownership to server, there is not enough information for ApplyValueBlending_IfAppropriate to extrapolate to right now right now, because it would seem highly prefferable to be able to extrapolate to now instead of staying at the value we had from back at negative GONetMain.valueBlendingBufferLeadTicks ago.  GONetId: ";
                            const string IDX = "  Value index: ";
                            GONetLog.Warning(string.Concat(NO_EXTRAP, gonetParticipant.GONetId, IDX, i)); // TODO printing out the index is not useful!  print a name of property or something!!!
                        }
                        */
                        valueChangesSupport.ApplyValueBlending_IfAppropriate(0); // make sure we update it to the latest value for right now right now (i.e., pass 0 instead of GONetMain.valueBlendingBufferLeadTicks) before we transfer ownership
                        valueChangesSupport.ClearMostRecentChanges(); // most recent changes is only useful for value blending...and since we are now the owner (or will be soon below), no sense in keeping this around

                        GONetSyncableValue valueAfter = valueChangesSupport.syncCompanion.GetAutoMagicalSyncValue((byte)i);
                        valueChangesSupport.lastKnownValue = valueChangesSupport.lastKnownValue_previous = valueAfter; // IMPORTANT: now that we are taking over ownership (below), we need to keep tabs on when changes occur and this is first step to baseline things from this point forward
                    }
                }
            }
            else
            {
                const string TRANS = "Transferring ownership to server and expecting to find an active auto sync support/companion instance, but did not.  NOTE: The transfer will still occur.  GONetId: ";
                GONetLog.Warning(string.Concat(TRANS, gonetParticipant.GONetId));
            }
        }

        /// <summary>
        /// Resets blend buffers for a GONetParticipant during ownership migration (host failover).
        /// This is similar to <see cref="Server_AssumeAuthorityOver_MakeCurrentAndStopValueBlending"/> but:
        /// - Does NOT change OwnerAuthorityId (already correct - still 1023 for server-owned objects)
        /// - Does NOT assign new GONetId (already correct)
        /// - ONLY resets blend buffers so new host can start broadcasting sync data
        ///
        /// Called during <see cref="GONetHostFailoverManager.CompleteSelfPromotion"/> for each
        /// server-owned GNP to prepare it for sync from the new host.
        /// </summary>
        internal static void ResetBlendBuffersForOwnershipMigration(GONetParticipant gnp, bool applyCurrentValueBeforeReset = true)
        {
            Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> autoSyncCompanions;
            GONetParticipant_AutoMagicalSyncCompanion_Generated autoSyncCompanion;

            if (activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(gnp.CodeGenerationId, out autoSyncCompanions) &&
                autoSyncCompanions.TryGetValue(gnp, out autoSyncCompanion))
            {
                byte valuesCount = autoSyncCompanion.valuesCount;
                for (int i = 0; i < valuesCount; ++i)
                {
                    AutoMagicalSync_ValueMonitoringSupport_ChangedValue valueChangesSupport = autoSyncCompanion.valuesChangesSupport[i];
                    if (valueChangesSupport.mostRecentChanges != null)
                    {
                        // Apply current blended value (extrapolate to now with no lead time) when requested.
                        // Handoff clients can skip this to avoid applying stale pre-handoff extrapolation.
                        if (applyCurrentValueBeforeReset)
                        {
                            valueChangesSupport.ApplyValueBlending_IfAppropriate(0);
                        }

                        // Clear the blend buffer - we're now the authority and will broadcast fresh values
                        valueChangesSupport.ClearMostRecentChanges();

                        // Reset baseline to current value - this is the starting point for change detection
                        GONetSyncableValue currentValue = valueChangesSupport.syncCompanion.GetAutoMagicalSyncValue((byte)i);
                        valueChangesSupport.lastKnownValue = valueChangesSupport.lastKnownValue_previous = currentValue;
                    }
                }

                // CRITICAL FIX (Dec 2025): Reset "at rest" tracking bits.
                // After failover, values may be stuck in NEEDS_TO_BROADCAST state from the old authority.
                // If we don't reset these, when values change the system will log 10,000+ warnings:
                // "Value was 'At Rest' but it was not broadcasted!"
                // This causes massive performance degradation (2 FPS) and prevents proper sync.
                autoSyncCompanion.ResetValueAtRestBitsForOwnershipMigration();

                //GONetLog.Debug($"[Failover] Reset blend buffers and at-rest bits for GNP '{gnp.name}' (GONetId: {gnp.GONetId})");
            }
            else
            {
                GONetLog.Warning($"[Failover] Could not find auto sync companion for GNP '{gnp.name}' (GONetId: {gnp.GONetId}, CodeGenId: {gnp.CodeGenerationId}) - blend buffers not reset");
            }
        }

        /// <summary>
        /// CRITICAL FIX (Dec 2025): During voluntary handoff, server-owned objects (OwnerAuthorityId=1023)
        /// don't change ownership, so MigratePromotingClientOwnedObjectsToServer doesn't process them.
        /// However, their at-rest tracking bits are still stuck in NEEDS_TO_BROADCAST state from the old host.
        /// This causes 10,000+ warnings "Value was 'At Rest' but it was not broadcasted!" every frame,
        /// resulting in massive performance degradation (2 FPS).
        ///
        /// This method resets blend buffers and at-rest bits for ALL server-owned objects so the new host
        /// can start fresh without inheriting stale at-rest state from the old authority.
        /// </summary>
        /// <returns>Number of objects reset.</returns>
        internal static int ResetBlendBuffersForAllServerOwnedObjects(bool applyCurrentValueBeforeReset = true)
        {
            int resetCount = 0;
            foreach (var kvp in gonetParticipantByGONetIdMap)
            {
                GONetParticipant gnp = kvp.Value;
                if (gnp == null) continue;

                // Only reset server-owned objects - client-owned objects are handled by MigratePromotingClientOwnedObjectsToServer
                if (gnp.OwnerAuthorityId != OwnerAuthorityId_Server) continue;

                ResetBlendBuffersForOwnershipMigration(gnp, applyCurrentValueBeforeReset);
                resetCount++;
            }

            if (resetCount > 0)
            {
                GONetLog.Info($"[Handoff] Reset blend buffers for {resetCount} server-owned object(s) (applyCurrentValue={applyCurrentValueBeforeReset}) - prevents at-rest log spam");
            }

            return resetCount;
        }

        /// <summary>
        /// CRITICAL FIX (Dec 2025): When the host demotes to client, its blend buffers still contain
        /// outgoing sync data it was sending. These need to be reset so it can properly receive
        /// sync data from the new host. Without this, the demoted host may fail to apply incoming
        /// value changes because it thinks it's still the authority.
        ///
        /// This also resets physics settings so demoted objects become kinematic (non-simulating).
        /// </summary>
        /// <returns>Number of objects reset.</returns>
        internal static int ResetBlendBuffersForDemotedHost()
        {
            int resetCount = 0;
            foreach (var kvp in gonetParticipantByGONetIdMap)
            {
                GONetParticipant gnp = kvp.Value;
                if (gnp == null) continue;

                // Reset ALL GNPs, not just server-owned, because the demoted host needs to receive sync for everything
                ResetBlendBuffersForOwnershipMigration(gnp);

                // Also update physics settings - demoted host no longer simulates physics
                gnp.SetRigidBodySettingsConsideringOwner();

                resetCount++;
            }

            // CRITICAL FIX (Dec 2025): Also reset SoA history for all registered objects.
            // The SoA blend buffers contain stale data from when this host was the authority.
            // Without resetting, incoming sync will be blended with stale history, causing
            // objects to appear at crazy positions.
            int soaResetCount = GONet.Core.SoA_StreamRegistry.ResetAllHistoryForDemotion(valueBlendingBufferLeadTicks);

            if (resetCount > 0)
            {
                GONetLog.Info($"[Handoff] Reset blend buffers for {resetCount} object(s) on demoted host (SoA values: {soaResetCount}) - ready to receive sync");
            }

            return resetCount;
        }

        /// <summary>
        /// CRITICAL FIX (Dec 2025): Clears pending "at rest needs to broadcast" bits for all objects.
        /// Called when a client disconnects from the server.
        ///
        /// When values go to "at rest", they're marked as LAST_KNOWN_VALUE_IS_AT_REST_NEEDS_TO_BROADCAST.
        /// If a client disconnects before receiving this broadcast, these bits are never cleared.
        /// When the value changes again, this triggers massive log spam:
        /// "Value was 'At Rest' but it was not broadcasted!"
        ///
        /// Clearing these bits on disconnect prevents the warning spam and associated FPS degradation.
        /// The worst case is skipping a final "at rest" broadcast, which is acceptable since the
        /// client is disconnecting anyway.
        /// </summary>
        internal static void ClearPendingAtRestBroadcastsForAllObjects()
        {
            int clearedCount = 0;
            foreach (var kvp in gonetParticipantByGONetIdMap)
            {
                GONetParticipant gnp = kvp.Value;
                if (gnp == null) continue;

                Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> autoSyncCompanions;
                GONetParticipant_AutoMagicalSyncCompanion_Generated autoSyncCompanion;
                if (activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(gnp.CodeGenerationId, out autoSyncCompanions) &&
                    autoSyncCompanions.TryGetValue(gnp, out autoSyncCompanion))
                {
                    autoSyncCompanion.ResetValueAtRestBitsForOwnershipMigration();
                    clearedCount++;
                }
            }

            if (clearedCount > 0)
            {
                GONetLog.Debug($"[ClientDisconnect] Cleared pending at-rest broadcasts for {clearedCount} object(s) - prevents warning spam");
            }
        }

        private static void OnOwnerAuthorityIdChanged(GONetEventEnvelope<SyncEvent_ValueChangeProcessed> eventEnvelope)
        {
            ////GONetLog.Debug("DREETS pork");

            GONetParticipant gonetParticipant = eventEnvelope.GONetParticipant;
            SyncEvent_ValueChangeProcessed @event = eventEnvelope.Event;
            OnGONetIdComponentChanged_EnsureMapKeysUpdated(gonetParticipant, @event.ValuePrevious.System_UInt16);

            if ((object)gonetParticipant != null && gonetParticipant.gonetId_raw != GONetParticipant.GONetId_Unset)
            {
                gonetParticipant.SetRigidBodySettingsConsideringOwner();

                // GONet v2 SoA: Register in SoA when we become non-authority
                // This handles cases where:
                // 1. Client spawns with Client_InstantiateToBeRemotelyControlledByMe (IsMine=false from start)
                // 2. Authority transfers away from us (IsMine was true, now false)
                // In both cases, if we're not registered yet, register now
                if (!gonetParticipant.IsMine && !gonetParticipant.v2_isRegisteredInSoA)
                {
                    RegisterObjectInSoA(gonetParticipant);
                }
            }
            else
            {
                const string EXP = "Expecting to receive a non-null GNP, but it is null.";
                GONetLog.Warning(EXP);
            }

            using (var en = allGONetBehaviours.GetEnumerator())
            {
                while (en.MoveNext())
                {
                    GONetBehaviour gnBehaviour = en.Current;
                    gnBehaviour.OnGONetParticipant_OwnerAuthorityIdChanged(
                        gonetParticipant,
                        @event.GONetId,
                        @event.ValuePrevious.System_UInt16,
                        @event.ValueNew.System_UInt16);
                }
            }
        }

        /// <summary>
        /// DEBUG: Handler for Transform.position sync events to trace what's actually being synced
        /// </summary>
        private static void OnTransformPositionChanged_Debug(GONetEventEnvelope<SyncEvent_ValueChangeProcessed> eventEnvelope)
        {
            GONetParticipant gnp = eventEnvelope.GONetParticipant;
            SyncEvent_ValueChangeProcessed @event = eventEnvelope.Event;

            string machineName = IsServer ? "Server" : $"Client:{MyAuthorityId}";
            string valuePrev = @event.ValuePrevious.UnityEngine_Vector3.ToString("F3");
            string valueNew = @event.ValueNew.UnityEngine_Vector3.ToString("F3");

            GONetLog.Info($"[{machineName}] [SYNC-DEBUG] Transform.position changed - GONetId: {gnp.GONetId}, Name: '{gnp.name}', IsMine: {gnp.IsMine}, Owner: {gnp.OwnerAuthorityId}, Prev: {valuePrev}, New: {valueNew}, IsRemote: {eventEnvelope.IsSourceRemote}");
        }

        /// <summary>
        /// DEBUG: Handler for Transform.rotation sync events to trace what's actually being synced
        /// </summary>
        private static void OnTransformRotationChanged_Debug(GONetEventEnvelope<SyncEvent_ValueChangeProcessed> eventEnvelope)
        {
            GONetParticipant gnp = eventEnvelope.GONetParticipant;
            SyncEvent_ValueChangeProcessed @event = eventEnvelope.Event;

            string machineName = IsServer ? "Server" : $"Client:{MyAuthorityId}";
            UnityEngine.Quaternion prevQuat = @event.ValuePrevious.UnityEngine_Quaternion;
            UnityEngine.Quaternion newQuat = @event.ValueNew.UnityEngine_Quaternion;
            string valuePrev = $"({prevQuat.x:F3}, {prevQuat.y:F3}, {prevQuat.z:F3}, {prevQuat.w:F3})";
            string valueNew = $"({newQuat.x:F3}, {newQuat.y:F3}, {newQuat.z:F3}, {newQuat.w:F3})";

            GONetLog.Info($"[{machineName}] [SYNC-DEBUG] Transform.rotation changed - GONetId: {gnp.GONetId}, Name: '{gnp.name}', IsMine: {gnp.IsMine}, Owner: {gnp.OwnerAuthorityId}, Prev: {valuePrev}, New: {valueNew}, IsRemote: {eventEnvelope.IsSourceRemote}");
        }

        internal static void OnGONetIdAboutToBeSet(uint gonetId_new, uint gonetId_raw_new, ushort ownerAuthorityId_new, GONetParticipant gonetParticipant)
        {
            if (gonetId_new == gonetParticipant.GONetIdAtInstantiation)
            {
                gonetParticipantByGONetIdAtInstantiationMap[gonetParticipant.GONetIdAtInstantiation] = gonetParticipant;
                gonetParticipantByGONetIdMap[gonetId_new] = gonetParticipant;

                // DIAGNOSTIC: Log participant registration (helps diagnose spawn propagation issues)
                //GONetLog.Debug($"[PARTICIPANT-REGISTERED] '{gonetParticipant.name}' " +
                              //$"GONetId: {gonetId_new}, " +
                              //$"InstantiationId: {gonetParticipant.GONetIdAtInstantiation}, " +
                              //$"IdsMatch: true, " +
                              //$"TotalInGONetIdMap: {gonetParticipantByGONetIdMap.Count}, " +
                              //$"TotalInInstantiationMap: {gonetParticipantByGONetIdAtInstantiationMap.Count}");

                // Deferred RPC system will automatically retry via ProcessDeferredRpcs() running every frame

                // Process any pending reparent events waiting for this participant
                OnParticipantRegistered_ProcessPendingReparents(gonetId_new);
            }
            else
            {
                ushort ownerAuthorityId_asRepresentedInside_gonetIdAtInstantiation = (ushort)((gonetParticipant.GONetIdAtInstantiation << GONetParticipant.GONET_ID_BIT_COUNT_USED) >> GONetParticipant.GONET_ID_BIT_COUNT_USED);
                uint gonetId_raw_asRepresentedInside_gonetIdAtInstantiation = gonetParticipant.GONetIdAtInstantiation >> GONetParticipant.GONET_ID_BIT_COUNT_UNUSED;

                bool areAllComponentsChanging =
                    ownerAuthorityId_asRepresentedInside_gonetIdAtInstantiation != ownerAuthorityId_new &&
                    gonetId_raw_asRepresentedInside_gonetIdAtInstantiation != gonetId_raw_new;

                // Always update map keys when GONetId changes (owner-only or raw-only changes included).
                // Relying on SyncEvent_GONetParticipant_GONetId can leave maps stale on clients, which
                // breaks SoA apply lookups after handoff and causes stuck objects.
                gonetParticipantByGONetIdAtInstantiationMap[gonetParticipant.GONetIdAtInstantiation] = gonetParticipant;

                gonetParticipantByGONetIdMap.Remove(gonetParticipant.GONetIdAtInstantiation);
                gonetParticipantByGONetIdMap[gonetId_new] = gonetParticipant; // TODO first check for collision/overwrite and throw exception....or warning at least!

                if (areAllComponentsChanging)
                {
                    // DIAGNOSTIC: Log participant GONetId change (helps diagnose spawn propagation issues)
                    GONetLog.Debug($"[PARTICIPANT-REGISTERED] '{gonetParticipant.name}' GONetId CHANGED - " +
                                  $"NewGONetId: {gonetId_new}, " +
                                  $"InstantiationId: {gonetParticipant.GONetIdAtInstantiation}, " +
                                  $"OldGONetId: {gonetParticipant.GONetIdAtInstantiation}, " +
                                  $"TotalInGONetIdMap: {gonetParticipantByGONetIdMap.Count}, " +
                                  $"TotalInInstantiationMap: {gonetParticipantByGONetIdAtInstantiationMap.Count}");
                }

                // Deferred RPC system will automatically retry via ProcessDeferredRpcs() running every frame

                // Process any pending reparent events waiting for this participant
                OnParticipantRegistered_ProcessPendingReparents(gonetId_new);
            }
        }

        private static void OnGONetIdChanged(GONetEventEnvelope<SyncEvent_ValueChangeProcessed> eventEnvelope)
        {
            ////GONetLog.Debug("DREETS pork");

            OnGONetIdComponentChanged_EnsureMapKeysUpdated(eventEnvelope.GONetParticipant, eventEnvelope.Event.ValuePrevious.System_UInt32);

            OnGONetIdChanged_UpdatePersistentInstantiationEvents(eventEnvelope);

            // BUG FIX (Jan 2026): Also update ReparentGONetParticipantEvent GONetIds when GONetId changes.
            // Without this, late-joiners receive reparent events with the OLD GONetId (e.g., batch-allocated 4095)
            // but the InstantiateEvent has been updated to the NEW GONetId (e.g., server-assigned 23551).
            // This mismatch causes reparent events to queue waiting for a GONetId that will never be registered,
            // resulting in 30-second timeouts and children having incorrect positions.
            OnGONetIdChanged_UpdatePersistentReparentEvents(eventEnvelope);

            // GONet v2 SoA Fix: Update SoA lookup dictionaries when GONetId changes.
            // Without this, client-spawned server-owned objects get stuck because:
            // 1. Client registers in SoA with batch-allocated GONetId (e.g., 4095)
            // 2. Server reassigns GONetId raw via Server_AssumeAuthorityOver
            // 3. Network data arrives with NEW GONetId, but SoA lookup only has OLD GONetId
            // 4. Lookup fails silently, object stays stuck with only seed values
            OnGONetIdChanged_UpdateSoALookups(eventEnvelope);
        }

        private unsafe static void OnGONetIdChanged_UpdatePersistentInstantiationEvents(GONetEventEnvelope<SyncEvent_ValueChangeProcessed> eventEnvelope)
        {
            LinkedListNode<IPersistentEvent> current = persistentEventsThisSession.First;

            while (current != null)
            {
                var persistentEvent = current.Value;
                if (persistentEvent is InstantiateGONetParticipantEvent)
                {
                    InstantiateGONetParticipantEvent instantiationEvent = (InstantiateGONetParticipantEvent)persistentEvent;
                    SyncEvent_ValueChangeProcessed newGONetIdEvent = eventEnvelope.Event;
                    if (instantiationEvent.GONetId == newGONetIdEvent.ValuePrevious.System_UInt32)
                    {
                        instantiationEvent.GONetId = newGONetIdEvent.ValueNew.System_UInt32;

                        // this is a struct and the copy over of the value is not going to stick inside the persistentEventsThisSession...so we do linked list stuffities to replace old

                        persistentEventsThisSession.AddBefore(current, instantiationEvent);
                        persistentEventsThisSession.Remove(current);

                        break;
                    }
                }

                current = current.Next;
            }
        }

        /// <summary>
        /// BUG FIX (Jan 2026): Update ReparentGONetParticipantEvent GONetIds when any GONetId changes.
        /// This fixes the late-joiner reparent bug where:
        /// 1. Object spawns with batch-allocated GONetId (e.g., 4095)
        /// 2. Object is reparented, ReparentEvent created with GONetId 4095
        /// 3. Server takes authority, GONetId changes to 23551
        /// 4. InstantiateEvent is updated to GONetId 23551
        /// 5. But ReparentEvent still has GONetId 4095!
        /// 6. Late-joiner: InstantiateEvent creates object with GONetId 23551
        /// 7. Late-joiner: ReparentEvent can't find GONetId 4095 → queued → timeout!
        ///
        /// This method updates all ReparentEvents that reference the changed GONetId:
        /// - GONetId (target object being reparented)
        /// - OriginalParentGONetId (original parent reference)
        /// - NewParentGONetId (new parent reference)
        /// </summary>
        private static void OnGONetIdChanged_UpdatePersistentReparentEvents(GONetEventEnvelope<SyncEvent_ValueChangeProcessed> eventEnvelope)
        {
            uint previousGONetId = eventEnvelope.Event.ValuePrevious.System_UInt32;
            uint newGONetId = eventEnvelope.Event.ValueNew.System_UInt32;

            if (previousGONetId == newGONetId || previousGONetId == GONetParticipant.GONetId_Unset)
                return;

            LinkedListNode<IPersistentEvent> current = persistentEventsThisSession.First;
            int updatedCount = 0;

            while (current != null)
            {
                var nextNode = current.Next; // Save next before potential modification

                if (current.Value is ReparentGONetParticipantEvent reparentEvent)
                {
                    bool wasUpdated = false;

                    // Update target GONetId
                    if (reparentEvent.GONetId == previousGONetId)
                    {
                        reparentEvent.GONetId = newGONetId;
                        wasUpdated = true;
                    }

                    // Update original parent GONetId
                    if (reparentEvent.OriginalParentGONetId == previousGONetId)
                    {
                        reparentEvent.OriginalParentGONetId = newGONetId;
                        wasUpdated = true;
                    }

                    // Update new parent GONetId
                    if (reparentEvent.NewParentGONetId == previousGONetId)
                    {
                        reparentEvent.NewParentGONetId = newGONetId;
                        wasUpdated = true;
                    }

                    if (wasUpdated)
                    {
                        updatedCount++;
                        // ReparentGONetParticipantEvent is a class, so changes persist directly
                        // No need to replace node in linked list (unlike struct InstantiateGONetParticipantEvent)

                        GONetLog.Debug($"[REPARENT-GONETID-FIX] Updated ReparentEvent GONetId(s) from {previousGONetId} to {newGONetId}");
                    }
                }

                current = nextNode;
            }

            if (updatedCount > 0)
            {
                GONetLog.Debug($"[REPARENT-GONETID-FIX] Updated {updatedCount} ReparentEvent(s) when GONetId changed from {previousGONetId} to {newGONetId}");
            }
        }

        /// <summary>
        /// GONet v2 SoA Fix: Update SoA lookup dictionaries when GONetId changes.
        /// This is critical for client-spawned server-owned objects:
        /// 1. Client allocates batch GONetId and registers in SoA with that ID
        /// 2. Server takes authority and reassigns GONetId raw (via Server_AssumeAuthorityOver)
        /// 3. Network data arrives with NEW GONetId - without this fix, lookup fails silently
        /// 4. This method re-keys the lookup dictionaries and updates stream.gonetIds arrays
        /// </summary>
        private static void OnGONetIdChanged_UpdateSoALookups(GONetEventEnvelope<SyncEvent_ValueChangeProcessed> eventEnvelope)
        {
            uint previousGONetId = eventEnvelope.Event.ValuePrevious.System_UInt32;
            uint newGONetId = eventEnvelope.Event.ValueNew.System_UInt32;

            if (previousGONetId == newGONetId || previousGONetId == GONetParticipant.GONetId_Unset)
                return;

            GONetParticipant participant = eventEnvelope.GONetParticipant;
            Transform expectedTransform = null;
            if ((object)participant != null)
            {
                expectedTransform = participant.transform;
            }

            (int streamIndex, int objectIndex) prevPosLookup = default;
            (int streamIndex, int objectIndex) prevRotLookup = default;
            (int streamIndex, int objectIndex) newPosLookup = default;
            (int streamIndex, int objectIndex) newRotLookup = default;

            bool prevPosFound = soaPositionLookup != null && soaPositionLookup.TryGetValue(previousGONetId, out prevPosLookup);
            bool prevRotFound = soaRotationLookup != null && soaRotationLookup.TryGetValue(previousGONetId, out prevRotLookup);
            bool prevTransformLookupFound = prevPosFound || prevRotFound;

            bool newPosFound = soaPositionLookup != null && soaPositionLookup.TryGetValue(newGONetId, out newPosLookup);
            bool newRotFound = soaRotationLookup != null && soaRotationLookup.TryGetValue(newGONetId, out newRotLookup);
            bool newTransformLookupFound = newPosFound || newRotFound;

            bool prevMatches = true;
            bool newMatches = false;
            string mismatchDetails = null;
            string expectedName = expectedTransform != null ? expectedTransform.name : "<null>";

            if (prevPosFound)
            {
                string actualName;
                bool match = TryMatchTransformFromPositionLookup(expectedTransform, prevPosLookup, out actualName);
                prevMatches &= match;
                if (!match)
                {
                    mismatchDetails = string.Concat(mismatchDetails, mismatchDetails == null ? "" : "; ", $"POS prev {prevPosLookup.streamIndex}:{prevPosLookup.objectIndex} expected='{expectedName}' actual='{actualName}'");
                }
            }

            if (prevRotFound)
            {
                string actualName;
                bool match = TryMatchTransformFromRotationLookup(expectedTransform, prevRotLookup, out actualName);
                prevMatches &= match;
                if (!match)
                {
                    mismatchDetails = string.Concat(mismatchDetails, mismatchDetails == null ? "" : "; ", $"ROT prev {prevRotLookup.streamIndex}:{prevRotLookup.objectIndex} expected='{expectedName}' actual='{actualName}'");
                }
            }

            if (newPosFound)
            {
                string actualName;
                if (TryMatchTransformFromPositionLookup(expectedTransform, newPosLookup, out actualName))
                {
                    newMatches = true;
                }
            }

            if (newRotFound)
            {
                string actualName;
                if (TryMatchTransformFromRotationLookup(expectedTransform, newRotLookup, out actualName))
                {
                    newMatches = true;
                }
            }

            if (prevTransformLookupFound && !prevMatches)
            {
                GONetLog.Warning($"[SoA-REKEY] Transform mismatch during rekey {previousGONetId} -> {newGONetId}. {mismatchDetails} - clearing old entries");
                SoA_ClearAllLookupsForGONetId(previousGONetId, "rekey-mismatch", scanAllStreams: true);
                if ((object)participant != null)
                {
                    participant.v2_isRegisteredInSoA = false;
                    if (expectedTransform != null && !participant.IsMine)
                    {
                        RegisterObjectInSoA(participant);
                    }
                }
                return;
            }

            if (prevTransformLookupFound && newMatches)
            {
                GONetLog.Warning($"[SoA-REKEY] GONetId {newGONetId} already mapped to expected transform '{expectedName}'. Clearing old GONetId {previousGONetId} entries.");
                SoA_ClearAllLookupsForGONetId(previousGONetId, "rekey-duplicate", scanAllStreams: true);
                return;
            }

            if (newTransformLookupFound && !newMatches && expectedTransform != null)
            {
                GONetLog.Warning($"[SoA-REKEY] GONetId {newGONetId} already mapped to a different transform (expected '{expectedName}'). Clearing old entries and re-registering.");
                SoA_ClearAllLookupsForGONetId(newGONetId, "rekey-collision", scanAllStreams: true);
                SoA_ClearAllLookupsForGONetId(previousGONetId, "rekey-collision", scanAllStreams: true);
                if ((object)participant != null)
                {
                    participant.v2_isRegisteredInSoA = false;
                    if (!participant.IsMine)
                    {
                        RegisterObjectInSoA(participant);
                    }
                }
                return;
            }

            if (SoAData.IsInitialized)
            {
                Core.SoA_BlendingPipeline.EnsureJobsComplete();
            }

            int updatedCount = 0;

            // Update Position lookup
            if (prevPosFound)
            {
                soaPositionLookup.Remove(previousGONetId);
                soaPositionLookup[newGONetId] = prevPosLookup;

                // Update the gonetIds array in the stream
                if (SoAData.IsInitialized && SoAData.positionStreams != null &&
                    prevPosLookup.streamIndex < SoAData.positionStreams.Length)
                {
                    var stream = SoAData.positionStreams[prevPosLookup.streamIndex];
                    if (stream.gonetIds.IsCreated && prevPosLookup.objectIndex < stream.gonetIds.Length)
                    {
                        stream.gonetIds[prevPosLookup.objectIndex] = newGONetId;
                    }
                }
                updatedCount++;
            }

            // Update Rotation lookup
            if (prevRotFound)
            {
                soaRotationLookup.Remove(previousGONetId);
                soaRotationLookup[newGONetId] = prevRotLookup;

                // Update the gonetIds array in the stream
                if (SoAData.IsInitialized && SoAData.rotationStreams != null &&
                    prevRotLookup.streamIndex < SoAData.rotationStreams.Length)
                {
                    var stream = SoAData.rotationStreams[prevRotLookup.streamIndex];
                    if (stream.gonetIds.IsCreated && prevRotLookup.objectIndex < stream.gonetIds.Length)
                    {
                        stream.gonetIds[prevRotLookup.objectIndex] = newGONetId;
                    }
                }
                updatedCount++;
            }

            // Update Scalar lookup
            if (soaScalarLookup != null && soaScalarLookup.TryGetValue(previousGONetId, out var scalarLookup))
            {
                soaScalarLookup.Remove(previousGONetId);
                soaScalarLookup[newGONetId] = scalarLookup;

                // Update the gonetIds array in the stream
                if (SoAData.IsInitialized && SoAData.scalarStreams != null &&
                    scalarLookup.streamIndex < SoAData.scalarStreams.Length)
                {
                    var stream = SoAData.scalarStreams[scalarLookup.streamIndex];
                    if (stream.gonetIds.IsCreated && scalarLookup.objectIndex < stream.gonetIds.Length)
                    {
                        stream.gonetIds[scalarLookup.objectIndex] = newGONetId;
                    }
                }
                updatedCount++;
            }

            // Update Vector2 lookup (uses composite key with memberIndex)
            if (soaVector2Lookup != null)
            {
                // Find all entries with matching previousGONetId and re-key them
                var keysToUpdate = new System.Collections.Generic.List<(uint gonetId, byte memberIndex)>();
                foreach (var kvp in soaVector2Lookup)
                {
                    if (kvp.Key.gonetId == previousGONetId)
                        keysToUpdate.Add(kvp.Key);
                }
                foreach (var oldKey in keysToUpdate)
                {
                    var lookup = soaVector2Lookup[oldKey];
                    soaVector2Lookup.Remove(oldKey);
                    soaVector2Lookup[(newGONetId, oldKey.memberIndex)] = lookup;
                    updatedCount++;
                }
            }

            // Update Vector4 lookup (uses composite key with memberIndex)
            if (soaVector4Lookup != null)
            {
                // Find all entries with matching previousGONetId and re-key them
                var keysToUpdate = new System.Collections.Generic.List<(uint gonetId, byte memberIndex)>();
                foreach (var kvp in soaVector4Lookup)
                {
                    if (kvp.Key.gonetId == previousGONetId)
                        keysToUpdate.Add(kvp.Key);
                }
                foreach (var oldKey in keysToUpdate)
                {
                    var lookup = soaVector4Lookup[oldKey];
                    soaVector4Lookup.Remove(oldKey);
                    soaVector4Lookup[(newGONetId, oldKey.memberIndex)] = lookup;
                    updatedCount++;
                }
            }

            // Update the unified stream registry (if using unified SoA blending)
            if (GONetFeatureFlags.UseUnifiedSoABlending)
            {
                SoA_StreamRegistry.UpdateGONetId(previousGONetId, newGONetId);
            }

            if (updatedCount > 0)
            {
                GONetLog.Debug($"[SoA-REKEY] Updated {updatedCount} SoA lookup entries: GONetId {previousGONetId} → {newGONetId}");
            }
        }

        private static bool TryMatchTransformFromPositionLookup(Transform expectedTransform, (int streamIndex, int objectIndex) lookup, out string actualName)
        {
            actualName = "<null>";
            if (expectedTransform == null || SoAData.positionStreams == null ||
                lookup.streamIndex < 0 || lookup.streamIndex >= SoAData.positionStreams.Length)
                return false;

            ref var stream = ref SoAData.positionStreams[lookup.streamIndex];
            if (!stream.transformPtrs.IsCreated || lookup.objectIndex < 0 || lookup.objectIndex >= stream.transformPtrs.Length)
                return false;

            return TryMatchTransformFromPtr(expectedTransform, stream.transformPtrs[lookup.objectIndex], out actualName);
        }

        private static bool TryMatchTransformFromRotationLookup(Transform expectedTransform, (int streamIndex, int objectIndex) lookup, out string actualName)
        {
            actualName = "<null>";
            if (expectedTransform == null || SoAData.rotationStreams == null ||
                lookup.streamIndex < 0 || lookup.streamIndex >= SoAData.rotationStreams.Length)
                return false;

            ref var stream = ref SoAData.rotationStreams[lookup.streamIndex];
            if (!stream.transformPtrs.IsCreated || lookup.objectIndex < 0 || lookup.objectIndex >= stream.transformPtrs.Length)
                return false;

            return TryMatchTransformFromPtr(expectedTransform, stream.transformPtrs[lookup.objectIndex], out actualName);
        }

        private static bool TryMatchTransformFromPtr(Transform expectedTransform, IntPtr transformPtr, out string actualName)
        {
            actualName = "<null>";
            if (expectedTransform == null)
                return false;

            if (!TryGetTransformFromPtr(transformPtr, out Transform actualTransform))
                return false;

            actualName = actualTransform != null ? actualTransform.name : "<null>";
            return actualTransform == expectedTransform;
        }

        private static bool TryGetTransformFromPtr(IntPtr transformPtr, out Transform transform)
        {
            transform = null;
            if (transformPtr == IntPtr.Zero)
                return false;

            try
            {
                GCHandle handle = GCHandle.FromIntPtr(transformPtr);
                if (!handle.IsAllocated)
                    return false;

                transform = handle.Target as Transform;
                return transform != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Now that OwnerAuthorityId changed, that means any data structures (namely dictionary) using OwnerAuthorityId =OR= GONetId (since it is a composite value that includes OwnerAuthorityId) as a key, need to be updated!
        /// </summary>
        private static void OnGONetIdComponentChanged_EnsureMapKeysUpdated(GONetParticipant gonetParticipant, uint previousGONetId)
        {
            if ((object)gonetParticipant != null && gonetParticipant.GONetId != GONetParticipant.GONetId_Unset)
            {
                if (gonetParticipant.GONetId == gonetParticipant.GONetIdAtInstantiation)
                {
                    gonetParticipantByGONetIdMap[gonetParticipant.GONetId] = gonetParticipant;

                    // Deferred RPC system will automatically retry via ProcessDeferredRpcs() running every frame
                }
                else
                {
                    ushort ownerAuthorityId_asRepresentedInside_gonetIdAtInstantiation = (ushort)((gonetParticipant.GONetIdAtInstantiation << GONetParticipant.GONET_ID_BIT_COUNT_USED) >> GONetParticipant.GONET_ID_BIT_COUNT_USED);
                    uint gonetId_raw_asRepresentedInside_gonetIdAtInstantiation = gonetParticipant.GONetIdAtInstantiation >> GONetParticipant.GONET_ID_BIT_COUNT_UNUSED;

                    bool areAllComponentsChanging =
                        ownerAuthorityId_asRepresentedInside_gonetIdAtInstantiation != gonetParticipant.OwnerAuthorityId &&
                        gonetId_raw_asRepresentedInside_gonetIdAtInstantiation != gonetParticipant.gonetId_raw;

                    // there is a bug if we put this back in for projectile/gnp being assumed ownership by server in that it never gets placed into gonetParticipantByGONetIdMap with the new gonetId if we keep this: if (areAllComponentsChanging)
                    {
                        gonetParticipantByGONetIdAtInstantiationMap[gonetParticipant.GONetIdAtInstantiation] = gonetParticipant;

                        gonetParticipantByGONetIdMap.Remove(gonetParticipant.GONetIdAtInstantiation);
                        gonetParticipantByGONetIdMap[gonetParticipant.GONetId] = gonetParticipant; // TODO first check for collision/overwrite and throw exception....or warning at least!

                        // Deferred RPC system will automatically retry via ProcessDeferredRpcs() running every frame
                    }
                }
            }
            else
            {
                const string EXP = "Expecting to receive a non-null GNP for ensuring map keys updated, but it is null.  Proper maintenance is likely not happening as a result.  All we have is previousGONetId: ";
                GONetLog.Warning(string.Concat(EXP, previousGONetId, " reference null? ", (object)gonetParticipant == null));
            }

            // well, looks like at time of writing there are no other ones to consider.....ok...we will monitor and hopefully keep this in mind if we add other Dictionary<uint, blah> later!
        }

        /// <summary>
        /// NOTE: The time maintained within is only updated once per main thread frame tick (i.e., call to <see cref="Update"/>).
        /// </summary>
        internal static readonly SecretaryOfTemporalAffairs Time = new();

        /// <summary>
        /// This is used to know which instances were instantiated due to a remote spawn message being received/processed.
        /// See <see cref="Instantiate_Remote(InstantiateGONetParticipantEvent)"/> and <see cref="Start_AutoPropagateInstantiation_IfAppropriate(GONetParticipant)"/>.
        /// </summary>
        static readonly List<GONetParticipant> remoteSpawns_avoidAutoPropagateSupport = new List<GONetParticipant>(1000);

        static GONetMain()
        {
            //Application.ThreadException += new ThreadExceptionEventHandler(Application_ThreadException);
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            // NOTE: GONetThreading initializes itself automatically via [RuntimeInitializeOnLoadMethod]
            // to avoid Unity serialization phase issues with calling Unity APIs

            GONetGlobal.ActualServerConnectionInfoSet += OnActualServerConnectionInfoSet_UpdateIsServerOverride;

            // Reliable transport logging (sequence numbers, ACKs, retransmissions)
            // Always enable LogCallback so diagnostic logging can work
            //ReliableNetcode.ReliableMessageChannel.LogCallback = (msg) => GONetLog.Debug(msg); // COMMENTED - spam (~1.2K logs/run)

#if GONet_RELIABLE_TRACE
            // Also enable via define symbol for backwards compatibility
            ReliableNetcode.ReliableMessageChannel.EnableDetailedReliableLogging = true;
#endif

            InitMessageTypeToMessageIDMap();
            InitShouldSkipSyncSupport();
        }

        private static void OnActualServerConnectionInfoSet_UpdateIsServerOverride(string serverIP, int serverPort)
        {
            GONetLog.Debug($"Server override set to: {isServerOverride}, args: [{serverIP}]:{serverPort}, ServerIPAddress_Actual: {GONetGlobal.ServerIPAddress_Actual}, ServerPort_Actual: {GONetGlobal.ServerPort_Actual}, p2p: {GONetGlobal.ServerP2pEndPoint}");
        }

        private static void InitEventSubscriptions()
        {
            if (areEventSubscriptionsInitialized)
            {
                return;
            }

            EventBus.Subscribe<IGONetEvent>(OnAnyEvent_RelayToRemoteConnections_IfAppropriate);
            // HOST MODE FIX: Only the SERVER should track persistent events for late-joiner synchronization.
            // The filter ensures:
            // 1. IsServer must be true (only servers need to track events for late-joiners)
            // 2. IsSourceRemote must be false OR the event originates from a client (for client-spawned objects)
            // This prevents clients (including HOST's client side) from adding duplicate entries.
            EventBus.Subscribe<IPersistentEvent>(OnPersistentEvent_KeepTrack, envelope => IsServer);
            EventBus.Subscribe<PersistentEvents_Bundle>(OnPersistentEventsBundle_ProcessAll_Remote, envelope => envelope.IsSourceRemote);
            EventBus.Subscribe<InstantiateGONetParticipantEvent>(OnInstantiationEvent_Remote, envelope => envelope.IsSourceRemote);

            EventBus.Subscribe<GONetParticipantEnabledEvent>(OnEnabledGNPEvent);
            EventBus.Subscribe<GONetParticipantStartedEvent>(OnStartedGNPEvent);
            EventBus.Subscribe<GONetParticipantDeserializeInitAllCompletedEvent>(OnDeserializeInitAllCompletedGNPEvent);
            EventBus.Subscribe<GONetParticipantDisabledEvent>(OnDisabledGNPEvent);

            var despawnSubscription = EventBus.Subscribe<DespawnGONetParticipantEvent>(OnDespawnGNPEvent_Remote, envelope => envelope.IsSourceRemote);
            despawnSubscription.SetSubscriptionPriority_INTERNAL(int.MinValue); // process internally LAST since the GO will be destroyed and other subscribers may want to do something just prior to it being destroyed

            EventBus.SubscribeAnySyncEvents(OnSyncValueChangeProcessed_Persist_Local);

            EventBus.Subscribe(SyncEvent_GeneratedTypes.SyncEvent_GONetParticipant_GONetId, OnGONetIdChanged);
            EventBus.Subscribe(SyncEvent_GeneratedTypes.SyncEvent_GONetParticipant_OwnerAuthorityId, OnOwnerAuthorityIdChanged);

            // DEBUG: Subscribe to position/rotation sync events to trace what's actually being synced
            // NOTE: This logs EVERY transform sync (hundreds per second!) - only enable for debugging
            // To enable, add LOG_SYNC_VERBOSE to Player Settings → Scripting Define Symbols
            #if LOG_SYNC_VERBOSE
            // DEBUG LOGGING DISABLED - uncomment to trace every position/rotation sync event
            // EventBus.Subscribe(SyncEvent_GeneratedTypes.SyncEvent_Transform_position, OnTransformPositionChanged_Debug);
            // EventBus.Subscribe(SyncEvent_GeneratedTypes.SyncEvent_Transform_rotation, OnTransformRotationChanged_Debug);
            #endif

            EventBus.Subscribe<ValueMonitoringSupport_NewBaselineEvent>(OnNewBaselineValue_Remote, envelope => envelope.IsSourceRemote);

            EventBus.Subscribe<ClientRemotelyControlledGONetIdServerBatchAssignmentEvent>(Client_AssignNewClientGONetIdRawBatch);
            EventBus.Subscribe<ClientRemotelyControlledGONetIdServerBatchRequestEvent>(Server_HandleClientBatchRequest);

            // Pooling events
            EventBus.Subscribe<PoolInitializationEvent>(GONetPoolManager.OnPoolInitializationEvent);
            EventBus.Subscribe<PoolGrowthEvent>(GONetPoolManager.OnPoolGrowthEvent);
            EventBus.Subscribe<PoolObjectBorrowEvent>(GONetPoolManager.OnPoolBorrowEvent);
            EventBus.Subscribe<PoolObjectReturnEvent>(GONetPoolManager.OnPoolReturnEvent);
            EventBus.Subscribe<PoolObjectDestroyedEvent>(GONetPoolManager.OnPoolDestroyedEvent);

            // Subscribe to chunked persistent events for reassembly
            EventBus.Subscribe<PersistentEvents_BundleChunk>(OnPersistentEventsChunkReceived, envelope => envelope.IsSourceRemote);

            // Subscribe to scene load complete events from clients (server-side handler)
            EventBus.Subscribe<SceneLoadCompleteEvent>(Server_OnClientSceneLoadComplete, envelope => envelope.IsSourceRemote);

            // Subscribe to client initialization acknowledgments (server-side handler)
            // Added 2025-11-06 to detect Steamworks reliable message delivery failures
            // See: .claude/STEAMWORKS_INIT_MESSAGE_DELIVERY_SOLUTION.md
            EventBus.Subscribe<ClientInitializationAcknowledgment>(Server_OnClientInitializationAcknowledgment, envelope => envelope.IsSourceRemote);

            EventBus.InitializeRpcSystem();
            areEventSubscriptionsInitialized = true;
        }

        private static void OnNewBaselineValue_Remote(GONetEventEnvelope<ValueMonitoringSupport_NewBaselineEvent> eventEnvelope)
        {
            ValueMonitoringSupport_NewBaselineEvent @event = eventEnvelope.Event;
            ApplyNewBaselineValue_Remote(@event);
        }

        private static void ApplyNewBaselineValue_Remote(ValueMonitoringSupport_NewBaselineEvent @event)
        {
            GONetParticipant gnp;
            if (gonetParticipantByGONetIdMap.TryGetValue(@event.GONetId, out gnp)
                || gonetParticipantByGONetIdAtInstantiationMap.TryGetValue(@event.GONetId, out gnp)
                || gonetParticipantByGONetIdMap.TryGetValue(GetGONetIdAtInstantiation(@event.GONetId), out gnp)) // IMPORTANT: this is here because a newly connecting client will process this new baseline with all the other persistent events, but it is done before the new gonetId assignment is made for gnps that were assumed authority by server and have a new gonetId
            {
                GONetParticipant_AutoMagicalSyncCompanion_Generated syncCompanion = activeAutoSyncCompanionsByCodeGenerationIdMap[gnp.CodeGenerationId][gnp];

                AutoMagicalSync_ValueMonitoringSupport_ChangedValue valueChangeSupport = syncCompanion.valuesChangesSupport[@event.ValueIndex];
                //GONetSyncableValue baselineValue_previous = valueChangeSupport.baselineValue_current;

                if (@event is ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector3) // most common first
                {
                    var newBaselineValue = ((ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector3)@event).NewBaselineValue;

                    valueChangeSupport.baselineValue_current = newBaselineValue;
                }
                else if (@event is ValueMonitoringSupport_NewBaselineEvent_System_Single)
                {
                    valueChangeSupport.baselineValue_current = ((ValueMonitoringSupport_NewBaselineEvent_System_Single)@event).NewBaselineValue;
                }
                else if (@event is ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector2)
                {
                    valueChangeSupport.baselineValue_current = ((ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector2)@event).NewBaselineValue;
                }
                else if (@event is ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector4)
                {
                    valueChangeSupport.baselineValue_current = ((ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Vector4)@event).NewBaselineValue;
                }
                else if (@event is ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Quaternion)
                {
                    valueChangeSupport.baselineValue_current = ((ValueMonitoringSupport_NewBaselineEvent_UnityEngine_Quaternion)@event).NewBaselineValue;
                }
                else
                {
                    const string NEW = "New baseline value is of type not yet accounted for.  type: ";
                    GONetLog.Warning(string.Concat(NEW, @event.GetType().FullName));
                }

                const string APPLIED = "New baseline value applied for type: ";
                const string INDEX = " valueIndex: ";
                //GONetLog.Debug(string.Concat(APPLIED, @event.GetType().FullName, INDEX, @event.ValueIndex));
            }
            else
            {
                // Suppress excessive warnings - only log once per 5 seconds per GONetId
                // This is expected: unreliable sync events arrive after despawn (race condition, not a bug)
                const long SUPPRESSION_WINDOW_TICKS = 5 * TimeSpan.TicksPerSecond;
                long currentTicks = Time.ElapsedTicks;
                long lastWarningTicks;
                bool shouldLog = !missingGONetParticipantWarningSuppressionMap.TryGetValue(@event.GONetId, out lastWarningTicks) ||
                                 (currentTicks - lastWarningTicks) >= SUPPRESSION_WINDOW_TICKS;

                if (shouldLog)
                {
                    const string GNID = "Unable to find GONetParticipant for GONetId: ";
                    const string POSSI = ", which is possibly due to it being destroyed and this event came at a bad time just after destroy processed....like was the case during testing with ProjectileTest.unity";
                    GONetLog.Warning(string.Concat(GNID, @event.GONetId, POSSI));
                    missingGONetParticipantWarningSuppressionMap[@event.GONetId] = currentTicks;
                }
                // else: warning suppressed (already logged recently for this GONetId)
            }
        }

        private static void OnSyncValueChangeProcessed_Persist_Local(GONetEventEnvelope<SyncEvent_ValueChangeProcessed> eventEnvelope)
        {
            ////GONetLog.Debug("DREETS pork");

            OnSyncValueChangeProcessed_Persist_Local(eventEnvelope.Event);
        }

        private static void OnSyncValueChangeProcessed_Persist_Local(SyncEvent_ValueChangeProcessed @event, bool doesRequireCopy = true)
        {
#if !PERF_NO_PROCESS_SYNC_EVENTS
            SyncEvent_ValueChangeProcessed instanceToEnqueue;
            if (doesRequireCopy)
            {
                // IMPORTANT: have to make a copy since these are pooled and we are not using the data immediately and GONet will return the event to the pool after this method exits...we need to keep a copy with good data until later on when we actually save
                SyncEvent_ValueChangeProcessed copy = GONet_SyncEvent_ValueChangeProcessed_Generated_Factory.CreateCopy(@event);
                instanceToEnqueue = copy;
            }
            else
            {
                instanceToEnqueue = @event;
            }

            instanceToEnqueue.ProcessedAtElapsedTicks = Time.ElapsedTicks;

            SyncEventsSaveSupport syncEventsToSaveQueue = syncEventsToSaveQueueByEventType[@event.GetType()];
            syncEventsToSaveQueue.queue_needsSavingASAP.Enqueue(instanceToEnqueue); // NOTE: instanceToEnqueu will get returned to its pool when this queue is processed!
#endif
        }

        private static void OnPersistentEventsBundle_ProcessAll_Remote(GONetEventEnvelope<PersistentEvents_Bundle> eventEnvelope)
        {
            int eventCount = eventEnvelope.Event.PersistentEvents.Count;
            //GONetLog.Warning($"[SPAWN_SYNC] CLIENT: OnPersistentEventsBundle_ProcessAll_Remote - Processing bundle with {eventCount} events from AuthorityId {eventEnvelope.SourceAuthorityId}");

            if (eventCount == 0 && IsClient && _gonetClient != null && !_gonetClient.hasAcknowledgedInitMessages)
            {
                _gonetClient.hasReceivedInitTrackingMarker_EventSingles = true;
                Client_TrySendInitializationAcknowledgment();
                return;
            }

            int sceneLoadCount = 0;
            int spawnCount = 0;
            int otherCount = 0;

            foreach (var item in eventEnvelope.Event.PersistentEvents)
            {
                if (item is SceneLoadEvent sceneLoad)
                {
                    sceneLoadCount++;
                    //GONetLog.Warning($"[SPAWN_SYNC] CLIENT: - Processing SceneLoadEvent: '{sceneLoad.SceneName}', Mode: {sceneLoad.Mode}");
                }
                else if (item is InstantiateGONetParticipantEvent spawn)
                {
                    spawnCount++;
                    //GONetLog.Debug($"[SPAWN_SYNC] CLIENT: - Processing spawn: GONetId {spawn.GONetId}, Scene: '{spawn.SceneIdentifier}'");
                }
                else
                {
                    otherCount++;
                }

                // CLIENTS NEVER ADD TO PERSISTENT EVENTS LIST: Only the SERVER tracks persistent events
                // for late-joiner synchronization. Clients (including host's client side) do NOT add:
                // - Host's client side: Server already has these events (don't duplicate)
                // - Pure clients: They don't serve other clients, so they don't need this list
                // The events are still processed via EventBus.Publish below for local handling.
                // (Removed: persistentEventsThisSession.AddLast(item) - clients never add)

                // Publish the persistent event to the event bus so all registered handlers can process it
                // This replaces the old piecemeal approach and ensures extensibility for new persistent event types
                EventBus.Publish(
                    item,
                    remoteSourceAuthorityId: eventEnvelope.SourceAuthorityId,
                    targetClientAuthorityId: MyAuthorityId, // this is required to ensure my handlers are invoked and that is it
                    shouldPublishReliably: true); // probably redundant as none of this should go back over the wire at all
            }

            GONetLog.Debug($"[SPAWN_SYNC] CLIENT: Bundle processing complete - SceneLoad: {sceneLoadCount}, Spawn: {spawnCount}, Other: {otherCount}");
        }

        /// <summary>
        /// Definition of "if appropriate":
        ///     -The server will always send to remote connections....clients only send to remote connections (i.e., just to server) when locally sourced!
        /// </summary>
        private static void OnAnyEvent_RelayToRemoteConnections_IfAppropriate(GONetEventEnvelope<IGONetEvent> eventEnvelope)
        {
            if (eventEnvelope.Event is ILocalOnlyPublish ||                                            //If this event implements ILocalOnlyPublish means that it will only be published locally and it will not be remotely transmitted.
                (eventEnvelope.IsSingularRecipientOnly && IsServer && eventEnvelope.IsSourceRemote) || //If this event has arrived to the server from a remote source and it does not need to be relayed then do not keep executing this method.
                (IsClient && !IsServer && eventEnvelope.IsSourceRemote))                               //If an event from a remote source (the server) has arrived to a PURE client (not host), it does not need to relay. HOST MODE FIX: Host must relay events from clients to other clients.
            {
                // DIAGNOSTIC: Log if scene event is filtered (expected behavior - clients don't relay server events)
                if (eventEnvelope.Event is SceneLoadEvent || eventEnvelope.Event is SceneUnloadEvent || eventEnvelope.Event is SceneLoadCompleteEvent)
                {
                    string eventType = eventEnvelope.Event.GetType().Name;
                    GONetLog.Debug($"[SCENE-RELAY-FILTERED] {eventType} filtered - LocalOnly={eventEnvelope.Event is ILocalOnlyPublish}, SingularRecip={eventEnvelope.IsSingularRecipientOnly}, IsServer={IsServer}, IsClient={IsClient}, IsSourceRemote={eventEnvelope.IsSourceRemote}");
                }
                return;
            }

            // Check for self-targeting early - don't relay to ourselves
            if (eventEnvelope.TargetClientAuthorityId != OwnerAuthorityId_Unset &&
                eventEnvelope.TargetClientAuthorityId == MyAuthorityId)
            {
                // This event is targeted at ourselves - local handlers have already been processed
                // in GONetEventBus.Publish(), so we don't need to relay it anywhere
                return;
            }

            byte[] bytes = default;
            int returnBytesUsedCount = default;
            bool doesNeedToReturn = default;
            try
            {
                //Get bytes from memory pool
                bytes = SerializationUtils.SerializeToBytes(eventEnvelope.Event, out returnBytesUsedCount, out doesNeedToReturn); // TODO FIXME if the envelope is processed from a remote source, then we SHOULD attach the bytes to it and reuse them!
            }
            catch (Exception e)
            {
                // DIAGNOSTIC: Log serialization failure
                if (eventEnvelope.Event is SceneLoadEvent || eventEnvelope.Event is SceneUnloadEvent)
                {
                    GONetLog.Error($"[SCENE-RELAY-SERIALIZE-FAIL] {eventEnvelope.Event.GetType().Name} failed to serialize: {e.Message}");
                }
                GONetLog.Error(e.ToString());
                return;
            }

            //Decide the Reliability of the event transmission based on the envelope
            GONetChannelId channelId = eventEnvelope.IsReliable ? GONetChannel.EventSingles_Reliable : GONetChannel.EventSingles_Unreliable;

            //If the event was not generated by server and we are the server, we relay it to our connections except the event's remote originator.
            if (IsServer && eventEnvelope.IsSourceRemote)
            {
                SendBytesToRemoteConnectionsExceptSourceRemote(eventEnvelope.SourceAuthorityId, bytes, returnBytesUsedCount, channelId);
            }
            else if (IsServer || !eventEnvelope.IsSourceRemote)
            {
                // HOST MODE FIX (December 2025): HOST should use SERVER broadcast path for local events
                // Previously, HOST took the IsClient path which was designed for pure clients sending to servers.
                // While this technically worked (null connection routes to SendBytesToAllClients), it caused
                // ~78% message loss in the reliable transport layer after ~42 seconds of operation.
                // The fix: Pure clients (IsClient && !IsServer) use client path, HOST uses server path.
                //
                // Pure client: broadcast to "connections" (which is just the server)
                if (IsClient && !IsServer)
                {
                    // DIAGNOSTIC: Log GONetLocal spawn event relay
                    if (GONetConfig.LogSpawnDiagnostics &&
                        eventEnvelope.Event is InstantiateGONetParticipantEvent spawnEvt &&
                        spawnEvt.DesignTimeLocation != null &&
                        spawnEvt.DesignTimeLocation.Contains("GONetLocal"))
                    {
                        GONetLog.Debug($"[SPAWN-DIAG] CLIENT relaying GONetLocal spawn to server - GONetId: {spawnEvt.GONetId}, bytes: {returnBytesUsedCount}, channel: {channelId}");
                    }

                    // DIAGNOSTIC (December 2025): Log spawn event relay for client-spawned server-owned objects
                    // To enable, add GONet_SPAWN_TRACE to Player Settings → Scripting Define Symbols
                    #if GONet_SPAWN_TRACE
                    if (eventEnvelope.Event is InstantiateGONetParticipantEvent spawnEvent && spawnEvent.ImmediatelyRelinquishAuthorityToServer_AndTakeRemoteControlAuthority)
                    {
                        // Extract first 12 bytes as hex for correlation with reliable transport logs
                        string bytesHex = returnBytesUsedCount >= 12
                            ? System.BitConverter.ToString(bytes, 0, 12).Replace("-", "")
                            : System.BitConverter.ToString(bytes, 0, returnBytesUsedCount).Replace("-", "");
                        GONetLog.Debug($"[SPAWN-RELAY] CLIENT relaying spawn event to server: GONetId={spawnEvent.GONetId}, bytes={returnBytesUsedCount}, channel={channelId}, firstBytes={bytesHex}");

                        // Auto-enable reliable transport logging for spawn event tracking
                        // This ensures we can correlate this spawn through the transport layer
                        if (!EnableDetailedReliableTransportLogging)
                        {
                            EnableDetailedReliableTransportLogging = true;
                            GONetLog.Info("[SPAWN-RELAY] Auto-enabled detailed reliable transport logging for spawn tracking");
                        }
                    }
                    #endif

                    // DIAGNOSTIC: Log SceneLoadCompleteEvent relay with full trace
                    if (EnableSceneLoadCompleteTracing && eventEnvelope.Event is SceneLoadCompleteEvent slcEvent)
                    {
                        long traceId = GetNextDiagnosticMessageId();
                        string bytesHex = returnBytesUsedCount >= 16
                            ? System.BitConverter.ToString(bytes, 0, 16).Replace("-", "")
                            : System.BitConverter.ToString(bytes, 0, returnBytesUsedCount).Replace("-", "");
                        GONetLog.Info($"[SLC-TRACE-1] STAGE1_SERIALIZE traceId={traceId} scene='{slcEvent.SceneName}' bytes={returnBytesUsedCount} channel={channelId} hex={bytesHex} auth={MyAuthorityId} connected={GONetClient?.IsConnectedToServer} time={Time.ElapsedSeconds:F3}");
                    }

                    SendBytesToRemoteConnections(bytes, returnBytesUsedCount, channelId);
                }
                else
                {
                    bool shouldBroadcast = eventEnvelope.TargetClientAuthorityId == OwnerAuthorityId_Unset;
                    if (shouldBroadcast)
                    {
                        SendBytesToRemoteConnections(bytes, returnBytesUsedCount, channelId);
                    }
                    else
                    {
                        GONetRemoteClient remoteClient = gonetServer.GetRemoteClientByAuthorityId(eventEnvelope.TargetClientAuthorityId);
                        SendBytesToRemoteConnection(remoteClient.ConnectionToClient, bytes, returnBytesUsedCount, channelId);
                    }
                }
            }

            if (doesNeedToReturn)
            {
                //Return borrowed bytes to memory pool
                SerializationUtils.ReturnByteArray(bytes);
            }
        }

        /// <summary>
        /// Sends an event to specific remote connections without triggering local handlers.
        /// This is used when we need to send the same event to multiple specific targets efficiently.
        /// </summary>
        internal static void Server_SendEventToSpecificRemoteConnections(IGONetEvent @event, ushort[] targetAuthorityIds, int targetCount, bool isReliable)
        {
            if (!IsServer || targetCount == 0)
            {
                return; // Only server can route to specific targets
            }

            // Serialize the event once
            byte[] bytes = default;
            int returnBytesUsedCount = default;
            bool doesNeedToReturn = default;

            try
            {
                bytes = SerializationUtils.SerializeToBytes(@event, out returnBytesUsedCount, out doesNeedToReturn);
            }
            catch (Exception e)
            {
                GONetLog.Error($"Failed to serialize event for multi-target send: {e}");
                return;
            }

            // Determine channel based on reliability
            GONetChannelId channelId = isReliable ? GONetChannel.EventSingles_Reliable : GONetChannel.EventSingles_Unreliable;

            // Send to each target
            for (int i = 0; i < targetCount; i++)
            {
                ushort targetAuthorityId = targetAuthorityIds[i];

                // Skip if targeting self (shouldn't happen but safety check)
                if (targetAuthorityId == MyAuthorityId)
                {
                    GONetLog.Warning($"SendEventToSpecificRemoteConnections called with self as target, skipping");
                    continue;
                }

                // Get the remote client
                if (gonetServer.TryGetRemoteClientByAuthorityId(targetAuthorityId, out GONetRemoteClient remoteClient))
                {
                    SendBytesToRemoteConnection(remoteClient.ConnectionToClient, bytes, returnBytesUsedCount, channelId);
                }
                else
                {
                    GONetLog.Warning($"Target authority {targetAuthorityId} not found in remote clients");
                }
            }

            // Return borrowed bytes
            if (doesNeedToReturn)
            {
                SerializationUtils.ReturnByteArray(bytes);
            }
        }

        private static void SendBytesToRemoteConnectionsExceptSourceRemote(ushort remoteSourceAuthorityId, byte[] bytes, int bytesUsedCount, GONetChannelId channelId)
        {
            GONetConnection_ServerToClient remoteClientConnection = null;
            uint count = _gonetServer.numConnections;

            // PHASE 2 FIX: Round-robin client processing to distribute server-side delay fairly
            // Without this, clients processed later in list experience cumulative processing delay
            // (e.g., Client 1: 10ms RTT, Client 5: 180ms RTT due to 170ms processing delay)
            // Round-robin starting index ensures all clients get "first" position equally over time
            int startIndex = _gonetServer.nextClientProcessingStartIndex;
            if (count > 0)
            {
                _gonetServer.nextClientProcessingStartIndex = (startIndex + 1) % (int)count;
            }

            for (int offset = 0; offset < count; ++offset)
            {
                int i = (startIndex + offset) % (int)count;
                remoteClientConnection = _gonetServer.remoteClients[i].ConnectionToClient;
                if (remoteClientConnection.OwnerAuthorityId != remoteSourceAuthorityId)
                {
                    SendBytesToRemoteConnection(remoteClientConnection, bytes, bytesUsedCount, channelId);
                }
            }
        }

        private static readonly List<IPersistentEvent> persistentEventsCancelledOut = new List<IPersistentEvent>(100);
        // PERF FIX (Dec 2025): Track nodes for O(1) removal instead of O(n) value search
        private static readonly List<LinkedListNode<IPersistentEvent>> persistentEventNodesCancelledOut = new List<LinkedListNode<IPersistentEvent>>(100);

        /// <summary>
        /// Stores persistent events for late-joiner synchronization and record/replay.
        ///
        /// ⚠️  CRITICAL: This method stores events BY REFERENCE for the entire session.
        /// These exact references are later serialized when late-joining clients connect
        /// (see Server_SendClientPersistentEventsSinceStart:4355).
        ///
        /// DESIGN REQUIREMENT:
        /// Event classes MUST NOT use object pooling. If they did:
        ///   eventEnvelope.Event stored here → Event.Return() clears data → Pool reuses object
        ///   → Late-joiner receives corrupted data from stored reference → CATASTROPHIC
        ///
        /// This is WHY PersistentRpcEvent and other IPersistentEvent implementations
        /// do NOT implement ISelfReturnEvent. See GONetRpcs.cs:912 and line 647 above
        /// for detailed rationale.
        /// </summary>
        private static void OnPersistentEvent_KeepTrack(GONetEventEnvelope<IPersistentEvent> eventEnvelope)
        {
            long methodStartTicks = HighResolutionTimeUtils.UtcNowTicks;

            // RECORD AND REPLAY: Always add to complete history archive (never remove)
            // This preserves the full event timeline for future replay functionality
            persistentEventsArchive_CompleteHistory.AddLast(eventEnvelope.Event);

            // DIAGNOSTIC: Track event type with details
            string eventTypeName = eventEnvelope.Event.GetType().Name;
            string eventKey = eventTypeName;

            // Add GONetId/ValueIndex for baseline events to see if same values keep triggering
            if (eventEnvelope.Event is ValueMonitoringSupport_BaselineExpiredEvent expiredEvt)
            {
                eventKey = $"BaselineExpired({expiredEvt.GONetId},{expiredEvt.ValueIndex})";
            }
            else if (eventEnvelope.Event is ValueMonitoringSupport_NewBaselineEvent newBaselineEvt)
            {
                eventKey = $"NewBaseline({newBaselineEvt.GONetId},{newBaselineEvt.ValueIndex})";
            }

            if (persistentEventDiag_eventTypeCountsSinceLastLog.TryGetValue(eventKey, out int typeCount))
            {
                persistentEventDiag_eventTypeCountsSinceLastLog[eventKey] = typeCount + 1;
            }
            else
            {
                persistentEventDiag_eventTypeCountsSinceLastLog[eventKey] = 1;
            }
            persistentEventDiag_eventsProcessedSinceLastLog++;

            // LATE-JOINER SYNC: Manage current state with cancellation logic
            ICancelOutOtherEvents iCancelOthers = eventEnvelope.Event as ICancelOutOtherEvents;
            persistentEventsCancelledOut.Clear();
            persistentEventNodesCancelledOut.Clear(); // PERF FIX: Track nodes for O(1) removal
            int iterationCount = 0;
            bool isPoolReturnEvent = eventEnvelope.Event is PoolObjectReturnEvent;
            if (iCancelOthers != null)
            {
                long cancelCheckStartTicks = HighResolutionTimeUtils.UtcNowTicks;
                // PERF FIX (Dec 2025): Iterate by node so we can do O(1) removal later
                // Old code used GetEnumerator() and then Remove(value) which is O(n) per removal
                var node = persistentEventsThisSession.First;
                while (node != null)
                {
                    iterationCount++;
                    IPersistentEvent consideredEvent = node.Value;
                    if (TypeUtils.IsTypeAInstanceOfAnyTypesB(consideredEvent.GetType(), iCancelOthers.OtherEventTypesCancelledOut) && iCancelOthers.DoesCancelOutOtherEvent(consideredEvent))
                    {
                        persistentEventsCancelledOut.Add(consideredEvent);
                        persistentEventNodesCancelledOut.Add(node); // Track node for O(1) removal
                    }
                    node = node.Next;
                }
                persistentEventDiag_totalCancelCheckTimeTicks += (HighResolutionTimeUtils.UtcNowTicks - cancelCheckStartTicks);
            }
            persistentEventDiag_totalIterationsSinceLastLog += iterationCount;

            int count = persistentEventsCancelledOut.Count;
            if (count == 0)
            {
                if (isPoolReturnEvent)
                {
                    return; // Pool returns are cancel-only and should not be stored
                }

                // CRITICAL DEDUPLICATION: For persistent RPCs, remove any previous RPC with same RpcId+GONetId
                // This prevents duplicate state updates from accumulating (e.g., BroadcastParticipantUpdate called 1000x = 1000 copies!)
                // Only the LATEST state matters for late-joiners, not the entire history
                if (eventEnvelope.Event is PersistentRpcEvent newRpc)
                {
                    // Find and remove any existing RPC with matching RpcId + GONetId
                    var node = persistentEventsThisSession.First;
                    while (node != null)
                    {
                        var nextNode = node.Next; // Save next before potential removal
                        if (node.Value is PersistentRpcEvent existingRpc &&
                            existingRpc.RpcId == newRpc.RpcId &&
                            existingRpc.GONetId == newRpc.GONetId)
                        {
                            persistentEventsThisSession.Remove(node);
                            //GONetLog.Debug($"[RPC_DEDUP] Removed duplicate persistent RPC 0x{existingRpc.RpcId:X8} for GONetId {existingRpc.GONetId} - keeping latest only");
                        }
                        node = nextNode;
                    }
                }

                // HOST MODE SAFEGUARD: Prevent duplicate spawn events from being added.
                // In host mode, various code paths could potentially publish the same spawn event.
                // Check if a spawn event with the same GONetId already exists and skip if so.
                if (eventEnvelope.Event is InstantiateGONetParticipantEvent newSpawn)
                {
                    bool alreadyExists = false;
                    foreach (var existingEvent in persistentEventsThisSession)
                    {
                        if (existingEvent is InstantiateGONetParticipantEvent existingSpawn &&
                            existingSpawn.GONetId == newSpawn.GONetId)
                        {
                            alreadyExists = true;
                            //GONetLog.Debug($"[SPAWN_DEDUP] Skipping duplicate spawn event for GONetId {newSpawn.GONetId} - already in persistentEventsThisSession");
                            break;
                        }
                    }
                    if (alreadyExists)
                    {
                        return; // Don't add duplicate spawn event
                    }
                }

                persistentEventsThisSession.AddLast(eventEnvelope.Event);
                //if (eventEnvelope.Event is DespawnGONetParticipantEvent despawn)
                //{
                    //GONetLog.Warning($"[DESPAWN_SYNC] Added DespawnGONetParticipantEvent to persistentEventsThisSession (no events cancelled) - GONetId: {despawn.GONetId}");
                //}
            }
            else
            {
                //if (eventEnvelope.Event is DespawnGONetParticipantEvent despawn)
                //{
                    //GONetLog.Warning($"[DESPAWN_SYNC] DespawnGONetParticipantEvent cancelled out {count} events for GONetId {despawn.GONetId} - despawn event NOT added to persistentEventsThisSession (correct: object no longer exists)");
                //}
                // PERF FIX (Dec 2025): Use O(1) node removal instead of O(n) value search
                // Old code: persistentEventsThisSession.Remove(cancelledEvent) - O(n) per removal!
                // New code: persistentEventsThisSession.Remove(node) - O(1) per removal
                for (int i = 0; i < count; ++i)
                {
                    persistentEventsThisSession.Remove(persistentEventNodesCancelledOut[i]);
                }

                // BUG FIX: The cancelling event should ALSO be added to the list (replacing what it cancelled).
                // Previously, events that cancelled others were not added, causing late-joiners to miss them.
                // Example: SceneLoadEvent for 'GameScene' cancels SceneLoadEvent for 'Lobby', but 'GameScene'
                // wasn't being added, so late-joiners never received the scene load instruction.
                // Exception: DespawnGONetParticipantEvent cancels InstantiateGONetParticipantEvent and should
                // NOT be added (the object no longer exists, so late-joiners don't need to know about it).
                if (!(eventEnvelope.Event is DespawnGONetParticipantEvent) && !isPoolReturnEvent)
                {
                    persistentEventsThisSession.AddLast(eventEnvelope.Event);
                }
            }

            // DIAGNOSTIC: Periodic logging every 2 seconds (commented out - development diagnostic)
            // long nowTicks = HighResolutionTimeUtils.UtcNowTicks;
            // if (nowTicks - persistentEventDiag_lastLogTicks > PERSISTENT_EVENT_DIAG_LOG_INTERVAL_TICKS)
            // {
            //     double cancelCheckTimeMs = persistentEventDiag_totalCancelCheckTimeTicks / 10000.0;
            //     double avgIterationsPerEvent = persistentEventDiag_eventsProcessedSinceLastLog > 0
            //         ? (double)persistentEventDiag_totalIterationsSinceLastLog / persistentEventDiag_eventsProcessedSinceLastLog
            //         : 0;
            //     var typeBreakdown = new System.Text.StringBuilder();
            //     foreach (var kvp in persistentEventDiag_eventTypeCountsSinceLastLog)
            //     {
            //         if (typeBreakdown.Length > 0) typeBreakdown.Append(", ");
            //         typeBreakdown.Append(kvp.Key).Append("=").Append(kvp.Value);
            //     }
            //     GONetLog.Warning($"[PERSIST-DIAG] Events={persistentEventDiag_eventsProcessedSinceLastLog}, " +
            //         $"ListSize={persistentEventsThisSession.Count}, ArchiveSize={persistentEventsArchive_CompleteHistory.Count}, " +
            //         $"TotalIterations={persistentEventDiag_totalIterationsSinceLastLog}, AvgIter/Event={avgIterationsPerEvent:F1}, " +
            //         $"CancelCheckTime={cancelCheckTimeMs:F2}ms | Types: [{typeBreakdown}]");
            //     persistentEventDiag_lastLogTicks = nowTicks;
            //     persistentEventDiag_eventsProcessedSinceLastLog = 0;
            //     persistentEventDiag_totalIterationsSinceLastLog = 0;
            //     persistentEventDiag_totalCancelCheckTimeTicks = 0;
            //     persistentEventDiag_eventTypeCountsSinceLastLog.Clear();
            // }
        }

        private static void OnInstantiationEvent_Remote(GONetEventEnvelope<InstantiateGONetParticipantEvent> eventEnvelope)
        {
            // DIAGNOSTIC: Log when server receives GONetLocal spawn event
            if (GONetConfig.LogSpawnDiagnostics && IsServer && eventEnvelope.Event.DesignTimeLocation != null &&
                eventEnvelope.Event.DesignTimeLocation.Contains("GONetLocal"))
            {
                GONetLog.Debug($"[SPAWN-DIAG] SERVER received GONetLocal spawn event - GONetId: {eventEnvelope.Event.GONetId}, SourceAuthority: {eventEnvelope.SourceAuthorityId}, DesignTimeLocation: {eventEnvelope.Event.DesignTimeLocation}");
                serverReceivedGONetLocalSpawnAuthorities[eventEnvelope.SourceAuthorityId] = 1;
            }

            // LATE-JOINER / CROSS-CHANNEL ORDERING FIX (Dec 2025):
            // Spawn events (persistent init channel) and despawn events (runtime reliable channel) do not have ordering guarantees
            // across channels. During late-joiner initialization it is possible to receive a despawn BEFORE the corresponding spawn
            // arrives/gets processed, which previously caused the despawn to be dropped and the object to become a permanent ghost.
            if (TryConsumeDespawnTombstone(eventEnvelope.Event.GONetId))
            {
                return; // Spawn was already despawned before we processed the spawn; ignore this spawn.
            }

            // DUPLICATE SPAWN PREVENTION (Dec 2025):
            // When reliable retransmission, hot-standby mesh sync, or other multi-path delivery causes
            // the same spawn event to arrive twice, prevent creating duplicate GameObjects.
            // The second instance would register in SoA, overwriting the first, leaving phantom objects
            // stuck at origin that never receive sync updates.
            uint gonetId = eventEnvelope.Event.GONetId;
            if (gonetId != GONetParticipant.GONetId_Unset && gonetParticipantByGONetIdMap.ContainsKey(gonetId))
            {
                GONetLog.Warning($"[SPAWN_SYNC] DUPLICATE spawn event ignored - GONetId {gonetId} already exists as '{gonetParticipantByGONetIdMap[gonetId].name}'");
                return;
            }

            const string IR = "pub/sub Instantiate REMOTE about to process...";
            //GONetLog.Debug(IR + $" gonetId: {eventEnvelope.Event.GONetId}, DesignTimeLocation: '{eventEnvelope.Event.DesignTimeLocation}', SceneIdentifier: '{eventEnvelope.Event.SceneIdentifier}', InstanceName: '{eventEnvelope.Event.InstanceName}'");

            // Check if this spawn requires a scene that isn't loaded yet
            if (!string.IsNullOrEmpty(eventEnvelope.Event.SceneIdentifier))
            {
                bool isSceneLoaded = IsSceneCurrentlyLoaded(eventEnvelope.Event.SceneIdentifier);
                //GONetLog.Debug($"[SPAWN_SYNC] GONetId {eventEnvelope.Event.GONetId} requires scene '{eventEnvelope.Event.SceneIdentifier}' - IsLoaded: {isSceneLoaded}");
                if (!isSceneLoaded)
                {
                    // Defer this spawn until the required scene is loaded
                    //GONetLog.Warning($"[SPAWN_SYNC] DEFERRING spawn for GONetId {eventEnvelope.Event.GONetId} - waiting for scene '{eventEnvelope.Event.SceneIdentifier}' to load");
                    deferredSpawnEvents.Add(eventEnvelope.Event);
                    return;
                }
            }

            //GONetLog.Debug($"[SPAWN_SYNC] Processing spawn immediately for GONetId {eventEnvelope.Event.GONetId}");
            GONetParticipant instance = Instantiate_Remote(eventEnvelope.Event);

            // If instantiation failed (e.g., empty DesignTimeLocation), skip further processing
            if (instance == null)
            {
                GONetLog.Warning($"Skipping remote instantiation processing - Instantiate_Remote returned null for GONetId: {eventEnvelope.Event.GONetId}");
                return;
            }

            // Complete the post-instantiation processing
            CompleteRemoteInstantiation(instance, eventEnvelope.Event, eventEnvelope.SourceAuthorityId);
        }

        /// <summary>
        /// Completes post-instantiation setup for remotely spawned objects.
        /// This must be called for ALL remote spawns (immediate and deferred) to ensure proper initialization.
        /// </summary>
        private static void CompleteRemoteInstantiation(GONetParticipant instance, InstantiateGONetParticipantEvent spawnEvent, ushort sourceAuthorityId)
        {
            if (IsServer)
            {
                // CRITICAL FIX (December 2025): Update lastAssignedGONetIdRaw when receiving client-owned spawns.
                // Without this, the server's lastAssignedGONetIdRaw can be lower than the client's,
                // causing batch allocations to overlap with IDs the client has already used.
                // Example scenario without fix:
                //   1. Server allocates batch [4-203], sets lastAssignedGONetIdRaw = 203
                //   2. Client spawns client-owned objects with raw=204, 205, ..., 225
                //   3. Client requests new batch, server allocates [205-404] (overlaps with 205-225!)
                //   4. Collision: Same raw ID used by client-owned AND server-owned objects
                uint remoteSpawnGONetIdRaw = spawnEvent.GONetId >> GONetParticipant.GONET_ID_BIT_COUNT_UNUSED;
                if (remoteSpawnGONetIdRaw > lastAssignedGONetIdRaw)
                {
                    //GONetLog.Debug($"[GONetIdBatch] SERVER updating lastAssignedGONetIdRaw: {lastAssignedGONetIdRaw} → {remoteSpawnGONetIdRaw} (from client spawn GONetId {spawnEvent.GONetId})");
                    lastAssignedGONetIdRaw = remoteSpawnGONetIdRaw;
                }

                GONetLocal gonetLocal = instance.gameObject.GetComponent<GONetLocal>();
                if (gonetLocal != null)
                {
                    Server_OnNewClientInstantiatedItsGONetLocal(gonetLocal);
                }

                if (spawnEvent.ImmediatelyRelinquishAuthorityToServer_AndTakeRemoteControlAuthority)
                {
                    GONetSpawnSupport_Runtime.Server_MarkToBeRemotelyControlled(instance, sourceAuthorityId);
                }
            }

            if (instance.ShouldHideDuringRemoteInstantiate && valueBlendingBufferLeadSeconds > 0)
            {
                //GONetLog.Debug($"[SPAWN_SYNC] Starting hide-during-buffer coroutine for '{instance.gameObject.name}'");
                GlobalSessionContext.StartCoroutine(OnInstantiationEvent_Remote_HideDuringBufferLeadTime(instance));
            }

            // CRITICAL: Start monitoring for auto-magical value sync on remote spawns
            // This was previously missing, causing remote spawns (especially server-owned projectiles from client spawn requests)
            // to not have their transform/value changes propagated over the network
            // The comment in Start_AutoPropogateInstantiation_IfAppropriate_INTERNAL said "remote source is processed like this elsewhere"
            // but there was no "elsewhere" - this is it!
            //
            // IMPORTANT: Force monitoring even if DidStartMonitoringForAutoMagicalNetworking is already true
            // Remote spawns may have had monitoring started elsewhere but it needs to happen on THIS machine (server)
            bool wasAlreadyMonitoring = instance.DidStartMonitoringForAutoMagicalNetworking;
            if (wasAlreadyMonitoring)
            {
                // Reset flag to allow monitoring to start
                //instance.DidStartMonitoringForAutoMagicalNetworking = false;
                //GONetLog.Debug($"[SPAWN_SYNC] Forcing monitoring restart for remote spawn '{instance.name}' (GONetId: {instance.GONetId}) - was already marked as monitoring");
            }
            OnWasInstantiatedKnown_StartMonitoringForAutoMagicalNetworking(instance);
            //GONetLog.Debug($"[SPAWN_SYNC] Started monitoring for remote spawn '{instance.name}' (GONetId: {instance.GONetId}, wasAlreadyMonitoring: {wasAlreadyMonitoring})");

            // FIX (December 2025): Register remote-spawned non-authority objects in SoA IMMEDIATELY.
            // Problem: OnGONetReady never fires for these objects because IsGONetReady() returns false
            // when GONetLocal.LookupByAuthorityId[OwnerAuthorityId] is null (server's GONetLocal not yet available on client).
            // This causes objects to never be registered in SoA lookup, so sync data is dropped (DATA_IN never logged).
            // Solution: Register directly here since we know:
            // 1. This is a remote spawn (received from network)
            // 2. GONetId is assigned
            // 3. IsMine=false (we don't own it, so we need to blend received data)
            // 4. Object needs blending (will receive sync data from the authority)
            if (!instance.IsMine && !instance.v2_isRegisteredInSoA)
            {
                RegisterObjectInSoA(instance);
#if GONet_SOA_TRACE
                GONetLog.Debug($"[SoA-REMOTE-REG] Registered remote spawn '{instance.name}' (GONetId {instance.GONetId}) in SoA immediately at CompleteRemoteInstantiation");
#endif
            }

            // PATH 7: Remote runtime-spawned participants - publish DeserializeInitAllCompleted after spawn completes
            // This handles projectiles/objects spawned by remote players that don't go through DeserializeBody_AllValuesBundle
            // The GONetId was set during Instantiate_Remote, so the participant is now fully ready from this client's perspective
            // NOTE: isRelatedLocalContentRequired=false because remote spawns are ready immediately on receiving client
            // The owner's GONetLocal may not exist yet on this client, but that's OK - the spawn itself is what matters
            if (instance.GONetId != 0) // Only require GONetId to be assigned
            {
                // Deduplication check: Only publish if not already published
                if (TryMarkDeserializeInitPublished(instance.GONetId))
                {
                    //GONetLog.Info($"[GONet] Publishing DeserializeInitAllCompleted for remote spawn '{instance.name}' (GONetId: {instance.GONetId}, IsMine: {instance.IsMine}) from CompleteRemoteInstantiation");
                    var deserializeInitEvent = new GONetParticipantDeserializeInitAllCompletedEvent(instance);
                    PublishEventAsSoonAsSufficientInfoAvailable(deserializeInitEvent, instance, isRelatedLocalContentRequired: false);
                }
                else
                {
                    //GONetLog.Info($"[GONet] Skipping duplicate DeserializeInitAllCompleted for remote spawn '{instance.name}' (GONetId: {instance.GONetId}) - already published from another path");
                }
            }
            else
            {
                GONetLog.Error($"[GONet] Remote spawn '{instance.name}' has no GONetId in CompleteRemoteInstantiation - this should never happen!");
            }
        }

        /// <summary>
        /// PRE: <see cref="valueBlendingBufferLeadSeconds"/> is greater than 0
        /// IMPORTANT: If there is a transition of authority (i.e., a call to <see cref="Server_AssumeAuthorityOver(GONetParticipant)"/>) and 
        ///            <see cref="GONetParticipant.IsMine"/> becomes true during this waiting period, then the renders will bet set to enabled instead of waiting.
        /// </summary>
        private static IEnumerator OnInstantiationEvent_Remote_HideDuringBufferLeadTime(GONetParticipant instance)
        {
            Renderer[] activeRenderers = instance.GetComponentsInChildren<Renderer>(false);

            int count = activeRenderers.Length;
            for (int i = 0; i < count; ++i)
            {
                activeRenderers[i].enabled = false;
            }

            long startTicks = Time.ElapsedTicks;
            while (instance != null && !instance.IsMine && ((Time.ElapsedTicks - startTicks) < valueBlendingBufferLeadTicks))
            {
                yield return null;
            }

            for (int i = 0; i < count; ++i)
            {
                Renderer renderer = activeRenderers[i];
                if (renderer != null)
                {
                    renderer.enabled = true;
                }
            }
        }

        /// <summary>
        /// PRODUCTION-READY EVENT BROADCAST FRAMEWORK
        ///
        /// Robustly iterates all GONetBehaviours and invokes a callback for each, with:
        /// - Unity fake null protection (destroyed objects during iteration)
        /// - Per-behaviour exception isolation (one failure doesn't break pipeline)
        /// - Detailed error logging with context
        /// - Safe enumerator disposal (handles DestroyImmediate mid-iteration)
        ///
        /// Added 2025-10-11 to replace brittle direct iteration pattern in lifecycle event handlers.
        /// </summary>
        /// <param name="callback">Action to invoke for each behaviour. Exceptions are caught and logged.</param>
        /// <param name="eventName">Name of event being broadcast (for error logging context)</param>
        /// <param name="gonetParticipant">Related GONetParticipant (for error logging context)</param>
        private static void BroadcastToAllGONetBehaviours_Robust(
            System.Action<GONetBehaviour> callback,
            string eventName,
            GONetParticipant gonetParticipant)
        {
            int successCount = 0;
            int failureCount = 0;
            int nullSkipCount = 0;

            using (var enumerator = allGONetBehaviours.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    GONetBehaviour behaviour = enumerator.Current;

                    // DEFENSIVE: Unity fake null check (object destroyed during iteration)
                    if (behaviour == null)
                    {
                        nullSkipCount++;
                        continue;
                    }

                    // ROBUST: Per-behaviour try-catch - one exception doesn't break pipeline
                    try
                    {
                        callback(behaviour);
                        successCount++;
                    }
                    catch (System.Exception ex)
                    {
                        failureCount++;

                        // DETAILED ERROR LOGGING: Include full context for debugging
                        GONetLog.Error(
                            $"[GONet-EventBroadcast] EXCEPTION in {eventName} handler for GONetBehaviour '{behaviour.GetType().Name}' " +
                            $"(GameObject: {behaviour.gameObject?.name ?? "NULL"}) " +
                            $"processing GONetParticipant '{gonetParticipant?.gameObject?.name ?? "NULL"}' " +
                            $"(GONetId: {gonetParticipant?.GONetId ?? 0})\n" +
                            $"Exception: {ex.Message}\n" +
                            $"StackTrace:\n{ex.StackTrace}");
                    }
                }
            }

            // DIAGNOSTIC: Log if failures occurred (only when there were actual errors)
            if (failureCount > 0)
            {
                GONetLog.Warning(
                    $"[GONet-EventBroadcast] {eventName} completed with ERRORS: " +
                    $"Success={successCount}, Failures={failureCount}, NullSkipped={nullSkipCount}");
            }
        }

        private static void OnEnabledGNPEvent(GONetEventEnvelope<GONetParticipantEnabledEvent> eventEnvelope)
        {
            GONetParticipant gonetParticipant = eventEnvelope.GONetParticipant;
            BroadcastToAllGONetBehaviours_Robust(
                behaviour => behaviour.OnGONetParticipantEnabled(gonetParticipant),
                nameof(OnEnabledGNPEvent),
                gonetParticipant);
        }

        private static void OnStartedGNPEvent(GONetEventEnvelope<GONetParticipantStartedEvent> eventEnvelope)
        {
            GONetParticipant gonetParticipant = eventEnvelope.GONetParticipant;
            BroadcastToAllGONetBehaviours_Robust(
                behaviour => behaviour.OnGONetParticipantStarted(gonetParticipant),
                nameof(OnStartedGNPEvent),
                gonetParticipant);

            // Process any pending animator trigger events for this newly spawned participant
            ProcessPendingAnimatorTriggers(gonetParticipant);
        }

        private static void OnDeserializeInitAllCompletedGNPEvent(GONetEventEnvelope<GONetParticipantDeserializeInitAllCompletedEvent> eventEnvelope)
        {
            GONetParticipant gonetParticipant = eventEnvelope.GONetParticipant;
            BroadcastToAllGONetBehaviours_Robust(
                behaviour => behaviour.OnGONetParticipantDeserializeInitAllCompleted(gonetParticipant),
                nameof(OnDeserializeInitAllCompletedGNPEvent),
                gonetParticipant);

            // LIFECYCLE GATE: Mark deserialization complete and check if OnGONetReady can fire
            // This replaces the old direct broadcast - now uses the centralized gate check
            gonetParticipant.MarkDeserializeInitComplete();
        }

        private static void OnDisabledGNPEvent(GONetEventEnvelope<GONetParticipantDisabledEvent> eventEnvelope)
        {
            GONetParticipant gonetParticipant = eventEnvelope.GONetParticipant;

            // CRITICAL: Check if Unity object is destroyed before accessing Unity methods
            // Unity fake null pattern: gonetParticipant reference exists, but Unity object may be destroyed
            if (gonetParticipant == null)
            {
                // Unity object destroyed - can't access Unity methods like GetInstanceID()
                // But we can still access C# properties if the reference isn't actually null
                if ((object)gonetParticipant != null)
                {
                    // C# reference exists - can access pure C# properties
                    recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map[gonetParticipant.GONetId] = gonetParticipant.GONetIdAtInstantiation;
                }
                return; // Skip rest of processing - can't call Unity methods on destroyed object
            }

            recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map[gonetParticipant.GONetId] = gonetParticipant.GONetIdAtInstantiation;

            // Clean up spawn scene tracking when GNP is disabled/destroyed
            ClearParticipantSpawnScene(gonetParticipant);
            definedInSceneParticipantInstanceIDs.Remove(gonetParticipant.GetInstanceID());

            // ROBUST: Use production-ready broadcast framework
            BroadcastToAllGONetBehaviours_Robust(
                behaviour => behaviour.OnGONetParticipantDisabled(gonetParticipant),
                nameof(OnDisabledGNPEvent),
                gonetParticipant);
        }

        /// <summary>
        /// Handles remote <see cref="DespawnGONetParticipantEvent"/> notifications.
        /// <para>This is called when a remote client/server despawns a GONetParticipant through gameplay logic
        /// (e.g., projectile hits, enemy dies, player destroys object).</para>
        /// <para><b>IMPORTANT:</b> This is NOT called for scene unload destroys, which are local-only.</para>
        /// </summary>
	        private static void OnDespawnGNPEvent_Remote(GONetEventEnvelope<DespawnGONetParticipantEvent> eventEnvelope)
	        {
	            uint gonetId = eventEnvelope.Event.GONetId;

            // CANCEL-ON-DESPAWN OPTIMIZATION (Dec 2025):
            // If this GONetId has a deferred spawn, CANCEL the spawn instead of deferring the despawn.
            // This is more efficient (no wasted work spawning then destroying) and reduces ordering bugs.
            // We also add a tombstone so any related AllValues bundles are handled gracefully.
	            int deferredSpawnIndex = deferredSpawnEvents.FindIndex(spawnEvent => spawnEvent.GONetId == gonetId);
	            if (deferredSpawnIndex >= 0)
	            {
	                GONetLog.Debug($"[SPAWN_SYNC] CANCELING deferred spawn for GONetId {gonetId} - despawn arrived before spawn was processed");
	                InstantiateGONetParticipantEvent deferredSpawnEvent = deferredSpawnEvents[deferredSpawnIndex];
	                deferredSpawnEvents.RemoveAt(deferredSpawnIndex);

	                // Tombstone this despawn so we can suppress the deferred spawn and drop any late-arriving sync/AllValues for this id.
	                // NOTE: Sync bundles often reference the instantiation id, not the current GONetId, so tombstone both when available.
	                AddOrRefreshDespawnTombstone(gonetId);
	                if (deferredSpawnEvent.GONetIdAtInstantiation != GONetParticipant.GONetId_Unset &&
	                    deferredSpawnEvent.GONetIdAtInstantiation != gonetId)
	                {
	                    AddOrRefreshDespawnTombstone(deferredSpawnEvent.GONetIdAtInstantiation);
	                }

	                // Clear any pending animator triggers for this despawned object
	                ClearPendingAnimatorTriggers(gonetId);
	                return;
	            }

            GONetParticipant gonetParticipant = null;
	            if (gonetParticipantByGONetIdMap.TryGetValue(gonetId, out gonetParticipant))
	            {
	                // Tombstone this despawn even when the participant exists, to drop any late-arriving sync bundles after destruction.
	                // This is especially important under congestion/failover where unreliable bundles can lag behind reliable despawns.
	                AddOrRefreshDespawnTombstone(gonetId);
	                if ((object)gonetParticipant != null &&
	                    gonetParticipant.GONetIdAtInstantiation != GONetParticipant.GONetId_Unset &&
	                    gonetParticipant.GONetIdAtInstantiation != gonetId)
	                {
	                    AddOrRefreshDespawnTombstone(gonetParticipant.GONetIdAtInstantiation);
	                }

	                gonetIdsDestroyedViaPropagation.Add(gonetParticipant.GONetId); // this container must have the gonetId added first in order to prevent OnDestroy_AutoPropagateRemoval_IfAppropriate from thinking it is appropriate to propagate more when it is already being propagated

                if (gonetParticipant == null || gonetParticipant.gameObject == null)
                {
                    const string REC = "Received remote notification to despawn a GNP, but Unity says it's already null. Ensure only the owner (i.e., GNP.IsMine) is the one who calls Unity's Destroy() method and the propagation across the network will be automatic via GONet.";
                    GONetLog.Error(REC);
                }
	            else
	            {
                    //GONetLog.Warning($"[DESPAWN_SYNC] CLIENT: Destroying GameObject '{gonetParticipant.gameObject.name}' for GONetId {gonetParticipant.GONetId}");
                    UnityEngine.Object.Destroy(gonetParticipant.gameObject);
                }

                // Clear any pending animator triggers for this despawned object
                ClearPendingAnimatorTriggers(gonetId);
            }
            else
            {
                // LATE-JOINER / CROSS-CHANNEL ORDERING FIX (Dec 2025):
                // If the spawn arrives later (different channel), we must remember this despawn so we can suppress the spawn.
                // Otherwise we can end up with permanent "ghost" objects (spawned from persistent bundle, despawn dropped).
	                AddOrRefreshDespawnTombstone(gonetId);

	                // Clear any pending animator triggers for this despawned object
	                ClearPendingAnimatorTriggers(gonetId);
	            }
	        }

	        // Tracks despawns so we can suppress late spawns and drop late-arriving sync/AllValues bundles for objects that no longer exist.
	        // NOTE: We may tombstone BOTH current GONetId and instantiation id because some sync bundles reference instantiation ids.
	        private static readonly Dictionary<uint, long> despawnTombstoneByGONetId = new Dictionary<uint, long>(128);
	        private static readonly List<uint> despawnTombstoneRemovalBuffer = new List<uint>(128);

	        // Configurable via GONetConfig - read dynamically to support runtime changes
	        private static int DespawnTombstoneMaxEntries => GONetConfig.DespawnTombstoneMaxEntries;
	        private static long DespawnTombstoneTTLTicks => (long)(GONetConfig.DespawnTombstoneTTLMinutes * 60 * TimeSpan.TicksPerSecond);
	        private static long DespawnTombstonePruneIntervalTicks => (long)(GONetConfig.DespawnTombstonePruneIntervalSeconds * TimeSpan.TicksPerSecond);
	        private static long despawnTombstoneLastPruneTicks;

	        private static void AddOrRefreshDespawnTombstone(uint gonetId)
	        {
	            long nowTicks = Time.ElapsedTicks;
	            despawnTombstoneByGONetId[gonetId] = nowTicks;
	            PruneDespawnTombstonesIfNeeded(nowTicks);
	        }

	        private static void PruneDespawnTombstonesIfNeeded(long nowTicks)
	        {
	            if (despawnTombstoneByGONetId.Count == 0)
	            {
	                return;
	            }

	            bool shouldPruneForTime = nowTicks - despawnTombstoneLastPruneTicks >= DespawnTombstonePruneIntervalTicks;
	            bool shouldPruneForSize = despawnTombstoneByGONetId.Count > DespawnTombstoneMaxEntries;
	            if (!shouldPruneForTime && !shouldPruneForSize)
	            {
	                return;
	            }

	            if (shouldPruneForTime)
	            {
	                despawnTombstoneLastPruneTicks = nowTicks;

	                // Time-based prune: remove expired entries regardless of current size.
	                long cutoffTicks = nowTicks - DespawnTombstoneTTLTicks;
	                despawnTombstoneRemovalBuffer.Clear();
	                foreach (var kvp in despawnTombstoneByGONetId)
	                {
	                    if (kvp.Value < cutoffTicks)
	                    {
	                        despawnTombstoneRemovalBuffer.Add(kvp.Key);
	                    }
	                }

	                int removalCount = despawnTombstoneRemovalBuffer.Count;
	                for (int i = 0; i < removalCount; ++i)
	                {
	                    despawnTombstoneByGONetId.Remove(despawnTombstoneRemovalBuffer[i]);
	                }
	            }

	            // Size-based prune: bound memory even if nothing is old enough (safety net).
	            int excessCount = despawnTombstoneByGONetId.Count - DespawnTombstoneMaxEntries;
	            if (excessCount > 0)
	            {
	                despawnTombstoneRemovalBuffer.Clear();
	                foreach (var kvp in despawnTombstoneByGONetId)
	                {
	                    despawnTombstoneRemovalBuffer.Add(kvp.Key);
	                    if (despawnTombstoneRemovalBuffer.Count >= excessCount)
	                    {
	                        break;
	                    }
	                }

	                int excessRemovalCount = despawnTombstoneRemovalBuffer.Count;
	                for (int i = 0; i < excessRemovalCount; ++i)
	                {
	                    despawnTombstoneByGONetId.Remove(despawnTombstoneRemovalBuffer[i]);
	                }
	            }
	        }

	        private static bool TryConsumeDespawnTombstone(uint gonetId)
	        {
	            if (despawnTombstoneByGONetId.Count == 0)
	            {
	                return false;
	            }

	            long nowTicks = Time.ElapsedTicks;
	            if (!despawnTombstoneByGONetId.TryGetValue(gonetId, out long tombstoneTicks))
	            {
	                PruneDespawnTombstonesIfNeeded(nowTicks);
	                return false;
	            }

	            // Enforce TTL even when the map never grows to the max.
	            if (nowTicks - tombstoneTicks > DespawnTombstoneTTLTicks)
	            {
	                despawnTombstoneByGONetId.Remove(gonetId);
	                return false;
	            }

	            // IMPORTANT: Do NOT remove the tombstone here.
	            // We keep it for a short TTL so any late-arriving AllValues/unreliable bundles for this id are dropped,
	            // preventing GONetReady queue blowups and "ghost" state after failover/cross-channel reordering.
	            PruneDespawnTombstonesIfNeeded(nowTicks);
	            return true;
	        }

        #region Tombstone Test Helpers

        /// <summary>
        /// TEST ONLY: Clears all despawn tombstones. Used by unit tests for clean state.
        /// </summary>
        internal static void ClearDespawnTombstones_TestOnly()
        {
            despawnTombstoneByGONetId.Clear();
        }

        /// <summary>
        /// TEST ONLY: Checks if a tombstone exists for the given GONetId.
        /// Used by unit tests to verify tombstone creation.
        /// </summary>
        internal static bool HasDespawnTombstone(uint gonetId)
        {
            if (despawnTombstoneByGONetId.Count == 0)
            {
                return false;
            }

            if (!despawnTombstoneByGONetId.TryGetValue(gonetId, out long tombstoneTicks))
            {
                return false;
            }

            // Check if tombstone is still valid (not expired)
            long nowTicks = Time.ElapsedTicks;
            return (nowTicks - tombstoneTicks) <= DespawnTombstoneTTLTicks;
        }

        /// <summary>
        /// TEST ONLY: Creates or refreshes a tombstone for the given GONetId.
        /// Used by unit tests to simulate tombstone creation.
        /// </summary>
        internal static void AddOrRefreshDespawnTombstone_TestOnly(uint gonetId)
        {
            AddOrRefreshDespawnTombstone(gonetId);
        }

        #endregion

        private static void InitShouldSkipSyncSupport()
        {

            // TODO FIXME add this back?: GONetAutoMagicalSyncAttribute.ShouldSkipSyncByRegistrationIdMap[(int)GONetAutoMagicalSyncAttribute.ShouldSkipSyncRegistrationId.GONetParticipant_IsRotationSyncd] = IsRotationNotSyncd;

            // TODO FIXME add this back?: GONetAutoMagicalSyncAttribute.ShouldSkipSyncByRegistrationIdMap[(int)GONetAutoMagicalSyncAttribute.ShouldSkipSyncRegistrationId.GONetParticipant_IsPositionSyncd] = IsPositionNotSyncd;
        }

        internal static bool IsRotationNotSyncd(AutoMagicalSync_ValueMonitoringSupport_ChangedValue monitoringSupport, int index)
        {
            // FIX (Oct 2025): Prevent MissingReferenceException when accessing destroyed objects after scene unload
            GONetParticipant participant = monitoringSupport.syncCompanion.gonetParticipant;
            if (participant == null)
            {
                return true; // Skip: GONetParticipant has been destroyed
            }

            // Check if rotation sync is disabled
            if (!participant.IsRotationSyncd)
            {
                return true; // Skip: rotation sync disabled
            }

            // PHYSICS SYNC FREQUENCY GATING: Check if this is a physics object and if so, gate by PhysicsUpdateInterval
            bool isPhysicsObject = participant.IsRigidBodyOwnerOnlyControlled && participant.myRigidBody != null && participant.IsMine;

            if (isPhysicsObject)
            {
                // Get the physics update interval from this specific value's sync profile
                int physicsUpdateInterval = monitoringSupport.syncAttribute_PhysicsUpdateInterval;

                // Skip this physics frame if counter doesn't match interval
                // physicsUpdateInterval=1: sync frames 0,1,2,3 (always)
                // physicsUpdateInterval=2: sync frames 0,2 (every 2nd)
                // physicsUpdateInterval=3: sync frames 0,3 (every 3rd)
                // physicsUpdateInterval=4: sync frame 0 (every 4th)
                if (physicsFrameCounter % physicsUpdateInterval != 0)
                {
                    return true; // Skip: not the right physics frame for this interval
                }
            }

            return false; // Don't skip: should sync
        }

        internal static bool IsPositionNotSyncd(AutoMagicalSync_ValueMonitoringSupport_ChangedValue monitoringSupport, int index)
        {
            // FIX (Oct 2025): Prevent MissingReferenceException when accessing destroyed objects after scene unload
            if (monitoringSupport.syncCompanion.gonetParticipant == null)
            {
                return true; // Skip: GONetParticipant has been destroyed
            }

            // Check if position sync is disabled
            if (!monitoringSupport.syncCompanion.gonetParticipant.IsPositionSyncd)
            {
                return true; // Skip: position sync disabled
            }

            // PHYSICS SYNC FREQUENCY GATING: Check if this is a physics object and if so, gate by PhysicsUpdateInterval
            GONetParticipant participant = monitoringSupport.syncCompanion.gonetParticipant;
            bool isPhysicsObject = participant.IsRigidBodyOwnerOnlyControlled && participant.myRigidBody != null && participant.IsMine;

            if (isPhysicsObject)
            {
                // Get the physics update interval from this specific value's sync profile
                int physicsUpdateInterval = monitoringSupport.syncAttribute_PhysicsUpdateInterval;

                // Skip this physics frame if counter doesn't match interval
                // physicsUpdateInterval=1: sync frames 0,1,2,3 (always)
                // physicsUpdateInterval=2: sync frames 0,2 (every 2nd)
                // physicsUpdateInterval=3: sync frames 0,3 (every 3rd)
                // physicsUpdateInterval=4: sync frame 0 (every 4th)
                if (physicsFrameCounter % physicsUpdateInterval != 0)
                {
                    return true; // Skip: not the right physics frame for this interval
                }
            }

            return false; // Don't skip: should sync
        }

        private static bool _hasLoggedAnimatorSyncDebug = false;
        private static HashSet<string> _loggedAnimatorSyncWarnings = new HashSet<string>();

        /// <summary>
        /// PERFORMANCE OPTIMIZED: Fast cached check for animator parameter sync status.
        /// Uses pre-initialized <see cref="AutoMagicalSync_ValueMonitoringSupport_ChangedValue.isAnimatorParameterSyncd_Cached"/>
        /// instead of doing a string-based dictionary lookup every frame.
        /// The cache is initialized at companion creation time.
        /// If animatorSyncSupport changes at runtime, call <see cref="GONetParticipant.RefreshAnimatorSyncCache"/>.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static bool IsAnimatorParameterNotSyncd_Cached(AutoMagicalSync_ValueMonitoringSupport_ChangedValue monitoringSupport, int index)
        {
            // Fast path: use cached value (no dictionary lookup!)
            // isAnimatorParameterSyncd_Cached is true if should sync, false if should skip
            // We return true to SKIP, so we return !isAnimatorParameterSyncd_Cached
            return !monitoringSupport.isAnimatorParameterSyncd_Cached;
        }

        /// <summary>
        /// LEGACY: Original method that does dictionary lookup every call.
        /// Kept for backward compatibility and as fallback if cache not initialized.
        /// For normal runtime operation, use <see cref="IsAnimatorParameterNotSyncd_Cached"/> instead.
        /// </summary>
        internal static bool IsAnimatorParameterNotSyncd(AutoMagicalSync_ValueMonitoringSupport_ChangedValue monitoringSupport, int index)
        {
            // Safety check for destroyed objects
            GONetParticipant participant = monitoringSupport.syncCompanion.gonetParticipant;
            if (participant == null)
            {
                return true; // Skip: GONetParticipant destroyed
            }

            // Check if this is an animator parameter (safety check)
            if (string.IsNullOrEmpty(monitoringSupport.animatorParameterName))
            {
                return false; // Not an animator parameter, don't skip
            }

            // Check the runtime isSyncd state
            if (participant.animatorSyncSupport != null &&
                participant.animatorSyncSupport.TryGetValue(monitoringSupport.animatorParameterName, out var paramInfo))
            {
                bool shouldSkip = !paramInfo.isSyncd;

                // Debug logging (only log once per parameter to avoid spam)
                if (!_hasLoggedAnimatorSyncDebug)
                {
                    GONetLog.Debug($"[ANIMATOR-SYNC] Parameter '{monitoringSupport.animatorParameterName}' on '{participant.gameObject.name}' - isSyncd={paramInfo.isSyncd}, shouldSkip={shouldSkip}, dictCount={participant.animatorSyncSupport.Count}");
                    _hasLoggedAnimatorSyncDebug = true;
                }

                return shouldSkip; // Return true to SKIP if isSyncd is false
            }

            // Debug: parameter not found (log only once per object to avoid spam)
            string warningKey = participant.gameObject.name;
            if (!_loggedAnimatorSyncWarnings.Contains(warningKey))
            {
                _loggedAnimatorSyncWarnings.Add(warningKey);
                GONetLog.Warning($"[ANIMATOR-SYNC] Parameter '{monitoringSupport.animatorParameterName}' NOT FOUND in animatorSyncSupport dictionary on '{participant.gameObject.name}'. Dict is null? {participant.animatorSyncSupport == null}, Count: {participant.animatorSyncSupport?.Count ?? 0}, CodeGenId={monitoringSupport.syncCompanion.CodeGenerationId}");
            }

            return true; // Skip if parameter not found in dictionary (safe default)
        }

        /// <summary>
        /// Initializes the animator parameter sync cache for a single monitoring support entry.
        /// Called during companion initialization and when <see cref="GONetParticipant.RefreshAnimatorSyncCache"/> is invoked.
        /// </summary>
        internal static void InitializeAnimatorParameterSyncCache(AutoMagicalSync_ValueMonitoringSupport_ChangedValue monitoringSupport, GONetParticipant participant)
        {
            if (string.IsNullOrEmpty(monitoringSupport.animatorParameterName))
            {
                // Not an animator parameter, nothing to cache
                monitoringSupport.isAnimatorParameterSyncd_Cached = true; // Default: don't skip non-animator params
                monitoringSupport.isAnimatorParameterSyncd_CacheInitialized = false;
                return;
            }

            // Look up isSyncd state from dictionary ONCE and cache it
            if (participant != null && participant.animatorSyncSupport != null &&
                participant.animatorSyncSupport.TryGetValue(monitoringSupport.animatorParameterName, out var paramInfo))
            {
                monitoringSupport.isAnimatorParameterSyncd_Cached = paramInfo.isSyncd;
                monitoringSupport.isAnimatorParameterSyncd_CacheInitialized = true;
            }
            else
            {
                // Parameter not found - default to not synced (skip it)
                monitoringSupport.isAnimatorParameterSyncd_Cached = false;
                monitoringSupport.isAnimatorParameterSyncd_CacheInitialized = true;
            }
        }

        static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            const string EM = "Error Message: ";
            const string ST = "\nError Stacktrace:\n";
            GONetLog.Error(string.Concat(EM, e.Exception.Message, ST, e.Exception.StackTrace));
        }

        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            const string EM = "Error Message: ";
            const string ST = "\nError Stacktrace:\n";
            Exception exception = (e.ExceptionObject as Exception);
            GONetLog.Error(string.Concat(EM, exception.Message, ST, exception.StackTrace));
        }

        #region public methods

        #region instantiate special support

        /// <summary>
        /// <para>This is the option to instantiate/spawn something that uses one original/prefab/template for the authority/owner/originator and a different one for everyone else (i.e., non-authorities).</para>
        /// <para>This is useful in some cases for instantiating/spawning things like players where the authority (i.e., the player) has certain scripts attached and only a model/mesh with arms and legs and non-authorities get less scripts and the full model/mesh.</para>
        /// <para>Only the authority/owner/originator can call this method (i.e., the resulting instance's <see cref="GONetParticipant.OwnerAuthorityId"/> will be set to <see cref="MyAuthorityId"/>).</para>
        /// <para>It operates within GONet just like <see cref="UnityEngine.Object.Instantiate{T}(T)"/>, where there is automatic spawn propagation support to all other machines in this game/session on the network.</para>
        /// <para>However, the difference is using this method ensures the other non-owner (networked) parties automatically instantiate <paramref name="nonAuthorityAlternateOriginal"/> instead of <paramref name="authorityOriginal"/>, which will be instantiated here for the authority/owner.</para>
        /// <para>Therefore, if you simply want to instantiate something across the network and it should be the same original <see cref="UnityEngine.Object"/> template, then use <see cref=""/></para>
        /// </summary>
        /// <param name="authorityOriginal"></param>
        /// <param name="nonAuthorityAlternateOriginal"></param>
        /// <returns></returns>
        public static GONetParticipant Instantiate_WithNonAuthorityAlternate(GONetParticipant authorityOriginal, GONetParticipant nonAuthorityAlternateOriginal)
        {
            return GONetSpawnSupport_Runtime.Instantiate_WithNonAuthorityAlternate(authorityOriginal, nonAuthorityAlternateOriginal);
        }

        /// <summary>
        /// <para>This is the option to instantiate/spawn something that uses one original/prefab/template for the authority/owner/originator and a different one for everyone else (i.e., non-authorities).</para>
        /// <para>This is useful in some cases for instantiating/spawning things like players where the authority (i.e., the player) has certain scripts attached and only a model/mesh with arms and legs and non-authorities get less scripts and the full model/mesh.</para>
        /// <para>Only the authority/owner/originator can call this method (i.e., the resulting instance's <see cref="GONetParticipant.OwnerAuthorityId"/> will be set to <see cref="MyAuthorityId"/>).</para>
        /// <para>It operates within GONet just like <see cref="UnityEngine.Object.Instantiate{T}(T)"/>, where there is automatic spawn propagation support to all other machines in this game/session on the network.</para>
        /// <para>However, the difference is using this method ensures the other non-owner (networked) parties automatically instantiate <paramref name="nonAuthorityAlternateOriginal"/> instead of <paramref name="authorityOriginal"/>, which will be instantiated here for the authority/owner.</para>
        /// <para>Therefore, if you simply want to instantiate something across the network and it should be the same original <see cref="UnityEngine.Object"/> template, then use <see cref=""/></para>
        /// </summary>
        /// <param name="authorityOriginal"></param>
        /// <param name="nonAuthorityAlternateOriginal"></param>
        /// <returns></returns>
        public static GONetParticipant Instantiate_WithNonAuthorityAlternate(GONetParticipant authorityOriginal, GONetParticipant nonAuthorityAlternateOriginal, Vector3 position, Quaternion rotation)
        {
            return GONetSpawnSupport_Runtime.Instantiate_WithNonAuthorityAlternate(authorityOriginal, nonAuthorityAlternateOriginal, position, rotation);
        }

        /// <summary>
        /// <para>
        /// This is mainly here to support player controlled <see cref="GONetParticipant"/>s (GNPs) in a strict server authoritative setup where a client/player only submits inputs to have
        /// the server process remotely and hopefully manipulate this GNP.
        /// See <see cref="GONetParticipant.RemotelyControlledByAuthorityId"/> and <see cref="GONetParticipant.IsMine_ToRemotelyControl"/>.
        /// IMPORTANT: send to server to immediately assume ownership over, which will yield this being always at 0 latency (i.e., all values will be extrapolated to match server)!!!
        /// </para>
        /// <para>
        /// IMPORTANT: This could be used for projectiles too even if the client instantiating it will not control it after the initial "birth" of it.
        /// </para>
        /// </summary>
        /// <summary>
        /// CLIENT ONLY: Legacy API for backward compatibility.
        /// Internally delegates to Client_TryInstantiateToBeRemotelyControlledByMe() with default limbo fallback.
        ///
        /// RECOMMENDED: Use Client_TryInstantiateToBeRemotelyControlledByMe() for explicit control over batch exhaustion handling.
        /// </summary>
        public static GONetParticipant Client_InstantiateToBeRemotelyControlledByMe(GONetParticipant prefab, Vector3 position, Quaternion rotation)
        {
            if (IsClient)
            {
                // Delegate to new batch-aware API with default limbo mode
                // This ensures old code still works but uses the batch system correctly
                if (Client_TryInstantiateToBeRemotelyControlledByMe(prefab, position, rotation, out GONetParticipant participant))
                {
                    return participant;
                }

                // Batch exhausted and limbo mode is ReturnFailure
                GONetLog.Error($"[GONet] Failed to spawn '{prefab.name}' - batch exhausted and limbo mode is ReturnFailure. " +
                              "Consider using Client_TryInstantiateToBeRemotelyControlledByMe() for explicit handling.");
                return null;
            }

            return null;
        }

        /// <summary>
        /// CLIENT ONLY: Try to instantiate a new server-owned object.
        /// Uses GONetId batch system with limbo mode fallback for batch exhaustion.
        ///
        /// IMPORTANT: Limbo is an EDGE CASE for extreme rapid spawning (100+ spawns/sec).
        /// Most games will NEVER encounter this - batches are designed to prevent it.
        ///
        /// <para>
        /// This is the RECOMMENDED API for client-spawned, server-owned objects (e.g., projectiles).
        /// Provides explicit failure handling via Try pattern instead of dangerous fallback code.
        /// </para>
        ///
        /// <para>
        /// Behavior when GONetId batch is exhausted:
        /// - ReturnFailure: Returns false, user handles spawn failure
        /// - InstantiateInLimboWithAutoDisableAll: Spawns with all MonoBehaviours disabled
        /// - InstantiateInLimboWithAutoDisableRenderingAndPhysics: Spawns with only rendering/physics disabled (RECOMMENDED)
        /// - InstantiateInLimbo: Spawns normally, user checks Client_IsInLimbo
        /// </para>
        ///
        /// <para>
        /// When batch arrives from server, limbo objects automatically "graduate" to full networked status.
        /// </para>
        /// </summary>
        /// <param name="prefab">The GONetParticipant prefab to instantiate</param>
        /// <param name="position">Position to spawn at</param>
        /// <param name="rotation">Rotation to spawn with</param>
        /// <param name="outParticipant">The instantiated participant (null if ReturnFailure and batch exhausted)</param>
        /// <returns>True if spawned successfully (either with GONetId or in limbo), false if spawn failed</returns>
        public static bool Client_TryInstantiateToBeRemotelyControlledByMe(
            GONetParticipant prefab,
            Vector3 position,
            Quaternion rotation,
            out GONetParticipant outParticipant)
        {
            outParticipant = null;

            if (!IsClient)
            {
                GONetLog.Error("[ClientLimbo] Client_TryInstantiate called on server - this is client-only API");
                return false;
            }

            // Check if we have available batch IDs
            bool hasBatchIds = GONetIdBatchManager.Client_HasAvailableIds();

            if (hasBatchIds)
            {
                // Normal path: We have batch IDs available
                outParticipant = GONetSpawnSupport_Runtime.Instantiate_MarkToBeRemotelyControlled(prefab, position, rotation);
                outParticipant.RemotelyControlledByAuthorityId = MyAuthorityId;
                return true;
            }

            // BATCH EXHAUSTED: Determine limbo mode
            Client_GONetIdBatchLimboMode limboMode = Client_GetLimboMode(prefab);

            if (limboMode == Client_GONetIdBatchLimboMode.ReturnFailure)
            {
                // User wants explicit failure - return false
                uint remainingIds = GONetIdBatchManager.Client_GetRemainingIds();
                GONetLog.Warning($"[ClientLimbo] Spawn FAILED for '{prefab.name}' - no batch IDs available (remaining: {remainingIds}), limbo mode is ReturnFailure");

                // Raise event so user can show UI notification
                Client_OnSpawnEnteredLimbo?.Invoke(new Client_SpawnLimboEventArgs
                {
                    Participant = null,
                    Prefab = prefab,
                    LimboMode = limboMode,
                    RemainingIds = remainingIds,
                    Position = position,
                    Rotation = rotation
                });

                return false;
            }

            // Spawn in limbo according to configured mode
            outParticipant = Client_InstantiateInLimbo(prefab, position, rotation, limboMode);
            return true;
        }

        /// <summary>
        /// CLIENT ONLY: Determines which limbo mode to use for a given prefab.
        /// Checks prefab override first, then falls back to project settings default.
        /// </summary>
        private static Client_GONetIdBatchLimboMode Client_GetLimboMode(GONetParticipant prefab)
        {
            if (prefab.client_overrideLimboMode)
            {
                return prefab.client_limboMode;
            }

            // TODO: Read from GONetProjectSettings once implemented
            // return GONetProjectSettings.Instance.client_defaultLimboMode;

            // Hardcoded default for now (most balanced option)
            return Client_GONetIdBatchLimboMode.InstantiateInLimboWithAutoDisableRenderingAndPhysics;
        }

        #endregion

        /// <summary>
        /// Searches for <see cref="GONetParticipant"/> by <paramref name="gonetId"/> and checks against <see cref="GONetParticipant.GONetId"/> and <see cref="GONetParticipant.GONetIdAtInstantiation"/>.
        /// </summary>
        /// <returns>null if not found</returns>
        public static GONetParticipant GetGONetParticipantById(uint gonetId)
        {
            GONetParticipant gonetParticipant = null;
            if (!gonetParticipantByGONetIdMap.TryGetValue(gonetId, out gonetParticipant))
            {
                gonetParticipantByGONetIdAtInstantiationMap.TryGetValue(gonetId, out gonetParticipant);
            }
            if (gonetParticipant == null && GONetConfig.EnableParticipantLookupRecovery)
            {
                gonetParticipant = TryRecoverMissingGONetParticipant(gonetId);
            }
            return gonetParticipant;
        }

        private static GONetParticipant TryRecoverMissingGONetParticipant(uint gonetId)
        {
            if (gonetId == GONetParticipant.GONetId_Unset)
            {
                return null;
            }

            if (!Application.isPlaying || !IsUnityMainThread)
            {
                return null;
            }

            if (TryConsumeDespawnTombstone(gonetId))
            {
                if (GONetConfig.LogParticipantMapDiagnostics)
                {
                    GONetLog.Debug($"[LOOKUP-RECOVERY] Skipping recovery for despawned GONetId {gonetId}");
                }
                return null;
            }

            long nowTicks = Time.RawElapsedTicks;
            if (missingGONetParticipantRecoveryLastAttempt.TryGetValue(gonetId, out long lastAttempt) &&
                nowTicks > lastAttempt &&
                (nowTicks - lastAttempt) < MissingParticipantRecoveryRetryTicks)
            {
                return null;
            }
            missingGONetParticipantRecoveryLastAttempt[gonetId] = nowTicks;

            GONetParticipant[] participants = Resources.FindObjectsOfTypeAll<GONetParticipant>();
            GONetParticipant recovered = null;
            for (int i = 0; i < participants.Length; i++)
            {
                GONetParticipant candidate = participants[i];
                if (candidate == null || candidate.gameObject == null)
                {
                    continue;
                }

                // Ignore prefab assets and hidden editor objects.
                if (!candidate.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (candidate.GONetId == gonetId || candidate.GONetIdAtInstantiation == gonetId)
                {
                    recovered = candidate;
                    break;
                }
            }

            if (recovered == null)
            {
                return null;
            }

            missingGONetParticipantRecoveryLastAttempt.Remove(gonetId);
            RegisterRecoveredParticipant(recovered, gonetId);
            return recovered;
        }

        private static void RegisterRecoveredParticipant(GONetParticipant gonetParticipant, uint requestedGONetId)
        {
            if (gonetParticipant == null)
            {
                return;
            }

            if (gonetParticipant.GONetId != GONetParticipant.GONetId_Unset)
            {
                gonetParticipantByGONetIdMap[gonetParticipant.GONetId] = gonetParticipant;
            }
            if (gonetParticipant.GONetIdAtInstantiation != GONetParticipant.GONetId_Unset)
            {
                gonetParticipantByGONetIdAtInstantiationMap[gonetParticipant.GONetIdAtInstantiation] = gonetParticipant;
            }

            // CRITICAL FIX (January 2026): Always attempt sync companion re-registration after LOOKUP-RECOVERY.
            //
            // Previous code only registered if isActiveAndEnabled was true, which caused late-joiners to miss
            // objects that were recovered but inactive (e.g., items held by players). The issue:
            // 1. Object reparented to inactive hierarchy (e.g., pickup) → OnDisable → sync companion removed
            // 2. LOOKUP-RECOVERY finds the object and re-adds to lookup maps
            // 3. OLD: Skip sync companion registration because object is inactive
            // 4. Late-joiner connects → Server_SendClientCurrentState_AllAutoMagicalSync_Coroutine iterates
            //    activeAutoSyncCompanionsByCodeGenerationIdMap → recovered object NOT included → late-joiner
            //    never receives GONetId assignment for scene objects
            // 5. Server sends reparent events/AllValues → late-joiner doesn't have the object → timeout errors
            //
            // FIX: Register sync companion even for inactive objects. This ensures:
            // - Late-joiners receive AllValues including GONetId assignment for scene-defined objects
            // - The object's current values are correctly tracked even while inactive
            // - When the object becomes active again, sync resumes seamlessly
            //
            // Note: EnsureAutoMagicalSyncCompanionRegistered checks DidStartMonitoringForAutoMagicalNetworking,
            // which was set to false in OnDisable_StopMonitoringForAutoMagicalNetworking, so re-registration proceeds.
            EnsureAutoMagicalSyncCompanionRegistered(gonetParticipant, "Lookup recovery");

            // Verify the sync companion was actually registered (important for late-joiner sync)
            var syncCompanion = GetSyncCompanionByGNP(gonetParticipant);
            bool companionRegistered = syncCompanion != null;

            if (GONetConfig.LogParticipantMapDiagnostics)
            {
                string path = HierarchyUtils.GetFullUniquePath(gonetParticipant.gameObject);
                GONetLog.Warning($"[LOOKUP-RECOVERY] Restored participant lookup for GONetId {requestedGONetId} -> '{gonetParticipant.name}' " +
                                 $"path='{path}' active={gonetParticipant.isActiveAndEnabled} syncCompanionRegistered={companionRegistered}");
            }

            if (!companionRegistered)
            {
                // This shouldn't happen, but if it does, log a warning to help diagnose
                GONetLog.Warning($"[LOOKUP-RECOVERY] Sync companion NOT registered for recovered participant " +
                                 $"GONetId={requestedGONetId} '{gonetParticipant.name}' - late-joiners may miss this object. " +
                                 $"CodeGenerationId={gonetParticipant.CodeGenerationId}, DidStartMonitoring={gonetParticipant.DidStartMonitoringForAutoMagicalNetworking}");
            }
        }

        /// <summary>
        /// This can be called from multiple threads....the final send will be done on yet another thread - <see cref="SendBytes_EndOfTheLine_AllSendsMUSTComeHere_SeparateThread"/>
        /// IMPORTANT: *IF* this method is called and <paramref name="channelId"/> is associated with <see cref="QosType.Unreliable"/> *AND* the
        ///            outbound buffer is full, it will NOT be queued up nor sent in which case false is returned.
        /// </summary>
        public static bool SendBytesToRemoteConnections(byte[] bytes, int bytesUsedCount, GONetChannelId channelId)
        {
            return SendBytesToRemoteConnection(null, bytes, bytesUsedCount, channelId); // passing null will result in sending to all remote connections
        }

        /// <summary>
        /// As the server, send <paramref name="messageBytes"/> over <paramref name="channelId"/> to all connected clients except the one represented by <paramref name="sourceClientConnection"/>.
        /// </summary>
        private static void Server_SendBytesToNonSourceClients(byte[] messageBytes, int bytesUsedCount, GONetConnection sourceClientConnection, byte channelId)
        {
            uint count = _gonetServer.numConnections;

            // PHASE 2 FIX: Round-robin client processing to distribute server-side delay fairly
            int startIndex = _gonetServer.nextClientProcessingStartIndex;
            if (count > 0)
            {
                _gonetServer.nextClientProcessingStartIndex = (startIndex + 1) % (int)count;
            }

            for (int offset = 0; offset < count; ++offset)
            {
                int i = (startIndex + offset) % (int)count;
                GONetConnection_ServerToClient remoteClientConnection = _gonetServer.remoteClients[i].ConnectionToClient;
                if (remoteClientConnection.OwnerAuthorityId != sourceClientConnection.OwnerAuthorityId)
                {
                    SendBytesToRemoteConnection(remoteClientConnection, messageBytes, bytesUsedCount, channelId);
                }
            }
        }

        /// <summary>
        /// This can be called from multiple threads....the final send will be done on yet another thread - <see cref="SendBytes_EndOfTheLine_AllSendsMUSTComeHere_SeparateThread"/>
        /// IMPORTANT: *IF* this method is called and <paramref name="channelId"/> is associated with <see cref="QosType.Unreliable"/> *AND* the
        ///            outbound buffer is full, it will NOT be queued up nor sent in which case false is returned.
        /// </summary>
        private static bool SendBytesToRemoteConnection(GONetConnection sendToConnection, byte[] bytes, int bytesUsedCount, GONetChannelId channelId)
        {
            // HOST MODE FIX: Skip sending to loopback connection entirely.
            // The host already has ALL data locally - sending through loopback causes:
            // 1. Wasted CPU (serialize → deserialize same data in same process)
            // 2. Potential feedback loops (data forwarded to loopback gets re-processed)
            // 3. Unnecessary network traffic accounting (~700KB/s observed without this filter)
            // This single check eliminates ALL loopback traffic at the lowest level.
            if (sendToConnection is GONetConnection_ClientHostLoopback)
            {
                return true; // Return true to indicate "success" (no need to send - host has data)
            }

            SingleProducerQueues singleProducerSendQueues = ReturnSingleProducerResources_IfAppropriate(singleProducerSendQueuesByThread, Thread.CurrentThread);

            { // flow control:
                // SCENE LOADING SUPPRESSION (November 2025): Drop UNRELIABLE messages while client is loading a scene
                // CRITICAL FIX: Prevents RELIABLE sync bundle errors when client doesn't have GONetIds yet
                //
                // PROBLEM: Early-joiners receive sync bundles during scene load, before compressed GONetId RPC arrives
                // - Server enables continuous sync at client init (11.834s)
                // - Server loads scene, coroutine broadcasts GONetIds (14.382s → 15.301s, 3.5s delay!)
                // - Client receives 68+ RELIABLE sync bundles for objects without GONetIds
                // - Queue backup, processing bottleneck, 20-30 seconds of choppy experience
                //
                // SOLUTION: Track per-client scene loading state, DROP (not queue!) unreliable messages during load
                // - SceneLoadEvent published → mark clients as loading
                // - UNRELIABLE messages dropped (using FAST O(1) dictionary lookup)
                // - RELIABLE messages still sent (InitComplete, GONetId RPCs must get through!)
                // - TIME SYNC messages always allowed (critical for late-joiner initialization!)
                // - SceneLoadCompleteEvent received → clear loading flag → resume unreliable
                if (sendToConnection != null && IsServer && gonetServer != null &&
                    GONetChannel.ById(channelId).QualityOfService == QosType.Unreliable &&
                    channelId != GONetChannel.TimeSync_Unreliable) // CRITICAL: Allow time sync during scene loading!
                {
                    // FAST O(1) lookup - no linear search like before!
                    if (gonetServer.TryGetRemoteClientByAuthorityId(sendToConnection.OwnerAuthorityId, out GONetRemoteClient remoteClient))
                    {
                        if (remoteClient.IsCurrentlyLoadingScene)
                        {
                            // DROP unreliable messages while client is loading scene
                            // Client can't process sync bundles without GONetIds anyway!
                            // Don't log every drop (too spammy), only first one
                            GONetLog.Debug($"[SCENE-LOADING-DROP] Dropping unreliable message for client {sendToConnection.OwnerAuthorityId} (loading scene '{remoteClient.CurrentlyLoadingSceneName}')");
                            return false;
                        }
                    }
                }

                // PER-CLIENT BACKPRESSURE (November 2025): Monitor reliable queue depth, suppress unreliable when backing up
                // CRITICAL FIX: Prevents late-joiner initialization failures when 800+ objects are actively syncing
                //
                // PROBLEM: Early-joiners work (connect when scene quiet), late-joiners fail (unreliable flood blocks reliable InitComplete)
                // SOLUTION: Per-client congestion tracking → drop unreliable when reliable queue > watermark → allows InitComplete through
                //
                // EXCEPTION (December 2025): Time sync messages MUST flow even during backpressure!
                // Time sync is critical for initialization - without it, all subsequent sync is broken.
                // High-latency clients need time sync to complete BEFORE heavy state sync for best experience.
                if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableLateJoinerBackpressure &&
                    sendToConnection != null &&
                    GONetChannel.ById(channelId).QualityOfService == QosType.Unreliable &&
                    channelId != GONetChannel.TimeSync_Unreliable) // CRITICAL: Allow time sync during backpressure!
                {
                    ClientCongestionState congestionState = GetOrCreateCongestionState(sendToConnection.OwnerAuthorityId);

                    // Update reliable queue depth (throttled to once per frame per client to avoid excessive parsing)
                    long currentTicks = Time.ElapsedTicks;
                    long ticksSinceLastCheck = currentTicks - congestionState.lastCheckTicks;
                    long ticksPerFrame = TimeSpan.TicksPerSecond / 60; // ~16ms at 60fps

                    if (ticksSinceLastCheck >= ticksPerFrame)
                    {
                        UpdateClientCongestionState(sendToConnection, congestionState);
                        congestionState.lastCheckTicks = currentTicks;
                    }

                    // If unreliable traffic is suppressed for this client, DROP this message (with optional trickle)
                    if (congestionState.isUnreliableSuppressed)
                    {
                        bool allowTrickle = false;
                        if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableBackpressureUnreliableTrickle)
                        {
                            // ADAPTIVE TRICKLE: Use per-client adaptive interval if enabled and available
                            int intervalMs = (GONetGlobal.Instance.enableAdaptiveTrickle && congestionState.currentAdaptiveTrickleIntervalMs > 0)
                                ? congestionState.currentAdaptiveTrickleIntervalMs
                                : GONetGlobal.Instance.backpressureUnreliableTrickleIntervalMs;
                            if (intervalMs > 0)
                            {
                                long minIntervalTicks = intervalMs * TimeSpan.TicksPerMillisecond;
                                long nowTicks = Time.ElapsedTicks;

                                // TRICKLE BATCH SIZE: Calculate effective batch size
                                // 0 = dynamic auto-sizing based on GONetParticipants
                                // >0 = explicit manual setting
                                int configuredBatchSize = GONetGlobal.Instance.trickleUnreliableBatchSize;
                                int effectiveBatchSize;
                                if (configuredBatchSize <= 0)
                                {
                                    // DYNAMIC SIZING: max(20, TotalParticipants / 8)
                                    // Target: Full sync loop in ~1.5 seconds at 200ms trickle interval
                                    // 800 objects / 8 = 100 batch. At 5Hz (200ms), 500 updates/sec → 1.6s full loop
                                    // Old formula (/80) gave batch=10 → 16 seconds to sync. WAY too slow.
                                    int participantCount = gonetParticipantByGONetIdMap.Count;
                                    effectiveBatchSize = Math.Max(20, participantCount / 8);
                                }
                                else
                                {
                                    effectiveBatchSize = configuredBatchSize;
                                }

                                // Check if new interval has started OR still within batch limit of current interval
                                bool isNewInterval = congestionState.lastUnreliableTrickleTicks < 0 ||
                                    nowTicks - congestionState.lastUnreliableTrickleTicks >= minIntervalTicks;

                                if (isNewInterval)
                                {
                                    // NEW INTERVAL: Reset batch count and allow first packet
                                    congestionState.lastUnreliableTrickleTicks = nowTicks;
                                    congestionState.currentTrickleBatchCount = 1;
                                    congestionState.totalUnreliableTrickleSent++;
                                    allowTrickle = true;

                                    bool shouldLogTrickle = congestionState.totalUnreliableTrickleSent == 1 ||
                                                            ((congestionState.totalUnreliableTrickleSent % 100 == 0) && GONetGlobal.Instance.enableCongestionStateLogging);
                                    if (shouldLogTrickle)
                                    {
                                        GONetLog.Info($"[BACKPRESSURE-TRICKLE] New interval: Allowing unreliable batch for client {sendToConnection.OwnerAuthorityId} " +
                                                      $"(interval {intervalMs}ms, batch 1/{effectiveBatchSize}, reliable queue: {congestionState.reliableQueueDepth})");
                                    }
                                }
                                else if (congestionState.currentTrickleBatchCount < effectiveBatchSize)
                                {
                                    // WITHIN INTERVAL + BATCH LIMIT: Allow additional packet in this batch
                                    congestionState.currentTrickleBatchCount++;
                                    congestionState.totalUnreliableTrickleSent++;
                                    allowTrickle = true;

                                    // Log every 10th batch packet if congestion logging is enabled
                                    if (congestionState.currentTrickleBatchCount == effectiveBatchSize && GONetGlobal.Instance.enableCongestionStateLogging)
                                    {
                                        GONetLog.Info($"[BACKPRESSURE-TRICKLE] Batch complete for client {sendToConnection.OwnerAuthorityId} " +
                                                      $"(batch {congestionState.currentTrickleBatchCount}/{effectiveBatchSize})");
                                    }
                                }
                                // else: WITHIN INTERVAL but BATCH EXHAUSTED: allowTrickle remains false, packet will be dropped
                            }
                        }

                        if (!allowTrickle)
                        {
                            congestionState.totalUnreliableDropped++;
                            _totalBackpressureDrops++;

                            // CRITICAL DIAGNOSTIC (November 20, 2025): ALWAYS log first drop to detect incorrect suppression
                            // This helps diagnose "objects not moving" issues even when congestion logging is disabled
                            bool shouldLog = (congestionState.totalUnreliableDropped == 1) || // Always log first drop
                                            ((congestionState.totalUnreliableDropped % 100 == 1) && GONetGlobal.Instance.enableCongestionStateLogging);

                            if (shouldLog)
                            {
                                GONetLog.Warning($"[BACKPRESSURE] ⚠️ Dropping unreliable message for client {sendToConnection.OwnerAuthorityId} (reliable queue: {congestionState.reliableQueueDepth}, total dropped: {congestionState.totalUnreliableDropped})");
                            }

                            return false; // Drop unreliable message
                        }
                    }
                }

                // ADAPTIVE POOL SIZING (October 2025): Use dynamic pool size from adaptive scaler
                // Pool automatically scales based on demand while respecting absolute maximum
                if (GONetChannel.ById(channelId).QualityOfService == QosType.Unreliable)
                {
                    // Get current effective pool size (dynamically adjusted by adaptive scaler)
                    int currentPoolSize = adaptivePoolScaler != null
                        ? adaptivePoolScaler.GetCurrentPoolSize()
                        : (GONetGlobal.Instance != null ? GONetGlobal.Instance.maxPacketsPerTick : SingleProducerQueues.MAX_PACKETS_PER_TICK);

                    float threshold = GONetGlobal.Instance != null ? GONetGlobal.Instance.unreliableDropThreshold : 0.90f;
                    int dropThresholdCount = (int)(currentPoolSize * threshold);

                    if (singleProducerSendQueues.resourcePool.BorrowedCount > dropThresholdCount)
                    {
                        // Track unreliable packet drops for diagnostics
                        _unreliablePacketDropCount++;
                        _unreliablePacketDropCount_sinceLastLog++;

                        // Log periodically (every 100 drops or first drop) to avoid spam
                        // ONLY log if congestion logging is enabled (configurable in GONetGlobal)
                        bool shouldLog = (_unreliablePacketDropCount_sinceLastLog >= 100 || _unreliablePacketDropCount == 1) &&
                                        (GONetGlobal.Instance == null || GONetGlobal.Instance.enableCongestionLogging);

                        if (shouldLog)
                        {
                            string channelName = GONetChannel.ById(channelId) == GONetChannel.AutoMagicalSync_Unreliable ? "AutoMagicalSync" :
                                               GONetChannel.ById(channelId) == GONetChannel.TimeSync_Unreliable ? "TimeSync" :
                                               GONetChannel.ById(channelId) == GONetChannel.EventSingles_Unreliable ? "EventSingles" :
                                               GONetChannel.ById(channelId) == GONetChannel.CustomSerialization_Unreliable ? "CustomSerialization" : "Unknown";

                            int borrowed = singleProducerSendQueues.resourcePool.BorrowedCount;
                            float utilization = (float)borrowed / currentPoolSize;
                            float dropRate = (float)_unreliablePacketDropCount / (_unreliablePacketDropCount + _successfulPacketSendCount);

                            // Get adaptive scaler diagnostics if available
                            string adaptiveInfo = adaptivePoolScaler != null ? adaptivePoolScaler.GetDiagnostics() : "";

                            // Build diagnostic message with actionable recommendations
                            string message = $"[CONGESTION] Unreliable packet drops detected!\n" +
                                           $"  Dropped: {_unreliablePacketDropCount_sinceLastLog} packets (this batch), {_unreliablePacketDropCount} total\n" +
                                           $"  Drop Rate: {dropRate:P} ({_unreliablePacketDropCount} dropped / {_unreliablePacketDropCount + _successfulPacketSendCount} total)\n" +
                                           $"  Pool Utilization: {borrowed}/{currentPoolSize} ({utilization:P})\n" +
                                           $"  Drop Threshold: {threshold:P}\n" +
                                           $"  Channel: {channelName}\n" +
                                           $"  Connection: {(sendToConnection == null ? "ALL (broadcast)" : sendToConnection.ToString())}\n" +
                                           (string.IsNullOrEmpty(adaptiveInfo) ? "" : $"  {adaptiveInfo}\n");

                            // Add severity-based recommendations
                            if (dropRate > 0.10f) // Critical: >10% drop rate
                            {
                                bool isAdaptiveEnabled = GONetGlobal.Instance != null && GONetGlobal.Instance.enableAdaptivePoolScaling;
                                message += $"\n⚠️ CRITICAL CONGESTION ({dropRate:P} drop rate):\n" +
                                          "  IMMEDIATE ACTIONS:\n" +
                                          (isAdaptiveEnabled
                                              ? $"  1. Adaptive scaling is ENABLED - pool is at {currentPoolSize} packets\n" +
                                                "     If this is close to maxPacketsPerTick ceiling, increase the ceiling!\n"
                                              : $"  1. Adaptive scaling is DISABLED - using fixed size: {currentPoolSize}\n" +
                                                "     Enable adaptive scaling or increase maxPacketsPerTick\n") +
                                          "  2. Check for spawn storms (too many objects spawned at once)\n" +
                                          "  3. Reduce sync frequency in GONetParticipant sync profiles\n" +
                                          $"  4. If '{channelName}' is AutoMagicalSync, consider:\n" +
                                          "     - Using less frequent position/rotation sync\n" +
                                          "     - Disabling sync on distant/irrelevant objects\n";
                            }
                            else if (dropRate > 0.05f) // Warning: >5% drop rate
                            {
                                message += $"\n⚠️ HIGH CONGESTION ({dropRate:P} drop rate):\n" +
                                          "  RECOMMENDED ACTIONS:\n" +
                                          $"  1. Current pool size: {currentPoolSize} (check if adaptive scaling is working)\n" +
                                          "  2. Monitor for spawn rate spikes\n" +
                                          "  3. Review sync profiles for over-syncing\n";
                            }
                            else // Moderate: <5% drop rate
                            {
                                message += $"\n  STATUS: Moderate congestion ({dropRate:P} drop rate)\n" +
                                          "  This is typically acceptable for burst scenarios.\n" +
                                          "  If drops persist, consider increasing maxPacketsPerTick.\n";
                            }

                            GONetLog.Warning(message);
                            _unreliablePacketDropCount_sinceLastLog = 0;
                        }

                        return false;
                    }
                }
            }

            // INIT MESSAGE TRACKING (SERVER-SIDE): Record sent init messages for delivery validation
            // Added 2025-01-21 to detect reliable message delivery failures (Steamworks send buffer issues, etc)
            // See: .claude/STEAMWORKS_INIT_MESSAGE_DELIVERY_SOLUTION.md
            // IMPORTANT: Only track RELIABLE init channels (8 and 9) to match client-side tracking
            // IMPORTANT: Must use lock for thread safety - SendBytesToRemoteConnection can be called from multiple threads
            if (IsServer && gonetServer != null && GONetChannel.IsChannelTrackedForInitValidation(channelId))
            {
                var serverToClientConnection = sendToConnection as GONetConnection_ServerToClient;
                if (serverToClientConnection != null && serverToClientConnection.OwnerAuthorityId != OwnerAuthorityId_Unset)
                {
                    ushort clientAuthorityId = serverToClientConnection.OwnerAuthorityId;
                    bool isTrackingActive = true;
                    if (gonetServer.TryGetRemoteClientByAuthorityId(clientAuthorityId, out GONetRemoteClient remoteClient))
                    {
                        isTrackingActive = remoteClient.IsInitMessageTrackingActive;
                    }

                    if (isTrackingActive)
                    {
                        GONetInitMessageTracker tracker = gonetServer.GetOrCreateInitMessageTracker(clientAuthorityId);

                        // Only track if client hasn't acknowledged yet
                        // Double-check inside lock to prevent race conditions
                        if (!tracker.Acknowledged)
                        {
                            lock (tracker)
                            {
                                if (!tracker.Acknowledged)
                                {
                                    tracker.RecordMessageSent(channelId, bytes, bytesUsedCount, Time.ElapsedTicks);
                                }
                            }
                        }
                    }
                }
            }

            byte[] bytesCopy = singleProducerSendQueues.resourcePool.Borrow(bytesUsedCount);
            Buffer.BlockCopy(bytes, 0, bytesCopy, 0, bytesUsedCount);

            NetworkData networkData = new NetworkData()
            {
                messageBytesBorrowedOnThread = Thread.CurrentThread,
                messageBytes = bytesCopy,
                bytesUsedCount = bytesUsedCount,
                relatedConnection = sendToConnection,
                channelId = channelId
            };

            // DIAGNOSTIC (December 2025): Log spawn events at enqueue for aliasing detection
            // DIAGNOSTIC (December 2025): Log spawn-sized messages being enqueued
            // To enable, add GONet_SPAWN_TRACE to Player Settings → Scripting Define Symbols
            #if GONet_SPAWN_TRACE
            if (channelId == GONetChannel.EventSingles_Reliable && bytesUsedCount >= 60)
            {
                uint extractedGONetId = 0;
                if (bytesUsedCount >= 8)
                {
                    extractedGONetId = (uint)(
                        bytesCopy[4] | (bytesCopy[5] << 8) | (bytesCopy[6] << 16) | (bytesCopy[7] << 24)
                    );
                }
                string firstBytes = bytesUsedCount >= 12
                    ? System.BitConverter.ToString(bytesCopy, 0, 12).Replace("-", "")
                    : System.BitConverter.ToString(bytesCopy, 0, bytesUsedCount).Replace("-", "");
                GONetLog.Debug($"[SPAWN-ENQUEUE] MAIN→queue: bytes={bytesUsedCount}, GONetId={extractedGONetId}, firstBytes={firstBytes}");
            }
            #endif

            // DIAGNOSTIC: SceneLoadComplete trace - Stage 2: Enqueued to send queue
            // COMMENTED (log cleanup) - fires frequently on reliable channel messages
            /*if (EnableSceneLoadCompleteTracing && channelId == GONetChannel.EventSingles_Reliable && bytesUsedCount >= 20 && bytesUsedCount <= 40)
            {
                // SceneLoadCompleteEvent is typically 25 bytes - check for it
                string hex = System.BitConverter.ToString(bytesCopy, 0, Math.Min(bytesUsedCount, 16)).Replace("-", "");
                GONetLog.Info($"[SLC-TRACE-2] STAGE2_ENQUEUE bytes={bytesUsedCount} channel={channelId} hex={hex} thread={Thread.CurrentThread.ManagedThreadId} queueSize={singleProducerSendQueues.queueForWork.Count} time={Time.ElapsedSeconds:F3}");
            }*/

            singleProducerSendQueues.queueForWork.Enqueue(networkData);

            // DIAGNOSTIC: Track outgoing packet by channel
            // Added 2025-10-11 to investigate packet saturation during rapid spawning
            GONetChannel outgoingChannel = GONetChannel.ById(channelId);
            bool isOutgoingReliable = outgoingChannel.QualityOfService == QosType.Reliable;
            IncrementOutgoingPacketCounter(isOutgoingReliable);

            // Track successful packet sends for drop rate calculation
            System.Threading.Interlocked.Increment(ref _successfulPacketSendCount);

            return true;
        }

        /// <summary>
        /// POST: if there is no entry for <paramref name="producerThread"/> in <paramref name="singleProducerQueuesByThread"/>, a new one is instantiated and added.
        /// </summary>
        private static SingleProducerQueues ReturnSingleProducerResources_IfAppropriate(ConcurrentDictionary<Thread, SingleProducerQueues> singleProducerQueuesByThread, Thread producerThread)
        {
            SingleProducerQueues singleProducerQueues;
            if (singleProducerQueuesByThread.TryGetValue(producerThread, out singleProducerQueues))
            {
                int processedCount = 0;
                int readyCount = singleProducerQueues.queueForPostWorkResourceReturn.Count;
                NetworkData readyToReturn;
                while (processedCount < readyCount && singleProducerQueues.queueForPostWorkResourceReturn.TryDequeue(out readyToReturn))
                {
                    singleProducerQueues.resourcePool.Return(readyToReturn.messageBytes); // since we now know we are on the correct thread (i.e.., same as borrowed on) we can return it to pool
                    ++processedCount;
                }

                if (processedCount < readyCount)
                {
                    GONetLog.Warning($"Not sure why, but there were {readyCount} items ready to be returned to resource pool, but we only returned {processedCount}.");
                }
            }
            else
            {
                singleProducerQueuesByThread[producerThread] = singleProducerQueues = new SingleProducerQueues();
            }

            return singleProducerQueues;
        }

        #region Comprehensive Message Flow Logging

        /// <summary>
        /// Logging profile name for message flow logging.
        /// Profile must be registered before logging can occur.
        /// Example:
        ///   GONetLog.RegisterLoggingProfile(new GONetLog.LoggingProfile(
        ///       GONetMain.MessageFlowLoggingProfile,
        ///       outputToSeparateFile: true,
        ///       includeStackTraces: false,
        ///       minimumLogLevel: GONetLog.LogLevel.Info));
        /// </summary>
        public const string MessageFlowLoggingProfile = "MessageFlow";

        /// <summary>
        /// Controls whether comprehensive message flow logging is enabled.
        /// WARNING: Generates large log output even without stack traces.
        /// Default: false (disabled) - enable only for targeted debugging sessions.
        /// NOTE: Profile must be registered separately via GONetLog.RegisterLoggingProfile()
        /// </summary>
        public static bool EnableMessageFlowLogging { get; set; } = false;

        /// <summary>
        /// Extracts metadata from message bytes for logging purposes.
        /// Thread-safe. Returns partial data if deserialization fails.
        /// </summary>
        private static string ExtractMessageMetadata(byte[] messageBytes, int bytesUsedCount, GONetChannelId channelId)
        {
            try
            {
                // Try to extract messageID and type from the bytes
                if (bytesUsedCount >= 4 && GONetChannel.IsGONetCoreChannel(channelId))
                {
                    using (var bitStream = BitByBitByteArrayBuilder.GetBuilder_WithNewData(messageBytes, bytesUsedCount))
                    {
                        uint messageID;
                        bitStream.ReadUInt(out messageID);

                        Type messageType;
                        if (messageTypeByMessageIDMap.TryGetValue(messageID, out messageType))
                        {
                            long elapsedTicksAtSend;
                            bitStream.ReadLong(out elapsedTicksAtSend);

                            // Try to extract GONetId if this is an event that has one
                            string gonetIdInfo = string.Empty;
                            if (messageType == typeof(InstantiateGONetParticipantEvent) ||
                                messageType == typeof(AutoMagicalSync_ValueChanges_Message) ||
                                messageType == typeof(AutoMagicalSync_AllCurrentValues_Message))
                            {
                                try
                                {
                                    // These message types should have GONetId in their payload
                                    // We'll just note it exists rather than fully parse to avoid side effects
                                    gonetIdInfo = " [ContainsGONetIds]";
                                }
                                catch
                                {
                                    // Ignore extraction failures
                                }
                            }

                            return $"MsgType={messageType.Name}, MsgID={messageID}, SentTicks={elapsedTicksAtSend}{gonetIdInfo}";
                        }

                        return $"MsgID={messageID} [TypeUnknown]";
                    }
                }
                else if (channelId == GONetChannel.EventSingles_Reliable || channelId == GONetChannel.EventSingles_Unreliable || channelId == GONetChannel.ClientInitialization_EventSingles_Reliable)
                {
                    return $"EventSingle [Channel={channelId}]";
                }
                else
                {
                    return $"CustomChannel={channelId}";
                }
            }
            catch (Exception e)
            {
                return $"[MetadataExtractionFailed: {e.Message}]";
            }
        }

        /// <summary>
        /// Extracts compact buffer statistics from a connection.
        /// NOTE: Stats are per-CONNECTION, not per-channel. All channels on this connection share these buffers.
        /// Returns format: "Buf=5/1024 Q=12/256" or "N/A" if stats unavailable.
        /// </summary>
        private static string GetCompactChannelBufferStats(GONetConnection connection, GONetChannelId channelId)
        {
            if (connection == null)
            {
                return "N/A";
            }

            try
            {
                string fullStats = connection.GetUsageStatistics();
                if (string.IsNullOrEmpty(fullStats))
                {
                    return "NoStats";
                }

                // DEBUG: Log raw stats on first call to understand format
                if (!_hasLoggedRawStatsFormat)
                {
                    _hasLoggedRawStatsFormat = true;
                    GONetLog.Warning($"[CHANNEL-STATS-DEBUG] Raw GetUsageStatistics() output:\n{fullStats}");
                }

                // Parse single-line space-separated format
                // Example: "RTTMilliseconds: 0 PacketLoss: 0 ... sendBuffer.Size: 1024 sendBufferUtilization: 1 messageQueue.Count: 0"

                string sendBufValue = "?";
                string sendBufSize = "1024";
                string queueValue = "?";

                // Extract sendBuffer.Size (max capacity)
                int sendBufSizeIndex = fullStats.IndexOf("sendBuffer.Size:");
                if (sendBufSizeIndex >= 0)
                {
                    int colonPos = sendBufSizeIndex + "sendBuffer.Size:".Length;
                    int valueStart = colonPos;
                    while (valueStart < fullStats.Length && char.IsWhiteSpace(fullStats[valueStart]))
                        valueStart++;

                    int valueEnd = valueStart;
                    while (valueEnd < fullStats.Length && !char.IsWhiteSpace(fullStats[valueEnd]))
                        valueEnd++;

                    if (valueEnd > valueStart)
                        sendBufSize = fullStats.Substring(valueStart, valueEnd - valueStart);
                }

                // Extract sendBufferUtilization (current count)
                int sendBufIndex = fullStats.IndexOf("sendBufferUtilization:");
                if (sendBufIndex >= 0)
                {
                    int colonPos = sendBufIndex + "sendBufferUtilization:".Length;
                    int valueStart = colonPos;
                    while (valueStart < fullStats.Length && char.IsWhiteSpace(fullStats[valueStart]))
                        valueStart++;

                    int valueEnd = valueStart;
                    while (valueEnd < fullStats.Length && !char.IsWhiteSpace(fullStats[valueEnd]))
                        valueEnd++;

                    if (valueEnd > valueStart)
                        sendBufValue = fullStats.Substring(valueStart, valueEnd - valueStart);
                }

                // Extract messageQueue.Count
                int queueIndex = fullStats.IndexOf("messageQueue.Count:");
                if (queueIndex >= 0)
                {
                    int colonPos = queueIndex + "messageQueue.Count:".Length;
                    int valueStart = colonPos;
                    while (valueStart < fullStats.Length && char.IsWhiteSpace(fullStats[valueStart]))
                        valueStart++;

                    int valueEnd = valueStart;
                    while (valueEnd < fullStats.Length && !char.IsWhiteSpace(fullStats[valueEnd]))
                        valueEnd++;

                    if (valueEnd > valueStart)
                        queueValue = fullStats.Substring(valueStart, valueEnd - valueStart);
                }

                // Return compact format: Buf=current/max Q=count
                return $"Buf={sendBufValue}/{sendBufSize} Q={queueValue}";
            }
            catch (Exception ex)
            {
                return $"ERR:{ex.GetType().Name}";
            }
        }

        private static bool _hasLoggedRawStatsFormat = false;

        /// <summary>
        /// Controls whether broadcast buffer stats show first client only (false) or all clients (true).
        /// Default: false (first client only) to keep logs concise.
        /// </summary>
        private static bool _showAllClientBufferStatsForBroadcast = false;

        /// <summary>
        /// Logs comprehensive send-side metadata for message flow debugging.
        /// Thread-safe. Called from background send thread.
        /// NOTE: Disabled by default - set EnableMessageFlowLogging = true to enable
        /// </summary>
        private static void LogMessageSend(byte[] messageBytes, int bytesUsedCount, GONetChannelId channelId, GONetConnection targetConnection, bool isServerBroadcast)
        {
            if (!EnableMessageFlowLogging) return; // Exit early if disabled

            try
            {
                long sendTimestamp = Time.ElapsedTicks;
                string sceneHistory = GetSceneHistory();
                string metadata = ExtractMessageMetadata(messageBytes, bytesUsedCount, channelId);

                string target;
                string bufferStats;

                if (isServerBroadcast)
                {
                    target = "ALL_CLIENTS";

                    // Get buffer stats from clients for broadcast messages
                    if (_gonetServer != null && _gonetServer.remoteClients != null && _gonetServer.remoteClients.Count > 0)
                    {
                        if (_showAllClientBufferStatsForBroadcast)
                        {
                            // Show all clients' buffer stats
                            System.Text.StringBuilder sb = new System.Text.StringBuilder();
                            sb.Append("[");
                            for (int i = 0; i < _gonetServer.remoteClients.Count; i++)
                            {
                                var client = _gonetServer.remoteClients[i];
                                if (client != null && client.ConnectionToClient != null)
                                {
                                    if (i > 0) sb.Append(" ");
                                    string stats = GetCompactChannelBufferStats(client.ConnectionToClient, channelId);
                                    sb.Append($"C{client.ConnectionToClient.OwnerAuthorityId}:{stats}");
                                }
                            }
                            sb.Append("]");
                            bufferStats = sb.ToString();
                        }
                        else
                        {
                            // Show first client's buffer stats only (default)
                            var firstClient = _gonetServer.remoteClients[0];
                            if (firstClient != null && firstClient.ConnectionToClient != null)
                            {
                                string stats = GetCompactChannelBufferStats(firstClient.ConnectionToClient, channelId);
                                bufferStats = $"{stats} (Client{firstClient.ConnectionToClient.OwnerAuthorityId})";
                            }
                            else
                            {
                                bufferStats = "BROADCAST (no valid clients)";
                            }
                        }
                    }
                    else
                    {
                        bufferStats = "BROADCAST (no clients)";
                    }
                }
                else if (targetConnection != null)
                {
                    ushort targetAuthority = targetConnection is GONetConnection_ServerToClient serverToClient
                        ? serverToClient.OwnerAuthorityId
                        : (ushort)0;
                    target = $"Authority{targetAuthority}";
                    bufferStats = GetCompactChannelBufferStats(targetConnection, channelId);
                }
                else
                {
                    target = "SERVER";
                    bufferStats = "N/A";
                }

                // Use logging profile (no stack traces if profile configured that way)
                string logMessage = $"[MSG-SEND] {sceneHistory} | SendTicks={sendTimestamp} | Source=Authority{MyAuthorityId} | Target={target} | Ch={channelId} | Bytes={bytesUsedCount} | {bufferStats} | {metadata}";
                GONetLog.Info(logMessage, MessageFlowLoggingProfile);
            }
            catch (Exception e)
            {
                GONetLog.Error($"[MSG-SEND] Logging failed: {e.Message}");
            }
        }

        /// <summary>
        /// Logs comprehensive receive-side metadata for message flow debugging.
        /// Thread-safe. Called from main thread during message processing.
        /// NOTE: Disabled by default - set EnableMessageFlowLogging = true to enable
        /// </summary>
        private static void LogMessageReceive(byte[] messageBytes, int bytesUsedCount, GONetChannelId channelId, GONetConnection sourceConnection, long elapsedTicksAtSend)
        {
            if (!EnableMessageFlowLogging) return; // Exit early if disabled

            try
            {
                long receiveTimestamp = Time.ElapsedTicks;
                long latencyTicks = receiveTimestamp - elapsedTicksAtSend;
                double latencyMs = (latencyTicks * GONet.Utils.HighResolutionTimeUtils.TICKS_TO_SECONDS) * 1000.0;

                string sceneHistory = GetSceneHistory();
                string metadata = ExtractMessageMetadata(messageBytes, bytesUsedCount, channelId);

                ushort sourceAuthority = sourceConnection is GONetConnection_ServerToClient serverToClient
                    ? serverToClient.OwnerAuthorityId
                    : (ushort)0;

                string bufferStats = GetCompactChannelBufferStats(sourceConnection, channelId);

                // Use logging profile (no stack traces if profile configured that way)
                string logMessage = $"[MSG-RECV] {sceneHistory} | RecvTicks={receiveTimestamp} | Source=Authority{sourceAuthority} | Target=Authority{MyAuthorityId} | Ch={channelId} | Bytes={bytesUsedCount} | Latency={latencyMs:F2}ms | {bufferStats} | {metadata}";
                GONetLog.Info(logMessage, MessageFlowLoggingProfile);
            }
            catch (Exception e)
            {
                GONetLog.Error($"[MSG-RECV] Logging failed: {e.Message}");
            }
        }

        /// <summary>
        /// Logs comprehensive process-side metadata for OnGONetReady event broadcasting.
        /// Thread-safe. Called from main thread during event publishing.
        /// NOTE: Disabled by default - set EnableMessageFlowLogging = true to enable
        /// </summary>
        private static void LogEventProcess(GONetParticipant participant, int behaviourCount)
        {
            if (!EnableMessageFlowLogging) return; // Exit early if disabled

            try
            {
                long processTimestamp = Time.ElapsedTicks;
                string sceneHistory = GetSceneHistory();

                // Use logging profile (no stack traces if profile configured that way)
                string logMessage = $"[MSG-PROC] {sceneHistory} | ProcTicks={processTimestamp} | Event=OnGONetReady | GONetId={participant.GONetId} | Name={participant.name} | IsMine={participant.IsMine} | Owner=Authority{participant.OwnerAuthorityId} | BehaviourCount={behaviourCount}";
                GONetLog.Info(logMessage, MessageFlowLoggingProfile);
            }
            catch (Exception e)
            {
                GONetLog.Error($"[MSG-PROC] Logging failed: {e.Message}");
            }
        }

        #endregion

        internal static ulong tickCount_endOfTheLineSend_Thread;
        private static volatile bool isRunning_endOfTheLineSend_Thread;

#if !PERF_NO_PROCESS_SYNC_EVENTS
        internal static ulong tickCount_databaseSave_Thread;
        private static volatile bool isRunning_databaseSave_Thread;
#endif


        /// <summary>
        /// HIGH PRIORITY THREAD: Network sends only (no file I/O).
        /// Renamed from SendBytes_EndOfTheLine_AllSendsAndSavesMUSTComeHere_SeparateThread.
        /// Save operations moved to dedicated lower-priority thread <see cref="DatabaseSave_SeparateThread"/>.
        /// </summary>
        private static void SendBytes_EndOfTheLine_AllSendsMUSTComeHere_SeparateThread()
        {
            tickCount_endOfTheLineSend_Thread = 0;

            while (isRunning_endOfTheLineSend_Thread)
            {
                bool didWork = false;

                // CPU TIME-BOXING: Reset stopwatch at start of each iteration
                _sendThreadProcessingStopwatch.Restart();

                try
                {
                    { // Do send stuffs
                        var sendThreads = singleProducerSendQueuesByThread.Keys;
                        foreach (var sendThread in sendThreads)
                        {
                            SingleProducerQueues singleProducerSendQueues = singleProducerSendQueuesByThread[sendThread];
                            ConcurrentQueue<NetworkData> endOfTheLineSendQueue = singleProducerSendQueues.queueForWork;
                            int processedCount = 0;
                            int readyCount = endOfTheLineSendQueue.Count;

                            // SEND-SIDE TEMPORAL THINNING: DUAL TRIGGER SYSTEM
                            // Trigger 1: Queue count exceeds threshold (burst traffic protection)
                            // Trigger 2: Processing time exceeds CPU budget (frame stutter protection)
                            bool shouldThinByCount = readyCount > (GONetGlobal.Instance != null ? GONetGlobal.Instance.sendQueueThinningTriggerCount : 200);
                            bool shouldThinByCpu = false;

                            if (GONetGlobal.Instance != null && GONetGlobal.Instance.queueProcessingCpuBudgetMs > 0f)
                            {
                                double elapsedMs = _sendThreadProcessingStopwatch.Elapsed.TotalMilliseconds;
                                shouldThinByCpu = elapsedMs > GONetGlobal.Instance.queueProcessingCpuBudgetMs;
                            }

                            if (GONetGlobal.Instance != null && GONetGlobal.Instance.enableTemporalThinning && (shouldThinByCount || shouldThinByCpu))
                            {
                                string triggerReason = shouldThinByCount && shouldThinByCpu ? "COUNT + CPU" :
                                                      shouldThinByCount ? "COUNT" : "CPU";

                                // Calculate congestion severity for adaptive thinning
                                double congestionSeverity = 1.0;
                                if (shouldThinByCount)
                                {
                                    congestionSeverity = (double)readyCount / (GONetGlobal.Instance != null ? GONetGlobal.Instance.sendQueueThinningTriggerCount : 200);
                                }
                                else if (shouldThinByCpu)
                                {
                                    double elapsedMs = _sendThreadProcessingStopwatch.Elapsed.TotalMilliseconds;
                                    congestionSeverity = elapsedMs / GONetGlobal.Instance.queueProcessingCpuBudgetMs;
                                }

                                if (shouldThinByCpu && GONetGlobal.Instance.enableCongestionLogging)
                                {
                                    GONetLog.Warning($"[CPU-TRIGGER] Send queue processing exceeded budget: {_sendThreadProcessingStopwatch.Elapsed.TotalMilliseconds:F2}ms > {GONetGlobal.Instance.queueProcessingCpuBudgetMs}ms (overage: {congestionSeverity:F1}x, triggering thinning via {triggerReason})");
                                }
                                else if (shouldThinByCount && GONetGlobal.Instance.enableCongestionLogging)
                                {
                                    GONetLog.Warning($"[COUNT-TRIGGER] Send queue has {readyCount} messages (threshold: {GONetGlobal.Instance.sendQueueThinningTriggerCount}, overage: {congestionSeverity:F1}x)");
                                }

                                ThinSendQueue(endOfTheLineSendQueue, singleProducerSendQueues, congestionSeverity);
                                readyCount = endOfTheLineSendQueue.Count; // Update count after thinning
                            }

                            NetworkData networkData;
                            while (processedCount < readyCount && endOfTheLineSendQueue.TryDequeue(out networkData))
                            {
                                if (networkData.relatedConnection == null)
                                {
                                    if (IsServer)
                                    {
                                        // HANDOFF FIX (Dec 2025): Protect against race during demotion.
                                        // Server could be stopped between the IsServer check and SendBytesToAllClients call.
                                        var serverRef = _gonetServer; // Capture reference to avoid race
                                        if (serverRef != null)
                                        {
                                           //GONetLog.Debug("sending something....my seconds: " + Time.ElapsedSeconds + " size: " + networkData.bytesUsedCount);

                                            try
                                            {
                                                serverRef.SendBytesToAllClients(networkData.messageBytes, networkData.bytesUsedCount, networkData.channelId);

                                                // COMPREHENSIVE LOGGING - Send to all clients
                                                LogMessageSend(networkData.messageBytes, networkData.bytesUsedCount, networkData.channelId, null, isServerBroadcast: true);
                                            }
                                            catch (InvalidOperationException)
                                            {
                                                // Expected during handoff transitions - server state changed between check and send
                                            }
                                        }
                                    }
                                    else
                                    {
                                        if (GONetClient != null)
                                        {
                                            while (isRunning_endOfTheLineSend_Thread && !GONetClient.IsConnectedToServer)
                                            {
                                                const string SLEEP = "SLEEP!  I am not connected right now.  I have data to send, but need to wait to be connected in order to send it.";
                                                //GONetLog.Info(SLEEP);

                                                Thread.Sleep(33); // TODO FIXME I am sure things will eventually get into strange states out in the wild where clients spotty network puts them here too often and I wonder if this is problematic...certainly quick/dirty and nieve!
                                            }

                                            if (isRunning_endOfTheLineSend_Thread)
                                            {
                                                // DIAGNOSTIC (December 2025): Log spawn events at transport layer with buffer content verification
                                                // To enable, add GONet_SPAWN_TRACE to Player Settings → Scripting Define Symbols
                                                #if GONet_SPAWN_TRACE
                                                if (networkData.channelId == GONetChannel.EventSingles_Reliable && networkData.bytesUsedCount >= 60)
                                                {
                                                    // Extract GONetId from spawn message for aliasing detection
                                                    // Spawn event structure: eventType(2) + flags(2) + GONetId(4) = GONetId at offset 4
                                                    uint extractedGONetId = 0;
                                                    if (networkData.bytesUsedCount >= 8)
                                                    {
                                                        extractedGONetId = (uint)(
                                                            networkData.messageBytes[4] |
                                                            (networkData.messageBytes[5] << 8) |
                                                            (networkData.messageBytes[6] << 16) |
                                                            (networkData.messageBytes[7] << 24)
                                                        );
                                                    }
                                                    string firstBytes = networkData.bytesUsedCount >= 12
                                                        ? System.BitConverter.ToString(networkData.messageBytes, 0, 12).Replace("-", "")
                                                        : System.BitConverter.ToString(networkData.messageBytes, 0, networkData.bytesUsedCount).Replace("-", "");
                                                    GONetLog.Debug($"[SPAWN-TRANSPORT] CLIENT dequeue→send: bytes={networkData.bytesUsedCount}, GONetId={extractedGONetId}, channel={networkData.channelId}, firstBytes={firstBytes}");
                                                }
                                                #endif

                                                // DIAGNOSTIC: SceneLoadComplete trace - Stage 3: Dequeued by send thread
                                                // COMMENTED (log cleanup) - fires frequently on reliable channel messages
                                                /*if (EnableSceneLoadCompleteTracing && networkData.channelId == GONetChannel.EventSingles_Reliable &&
                                                    networkData.bytesUsedCount >= 20 && networkData.bytesUsedCount <= 40)
                                                {
                                                    string hex = System.BitConverter.ToString(networkData.messageBytes, 0, Math.Min(networkData.bytesUsedCount, 16)).Replace("-", "");
                                                    GONetLog.Info($"[SLC-TRACE-3] STAGE3_DEQUEUE bytes={networkData.bytesUsedCount} channel={networkData.channelId} hex={hex} thread={Thread.CurrentThread.ManagedThreadId} connected={GONetClient?.IsConnectedToServer} time={Time.ElapsedSeconds:F3}");
                                                }*/

                                                //GONetLog.Debug("sending something....my seconds: " + Time.ElapsedSeconds + " size: " + networkData.bytesUsedCount);

                                                // HANDOFF FIX (Dec 2025): Protect against connection state changes during handoff.
                                                // During voluntary/involuntary handoff, GONetClient reference or its internal state
                                                // can change between the IsConnectedToServer check and the actual send, causing
                                                // InvalidOperationException. Catch and skip gracefully to allow sync to resume
                                                // after handoff completes.
                                                try
                                                {
                                                    var clientRef = GONetClient; // Capture reference to avoid race
                                                    if (clientRef != null && clientRef.IsConnectedToServer)
                                                    {
                                                        clientRef.SendBytesToServer(networkData.messageBytes, networkData.bytesUsedCount, networkData.channelId);

                                                        // COMPREHENSIVE LOGGING - Client to server
                                                        LogMessageSend(networkData.messageBytes, networkData.bytesUsedCount, networkData.channelId, null, isServerBroadcast: false);
                                                    }
                                                }
                                                catch (InvalidOperationException)
                                                {
                                                    // Expected during handoff transitions - connection state changed between check and send
                                                    // Data will be re-synced after handoff completes
                                                }
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    // HOST MODE FIX: Skip loopback connection - host already has all data locally
                                    if (!(networkData.relatedConnection is GONetConnection_ClientHostLoopback))
                                    {
                                        // HANDOFF FIX (Dec 2025): After demotion, we may still have queued messages
                                        // with GONetConnection_ServerToClient connections from when we were the server.
                                        // Skip these since we can no longer send via server-to-client connections.
                                        if (networkData.relatedConnection is GONetConnection_ServerToClient && !IsServer)
                                        {
                                            // Silently discard - we've demoted and can no longer use server connections
                                            // The new server will handle sending to these clients
                                        }
                                        else
                                        {
                                            //GONetLog.Debug("sending something....my seconds: " + Time.ElapsedSeconds + " size: " + networkData.bytesUsedCount);
                                            try
                                            {
                                                // SLOT RESERVATION (December 2025): Use channel-based priority
                                                // System priority channels (scene loads, init, authority) bypass Gameplay slot limits
                                                var priority = GONetChannel.GetMessagePriority(networkData.channelId);
                                                networkData.relatedConnection.SendMessageOverChannel(networkData.messageBytes, networkData.bytesUsedCount, networkData.channelId, priority);

                                                // COMPREHENSIVE LOGGING - Targeted send
                                                LogMessageSend(networkData.messageBytes, networkData.bytesUsedCount, networkData.channelId, networkData.relatedConnection, isServerBroadcast: false);
                                            }
                                            catch (InvalidOperationException)
                                            {
                                                // Expected during handoff transitions - connection state changed between check and send
                                            }
                                        }
                                    }
                                }

                                { // set things up so the byte[] on networkData can be returned to the proper pool AND on the proper thread on which is was initially borrowed!
                                    singleProducerSendQueues.queueForPostWorkResourceReturn.Enqueue(networkData);
                                }

                                ++processedCount;
                                didWork = true;
                            }

                            if (processedCount < readyCount)
                            {
                                GONetLog.Warning($"Not sure why, but there were {readyCount} items ready to be processed, but we only processed {processedCount}.");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    GONetLog.Error(string.Concat("Unexpected error attempting to process sends in separate thread.  Exception.Type: ", e.GetType().Name, " Exception.Message: ", e.Message, " \nException.StackTrace: ", e.StackTrace));
                }
                finally
                {
                    ++tickCount_endOfTheLineSend_Thread;

                    // Sleep if idle to prevent busy-spin CPU burn (same fix as socket read thread)
                    if (!didWork)
                    {
                        Thread.Sleep(1); // Yields CPU when no work available (~0.5-2ms latency, minimal overhead)
                    }
                }
            }
        }

#if !PERF_NO_PROCESS_SYNC_EVENTS
        /// <summary>
        /// LOW PRIORITY THREAD: Database saves only (file I/O operations).
        /// Separated from send thread to prevent blocking high-priority network operations.
        /// Processes save queues independently without impacting send latency.
        /// NOTE: Only compiled when PERF_NO_PROCESS_SYNC_EVENTS is NOT defined (saves enabled).
        /// </summary>
        private static void DatabaseSave_SeparateThread()
        {
            tickCount_databaseSave_Thread = 0;

            while (isRunning_databaseSave_Thread)
            {
                bool didWork = false;

                try
                {
                    { // Do save stuffs (moved from send thread to eliminate file I/O blocking)
                        var syncvEventsEnumerator = syncEventsToSaveQueueByEventType.GetEnumerator();
                        while (syncvEventsEnumerator.MoveNext())
                        {
                            SyncEventsSaveSupport saveSupport = syncvEventsEnumerator.Current.Value;
                            if (saveSupport.queue_needsSaving.Count > 0 && saveSupport.IsSaving)
                            {
                                lock (saveSupport.queue_needsSaving)
                                {
                                    AppendToDatabaseFile_SaveThread(saveSupport.queue_needsSaving); // this is the act of saving...after this, they no longer need saving
                                }
                                saveSupport.OnAfterAllSaved_SaveThread();
                                didWork = true;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    GONetLog.Error(string.Concat("Unexpected error attempting to process database saves in separate thread.  Exception.Type: ", e.GetType().Name, " Exception.Message: ", e.Message, " \nException.StackTrace: ", e.StackTrace));
                }
                finally
                {
                    ++tickCount_databaseSave_Thread;

                    // Sleep if idle to prevent busy-spin CPU burn (lower priority = longer sleep OK)
                    if (!didWork)
                    {
                        Thread.Sleep(10); // Longer sleep than send thread - saves are lower priority
                    }
                }
            }
        }
#endif

        #endregion

        #region internal methods

        static readonly SyncBundleUniqueGrouping grouping_endOfFrame_reliable = new SyncBundleUniqueGrouping(AutoMagicalSyncFrequencies.END_OF_FRAME_IN_WHICH_CHANGE_OCCURS_SECONDS, AutoMagicalSyncReliability.Reliable, false);
        static readonly SyncBundleUniqueGrouping grouping_endOfFrame_unreliable = new SyncBundleUniqueGrouping(AutoMagicalSyncFrequencies.END_OF_FRAME_IN_WHICH_CHANGE_OCCURS_SECONDS, AutoMagicalSyncReliability.Unreliable, false);

        /// <summary>
        /// Physics sync grouping - Used for server-authoritative Rigidbody synchronization.
        /// Runs via WaitForFixedUpdate coroutine AFTER all physics processing (simulation + collision/trigger callbacks).
        /// Uses END_OF_FRAME frequency (0f) so ProcessASAP() executes EVERY FixedUpdate without frequency throttling.
        /// Physics objects are filtered at call site in AutoMagicalSyncProcessing.Process() using IsRigidBodyOwnerOnlyControlled flag.
        /// Uses FixedElapsedTicks timestamps and unreliable channel for frequent physics updates (actual rate: 50Hz via FixedUpdate).
        /// </summary>
        internal static readonly SyncBundleUniqueGrouping grouping_physics_unreliable = new SyncBundleUniqueGrouping(AutoMagicalSyncFrequencies.END_OF_FRAME_IN_WHICH_CHANGE_OCCURS_SECONDS, AutoMagicalSyncReliability.Unreliable, true);

        /// <summary>
        /// Physics frame counter for physics sync frequency gating.
        /// Incremented every FixedUpdate to track which physics frame we're on.
        /// Used with PhysicsUpdateInterval (1-4) to determine if this frame should sync physics state.
        /// </summary>
        private static int physicsFrameCounter = 0;

        static Thread endOfLineSendThread; // Renamed from endOfLineSendAndSaveThread (HIGH priority - sends only)
#if !PERF_NO_PROCESS_SYNC_EVENTS
        static Thread databaseSaveThread; // NEW (LOW priority - database saves only)
#endif

        /// <summary>
        /// Should only be called from <see cref="GONetGlobal"/> once per Unity <see cref="MonoBehaviour"/> Update cycle.
        /// </summary>
        internal static void Update(GONetBehaviour coroutineManager)
        {
            Time.Update(); // This is the important thing to execute as early in a frame as possible (hence the -32000 setting in Script Execution Order) to get more accurate network timing to match Unity's frame time as it relates to values changing

            EventBus.PublishQueuedEventsForMainThread();

            // Pooling system update (server init + deferred event processing)
            GONetPoolManager.Update();
            ProcessPendingAutoSyncCompanionRecovery();

            if (myLocal == null) // NOTE: This check is important since it will eventually call Update_DoTheHeavyLifting_IfAppropriate, which is also called regularly from MyLocal.LateUpdate and we only want to process this during the time MyLocal is not present (i.e., since it is instantiated after start-up)
            {
                coroutineManager.StartCoroutine(Update_EndOfFrame());
            }

            // GONet v2: Multi-rate scheduler + shadow buffer apply
            Update_SoA_DynamicMultiRate();

            // Health monitor - detect stuck objects, log periodic health reports
            SoA_ObjectHealthMonitor.Update();

            // EARLY FRAME UPDATE: Call UpdateAfterGONetReady for all ready companions
            // Runs at end of GONetGlobal.Update() (priority -32000, early in frame)
            Update_EarlyFrame_UpdateAfterGONetReady();

            // CRITICAL FIX (December 2025): Process DistributedHost channel messages BEFORE gossip update.
            // Without this, SessionPromote messages from the peer are stuck in the network thread's queue
            // until end-of-frame processing. The failover manager runs early in frame (here), so it never
            // sees the SessionPromote until the NEXT frame - causing split-brain where both clients promote.
            ProcessIncomingBytes_DistributedHostOnly_MainThread();

            // Update distributed host gossip system if enabled
            DistributedHost.GONetGossipIntegration.Update();

            // Update reparenting system - process pending reparents and auto-publish
            Update_Reparenting();
        }

        /// <summary>
        /// GONet v2: Multi-rate scheduler for Structure-of-Arrays blending.
        /// Hz-agnostic - iterates ALL discovered streams dynamically.
        /// Kicks off Burst-compiled blending jobs at different Hz for each stream.
        /// Then applies shadow buffers to Transforms in batched writes.
        /// </summary>
        private static void Update_SoA_DynamicMultiRate()
        {
            if (!SoAData.IsInitialized)
                return;

            // Feature flag: Use unified SoA blending pipeline
            if (GONetFeatureFlags.UseUnifiedSoABlending)
            {
                // Unified path: Schedule jobs early in frame (Update, priority -32000)
                // Jobs run on worker threads while main thread continues
                // Completion + Apply happens late in frame (LateUpdate, priority +32000)
                // via Update_CompleteSoABlendingJobs() called from Update_DoTheHeavyLifting_IfAppropriate()
                // CRITICAL: Subtract valueBlendingBufferLeadTicks to target interpolation within buffer
                // instead of extrapolating into the future (which causes jitter)
                SoA_BlendingPipeline.ScheduleBlendingJobs(ref SoAData, Time.ElapsedTicks - valueBlendingBufferLeadTicks);
                return;
            }

            // Legacy path: Hz-based scheduling with separate job kicks
            double currentTime = Time.ElapsedSeconds;

            // Iterate all Vector3 streams (positions) - Hz-agnostic
            if (SoAData.positionStreams != null)
            {
                for (int i = 0; i < SoAData.positionStreamInfos.Length; i++)
                {
                    var streamInfo = SoAData.positionStreamInfos[i];
                    if (currentTime >= streamInfo.nextUpdateTime)
                    {
                        KickBlendJob_Vector3(i);

                        streamInfo.nextUpdateTime = currentTime + streamInfo.updateInterval;
                        SoAData.positionStreamInfos[i] = streamInfo;
                    }
                }
            }

            // Iterate all Quaternion streams (rotations) - Hz-agnostic
            if (SoAData.rotationStreams != null)
            {
                for (int i = 0; i < SoAData.rotationStreamInfos.Length; i++)
                {
                    var streamInfo = SoAData.rotationStreamInfos[i];
                    if (currentTime >= streamInfo.nextUpdateTime)
                    {
                        KickBlendJob_Quaternion(i);

                        streamInfo.nextUpdateTime = currentTime + streamInfo.updateInterval;
                        SoAData.rotationStreamInfos[i] = streamInfo;
                    }
                }
            }

            // Iterate all Scalar streams - Hz-agnostic
            if (SoAData.scalarStreams != null)
            {
                for (int i = 0; i < SoAData.scalarStreamInfos.Length; i++)
                {
                    var streamInfo = SoAData.scalarStreamInfos[i];
                    if (currentTime >= streamInfo.nextUpdateTime)
                    {
                        KickBlendJob_Scalar(i);

                        streamInfo.nextUpdateTime = currentTime + streamInfo.updateInterval;
                        SoAData.scalarStreamInfos[i] = streamInfo;
                    }
                }
            }

            // Apply shadow buffers to Transforms (batched writes)
            ApplyShadowBuffersToTransforms();
        }

        /// <summary>
        /// GONet v2: Kick Vector3 (position) blend job for a specific stream.
        /// Hz-agnostic - works with ANY update rate.
        /// </summary>
        /// <param name="streamIndex">Index into SoAData.positionStreams array</param>
        private static void KickBlendJob_Vector3(int streamIndex)
        {
            var stream = SoAData.positionStreams[streamIndex]; // Read-only access - no need for ref

            if (stream.activeCount == 0)
                return; // No objects in this stream

            // Create Burst-compiled blending job
            var job = new BlendPositionsJob
            {
                // Input: Ring buffer samples
                posX = stream.posX,
                posY = stream.posY,
                posZ = stream.posZ,
                posTicks = stream.posTicks,
                historyCount = stream.historyCount,
                isActive = stream.isActive,

                // Output: Shadow buffer (blended results)
                shadowPos = SoAData.GetCurrentShadowPositions(),

                // Parameters - GONet high-resolution time
                targetElapsedTicks = Time.ElapsedTicks,
                ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond
            };

            // Schedule parallel job (process N objects in parallel across all cores)
            job.Schedule(stream.activeCount, 64).Complete();
        }

        /// <summary>
        /// GONet v2: Kick Quaternion (rotation) blend job for a specific stream.
        /// Hz-agnostic - works with ANY update rate.
        /// </summary>
        /// <param name="streamIndex">Index into SoAData.rotationStreams array</param>
        private static void KickBlendJob_Quaternion(int streamIndex)
        {
            var stream = SoAData.rotationStreams[streamIndex]; // Read-only access - no need for ref

            if (stream.activeCount == 0)
                return; // No objects in this stream

            var job = new BlendRotationsJob
            {
                // Input: Ring buffer samples
                rotX = stream.rotX,
                rotY = stream.rotY,
                rotZ = stream.rotZ,
                rotW = stream.rotW,
                rotTicks = stream.rotTicks,
                historyCount = stream.historyCount,
                isActive = stream.isActive,

                // Output: Shadow buffer
                shadowRot = SoAData.GetCurrentShadowRotations(),

                // Parameters - GONet high-resolution time
                targetElapsedTicks = Time.ElapsedTicks,
                ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond
            };

            job.Schedule(stream.activeCount, 64).Complete();
        }

        /// <summary>
        /// GONet v2: Kick Scalar blend job for a specific stream.
        /// Hz-agnostic - works with ANY update rate.
        /// </summary>
        /// <param name="streamIndex">Index into SoAData.scalarStreams array</param>
        private static void KickBlendJob_Scalar(int streamIndex)
        {
            var stream = SoAData.scalarStreams[streamIndex]; // Read-only access - no need for ref

            if (stream.activeCount == 0)
                return; // No objects in this stream

            // TODO: Scalars need custom output buffer and field-specific routing
            // For now, leave unimplemented - scalar blending requires more design work
            // (unlike Transform position/rotation which have dedicated shadow buffers)

            /*
            var job = new BlendScalarsJob
            {
                // Input: Ring buffer samples
                values = SoAData.scalar_24hz.values,
                valueTicks = SoAData.scalar_24hz.valueTicks,
                historyCount = SoAData.scalar_24hz.historyCount,
                isActive = SoAData.scalar_24hz.isActive,

                // Output: Blended scalars (needs custom output buffer)
                shadowValues = ???, // TODO: Need to create scalar shadow buffer

                // Parameters
                targetElapsedTicks = DateTime.UtcNow.Ticks,
                ticksToSeconds = 1.0 / TimeSpan.TicksPerSecond
            };

            job.Schedule(SoAData.scalar_24hz.activeCount, 64).Complete();
            */
        }

        /// <summary>
        /// GONet v2: Apply previous shadow buffer to Transforms, then flip buffers.
        /// Uses batched SetPositionAndRotation() for cache-friendly writes.
        /// 10-20× faster than individual position/rotation writes.
        /// </summary>
        /// <summary>
        /// GONet v2: Apply shadow buffer results to Transforms (Hz-agnostic).
        /// Reads from PREVIOUS shadow buffer (jobs wrote to CURRENT).
        /// Uses batched SetPositionAndRotation() for 10-20× speedup vs individual properties.
        /// </summary>
        private static unsafe void ApplyShadowBuffersToTransforms()
        {
            // Get PREVIOUS shadow buffer (blending jobs write to CURRENT, we read from PREVIOUS)
            NativeArray<Vector3> positions = SoAData.GetPreviousShadowPositions();
            NativeArray<Quaternion> rotations = SoAData.GetPreviousShadowRotations();

            // Apply to Transforms using batched SetPositionAndRotation
            // Iterate ALL position streams (Hz-agnostic)
            if (SoAData.positionStreams != null)
            {
                for (int streamIdx = 0; streamIdx < SoAData.positionStreams.Length; streamIdx++)
                {
                    var stream = SoAData.positionStreams[streamIdx]; // Read-only access - no need for ref

                    for (int objIdx = 0; objIdx < stream.activeCount; objIdx++)
                    {
                        // Check if object is still active
                        if (!stream.isActive[objIdx])
                            continue;

                        // REPARENTING FIX (Jan 2026): Skip if transform sync is suspended due to parenting.
                        // When a child is reparented, we suspend transform sync to prevent shadow buffer
                        // values from overwriting the local position/rotation offset set during reparenting.
                        if (stream.gonetIds.IsCreated && stream.gonetIds.Length > objIdx)
                        {
                            uint gonetId = stream.gonetIds[objIdx];
                            if (gonetId != 0 && gonetParticipantByGONetIdMap.TryGetValue(gonetId, out var gnp) && gnp != null)
                            {
                                if (gnp.IsTransformSyncSuspendedDueToParenting)
                                    continue;
                            }
                        }

                        // Get Transform pointer and apply batched update
                        IntPtr transformPtr = stream.transformPtrs[objIdx];
                        if (transformPtr != IntPtr.Zero)
                        {
                            Transform transform = (Transform)GCHandle.FromIntPtr(transformPtr).Target;
                            if (transform != null)
                            {
                                // Apply position + rotation from shadow buffers
                                // NOTE: Assumes position and rotation streams have same object ordering
                                transform.SetPositionAndRotation(positions[objIdx], rotations[objIdx]);
                            }
                        }
                    }
                }
            }

            // Swap shadow buffers for next frame (ping-pong)
            SoAData.SwapShadowBuffers();
        }

        /// <summary>
        /// GONet v2: Register non-authority object in appropriate SoA streams (Hz-agnostic).
        /// Called when OnGONetReady fires and IsMine=False.
        /// Looks up which streams this CodeGenId participates in based on sync profile Hz.
        /// </summary>
        private enum SoARegistrationResult
        {
            RegisteredTransform,
            RegisteredNonTransformOnly,
            AlreadyRegistered,
            SkippedNullParticipant,
            SkippedUnsetGONetId,
            SkippedSoANotInitialized,
            SkippedNoCompanion,
            SkippedNoBlendableMembers
        }

        internal static unsafe void RegisterObjectInSoA(GONetParticipant gonetParticipant)
        {
            TryRegisterObjectInSoA(gonetParticipant, out _);
        }

        private static unsafe bool TryRegisterObjectInSoA(GONetParticipant gonetParticipant, out SoARegistrationResult result)
        {
            if (gonetParticipant == null)
            {
                result = SoARegistrationResult.SkippedNullParticipant;
                return false;
            }

            // DEFENSIVE: Prevent duplicate registration which causes orphaned SoA entries
            // and stuck objects (first entry never receives DATA_IN, only lookup target does)
            if (gonetParticipant.v2_isRegisteredInSoA)
            {
                GONetLog.Warning($"[SoA] Attempted duplicate registration for '{gonetParticipant.name}' (GONetId {gonetParticipant.GONetId}) - skipping");
                Core.SoA_BlendingDiagnostics.LogDuplicateRegistrationBlocked(gonetParticipant.GONetId, gonetParticipant.name);
                result = SoARegistrationResult.AlreadyRegistered;
                return false;
            }

            if (!SoAData.IsInitialized)
            {
                GONetLog.Debug($"[SoA] Skipping registration for '{gonetParticipant.name}' (GONetId {gonetParticipant.GONetId}) - SoA not initialized");
                result = SoARegistrationResult.SkippedSoANotInitialized;
                return false;
            }

            uint gonetId = gonetParticipant.GONetId;
            if (gonetId == GONetParticipant.GONetId_Unset)
            {
                GONetLog.Debug($"[SoA] Skipping registration for '{gonetParticipant.name}' - GONetId is unset");
                result = SoARegistrationResult.SkippedUnsetGONetId;
                return false;
            }

            // CRITICAL: Complete any pending blending jobs BEFORE registration.
            // Registration may trigger Resize() which disposes NativeArrays.
            // If jobs are still running on worker threads using those arrays = CRASH.
            if (GONetFeatureFlags.UseUnifiedSoABlending && SoA_BlendingPipeline.HasPendingJobs)
            {
                SoA_BlendingPipeline.CompleteBlendingJobs(ref SoAData);
            }

            byte codeGenId = gonetParticipant.CodeGenerationId;

            // Get sync companion to determine Hz rates for this object
            var syncCompanion = GetSyncCompanionByGNP(gonetParticipant);
            if (syncCompanion == null)
            {
                // No sync companion = no auto-magical sync values (e.g., GONetGlobal singleton)
                // This is normal for some objects, so just return silently
                GONetLog.Debug($"[SoA] Skipping registration for '{gonetParticipant.name}' (CodeGenId {codeGenId}) - no sync companion");
                result = SoARegistrationResult.SkippedNoCompanion;
                return false;
            }

            bool registeredAny = false;
            bool registeredTransform = false;
            bool transformPtrUsed = false;
            bool companionPtrUsed = false;
            IntPtr transformPtr = IntPtr.Zero;
            IntPtr companionPtr = IntPtr.Zero;

            // Generalized registration: iterate ALL sync members and register by type
            for (int i = 0; i < syncCompanion.valuesChangesSupport.Length; i++)
            {
                var changesSupport = syncCompanion.valuesChangesSupport[i];
                if (changesSupport == null)
                    continue;

                // Check if blending is enabled for this member (respects sync profile settings)
                if (!changesSupport.syncAttribute_ShouldBlendBetweenValuesReceived)
                    continue;

                // Get member type from current value
                GONetSyncableValue currentValue = syncCompanion.GetAutoMagicalSyncValue((byte)i);
                float syncInterval = changesSupport.syncAttribute_SyncChangesEverySeconds;
                byte memberIndex = (byte)i;

                switch (currentValue.GONetSyncType)
                {
                    case GONetSyncableValueTypes.UnityEngine_Vector3:
                        if (transformPtr == IntPtr.Zero)
                        {
                            Transform transform = gonetParticipant.transform;
                            transformPtr = GCHandle.ToIntPtr(GCHandle.Alloc(transform, GCHandleType.Weak));
                        }
                        if (RegisterVector3InSoA(gonetParticipant, gonetId, memberIndex, syncInterval, transformPtr, changesSupport.memberName))
                        {
                            registeredAny = true;
                            transformPtrUsed = true;
                            if (IsTransformSyncMember(changesSupport.memberName))
                            {
                                registeredTransform = true;
                            }
                        }
                        break;

                    case GONetSyncableValueTypes.UnityEngine_Quaternion:
                        if (transformPtr == IntPtr.Zero)
                        {
                            Transform transform = gonetParticipant.transform;
                            transformPtr = GCHandle.ToIntPtr(GCHandle.Alloc(transform, GCHandleType.Weak));
                        }
                        if (RegisterQuaternionInSoA(gonetParticipant, gonetId, memberIndex, syncInterval, transformPtr, changesSupport.memberName))
                        {
                            registeredAny = true;
                            transformPtrUsed = true;
                            if (IsTransformSyncMember(changesSupport.memberName))
                            {
                                registeredTransform = true;
                            }
                        }
                        break;

                    case GONetSyncableValueTypes.UnityEngine_Vector2:
                        if (companionPtr == IntPtr.Zero)
                        {
                            companionPtr = GCHandle.ToIntPtr(GCHandle.Alloc(syncCompanion, GCHandleType.Weak));
                        }
                        if (RegisterVector2InSoA(gonetParticipant, gonetId, memberIndex, syncInterval, companionPtr, changesSupport.memberName))
                        {
                            registeredAny = true;
                            companionPtrUsed = true;
                        }
                        break;

                    case GONetSyncableValueTypes.UnityEngine_Vector4:
                        if (companionPtr == IntPtr.Zero)
                        {
                            companionPtr = GCHandle.ToIntPtr(GCHandle.Alloc(syncCompanion, GCHandleType.Weak));
                        }
                        if (RegisterVector4InSoA(gonetParticipant, gonetId, memberIndex, syncInterval, companionPtr, changesSupport.memberName))
                        {
                            registeredAny = true;
                            companionPtrUsed = true;
                        }
                        break;

                    // TODO: Add scalar types when SoA scalar blending is enabled
                    default:
                        // Other types (scalars, etc.) - future enhancement
                        break;
                }
            }

            if (!registeredAny)
            {
                if (transformPtr != IntPtr.Zero)
                {
                    GCHandle.FromIntPtr(transformPtr).Free();
                }
                if (companionPtr != IntPtr.Zero)
                {
                    GCHandle.FromIntPtr(companionPtr).Free();
                }

                GONetLog.Debug($"[SoA] Skipping registration for '{gonetParticipant.name}' (GONetId {gonetId}) - no blendable members");
                result = SoARegistrationResult.SkippedNoBlendableMembers;
                return false;
            }

            if (!transformPtrUsed && transformPtr != IntPtr.Zero)
            {
                GCHandle.FromIntPtr(transformPtr).Free();
            }

            if (!companionPtrUsed && companionPtr != IntPtr.Zero)
            {
                GCHandle.FromIntPtr(companionPtr).Free();
            }

            if (!registeredTransform)
            {
                result = SoARegistrationResult.RegisteredNonTransformOnly;
                return false;
            }

            // Mark as registered (enables v2 code path in InitSingle)
            gonetParticipant.v2_isRegisteredInSoA = true;

            // Notify health monitor and lifecycle tracker of registration
            bool isPhysicsObject = gonetParticipant.IsRigidBodyOwnerOnlyControlled && gonetParticipant.myRigidBody != null;
#if GONet_SOA_TRACE
            string role = IsServer ? "SVR" : "CLI";
            GONetLog.Debug($"[SoA-REG-TRACE] {role}|GONetId={gonetId}|name={gonetParticipant.name}|physics={isPhysicsObject}|HealthMonitorEnabled={SoA_ObjectHealthMonitor.IsEnabled}|LifecycleEnabled={SoA_LifecycleTracker.IsEnabled}");
#endif
            SoA_ObjectHealthMonitor.OnRegistered(gonetId, gonetParticipant.name, gonetParticipant.transform.position, isPhysicsObject);
            SoA_LifecycleTracker.OnSoARegistered(gonetId, gonetParticipant.name, isPhysicsObject);

            result = SoARegistrationResult.RegisteredTransform;
            return true;
        }

        /// <summary>
        /// Register a Vector3 member in the appropriate position stream.
        /// </summary>
        private static bool RegisterVector3InSoA(GONetParticipant gonetParticipant, uint gonetId, byte memberIndex, float syncInterval, IntPtr transformPtr, string memberName)
        {
            if (SoAData.positionStreamInfos.Length == 0 || SoAData.positionStreams == null)
                return false;

            int streamIndex = FindOrCreateStreamIndex(SoAData.positionStreamInfos, syncInterval);
            if (streamIndex < 0 || streamIndex >= SoAData.positionStreams.Length)
                return false;

            ref var stream = ref SoAData.positionStreams[streamIndex];
            int objectIndex = stream.RegisterObject(gonetId, transformPtr);
            if (objectIndex < 0)
                return false;

            EnsureShadowBufferCapacity();

            // DIAGNOSTIC + FIX: When overwriting stale lookup entry, mark old slot as inactive
            // This prevents the apply loop from finding the same GONetId at multiple indices
            if (soaPositionLookup.ContainsKey(gonetId))
            {
                var stale = soaPositionLookup[gonetId];
                GONetLog.Warning($"[SoA] Overwriting stale POS lookup for GONetId {gonetId} (was {stale.streamIndex}:{stale.objectIndex}, now {streamIndex}:{objectIndex}) - marking old slot inactive");

                // Mark the old slot as inactive to prevent duplicate apply attempts
                if (stale.streamIndex >= 0 && stale.streamIndex < SoAData.positionStreams.Length)
                {
                    ref var staleStream = ref SoAData.positionStreams[stale.streamIndex];
                    if (stale.objectIndex >= 0 && stale.objectIndex < staleStream.capacity)
                    {
                        staleStream.isActive[stale.objectIndex] = false;
                        staleStream.gonetIds[stale.objectIndex] = 0; // Clear GONetId so it won't match in apply loop
                    }
                }
            }
            soaPositionLookup[gonetId] = (streamIndex, objectIndex);

            int hz = Mathf.RoundToInt(1f / syncInterval);
            // GONetLog.Debug($"[SoA] Registered {gonetParticipant.name}.{memberName} (GONetId {gonetId}) in VECTOR3 @ {hz}Hz at index {objectIndex}");
            Core.SoA_BlendingDiagnostics.LogSoARegistration(gonetId, "POS", streamIndex, objectIndex, hz, gonetParticipant.name);

            UpdateSoATelemetry("VECTOR3", hz, stream.activeCount);

            if (GONetFeatureFlags.UseUnifiedSoABlending)
            {
                SoA_StreamRegistry.RegisterPosition(gonetId, memberIndex, streamIndex, objectIndex);
            }

            // LATE-JOINER FIX: Seed ring buffer with TWO samples at different timestamps.
            // For late joiners, AllValues bundle arrives BEFORE OnGONetReady/registration.
            // Without seeding, SoA ring buffer has historyCount=0 and blending fails (stationary objects).
            //
            // CRITICAL: We seed with TWO samples to create proper temporal spread:
            // - Sample 1: double-backdated (currentTicks - 2*bufferLead)
            // - Sample 2: single-backdated (currentTicks - bufferLead)
            // This ensures blending target time falls BETWEEN the two samples, giving tValue ≈ 0.5-1.0
            // instead of ≈ 0 (which would cause objects to appear stuck at spawn position).
            Vector3 currentPosition = gonetParticipant.transform.position;
            long currentTicks = Time.ElapsedTicks;
            long doubleBackdatedTicks = currentTicks - (2 * valueBlendingBufferLeadTicks);
            long singleBackdatedTicks = currentTicks - valueBlendingBufferLeadTicks;
            // Write older sample first, then newer sample (ring buffer expects chronological order)
            SoA_WritePositionUpdate(gonetId, currentPosition, doubleBackdatedTicks, isAnchor: false, isPhysicsObject: false);
            SoA_WritePositionUpdate(gonetId, currentPosition, singleBackdatedTicks, isAnchor: false, isPhysicsObject: false);
#if GONet_SOA_TRACE
            GONetLog.Debug($"[SoA-SEED] Seeded position with 2 samples: GONetId={gonetId}, pos={currentPosition}, ticks=[{doubleBackdatedTicks}, {singleBackdatedTicks}]");
#endif
            return true;
        }

        /// <summary>
        /// Register a Quaternion member in the appropriate rotation stream.
        /// </summary>
        private static bool RegisterQuaternionInSoA(GONetParticipant gonetParticipant, uint gonetId, byte memberIndex, float syncInterval, IntPtr transformPtr, string memberName)
        {
            if (SoAData.rotationStreamInfos.Length == 0 || SoAData.rotationStreams == null)
                return false;

            int streamIndex = FindOrCreateStreamIndex(SoAData.rotationStreamInfos, syncInterval);
            if (streamIndex < 0 || streamIndex >= SoAData.rotationStreams.Length)
                return false;

            ref var stream = ref SoAData.rotationStreams[streamIndex];
            int objectIndex = stream.RegisterObject(gonetId, transformPtr);
            if (objectIndex < 0)
                return false;

            EnsureShadowBufferCapacity();

            // DIAGNOSTIC + FIX: When overwriting stale lookup entry, mark old slot as inactive
            // This prevents the apply loop from finding the same GONetId at multiple indices
            if (soaRotationLookup.ContainsKey(gonetId))
            {
                var stale = soaRotationLookup[gonetId];
                GONetLog.Warning($"[SoA] Overwriting stale ROT lookup for GONetId {gonetId} (was {stale.streamIndex}:{stale.objectIndex}, now {streamIndex}:{objectIndex}) - marking old slot inactive");

                // Mark the old slot as inactive to prevent duplicate apply attempts
                if (stale.streamIndex >= 0 && stale.streamIndex < SoAData.rotationStreams.Length)
                {
                    ref var staleStream = ref SoAData.rotationStreams[stale.streamIndex];
                    if (stale.objectIndex >= 0 && stale.objectIndex < staleStream.capacity)
                    {
                        staleStream.isActive[stale.objectIndex] = false;
                        staleStream.gonetIds[stale.objectIndex] = 0; // Clear GONetId so it won't match in apply loop
                    }
                }
            }
            soaRotationLookup[gonetId] = (streamIndex, objectIndex);

            int hz = Mathf.RoundToInt(1f / syncInterval);
            // GONetLog.Debug($"[SoA] Registered {gonetParticipant.name}.{memberName} (GONetId {gonetId}) in QUATERNION @ {hz}Hz at index {objectIndex}");
            Core.SoA_BlendingDiagnostics.LogSoARegistration(gonetId, "ROT", streamIndex, objectIndex, hz, gonetParticipant.name);

            UpdateSoATelemetry("QUATERNION", hz, stream.activeCount);

            if (GONetFeatureFlags.UseUnifiedSoABlending)
            {
                SoA_StreamRegistry.RegisterRotation(gonetId, memberIndex, streamIndex, objectIndex);
            }

            // LATE-JOINER FIX: Seed ring buffer with TWO samples at different timestamps.
            // For late joiners, AllValues bundle arrives BEFORE OnGONetReady/registration.
            // Without seeding, SoA ring buffer has historyCount=0 and blending fails (stationary objects).
            //
            // CRITICAL: We seed with TWO samples to create proper temporal spread:
            // - Sample 1: double-backdated (currentTicks - 2*bufferLead)
            // - Sample 2: single-backdated (currentTicks - bufferLead)
            // This ensures blending target time falls BETWEEN the two samples, giving tValue ≈ 0.5-1.0
            // instead of ≈ 0 (which would cause objects to appear stuck at spawn position).
            Quaternion currentRotation = gonetParticipant.transform.rotation;
            long currentTicks = Time.ElapsedTicks;
            long doubleBackdatedTicks = currentTicks - (2 * valueBlendingBufferLeadTicks);
            long singleBackdatedTicks = currentTicks - valueBlendingBufferLeadTicks;
            // Write older sample first, then newer sample (ring buffer expects chronological order)
            SoA_WriteRotationUpdate(gonetId, currentRotation, doubleBackdatedTicks, isAnchor: false, isPhysicsObject: false);
            SoA_WriteRotationUpdate(gonetId, currentRotation, singleBackdatedTicks, isAnchor: false, isPhysicsObject: false);
#if GONet_SOA_TRACE
            GONetLog.Debug($"[SoA-SEED] Seeded rotation with 2 samples: GONetId={gonetId}, rot={currentRotation}, ticks=[{doubleBackdatedTicks}, {singleBackdatedTicks}]");
#endif
            return true;
        }

        /// <summary>
        /// Register a Vector2 member in the appropriate Vector2 stream.
        /// </summary>
        private static bool RegisterVector2InSoA(GONetParticipant gonetParticipant, uint gonetId, byte memberIndex, float syncInterval, IntPtr companionPtr, string memberName)
        {
            if (!SoAData.vector2StreamInfos.IsCreated || SoAData.vector2StreamInfos.Length == 0 || SoAData.vector2Streams == null)
                return false;

            int streamIndex = FindOrCreateStreamIndex(SoAData.vector2StreamInfos, syncInterval);
            if (streamIndex < 0 || streamIndex >= SoAData.vector2Streams.Length)
                return false;

            ref var stream = ref SoAData.vector2Streams[streamIndex];
            int objectIndex = stream.RegisterObject(gonetId, memberIndex, companionPtr);
            if (objectIndex < 0)
                return false;

            EnsureShadowBufferCapacity();

            soaVector2Lookup[(gonetId, memberIndex)] = (streamIndex, objectIndex);

            int hz = Mathf.RoundToInt(1f / syncInterval);
            // GONetLog.Debug($"[SoA] Registered {gonetParticipant.name}.{memberName} (GONetId {gonetId}) in VECTOR2 @ {hz}Hz at index {objectIndex}");

            UpdateSoATelemetry("VECTOR2", hz, stream.activeCount);
            return true;
        }

        /// <summary>
        /// Register a Vector4 member in the appropriate Vector4 stream.
        /// </summary>
        private static bool RegisterVector4InSoA(GONetParticipant gonetParticipant, uint gonetId, byte memberIndex, float syncInterval, IntPtr companionPtr, string memberName)
        {
            if (!SoAData.vector4StreamInfos.IsCreated || SoAData.vector4StreamInfos.Length == 0 || SoAData.vector4Streams == null)
                return false;

            int streamIndex = FindOrCreateStreamIndex(SoAData.vector4StreamInfos, syncInterval);
            if (streamIndex < 0 || streamIndex >= SoAData.vector4Streams.Length)
                return false;

            ref var stream = ref SoAData.vector4Streams[streamIndex];
            int objectIndex = stream.RegisterObject(gonetId, memberIndex, companionPtr);
            if (objectIndex < 0)
                return false;

            EnsureShadowBufferCapacity();

            soaVector4Lookup[(gonetId, memberIndex)] = (streamIndex, objectIndex);

            int hz = Mathf.RoundToInt(1f / syncInterval);
            // GONetLog.Debug($"[SoA] Registered {gonetParticipant.name}.{memberName} (GONetId {gonetId}) in VECTOR4 @ {hz}Hz at index {objectIndex}");

            UpdateSoATelemetry("VECTOR4", hz, stream.activeCount);
            return true;
        }

        /// <summary>
        /// Find member index by name in sync companion's valuesChangesSupport array.
        /// </summary>
        private static int FindMemberIndex(GONetParticipant_AutoMagicalSyncCompanion_Generated syncCompanion, string memberName)
        {
            for (int i = 0; i < syncCompanion.valuesChangesSupport.Length; i++)
            {
                if (syncCompanion.valuesChangesSupport[i]?.memberName == memberName)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Find stream index that matches the given sync interval (Hz).
        /// Returns -1 if no matching stream found.
        /// </summary>
        private static int FindOrCreateStreamIndex(NativeArray<SoA_StreamInfo> streamInfos, float syncInterval)
        {
            const float EPSILON = 0.001f; // Tolerance for float comparison

            for (int i = 0; i < streamInfos.Length; i++)
            {
                if (Mathf.Abs(streamInfos[i].updateInterval - syncInterval) < EPSILON)
                    return i;
            }

            return -1; // No matching stream (should never happen if descriptor generated correctly)
        }

        /// <summary>
        /// Update SoA telemetry: Track peak activeCount per stream (Editor only).
        /// Used to improve baseline capacity calculation in next code generation.
        /// </summary>
        private static void UpdateSoATelemetry(string streamType, int hz, int currentActiveCount)
        {
#if UNITY_EDITOR
            string key = $"GONet_SoA_Peak_{streamType}_{hz}Hz";
            int previousPeak = UnityEngine.PlayerPrefs.GetInt(key, 0);

            if (currentActiveCount > previousPeak)
            {
                UnityEngine.PlayerPrefs.SetInt(key, currentActiveCount);
                UnityEngine.PlayerPrefs.Save();
                // GONetLog.Debug($"[SoA-Telemetry] New peak for {streamType} @ {hz}Hz: {currentActiveCount} (was {previousPeak})");
            }
#endif
        }

        /// <summary>
        /// Ensure shadow buffers are large enough for current stream capacities.
        /// Called after stream registration (which may trigger auto-resize).
        /// </summary>
        private static void EnsureShadowBufferCapacity()
        {
            // Find max capacity across all position streams
            int maxPositionCapacity = 0;
            if (SoAData.positionStreams != null)
            {
                for (int i = 0; i < SoAData.positionStreams.Length; i++)
                {
                    if (SoAData.positionStreams[i].capacity > maxPositionCapacity)
                        maxPositionCapacity = SoAData.positionStreams[i].capacity;
                }
            }

            // Find max capacity across all rotation streams
            int maxRotationCapacity = 0;
            if (SoAData.rotationStreams != null)
            {
                for (int i = 0; i < SoAData.rotationStreams.Length; i++)
                {
                    if (SoAData.rotationStreams[i].capacity > maxRotationCapacity)
                        maxRotationCapacity = SoAData.rotationStreams[i].capacity;
                }
            }

            // Find max capacity across all Vector2 streams
            int maxVector2Capacity = 0;
            if (SoAData.vector2Streams != null)
            {
                for (int i = 0; i < SoAData.vector2Streams.Length; i++)
                {
                    if (SoAData.vector2Streams[i].capacity > maxVector2Capacity)
                        maxVector2Capacity = SoAData.vector2Streams[i].capacity;
                }
            }

            // Find max capacity across all Vector4 streams
            int maxVector4Capacity = 0;
            if (SoAData.vector4Streams != null)
            {
                for (int i = 0; i < SoAData.vector4Streams.Length; i++)
                {
                    if (SoAData.vector4Streams[i].capacity > maxVector4Capacity)
                        maxVector4Capacity = SoAData.vector4Streams[i].capacity;
                }
            }

            // Resize shadow buffers if needed
            SoAData.ResizeShadowBuffersIfNeeded(maxPositionCapacity, maxRotationCapacity, maxVector2Capacity, maxVector4Capacity);
        }

        /// <summary>
        /// GONet v2: Unregister non-authority object from SoA streams (Hz-agnostic).
        /// Called when OnDestroy fires for non-authority objects.
        /// Searches ALL streams of each type to find and unregister this GONetId.
        /// </summary>
        private static void UnregisterObjectFromSoA(GONetParticipant gonetParticipant)
        {
            // CRITICAL: Ensure any pending blending jobs are complete before modifying SoA arrays.
            // Without this, Unity's job safety system will throw an exception if jobs are still reading.
            Core.SoA_BlendingPipeline.EnsureJobsComplete();

            uint gonetId = gonetParticipant.GONetId;

            // Unregister from Vector3 streams (positions) - O(1) via lookup dictionary
            if (soaPositionLookup != null && soaPositionLookup.TryGetValue(gonetId, out var posLookup))
            {
                ref var stream = ref SoAData.positionStreams[posLookup.streamIndex];
                stream.UnregisterObject(posLookup.objectIndex);
                Core.SoA_BlendingDiagnostics.LogSoAUnregistration(gonetId, "POS", posLookup.streamIndex, posLookup.objectIndex, gonetParticipant.name);
                soaPositionLookup.Remove(gonetId);
                int hz = Mathf.RoundToInt(1f / SoAData.positionStreamInfos[posLookup.streamIndex].updateInterval);
                // GONetLog.Debug($"[SoA] Unregistered {gonetParticipant.name} (GONetId {gonetId}) from VECTOR3 @ {hz}Hz");
            }

            // Unregister from Quaternion streams (rotations) - O(1) via lookup dictionary
            if (soaRotationLookup != null && soaRotationLookup.TryGetValue(gonetId, out var rotLookup))
            {
                ref var stream = ref SoAData.rotationStreams[rotLookup.streamIndex];
                stream.UnregisterObject(rotLookup.objectIndex);
                Core.SoA_BlendingDiagnostics.LogSoAUnregistration(gonetId, "ROT", rotLookup.streamIndex, rotLookup.objectIndex, gonetParticipant.name);
                soaRotationLookup.Remove(gonetId);
                int hz = Mathf.RoundToInt(1f / SoAData.rotationStreamInfos[rotLookup.streamIndex].updateInterval);
                // GONetLog.Debug($"[SoA] Unregistered {gonetParticipant.name} (GONetId {gonetId}) from QUATERNION @ {hz}Hz");
            }

            // Unregister from scalar streams - O(1) via lookup dictionary
            if (soaScalarLookup != null && soaScalarLookup.TryGetValue(gonetId, out var scalarLookup))
            {
                ref var stream = ref SoAData.scalarStreams[scalarLookup.streamIndex];
                stream.UnregisterObject(scalarLookup.objectIndex);
                soaScalarLookup.Remove(gonetId);
            }

            // Unregister from Vector2 streams - O(1) via lookup dictionary
            if (soaVector2Lookup != null && SoAData.vector2Streams != null)
            {
                // Must iterate all members since key includes memberIndex
                var keysToRemove = new System.Collections.Generic.List<(uint, byte)>();
                foreach (var kvp in soaVector2Lookup)
                {
                    if (kvp.Key.gonetId == gonetId)
                    {
                        ref var stream = ref SoAData.vector2Streams[kvp.Value.streamIndex];
                        stream.UnregisterObject(kvp.Value.objectIndex);
                        keysToRemove.Add(kvp.Key);
                    }
                }
                foreach (var key in keysToRemove)
                    soaVector2Lookup.Remove(key);
            }

            // Unregister from Vector4 streams - O(1) via lookup dictionary
            if (soaVector4Lookup != null && SoAData.vector4Streams != null)
            {
                // Must iterate all members since key includes memberIndex
                var keysToRemove = new System.Collections.Generic.List<(uint, byte)>();
                foreach (var kvp in soaVector4Lookup)
                {
                    if (kvp.Key.gonetId == gonetId)
                    {
                        ref var stream = ref SoAData.vector4Streams[kvp.Value.streamIndex];
                        stream.UnregisterObject(kvp.Value.objectIndex);
                        keysToRemove.Add(kvp.Key);
                    }
                }
                foreach (var key in keysToRemove)
                    soaVector4Lookup.Remove(key);
            }

            // Clear registration flag
            gonetParticipant.v2_isRegisteredInSoA = false;

            // Notify health monitor and lifecycle tracker
            SoA_ObjectHealthMonitor.OnUnregistered(gonetId);
            SoA_LifecycleTracker.OnDespawn(gonetId, gonetParticipant.name);
        }

        /// <summary>
        /// Failover safety: Deactivate stale SoA transform entries when they no longer map to the correct Transform.
        /// This prevents applying blended values to the wrong object and forces v1 blending fallback.
        /// </summary>
        internal static void SoA_DeactivateTransformEntriesForGONetId(uint gonetId, string reason, bool scanAllStreams = false)
        {
            bool deactivated = false;
            bool lookupHit = false;

            // Ensure blending jobs are complete before modifying SoA arrays.
            Core.SoA_BlendingPipeline.EnsureJobsComplete();

            if (soaPositionLookup != null && soaPositionLookup.TryGetValue(gonetId, out var posLookup))
            {
                lookupHit = true;
                if (SoAData.positionStreams != null &&
                    posLookup.streamIndex >= 0 && posLookup.streamIndex < SoAData.positionStreams.Length)
                {
                    ref var stream = ref SoAData.positionStreams[posLookup.streamIndex];
                    if (posLookup.objectIndex >= 0 && posLookup.objectIndex < stream.capacity)
                    {
                        stream.isActive[posLookup.objectIndex] = false;
                        stream.gonetIds[posLookup.objectIndex] = 0;
                        if (stream.transformPtrs.IsCreated)
                            stream.transformPtrs[posLookup.objectIndex] = IntPtr.Zero;
                        if (stream.historyCount.IsCreated)
                            stream.historyCount[posLookup.objectIndex] = 0;
                        if (stream.historyWriteIndex.IsCreated)
                            stream.historyWriteIndex[posLookup.objectIndex] = 0;
                        deactivated = true;
                    }
                }
                soaPositionLookup.Remove(gonetId);
            }

            if (soaRotationLookup != null && soaRotationLookup.TryGetValue(gonetId, out var rotLookup))
            {
                lookupHit = true;
                if (SoAData.rotationStreams != null &&
                    rotLookup.streamIndex >= 0 && rotLookup.streamIndex < SoAData.rotationStreams.Length)
                {
                    ref var stream = ref SoAData.rotationStreams[rotLookup.streamIndex];
                    if (rotLookup.objectIndex >= 0 && rotLookup.objectIndex < stream.capacity)
                    {
                        stream.isActive[rotLookup.objectIndex] = false;
                        stream.gonetIds[rotLookup.objectIndex] = 0;
                        if (stream.transformPtrs.IsCreated)
                            stream.transformPtrs[rotLookup.objectIndex] = IntPtr.Zero;
                        if (stream.historyCount.IsCreated)
                            stream.historyCount[rotLookup.objectIndex] = 0;
                        if (stream.historyWriteIndex.IsCreated)
                            stream.historyWriteIndex[rotLookup.objectIndex] = 0;
                        deactivated = true;
                    }
                }
                soaRotationLookup.Remove(gonetId);
            }

            if (scanAllStreams || !lookupHit)
            {
                int orphanedCleared = 0;

                if (SoAData.positionStreams != null)
                {
                    for (int streamIndex = 0; streamIndex < SoAData.positionStreams.Length; streamIndex++)
                    {
                        ref var stream = ref SoAData.positionStreams[streamIndex];
                        if (!stream.gonetIds.IsCreated || !stream.isActive.IsCreated)
                            continue;

                        int limit = stream.activeCount;
                        if (limit > stream.capacity)
                            limit = stream.capacity;

                        for (int objIdx = 0; objIdx < limit; objIdx++)
                        {
                            if (!stream.isActive[objIdx] || stream.gonetIds[objIdx] != gonetId)
                                continue;

                            stream.isActive[objIdx] = false;
                            stream.gonetIds[objIdx] = 0;
                            if (stream.transformPtrs.IsCreated)
                                stream.transformPtrs[objIdx] = IntPtr.Zero;
                            if (stream.historyCount.IsCreated)
                                stream.historyCount[objIdx] = 0;
                            if (stream.historyWriteIndex.IsCreated)
                                stream.historyWriteIndex[objIdx] = 0;
                            orphanedCleared++;
                        }
                    }
                }

                if (SoAData.rotationStreams != null)
                {
                    for (int streamIndex = 0; streamIndex < SoAData.rotationStreams.Length; streamIndex++)
                    {
                        ref var stream = ref SoAData.rotationStreams[streamIndex];
                        if (!stream.gonetIds.IsCreated || !stream.isActive.IsCreated)
                            continue;

                        int limit = stream.activeCount;
                        if (limit > stream.capacity)
                            limit = stream.capacity;

                        for (int objIdx = 0; objIdx < limit; objIdx++)
                        {
                            if (!stream.isActive[objIdx] || stream.gonetIds[objIdx] != gonetId)
                                continue;

                            stream.isActive[objIdx] = false;
                            stream.gonetIds[objIdx] = 0;
                            if (stream.transformPtrs.IsCreated)
                                stream.transformPtrs[objIdx] = IntPtr.Zero;
                            if (stream.historyCount.IsCreated)
                                stream.historyCount[objIdx] = 0;
                            if (stream.historyWriteIndex.IsCreated)
                                stream.historyWriteIndex[objIdx] = 0;
                            orphanedCleared++;
                        }
                    }
                }

                if (orphanedCleared > 0)
                {
                    if (soaPositionLookup != null)
                        soaPositionLookup.Remove(gonetId);
                    if (soaRotationLookup != null)
                        soaRotationLookup.Remove(gonetId);

                    deactivated = true;
                    GONetLog.Warning($"[SoA] Cleared {orphanedCleared} orphaned transform entries for GONetId {gonetId} ({reason})");
                }
            }

            if (deactivated)
            {
                if (gonetParticipantByGONetIdMap.TryGetValue(gonetId, out var gnp) && gnp != null)
                {
                    gnp.v2_isRegisteredInSoA = false;
                    if (!gnp.IsMine)
                    {
                        QueueSoAReRegister(gnp, reason);
                    }
                }

                Core.SoA_StreamRegistry.UnregisterAll(gonetId);
                SoA_ObjectHealthMonitor.OnUnregistered(gonetId);
                GONetLog.Warning($"[SoA] Deactivated transform streams for GONetId {gonetId} ({reason})");
            }
        }

        internal static void SoA_DeactivateTransformEntry(uint gonetId, int streamIndex, int objectIndex, bool isPosition, string reason)
        {
            if (!SoAData.IsInitialized)
            {
                return;
            }

            Core.SoA_BlendingPipeline.EnsureJobsComplete();

            bool deactivated = false;

            if (isPosition && SoAData.positionStreams != null &&
                streamIndex >= 0 && streamIndex < SoAData.positionStreams.Length)
            {
                ref var stream = ref SoAData.positionStreams[streamIndex];
                if (objectIndex >= 0 && objectIndex < stream.capacity && stream.isActive[objectIndex])
                {
                    stream.isActive[objectIndex] = false;
                    stream.gonetIds[objectIndex] = 0;
                    stream.transformPtrs[objectIndex] = IntPtr.Zero;
                    stream.historyCount[objectIndex] = 0;
                    deactivated = true;
                }

                if (soaPositionLookup != null &&
                    soaPositionLookup.TryGetValue(gonetId, out var lookup) &&
                    lookup.streamIndex == streamIndex && lookup.objectIndex == objectIndex)
                {
                    soaPositionLookup.Remove(gonetId);
                }
            }

            if (!isPosition && SoAData.rotationStreams != null &&
                streamIndex >= 0 && streamIndex < SoAData.rotationStreams.Length)
            {
                ref var stream = ref SoAData.rotationStreams[streamIndex];
                if (objectIndex >= 0 && objectIndex < stream.capacity && stream.isActive[objectIndex])
                {
                    stream.isActive[objectIndex] = false;
                    stream.gonetIds[objectIndex] = 0;
                    stream.transformPtrs[objectIndex] = IntPtr.Zero;
                    stream.historyCount[objectIndex] = 0;
                    deactivated = true;
                }

                if (soaRotationLookup != null &&
                    soaRotationLookup.TryGetValue(gonetId, out var lookup) &&
                    lookup.streamIndex == streamIndex && lookup.objectIndex == objectIndex)
                {
                    soaRotationLookup.Remove(gonetId);
                }
            }

            if (!deactivated)
            {
                return;
            }

            bool hasPosition = soaPositionLookup != null && soaPositionLookup.ContainsKey(gonetId);
            bool hasRotation = soaRotationLookup != null && soaRotationLookup.ContainsKey(gonetId);

            if (!hasPosition && !hasRotation)
            {
                if (gonetParticipantByGONetIdMap.TryGetValue(gonetId, out var gnp) && gnp != null)
                {
                    gnp.v2_isRegisteredInSoA = false;
                    if (!gnp.IsMine)
                    {
                        QueueSoAReRegister(gnp, reason);
                    }
                }

                Core.SoA_StreamRegistry.UnregisterAll(gonetId);
                SoA_ObjectHealthMonitor.OnUnregistered(gonetId);
            }
        }

        private static void QueueSoAReRegister(GONetParticipant participant, string reason)
        {
            if (participant == null || participant.IsMine)
            {
                return;
            }

            if (participant.GONetId == GONetParticipant.GONetId_Unset)
            {
                return;
            }

            if (!soaReRegisterSet.Add(participant))
            {
                return;
            }

            soaReRegisterQueue.Enqueue(participant);
            GONetLog.Warning($"[SoA-REPAIR] Queued re-register for '{participant.name}' GONetId={participant.GONetId} (reason={reason})");
        }

        private static void SoA_ClearAllLookupsForGONetId(uint gonetId, string reason, bool scanAllStreams = false)
        {
            SoA_DeactivateTransformEntriesForGONetId(gonetId, reason, scanAllStreams);

            if (soaScalarLookup != null && soaScalarLookup.TryGetValue(gonetId, out var scalarLookup))
            {
                if (SoAData.scalarStreams != null &&
                    scalarLookup.streamIndex >= 0 && scalarLookup.streamIndex < SoAData.scalarStreams.Length)
                {
                    ref var stream = ref SoAData.scalarStreams[scalarLookup.streamIndex];
                    if (scalarLookup.objectIndex >= 0 && scalarLookup.objectIndex < stream.capacity)
                    {
                        stream.UnregisterObject(scalarLookup.objectIndex);
                        stream.gonetIds[scalarLookup.objectIndex] = 0;
                    }
                }
                soaScalarLookup.Remove(gonetId);
            }

            if (soaVector2Lookup != null && SoAData.vector2Streams != null)
            {
                var keysToRemove = new System.Collections.Generic.List<(uint, byte)>();
                foreach (var kvp in soaVector2Lookup)
                {
                    if (kvp.Key.gonetId == gonetId)
                    {
                        ref var stream = ref SoAData.vector2Streams[kvp.Value.streamIndex];
                        stream.UnregisterObject(kvp.Value.objectIndex);
                        keysToRemove.Add(kvp.Key);
                    }
                }
                foreach (var key in keysToRemove)
                {
                    soaVector2Lookup.Remove(key);
                }
            }

            if (soaVector4Lookup != null && SoAData.vector4Streams != null)
            {
                var keysToRemove = new System.Collections.Generic.List<(uint, byte)>();
                foreach (var kvp in soaVector4Lookup)
                {
                    if (kvp.Key.gonetId == gonetId)
                    {
                        ref var stream = ref SoAData.vector4Streams[kvp.Value.streamIndex];
                        stream.UnregisterObject(kvp.Value.objectIndex);
                        keysToRemove.Add(kvp.Key);
                    }
                }
                foreach (var key in keysToRemove)
                {
                    soaVector4Lookup.Remove(key);
                }
            }
        }

        /// <summary>
        /// Find object index within a specific stream by GONetId.
        /// Linear search - acceptable for small arrays (2-100 capacity).
        /// </summary>
        private static int FindObjectInStream(NativeArray<uint> gonetIds, int activeCount, uint targetId)
        {
            for (int i = 0; i < activeCount; i++)
            {
                if (gonetIds[i] == targetId)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// GONet v2: Network deserialization hook - write Transform position to SoA ring buffer (Hz-agnostic).
        /// Called when position sync value arrives from network.
        /// Searches ALL Vector3 streams to find where this GONetId is registered.
        /// </summary>
        /// <param name="isAnchor">True if this is a VALUE bundle (anchor)</param>
        /// <param name="isPhysicsObject">True if this is a physics object (IsRigidBodyOwnerOnlyControlled=true).
        /// Physics objects use 50Hz physics pipeline and need anchor double-write to reset velocity on VALUE bundles.
        /// Non-physics objects only receive VALUE bundles and need single-write to preserve temporal history.</param>
        // FAILOVER-DIAG: Track last write time per GONetId to detect data flow gaps
        private static Dictionary<uint, long> _soaDiag_lastWriteTicks = new Dictionary<uint, long>();
        private static long _soaDiag_failoverStartTicks = 0;

        /// <summary>
        /// Call this when failover begins to enable detailed SoA diagnostics for a window of time.
        /// </summary>
        internal static void SoA_EnableFailoverDiagnostics()
        {
            _soaDiag_failoverStartTicks = Time.ElapsedTicks;
            // GONetLog.Warning($"[SoA-FAILOVER-DIAG] Diagnostics enabled at ticks={_soaDiag_failoverStartTicks}");
        }

        // STEAMWORKS SYNC DIAGNOSTIC (Dec 2025): Track SoA writes (commented out - development diagnostic)
        // private static int _soaDiag_writeCount = 0;
        // private static int _soaDiag_writeLastLogFrame = 0;
        // private static int _soaDiag_writeFor4095Count = 0;

        /// <summary>
        /// Late-joiner fix: Replace spawn-time seed samples with current values after DeserializeInitAll.
        /// Prevents stale spawn positions/rotations from blending over the authoritative init state (non-physics only).
        /// </summary>
        private static void SoA_ResetTransformHistoryFromDeserializeInit(GONetParticipant gonetParticipant, bool hasPosition, bool hasRotation)
        {
            if (gonetParticipant == null || !SoAData.IsInitialized)
                return;

            bool isPhysicsObject = gonetParticipant.IsRigidBodyOwnerOnlyControlled && gonetParticipant.myRigidBody != null;
            if (isPhysicsObject)
                return;

            uint gonetId = gonetParticipant.GONetId;
            if (gonetId == GONetParticipant.GONetId_Unset)
                return;

            Core.SoA_BlendingPipeline.EnsureJobsComplete();

            long currentTicks = Time.ElapsedTicks;
            long doubleBackdatedTicks = currentTicks - (2 * valueBlendingBufferLeadTicks);
            long singleBackdatedTicks = currentTicks - valueBlendingBufferLeadTicks;

            if (hasPosition && soaPositionLookup != null && soaPositionLookup.TryGetValue(gonetId, out var posLookup))
            {
                if (SoAData.positionStreams != null &&
                    posLookup.streamIndex >= 0 && posLookup.streamIndex < SoAData.positionStreams.Length)
                {
                    ref var stream = ref SoAData.positionStreams[posLookup.streamIndex];
                    if (posLookup.objectIndex >= 0 && posLookup.objectIndex < stream.capacity && stream.isActive[posLookup.objectIndex])
                    {
                        if (stream.historyWriteIndex.IsCreated)
                            stream.historyWriteIndex[posLookup.objectIndex] = 0;
                        if (stream.historyCount.IsCreated)
                            stream.historyCount[posLookup.objectIndex] = 0;
                    }
                }
            }

            if (hasRotation && soaRotationLookup != null && soaRotationLookup.TryGetValue(gonetId, out var rotLookup))
            {
                if (SoAData.rotationStreams != null &&
                    rotLookup.streamIndex >= 0 && rotLookup.streamIndex < SoAData.rotationStreams.Length)
                {
                    ref var stream = ref SoAData.rotationStreams[rotLookup.streamIndex];
                    if (rotLookup.objectIndex >= 0 && rotLookup.objectIndex < stream.capacity && stream.isActive[rotLookup.objectIndex])
                    {
                        if (stream.historyWriteIndex.IsCreated)
                            stream.historyWriteIndex[rotLookup.objectIndex] = 0;
                        if (stream.historyCount.IsCreated)
                            stream.historyCount[rotLookup.objectIndex] = 0;
                    }
                }
            }

            Vector3 currentPosition = gonetParticipant.transform.position;
            Quaternion currentRotation = gonetParticipant.transform.rotation;

            if (hasPosition)
            {
                SoA_WritePositionUpdate(gonetId, currentPosition, doubleBackdatedTicks, isAnchor: false, isPhysicsObject: false);
                SoA_WritePositionUpdate(gonetId, currentPosition, singleBackdatedTicks, isAnchor: false, isPhysicsObject: false);
            }

            if (hasRotation)
            {
                SoA_WriteRotationUpdate(gonetId, currentRotation, doubleBackdatedTicks, isAnchor: false, isPhysicsObject: false);
                SoA_WriteRotationUpdate(gonetId, currentRotation, singleBackdatedTicks, isAnchor: false, isPhysicsObject: false);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SoA_WritePositionUpdate(uint gonetId, Vector3 position, long ticks, bool isAnchor = false, bool isPhysicsObject = false)
        {
            // CRITICAL FIX (Dec 2025): Ensure blending jobs are complete before writing to NativeArrays.
            // Without this, writing during job execution causes InvalidOperationException.
            Core.SoA_BlendingPipeline.EnsureJobsComplete();

            // Development diagnostic - commented out
            // _soaDiag_writeCount++;
            // if (gonetId == 4095) _soaDiag_writeFor4095Count++;
            // int currentFrame = UnityEngine.Time.frameCount;
            // if (currentFrame - _soaDiag_writeLastLogFrame >= 120)
            // {
            //     GONetLog.Info($"[SYNC-DIAG-SOA] SoA_WritePositionUpdate: total={_soaDiag_writeCount}, for4095={_soaDiag_writeFor4095Count}, isServer={IsServer}");
            //     _soaDiag_writeLastLogFrame = currentFrame;
            // }

            // O(1) lookup instead of O(n) linear search
            if (soaPositionLookup != null && soaPositionLookup.TryGetValue(gonetId, out var lookup))
            {
                var stream = SoAData.positionStreams[lookup.streamIndex];

                // FAILOVER-DIAG: Log data arrival for server-owned objects during failover window (commented out - development diagnostic)
                // bool isInFailoverWindow = _soaDiag_failoverStartTicks > 0 &&
                //                           (Time.ElapsedTicks - _soaDiag_failoverStartTicks) < TimeSpan.TicksPerSecond * 30;
                // if (isInFailoverWindow && gonetId == 4095)
                // {
                //     int historyCountBefore = stream.historyCount[lookup.objectIndex];
                //     long ticksSinceFailover = Time.ElapsedTicks - _soaDiag_failoverStartTicks;
                //     _soaDiag_lastWriteTicks.TryGetValue(gonetId, out long lastWrite);
                //     long ticksSinceLastWrite = lastWrite > 0 ? ticks - lastWrite : -1;
                //     GONetLog.Warning($"[SoA-FAILOVER-DIAG] WRITE gonetId={gonetId} pos=({position.x:F2},{position.y:F2},{position.z:F2}) " +
                //                     $"ticks={ticks} historyBefore={historyCountBefore} " +
                //                     $"sinceFailover={ticksSinceFailover / TimeSpan.TicksPerMillisecond}ms " +
                //                     $"sinceLastWrite={ticksSinceLastWrite / TimeSpan.TicksPerMillisecond}ms " +
                //                     $"streamIdx={lookup.streamIndex} objIdx={lookup.objectIndex}");
                //     _soaDiag_lastWriteTicks[gonetId] = ticks;
                // }

                // Physics objects: Anchor double-write resets velocity to zero when VALUE bundle arrives
                //                  (prevents velocity spikes from stale VELOCITY-synthesized positions)
                // Non-physics objects: Single-write preserves temporal history for proper blending
                //                      (these objects only get VALUE bundles, need time delta between them)
                bool useAnchorDoubleWrite = isAnchor && isPhysicsObject;
                SoA_LockFreeRingBuffer.WritePositionUpdate(stream, lookup.objectIndex, position, ticks, useAnchorDoubleWrite);

                // FAILOVER-DIAG: Log history count after write (commented out - development diagnostic)
                // if (isInFailoverWindow && gonetId == 4095)
                // {
                //     int historyCountAfter = stream.historyCount[lookup.objectIndex];
                //     GONetLog.Warning($"[SoA-FAILOVER-DIAG] WRITE-DONE gonetId={gonetId} historyAfter={historyCountAfter}");
                // }

                // Diagnostic: Log data received
                Core.SoA_BlendingDiagnostics.LogPositionReceived(gonetId, position, ticks, isAnchor, isPhysicsObject);

                // Health monitor: Track data received
                SoA_ObjectHealthMonitor.OnDataIn(gonetId, position, stream.historyCount[lookup.objectIndex]);
            }
            // FAILOVER-DIAG: Log if lookup failed for our target object (commented out - development diagnostic)
            // else if (_soaDiag_failoverStartTicks > 0 && gonetId == 4095)
            // {
            //     bool hasLookup = soaPositionLookup != null;
            //     bool foundInLookup = hasLookup && soaPositionLookup.ContainsKey(gonetId);
            //     GONetLog.Warning($"[SoA-FAILOVER-DIAG] WRITE-SKIP gonetId={gonetId} - lookup failed! " +
            //                     $"hasLookup={hasLookup} foundInLookup={foundInLookup}");
            // }
            // Not found = authority object or not yet registered (normal)
        }

        /// <summary>
        /// GONet v2: Network deserialization hook - write Transform rotation to SoA ring buffer (Hz-agnostic).
        /// Called when rotation sync value arrives from network.
        /// Searches ALL Quaternion streams to find where this GONetId is registered.
        /// </summary>
        /// <param name="isAnchor">True if this is a VALUE bundle (anchor)</param>
        /// <param name="isPhysicsObject">True if this is a physics object (IsRigidBodyOwnerOnlyControlled=true).
        /// Physics objects use 50Hz physics pipeline and need anchor double-write to reset velocity on VALUE bundles.
        /// Non-physics objects only receive VALUE bundles and need single-write to preserve temporal history.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SoA_WriteRotationUpdate(uint gonetId, Quaternion rotation, long ticks, bool isAnchor = false, bool isPhysicsObject = false)
        {
            // CRITICAL FIX (Dec 2025): Ensure blending jobs are complete before writing to NativeArrays.
            // Without this, writing during job execution causes InvalidOperationException.
            Core.SoA_BlendingPipeline.EnsureJobsComplete();

            // O(1) lookup instead of O(n) linear search
            if (soaRotationLookup != null && soaRotationLookup.TryGetValue(gonetId, out var lookup))
            {
                var stream = SoAData.rotationStreams[lookup.streamIndex];
                // Physics objects: Anchor double-write resets velocity to zero
                // Non-physics objects: Single-write preserves temporal history
                bool useAnchorDoubleWrite = isAnchor && isPhysicsObject;
                SoA_LockFreeRingBuffer.WriteRotationUpdate(stream, lookup.objectIndex, rotation, ticks, useAnchorDoubleWrite);

                // Diagnostic: Log data received
                Core.SoA_BlendingDiagnostics.LogRotationReceived(gonetId, rotation, ticks, isAnchor, isPhysicsObject);

                // Health monitor: Track data received (use rotation as position proxy for tracking)
                SoA_ObjectHealthMonitor.OnDataIn(gonetId, Vector3.zero, stream.historyCount[lookup.objectIndex]);
            }
            // Not found = authority object or not yet registered (normal)
        }

        /// <summary>
        /// GONet v2: Network deserialization hook - write scalar value to SoA ring buffer (Hz-agnostic).
        /// Called when any float/int/bool sync value arrives from network.
        /// Searches ALL Scalar streams to find where this GONetId is registered.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SoA_WriteScalarUpdate(uint gonetId, float value, long ticks)
        {
            // O(1) lookup instead of O(n) linear search
            if (soaScalarLookup != null && soaScalarLookup.TryGetValue(gonetId, out var lookup))
            {
                var stream = SoAData.scalarStreams[lookup.streamIndex];
                SoA_LockFreeRingBuffer.WriteScalarUpdate(stream, lookup.objectIndex, value, ticks);
            }
            // Not found = authority object or not yet registered (normal)
        }

        private static void ProcessQueuedSoAReRegisters()
        {
            if (!SoAData.IsInitialized || soaReRegisterQueue.Count == 0)
            {
                return;
            }

            int processed = 0;
            while (processed < MAX_SOA_REREG_PER_FRAME && soaReRegisterQueue.Count > 0)
            {
                GONetParticipant participant = soaReRegisterQueue.Dequeue();
                soaReRegisterSet.Remove(participant);

                if (participant == null || participant.IsMine)
                {
                    continue;
                }

                if (participant.GONetId == GONetParticipant.GONetId_Unset)
                {
                    continue;
                }

                if (participant.v2_isRegisteredInSoA)
                {
                    continue;
                }

                RegisterObjectInSoA(participant);
                processed++;
            }
        }

        private static IEnumerator Update_EndOfFrame()
        {
            yield return new WaitForEndOfFrame();

            Update_DoTheHeavyLifting_IfAppropriate(null, false);
        }

        static int lastCalledFrame_Update_DoTheHeavyLifting = -1;

        /// <summary>
        /// GONet v2: Check if a member name is Transform sync (position/rotation).
        /// Used to bypass v1 blending for v2-registered objects.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsTransformSyncMember(string memberName)
        {
            return memberName == "position" || memberName == "rotation";
        }

        /// <summary>
        /// GONet v2: Multi-rate blending scheduler (Hz-agnostic).
        /// Iterates ALL discovered streams and kicks blend jobs at their configured Hz.
        /// Supports ANY combination of update rates (24Hz, 25Hz, 60Hz, etc.).
        /// Called every frame from Update_DoTheHeavyLifting_IfAppropriate.
        /// </summary>
        private static void Update_V2_MultiRateBlending()
        {
            // Check if v2 SoA is initialized
            if (!SoAData.IsInitialized)
                return;

            double currentTime = Time.ElapsedSeconds;

            // Iterate all Vector3 streams (positions) - supports ANY Hz configuration
            if (SoAData.positionStreams != null)
            {
                for (int i = 0; i < SoAData.positionStreamInfos.Length; i++)
                {
                    var streamInfo = SoAData.positionStreamInfos[i];
                    if (currentTime >= streamInfo.nextUpdateTime)
                    {
                        KickBlendJob_Vector3(i);

                        // Update next tick time
                        streamInfo.nextUpdateTime = currentTime + streamInfo.updateInterval;
                        SoAData.positionStreamInfos[i] = streamInfo;
                    }
                }
            }

            // Iterate all Quaternion streams (rotations) - supports ANY Hz configuration
            if (SoAData.rotationStreams != null)
            {
                for (int i = 0; i < SoAData.rotationStreamInfos.Length; i++)
                {
                    var streamInfo = SoAData.rotationStreamInfos[i];
                    if (currentTime >= streamInfo.nextUpdateTime)
                    {
                        KickBlendJob_Quaternion(i);

                        // Update next tick time
                        streamInfo.nextUpdateTime = currentTime + streamInfo.updateInterval;
                        SoAData.rotationStreamInfos[i] = streamInfo;
                    }
                }
            }

            // Iterate all Scalar streams (custom fields) - supports ANY Hz configuration
            if (SoAData.scalarStreams != null)
            {
                for (int i = 0; i < SoAData.scalarStreamInfos.Length; i++)
                {
                    var streamInfo = SoAData.scalarStreamInfos[i];
                    if (currentTime >= streamInfo.nextUpdateTime)
                    {
                        KickBlendJob_Scalar(i);

                        // Update next tick time
                        streamInfo.nextUpdateTime = currentTime + streamInfo.updateInterval;
                        SoAData.scalarStreamInfos[i] = streamInfo;
                    }
                }
            }

            // Apply blended results to Transforms (batched writes)
            ApplyShadowBuffersToTransforms();
        }

        internal static void Update_DoTheHeavyLifting_IfAppropriate(GONetLocal gonetLocalCaller, bool shouldCheckGONetLocalArgument)
        {
            bool isAppropriate = (!shouldCheckGONetLocalArgument || gonetLocalCaller == myLocal)
                && lastCalledFrame_Update_DoTheHeavyLifting < UnityEngine.Time.frameCount; // avoid accidentally calling this multiple times a frame since it is called from two possible places

            if (isAppropriate)
            {
                lastCalledFrame_Update_DoTheHeavyLifting = UnityEngine.Time.frameCount;

                // ================================================================================
                // SoA BLENDING COMPLETION: Complete deferred blending jobs (scheduled in Update)
                // ================================================================================
                // Jobs were scheduled early in frame (GONet.Update, priority -32000).
                // By now, worker threads have had the entire frame duration to complete.
                // If jobs are already done, Complete() returns immediately (no blocking).
                // Then apply blended values to Transforms (must happen on main thread).
                if (GONetFeatureFlags.UseUnifiedSoABlending && SoAData.IsInitialized)
                {
                    SoA_BlendingPipeline.CompleteBlendingJobs(ref SoAData);
                    SoA_ValueApplicator.Apply(ref SoAData);

                    // LOG_BLEND_DIAG: Log blending metrics for analysis (conditional compilation)
                    // IMPORTANT: Use same targetTicks as blending pipeline (Time.ElapsedTicks - valueBlendingBufferLeadTicks)
                    SoA_BlendingDiagnostics.LogBlendingMetrics(ref SoAData, Time.ElapsedTicks - valueBlendingBufferLeadTicks, valueBlendingBufferLeadTicks);
                }

                ProcessQueuedSoAReRegisters();

                // Update adaptive pool scaler based on current utilization
                if (adaptivePoolScaler != null)
                {
                    // Calculate total borrowed count across all thread pools
                    int totalBorrowed = 0;
                    foreach (var kvp in singleProducerSendQueuesByThread)
                    {
                        totalBorrowed += kvp.Value.resourcePool.BorrowedCount;
                    }

                    int numClients = IsServer && gonetServer != null ? (int)gonetServer.numConnections : 0;
                    adaptivePoolScaler.Update(totalBorrowed, numClients);
                }

                // Process any queued main thread callbacks from async operations
                GONetThreading.ProcessMainThreadCallbacks();

                // ==========================================================================================
                // CRITICAL FIX (November 2025): Poll for unprocessed deferred AllValues bundles.
                // ==========================================================================================
                // This acts as a fail-safe if:
                // 1. The OnSceneLoadCompleted event didn't fire (race condition)
                // 2. Bundles arrived in the "tail" of scene loading (after IsCurrentlyLoadingScene=false but before readiness)
                // 3. Bundles were re-deferred due to exceptions in ProcessIncomingBytes
                // 4. Expected bundle count has been reached (deterministic completion signal)
                // 5. Timeout reached (fallback if expected count wrong or not set)
                if (deferredAllValuesBundles.Count > 0)
                {
                    // Collect unique scenes required by current deferred bundles to avoid redundant checks
                    HashSet<string> scenesToCheck = new HashSet<string>();
                    for (int i = 0; i < deferredAllValuesBundles.Count; i++)
                    {
                        string sceneName = deferredAllValuesBundles[i].RequiredSceneName;
                        if (!string.IsNullOrEmpty(sceneName))
                        {
                            scenesToCheck.Add(sceneName);
                        }
                    }

                    // Process any scene that meets completion criteria
                    foreach (string sceneName in scenesToCheck)
                    {
                        if (IsSceneCurrentlyLoaded(sceneName))
                        {
                            // Check if we should process now based on completion signals
                            bool expectedCountReached =
                                (expectedAllValuesBundlesForScene > 0 &&
                                 receivedAllValuesBundlesForLateJoinerInit >= expectedAllValuesBundlesForScene);

                            float timeSinceLastBundle = UnityEngine.Time.time - timeOfLastAllValuesBundle;
                            bool timeoutReached = timeSinceLastBundle >= ALLVALUES_BATCH_TIMEOUT;

                            if (expectedCountReached || timeoutReached)
                            {
                                // Only log if we actually find work to do, to avoid spamming every frame
                                // This indicates the Event system missed the trigger, but the Polling system caught it
                                string reason = expectedCountReached
                                    ? $"expected count reached ({receivedAllValuesBundlesForLateJoinerInit}/{expectedAllValuesBundlesForScene})"
                                    : $"timeout reached ({timeSinceLastBundle:F2}s since last bundle)";

                                GONetLog.Warning($"[POLLING-FAILSAFE] Found {deferredAllValuesBundles.Count} deferred bundles for loaded scene '{sceneName}' - forcing processing now. Reason: {reason}");
                                ProcessDeferredSpawnsForScene(sceneName);

                                // Reset counters after processing
                                expectedAllValuesBundlesForScene = -1;
                                receivedAllValuesBundlesForLateJoinerInit = 0;
                                lateJoinerInitSceneName = "";
                            }
                        }
                    }
                }
                // ==========================================================================================

                ProcessIncomingBytes_QueuedNetworkData_MainThread();
                UpdatePostHandoffSyncWatchdog();

                // GONet v2: Multi-rate blending (BEFORE v1 blending)
                // Skip if using unified SoA blending (already handled in Update_SoA_DynamicMultiRate)
                if (!GONetFeatureFlags.UseUnifiedSoABlending)
                {
                    Update_V2_MultiRateBlending();
                }

                AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable itemsToProcessEveryFrame;
                if (autoSyncProcessingSupportByFrequencyMap.TryGetValue(grouping_endOfFrame_reliable, out itemsToProcessEveryFrame))
                {
                    itemsToProcessEveryFrame.ProcessASAP(); // this one requires manual initiation of processing
                }
                if (autoSyncProcessingSupportByFrequencyMap.TryGetValue(grouping_endOfFrame_unreliable, out itemsToProcessEveryFrame))
                {
                    itemsToProcessEveryFrame.ProcessASAP(); // this one requires manual initiation of processing
                }

                int mainThreadSupportCount = autoSyncProcessingSupports_UnityMainThread.Count;
                for (int i = 0; i < mainThreadSupportCount; ++i)
                {
                    AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable autoSyncProcessingSupport_mainThread = autoSyncProcessingSupports_UnityMainThread[i];
                    autoSyncProcessingSupport_mainThread.ProcessASAP();
                }

                var enumerator_activeAutoSyncCompanionsMapByCodeGenerationId = activeAutoSyncCompanionsByCodeGenerationIdMap.GetEnumerator();
                while (enumerator_activeAutoSyncCompanionsMapByCodeGenerationId.MoveNext())
                {
                    var kvp_activeAutoSyncCompanionsMapForCodeGenerationId = enumerator_activeAutoSyncCompanionsMapByCodeGenerationId.Current;

                    var enumerator_activeAutoSyncCompanionsMap = kvp_activeAutoSyncCompanionsMapForCodeGenerationId.Value.GetEnumerator();
                    while (enumerator_activeAutoSyncCompanionsMap.MoveNext())
                    {
                        var kvp_activeAutoSyncCompanion = enumerator_activeAutoSyncCompanionsMap.Current;
                        GONetParticipant_AutoMagicalSyncCompanion_Generated activeAutoSyncCompanion = kvp_activeAutoSyncCompanion.Value;

                        // FIX (Oct 2025): Skip processing if GONetParticipant has been destroyed (scene unload or manual destroy)
                        if (activeAutoSyncCompanion.gonetParticipant == null)
                        {
                            continue; // Skip destroyed participants
                        }

                        int length_valueChangesSupport = activeAutoSyncCompanion.valuesChangesSupport.Length;
                        for (int i = 0; i < length_valueChangesSupport; ++i)
                        {
                            AutoMagicalSync_ValueMonitoringSupport_ChangedValue valueChangeSupport = activeAutoSyncCompanion.valuesChangesSupport[i];
                            if (valueChangeSupport != null && !GONetParticipant_AutoMagicalSyncCompanion_Generated.ShouldSkipSync(valueChangeSupport, i))
                            {
                                // GONet v2: Skip v1 blending if v2 is handling this value
                                if (activeAutoSyncCompanion.gonetParticipant.v2_isRegisteredInSoA &&
                                    IsTransformSyncMember(valueChangeSupport.memberName))
                                {
                                    continue; // v2 SoA handles this - skip v1 blending
                                }

                                valueChangeSupport.ApplyValueBlending_IfAppropriate(valueBlendingBufferLeadTicks);
                            }
                        }
                    }
                }

                PublishEvents_SentToOthers();
#if !PERF_NO_PROCESS_SYNC_EVENTS
                PublishEvents_SyncValueChanges_ReceivedFromOthers();
#endif
                SaveEventsInQueueASAP_IfAppropriate();

                // HIGH PRIORITY THREAD: Network sends only (no file I/O blocking)
                if (endOfLineSendThread == null || !endOfLineSendThread.IsAlive)
                {
                    isRunning_endOfTheLineSend_Thread = true;
                    endOfLineSendThread = new Thread(SendBytes_EndOfTheLine_AllSendsMUSTComeHere_SeparateThread);
                    endOfLineSendThread.Name = "GONet End-of-the-Line Send (HIGH Priority)";
                    endOfLineSendThread.Priority = System.Threading.ThreadPriority.AboveNormal;
                    endOfLineSendThread.IsBackground = true; // do not prevent process from exiting when foreground thread(s) end
                    endOfLineSendThread.Start();
                }

#if !PERF_NO_PROCESS_SYNC_EVENTS
                // LOW PRIORITY THREAD: Database saves only (file I/O won't block sends)
                if (databaseSaveThread == null || !databaseSaveThread.IsAlive)
                {
                    isRunning_databaseSave_Thread = true;
                    databaseSaveThread = new Thread(DatabaseSave_SeparateThread);
                    databaseSaveThread.Name = "GONet Database Save (LOW Priority)";
                    databaseSaveThread.Priority = System.Threading.ThreadPriority.BelowNormal;
                    databaseSaveThread.IsBackground = true; // do not prevent process from exiting when foreground thread(s) end
                    databaseSaveThread.Start();
                }
#endif

                if (IsServer)
                {
                    _gonetServer?.Update();
                }

                if (IsClient)
                {
                    // HOST MODE FIX: Only pure clients should sync time with server.
                    // Host IS the time authority - it should never request time sync from itself.
                    if (!IsServer)
                    {
                        Client_SyncTimeWithServer_Initiate_IfAppropriate();
                    }
                    GONetClient?.Update();
                }

                foreach (var gnp in gnpsAwaitingCompanion)
                {
                    if (gnp != null)
                    {
                        GONetLog.Debug("gnp now not unity null...gnp.gonetId: " + gnp.GONetId);

                        Dictionary<GONetParticipant, GONetParticipant_AutoMagicalSyncCompanion_Generated> companionMap;
                        if (activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(gnp.CodeGenerationId, out companionMap))
                        {
                            if (companionMap.ContainsKey(gnp))
                            {
                                GONetLog.Debug("gnp also now in map.....can now proceed with processing the remaining bytes!");
                            }
                        }
                    }
                }

                recentlyDisabledGONetId_to_GONetIdAtInstantiation_Map.Clear();

                // Periodically clean up old warning suppression entries (once every 10 seconds)
                // Prevents dictionary from growing indefinitely with despawned object IDs
                const long CLEANUP_INTERVAL_TICKS = 10 * TimeSpan.TicksPerSecond;
                const long SUPPRESSION_WINDOW_TICKS = 5 * TimeSpan.TicksPerSecond;
                if (missingGONetParticipantWarningSuppressionMap.Count > 0)
                {
                    long currentTicks = Time.ElapsedTicks;
                    if (!_lastWarningSuppressionCleanupTicks.HasValue ||
                        (currentTicks - _lastWarningSuppressionCleanupTicks.Value) >= CLEANUP_INTERVAL_TICKS)
                    {
                        // Remove entries older than suppression window + cleanup interval
                        long expiryThreshold = currentTicks - (SUPPRESSION_WINDOW_TICKS + CLEANUP_INTERVAL_TICKS);
                        var keysToRemove = new List<uint>();
                        foreach (var kvp in missingGONetParticipantWarningSuppressionMap)
                        {
                            if (kvp.Value < expiryThreshold)
                            {
                                keysToRemove.Add(kvp.Key);
                            }
                        }

                        foreach (uint key in keysToRemove)
                        {
                            missingGONetParticipantWarningSuppressionMap.Remove(key);
                        }

                        _lastWarningSuppressionCleanupTicks = currentTicks;

                        // Also log unreliable packet drop summary periodically
                        if (_unreliablePacketDropCount > 0)
                        {
                            GONetLog.Info($"[SYNC-HEALTH] Unreliable packet drops since start: {_unreliablePacketDropCount} | Active GONetParticipants: {gonetParticipantByGONetIdMap.Count} | Send buffer max: {SingleProducerQueues.MAX_PACKETS_PER_TICK}");
                        }
                    }
                }

                // LATE FRAME UPDATE: LateUpdateAfterGONetReady for all ready GONetParticipantCompanionBehaviours
                // Runs in Update_DoTheHeavyLifting_IfAppropriate (called from GONetLocal.LateUpdate() at priority +32000)
                //
                // ROBUSTNESS FEATURES:
                // - Enumerator with dispose: Safe against DestroyImmediate() modifying HashSet mid-loop
                // - Per-behaviour try-catch: One exception doesn't break entire pipeline
                // - Optimized null checks: Avoids Unity's overloaded null operator
                // - Static reflection cache: Zero overhead for behaviours that don't override method
                using (var enumerator = allGONetBehaviours.GetEnumerator())
                {
                    while (enumerator.MoveNext())
                    {
                        GONetBehaviour behaviour = enumerator.Current;

                        // Defensive: Check if destroyed during iteration
                        object behaviourObj = behaviour;
                        if (behaviourObj == null)
                        {
                            continue;
                        }

                        // Fast early exit: Type doesn't override method
                        if (!behaviour.hasLateUpdateAfterGONetReadyOverride)
                        {
                            continue;
                        }

                        // Optimized cast and null checks (avoid Unity null operator)
                        GONetParticipantCompanionBehaviour companion = behaviour as GONetParticipantCompanionBehaviour;
                        object companionObj = companion;
                        if (companionObj == null)
                        {
                            continue;
                        }

                        object participantObj = companion.GONetParticipant;
                        if (participantObj == null)
                        {
                            continue;
                        }

                        // Check if participant is ready
                        if (!IsGONetReady(companion.GONetParticipant))
                        {
                            continue;
                        }

                        // ROBUST: Try-catch per behaviour - one exception doesn't break pipeline
                        try
                        {
                            companion.LateUpdateAfterGONetReady();
                        }
                        catch (Exception e)
                        {
                            // Log with full context for debugging
                            GONetLog.Error($"[GONet] Exception in LateUpdateAfterGONetReady() for {companion.GetType().Name} (GONetId: {companion.GONetParticipant.GONetId}): {e}");
                        }
                    }
                }
                // END of late update loop

                // DIAGNOSTIC: Frame-end metrics for packet processing and deserialization
                // Added 2025-10-11 to investigate DeserializeInitAllCompleted event delivery during rapid spawning
                //LogFrameEndMetrics_IfAppropriate();

                // ==========================================================================================
                // FLOW CONTROL & SECURITY: Process pending chunks and cleanup stale reassemblies
                // ==========================================================================================
                // Added 2025-11-23: Production-ready chunking with flow control and DoS prevention
                // - ProcessPendingChunks(): Time-sliced sending (2 chunks/frame) prevents packet storms
                // - CleanupStaleChunkReassemblies(): TTL cleanup (10s timeout) prevents memory leaks
                //
                // PERFORMANCE: With 10KB chunks: 2 chunks/frame × frame rate = controlled throughput
                //   Example: @ 60 FPS = 1.2 MB/sec, @ 120 FPS = 2.4 MB/sec
                // SECURITY: Prevents malicious chunk headers from exhausting server memory
                //
                // See commits: 67b47941 (Phase 1: Security), e0928c78 (Phase 2: Flow Control)

                // Process server connections (if server)
                if (IsServer && _gonetServer != null)
                {
                    uint numConnections = _gonetServer.numConnections;
                    for (int i = 0; i < numConnections; i++)
                    {
                        GONetConnection_ServerToClient connection = _gonetServer.remoteClients[i].ConnectionToClient;

                        // FLOW CONTROL (CRITICAL): Process pending chunks every frame
                        // Sends max 2 chunks (approx 20KB) per client per frame
                        connection.ProcessPendingChunks();
                    }

                    // SECURITY / JANITOR (LOW FREQUENCY): Cleanup stale reassemblies periodically
                    // Use GONetMain.Time.FrameCount for network-consistent timing
                    long currentFrameCount = Time.FrameCount;
                    if (currentFrameCount % 60 == 0) // Every 60 frames (approx 1 second at typical frame rates)
                    {
                        for (int i = 0; i < numConnections; i++)
                        {
                            _gonetServer.remoteClients[i].ConnectionToClient.CleanupStaleChunkReassemblies();
                        }
                    }
                }

                // Process client connection (if client)
                if (IsClient && _gonetClient != null && _gonetClient.connectionToServer != null)
                {
                    GONetConnection_ClientToServer connection = _gonetClient.connectionToServer;

                    // FLOW CONTROL: Process pending chunks
                    connection.ProcessPendingChunks();

                    // SECURITY: Cleanup stale reassemblies periodically
                    long currentFrameCount = Time.FrameCount;
                    if (currentFrameCount % 60 == 0)
                    {
                        connection.CleanupStaleChunkReassemblies();
                    }
                }

                // REPARENT POSITION GUARD (Jan 2026): Enforce correct local offsets for reparented children.
                // CRITICAL: This MUST run at the end of LateUpdate, AFTER all sync/blending has been applied.
                // If this ran earlier (in Update), sync application in LateUpdate would overwrite the corrections.
                // This catches any sync/blending paths that we may have missed with suspension checks.
                EnforceReparentPositionGuards();
            }
        }
        // END of Update_DoTheHeavyLifting_IfAppropriate method

        /// <summary>
        /// DIAGNOSTIC: Enhanced frame-end metrics for packet processing pipeline analysis.
        /// Added 2025-10-11 to investigate packet saturation during rapid spawning.
        ///
        /// Tracks PER-FRAME and BY-CHANNEL:
        /// - INCOMING: Packets received, queued (awaiting process), processed this frame
        /// - OUTGOING: Packets sent this frame (reliable vs unreliable breakdown)
        /// - PARTICIPANTS: Waiting for deserialization
        /// - EVENTS: DeserializeInitAllCompleted published
        ///
        /// Channel breakdown helps identify which pipeline saturates first.
        /// </summary>
        private static int _lastLoggedFrame_FrameEndMetrics = -1;
        private static int _deserializeInitEventsPublishedThisFrame = 0;

        // Per-frame counters for incoming packet tracking
        private static int _incomingPacketsProcessedThisFrame_Reliable = 0;
        private static int _incomingPacketsProcessedThisFrame_Unreliable = 0;

        // Per-frame counters for outgoing packet tracking
        private static int _outgoingPacketsSentThisFrame_Reliable = 0;
        private static int _outgoingPacketsSentThisFrame_Unreliable = 0;

        /// <summary>
        /// DIAGNOSTIC: Call when processing an incoming packet to track throughput by channel.
        /// </summary>
        internal static void IncrementIncomingPacketCounter(bool isReliable)
        {
            if (isReliable)
                _incomingPacketsProcessedThisFrame_Reliable++;
            else
                _incomingPacketsProcessedThisFrame_Unreliable++;
        }

        /// <summary>
        /// DIAGNOSTIC: Call when sending an outgoing packet to track throughput by channel.
        /// </summary>
        internal static void IncrementOutgoingPacketCounter(bool isReliable)
        {
            if (isReliable)
                _outgoingPacketsSentThisFrame_Reliable++;
            else
                _outgoingPacketsSentThisFrame_Unreliable++;
        }

        /// <summary>
        /// DIAGNOSTIC: Call whenever a DeserializeInitAllCompleted event is published to track event rate.
        /// </summary>
        internal static void IncrementDeserializeInitEventCounter()
        {
            _deserializeInitEventsPublishedThisFrame++;
        }

        private static void LogFrameEndMetrics_IfAppropriate()
        {
            int currentFrame = UnityEngine.Time.frameCount;

            // Only log once per frame (defensive check)
            if (_lastLoggedFrame_FrameEndMetrics == currentFrame)
            {
                return;
            }
            _lastLoggedFrame_FrameEndMetrics = currentFrame;

            // === 1. INCOMING PACKET METRICS (by channel) ===
            int incomingQueued_Reliable = 0;
            int incomingQueued_Unreliable = 0;
            int incomingProcessed_Reliable = _incomingPacketsProcessedThisFrame_Reliable;
            int incomingProcessed_Unreliable = _incomingPacketsProcessedThisFrame_Unreliable;

            // Count packets currently queued awaiting processing (approx - queue doesn't track reliability)
            int totalQueuedPackets = 0;
            using (var enumerator = singleProducerReceiveQueuesByThread.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    SingleProducerQueues singleProducerReceiveQueues = enumerator.Current.Value;
                    ConcurrentQueue<NetworkData> incomingNetworkData = singleProducerReceiveQueues.queueForWork;
                    totalQueuedPackets += incomingNetworkData.Count;
                }
            }

            // Reset counters for next frame
            _incomingPacketsProcessedThisFrame_Reliable = 0;
            _incomingPacketsProcessedThisFrame_Unreliable = 0;

            // === 2. OUTGOING PACKET METRICS (by channel) ===
            int outgoingSent_Reliable = _outgoingPacketsSentThisFrame_Reliable;
            int outgoingSent_Unreliable = _outgoingPacketsSentThisFrame_Unreliable;

            // Count packets currently queued awaiting send
            int totalQueuedOutgoing = 0;
            using (var enumerator = singleProducerSendQueuesByThread.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    SingleProducerQueues singleProducerSendQueues = enumerator.Current.Value;
                    totalQueuedOutgoing += singleProducerSendQueues.queueForWork.Count;
                }
            }

            // Reset counters for next frame
            _outgoingPacketsSentThisFrame_Reliable = 0;
            _outgoingPacketsSentThisFrame_Unreliable = 0;

            // === 3. PARTICIPANT DESERIALIZATION STATE ===
            int participantsWaitingForDeserialize = 0;
            int totalParticipants = 0;

            using (var enumerator = gonetParticipantByGONetIdMap.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    GONetParticipant participant = enumerator.Current.Value;
                    if (participant != null)
                    {
                        totalParticipants++;
                        if (participant.requiresDeserializeInit && !participant.didDeserializeInitComplete)
                        {
                            participantsWaitingForDeserialize++;
                        }
                    }
                }
            }

            // === 4. EVENT BUS METRICS ===
            int eventBusQueueDepth = EventBus != null ? EventBus.GetApproximateQueueDepth() : 0;
            int deserializeInitEventsPublished = _deserializeInitEventsPublishedThisFrame;
            _deserializeInitEventsPublishedThisFrame = 0; // Reset for next frame

            // === 5. LOG IF INTERESTING (avoid spam during idle) ===
            // Threshold: Processing activity OR queues building up OR participants waiting
            const int ACTIVITY_THRESHOLD = 0; // Log whenever there's ANY activity
            bool hasActivity =
                (incomingProcessed_Reliable + incomingProcessed_Unreliable) > ACTIVITY_THRESHOLD ||
                (outgoingSent_Reliable + outgoingSent_Unreliable) > ACTIVITY_THRESHOLD ||
                totalQueuedPackets > 5 ||
                totalQueuedOutgoing > 5 ||
                participantsWaitingForDeserialize > 0 ||
                deserializeInitEventsPublished > 0;

            if (hasActivity)
            {
                // Count clients with suppressed unreliable (active backpressure)
                int clientsWithBackpressure = 0;
                int totalReliableQueueDepth = 0;
                foreach (var state in _clientCongestionStates.Values)
                {
                    if (state.isUnreliableSuppressed)
                    {
                        clientsWithBackpressure++;
                    }
                    totalReliableQueueDepth += state.reliableQueueDepth;
                }

                string backpressureInfo = _clientCongestionStates.Count > 0
                    ? $"Backpressure={{Clients:{clientsWithBackpressure}/{_clientCongestionStates.Count}, RelQDepth:{totalReliableQueueDepth}, TotalDrops:{_totalBackpressureDrops}, StateChanges:{_totalSuppressionStateChanges}}} | "
                    : "";

                GONetLog.Info(
                    $"[FRAME-METRICS] Frame {currentFrame}: " +
                    $"IN={{Processed:R{incomingProcessed_Reliable}/U{incomingProcessed_Unreliable}, Queued:{totalQueuedPackets}}} | " +
                    $"OUT={{Sent:R{outgoingSent_Reliable}/U{outgoingSent_Unreliable}, Queued:{totalQueuedOutgoing}}} | " +
                    backpressureInfo +
                    $"Waiting={participantsWaitingForDeserialize}/{totalParticipants} | " +
                    $"EventBus={eventBusQueueDepth} | " +
                    $"DeserInitPub={deserializeInitEventsPublished}");
            }
        }

        /// <summary>
        /// EARLY FRAME UPDATE: UpdateAfterGONetReady for all ready GONetParticipantCompanionBehaviours.
        /// Called from GONetMain.Update() at end (runs at GONetGlobal.Update priority -32000, early in frame).
        ///
        /// ROBUSTNESS FEATURES:
        /// - Enumerator with dispose: Safe against DestroyImmediate() modifying HashSet mid-loop
        /// - Per-behaviour try-catch: One exception doesn't break entire pipeline
        /// - Optimized null checks: Avoids Unity's overloaded null operator (cast to object first)
        /// - Static reflection cache: Zero overhead for behaviours that don't override method
        /// </summary>
        internal static void Update_EarlyFrame_UpdateAfterGONetReady()
        {
            // SAFE ITERATION: Using enumerator with dispose pattern (HashSet-safe)
            // This handles DestroyImmediate() modifying the collection during iteration
            using (var enumerator = allGONetBehaviours.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    GONetBehaviour behaviour = enumerator.Current;

                // Defensive: Check if destroyed during iteration
                object behaviourObj = behaviour;
                if (behaviourObj == null)
                {
                    continue;
                }

                // Fast early exit: Type doesn't override method
                if (!behaviour.hasUpdateAfterGONetReadyOverride)
                {
                    continue;
                }

                // Optimized cast and null checks (avoid Unity null operator)
                GONetParticipantCompanionBehaviour companion = behaviour as GONetParticipantCompanionBehaviour;
                object companionObj = companion;
                if (companionObj == null)
                {
                    continue;
                }

                object participantObj = companion.GONetParticipant;
                if (participantObj == null)
                {
                    continue;
                }

                // Check if participant is ready
                if (!IsGONetReady(companion.GONetParticipant))
                {
                    // DIAGNOSTIC: Log why objects aren't ready (once per object) - covers handoff sync issues
                    if (!companion.GONetParticipant.didLogNotReadyReason && Time.ElapsedSeconds > 5)
                    {
                        companion.GONetParticipant.didLogNotReadyReason = true;
                        string reason = GetIsGONetReadyBlockingReason(companion.GONetParticipant);

                        // Pooled inactive objects are EXPECTED to not be ready (Debug level)
                        // Non-pooled objects not ready after 5 seconds may indicate a real problem (Warning level)
                        if (companion.GONetParticipant.IsPooledInactive)
                        {
                            GONetLog.Debug($"[NOT-READY] '{companion.GONetParticipant.name}' (GONetId={companion.GONetParticipant.GONetId}, OwnerAuth={companion.GONetParticipant.OwnerAuthorityId}) not ready (pooled inactive): {reason}");
                        }
                        else
                        {
                            GONetLog.Warning($"[NOT-READY] '{companion.GONetParticipant.name}' (GONetId={companion.GONetParticipant.GONetId}, OwnerAuth={companion.GONetParticipant.OwnerAuthorityId}) not ready: {reason}");
                        }
                    }
                    continue;
                }

                    // ROBUST: Try-catch per behaviour - one exception doesn't break pipeline
                    try
                    {
                        companion.UpdateAfterGONetReady();
                    }
                    catch (Exception e)
                    {
                        // Log with full context for debugging
                        GONetLog.Error($"[GONet] Exception in UpdateAfterGONetReady() for {companion.GetType().Name} (GONetId: {companion.GONetParticipant.GONetId}): {e}");
                    }
                }
            }
        }

        /// <summary>
        /// REMOVED: Old Server_CollectAndSyncPhysicsState() method.
        /// Physics sync now handled by PhysicsSync_ProcessASAP() using standard AutoMagicalSync infrastructure.
        /// The T4 template generates Rigidbody-aware value sourcing for position/rotation automatically.
        /// See PhysicsSync_ProcessASAP() method and WaitForFixedUpdate coroutine in GONetGlobal.cs.
        /// </summary>

        /// <summary>
        /// PHYSICS FRAME UPDATE: FixedUpdateAfterGONetReady for all ready GONetParticipantCompanionBehaviours.
        /// Called from GONetGlobal.FixedUpdate() at Unity's fixed timestep (default: 50Hz / 0.02s).
        ///
        /// ROBUSTNESS FEATURES:
        /// - Enumerator with dispose: Safe against DestroyImmediate() modifying HashSet mid-loop
        /// - Per-behaviour try-catch: One exception doesn't break entire pipeline
        /// - Optimized null checks: Avoids Unity's overloaded null operator
        /// </summary>
        internal static void FixedUpdate_AfterGONetReady()
        {
            // Refresh physics time counter (mirrors Unity's Time.fixedTime behavior)
            if (Time != null)
            {
                Time.FixedUpdate();
            }

            // Physics sync now happens in WaitForFixedUpdate coroutine (started in GONetGlobal.cs)
            // This runs AFTER all physics processing (simulation + collision/trigger callbacks)
            // See PhysicsSync_ProcessASAP() method for implementation details

            // SAFE ITERATION: Using enumerator with dispose pattern (HashSet-safe)
            using (var enumerator = allGONetBehaviours.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    GONetBehaviour behaviour = enumerator.Current;

                // Defensive: Check if destroyed during iteration
                object behaviourObj = behaviour;
                if (behaviourObj == null)
                {
                    continue;
                }

                // Fast early exit: Type doesn't override method
                if (!behaviour.hasFixedUpdateAfterGONetReadyOverride)
                {
                    continue;
                }

                // Optimized cast and null checks
                GONetParticipantCompanionBehaviour companion = behaviour as GONetParticipantCompanionBehaviour;
                object companionObj = companion;
                if (companionObj == null)
                {
                    continue;
                }

                object participantObj = companion.GONetParticipant;
                if (participantObj == null)
                {
                    continue;
                }

                // Check if participant is ready
                if (!IsGONetReady(companion.GONetParticipant))
                {
                    continue;
                }

                    // ROBUST: Try-catch per behaviour
                    try
                    {
                        companion.FixedUpdateAfterGONetReady();
                    }
                    catch (Exception e)
                    {
                        GONetLog.Error($"[GONet] Exception in FixedUpdateAfterGONetReady() for {companion.GetType().Name} (GONetId: {companion.GONetParticipant.GONetId}): {e}");
                    }
                }
            }

            // Physics sync now happens in WaitForFixedUpdate coroutine (started in GONetGlobal)
            // This runs AFTER all physics processing (simulation + collision/trigger callbacks)
            // See PhysicsSync_ProcessASAP() method
        }

        /// <summary>
        /// Physics sync - Captures and syncs Rigidbody state from server to clients.
        /// Called from WaitForFixedUpdate coroutine AFTER all physics processing completes:
        /// - After all FixedUpdate() calls
        /// - After internal physics simulation
        /// - After OnCollisionEnter/Stay/Exit callbacks
        /// - After OnTriggerEnter/Stay/Exit callbacks
        ///
        /// This timing ensures we capture the FINAL physics state, not intermediate state.
        /// </summary>
        internal static void PhysicsSync_ProcessASAP()
        {
            // Increment physics frame counter (wraps around to prevent overflow)
            physicsFrameCounter = (physicsFrameCounter + 1) % 4;

            // SERVER ONLY: Physics sync only runs on server (authority over physics simulation)
            if (!IsServer)
            {
                return;
            }

            // Ensure physics sync processing support exists
            // This is created dynamically when first companion with physics sync needs it,
            // but we ensure it exists here for robustness
            if (!autoSyncProcessingSupportByFrequencyMap.ContainsKey(grouping_physics_unreliable))
            {
                // First-time setup - only happens once per session
                var physicsAutoSyncProcessingSupport =
                    new AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable(
                        grouping_physics_unreliable,
                        activeAutoSyncCompanionsByCodeGenerationIdMap);
                physicsAutoSyncProcessingSupport.AboutToProcess += AutoSyncProcessingSupport_AboutToProcess;
                autoSyncProcessingSupportByFrequencyMap[grouping_physics_unreliable] = physicsAutoSyncProcessingSupport;
                autoSyncProcessingSupports_UnityMainThread.Add(physicsAutoSyncProcessingSupport);
            }

            // Process physics sync using standard AutoMagicalSyncProcessing pipeline
            AutoMagicalSyncProcessing_SingleGrouping_SeparateThreadCapable physicsSync;
            if (autoSyncProcessingSupportByFrequencyMap.TryGetValue(grouping_physics_unreliable, out physicsSync))
            {
                physicsSync.ProcessASAP();
            }
            else
            {
                GONetLog.Warning("[Physics Sync] Could not find physics sync processing support!");
            }
        }

        /// <summary>
        /// Stopwatch for measuring persistence queue processing time.
        /// Used to track CPU usage and trigger emergency thinning if needed.
        /// </summary>
        private static readonly System.Diagnostics.Stopwatch persistenceProcessingStopwatch = new System.Diagnostics.Stopwatch();

        private static void SaveEventsInQueueASAP_IfAppropriate(bool shouldForceAppropriateness = false) // TODO put all this in another thread to not disrupt the main thread with saving!!!
        {
            // Start measuring processing time
            persistenceProcessingStopwatch.Restart();

            var enumerator = syncEventsToSaveQueueByEventType.GetEnumerator();
            while (enumerator.MoveNext())
            {
                SyncEventsSaveSupport syncEventsToSaveQueue = enumerator.Current.Value;
                int count = syncEventsToSaveQueue.queue_needsSavingASAP.Count;
                bool isAppropriate = shouldForceAppropriateness || (!syncEventsToSaveQueue.IsSaving && count >= SYNC_EVENT_QUEUE_SAVE_WHEN_FULL_SIZE); // TODO add in another condition that makes it appropriate: enough time passed since last save (e.g., 30 seconds)
                if (isAppropriate)
                {
                    syncEventsToSaveQueue.InitiateSave_MainUnityThread();
                }

                { // return some that are ready...just be sure to spread it out over multiple frames
                    syncEventsToSaveQueue.ReturnSaved_SpreadOverFrames_MainUnityThread();
                }
            }

            // Stop measuring and update last processing time
            persistenceProcessingStopwatch.Stop();
            persistenceQueueLastProcessingTimeMs = persistenceProcessingStopwatch.Elapsed.TotalMilliseconds;
        }

        static readonly Queue<SyncEvent_ValueChangeProcessed> syncEventsToSaveQueue_hereUseMeToAvoidMultiLevelEnumerationErrors = new Queue<SyncEvent_ValueChangeProcessed>(SYNC_EVENT_QUEUE_SAVE_WHEN_FULL_SIZE + 100);
        /// <summary>
        /// PRE: call this not from the main unity thread, but rather the "save thread" (which is <see cref="databaseSaveThread"/>)
        /// </summary>
        private static void AppendToDatabaseFile_SaveThread(Queue<SyncEvent_ValueChangeProcessed> syncEventsToSaveQueue)
        {
            syncEventsToSaveQueue_hereUseMeToAvoidMultiLevelEnumerationErrors.Clear();
            var sourceEnumerator = syncEventsToSaveQueue.GetEnumerator();
            while (sourceEnumerator.MoveNext())
            {
                syncEventsToSaveQueue_hereUseMeToAvoidMultiLevelEnumerationErrors.Enqueue(sourceEnumerator.Current);
            }

            SyncEvent_PersistenceBundle.Instance.bundle = syncEventsToSaveQueue_hereUseMeToAvoidMultiLevelEnumerationErrors;
            int returnBytesUsedCount;
            byte[] bytes = SerializationUtils.SerializeToBytes(SyncEvent_PersistenceBundle.Instance, out returnBytesUsedCount, out bool doesNeedToReturn);

            persistenceFileStream.Write(bytes, 0, returnBytesUsedCount);
            persistenceFileStream.Flush(true);

            //GONetLog.Debug("WROTE DB!!!! ++++++++++++++++++++++++++++++ count: " + syncEventsToSaveQueue_hereUseMeToAvoidMultiLevelEnumerationErrors.Count);

            /*{ // example of reading from file:
                byte[] allBytes = File.ReadAllBytes(persistenceFilePath);
                int bytesRead = 0;
                while (bytesRead < allBytes.Length)
                {
                    int bytesReadInner;
                    SyncEvent_PersistenceBundle bundle = SerializationUtils.DeserializeFromBytes<SyncEvent_PersistenceBundle>(allBytes, bytesRead, out bytesReadInner);
                    bytesRead += bytesReadInner;
                }
            }*/

            if (doesNeedToReturn)
            {
                SerializationUtils.ReturnByteArray(bytes);
            }
        }


        private static void PublishEvents_SyncValueChanges_ReceivedFromOthers()
        {
            int count = syncValueChanges_ReceivedFromOtherQueue.Count;
            for (int i = 0; i < count; ++i)
            {
                var @event = syncValueChanges_ReceivedFromOtherQueue.Dequeue();
                try
                {
                    EventBus.Publish(@event);
                }
                catch (Exception e)
                {
                    const string BOO = "Boo.  Publishing this sync value change event failed.  Error.Message: ";
                    GONetLog.Error(string.Concat(BOO, e.Message));
                }
            }
        }

        private static void PublishEvents_SentToOthers()
        {
            long methodStartTicks = HighResolutionTimeUtils.UtcNowTicks;
            publishEventsDiag_publishCallCount++;

            // DEFENSIVE: Check if current thread is registered
            // This should never happen since Unity main thread is registered during InitOnUnityMainThread(),
            // but adding defensive check to prevent KeyNotFoundException
            Thread currentThread = Thread.CurrentThread;
            if (!events_SendToOthersQueue_ByThreadMap.TryGetValue(currentThread, out RingBuffer<IGONetEvent> eventQueue))
            {
                // Thread not registered - log error with diagnostic info
                GONetLog.Error($"[GONet] PublishEvents_SentToOthers() called from unregistered thread (ID: {currentThread.ManagedThreadId}, Name: '{currentThread.Name}'). " +
                    $"This indicates a bug in GONet initialization. Registered threads: {string.Join(", ", events_SendToOthersQueue_ByThreadMap.Keys.Select(t => $"{t.ManagedThreadId}:{t.Name}"))}");
                return; // Early exit - can't publish events without queue
            }

            // DIAGNOSTIC: Track queue size before draining
            int queueSizeBeforeDrain = eventQueue.Count;
            if (queueSizeBeforeDrain > publishEventsDiag_maxQueueSizeSeen)
            {
                publishEventsDiag_maxQueueSizeSeen = queueSizeBeforeDrain;
            }

            int eventsThisCall = 0;
            IGONetEvent @event;
            while (eventQueue.TryRead(out @event))
            {
                eventsThisCall++;
                try
                {
                    // DIAGNOSTIC: Track event type
                    string eventTypeName = @event.GetType().Name;
                    if (publishEventsDiag_eventTypeCountsSinceLastLog.TryGetValue(eventTypeName, out int typeCount))
                    {
                        publishEventsDiag_eventTypeCountsSinceLastLog[eventTypeName] = typeCount + 1;
                    }
                    else
                    {
                        publishEventsDiag_eventTypeCountsSinceLastLog[eventTypeName] = 1;
                    }

                    EventBus.Publish(@event);
                }
                catch (Exception e)
                {
                    const string BOO = "Boo. Publishing this event failed. Error.Message: ";
                    GONetLog.Error(string.Concat(BOO, e.Message));
                }
            }

            publishEventsDiag_totalEventsPublished += eventsThisCall;
            publishEventsDiag_totalPublishTimeTicks += (HighResolutionTimeUtils.UtcNowTicks - methodStartTicks);

            // DIAGNOSTIC: Periodic logging every 2 seconds
            long nowTicks = HighResolutionTimeUtils.UtcNowTicks;
            if (nowTicks - publishEventsDiag_lastLogTicks > PERSISTENT_EVENT_DIAG_LOG_INTERVAL_TICKS)
            {
                double publishTimeMs = publishEventsDiag_totalPublishTimeTicks / 10000.0;
                double avgEventsPerCall = publishEventsDiag_publishCallCount > 0
                    ? (double)publishEventsDiag_totalEventsPublished / publishEventsDiag_publishCallCount
                    : 0;

                // Build event type breakdown
                var typeBreakdown = new System.Text.StringBuilder();
                foreach (var kvp in publishEventsDiag_eventTypeCountsSinceLastLog)
                {
                    if (typeBreakdown.Length > 0) typeBreakdown.Append(", ");
                    typeBreakdown.Append(kvp.Key).Append("=").Append(kvp.Value);
                }

                // Also check all thread queues for accumulation
                int totalQueuedAcrossThreads = 0;
                int threadCount = 0;
                foreach (var kvp in events_SendToOthersQueue_ByThreadMap)
                {
                    totalQueuedAcrossThreads += kvp.Value.Count;
                    threadCount++;
                }

                // INSTRUMENTATION (Dec 2025): Include map sizes in diagnostics
                // COMMENTED (log cleanup) - fires every 2 seconds, very spammy
                /*int byGONetIdCount = gonetParticipantByGONetIdMap.Count;
                int byInstantiationIdCount = gonetParticipantByGONetIdAtInstantiationMap.Count;
                GONetLog.Warning($"[PUBLISH-DIAG] Calls={publishEventsDiag_publishCallCount}, TotalEvents={publishEventsDiag_totalEventsPublished}, " +
                    $"AvgEvents/Call={avgEventsPerCall:F1}, MaxQueueSize={publishEventsDiag_maxQueueSizeSeen}, " +
                    $"TotalTime={publishTimeMs:F2}ms, ThreadQueues={threadCount}, QueuedNow={totalQueuedAcrossThreads}, " +
                    $"byGONetId={byGONetIdCount}, byInstId={byInstantiationIdCount} | Types: [{typeBreakdown}]");*/

                // Reset counters
                publishEventsDiag_lastLogTicks = nowTicks;
                publishEventsDiag_totalEventsPublished = 0;
                publishEventsDiag_maxQueueSizeSeen = 0;
                publishEventsDiag_publishCallCount = 0;
                publishEventsDiag_totalPublishTimeTicks = 0;
                publishEventsDiag_eventTypeCountsSinceLastLog.Clear();
            }
        }


        static uint lastAssignedGONetIdRaw = GONetParticipant.GONetIdRaw_Unset;
        static uint client_lastServerGONetIdRawForRemoteControl = GONetParticipant.GONetIdRaw_Unset; // Used in GetNextAvailableGONetIdRaw for legacy flow
        // NOTE: Batch management now handled by GONetIdBatchManager


        /// <summary>
        /// Counter for generating unique chunk IDs for multi-chunk messages.
        /// Used by PersistentEvents_BundleChunk to identify which chunks belong together.
        /// </summary>
        static int lastAssignedChunkId = 0;

        /// <summary>
        /// Generates a unique chunk ID for identifying multi-chunk messages.
        /// Thread-safe for use across multiple connections.
        /// </summary>
        private static uint GenerateUniqueChunkId()
        {
            return (uint)System.Threading.Interlocked.Increment(ref lastAssignedChunkId);
        }

        #region GONetBehaviour Lifecycle (Tick, Register, GONetReady)

        private static readonly HashSet<GONetBehaviour> tickReceivers = new HashSet<GONetBehaviour>();
        private static readonly HashSet<GONetBehaviour> tickReceivers_awaitingAdd = new HashSet<GONetBehaviour>();
        private static readonly HashSet<GONetBehaviour> tickReceivers_awaitingRemove = new HashSet<GONetBehaviour>();

        // PERFORMANCE: Use GONet's ArrayPool for zero-GC iteration during Tick() calls
        // Pool manages array lifecycle - borrow, use, return. Zero allocations after warmup.
        private static readonly Utils.ArrayPool<GONetBehaviour> tickReceivers_arrayPool =
            new Utils.ArrayPool<GONetBehaviour>(initialSize: 1, growByCount: 1, arraySizeMinimum: 10, arraySizeMaximum: 500);

        internal static void AddTickReceiver(GONetBehaviour gONetBehaviour)
        {
            tickReceivers_awaitingAdd.Add(gONetBehaviour);
        }

        internal static void RemoveTickReceiver(GONetBehaviour gONetBehaviour)
        {
            tickReceivers_awaitingRemove.Add(gONetBehaviour);
        }

        private static readonly HashSet<GONetBehaviour> allGONetBehaviours = new(1000);

        /// <summary>
        /// Tracks GONetIds that have already published DeserializeInitAllCompleted to prevent duplicate OnGONetReady() calls.
        /// Ensures exactly-once delivery across all publication paths:
        ///
        /// PATH 2: ProcessIncomingBytes_DeserializeAll_INTERNAL (line ~6114) - Remote scene-defined participants receiving first network sync
        /// PATH 3: GONetLocal.AddToLookupOnceAuthorityIdKnown (line ~135) - GONetLocal itself when added to lookup
        /// PATH 4: GONetLocal.AddToLookupOnceAuthorityIdKnown (line ~154) - Scene-defined IsMine participants (may start before GONetLocal ready)
        /// PATH 5: GONetLocal.AddIfAppropriate (line ~204) - Runtime-spawned IsMine participants (after GONetLocal already in lookup)
        /// PATH 6: GONetParticipantCompanionBehaviour.Start() (GONetBehaviour.cs ~311) - Runtime-added COMPONENTS via GONetRuntimeComponentInitializer
        /// PATH 7: CompleteRemoteInstantiation (line ~1352) - Remote runtime-spawned participants (received via network)
        /// PATH 8: Start_AutoPropagateInstantiation_IfAppropriate (line ~4611) - Client-spawned remotely-controlled participants (projectiles with server authority)
        ///
        /// REMOVED: Path 1 (Start) - Caused race conditions, redundant with paths above
        ///
        /// NOTE: Path 6 is special - it doesn't publish DeserializeInitAllCompleted, it directly calls OnGONetReady() on the component
        /// for ALL ready participants. This ensures components added mid-game don't miss participants that became ready earlier.
        ///
        /// This deduplication acts as defense-in-depth to guarantee exactly-once OnGONetReady() delivery for Paths 2-5, 7-8.
        /// Path 6 doesn't use this system (it's component-scoped, not participant-scoped).
        /// </summary>
        private static readonly HashSet<uint> deserializeInitPublishedGONetIds = new HashSet<uint>();

        /// <summary>
        /// Attempts to mark a GONetId as having published DeserializeInitAllCompleted.
        /// Returns true if this is the first publication (should publish), false if already published (skip).
        /// Thread-safe due to HashSet.Add() being atomic for the check-and-insert operation.
        /// </summary>
        internal static bool TryMarkDeserializeInitPublished(uint gonetId)
        {
            return deserializeInitPublishedGONetIds.Add(gonetId);
        }

        internal static void RegisterBehaviour(GONetBehaviour gONetBehaviour)
        {
            allGONetBehaviours.Add(gONetBehaviour);
        }
        internal static void UnregisterBehaviour(GONetBehaviour gONetBehaviour)
        {
            allGONetBehaviours.Remove(gONetBehaviour);
        }

        /// <summary>
        /// Broadcasts OnHostFailoverCompleted to all registered GONetBehaviours.
        /// Called from GONetHostFailoverManager during failover completion.
        /// </summary>
        internal static void BroadcastHostFailoverCompleted(ushort newHostAuthorityId, ushort originalPeerAuthorityId, bool isSelf)
        {
            using (var enumerator = allGONetBehaviours.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    GONetBehaviour behaviour = enumerator.Current;
                    try
                    {
                        behaviour.OnHostFailoverCompleted(newHostAuthorityId, originalPeerAuthorityId, isSelf);
                    }
                    catch (System.Exception ex)
                    {
                        GONetLog.Error($"[Failover] Exception in OnHostFailoverCompleted for '{behaviour.GetType().Name}': {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Refreshes rigidbody owner-only settings after a local authority ID change.
        /// This is needed because IsMine can change without an OwnerAuthorityId change.
        /// </summary>
        private static int RefreshRigidBodySettingsForAuthorityChange(string reason)
        {
            int updatedCount = 0;

            foreach (var kvp in gonetParticipantByGONetIdMap)
            {
                GONetParticipant participant = kvp.Value;
                if (participant == null)
                {
                    continue;
                }

                if (!participant.IsRigidBodyOwnerOnlyControlled)
                {
                    continue;
                }

                if (participant.myRigidBody == null && participant.myRigidBody2D == null)
                {
                    continue;
                }

                participant.SetRigidBodySettingsConsideringOwner();
                updatedCount++;
            }

            GONetLog.Info($"[Physics] Refreshed rigidbody owner settings for {updatedCount} participants after authority change (reason='{reason}', myAuth={MyAuthorityId})");

            return updatedCount;
        }

        /// <summary>
        /// CRITICAL FIX (Dec 2025): Registers all non-mine objects in SoA after demotion.
        ///
        /// When MyAuthorityId changes during demotion, objects that were IsMine=true become IsMine=false.
        /// However, OnOwnerAuthorityIdChanged is never called (OwnerAuthorityId doesn't change), so these
        /// objects never get registered in SoA. Without SoA registration, sync data from the new host
        /// won't be applied, causing objects to be stuck or have crazy positions.
        ///
        /// This function iterates all GNPs and registers any that:
        /// 1. Have IsMine=false (we don't own them anymore)
        /// 2. Are not already registered in SoA
        /// </summary>
        /// <returns>Number of objects registered in SoA.</returns>
        internal static int RegisterNonMineObjectsInSoAAfterDemotion()
        {
            const int MAX_DEMOTION_REG_DIAG = 20;

            int registeredCount = 0;
            int alreadyRegisteredCount = 0;
            int forcedReRegisterCount = 0;
            int forcedServerOwnedCount = 0;
            int forcedMissingLookupCount = 0;
            int forcedMismatchRepairCount = 0;
            int skippedNullCount = 0;
            int skippedMineCount = 0;
            int skippedUnsetIdCount = 0;
            int skippedSoAUninitCount = 0;
            int skippedNoCompanionCount = 0;
            int skippedNoBlendableCount = 0;
            int skippedNoTransformCount = 0;
            int duplicateReferenceCount = 0;
            int diagLoggedCount = 0;

            var seen = new HashSet<GONetParticipant>();

            bool TryGetTransformSyncRequirements(GONetParticipant participant, out bool requiresPosition, out bool requiresRotation)
            {
                requiresPosition = false;
                requiresRotation = false;

                if (participant == null)
                {
                    return false;
                }

                if (!activeAutoSyncCompanionsByCodeGenerationIdMap.TryGetValue(participant.CodeGenerationId, out var companionMap))
                {
                    return false;
                }

                if (!companionMap.TryGetValue(participant, out var companion) || companion == null)
                {
                    return false;
                }

                int valuesCount = companion.valuesCount;
                for (int i = 0; i < valuesCount; ++i)
                {
                    AutoMagicalSync_ValueMonitoringSupport_ChangedValue changesSupport = companion.valuesChangesSupport[i];
                    if (changesSupport == null)
                    {
                        continue;
                    }

                    if (!IsTransformSyncMember(changesSupport.memberName))
                    {
                        continue;
                    }

                    if (changesSupport.memberName == "position")
                    {
                        requiresPosition = true;
                    }
                    else if (changesSupport.memberName == "rotation")
                    {
                        requiresRotation = true;
                    }

                    if (requiresPosition && requiresRotation)
                    {
                        break;
                    }
                }

                return requiresPosition || requiresRotation;
            }

            void ConsiderParticipant(GONetParticipant participant)
            {
                if (participant == null)
                {
                    skippedNullCount++;
                    return;
                }

                if (!seen.Add(participant))
                {
                    duplicateReferenceCount++;
                    return;
                }

                if (participant.IsMine)
                {
                    skippedMineCount++;
                    return;
                }

                bool requiresPosition;
                bool requiresRotation;
                bool hasTransformSync = TryGetTransformSyncRequirements(participant, out requiresPosition, out requiresRotation);
                bool missingTransformLookup = false;
                bool mismatchedTransformLookup = false;
                string mismatchDetails = null;
                if (hasTransformSync && SoAData.IsInitialized && participant.GONetId != GONetParticipant.GONetId_Unset)
                {
                    if (requiresPosition)
                    {
                        if (soaPositionLookup == null || !soaPositionLookup.TryGetValue(participant.GONetId, out var posLookup))
                        {
                            missingTransformLookup = true;
                        }
                        else if (!TryMatchTransformFromPositionLookup(participant.transform, posLookup, out string actualName))
                        {
                            mismatchedTransformLookup = true;
                            mismatchDetails = string.Concat(mismatchDetails, mismatchDetails == null ? "" : "; ",
                                $"POS expected='{participant.name}' actual='{actualName}'");
                        }
                    }

                    if (requiresRotation)
                    {
                        if (soaRotationLookup == null || !soaRotationLookup.TryGetValue(participant.GONetId, out var rotLookup))
                        {
                            missingTransformLookup = true;
                        }
                        else if (!TryMatchTransformFromRotationLookup(participant.transform, rotLookup, out string actualName))
                        {
                            mismatchedTransformLookup = true;
                            mismatchDetails = string.Concat(mismatchDetails, mismatchDetails == null ? "" : "; ",
                                $"ROT expected='{participant.name}' actual='{actualName}'");
                        }
                    }
                }

                bool forceReRegister = hasTransformSync &&
                                       (participant.OwnerAuthorityId == OwnerAuthorityId_Server || missingTransformLookup || mismatchedTransformLookup);

                if (participant.v2_isRegisteredInSoA)
                {
                    if (!forceReRegister)
                    {
                        alreadyRegisteredCount++;
                        return;
                    }

                    if (participant.GONetId == GONetParticipant.GONetId_Unset)
                    {
                        skippedUnsetIdCount++;
                        return;
                    }

                    if (SoAData.IsInitialized)
                    {
                        string reason = missingTransformLookup
                            ? "post-demotion-missing-lookup"
                            : (mismatchedTransformLookup ? "post-demotion-mismatch" : "post-demotion-server-owned");
                        SoA_ClearAllLookupsForGONetId(participant.GONetId, reason, scanAllStreams: true);
                        participant.v2_isRegisteredInSoA = false;
                    }

                    forcedReRegisterCount++;
                    if (participant.OwnerAuthorityId == OwnerAuthorityId_Server)
                    {
                        forcedServerOwnedCount++;
                    }
                    if (missingTransformLookup)
                    {
                        forcedMissingLookupCount++;
                    }
                    if (mismatchedTransformLookup)
                    {
                        forcedMismatchRepairCount++;
                        if (diagLoggedCount < MAX_DEMOTION_REG_DIAG)
                        {
                            diagLoggedCount++;
                            GONetLog.Warning($"[SoA-REG-MISMATCH] name='{participant.name}' GONetId={participant.GONetId} owner={participant.OwnerAuthorityId} {mismatchDetails}");
                        }
                    }
                }

                SoARegistrationResult result;
                bool registered = TryRegisterObjectInSoA(participant, out result);
                if (registered)
                {
                    registeredCount++;
                    return;
                }

                switch (result)
                {
                    case SoARegistrationResult.SkippedNullParticipant:
                        skippedNullCount++;
                        break;
                    case SoARegistrationResult.SkippedUnsetGONetId:
                        skippedUnsetIdCount++;
                        break;
                    case SoARegistrationResult.SkippedSoANotInitialized:
                        skippedSoAUninitCount++;
                        break;
                    case SoARegistrationResult.SkippedNoCompanion:
                        skippedNoCompanionCount++;
                        break;
                    case SoARegistrationResult.SkippedNoBlendableMembers:
                        skippedNoBlendableCount++;
                        break;
                    case SoARegistrationResult.RegisteredNonTransformOnly:
                        skippedNoTransformCount++;
                        break;
                    case SoARegistrationResult.AlreadyRegistered:
                        alreadyRegisteredCount++;
                        break;
                    default:
                        break;
                }

                if (diagLoggedCount < MAX_DEMOTION_REG_DIAG)
                {
                    diagLoggedCount++;
                    GONetLog.Warning($"[SoA-REG-SKIP] name='{participant.name}' GONetId={participant.GONetId} owner={participant.OwnerAuthorityId} " +
                                     $"IsMine={participant.IsMine} v2Reg={participant.v2_isRegisteredInSoA} result={result}");
                }
            }

            foreach (var kvp in gonetParticipantByGONetIdMap)
            {
                ConsiderParticipant(kvp.Value);
            }

            foreach (var kvp in gonetParticipantByGONetIdAtInstantiationMap)
            {
                ConsiderParticipant(kvp.Value);
            }

            GONetLog.Info($"[Handoff] SoA post-demotion registration: registered={registeredCount}, already={alreadyRegisteredCount}, " +
                          $"repaired={forcedReRegisterCount}, serverOwnedRepair={forcedServerOwnedCount}, missingLookupRepair={forcedMissingLookupCount}, mismatchRepair={forcedMismatchRepairCount}, " +
                          $"mine={skippedMineCount}, unsetId={skippedUnsetIdCount}, noCompanion={skippedNoCompanionCount}, " +
                          $"noBlendable={skippedNoBlendableCount}, noTransform={skippedNoTransformCount}, " +
                          $"soAUninit={skippedSoAUninitCount}, nullRefs={skippedNullCount}, dupRefs={duplicateReferenceCount}");

            return registeredCount;
        }

        /// <summary>
        /// Broadcasts OnHostDemoted to all registered GONetBehaviours on the demoted host.
        /// </summary>
        internal static void BroadcastHostDemoted(
            ushort previousHostAuthorityId,
            ushort previousHostOriginalAuthorityId,
            ushort demotedHostNewAuthorityId,
            ushort newHostAuthorityId,
            ushort newHostOriginalAuthorityId,
            uint newHostEpoch,
            bool wasVoluntary)
        {
            using (var enumerator = allGONetBehaviours.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    GONetBehaviour behaviour = enumerator.Current;
                    try
                    {
                        behaviour.OnHostDemoted(
                            previousHostAuthorityId,
                            previousHostOriginalAuthorityId,
                            demotedHostNewAuthorityId,
                            newHostAuthorityId,
                            newHostOriginalAuthorityId,
                            newHostEpoch,
                            wasVoluntary);
                    }
                    catch (System.Exception ex)
                    {
                        GONetLog.Error($"[Failover] Exception in OnHostDemoted for '{behaviour.GetType().Name}': {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Checks if the passed in <paramref name="gonetParticipant"/> is fully initialized and ready for use.
        /// This means:
        /// - GONetId is assigned
        /// - GONetLocal is available in the lookup
        /// - Client/Server status is known
        /// - If client, fully initialized with server
        /// </summary>
        public static bool IsGONetReady(GONetParticipant gonetParticipant)
        {
            // Check basic participant initialization
            if (gonetParticipant == null ||
                gonetParticipant.OwnerAuthorityId == OwnerAuthorityId_Unset ||
                gonetParticipant.gonetId_raw == GONetParticipant.GONetIdRaw_Unset ||
                !gonetParticipant.IsInternallyConfigured)
            {
                return false;
            }

            // Pooled inactive objects are intentionally not "ready" while in the pool.
            if (gonetParticipant.IsPooledInactive)
            {
                return false;
            }

            // Check client/server status is known
            if (!IsClientVsServerStatusKnown)
            {
                return false;
            }

            // If we're a client, ensure client instance exists and is fully initialized
            // HOST MODE EXCEPTION: If we're ALSO the server, skip initialization check - HOST is always "initialized" with itself
            if (IsClient && !IsServer)
            {
                if (GONetClient == null)
                {
                    return false; // Client but no client instance - not ready
                }

                if (!GONetClient.IsInitializedWithServer)
                {
                    return false; // Client exists but not initialized with server
                }
            }

            // Check GONetLocal lookup is available
            if (GONetLocal.LookupByAuthorityId == null)
            {
                return false;
            }

            // Use the indexer to look up the GONetLocal for this participant's authority ID
            // The indexer returns null if not found (safe, no exceptions)
            GONetLocal local = GONetLocal.LookupByAuthorityId[gonetParticipant.OwnerAuthorityId];
            if (local == null)
            {
                return false;
            }

            // LIFECYCLE GATE: Check Unity lifecycle completion (Awake, Start)
            if (!gonetParticipant.didAwakeComplete || !gonetParticipant.didStartComplete)
            {
                return false; // Unity lifecycle not yet complete
            }

            // LIFECYCLE GATE: Check deserialization requirement (if needed for remote objects)
            if (gonetParticipant.requiresDeserializeInit && !gonetParticipant.didDeserializeInitComplete)
            {
                return false; // Waiting for remote sync data (DeserializeInitAllCompleted)
            }

            // LIFECYCLE GATE: Ensure not in limbo state (client batch exhaustion edge case)
            if (gonetParticipant.Client_IsInLimbo)
            {
                return false; // Still waiting for GONetId batch from server
            }

            return true;
        }

        /// <summary>
        /// DIAGNOSTIC HELPER: Returns a string describing which gate condition is blocking OnGONetReady.
        /// Used to identify the exact bottleneck preventing objects from becoming ready.
        /// </summary>
        public static string GetIsGONetReadyBlockingReason(GONetParticipant gonetParticipant)
        {
            if (gonetParticipant == null)
                return "participant is NULL";

            if (gonetParticipant.OwnerAuthorityId == OwnerAuthorityId_Unset)
                return "OwnerAuthorityId not set";

            if (gonetParticipant.gonetId_raw == GONetParticipant.GONetIdRaw_Unset)
                return "GONetId not assigned";

            if (!gonetParticipant.IsInternallyConfigured)
                return "IsInternallyConfigured=false";

            if (!IsClientVsServerStatusKnown)
                return "Client/Server status unknown";

            // HOST MODE EXCEPTION: Skip client initialization checks if we're also the server
            if (IsClient && !IsServer)
            {
                if (GONetClient == null)
                    return "Client mode but GONetClient is NULL";

                if (!GONetClient.IsInitializedWithServer)
                    return "GONetClient not initialized with server";
            }

            if (GONetLocal.LookupByAuthorityId == null)
                return "GONetLocal.LookupByAuthorityId is NULL";

            GONetLocal local = GONetLocal.LookupByAuthorityId[gonetParticipant.OwnerAuthorityId];
            if (local == null)
                return $"GONetLocal not found for OwnerAuthorityId={gonetParticipant.OwnerAuthorityId}";

            if (!gonetParticipant.didAwakeComplete)
                return "Awake() not complete";

            if (!gonetParticipant.didStartComplete)
                return "Start() not complete ← LIKELY BOTTLENECK!";

            if (gonetParticipant.requiresDeserializeInit && !gonetParticipant.didDeserializeInitComplete)
                return "DeserializeInit required but not complete";

            if (gonetParticipant.Client_IsInLimbo)
                return "In limbo (waiting for GONetId batch)";

            return "Unknown reason (all gates should be passed!)";
        }

        /// <summary>
        /// Checks if all OnGONetReady prerequisites are met and broadcasts to all GONetBehaviours if so.
        /// Called after each lifecycle milestone (Awake, Start, DeserializeInit, ExitLimbo).
        ///
        /// This is the simplified gate check that delegates to IsGONetReady() for all validation.
        /// Only fires OnGONetReady once per participant (tracked via didOnGONetReadyFire flag).
        /// </summary>
        internal static void CheckAndPublishOnGONetReady_IfAllConditionsMet(GONetParticipant gonetParticipant)
        {
            // Prevent duplicate calls - OnGONetReady should only fire once
            if (gonetParticipant.didOnGONetReadyFire)
            {
                return; // Already fired, nothing to do
            }

            // Check if all prerequisites are met (delegates to IsGONetReady)
            if (!IsGONetReady(gonetParticipant))
            {
                // Log WHY it's not ready (for debugging scene object lifecycle issues)
                string reason = GetIsGONetReadyBlockingReason(gonetParticipant);
                // GONetLog.Debug($"[SoA-DEBUG] OnGONetReady NOT ready for '{gonetParticipant.name}' (GONetId {gonetParticipant.GONetId}): {reason}");
                return; // Not ready yet, wait for next milestone
            }

            // All conditions met! Mark as fired and broadcast OnGONetReady to all GONetBehaviours
            gonetParticipant.didOnGONetReadyFire = true;

            // Track OnGONetReady in lifecycle
            SoA_LifecycleTracker.OnGONetReady(gonetParticipant.GONetId, gonetParticipant.name, gonetParticipant.IsMine);

            // GONet v2: Register object in SoA if it's non-authority and not already registered
            // CRITICAL FIX (December 2025): Check v2_isRegisteredInSoA to prevent duplicate registration.
            // OnOwnerAuthorityIdChanged may have already registered this object, causing duplicate entries
            // in SoA streams that result in stuck objects (orphaned first entry never receives DATA_IN).
            // GONetLog.Debug($"[SoA-DEBUG] OnGONetReady for '{gonetParticipant.name}' (GONetId {gonetParticipant.GONetId}): IsMine={gonetParticipant.IsMine}, OwnerAuthorityId={gonetParticipant.OwnerAuthorityId}, MyAuthorityId={MyAuthorityId}");
            if (!gonetParticipant.IsMine && !gonetParticipant.v2_isRegisteredInSoA)
            {
                RegisterObjectInSoA(gonetParticipant);
            }

            // Broadcast OnGONetReady to all registered GONetBehaviours
            using (var en = allGONetBehaviours.GetEnumerator())
            {
                while (en.MoveNext())
                {
                    GONetBehaviour gnBehaviour = en.Current;
                    try
                    {
                        gnBehaviour.OnGONetReady(gonetParticipant);
                    }
                    catch (Exception ex)
                    {
                        GONetLog.Error($"[GONet] Exception in OnGONetReady() broadcast for behaviour '{gnBehaviour.GetType().Name}' on '{gnBehaviour.gameObject.name}' with participant '{gonetParticipant.name}': {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }

            // NEW: This participant is now ready - try processing deferred sync bundles
            // (They might have been waiting for THIS participant specifically)
            ProcessDeferredSyncBundlesWaitingForGONetReady();
        }

        #region Velocity-Augmented Sync Helper Methods

        /// <summary>
        /// Velocity-augmented sync: Synthesizes a new value from a previous value and velocity over deltaTime.
        /// Used to generate intermediate positions/rotations from received velocity packets.
        /// </summary>
        /// <param name="lastValue">The last received VALUE (e.g., position, rotation)</param>
        /// <param name="velocity">The received VELOCITY (e.g., linear velocity for Vector3, angular velocity as Vector3 for Quaternion)</param>
        /// <param name="deltaTime">Time elapsed since lastValue was received (in seconds)</param>
        /// <returns>Synthesized value: lastValue + velocity * deltaTime (appropriate for the type)</returns>
        private static GONetSyncableValue SynthesizeValueFromVelocity(
            GONetSyncableValue lastValue,
            GONetSyncableValue velocity,
            float deltaTime)
        {
            GONetSyncableValue result;

            switch (lastValue.GONetSyncType)
            {
                case GONetSyncableValueTypes.System_Single: // float
                {
                    result = new GONetSyncableValue { System_Single = lastValue.System_Single + velocity.System_Single * deltaTime };
                    return result;
                }

                case GONetSyncableValueTypes.UnityEngine_Vector2:
                {
                    result = new GONetSyncableValue { UnityEngine_Vector2 = lastValue.UnityEngine_Vector2 + velocity.UnityEngine_Vector2 * deltaTime };
                    return result;
                }

                case GONetSyncableValueTypes.UnityEngine_Vector3:
                {
                    result = new GONetSyncableValue { UnityEngine_Vector3 = lastValue.UnityEngine_Vector3 + velocity.UnityEngine_Vector3 * deltaTime };
                    return result;
                }

                case GONetSyncableValueTypes.UnityEngine_Vector4:
                {
                    result = new GONetSyncableValue { UnityEngine_Vector4 = lastValue.UnityEngine_Vector4 + velocity.UnityEngine_Vector4 * deltaTime };
                    return result;
                }

                case GONetSyncableValueTypes.UnityEngine_Quaternion:
                {
                    // Angular velocity is stored as Vector3 (axis × radians/sec)
                    if (velocity.GONetSyncType != GONetSyncableValueTypes.UnityEngine_Vector3)
                    {
                        GONetLog.Error($"[VelocitySync] Quaternion synthesis requires Vector3 angular velocity, but received {velocity.GONetSyncType}");
                        return lastValue; // Return unchanged
                    }

                    result = new GONetSyncableValue
                    {
                        UnityEngine_Quaternion = RotateQuaternionByAngularVelocity(
                            lastValue.UnityEngine_Quaternion,
                            velocity.UnityEngine_Vector3, // Angular velocity as Vector3
                            deltaTime)
                    };
                    return result;
                }

                default:
                    GONetLog.Warning($"[VelocitySync] Velocity synthesis not implemented for type {lastValue.GONetSyncType}. Returning lastValue unchanged.");
                    return lastValue;
            }
        }

        /// <summary>
        /// Velocity-augmented sync: Rotates a quaternion by angular velocity over deltaTime.
        /// Angular velocity is represented as Vector3: axis (normalized direction) × magnitude (radians/sec).
        /// </summary>
        /// <param name="current">Current rotation</param>
        /// <param name="angularVelocity">Angular velocity as Vector3 (axis × radians/sec)</param>
        /// <param name="deltaTime">Time elapsed (in seconds)</param>
        /// <returns>New rotation after applying angular velocity</returns>
        private static UnityEngine.Quaternion RotateQuaternionByAngularVelocity(
            UnityEngine.Quaternion current,
            UnityEngine.Vector3 angularVelocity,
            float deltaTime)
        {
            // Calculate total rotation angle in radians
            float angle = angularVelocity.magnitude * deltaTime;

            // Early exit if rotation is negligible (avoid division by zero on normalize)
            if (angle < 0.0001f)
            {
                return current;
            }

            // Extract rotation axis
            UnityEngine.Vector3 axis = angularVelocity.normalized;

            // Create delta rotation quaternion (Unity's AngleAxis expects degrees)
            UnityEngine.Quaternion deltaRot = UnityEngine.Quaternion.AngleAxis(angle * UnityEngine.Mathf.Rad2Deg, axis);

            // Apply delta rotation: current * deltaRot (order matters! This applies rotation in local space)
            UnityEngine.Quaternion result = current * deltaRot;

            return result;
        }

        #endregion

        #endregion

        #endregion

    }
}
