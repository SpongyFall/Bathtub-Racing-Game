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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MemoryPack;
using GONet.Utils;
using UnityEngine;

namespace GONet.DistributedHost
{
    /// <summary>
    /// Current state of failover detection.
    /// </summary>
    public enum FailoverState
    {
        /// <summary>
        /// Host is alive and responsive.
        /// </summary>
        HostAlive,

        /// <summary>
        /// Host heartbeat timed out - potential failure.
        /// </summary>
        HostSuspect,

        /// <summary>
        /// Host confirmed dead - failover in progress.
        /// </summary>
        HostDead,

        /// <summary>
        /// Waiting for vice host to promote.
        /// </summary>
        WaitingForViceHost,

        /// <summary>
        /// This node is self-promoting as vice host.
        /// </summary>
        SelfPromoting,

        /// <summary>
        /// Waiting for another peer to promote via tiebreaker (they have lower authority ID).
        /// </summary>
        WaitingForTiebreaker,

        /// <summary>
        /// Failover complete.
        /// </summary>
        Complete
    }

    /// <summary>
    /// Manages emergency host failover when the current host crashes.
    ///
    /// Detection:
    /// - Heartbeat timeout (2 seconds of silence)
    ///
    /// Promotion logic (Monarch's Heir):
    /// 1. Only the designated vice host self-promotes
    /// 2. Others wait for vice host (100ms)
    /// 3. If vice host also fails, fall back to deterministic tiebreaker (lowest AuthorityID)
    ///
    /// Split-brain prevention:
    /// 1. Higher epoch always wins
    /// 2. Designated vice host wins within same epoch
    /// 3. Lowest authority ID as final tiebreaker
    /// </summary>
    public class GONetHostFailoverManager
    {
        #region Constants

        /// <summary>
        /// Aggressive timeout - 6 missed heartbeats at 8Hz = host is dead.
        /// Prioritize fast failover over waiting for stragglers.
        /// </summary>
        public const float HOST_HEARTBEAT_TIMEOUT_SECONDS = 0.75f;

        /// <summary>
        /// Time to wait for vice host to promote before falling back to tiebreaker.
        /// </summary>
        public const float VICE_HOST_PROMOTION_WAIT_SECONDS = 0.2f; // 200ms

        /// <summary>
        /// Time to wait for tiebreaker peer to promote before self-promoting.
        /// This timeout handles the case where the expected peer failed to promote
        /// or their promotion message was lost in transit.
        /// </summary>
        public const float TIEBREAKER_WAIT_TIMEOUT_SECONDS = 3.0f; // 3 seconds

        /// <summary>
        /// 8Hz heartbeats - doubled frequency to handle unreliable network conditions.
        /// With 0.75s timeout, this allows 6 missed heartbeats before failover triggers.
        /// </summary>
        public const float HOST_HEARTBEAT_INTERVAL_SECONDS = 0.125f;

        /// <summary>
        /// Grace period after failover before checking heartbeats again.
        /// </summary>
        public const float POST_FAILOVER_GRACE_PERIOD_SECONDS = 3.0f;

        /// <summary>
        /// Grace period after graceful handoff demotion before checking heartbeats.
        /// This prevents the old host from self-promoting while the new host connection stabilizes.
        /// </summary>
        public const float GRACEFUL_HANDOFF_GRACE_PERIOD_SECONDS = 30.0f;

        /// <summary>
        /// Grace period after initialization before starting failover detection.
        /// This prevents false failover triggers during connection establishment
        /// when GONetMain.Time.ElapsedSeconds may jump significantly between frames.
        /// </summary>
        public const float INIT_GRACE_PERIOD_SECONDS = 10.0f;

        /// <summary>
        /// Number of times to retry sending emergency promotion message.
        /// Connections may be reconnecting during failover, so retries ensure delivery.
        /// </summary>
        public const int EMERGENCY_PROMOTION_RETRY_COUNT = 5;

        /// <summary>
        /// Interval between emergency promotion retry attempts (in seconds).
        /// </summary>
        public const float EMERGENCY_PROMOTION_RETRY_INTERVAL_SECONDS = 0.2f;

        /// <summary>
        /// Delay after failover before sending reconciliation snapshot.
        /// Allows time for clients to reconnect via SessionPromote before receiving the snapshot.
        /// </summary>
        public const float RECONCILIATION_SNAPSHOT_DELAY_SECONDS = 3.0f;

        /// <summary>
        /// Optional periodic reconciliation interval (disabled by default).
        /// Set to 0 or negative to disable periodic reconciliation.
        /// When enabled, server periodically sends snapshots to catch any drift.
        /// </summary>
        public static float PeriodicReconciliationIntervalSeconds = 0f; // Disabled by default

        /// <summary>
        /// Maximum time to wait for voluntary demotion guard before force-clearing it.
        /// This is a safety-net to prevent demoted clients from being stuck forever if the
        /// new host becomes unreachable. After this timeout, failover detection resumes.
        /// </summary>
        public const float VOLUNTARY_DEMOTION_SAFETY_TIMEOUT_SECONDS = 45.0f;

        /// <summary>
        /// Number of consecutive heartbeats from the target host needed to clear voluntary demotion.
        /// This ensures the new host is stable before allowing the demoted client to participate in failover.
        /// </summary>
        public const int VOLUNTARY_DEMOTION_CLEAR_HEARTBEAT_COUNT = 3;

        /// <summary>
        /// Delay between post-handoff reconciliation requests from a demoted host.
        /// </summary>
        private const float VOLUNTARY_HANDOFF_RECONCILIATION_RETRY_SECONDS = 4.0f;

        /// <summary>
        /// Total number of post-handoff reconciliation requests to send (initial + retries).
        /// </summary>
        private const int VOLUNTARY_HANDOFF_RECONCILIATION_REQUEST_ATTEMPTS = 2;

        /// <summary>
        /// Late post-handoff reconciliation request delay to catch long-lived objects.
        /// </summary>
        private const float VOLUNTARY_HANDOFF_RECONCILIATION_LATE_SECONDS = 12.0f;

        /// <summary>
        /// Interval between late reconciliation requests (in seconds).
        /// </summary>
        private const float VOLUNTARY_HANDOFF_RECONCILIATION_LATE_RETRY_SECONDS = 10.0f;

        /// <summary>
        /// Total number of late reconciliation requests to send.
        /// </summary>
        private const int VOLUNTARY_HANDOFF_RECONCILIATION_LATE_ATTEMPTS = 4;

        /// <summary>
        /// Delay after the final late reconciliation request before requesting a full state sync.
        /// </summary>
        private const float VOLUNTARY_HANDOFF_FULL_STATE_SYNC_DELAY_SECONDS = 2.0f;

        #endregion

        #region State

        private FailoverState currentState = FailoverState.HostAlive;

        // CRITICAL: Use raw ticks (not synchronized time) for heartbeat tracking!
        // Synchronized time (GONetMain.Time.ElapsedSeconds) can jump when time sync occurs,
        // which causes false failover triggers on late-joining clients.
        // Raw ticks are monotonic and never jump backward or forward during sync.
        private long lastHostHeartbeatRawTicks;
        private long lastHeartbeatSentRawTicks;
        private long stateStartRawTicks;
        private long lastFailoverRawTicks;
        private long lastGracefulHandoffRawTicks;
        private long initializationRawTicks;
        private long lastVoluntaryBlockLogRawTicks;
        private long lastGracePeriodLogRawTicks;
        private long lastHeartbeatSuppressionLogRawTicks;
        private ulong voluntaryDemotionPersistentId;
        private int pendingVoluntaryReconciliationRequests;
        private long pendingVoluntaryReconciliationRawTicks;
        private uint pendingVoluntaryReconciliationEpoch;
        private int pendingVoluntaryReconciliationLateRequests;
        private long pendingVoluntaryReconciliationLateRawTicks;
        private uint pendingVoluntaryReconciliationLateEpoch;
        private long pendingVoluntaryFullStateSyncRawTicks;
        private uint pendingVoluntaryFullStateSyncEpoch;
        private bool pendingVoluntaryFullStateSyncSent;

        // Convert timeout constants to ticks for comparison
        private static readonly long HOST_HEARTBEAT_TIMEOUT_TICKS = (long)(HOST_HEARTBEAT_TIMEOUT_SECONDS * TimeSpan.TicksPerSecond);
        private static readonly long POST_FAILOVER_GRACE_PERIOD_TICKS = (long)(POST_FAILOVER_GRACE_PERIOD_SECONDS * TimeSpan.TicksPerSecond);
        private static readonly long GRACEFUL_HANDOFF_GRACE_PERIOD_TICKS = (long)(GRACEFUL_HANDOFF_GRACE_PERIOD_SECONDS * TimeSpan.TicksPerSecond);
        private static readonly long INIT_GRACE_PERIOD_TICKS = (long)(INIT_GRACE_PERIOD_SECONDS * TimeSpan.TicksPerSecond);
        private static readonly long FAILOVER_DIAGNOSTIC_LOG_INTERVAL_TICKS = (long)(2.0f * TimeSpan.TicksPerSecond);
        private static readonly long VOLUNTARY_DEMOTION_SAFETY_TIMEOUT_TICKS = (long)(VOLUNTARY_DEMOTION_SAFETY_TIMEOUT_SECONDS * TimeSpan.TicksPerSecond);

        /// <summary>
        /// The host authority ID at last heartbeat.
        /// </summary>
        private ushort lastKnownHostAuthorityId;

        /// <summary>
        /// The vice host authority ID at last heartbeat.
        /// </summary>
        private ushort lastKnownViceHostAuthorityId;

        /// <summary>
        /// Vice host authority ID captured at the failover boundary for the current epoch.
        /// Used for deterministic same-epoch conflict resolution even after vice host changes.
        /// </summary>
        private ushort tiebreakViceHostAuthorityIdForCurrentEpoch;

        /// <summary>
        /// Whether this node is currently the host.
        /// </summary>
        private bool isHost;

        /// <summary>
        /// Whether this node is the designated vice host.
        /// </summary>
        private bool isViceHost;

        /// <summary>
        /// Whether the manager is initialized.
        /// </summary>
        private bool isInitialized;

        /// <summary>
        /// Whether we've received at least one heartbeat from the host.
        /// Failover detection doesn't start until we've received a heartbeat.
        /// </summary>
        private bool hasReceivedFirstHeartbeat;

        /// <summary>
        /// Whether this node voluntarily demoted during a graceful handoff.
        /// When true, emergency self-promotion is blocked until a new host is confirmed.
        /// </summary>
        private bool didVoluntarilyDemote;

        /// <summary>
        /// Authority ID of the host we handed off to (for diagnostics and unblocking).
        /// </summary>
        private ushort voluntaryHandoffTargetAuthorityId;

        /// <summary>
        /// Host epoch of the handoff target we committed to.
        /// </summary>
        private uint voluntaryHandoffTargetEpoch;

        /// <summary>
        /// Count of consecutive heartbeats received from the handoff target.
        /// Used to determine when voluntary demotion guard can be safely cleared.
        /// </summary>
        private int voluntaryDemotionHeartbeatCount;

        /// <summary>
        /// Pending emergency promotion message to retry sending.
        /// Null when no retries are pending.
        /// </summary>
        private EmergencyHostPromotionMessage pendingPromotionMessage;

        /// <summary>
        /// Number of retry attempts remaining for emergency promotion.
        /// </summary>
        private int promotionRetryCount;

        /// <summary>
        /// Raw ticks when last emergency promotion was sent.
        /// </summary>
        private long lastPromotionSentRawTicks;

        /// <summary>
        /// Ticks interval between retry attempts.
        /// </summary>
        private static readonly long EMERGENCY_PROMOTION_RETRY_INTERVAL_TICKS = (long)(EMERGENCY_PROMOTION_RETRY_INTERVAL_SECONDS * TimeSpan.TicksPerSecond);

        /// <summary>
        /// The authority ID this node had BEFORE self-promoting to host (1023).
        /// Used in heartbeats so peers can look up standby connections by original ID.
        /// Value is 0 if this is the original server (never promoted from a client).
        /// </summary>
        private ushort selfPromotedFromAuthorityId;

        /// <summary>
        /// Gets the authority ID this node had before promotion (0 if original server).
        /// Used to initialize server_lastAssignedAuthorityId during failover.
        /// </summary>
        public ushort SelfPromotedFromAuthorityId => selfPromotedFromAuthorityId;

        /// <summary>
        /// The ORIGINAL authority ID of the current host (before they promoted to 1023).
        /// This is used by the tiebreaker to exclude the dead peer from consideration.
        /// For example: If Client 1 (authority 2) promoted to host (1023) and then dies,
        /// the tiebreaker must exclude both 1023 AND 2 from candidates.
        /// Value is 0 for the original server (was never a client).
        /// </summary>
        private ushort currentHostOriginalAuthorityId;

        /// <summary>
        /// Set of ALL original authority IDs of hosts that have died during this session.
        /// This accumulates across multiple failovers to prevent the tiebreaker from waiting
        /// for long-dead peers whose entries still exist in the gossip system.
        /// </summary>
        private readonly HashSet<ushort> deadHostOriginalAuthorityIds = new HashSet<ushort>();

        /// <summary>
        /// GONetIds that were destroyed during promotion cleanup but couldn't have despawn messages
        /// sent because GONetServer wasn't available yet. These are delivered to clients via
        /// <see cref="SessionPromoteMessage.DeferredDespawnGONetIds"/> during traffic switchover.
        /// </summary>
        private List<uint> pendingDespawnNotifications = new List<uint>();

        /// <summary>
        /// Raw ticks when the last reconciliation snapshot was sent (or scheduled).
        /// </summary>
        private long pendingReconciliationSnapshotRawTicks;

        /// <summary>
        /// Raw ticks when periodic reconciliation was last sent.
        /// </summary>
        private long lastPeriodicReconciliationRawTicks;

        /// <summary>
        /// The last failover epoch for which we sent a reconciliation snapshot.
        /// Used to avoid sending duplicate snapshots for the same failover.
        /// </summary>
        private uint lastReconciliationEpoch;

        /// <summary>
        /// Reusable HashSet for efficient GONetId lookup during reconciliation.
        /// Allocated once to avoid GC during reconciliation.
        /// </summary>
        private readonly HashSet<uint> reconciliationAliveSet = new HashSet<uint>();

        /// <summary>
        /// Cached snapshot for sending to late-joining clients.
        /// Cleared when a new epoch starts.
        /// </summary>
        private PostFailoverReconciliationSnapshotEvent cachedReconciliationSnapshot;

        #endregion

        #region Events

        /// <summary>
        /// Fired when host heartbeat timeout is detected.
        /// </summary>
        public event Action OnHostHeartbeatTimeout;

        /// <summary>
        /// Fired when host death is confirmed and failover begins.
        /// </summary>
        public event Action<ushort> OnHostDeathConfirmed;

        /// <summary>
        /// Fired when this node self-promotes to host.
        /// </summary>
        public event Action OnSelfPromotedToHost;

        /// <summary>
        /// Fired when a new host is detected (someone else promoted).
        /// Parameter is the new host's authority ID (typically 1023 after promotion).
        /// </summary>
        public event Action<ushort> OnNewHostDetected;

        /// <summary>
        /// Fired when a new host is detected, including their original authority ID.
        /// Parameters: (newHostAuthorityId, originalAuthorityIdOfPromotingPeer)
        /// The originalAuthorityId is critical for hot standby lookup - standby connections
        /// are keyed by the peer's ORIGINAL authority ID, not their post-promotion server ID.
        /// </summary>
        public event Action<ushort, ushort> OnNewHostDetectedWithOriginalId;

        /// <summary>
        /// Fired when failover completes.
        /// </summary>
        public event Action<ushort, uint> OnFailoverComplete;

        /// <summary>
        /// Fired when this node was the host but got demoted by a higher-epoch host.
        /// Parameter is the new host's authority ID.
        /// </summary>
        public event Action<ushort> OnDemotedFromHost;

        #endregion

        #region Singleton

        private static GONetHostFailoverManager instance;
        public static GONetHostFailoverManager Instance => instance ??= new GONetHostFailoverManager();

        private GONetHostFailoverManager()
        {
            // Subscribe to reconciliation snapshot events (client-side processing)
            GONetMain.EventBus.Subscribe<PostFailoverReconciliationSnapshotEvent>(OnReconciliationSnapshotReceived);

            // Subscribe to reconciliation requests (server-side, client-pull model)
            GONetMain.EventBus.Subscribe<ReconciliationRequestEvent>(OnReconciliationRequestReceived);

            // Subscribe to full state sync requests (server-side, post-handoff client recovery)
            GONetMain.EventBus.Subscribe<PostHandoffFullStateSyncRequestEvent>(OnPostHandoffFullStateSyncRequestReceived);
        }

        private void OnReconciliationSnapshotReceived(GONetEventEnvelope<PostFailoverReconciliationSnapshotEvent> envelope)
        {
            ProcessReconciliationSnapshot(envelope.Event);
        }

        /// <summary>
        /// Server-side handler for client reconciliation requests.
        /// Sends the authoritative snapshot to the requesting client.
        /// </summary>
        private void OnReconciliationRequestReceived(GONetEventEnvelope<ReconciliationRequestEvent> envelope)
        {
            if (!GONetMain.IsServer)
                return;

            ushort clientAuthorityId = envelope.SourceAuthorityId;
            uint clientEpoch = envelope.Event.ClientEpoch;

            GONetLog.Info($"[Reconciliation] Received request from client {clientAuthorityId} (clientEpoch={clientEpoch}, serverEpoch={GONetMain.HostEpoch})");

            // Send fresh snapshot to the requesting client
            SendReconciliationSnapshotToClient(clientAuthorityId);
        }

        /// <summary>
        /// Server-side handler for post-handoff full state sync requests.
        /// Sends AllCurrentValues bundles to the requesting client.
        /// </summary>
        private void OnPostHandoffFullStateSyncRequestReceived(GONetEventEnvelope<PostHandoffFullStateSyncRequestEvent> envelope)
        {
            if (!GONetMain.IsServer)
                return;

            ushort clientAuthorityId = envelope.SourceAuthorityId;
            uint clientEpoch = envelope.Event.ClientEpoch;

            GONetLog.Info($"[Reconciliation] Received full state sync request from client {clientAuthorityId} (clientEpoch={clientEpoch}, serverEpoch={GONetMain.HostEpoch})");

            GONetMain.Server_RequestFullStateSyncForClient(clientAuthorityId, "post-handoff");
        }

        /// <summary>
        /// Sends a reconciliation snapshot to a specific client.
        /// Called when a client requests reconciliation after completing late-joiner sync.
        /// </summary>
        private void SendReconciliationSnapshotToClient(ushort clientAuthorityId)
        {
            if (!GONetMain.IsServer || GONetMain.gonetServer == null)
            {
                GONetLog.Warning("[Reconciliation] Cannot send snapshot - not the server");
                return;
            }

            // Build the authoritative list of alive GONetIds
            reconciliationAliveSet.Clear();
            int sceneObjectCount = 0;

            foreach (var gnp in GONetMain.gonetParticipantByGONetIdMap.Values)
            {
                if (gnp == null || gnp.GONetId == GONetParticipant.GONetId_Unset)
                    continue;

                if (gnp.SpawnerPersistentId != 0)
                {
                    reconciliationAliveSet.Add(gnp.GONetId);
                }
                else
                {
                    sceneObjectCount++;
                }
            }

            uint[] aliveGONetIds = new uint[reconciliationAliveSet.Count];
            reconciliationAliveSet.CopyTo(aliveGONetIds);

            var snapshotEvent = new PostFailoverReconciliationSnapshotEvent(
                failoverEpoch: GONetMain.HostEpoch,
                aliveGONetIds: aliveGONetIds,
                serverElapsedSeconds: GONetMain.Time.ElapsedSeconds,
                connectedClientCount: GONetMain.gonetServer?.remoteClients?.Count ?? 0
            );
            snapshotEvent.OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks;

            GONetMain.EventBus.Publish(snapshotEvent);

            GONetLog.Info($"[Reconciliation] Sent snapshot to client {clientAuthorityId}: epoch={GONetMain.HostEpoch}, aliveIds={aliveGONetIds.Length}, sceneObjects={sceneObjectCount}");
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the current failover state.
        /// </summary>
        public FailoverState CurrentState => currentState;

        /// <summary>
        /// Gets whether failover is in progress.
        /// </summary>
        public bool IsFailoverInProgress => currentState != FailoverState.HostAlive &&
                                            currentState != FailoverState.Complete;

        /// <summary>
        /// Gets the last known host authority ID.
        /// </summary>
        public ushort LastKnownHostAuthorityId => lastKnownHostAuthorityId;

        /// <summary>
        /// Gets the last known vice host authority ID.
        /// </summary>
        public ushort LastKnownViceHostAuthorityId => lastKnownViceHostAuthorityId;

        /// <summary>
        /// Gets whether this node voluntarily demoted during a graceful handoff.
        /// </summary>
        public bool DidVoluntarilyDemote => didVoluntarilyDemote;

        /// <summary>
        /// Gets the authority ID of the handoff target for a voluntary demotion.
        /// </summary>
        public ushort VoluntaryHandoffTargetAuthorityId => voluntaryHandoffTargetAuthorityId;

        /// <summary>
        /// Gets the host epoch for the voluntary handoff target.
        /// </summary>
        public uint VoluntaryHandoffTargetEpoch => voluntaryHandoffTargetEpoch;

        /// <summary>
        /// Gets whether we are in a grace period (either initialization or post-failover).
        /// Uses raw monotonic ticks to avoid false positives from time sync jumps.
        /// </summary>
        public bool IsInGracePeriod
        {
            get
            {
                long nowRawTicks = GONetMain.Time.RawElapsedTicks;
                // Initialization grace period prevents false failovers during connection establishment
                if (nowRawTicks - initializationRawTicks < INIT_GRACE_PERIOD_TICKS) return true;
                // Post-failover grace period prevents cascading failovers
                if (nowRawTicks - lastFailoverRawTicks < POST_FAILOVER_GRACE_PERIOD_TICKS) return true;
                // Graceful handoff grace period prevents demoted host from self-promoting
                if (lastGracefulHandoffRawTicks != 0 &&
                    nowRawTicks - lastGracefulHandoffRawTicks < GRACEFUL_HANDOFF_GRACE_PERIOD_TICKS) return true;
                return false;
            }
        }

        /// <summary>
        /// Gets the time in seconds since the last heartbeat was received from the host.
        /// Returns 0 if no heartbeat has been received yet.
        /// Uses raw monotonic ticks to provide accurate time since heartbeat.
        /// </summary>
        public float TimeSinceLastHeartbeat
        {
            get
            {
                if (!hasReceivedFirstHeartbeat) return 0f;
                long nowRawTicks = GONetMain.Time.RawElapsedTicks;
                long ticksSinceHeartbeat = nowRawTicks - lastHostHeartbeatRawTicks;
                return (float)(ticksSinceHeartbeat / (double)TimeSpan.TicksPerSecond);
            }
        }

        /// <summary>
        /// Gets whether the heartbeat from the host is stale (exceeded 50% of timeout threshold).
        /// Useful for diagnostics when logging disconnect events.
        /// </summary>
        public bool IsHeartbeatStale => hasReceivedFirstHeartbeat &&
                                        TimeSinceLastHeartbeat > HOST_HEARTBEAT_TIMEOUT_SECONDS * 0.5f;

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the failover manager.
        /// </summary>
        /// <param name="asHost">True if this node is the host</param>
        public void Initialize(bool asHost)
        {
            if (isInitialized) return;

            isHost = asHost;
            isViceHost = false;
            currentState = FailoverState.HostAlive;

            // Use raw monotonic ticks for all timing to avoid time sync jump issues
            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            initializationRawTicks = nowRawTicks;
            lastHostHeartbeatRawTicks = nowRawTicks;
            lastHeartbeatSentRawTicks = nowRawTicks;
            lastFailoverRawTicks = 0;
            lastGracefulHandoffRawTicks = 0;
            lastVoluntaryBlockLogRawTicks = 0;
            lastGracePeriodLogRawTicks = 0;
            lastHeartbeatSuppressionLogRawTicks = 0;

            didVoluntarilyDemote = false;
            voluntaryHandoffTargetAuthorityId = 0;
            voluntaryHandoffTargetEpoch = 0;
            voluntaryDemotionPersistentId = 0;
            pendingVoluntaryReconciliationRequests = 0;
            pendingVoluntaryReconciliationRawTicks = 0;
            pendingVoluntaryReconciliationEpoch = 0;
            pendingVoluntaryReconciliationLateRequests = 0;
            pendingVoluntaryReconciliationLateRawTicks = 0;
            pendingVoluntaryReconciliationLateEpoch = 0;
            pendingVoluntaryFullStateSyncRawTicks = 0;
            pendingVoluntaryFullStateSyncEpoch = 0;
            pendingVoluntaryFullStateSyncSent = false;

            lastKnownHostAuthorityId = GONetMain.CurrentHostIdentity.HostAuthorityId;
            lastKnownViceHostAuthorityId = GONetMain.CurrentHostIdentity.ViceHostAuthorityId;

            // Hosts don't need to receive heartbeats - they send them
            // Clients must receive at least one heartbeat before failover detection starts
            hasReceivedFirstHeartbeat = asHost;

            isInitialized = true;
            GONetLog.Info($"[Failover] Initialized (isHost={asHost})");
        }

        /// <summary>
        /// Shuts down the failover manager.
        /// </summary>
        public void Shutdown()
        {
            if (!isInitialized) return;

            currentState = FailoverState.HostAlive;
            isInitialized = false;

            GONetLog.Info("[Failover] Shut down");
        }

        #endregion

        #region Update Loop

        /// <summary>
        /// Called from GONetMain.Update() to check for host failure and send heartbeats.
        /// </summary>
        public void Update(float elapsedSeconds)
        {
            // ULTRA DEBUG: Only log occasionally (every 30s) to avoid console spam
            // Enabled: Log when heartbeat is stale (> 0.5s) OR state is not HostAlive
            float timeSinceHB = TimeSinceLastHeartbeat;
            // Disabled per-frame logging - re-enable for debugging by uncommenting:
            // if (timeSinceHB > 0.5f || currentState != FailoverState.HostAlive)
            // {
            //     GONetLog.Warning($"[Failover-ULTRA] Update ENTRY: elapsed={elapsedSeconds:F2}s, isInit={isInitialized}, isHost={isHost}, state={currentState}, timeSinceHB={timeSinceHB:F2}s");
            // }

            if (!isInitialized)
            {
                // INSTRUMENTATION: Log EVERY TIME when not initialized (not just periodic)
                GONetLog.Warning($"[Failover-TRACE] Update called but NOT INITIALIZED (elapsed={elapsedSeconds:F1}s)");
                return;
            }
            if (!GONetGlobal.Instance.enableDistributedHostAuthority)
            {
                // INSTRUMENTATION: Log when distributed host is disabled
                GONetLog.Warning($"[Failover-TRACE] Update called but enableDistributedHostAuthority=FALSE (elapsed={elapsedSeconds:F1}s)");
                return;
            }

            // INSTRUMENTATION: Log every 5 seconds to track isHost state during failover
            if ((int)elapsedSeconds % 5 == 0 && (int)(elapsedSeconds * 10) % 10 == 0)
            {
                //GONetLog.Debug($"[Failover-TRACE] Update called: isHost={isHost}, state={currentState}, timeSinceHB={TimeSinceLastHeartbeat:F2}s");
            }

            // CRITICAL DEBUG: Log EVERY frame when not in HostAlive state to trace failover flow
            // Disabled per-frame logging - re-enable for debugging by uncommenting:
            // if (currentState != FailoverState.HostAlive)
            // {
            //     GONetLog.Warning($"[Failover-TRACE] Update FAILOVER: state={currentState}, isHost={isHost}, elapsed={elapsedSeconds:F2}s");
            // }

            if (isHost)
            {
                // Host: Send periodic heartbeats
                UpdateHostHeartbeat(elapsedSeconds);

                // Process any pending emergency promotion retries
                ProcessEmergencyPromotionRetries();
            }
            else
            {
                // Non-host: Monitor for host failure
                UpdateFailoverDetection(elapsedSeconds);
                UpdatePostHandoffReconciliation();
            }
        }

        /// <summary>
        /// Host-side: Send periodic heartbeats.
        /// Uses raw monotonic ticks for consistent timing.
        /// </summary>
        private void UpdateHostHeartbeat(float elapsedSeconds)
        {
            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            if (didVoluntarilyDemote)
            {
                if (nowRawTicks - lastHeartbeatSuppressionLogRawTicks >= FAILOVER_DIAGNOSTIC_LOG_INTERVAL_TICKS)
                {
                    float sinceDemoteSeconds = lastGracefulHandoffRawTicks == 0
                        ? 0f
                        : (float)((nowRawTicks - lastGracefulHandoffRawTicks) / (double)TimeSpan.TicksPerSecond);
                    GONetLog.Warning($"[Failover] Suppressing host heartbeat after voluntary demotion " +
                                     $"(target={voluntaryHandoffTargetAuthorityId}, epoch={voluntaryHandoffTargetEpoch}, sinceDemote={sinceDemoteSeconds:F2}s)");
                    lastHeartbeatSuppressionLogRawTicks = nowRawTicks;
                }
                return;
            }
            long heartbeatIntervalTicks = (long)(HOST_HEARTBEAT_INTERVAL_SECONDS * TimeSpan.TicksPerSecond);
            if (nowRawTicks - lastHeartbeatSentRawTicks >= heartbeatIntervalTicks)
            {
                SendHostHeartbeat();
                lastHeartbeatSentRawTicks = nowRawTicks;
            }
        }

        /// <summary>
        /// Non-host: Monitor for host failure.
        /// CRITICAL: Uses raw monotonic ticks to avoid false failovers from time sync jumps.
        /// </summary>
        private void UpdateFailoverDetection(float elapsedSeconds)
        {
            // INSTRUMENTATION: Log entry to UpdateFailoverDetection periodically
            // Disabled verbose logging - re-enable for debugging by uncommenting:
            float timeSinceHB = TimeSinceLastHeartbeat;
            // if (timeSinceHB > 0.5f && (int)(elapsedSeconds * 2) % 2 == 0)
            // {
            //     GONetLog.Warning($"[Failover-TRACE] UpdateFailoverDetection ENTRY: timeSinceHB={timeSinceHB:F2}s, state={currentState}, isGrace={IsInGracePeriod}");
            // }

            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            if (didVoluntarilyDemote)
            {
                float sinceDemoteSeconds = lastGracefulHandoffRawTicks == 0
                    ? 0f
                    : (float)((nowRawTicks - lastGracefulHandoffRawTicks) / (double)TimeSpan.TicksPerSecond);
                
                // Safety-net: Clear voluntary demotion guard if:
                // 1. Timeout expired - new host should have sent heartbeats by now
                // 2. Main client connection is broken - we need to be able to failover
                bool timeoutExpired = (nowRawTicks - lastGracefulHandoffRawTicks) > VOLUNTARY_DEMOTION_SAFETY_TIMEOUT_TICKS;
                bool clientBroken = !GONetMain.IsClient || GONetMain.GONetClient?.IsConnectedToServer != true;
                
                if (timeoutExpired || (sinceDemoteSeconds > GRACEFUL_HANDOFF_GRACE_PERIOD_SECONDS && clientBroken))
                {
                    string reason = timeoutExpired ? "timeout expired" : "client connection broken";
                    GONetLog.Warning($"[Failover] Voluntary demotion guard force-cleared ({reason}) - " +
                                     $"target={voluntaryHandoffTargetAuthorityId}, epoch={voluntaryHandoffTargetEpoch}, " +
                                     $"sinceDemote={sinceDemoteSeconds:F2}s, clientConnected={!clientBroken}");
                    didVoluntarilyDemote = false;
                    voluntaryDemotionHeartbeatCount = 0;
                    // Fall through to normal failover detection
                }
                else
                {
                    if (nowRawTicks - lastVoluntaryBlockLogRawTicks >= FAILOVER_DIAGNOSTIC_LOG_INTERVAL_TICKS)
                    {
                        GONetLog.Warning($"[Failover] Voluntary demotion active - blocking failover " +
                                         $"(target={voluntaryHandoffTargetAuthorityId}, epoch={voluntaryHandoffTargetEpoch}, " +
                                         $"sinceDemote={sinceDemoteSeconds:F2}s, state={currentState}, timeSinceHB={timeSinceHB:F2}s)");
                        lastVoluntaryBlockLogRawTicks = nowRawTicks;
                    }
                    return;
                }
            }

            // Don't check during grace period (uses raw ticks internally)
            if (IsInGracePeriod)
            {
                LogGracePeriodStatus(nowRawTicks);
                // Disabled verbose logging - re-enable for debugging by uncommenting:
                // if (currentState != FailoverState.HostAlive)
                // {
                //     GONetLog.Warning($"[Failover-TRACE] In grace period during {currentState}, skipping detection (elapsed={elapsedSeconds:F1}s)");
                // }
                return;
            }

            // Don't start failover detection until we've received at least one heartbeat
            if (!hasReceivedFirstHeartbeat)
            {
                // Disabled verbose logging - re-enable for debugging by uncommenting:
                // if ((int)elapsedSeconds % 5 == 0 && (int)(elapsedSeconds * 10) % 10 == 0)
                // {
                //     GONetLog.Debug($"[Failover-TRACE] Waiting for first heartbeat (elapsed={elapsedSeconds:F1}s)");
                // }
                return;
            }

            // Use raw monotonic ticks for accurate time since last heartbeat
            long ticksSinceLastHeartbeat = nowRawTicks - lastHostHeartbeatRawTicks;
            float secondsSinceHeartbeat = (float)(ticksSinceLastHeartbeat / (double)TimeSpan.TicksPerSecond);

            // INSTRUMENTATION: Log heartbeat staleness periodically (every 0.5s when stale)
            // Disabled verbose logging - re-enable for debugging by uncommenting:
            // if (secondsSinceHeartbeat > 0.5f && currentState == FailoverState.HostAlive)
            // {
            //     if (secondsSinceHeartbeat > HOST_HEARTBEAT_TIMEOUT_SECONDS * 0.75f)
            //     {
            //         GONetLog.Warning($"[Failover-TRACE] Heartbeat STALE: {secondsSinceHeartbeat:F2}s since last heartbeat (timeout={HOST_HEARTBEAT_TIMEOUT_SECONDS}s)");
            //     }
            // }

            switch (currentState)
            {
                case FailoverState.HostAlive:
                    if (ticksSinceLastHeartbeat > HOST_HEARTBEAT_TIMEOUT_TICKS)
                    {
                        // MESH IMPROVEMENT: Before declaring host dead based on mesh heartbeat timeout,
                        // consult the main game connection. The mesh intentionally communicates less
                        // frequently to save bandwidth, so stale mesh heartbeats don't necessarily
                        // mean the server is dead. If the main game connection is healthy (actively
                        // receiving sync bundles, RPCs, etc.), the server is clearly alive.
                        bool mainConnectionHealthy = GONetMain.IsClient &&
                                                     GONetMain.GONetClient?.IsConnectedToServer == true;

                        if (mainConnectionHealthy)
                        {
                            // Main game connection says server is alive - defer failover
                            // This will be checked again next frame. If server truly dies,
                            // the main connection will eventually detect it too.
                            // Log sparingly to avoid spam (only once per second)
                            if (secondsSinceHeartbeat % 1.0f < 0.02f) // ~once per second
                            {
                                GONetLog.Warning($"[Failover] Mesh heartbeat stale ({secondsSinceHeartbeat:F2}s) but main game connection healthy - deferring failover decision");
                            }
                            break;
                        }

                        // Both mesh AND main connection indicate problems - host is truly dead
                        TransitionTo(FailoverState.HostDead);
                        GONetLog.Error($"[Failover] Host death detected ({secondsSinceHeartbeat:F2}s without heartbeat, main connection also unhealthy) - authority {lastKnownHostAuthorityId}");
                        OnHostHeartbeatTimeout?.Invoke();
                        OnHostDeathConfirmed?.Invoke(lastKnownHostAuthorityId);
                        BeginFailover();
                    }
                    break;

                case FailoverState.HostSuspect:
                    // Legacy state - should not reach here with aggressive failover
                    // but handle it just in case
                    TransitionTo(FailoverState.HostDead);
                    GONetLog.Error($"[Failover] Host death confirmed (authority {lastKnownHostAuthorityId})");
                    OnHostDeathConfirmed?.Invoke(lastKnownHostAuthorityId);
                    BeginFailover();
                    break;

                case FailoverState.WaitingForViceHost:
                    // Check if vice host has promoted (using raw ticks)
                    long viceHostWaitTicks = (long)(VICE_HOST_PROMOTION_WAIT_SECONDS * TimeSpan.TicksPerSecond);
                    if (nowRawTicks - stateStartRawTicks > viceHostWaitTicks)
                    {
                        // Vice host didn't promote - fall back to tiebreaker
                        GONetLog.Warning("[Failover] Vice host did not promote - falling back to tiebreaker");
                        FallbackToTiebreaker();
                    }
                    break;

                case FailoverState.SelfPromoting:
                    // Complete self-promotion
                    CompleteSelfPromotion();
                    break;

                case FailoverState.WaitingForTiebreaker:
                    // Waiting for another peer with lower authority ID to promote
                    // Heartbeat recovery in OnHostHeartbeatReceived() will handle transition when we receive
                    // heartbeat from the new host with higher epoch

                    // TIMEOUT: If the expected peer doesn't promote within timeout, self-promote
                    // This handles the case where the expected peer failed to promote or their
                    // promotion message was lost (e.g., mesh connections were down)
                    long tiebreakerWaitTicks = (long)(TIEBREAKER_WAIT_TIMEOUT_SECONDS * TimeSpan.TicksPerSecond);
                    long ticksInState = nowRawTicks - stateStartRawTicks;
                    float secondsInState = (float)(ticksInState / (double)TimeSpan.TicksPerSecond);

                    // CRITICAL DEBUG: Log on every frame during tiebreaker wait
                    // Disabled per-frame logging - re-enable for debugging by uncommenting:
                    // GONetLog.Warning($"[Failover-TRACE] WaitingForTiebreaker EVERY FRAME: inState={secondsInState:F2}s, timeout={TIEBREAKER_WAIT_TIMEOUT_SECONDS}s, " +
                    //                $"timeSinceHB={secondsSinceHeartbeat:F2}s, nowTicks={nowRawTicks}, stateStartTicks={stateStartRawTicks}");

                    if (ticksInState > tiebreakerWaitTicks)
                    {
                        GONetLog.Warning($"[Failover] Tiebreaker timeout ({TIEBREAKER_WAIT_TIMEOUT_SECONDS}s) - expected peer did not promote, self-promoting now");
                        TransitionTo(FailoverState.SelfPromoting);
                    }
                    break;

                case FailoverState.Complete:
                    // CRITICAL FIX: After failover completes, transition back to HostAlive to enable
                    // detection of subsequent host failures (double failover scenario).
                    // Without this, the state machine stays in Complete and never detects the next host death.
                    TransitionTo(FailoverState.HostAlive);
                    GONetLog.Debug($"[Failover] Resumed monitoring after failover complete - ready for next failover if needed");
                    break;
            }
        }

        private void LogGracePeriodStatus(long nowRawTicks)
        {
            if (nowRawTicks - lastGracePeriodLogRawTicks < FAILOVER_DIAGNOSTIC_LOG_INTERVAL_TICKS)
            {
                return;
            }

            float nowSeconds = (float)(nowRawTicks / (double)TimeSpan.TicksPerSecond);
            float initDelta = (float)((nowRawTicks - initializationRawTicks) / (double)TimeSpan.TicksPerSecond);
            float failoverDelta = lastFailoverRawTicks == 0
                ? -1f
                : (float)((nowRawTicks - lastFailoverRawTicks) / (double)TimeSpan.TicksPerSecond);
            float handoffDelta = lastGracefulHandoffRawTicks == 0
                ? -1f
                : (float)((nowRawTicks - lastGracefulHandoffRawTicks) / (double)TimeSpan.TicksPerSecond);

            GONetLog.Warning($"[Failover-TRACE] Grace check: now={nowSeconds:F3}, " +
                             $"initDelta={initDelta:F3}/{INIT_GRACE_PERIOD_SECONDS}s, " +
                             $"postFailoverDelta={failoverDelta:F3}/{POST_FAILOVER_GRACE_PERIOD_SECONDS}s, " +
                             $"handoffDelta={handoffDelta:F3}/{GRACEFUL_HANDOFF_GRACE_PERIOD_SECONDS}s, " +
                             $"isInGrace={IsInGracePeriod}, didVoluntarilyDemote={didVoluntarilyDemote}");

            lastGracePeriodLogRawTicks = nowRawTicks;
        }

        #endregion

        #region Host Status Changes

        /// <summary>
        /// Called when this node becomes the host.
        /// </summary>
        public void OnBecameHost()
        {
            isHost = true;
            isViceHost = false;
            currentState = FailoverState.HostAlive;
            lastHeartbeatSentRawTicks = GONetMain.Time.RawElapsedTicks;
            didVoluntarilyDemote = false;
            voluntaryHandoffTargetAuthorityId = 0;
            voluntaryHandoffTargetEpoch = 0;
            voluntaryDemotionHeartbeatCount = 0;
            lastHeartbeatSuppressionLogRawTicks = 0;
            voluntaryDemotionPersistentId = 0;

            GONetLog.Info("[Failover] This node is now the host - starting heartbeats");
        }

        /// <summary>
        /// Called when this node stops being the host.
        /// </summary>
        public void OnStoppedBeingHost(ushort handoffTargetAuthorityId, uint handoffTargetEpoch)
        {
            isHost = false;
            currentState = FailoverState.HostAlive;
            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            lastGracefulHandoffRawTicks = nowRawTicks;
            lastHostHeartbeatRawTicks = nowRawTicks;
            hasReceivedFirstHeartbeat = true;
            didVoluntarilyDemote = true;
            voluntaryHandoffTargetAuthorityId = handoffTargetAuthorityId;
            voluntaryHandoffTargetEpoch = handoffTargetEpoch;
            voluntaryDemotionHeartbeatCount = 0;
            lastVoluntaryBlockLogRawTicks = 0;
            lastGracePeriodLogRawTicks = 0;
            lastHeartbeatSuppressionLogRawTicks = 0;
            voluntaryDemotionPersistentId = 0;

            if (GONetGossipManager.Instance != null)
            {
                if (!GONetGossipManager.Instance.TryGetNodePersistentId(GONetMain.MyAuthorityId, out voluntaryDemotionPersistentId))
                {
                    GONetLog.Warning($"[Failover] Failed to capture persistent ID for voluntary demotion (authority {GONetMain.MyAuthorityId})");
                }
                else
                {
                    GONetLog.Info($"[Failover] Captured local persistent ID for voluntary demotion: {voluntaryDemotionPersistentId:X16} (authority {GONetMain.MyAuthorityId})");
                }
            }
            else
            {
                GONetLog.Warning("[Failover] Cannot capture persistent ID for voluntary demotion - gossip manager unavailable");
            }

            GONetLog.Info($"[Failover] This node is no longer the host - grace period {GRACEFUL_HANDOFF_GRACE_PERIOD_SECONDS:0.##}s, " +
                          $"voluntaryDemoteTarget={handoffTargetAuthorityId}, epoch={handoffTargetEpoch}");
        }

        /// <summary>
        /// Called when this node is designated as vice host.
        /// </summary>
        public void OnDesignatedAsViceHost()
        {
            isViceHost = true;
            GONetLog.Info("[Failover] This node is now the designated vice host (heir apparent)");
        }

        /// <summary>
        /// Called when this node is no longer the vice host.
        /// </summary>
        public void OnRemovedAsViceHost()
        {
            isViceHost = false;
            GONetLog.Info("[Failover] This node is no longer the vice host");
        }

        #endregion

        #region Heartbeat Processing

        /// <summary>
        /// Sends a host heartbeat to all clients.
        /// </summary>
        private void SendHostHeartbeat()
        {
            var heartbeat = new HostHeartbeatMessage
            {
                HostIdentity = GONetMain.CurrentHostIdentity,
                HostMetrics = GONetGossipManager.Instance.LocalMetrics,
                ViceHostScore = GONetViceHostManager.Instance?.CurrentViceHostScore ?? 0f,
                HostPeerOriginalAuthorityId = selfPromotedFromAuthorityId, // 0 if original server, or the peer's original ID if promoted
                HostElapsedTicks = GONetMain.Time.ElapsedTicks,
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };

            GONetGossipIntegration.SendHostHeartbeat(heartbeat);
        }

        /// <summary>
        /// Called when a host heartbeat is received.
        /// </summary>
        public void OnHostHeartbeatReceived(HostHeartbeatMessage message)
        {
            uint receivedEpoch = message.HostIdentity.HostEpoch;
            ushort receivedHostAuthorityId = message.HostIdentity.HostAuthorityId;
            ushort receivedViceHostAuthorityId = message.HostIdentity.ViceHostAuthorityId;
            ushort receivedHostOriginalAuthorityId = message.HostPeerOriginalAuthorityId;

            ushort tiebreakViceHostAuthorityId =
                tiebreakViceHostAuthorityIdForCurrentEpoch != 0
                    ? tiebreakViceHostAuthorityIdForCurrentEpoch
                    : lastKnownViceHostAuthorityId;

            // If we're self-promoting (but not yet host), yield if a better/equal host claim is already alive.
            if (!isHost && currentState == FailoverState.SelfPromoting)
            {
                uint myPlannedEpoch = GONetMain.HostEpoch + 1;
                ushort myOriginalAuthorityId = GONetMain.MyAuthorityId;

                if (IsOtherHostClaimPreferred(
                    otherEpoch: receivedEpoch,
                    otherPromotingOriginalAuthorityId: receivedHostOriginalAuthorityId,
                    currentEpoch: myPlannedEpoch,
                    currentPromotingOriginalAuthorityId: myOriginalAuthorityId,
                    tiebreakViceHostAuthorityId: tiebreakViceHostAuthorityId))
                {
                    GONetLog.Warning($"[Failover] HEARTBEAT CONFLICT - yielding self-promotion to host heartbeat " +
                                   $"(authority {receivedHostAuthorityId}, epoch {receivedEpoch}, originalPeerAuthority {receivedHostOriginalAuthorityId})");

                    AcceptNewHostInternal(
                        newHostAuthorityId: receivedHostAuthorityId,
                        newHostOriginalAuthorityId: receivedHostOriginalAuthorityId,
                        newHostEpoch: receivedEpoch,
                        newViceHostAuthorityId: receivedViceHostAuthorityId,
                        reason: "Heartbeat conflict while self-promoting");
                    return;
                }
            }

            // Host-side: demote on a preferred competing host claim (higher epoch or same-epoch winner).
            if (isHost)
            {
                uint myEpoch = GONetMain.HostEpoch;
                ushort myOriginalAuthorityId = selfPromotedFromAuthorityId != 0 ? selfPromotedFromAuthorityId : (ushort)0;

                if (IsOtherHostClaimPreferred(
                    otherEpoch: receivedEpoch,
                    otherPromotingOriginalAuthorityId: receivedHostOriginalAuthorityId,
                    currentEpoch: myEpoch,
                    currentPromotingOriginalAuthorityId: myOriginalAuthorityId,
                    tiebreakViceHostAuthorityId: tiebreakViceHostAuthorityId))
                {
                    GONetLog.Warning($"[Failover] DEMOTED - received competing host heartbeat " +
                                   $"(authority {receivedHostAuthorityId}, epoch {receivedEpoch}, originalPeerAuthority {receivedHostOriginalAuthorityId})");

                    AcceptNewHostInternal(
                        newHostAuthorityId: receivedHostAuthorityId,
                        newHostOriginalAuthorityId: receivedHostOriginalAuthorityId,
                        newHostEpoch: receivedEpoch,
                        newViceHostAuthorityId: receivedViceHostAuthorityId,
                        reason: "Demoted by preferred host heartbeat");
                    return;
                }

                // We are the host - ignore peer heartbeats if we remain the preferred claim.
                return;
            }

            // Ignore stale epochs.
            if (receivedEpoch < GONetMain.HostEpoch)
            {
                GONetLog.Debug($"[Failover] Ignoring stale heartbeat (epoch {receivedEpoch} < {GONetMain.HostEpoch})");
                return;
            }

            // Late joiner support: learn the promoted host's original authority ID from heartbeats.
            if (currentHostOriginalAuthorityId == 0 && receivedHostOriginalAuthorityId != 0 && GONetMain.HostEpoch > 0)
            {
                currentHostOriginalAuthorityId = receivedHostOriginalAuthorityId;
            }

            // If host claim differs (newer epoch or same epoch but preferred), adopt it.
            if (receivedEpoch != GONetMain.HostEpoch || receivedHostOriginalAuthorityId != currentHostOriginalAuthorityId)
            {
                if (IsOtherHostClaimPreferred(
                    otherEpoch: receivedEpoch,
                    otherPromotingOriginalAuthorityId: receivedHostOriginalAuthorityId,
                    currentEpoch: GONetMain.HostEpoch,
                    currentPromotingOriginalAuthorityId: currentHostOriginalAuthorityId,
                    tiebreakViceHostAuthorityId: tiebreakViceHostAuthorityId))
                {
                    GONetLog.Warning($"[Failover] HEARTBEAT HOST CHANGE - adopting host heartbeat " +
                                   $"(authority {receivedHostAuthorityId}, epoch {receivedEpoch}, originalPeerAuthority {receivedHostOriginalAuthorityId})");

                    AcceptNewHostInternal(
                        newHostAuthorityId: receivedHostAuthorityId,
                        newHostOriginalAuthorityId: receivedHostOriginalAuthorityId,
                        newHostEpoch: receivedEpoch,
                        newViceHostAuthorityId: receivedViceHostAuthorityId,
                        reason: "Host adopted via heartbeat");
                    return;
                }
            }

            // Same host claim: update vice host identity without advancing the epoch.
            if (receivedEpoch == GONetMain.HostEpoch &&
                receivedHostAuthorityId == GONetMain.CurrentHostIdentity.HostAuthorityId &&
                (currentHostOriginalAuthorityId == 0 || receivedHostOriginalAuthorityId == currentHostOriginalAuthorityId) &&
                GONetMain.CurrentHostIdentity.ViceHostAuthorityId != receivedViceHostAuthorityId)
            {
                GONetMain.UpdateViceHostAuthority(receivedViceHostAuthorityId);
            }

            // Update last heartbeat time using raw monotonic ticks
            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            long previousHeartbeatRawTicks = lastHostHeartbeatRawTicks;
            lastHostHeartbeatRawTicks = nowRawTicks;
            lastKnownHostAuthorityId = receivedHostAuthorityId;
            lastKnownViceHostAuthorityId = receivedViceHostAuthorityId;

            if (didVoluntarilyDemote)
            {
                bool epochMatches = voluntaryHandoffTargetEpoch == 0 || receivedEpoch >= voluntaryHandoffTargetEpoch;
                bool hostMatches = voluntaryHandoffTargetAuthorityId == 0 || receivedHostAuthorityId == voluntaryHandoffTargetAuthorityId;
                
                if (epochMatches && hostMatches)
                {
                    voluntaryDemotionHeartbeatCount++;
                    
                    // Clear voluntary demotion guard when:
                    // 1. We've received enough consecutive heartbeats from the new host
                    // 2. Our main client connection is healthy
                    bool clientConnected = GONetMain.IsClient && 
                                          GONetMain.GONetClient?.IsConnectedToServer == true;
                    
                    if (voluntaryDemotionHeartbeatCount >= VOLUNTARY_DEMOTION_CLEAR_HEARTBEAT_COUNT && clientConnected)
                    {
                        GONetLog.Info($"[Failover] Voluntary demotion guard cleared - new host {receivedHostAuthorityId} confirmed (epoch={receivedEpoch}, heartbeats={voluntaryDemotionHeartbeatCount})");
                        QueuePostHandoffReconciliation(receivedEpoch, nowRawTicks);
                        didVoluntarilyDemote = false;
                        voluntaryDemotionHeartbeatCount = 0;
                    }
                }
                else
                {
                    // Heartbeat from different host - reset count
                    voluntaryDemotionHeartbeatCount = 0;
                }
            }

            // Debug: Log all heartbeats to trace delivery
            float timeSinceLastHeartbeat = (float)((nowRawTicks - previousHeartbeatRawTicks) / (double)TimeSpan.TicksPerSecond);
            float nowSeconds = (float)(nowRawTicks / (double)TimeSpan.TicksPerSecond);
            //GONetLog.Debug($"[Heartbeat-PROC] Heartbeat processed: time={nowSeconds:F3}, " +
//                          $"sinceLast={timeSinceLastHeartbeat:F3}s, host={lastKnownHostAuthorityId}, isFirst={!hasReceivedFirstHeartbeat}");

            // Mark that we've received our first heartbeat - failover detection can now start
            if (!hasReceivedFirstHeartbeat)
            {
                hasReceivedFirstHeartbeat = true;
                GONetLog.Info($"[Failover] First heartbeat received from host (authority {message.HostIdentity.HostAuthorityId})");
            }

            // Check if we're the designated vice host
            bool wasViceHost = isViceHost;
            isViceHost = message.HostIdentity.IsViceHost(GONetMain.MyAuthorityId);

            if (isViceHost && !wasViceHost)
            {
                OnDesignatedAsViceHost();
                GONetViceHostManager.Instance.OnDesignatedAsViceHost();
            }
            else if (!isViceHost && wasViceHost)
            {
                OnRemovedAsViceHost();
                GONetViceHostManager.Instance.OnRemovedAsViceHost();
            }

            // If we were in failover detection, reset
            if (currentState == FailoverState.HostSuspect)
            {
                GONetLog.Info("[Failover] Host heartbeat received - false alarm");
                TransitionTo(FailoverState.HostAlive);
            }
        }

        private void QueuePostHandoffReconciliation(uint epoch, long nowRawTicks)
        {
            pendingVoluntaryReconciliationEpoch = epoch;
            pendingVoluntaryReconciliationRequests = VOLUNTARY_HANDOFF_RECONCILIATION_REQUEST_ATTEMPTS;
            pendingVoluntaryReconciliationRawTicks = nowRawTicks;
            pendingVoluntaryReconciliationLateEpoch = epoch;
            pendingVoluntaryReconciliationLateRequests = VOLUNTARY_HANDOFF_RECONCILIATION_LATE_ATTEMPTS;
            pendingVoluntaryReconciliationLateRawTicks = nowRawTicks +
                                                       (long)(VOLUNTARY_HANDOFF_RECONCILIATION_LATE_SECONDS * TimeSpan.TicksPerSecond);
            pendingVoluntaryFullStateSyncEpoch = epoch;
            pendingVoluntaryFullStateSyncRawTicks = 0;
            pendingVoluntaryFullStateSyncSent = false;
        }

        private void UpdatePostHandoffReconciliation()
        {
            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            bool clientReady = GONetMain.IsClient &&
                               GONetMain.GONetClient?.IsConnectedToServer == true &&
                               GONetMain.GONetClient.IsInitializedWithServer;

            bool hasImmediateRequests = pendingVoluntaryReconciliationRequests > 0;
            bool hasLateRequest = pendingVoluntaryReconciliationLateRequests > 0;
            bool hasFullStateRequestPending = !pendingVoluntaryFullStateSyncSent && pendingVoluntaryFullStateSyncEpoch > 0;

            if (!hasImmediateRequests && !hasLateRequest && !hasFullStateRequestPending)
            {
                return;
            }

            if (hasImmediateRequests && nowRawTicks < pendingVoluntaryReconciliationRawTicks)
            {
                hasImmediateRequests = false;
            }

            if (!clientReady && hasImmediateRequests)
            {
                pendingVoluntaryReconciliationRawTicks = nowRawTicks + (long)(0.5f * TimeSpan.TicksPerSecond);
            }

            if (hasImmediateRequests && clientReady)
            {
                var request = new ReconciliationRequestEvent(clientEpoch: pendingVoluntaryReconciliationEpoch)
                {
                    OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
                };
                GONetMain.EventBus.Publish(request);
                pendingVoluntaryReconciliationRequests--;

                GONetLog.Info($"[Reconciliation] Requested post-handoff snapshot after voluntary demotion (epoch={pendingVoluntaryReconciliationEpoch}, remaining={pendingVoluntaryReconciliationRequests})");

                if (pendingVoluntaryReconciliationRequests > 0)
                {
                    pendingVoluntaryReconciliationRawTicks = nowRawTicks +
                                                            (long)(VOLUNTARY_HANDOFF_RECONCILIATION_RETRY_SECONDS * TimeSpan.TicksPerSecond);
                }
                else
                {
                    pendingVoluntaryReconciliationRawTicks = 0;
                }
            }

            if (hasLateRequest)
            {
                if (nowRawTicks >= pendingVoluntaryReconciliationLateRawTicks)
                {
                    if (!clientReady)
                    {
                        pendingVoluntaryReconciliationLateRawTicks = nowRawTicks + (long)(0.5f * TimeSpan.TicksPerSecond);
                    }
                    else
                    {
                        var lateRequest = new ReconciliationRequestEvent(clientEpoch: pendingVoluntaryReconciliationLateEpoch)
                        {
                            OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
                        };
                        GONetMain.EventBus.Publish(lateRequest);
                        pendingVoluntaryReconciliationLateRequests--;

                        GONetLog.Info($"[Reconciliation] Requested late post-handoff snapshot after voluntary demotion (epoch={pendingVoluntaryReconciliationLateEpoch}, remaining={pendingVoluntaryReconciliationLateRequests})");

                        if (pendingVoluntaryReconciliationLateRequests > 0)
                        {
                            pendingVoluntaryReconciliationLateRawTicks = nowRawTicks +
                                                                        (long)(VOLUNTARY_HANDOFF_RECONCILIATION_LATE_RETRY_SECONDS * TimeSpan.TicksPerSecond);
                        }
                        else
                        {
                            pendingVoluntaryReconciliationLateRawTicks = 0;
                        }
                    }
                }
            }

            if (hasFullStateRequestPending)
            {
                if (pendingVoluntaryReconciliationRequests == 0 &&
                    pendingVoluntaryFullStateSyncRawTicks == 0)
                {
                    pendingVoluntaryFullStateSyncRawTicks = nowRawTicks +
                                                            (long)(VOLUNTARY_HANDOFF_FULL_STATE_SYNC_DELAY_SECONDS * TimeSpan.TicksPerSecond);
                }

                if (pendingVoluntaryFullStateSyncRawTicks > 0 && nowRawTicks >= pendingVoluntaryFullStateSyncRawTicks)
                {
                    if (!clientReady)
                    {
                        pendingVoluntaryFullStateSyncRawTicks = nowRawTicks + (long)(0.5f * TimeSpan.TicksPerSecond);
                    }
                    else
                    {
                        uint fullStateEpoch = pendingVoluntaryFullStateSyncEpoch;
                        var fullStateRequest = new PostHandoffFullStateSyncRequestEvent(clientEpoch: fullStateEpoch)
                        {
                            OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
                        };
                        GONetMain.EventBus.Publish(fullStateRequest);
                        pendingVoluntaryFullStateSyncSent = true;
                        pendingVoluntaryFullStateSyncRawTicks = 0;
                        pendingVoluntaryFullStateSyncEpoch = 0;

                        GONetLog.Info($"[Reconciliation] Requested full state sync after voluntary demotion (epoch={fullStateEpoch})");
                    }
                }
            }
        }

        /// <summary>
        /// Called when a mesh heartbeat is received from the host via the hot standby mesh.
        /// This provides redundant failover detection - if main server heartbeats are blocked
        /// but mesh heartbeats are getting through, we know the host is still alive.
        /// </summary>
        public void OnMeshHeartbeatReceived(ushort hostAuthorityId, uint hostEpoch)
        {
            if (!isInitialized || isHost) return;

            // Update last heartbeat time - this is equivalent to receiving a main server heartbeat
            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            lastHostHeartbeatRawTicks = nowRawTicks;

            // If we were in failover detection, reset
            if (currentState == FailoverState.HostSuspect)
            {
                GONetLog.Info($"[Failover] Mesh heartbeat received from host {hostAuthorityId} - false alarm");
                TransitionTo(FailoverState.HostAlive);
            }
        }

        #endregion

        #region Failover Logic

        /// <summary>
        /// Begins the failover process after host death is confirmed.
        /// </summary>
        private void BeginFailover()
        {
            if (didVoluntarilyDemote)
            {
                GONetLog.Warning($"[Failover] BLOCKED self-promotion - node voluntarily demoted to {voluntaryHandoffTargetAuthorityId} (epoch {voluntaryHandoffTargetEpoch})");
                return;
            }

            // CRITICAL: Track the dead host's original authority ID for future tiebreakers.
            // This prevents waiting for long-dead peers whose gossip entries persist.
            if (currentHostOriginalAuthorityId != 0)
            {
                deadHostOriginalAuthorityIds.Add(currentHostOriginalAuthorityId);
                GONetLog.Debug($"[Failover] Added {currentHostOriginalAuthorityId} to dead host tracking (total: {deadHostOriginalAuthorityIds.Count})");
            }

            // INSTRUMENTATION: Log all relevant state at failover start
            GONetLog.Warning($"[Failover-TRACE] BeginFailover START: " +
                           $"myAuthority={GONetMain.MyAuthorityId}, " +
                           $"isViceHost={isViceHost}, " +
                           $"lastKnownViceHostAuthorityId={lastKnownViceHostAuthorityId}, " +
                           $"lastKnownHostAuthorityId={lastKnownHostAuthorityId}, " +
                           $"serverAuthorityId={GONetMain.OwnerAuthorityId_Server}, " +
                           $"deadHostIds=[{string.Join(",", deadHostOriginalAuthorityIds)}]");

            // Capture vice host from the previous epoch for deterministic same-epoch conflict resolution.
            tiebreakViceHostAuthorityIdForCurrentEpoch = lastKnownViceHostAuthorityId;

            if (isViceHost)
            {
                // We are the heir apparent - self-promote immediately
                GONetLog.Info("[Failover] I am the vice host - self-promoting now");
                TransitionTo(FailoverState.SelfPromoting);
            }
            else
            {
                // Check if the vice host is actually valid and different from the dead server
                bool viceHostNotZero = lastKnownViceHostAuthorityId != 0;
                bool viceHostNotDeadHost = lastKnownViceHostAuthorityId != lastKnownHostAuthorityId;
                bool viceHostNotServer = lastKnownViceHostAuthorityId != GONetMain.OwnerAuthorityId_Server;
                bool viceHostIsValid = viceHostNotZero && viceHostNotDeadHost && viceHostNotServer;

                GONetLog.Warning($"[Failover-TRACE] Vice host validity check: " +
                               $"notZero={viceHostNotZero}, notDeadHost={viceHostNotDeadHost}, notServer={viceHostNotServer}, " +
                               $"isValid={viceHostIsValid}");

                if (viceHostIsValid)
                {
                    // Also verify the vice host is still alive via gossip metrics
                    bool viceHostIsAlive = false;
                    var gossipNodes = new System.Text.StringBuilder();
                    foreach (var (authorityId, _) in GONetGossipManager.Instance.GetAllNodeMetrics())
                    {
                        gossipNodes.Append($"{authorityId},");
                        if (authorityId == lastKnownViceHostAuthorityId)
                        {
                            viceHostIsAlive = true;
                        }
                    }
                    GONetLog.Warning($"[Failover-TRACE] Gossip nodes: [{gossipNodes}] viceHostIsAlive={viceHostIsAlive}");

                    if (viceHostIsAlive)
                    {
                        // Wait for vice host to promote
                        GONetLog.Info($"[Failover] Waiting for vice host (authority {lastKnownViceHostAuthorityId}) to promote");
                        TransitionTo(FailoverState.WaitingForViceHost);
                    }
                    else
                    {
                        // Vice host is not in gossip (dead/disconnected) - skip to tiebreaker
                        GONetLog.Warning($"[Failover] Vice host (authority {lastKnownViceHostAuthorityId}) not found in gossip - falling back to tiebreaker");
                        FallbackToTiebreaker();
                    }
                }
                else
                {
                    // Vice host is invalid (0, dead server, or server authority) - skip to tiebreaker immediately
                    GONetLog.Warning($"[Failover] Vice host is invalid (viceHost={lastKnownViceHostAuthorityId}, deadHost={lastKnownHostAuthorityId}) - falling back to tiebreaker immediately");
                    FallbackToTiebreaker();
                }
            }
        }

        /// <summary>
        /// Falls back to deterministic tiebreaker when vice host doesn't promote.
        /// </summary>
        private void FallbackToTiebreaker()
        {
            if (didVoluntarilyDemote)
            {
                GONetLog.Warning($"[Failover] BLOCKED tiebreaker promotion - node voluntarily demoted to {voluntaryHandoffTargetAuthorityId} (epoch {voluntaryHandoffTargetEpoch})");
                return;
            }

            // INSTRUMENTATION: Log tiebreaker start
            GONetLog.Warning($"[Failover-TRACE] FallbackToTiebreaker START: " +
                           $"myAuthority={GONetMain.MyAuthorityId}, " +
                           $"lastKnownHostAuthorityId={lastKnownHostAuthorityId}, " +
                           $"serverAuthorityId={GONetMain.OwnerAuthorityId_Server}");

            // Find lowest authority ID among remaining ALIVE nodes (excluding dead server)
            ushort lowestAuthorityId = GONetMain.MyAuthorityId;
            var candidateNodes = new System.Text.StringBuilder();
            var excludedNodes = new System.Text.StringBuilder();
            var candidateSet = new HashSet<ushort>();

            // CRITICAL FIX (Dec 2025): Get the set of ACTUALLY CONNECTED mesh peers.
            // Gossip may include peers that are known but not reachable (stuck in Connecting state).
            // We must only consider peers we can actually communicate with for tiebreaker.
            var hotStandby = GONetHotStandbyManager.Instance;
            HashSet<ushort> connectedMeshPeers = new HashSet<ushort>();
            if (hotStandby != null)
            {
                foreach (var meshPeerId in hotStandby.GetConnectedMeshPeerAuthorityIds())
                {
                    connectedMeshPeers.Add(meshPeerId);
                }
            }

            foreach (var (authorityId, _) in GONetGossipManager.Instance.GetAllNodeMetrics())
            {
                // CRITICAL: Exclude dead peers from tiebreaker consideration.
                // The gossip system may still have entries for dead peers.
                // We exclude:
                // 1. The current host authority (1023)
                // 2. The server authority constant (1023)
                // 3. The current host's original authority ID (before they promoted)
                // 4. ALL previously dead hosts' original authority IDs (accumulated across failovers)
                // 5. Peers that are NOT actually connected in the mesh (unreachable)
                if (authorityId == lastKnownHostAuthorityId ||
                    authorityId == GONetMain.OwnerAuthorityId_Server ||
                    (currentHostOriginalAuthorityId != 0 && authorityId == currentHostOriginalAuthorityId) ||
                    deadHostOriginalAuthorityIds.Contains(authorityId))
                {
                    excludedNodes.Append($"{authorityId}(dead),");
                    continue;
                }

                // CRITICAL: Only consider peers that are ACTUALLY CONNECTED in the mesh.
                // A peer in gossip but not connected (e.g., stuck in Connecting state) is unreachable
                // and cannot participate in failover coordination.
                if (!connectedMeshPeers.Contains(authorityId))
                {
                    excludedNodes.Append($"{authorityId}(not-connected),");
                    continue;
                }

                candidateSet.Add(authorityId);
                candidateNodes.Append($"{authorityId},");
                if (authorityId < lowestAuthorityId)
                {
                    lowestAuthorityId = authorityId;
                }
            }

            // MESH TOPOLOGY FIX (Dec 2025): Also include peers from the mesh topology
            // that weren't in gossip. This handles late-joining clients who connected
            // after the original host died and weren't broadcast via gossip.
            // Note: We already have connectedMeshPeers from earlier, reuse it.
            var meshPeersAdded = new System.Text.StringBuilder();
            foreach (var meshPeerAuthorityId in connectedMeshPeers)
            {
                // Apply same exclusion rules
                if (meshPeerAuthorityId == lastKnownHostAuthorityId ||
                    meshPeerAuthorityId == GONetMain.OwnerAuthorityId_Server ||
                    (currentHostOriginalAuthorityId != 0 && meshPeerAuthorityId == currentHostOriginalAuthorityId) ||
                    deadHostOriginalAuthorityIds.Contains(meshPeerAuthorityId) ||
                    meshPeerAuthorityId == GONetMain.MyAuthorityId)
                {
                    continue;
                }

                // Add if not already in candidate set from gossip
                if (candidateSet.Add(meshPeerAuthorityId))
                {
                    meshPeersAdded.Append($"{meshPeerAuthorityId},");
                    if (meshPeerAuthorityId < lowestAuthorityId)
                    {
                        lowestAuthorityId = meshPeerAuthorityId;
                    }
                }
            }

            GONetLog.Warning($"[Failover-TRACE] Tiebreaker evaluation: " +
                           $"connectedMeshPeers=[{string.Join(",", connectedMeshPeers)}] " +
                           $"meshPeersAdded=[{meshPeersAdded}] " +
                           $"candidates=[{candidateNodes}] excluded=[{excludedNodes}] " +
                           $"lowestAuthorityId={lowestAuthorityId}, iAmLowest={lowestAuthorityId == GONetMain.MyAuthorityId}, " +
                           $"currentHostOriginalAuthorityId={currentHostOriginalAuthorityId}, " +
                           $"deadHostIds=[{string.Join(",", deadHostOriginalAuthorityIds)}]");

            if (lowestAuthorityId == GONetMain.MyAuthorityId)
            {
                GONetLog.Info("[Failover] I have lowest authority ID - self-promoting via tiebreaker");
                TransitionTo(FailoverState.SelfPromoting);
            }
            else
            {
                GONetLog.Info($"[Failover] Authority {lowestAuthorityId} should promote via tiebreaker - waiting for their promotion");
                TransitionTo(FailoverState.WaitingForTiebreaker);
            }
        }

        /// <summary>
        /// Completes the self-promotion to host.
        /// </summary>
        private void CompleteSelfPromotion()
        {
            // INSTRUMENTATION: Log promotion start
            GONetLog.Warning($"[Failover-TRACE] CompleteSelfPromotion START: myAuthority={GONetMain.MyAuthorityId}");

            // CRITICAL: Preserve time sync offset BEFORE any authority changes.
            // This captures our current offset to the old server so we can maintain
            // time continuity for other clients when we start responding to time sync requests.
            // Without this, clients synced to the old server would experience ~5 second time jumps.
            GONetMain.PreserveTimeOffsetForFailover();

            // CRITICAL: Capture our original authority ID BEFORE promotion
            // Peers have standby connections keyed by our ORIGINAL authority ID, not 1023
            ushort originalAuthorityId = GONetMain.MyAuthorityId;
            selfPromotedFromAuthorityId = originalAuthorityId; // Store for heartbeats

            // CRITICAL: Capture the OLD host's persistent ID BEFORE we become authority 1023.
            // After PromoteToServerAuthority(), we ARE authority 1023, so TryGetNodePersistentId(1023)
            // would return OUR persistent ID instead of the dead host's persistent ID.
            //
            // For double failover: If the dead host was a promoted client (currentHostOriginalAuthorityId != 0),
            // we need to look up THEIR original authority ID, not 1023. The gossip entry for 1023 contains
            // the ORIGINAL server's persistent ID, not the promoted client-host's ID.
            ulong deadHostPersistentId = 0;
            ushort lookupAuthorityId = currentHostOriginalAuthorityId != 0
                ? currentHostOriginalAuthorityId
                : lastKnownHostAuthorityId;

            if (!GONetGossipManager.Instance.TryGetNodePersistentId(lookupAuthorityId, out deadHostPersistentId))
            {
                GONetLog.Warning($"[Failover] Could not find persistent ID for previous host (authority {lookupAuthorityId}, wasPromoted={currentHostOriginalAuthorityId != 0}) - spawner death processing may be incomplete");
            }
            else
            {
                GONetLog.Info($"[Failover] Captured previous host persistent ID: {deadHostPersistentId:X16} (authority {lookupAuthorityId}, wasPromoted={currentHostOriginalAuthorityId != 0})");
            }

            // Promote our authority to server authority ID (1023)
            // This also updates GONetLocal lookup for IsGONetReady() to work on server-owned objects
            GONetMain.PromoteToServerAuthority();

            // INSTRUMENTATION: Log after authority promotion
            GONetLog.Warning($"[Failover-TRACE] Authority promoted: {originalAuthorityId} -> {GONetMain.MyAuthorityId}");

            // NOTE: Client-host loopback establishment is deferred to OnBecameHost() in GONetHotStandby
            // because GONetServer is null until the dormant server is promoted there.

            // Increment epoch and become host
            uint newEpoch = GONetMain.HostEpoch + 1;
            GONetMain.IncrementHostEpoch(GONetMain.MyAuthorityId, 0);

            // Update internal state
            isHost = true;
            isViceHost = false;

            // Notify subsystems
            // CRITICAL: Update gossip authority ID BEFORE changing host status
            // After PromoteToServerAuthority(), our authority ID is now 1023, but the gossip system
            // still has the old authority ID. Without this, gossip broadcasts with the old ID and
            // the entry for authority 1023 becomes stale, causing mesh UI flickering.
            GONetGossipManager.Instance.UpdateLocalAuthorityId(GONetMain.MyAuthorityId);
            // CRITICAL FIX (Jan 2026): Call GONetGossipIntegration.OnHostStatusChanged instead of
            // GONetGossipManager.OnHostStatusChanged. The Integration version also calls
            // OnLocalAuthorityChanged to clear stale remoteMetrics[1023] from the old server.
            // Without this, the gossip aggregate contains the old server's endpoint (e.g., port 1)
            // which overwrites the correct endpoint, causing late-joiners to fail connecting.
            GONetGossipIntegration.OnHostStatusChanged(true);
            GONetViceHostManager.Instance.OnBecameHost();

            // Phase 2.13: Migrate server-owned GNPs to this new host
            // This resets blend buffers so we can start broadcasting sync data
            int migratedGNPCount = MigrateServerOwnedGNPs(originalAuthorityId);

            // Phase 2.14: Cleanup promoting client's orphaned objects (Dec 2025)
            // When this client promotes to server, objects owned by our old authority become orphans
            // because no one sends sync data for that authority anymore.
            List<uint> promotingClientDestroyedGONetIds = new List<uint>();
            int promotingClientCleanupCount = CleanupPromotingClientObjects(originalAuthorityId, promotingClientDestroyedGONetIds);

            // Phase 2.5: Process spawner death for the previous host
            // This destroys player-bound objects (DestroyWhenSpawnerLeaves=true) and logs surviving objects
            // Note: Objects with DestroyWhenSpawnerLeaves=false are already handled by MigrateServerOwnedGNPs above
            // CRITICAL: Use the persistent ID we captured BEFORE promotion, not a lookup (which would return OUR ID now)
            // CRITICAL: Pass originalAuthorityId so our own objects (like GONetLocal) survive even though IsMine is now false
            int destroyedCount = 0, survivedCount = 0;
            if (deadHostPersistentId != 0)
            {
                (destroyedCount, survivedCount) = ProcessSpawnerDeath(deadHostPersistentId, originalAuthorityId);
                GONetLog.Info($"[Failover] Processed spawner death for previous host {lastKnownHostAuthorityId} (persistentId: {deadHostPersistentId:X16}): destroyed={destroyedCount}, survived={survivedCount}");
            }
            else
            {
                GONetLog.Warning($"[Failover] Skipped spawner death processing - could not determine previous host's persistent ID");
            }

            // SAFETY NET (Dec 2025): Cleanup server-owned transient objects that survived ProcessSpawnerDeath.
            // This catches cases where deadHostPersistentId lookup failed or there was a SpawnerPersistentId mismatch.
            // Without this, zombie projectiles/effects remain and cause sync bundle serialization overhead.
            List<uint> safetyNetDestroyedGONetIds = new List<uint>();
            int safetyNetDestroyedCount = CleanupServerOwnedTransientObjects(safetyNetDestroyedGONetIds);
            if (safetyNetDestroyedCount > 0)
            {
                GONetLog.Warning($"[Failover] Safety-net cleanup destroyed {safetyNetDestroyedCount} additional server-owned transient objects");
            }

            // DECEMBER 2025 FIX: Store ALL destroyed GONetIds for deferred despawn notification.
            // At this point GONetServer is null (promotion in-progress), so we can't safely rely on normal
            // event propagation. These ids will be delivered via SessionPromote after OnBecameHost() promotes
            // the dormant server, so clients can apply the despawns BEFORE ReliableNetcode reset suppresses
            // reliable traffic during traffic switchover.
            pendingDespawnNotifications.Clear();
            pendingDespawnNotifications.AddRange(promotingClientDestroyedGONetIds);
            pendingDespawnNotifications.AddRange(safetyNetDestroyedGONetIds);
            if (pendingDespawnNotifications.Count > 0)
            {
                GONetLog.Info($"[Failover] Queued {pendingDespawnNotifications.Count} GONetIds for deferred despawn notification (promoting client: {promotingClientDestroyedGONetIds.Count}, safety net: {safetyNetDestroyedGONetIds.Count})");
            }

            // DIAGNOSTIC: Log IsGONetReady status for all server-owned GNPs after migration
            DiagnoseIsReadyStateAfterPromotion();

            // CRITICAL: Synthesize persistent events (SceneLoadEvents, etc.) for the promoted host
            // Without this, late-joining clients won't receive scene load instructions and will be stuck in lobby
            GONetMain.SynthesizePersistentEventsForPromotedHost();

            // FAILOVER FIX (Dec 2025): Remove any synthesized spawns for objects we just destroyed during promotion.
            // These objects are scheduled for destruction (Unity destroys end-of-frame) and can still appear in
            // gonetParticipantByGONetIdMap during synthesis, which would cause late joiners to spawn "ghost" objects.
            // Use ALL destroyed GONetIds (not just promoting client ones).
            List<uint> allDestroyedGONetIds = new List<uint>(promotingClientDestroyedGONetIds);
            allDestroyedGONetIds.AddRange(safetyNetDestroyedGONetIds);
            CancelSynthesizedSpawns_ForDestroyedGONetIds(allDestroyedGONetIds, "promotion-cleanup");

            // Broadcast emergency promotion with BOTH authority IDs
            var promotionMsg = new EmergencyHostPromotionMessage
            {
                NewHostAuthorityId = GONetMain.MyAuthorityId, // 1023 after promotion
                NewHostEpoch = newEpoch,
                PreviousHostAuthorityId = lastKnownHostAuthorityId,
                PromotingPeerOriginalAuthorityId = originalAuthorityId, // Our ORIGINAL ID for standby lookup
                FailoverReason = "Heartbeat timeout",
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };

            // Send emergency promotion with retries - connections may be reconnecting during failover
            SendEmergencyPromotionWithRetries(promotionMsg);

            TransitionTo(FailoverState.Complete);
            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            lastFailoverRawTicks = nowRawTicks;

            GONetLog.Warning($"[Failover-TRACE] EMERGENCY PROMOTION COMPLETE: " +
                           $"newHost={GONetMain.MyAuthorityId}, " +
                           $"originalAuthority={originalAuthorityId}, " +
                           $"epoch={newEpoch}, " +
                           $"previousHost={lastKnownHostAuthorityId}, " +
                           $"migratedGNPs={migratedGNPCount}");

            OnSelfPromotedToHost?.Invoke();
            OnFailoverComplete?.Invoke(GONetMain.MyAuthorityId, newEpoch);

            // CRITICAL FAILOVER FIX (Dec 2025): Send synthesized persistent events to already-connected hot-standby clients.
            // These clients were marked IsInitializedWithServer=true but never went through the normal init flow that
            // delivers persistent events. Without this, they miss spawn events and have fewer objects than the server.
            // NOTE: Must be called AFTER OnSelfPromotedToHost, which triggers OnBecameHost -> SetPromotedServer.
            GONetMain.Server_SendPersistentEventsToExistingClients();

            // Fire HostFailoverCompletedEvent via EventBus
            GONetMain.EventBus.Publish<GONet.IGONetEvent>(new GONet.HostFailoverCompletedEvent(
                GONetMain.Time.ElapsedTicks,
                GONetMain.MyAuthorityId,
                originalAuthorityId,
                isSelf: true,
                migratedGNPCount));

            // Broadcast OnHostFailoverCompleted to all GONetBehaviours
            GONetMain.BroadcastHostFailoverCompleted(GONetMain.MyAuthorityId, originalAuthorityId, isSelf: true);

            // Start sending heartbeats
            lastHeartbeatSentRawTicks = nowRawTicks;
        }

        /// <summary>
        /// Migrates all server-owned GONetParticipants to this new host after failover.
        /// This resets blend buffers so we can start broadcasting sync data.
        /// Only processes GNPs where OwnerAuthorityId == 1023 (server authority).
        /// </summary>
        /// <param name="originalAuthorityId">Our authority ID before promotion (for callbacks)</param>
        /// <returns>Number of GNPs migrated</returns>
        private int MigrateServerOwnedGNPs(ushort originalAuthorityId)
        {
            int count = 0;

            foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
            {
                GONetParticipant gnp = kvp.Value;
                if (gnp == null) continue;

                // Only migrate server-owned GNPs (OwnerAuthorityId == 1023)
                if (gnp.OwnerAuthorityId != GONetMain.OwnerAuthorityId_Server)
                    continue;

                // Reset blend buffers so we can start broadcasting
                GONetMain.ResetBlendBuffersForOwnershipMigration(gnp);

                // FAILOVER FIX: Reset rigidbody settings now that we're the authority
                // Before failover, this client was non-authority so rigidbody may have been set to kinematic.
                // Now that we're the authority (server), rigidbody should use original settings.
                gnp.SetRigidBodySettingsConsideringOwner();

                // CRITICAL FIX: Clear deserialization requirements since we're now the authority.
                // Before failover, as a client, scene objects were marked with requiresDeserializeInit=true
                // waiting for server sync data. Now WE are the server, so we don't need to wait for anything.
                // Without this, IsGONetReady() returns false and the object appears "stuck".
                gnp.ClearDeserializeInitRequirement();

                // DIAGNOSTIC: Verify IsMine status after migration
                bool isMineNow = gnp.IsMine;
                //GONetLog.Info($"[Failover] Migrated GNP '{gnp.name}' (GONetId: {gnp.GONetId}): IsMine={isMineNow}, OwnerAuthorityId={gnp.OwnerAuthorityId}, MyAuthorityId={GONetMain.MyAuthorityId}");

                // Notify companion behaviours on this GNP
                NotifyCompanionBehaviours(gnp, isMineNow: isMineNow, originalAuthorityId);

                count++;
            }

            GONetLog.Info($"[Failover] Migrated {count} server-owned GNPs to new host (originalAuthority: {originalAuthorityId})");
            return count;
        }

        /// <summary>
        /// Voluntary handoff support: migrate objects owned by the promoting client's old authority to server authority.
        /// This preserves client-owned objects (e.g., projectiles) when the vice host becomes the host.
        /// </summary>
        /// <param name="promotingClientOriginalAuthorityId">The promoting client's authority ID BEFORE promotion.</param>
        /// <param name="reason">Optional reason for diagnostics.</param>
        /// <returns>Number of objects migrated.</returns>
        public int MigratePromotingClientOwnedObjectsToServer(ushort promotingClientOriginalAuthorityId, string reason)
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[Handoff] Cannot migrate promoting client-owned objects - not server");
                return 0;
            }

            if (promotingClientOriginalAuthorityId == 0 || promotingClientOriginalAuthorityId == GONetMain.OwnerAuthorityId_Server)
            {
                return 0;
            }

            List<GONetParticipant> toMigrate = new List<GONetParticipant>();
            int skippedLocalCount = 0;

            HashSet<GONetParticipant> candidates = new HashSet<GONetParticipant>();

            void CollectCandidates(Dictionary<uint, GONetParticipant> map)
            {
                foreach (var kvp in map)
                {
                    if (kvp.Value != null)
                    {
                        candidates.Add(kvp.Value);
                    }
                }
            }

            CollectCandidates(GONetMain.gonetParticipantByGONetIdMap);
            CollectCandidates(GONetMain.gonetParticipantByGONetIdAtInstantiationMap);

            foreach (var gnp in candidates)
            {
                if (gnp == null)
                {
                    continue;
                }

                ushort ownerFromId = 0;
                if (gnp.GONetId != GONetParticipant.GONetId_Unset)
                {
                    ownerFromId = (ushort)(gnp.GONetId & ((1 << GONetParticipant.OWNER_AUTHORITY_ID_BIT_COUNT_USED) - 1));
                }

                if (gnp.OwnerAuthorityId != promotingClientOriginalAuthorityId &&
                    ownerFromId != promotingClientOriginalAuthorityId)
                {
                    continue;
                }

                GONetLocal maybeLocal = gnp.GetComponent<GONetLocal>();
                if (maybeLocal != null)
                {
                    skippedLocalCount++;
                    continue;
                }

                toMigrate.Add(gnp);
            }

            int migratedCount = 0;
            foreach (var gnp in toMigrate)
            {
                if (!GONetMain.Server_AssumeAuthorityOver(gnp))
                {
                    continue;
                }

                GONetMain.ResetBlendBuffersForOwnershipMigration(gnp);
                gnp.SetRigidBodySettingsConsideringOwner();
                gnp.ClearDeserializeInitRequirement();

                NotifyCompanionBehaviours(gnp, isMineNow: true, originalAuthorityId: promotingClientOriginalAuthorityId);

                migratedCount++;
            }

            string reasonSuffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" (reason='{reason}')";
            string skippedSuffix = skippedLocalCount > 0 ? $" (skippedLocal={skippedLocalCount})" : string.Empty;
            GONetLog.Info($"[Handoff] Migrated {migratedCount} participant(s) from authority {promotingClientOriginalAuthorityId} to server{reasonSuffix}{skippedSuffix}");

            return migratedCount;
        }

        /// <summary>
        /// CRITICAL FIX (Dec 2025): Cleanup objects owned by the promoting client's OLD authority.
        /// When a client promotes to server, their client authority (e.g., 2) no longer exists as a sync source.
        /// Objects owned by that authority become orphans because no one sends sync data for them.
        ///
        /// This function handles:
        /// - Preserves the promoting client's GONetLocal (required for promoted host readiness)
        /// - Destroys other transient objects owned by the promoting client's old authority
        /// </summary>
        /// <param name="promotingClientOriginalAuthorityId">The promoting client's authority ID BEFORE promotion</param>
        /// <returns>Number of objects destroyed</returns>
        private int CleanupPromotingClientObjects(ushort promotingClientOriginalAuthorityId)
        {
            return CleanupPromotingClientObjects(promotingClientOriginalAuthorityId, destroyedGONetIds: null);
        }

        private int CleanupPromotingClientObjects(ushort promotingClientOriginalAuthorityId, List<uint> destroyedGONetIds)
        {
            if (promotingClientOriginalAuthorityId == 0 || promotingClientOriginalAuthorityId == GONetMain.OwnerAuthorityId_Server)
            {
                return 0;
            }

            List<GONetParticipant> toDestroy = new List<GONetParticipant>();

            foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
            {
                GONetParticipant gnp = kvp.Value;
                if (gnp == null) continue;

                // Only process objects owned by the promoting client's OLD authority
                if (gnp.OwnerAuthorityId != promotingClientOriginalAuthorityId)
                    continue;

                // IMPORTANT: Do NOT destroy the promoting client's GONetLocal.
                // PromoteToServerAuthority() relies on GONetMain.myLocal to add the temporary 1023 authority mapping
                // so IsGONetReady() works in client-host mode after promotion.
                GONetLocal maybeLocal = gnp.GetComponent<GONetLocal>();
                if (maybeLocal != null)
                {
                    if (GONetMain.myLocal != null && maybeLocal == GONetMain.myLocal)
                    {
                        GONetLog.Info($"[Failover] Preserving promoting client's GONetLocal (GONetId: {gnp.GONetId}) - required for promoted host readiness");
                        continue;
                    }

                    GONetLog.Warning($"[Failover] Found unexpected GONetLocal owned by promoting client's old authority ({promotingClientOriginalAuthorityId}) (GONetId: {gnp.GONetId}) - preserving to avoid breaking readiness");
                    continue;
                }

                // Destroy other transient objects (DestroyWhenSpawnerLeaves=true)
                if (gnp.DestroyWhenSpawnerLeaves)
                {
                    ////GONetLog.Info($"[Failover] Destroying promoting client's transient object '{gnp.name}' (GONetId: {gnp.GONetId}) - would become orphan after promotion");
                    toDestroy.Add(gnp);

                    if (destroyedGONetIds != null && gnp.GONetId != GONetParticipant.GONetId_Unset)
                    {
                        destroyedGONetIds.Add(gnp.GONetId);
                    }
                    continue;
                }

                // Non-transient objects owned by the promoting client survive (they may need manual handling)
                GONetLog.Warning($"[Failover] Promoting client's non-transient object '{gnp.name}' (GONetId: {gnp.GONetId}) survives - may need authority migration");
            }

            int destroyedCount = 0;
            foreach (var gnp in toDestroy)
            {
                try
                {
                    // During self-promotion, these objects are no longer "mine" after authority changes.
                    // Mark as expected to avoid warnings and to prevent OnDestroy from auto-propagating another despawn.
                    if (GONetMain.IsServer && gnp.GONetId != GONetParticipant.GONetId_Unset)
                    {
                        GONetMain.MarkGONetIdDestroyedViaPropagation(gnp.GONetId);
                    }

                    DestroyGNP_LocalOnly(gnp);
                    destroyedCount++;
                }
                catch (System.Exception ex)
                {
                    GONetLog.Error($"[Failover] Failed to destroy promoting client's object '{gnp.name}': {ex.Message}");
                }
            }

            GONetLog.Info($"[Failover] Cleaned up {destroyedCount} objects owned by promoting client's old authority ({promotingClientOriginalAuthorityId})");
            return destroyedCount;
        }

        /// <summary>
        /// HANDOFF FIX (Jan 2025): Clears deserialization requirements for server-owned objects on third-party clients.
        ///
        /// PROBLEM: When a third-party client (pure client that's not the new or old host) receives a handoff commit,
        /// server-owned objects still have requiresDeserializeInit=true from initial scene load. These objects were
        /// already fully synced before the handoff, but the flag was never cleared because clients don't receive
        /// DeserializeInitAllCompleted after handoff (that's only for newly spawned objects).
        ///
        /// RESULT: IsGONetReady() returns false forever → UpdateAfterGONetReady() never runs → ApplyVisualState() never called.
        /// Position/rotation work (SoA blending bypasses IsGONetReady), but Vector2/Vector4/scalar don't (V1 blending path).
        ///
        /// SOLUTION: Clear requiresDeserializeInit for all server-owned objects that were already synced.
        /// Since these objects existed before handoff, they don't need re-initialization - just continued sync data.
        /// </summary>
        /// <returns>Number of objects with cleared deserialization requirements.</returns>
        public int ClearDeserializeInitRequirements_ForServerOwnedObjects()
        {
            int clearedCount = 0;

            foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
            {
                GONetParticipant gnp = kvp.Value;
                if (gnp == null) continue;

                // Only process server-owned objects (authority 1023)
                if (gnp.OwnerAuthorityId != GONetMain.OwnerAuthorityId_Server) continue;

                // Only clear if the flag is blocking (requiresDeserializeInit=true but not complete)
                if (!gnp.requiresDeserializeInit || gnp.didDeserializeInitComplete) continue;

                gnp.ClearDeserializeInitRequirement();
                clearedCount++;
            }

            if (clearedCount > 0)
            {
                GONetLog.Info($"[Handoff] Cleared deserialization requirements for {clearedCount} server-owned objects (third-party client handoff fix)");
            }

            return clearedCount;
        }

        private static void CancelSynthesizedSpawns_ForDestroyedGONetIds(List<uint> destroyedGONetIds, string reason)
        {
            if (destroyedGONetIds == null || destroyedGONetIds.Count == 0)
            {
                return;
            }

            int publishedCount = 0;
            int count = destroyedGONetIds.Count;
            for (int i = 0; i < count; ++i)
            {
                uint gonetId = destroyedGONetIds[i];
                if (gonetId == GONetParticipant.GONetId_Unset)
                {
                    continue;
                }

                // Publish locally so the server's persistent event tracker can cancel out any synthesized spawn.
                // (This does not destroy locally; destruction is already scheduled via UnityEngine.Object.Destroy.)
                GONetMain.EventBus.Publish(new DespawnGONetParticipantEvent { GONetId = gonetId });
                publishedCount++;
            }

            if (publishedCount > 0)
            {
                GONetLog.Info($"[Failover] Cancelled {publishedCount} synthesized spawn(s) via Despawn events (reason='{reason}')");
            }
        }

        private static void DestroyGNP_LocalOnly(GONetParticipant gonetParticipant)
        {
            if (gonetParticipant == null || gonetParticipant.gameObject == null)
            {
                return;
            }

            // FAILOVER RECONCILIATION: Clients may need to destroy objects they do NOT own.
            // Mark as expected so GONetParticipant.OnDestroy does not spam warnings.
            if (!GONetMain.IsServer)
            {
                GONetMain.MarkGONetIdDestroyedViaPropagation(gonetParticipant.GONetId);
            }

            UnityEngine.Object.Destroy(gonetParticipant.gameObject);
        }

        private int CleanupServerOwnedTransientsSpawnedBy(ulong deadSpawnerPersistentId)
        {
            if (deadSpawnerPersistentId == GONetParticipant.SpawnerPersistentId_NoSpawner)
            {
                return 0;
            }

            List<GONetParticipant> toDestroy = new List<GONetParticipant>();

            foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
            {
                GONetParticipant gnp = kvp.Value;
                if (gnp == null) continue;

                if (gnp.OwnerAuthorityId != GONetMain.OwnerAuthorityId_Server) continue;
                if (gnp.SpawnerPersistentId != deadSpawnerPersistentId) continue;
                if (!gnp.DestroyWhenSpawnerLeaves) continue;

                toDestroy.Add(gnp);
            }

            int destroyedCount = 0;
            foreach (var gnp in toDestroy)
            {
                try
                {
                    DestroyGNP_LocalOnly(gnp);
                    destroyedCount++;
                }
                catch (Exception ex)
                {
                    GONetLog.Error($"[Failover] Failed to destroy server-owned transient '{gnp.name}' (GONetId: {gnp.GONetId}): {ex.Message}");
                }
            }

            return destroyedCount;
        }

        /// <summary>
        /// Notifies all GONetParticipantCompanionBehaviour instances on a GNP about ownership migration.
        /// </summary>
        private void NotifyCompanionBehaviours(GONetParticipant gnp, bool isMineNow, ushort originalAuthorityId)
        {
            var companions = gnp.GetComponents<GONetParticipantCompanionBehaviour>();
            foreach (var companion in companions)
            {
                try
                {
                    companion.OnOwnershipMigratedDuringFailover(isMineNow, originalAuthorityId);
                }
                catch (System.Exception ex)
                {
                    GONetLog.Error($"[Failover] Exception in OnOwnershipMigratedDuringFailover for '{companion.GetType().Name}' on '{gnp.name}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// DIAGNOSTIC: Logs detailed IsGONetReady state for all GNPs after self-promotion.
        /// This identifies which gate (1-10) is blocking readiness - particularly Gate 6
        /// (GONetLocal lookup for authority ID 1023) which is the suspected failure point.
        /// </summary>
        private void DiagnoseIsReadyStateAfterPromotion()
        {
            GONetLog.Warning($"[Failover-DIAG] ===== IsGONetReady DIAGNOSTIC START =====");
            GONetLog.Warning($"[Failover-DIAG] IsServer={GONetMain.IsServer}, IsClient={GONetMain.IsClient}, MyAuthorityId={GONetMain.MyAuthorityId}");

            // Check GONetLocal lookup table state
            if (GONetLocal.LookupByAuthorityId == null)
            {
                GONetLog.Error($"[Failover-DIAG] CRITICAL: GONetLocal.LookupByAuthorityId is NULL!");
            }
            else
            {
                // Check if server authority ID (1023) has a GONetLocal entry
                GONetLocal localFor1023 = GONetLocal.LookupByAuthorityId[GONetMain.OwnerAuthorityId_Server];
                GONetLog.Warning($"[Failover-DIAG] GONetLocal[1023] = {(localFor1023 != null ? localFor1023.gameObject.name : "NULL")}");

                // Check our own authority ID too
                GONetLocal localForMe = GONetLocal.LookupByAuthorityId[GONetMain.MyAuthorityId];
                GONetLog.Warning($"[Failover-DIAG] GONetLocal[{GONetMain.MyAuthorityId}] = {(localForMe != null ? localForMe.gameObject.name : "NULL")}");
            }

            // Check IsGONetReady for each server-owned GNP
            int readyCount = 0;
            int notReadyCount = 0;

            foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
            {
                GONetParticipant gnp = kvp.Value;
                if (gnp == null) continue;

                // Only check server-owned GNPs
                if (gnp.OwnerAuthorityId != GONetMain.OwnerAuthorityId_Server)
                    continue;

                bool isReady = GONetMain.IsGONetReady(gnp);
                if (isReady)
                {
                    readyCount++;
                }
                else
                {
                    notReadyCount++;
                    string reason = GONetMain.GetIsGONetReadyBlockingReason(gnp);
                    GONetLog.Warning($"[Failover-DIAG] GNP NOT READY: '{gnp.name}' (GONetId={gnp.GONetId}) - REASON: {reason}");
                }
            }

            GONetLog.Warning($"[Failover-DIAG] Server-owned GNPs: {readyCount} ready, {notReadyCount} NOT ready");
            GONetLog.Warning($"[Failover-DIAG] ===== IsGONetReady DIAGNOSTIC END =====");
        }

        /// <summary>
        /// Called when an emergency promotion is received from another node.
        /// </summary>
        public void OnEmergencyPromotionReceived(EmergencyHostPromotionMessage message)
        {
            ushort newHostAuthorityId = message.NewHostAuthorityId;
            uint newHostEpoch = message.NewHostEpoch;
            ushort newHostOriginalAuthorityId = message.PromotingPeerOriginalAuthorityId;

            ushort tiebreakViceHostAuthorityId =
                tiebreakViceHostAuthorityIdForCurrentEpoch != 0
                    ? tiebreakViceHostAuthorityIdForCurrentEpoch
                    : lastKnownViceHostAuthorityId;

            // If we are host or self-promoting, compare against our own claim.
            if (isHost || currentState == FailoverState.SelfPromoting)
            {
                uint myClaimEpoch = isHost ? GONetMain.HostEpoch : GONetMain.HostEpoch + 1;
                ushort myClaimOriginalAuthorityId = isHost
                    ? (selfPromotedFromAuthorityId != 0 ? selfPromotedFromAuthorityId : (ushort)0)
                    : GONetMain.MyAuthorityId; // pre-promotion

                if (!IsOtherHostClaimPreferred(
                    otherEpoch: newHostEpoch,
                    otherPromotingOriginalAuthorityId: newHostOriginalAuthorityId,
                    currentEpoch: myClaimEpoch,
                    currentPromotingOriginalAuthorityId: myClaimOriginalAuthorityId,
                    tiebreakViceHostAuthorityId: tiebreakViceHostAuthorityId))
                {
                    GONetLog.Warning($"[Failover] Ignoring emergency promotion from authority {newHostAuthorityId} " +
                                   $"(epoch {newHostEpoch}, originalPeerAuthority {newHostOriginalAuthorityId}) - our claim wins");
                    return;
                }

                GONetLog.Warning($"[Failover] Accepting emergency promotion from authority {newHostAuthorityId} " +
                               $"(epoch {newHostEpoch}, originalPeerAuthority {newHostOriginalAuthorityId}) - yielding to preferred claim");

                AcceptNewHostInternal(
                    newHostAuthorityId: newHostAuthorityId,
                    newHostOriginalAuthorityId: newHostOriginalAuthorityId,
                    newHostEpoch: newHostEpoch,
                    newViceHostAuthorityId: 0,
                    reason: "Emergency promotion conflict resolution");
                return;
            }

            // Regular client: ignore stale epochs.
            if (newHostEpoch < GONetMain.HostEpoch)
            {
                GONetLog.Debug($"[Failover] Ignoring stale emergency promotion (epoch {newHostEpoch} < {GONetMain.HostEpoch})");
                return;
            }

            // Late joiner support: if we don't yet know the promoted host's original authority, accept it.
            if (newHostEpoch == GONetMain.HostEpoch &&
                currentHostOriginalAuthorityId == 0 &&
                GONetMain.HostEpoch > 0 &&
                newHostOriginalAuthorityId != 0)
            {
                AcceptNewHostInternal(
                    newHostAuthorityId: newHostAuthorityId,
                    newHostOriginalAuthorityId: newHostOriginalAuthorityId,
                    newHostEpoch: newHostEpoch,
                    newViceHostAuthorityId: 0,
                    reason: "Emergency promotion learned promoted host original authority (late joiner)");
                return;
            }

            // Same-epoch duplicate.
            if (newHostEpoch == GONetMain.HostEpoch && newHostOriginalAuthorityId == currentHostOriginalAuthorityId)
            {
                return;
            }

            // Same epoch: only accept if the incoming host claim is preferred.
            if (newHostEpoch == GONetMain.HostEpoch &&
                !IsOtherHostClaimPreferred(
                    otherEpoch: newHostEpoch,
                    otherPromotingOriginalAuthorityId: newHostOriginalAuthorityId,
                    currentEpoch: GONetMain.HostEpoch,
                    currentPromotingOriginalAuthorityId: currentHostOriginalAuthorityId,
                    tiebreakViceHostAuthorityId: tiebreakViceHostAuthorityId))
            {
                GONetLog.Warning($"[Failover] Ignoring same-epoch emergency promotion from authority {newHostAuthorityId} " +
                               $"(epoch {newHostEpoch}, originalPeerAuthority {newHostOriginalAuthorityId}) - current host claim wins");
                return;
            }

            AcceptNewHostInternal(
                newHostAuthorityId: newHostAuthorityId,
                newHostOriginalAuthorityId: newHostOriginalAuthorityId,
                newHostEpoch: newHostEpoch,
                newViceHostAuthorityId: 0,
                reason: "Emergency promotion received");
        }

        #endregion

        #region Internal

        internal static bool IsOtherHostClaimPreferred(
            uint otherEpoch,
            ushort otherPromotingOriginalAuthorityId,
            uint currentEpoch,
            ushort currentPromotingOriginalAuthorityId,
            ushort tiebreakViceHostAuthorityId)
        {
            // Higher epoch always wins.
            if (otherEpoch < currentEpoch) return false;
            if (otherEpoch > currentEpoch) return true;

            // Same epoch: identical claim is not preferred.
            if (otherPromotingOriginalAuthorityId == currentPromotingOriginalAuthorityId) return false;

            // Same epoch: designated vice host wins if known for this failover boundary.
            if (tiebreakViceHostAuthorityId != 0)
            {
                bool otherIsVice = otherPromotingOriginalAuthorityId == tiebreakViceHostAuthorityId;
                bool currentIsVice = currentPromotingOriginalAuthorityId == tiebreakViceHostAuthorityId;
                if (otherIsVice != currentIsVice)
                {
                    return otherIsVice;
                }
            }

            // Final deterministic tiebreaker: lower original authority ID wins.
            return otherPromotingOriginalAuthorityId < currentPromotingOriginalAuthorityId;
        }

        private void AcceptNewHostInternal(
            ushort newHostAuthorityId,
            ushort newHostOriginalAuthorityId,
            uint newHostEpoch,
            ushort newViceHostAuthorityId,
            string reason)
        {
            ushort previousHostAuthorityId = lastKnownHostAuthorityId;
            ushort previousHostOriginalAuthorityId = currentHostOriginalAuthorityId;
            bool isFailoverBoundary = newHostEpoch > GONetMain.HostEpoch;
            bool learnedPromotedHostOriginalAuthority =
                !isFailoverBoundary &&
                newHostEpoch == GONetMain.HostEpoch &&
                previousHostOriginalAuthorityId == 0 &&
                newHostOriginalAuthorityId != 0 &&
                GONetMain.HostEpoch > 0;

            if (didVoluntarilyDemote)
            {
                GONetLog.Warning($"[Failover] Voluntary demotion lock retained while adopting host {newHostAuthorityId} (epoch {newHostEpoch})");
            }

            // Capture vice host from the previous epoch as we cross the failover boundary.
            if (newHostEpoch > GONetMain.HostEpoch)
            {
                tiebreakViceHostAuthorityIdForCurrentEpoch = lastKnownViceHostAuthorityId;
            }

            // If we were host, step down before adopting the new host.
            if (isHost)
            {
                StepDownFromHost(newHostAuthorityId, newHostOriginalAuthorityId, newHostEpoch, reason);
            }

            // Ensure we do not keep retrying our own promotion after adopting someone else.
            pendingPromotionMessage = null;
            promotionRetryCount = 0;

            // FAILOVER RECONCILIATION (Dec 2025): Clients can retain orphaned transients after host promotion.
            // Strategy:
            // 1) Drop server-owned transients spawned by the dead host (new host also drops these)
            // 2) Drop objects owned by the promoted host's pre-promotion authority (no sync source after promotion)
            // 3) Drop objects owned by the previous promoted host authority (cascading failover)
            if (!GONetMain.IsServer && (isFailoverBoundary || learnedPromotedHostOriginalAuthority))
            {
                if (isFailoverBoundary &&
                    previousHostOriginalAuthorityId != 0 &&
                    previousHostOriginalAuthorityId != newHostOriginalAuthorityId)
                {
                    int destroyedPreviousPromotedHost = CleanupPromotingClientObjects(previousHostOriginalAuthorityId);
                    if (destroyedPreviousPromotedHost > 0)
                    {
                        GONetLog.Warning($"[Failover] Client cleanup destroyed {destroyedPreviousPromotedHost} orphaned objects owned by previous promoted host authority {previousHostOriginalAuthorityId}");
                    }
                }

                if (newHostOriginalAuthorityId != 0)
                {
                    int destroyedPromotedHostOrphans = CleanupPromotingClientObjects(newHostOriginalAuthorityId);
                    if (destroyedPromotedHostOrphans > 0)
                    {
                        GONetLog.Warning($"[Failover] Client cleanup destroyed {destroyedPromotedHostOrphans} orphaned objects owned by promoted host old authority {newHostOriginalAuthorityId}");
                    }
                }

                if (isFailoverBoundary)
                {
                    ushort lookupAuthorityId = previousHostOriginalAuthorityId != 0
                        ? previousHostOriginalAuthorityId
                        : previousHostAuthorityId;

                    // CRITICAL FIX (Dec 2025): Late-joiners who connect after failover must NOT cleanup current host objects
                    if (lookupAuthorityId != 0 && lookupAuthorityId != newHostAuthorityId &&
                        GONetGossipManager.Instance.TryGetNodePersistentId(lookupAuthorityId, out ulong deadHostPersistentId) &&
                        deadHostPersistentId != 0)
                    {
                        int destroyedDeadHostTransients = CleanupServerOwnedTransientsSpawnedBy(deadHostPersistentId);
                        if (destroyedDeadHostTransients > 0)
                        {
                            GONetLog.Warning($"[Failover] Client cleanup destroyed {destroyedDeadHostTransients} server-owned transients spawned by dead host {lookupAuthorityId} (persistentId: {deadHostPersistentId:X16})");
                        }
                    }
                }

                // NOTE: Do NOT run CleanupServerOwnedTransientObjects() on clients here.
                // The blanket cleanup destroys FRESH objects just received from the new server
                // via late-joiner spawn. The server handles its own cleanup during promotion
                // and sends despawn messages to clients for cleaned objects.
                // The spawner-specific cleanup above is sufficient for clients.
            }

            // Adopt host identity (may advance by more than 1 in partition-heal scenarios).
            GONetMain.AdoptHostIdentity(newHostEpoch, newHostAuthorityId, newViceHostAuthorityId);

            lastKnownHostAuthorityId = newHostAuthorityId;
            if (newViceHostAuthorityId != 0)
            {
                lastKnownViceHostAuthorityId = newViceHostAuthorityId;
            }

            // Track the original authority ID of the promoted host for tiebreaker + hot standby lookup.
            currentHostOriginalAuthorityId = newHostOriginalAuthorityId;
            GONetMain.TryMapServerAuthorityToHostLocal(newHostOriginalAuthorityId);

            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            lastHostHeartbeatRawTicks = nowRawTicks;
            lastFailoverRawTicks = nowRawTicks;
            hasReceivedFirstHeartbeat = true;

            TransitionTo(FailoverState.Complete);

            GONetLog.Warning($"[Failover] Accepted host claim: host={newHostAuthorityId}, epoch={newHostEpoch}, " +
                           $"originalPeerAuthority={newHostOriginalAuthorityId}, reason='{reason}'");

            // FAILOVER-DIAG: Enable SoA diagnostics for non-promoting nodes to trace data flow
            GONetMain.SoA_EnableFailoverDiagnostics();

            OnNewHostDetected?.Invoke(newHostAuthorityId);
            if (newHostOriginalAuthorityId != 0)
            {
                OnNewHostDetectedWithOriginalId?.Invoke(newHostAuthorityId, newHostOriginalAuthorityId);
            }
            OnFailoverComplete?.Invoke(newHostAuthorityId, newHostEpoch);

            // Fire HostFailoverCompletedEvent via EventBus for all non-self adoptions (promotion/heartbeat recovery/same-epoch correction).
            GONetMain.EventBus.Publish<GONet.IGONetEvent>(new GONet.HostFailoverCompletedEvent(
                GONetMain.Time.ElapsedTicks,
                newHostAuthorityId,
                newHostOriginalAuthorityId,
                isSelf: false,
                migratedGNPCount: 0));

            // Broadcast OnHostFailoverCompleted to all GONetBehaviours
            GONetMain.BroadcastHostFailoverCompleted(newHostAuthorityId, newHostOriginalAuthorityId, isSelf: false);
        }

        private void StepDownFromHost(ushort newHostAuthorityId, ushort newHostOriginalAuthorityId, uint newHostEpoch, string reason)
        {
            // CRITICAL FIX (Dec 2025): If we're the outgoing host in a voluntary handoff,
            // defer the full demotion to the handoff manager. The failover system detected
            // the new host via heartbeat, but the handoff manager has the reserved client
            // authority ID and should handle the proper demotion sequence.
            var handoffManager = GONetHostHandoffManager.Instance;
            if (handoffManager != null && handoffManager.IsHandoffInProgress && handoffManager.IsOutgoingHost)
            {
                GONetLog.Warning($"[Failover] StepDownFromHost deferred to handoff manager (voluntary handoff in progress). " +
                               $"reason='{reason}', newHost={newHostAuthorityId}");
                // Still update failover state but DON'T stop server or demote - let handoff manager handle it
                isHost = false;
                isViceHost = false;
                return;
            }

            ushort previousHostAuthorityId = GONetMain.MyAuthorityId;
            ushort previousHostOriginalAuthorityId = selfPromotedFromAuthorityId;
            ushort restoredAuthorityId = selfPromotedFromAuthorityId;

            GONetLog.Warning($"[Failover] Stepping down from host. reason='{reason}', " +
                           $"restoredAuthorityId={restoredAuthorityId}, newHost={newHostAuthorityId}");

            // Notify subsystems first (prevents gossip from continuing to act as host).
            GONetGossipManager.Instance.OnHostStatusChanged(false);
            GONetViceHostManager.Instance.OnDemotedFromHost();

            // Bring hot standby back to client mode (restarts dormant server in DormantMesh mode).
            var hotStandby = GONetHotStandbyManager.Instance;
            if (hotStandby != null)
            {
                try { hotStandby.OnDemotedFromHost(); } catch { }
            }

            // Best-effort stop of active host server.
            if (GONetMain.gonetServer != null)
            {
                try { GONetMain.gonetServer.Stop(); } catch { }
            }

            if (restoredAuthorityId != 0)
            {
                GONetMain.DemoteFromServerAuthority(restoredAuthorityId);
                GONetGossipManager.Instance.UpdateLocalAuthorityId(GONetMain.MyAuthorityId);
                selfPromotedFromAuthorityId = 0;
            }
            else
            {
                // Original-server demotion isn't currently supported (no original client authority ID to restore).
                GONetLog.Error("[Failover] Cannot demote cleanly - selfPromotedFromAuthorityId is 0 (original server demotion not supported)");
            }

            isHost = false;
            isViceHost = false;

            ushort demotedHostNewAuthorityId = GONetMain.MyAuthorityId;
            var demotedEvent = new GONet.HostDemotedEvent(
                GONetMain.Time.ElapsedTicks,
                previousHostAuthorityId,
                previousHostOriginalAuthorityId,
                demotedHostNewAuthorityId,
                newHostAuthorityId,
                newHostOriginalAuthorityId,
                newHostEpoch,
                wasVoluntary: false);
            GONetMain.EventBus.Publish<GONet.IGONetEvent>(demotedEvent);
            GONetMain.BroadcastHostDemoted(
                previousHostAuthorityId,
                previousHostOriginalAuthorityId,
                demotedHostNewAuthorityId,
                newHostAuthorityId,
                newHostOriginalAuthorityId,
                newHostEpoch,
                wasVoluntary: false);

            OnDemotedFromHost?.Invoke(newHostAuthorityId);
        }

        private void TransitionTo(FailoverState newState)
        {
            if (currentState != newState)
            {
                // INSTRUMENTATION: Log state transitions at Warning level for visibility
                GONetLog.Warning($"[Failover-TRACE] STATE TRANSITION: {currentState} -> {newState} (myAuthority={GONetMain.MyAuthorityId})");
                currentState = newState;
                stateStartRawTicks = GONetMain.Time.RawElapsedTicks;
            }
        }

        private void SendEmergencyPromotion(EmergencyHostPromotionMessage message)
        {
            GONetLog.Debug($"[Failover] Sending emergency promotion: new host {message.NewHostAuthorityId}, epoch {message.NewHostEpoch}");
            GONetGossipIntegration.SendEmergencyPromotion(message);
        }

        /// <summary>
        /// Sends emergency promotion message with automatic retries.
        /// Connections may be reconnecting during failover, so retries ensure delivery.
        /// </summary>
        private void SendEmergencyPromotionWithRetries(EmergencyHostPromotionMessage message)
        {
            // Send immediately
            SendEmergencyPromotion(message);

            // Queue retries
            pendingPromotionMessage = message;
            promotionRetryCount = EMERGENCY_PROMOTION_RETRY_COUNT;
            lastPromotionSentRawTicks = GONetMain.Time.RawElapsedTicks;

            GONetLog.Info($"[Failover] Emergency promotion sent, {promotionRetryCount} retries queued");
        }

        /// <summary>
        /// Called from Update to process pending emergency promotion retries.
        /// </summary>
        private void ProcessEmergencyPromotionRetries()
        {
            if (pendingPromotionMessage == null || promotionRetryCount <= 0)
                return;

            long nowRawTicks = GONetMain.Time.RawElapsedTicks;
            if (nowRawTicks - lastPromotionSentRawTicks >= EMERGENCY_PROMOTION_RETRY_INTERVAL_TICKS)
            {
                promotionRetryCount--;
                lastPromotionSentRawTicks = nowRawTicks;

                GONetLog.Debug($"[Failover] Sending emergency promotion retry ({EMERGENCY_PROMOTION_RETRY_COUNT - promotionRetryCount}/{EMERGENCY_PROMOTION_RETRY_COUNT})");
                SendEmergencyPromotion(pendingPromotionMessage);

                if (promotionRetryCount <= 0)
                {
                    GONetLog.Info("[Failover] Emergency promotion retries complete");
                    pendingPromotionMessage = null;
                }
            }
        }

        /// <summary>
        /// Raises the OnFailoverComplete event. Called externally for testing purposes.
        /// Normal failover completion is handled internally by the heartbeat timeout system.
        /// </summary>
        internal void RaiseFailoverComplete(ushort newHostAuthorityId, uint newEpoch)
        {
            lastFailoverRawTicks = GONetMain.Time.RawElapsedTicks;
            TransitionTo(FailoverState.Complete);
            OnFailoverComplete?.Invoke(newHostAuthorityId, newEpoch);
        }

        /// <summary>
        /// Triggers self-promotion to host. Called externally for testing or emergency situations.
        /// Normal failover is triggered by heartbeat timeout detection in <see cref="CheckForHostHeartbeatTimeout"/>.
        /// </summary>
        public void TriggerSelfPromotion()
        {
            GONetLog.Info("[Failover] TriggerSelfPromotion called - completing self-promotion");
            CompleteSelfPromotion();
        }

        #endregion

        #region Phase 2.5: Client-Host Ownership Semantics

        /// <summary>
        /// Processes spawner death during failover.
        /// Called when a machine (client-host or regular client) leaves/crashes.
        ///
        /// For each GONetParticipant spawned by the dead machine:
        /// - If DestroyWhenSpawnerLeaves == true: Destroy the object (player objects, weapons, projectiles)
        /// - If DestroyWhenSpawnerLeaves == false: Object survives (world objects like doors, NPCs)
        /// - If SpawnerPersistentId == 0: Object is immune (scene objects)
        /// - If IsMine == true: Object is adopted by new host and survives
        /// - If object belonged to the promoting client before promotion: survives (was "mine" pre-promotion)
        ///
        /// RELATIONSHIP WITH EXISTING CLEANUP:
        /// This system (SpawnerPersistentId-based) complements the existing OwnerAuthorityId-based cleanup
        /// in GONetGlobal.Server_MakeDoublySureAllClientOwnedGNPsDestroyed:
        /// - Existing system: Handles normal client disconnect (destroys by OwnerAuthorityId)
        /// - This system: Handles host failover (destroys by SpawnerPersistentId + DestroyWhenSpawnerLeaves)
        /// These dimensions are orthogonal - if both target the same object, first one destroys it.
        /// </summary>
        /// <param name="deadSpawnerPersistentId">Persistent ID of the machine that died</param>
        /// <param name="promotingClientOriginalAuthorityId">The promoting client's authority ID BEFORE promotion (0 if not a self-promotion)</param>
        /// <returns>Count of objects destroyed, count of objects that survived</returns>
        public (int destroyedCount, int survivedCount) ProcessSpawnerDeath(
            ulong deadSpawnerPersistentId,
            ushort promotingClientOriginalAuthorityId = 0,
            List<uint> destroyedGONetIds = null)
        {
            if (deadSpawnerPersistentId == GONetParticipant.SpawnerPersistentId_NoSpawner)
            {
                GONetLog.Warning($"[Failover] ProcessSpawnerDeath called with SpawnerPersistentId_NoSpawner (0) - nothing to process");
                return (0, 0);
            }

            int destroyedCount = 0;
            int survivedCount = 0;

            // DIAGNOSTIC: Log all GNPs and their SpawnerPersistentIds for debugging
            GONetLog.Info($"[Failover] ProcessSpawnerDeath scanning for spawner {deadSpawnerPersistentId:X16}. GNP count: {GONetMain.gonetParticipantByGONetIdMap.Count}");

            // Collect GNPs to process (can't modify collection during iteration)
            // Track reason for survival to avoid misleading log messages
            List<GONetParticipant> gnpsToDestroy = new List<GONetParticipant>();
            List<(GONetParticipant gnp, string reason)> gnpsToTransfer = new List<(GONetParticipant, string)>();

            foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
            {
                GONetParticipant gnp = kvp.Value;
                if (gnp == null) continue;

                // DIAGNOSTIC: Log each GNP's SpawnerPersistentId (scene objects should be 0)
                bool isSceneObject = gnp.SpawnerPersistentId == GONetParticipant.SpawnerPersistentId_NoSpawner;
                //GONetLog.Debug($"[Failover] GNP '{gnp.name}' (GONetId: {gnp.GONetId}) SpawnerPersistentId: {gnp.SpawnerPersistentId:X16} {(isSceneObject ? "(SCENE-IMMUNE)" : "")}");

                // Only process GNPs spawned by the dead machine
                if (gnp.SpawnerPersistentId != deadSpawnerPersistentId) continue;

                // CRITICAL FIX (Dec 2025): After promotion, MyAuthorityId=1023, so IsMine=true ONLY
                // for server-owned objects (OwnerAuthorityId=1023). But those are the DEAD server's
                // objects, not legitimately adopted objects! The promoting client's own objects
                // have IsMine=false after promotion and are protected by the promotingClientOriginalAuthorityId
                // check below.
                //
                // Only protect non-server-owned objects that have IsMine=true (this handles
                // edge cases where ProcessSpawnerDeath is called before authority change).
                if (gnp.IsMine && gnp.OwnerAuthorityId != GONetMain.OwnerAuthorityId_Server)
                {
                    GONetLog.Info($"[Failover] Keeping '{gnp.name}' (GONetId: {gnp.GONetId}) - IsMine=true (adopted by new host)");
                    gnpsToTransfer.Add((gnp, "IsMine=true (adopted by new host)"));
                    continue;
                }

                // CRITICAL: Don't destroy objects that were "mine" BEFORE promotion.
                // When a client promotes to server authority (1023), their own objects (like GONetLocal)
                // suddenly have IsMine=false because OwnerAuthorityId hasn't changed but MyAuthorityId has.
                // These objects should survive because they still belong to this machine.
                if (promotingClientOriginalAuthorityId != 0 && gnp.OwnerAuthorityId == promotingClientOriginalAuthorityId)
                {
                    GONetLog.Info($"[Failover] Keeping '{gnp.name}' (GONetId: {gnp.GONetId}) - belonged to this client before promotion (OwnerAuthorityId={promotingClientOriginalAuthorityId})");
                    gnpsToTransfer.Add((gnp, $"pre-promotion owner (OwnerAuthorityId={promotingClientOriginalAuthorityId})"));
                    continue;
                }

                // CRITICAL FIX (Dec 2025): Don't destroy CLIENT-OWNED objects here.
                // Client-owned objects (like GONetLocal for other clients) are spawned by the server,
                // so they pass the SpawnerPersistentId check. But the owning client might still be
                // alive and reconnecting via hot standby mesh! Let Server_MakeDoublySureAllClientOwnedGNPsDestroyed
                // (triggered by ClientDisconnected event) handle cleanup when the client actually disconnects.
                // Only destroy SERVER-OWNED objects (OwnerAuthorityId=1023) from the dead server.
                if (gnp.OwnerAuthorityId != GONetMain.OwnerAuthorityId_Server)
                {
                    GONetLog.Info($"[Failover] Keeping '{gnp.name}' (GONetId: {gnp.GONetId}) - client-owned (authority {gnp.OwnerAuthorityId}), deferring cleanup to ClientDisconnected");
                    gnpsToTransfer.Add((gnp, $"client-owned (authority {gnp.OwnerAuthorityId}), deferred cleanup"));
                    continue;
                }

                // Check DestroyWhenSpawnerLeaves directly on GONetParticipant (serialized on prefab)
                if (gnp.DestroyWhenSpawnerLeaves)
                {
                    gnpsToDestroy.Add(gnp);
                }
                else
                {
                    gnpsToTransfer.Add((gnp, "DestroyWhenSpawnerLeaves=false"));
                }
            }

            // Destroy player-bound objects
            foreach (GONetParticipant gnp in gnpsToDestroy)
            {
                try
                {
                    uint gonetId = gnp.GONetId;
                    // Enhanced diagnostic: include SpawnerPersistentId to verify it matches deadSpawnerPersistentId
                    //GONetLog.Info($"[Failover] Destroying '{gnp.name}' (GONetId: {gnp.GONetId}, SpawnerPersistentId: {gnp.SpawnerPersistentId:X16}) - spawner {deadSpawnerPersistentId:X16} died, DestroyWhenSpawnerLeaves=true");
                    DestroyGNP_LocalOnly(gnp);
                    destroyedCount++;

                    if (destroyedGONetIds != null && gonetId != GONetParticipant.GONetId_Unset)
                    {
                        destroyedGONetIds.Add(gonetId);
                    }
                }
                catch (Exception ex)
                {
                    GONetLog.Error($"[Failover] Failed to destroy '{gnp.name}': {ex.Message}");
                }
            }

            // Log transferred objects with accurate survival reason
            foreach (var (gnp, reason) in gnpsToTransfer)
            {
                GONetLog.Info($"[Failover] Preserved '{gnp.name}' (GONetId: {gnp.GONetId}) - spawner {deadSpawnerPersistentId:X16} died, reason: {reason}");
                survivedCount++;
            }

            GONetLog.Info($"[Failover] ProcessSpawnerDeath complete for spawner {deadSpawnerPersistentId:X16}: destroyed={destroyedCount}, survived={survivedCount}");
            return (destroyedCount, survivedCount);
        }

        /// <summary>
        /// SAFETY NET (Dec 2025): Cleanup server-owned objects that should have been destroyed during failover.
        /// This catches cases where:
        /// - deadHostPersistentId lookup failed (gossip entry missing)
        /// - SpawnerPersistentId mismatch (server used different persistent ID than expected)
        /// - Race conditions during promotion
        ///
        /// Destroys server-owned GNPs (OwnerAuthorityId == 1023) where:
        /// - DestroyWhenSpawnerLeaves == true (transient objects like projectiles)
        /// - SpawnerPersistentId != 0 (not a scene object)
        /// - NOT a core infrastructure object (GONetGlobal, GONetLocal, etc.)
        ///
        /// NOTE: We intentionally do NOT check IsMine because after promotion, ALL server-owned
        /// objects have IsMine=true (OwnerAuthorityId=1023, MyAuthorityId=1023). The transient
        /// projectiles/effects should be destroyed regardless of the IsMine status.
        /// </summary>
        /// <returns>Number of objects destroyed</returns>
        private int CleanupServerOwnedTransientObjects()
        {
            return CleanupServerOwnedTransientObjects(destroyedGONetIds: null);
        }

        /// <summary>
        /// Overload that also tracks destroyed GONetIds for deferred despawn notification.
        /// </summary>
        private int CleanupServerOwnedTransientObjects(List<uint> destroyedGONetIds)
        {
            List<GONetParticipant> toDestroy = new List<GONetParticipant>();

            foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
            {
                GONetParticipant gnp = kvp.Value;
                if (gnp == null) continue;

                // Only check server-owned objects
                if (gnp.OwnerAuthorityId != GONetMain.OwnerAuthorityId_Server) continue;

                // Skip scene objects (they're immune to cleanup)
                if (gnp.SpawnerPersistentId == GONetParticipant.SpawnerPersistentId_NoSpawner) continue;

                // Only destroy transient objects (DestroyWhenSpawnerLeaves=true)
                if (!gnp.DestroyWhenSpawnerLeaves) continue;

                // CRITICAL FIX (Dec 2025): GONetLocal from the OLD SERVER should be destroyed.
                // The new host's GONetLocal has OwnerAuthorityId != 1023 (their original authority),
                // so it's not processed by this function (which only looks at server-owned objects).
                // GONetGlobal is a scene object (SpawnerPersistentId=0), already excluded above.
                // We still check for GONetGlobal as a safety measure, but don't skip GONetLocal.
                if (gnp.GetComponent<GONetGlobal>() != null)
                {
                    continue;
                }

                toDestroy.Add(gnp);
            }

            int destroyedCount = 0;
            foreach (var gnp in toDestroy)
            {
                try
                {
                    uint gonetId = gnp.GONetId;
                    //GONetLog.Info($"[Failover-SafetyNet] Destroying orphaned transient '{gnp.name}' (GONetId: {gonetId}, SpawnerPersistentId: {gnp.SpawnerPersistentId:X16})");
                    DestroyGNP_LocalOnly(gnp);
                    destroyedCount++;

                    // Track for deferred despawn notification (sent after GONetServer is available)
                    if (destroyedGONetIds != null && gonetId != GONetParticipant.GONetId_Unset)
                    {
                        destroyedGONetIds.Add(gonetId);
                    }
                }
                catch (Exception ex)
                {
                    GONetLog.Error($"[Failover-SafetyNet] Failed to destroy '{gnp.name}': {ex.Message}");
                }
            }

            GONetLog.Info($"[Failover-SafetyNet] Cleanup complete: scanned {GONetMain.gonetParticipantByGONetIdMap.Count} GNPs, destroyed {destroyedCount} orphaned transients");
            return destroyedCount;
        }

        /// <summary>
        /// Sends despawn notifications to connected clients for objects that were destroyed during
        /// server promotion but couldn't have their despawn messages sent because GONetServer wasn't
        /// available at that time.
        /// <para>
        /// NOTE (Dec 2025): Prefer delivering these ids via <see cref="SessionPromoteMessage.DeferredDespawnGONetIds"/>
        /// to avoid losing reliable traffic during the post-failover reliability reset/switchover window.
        /// If the pending list was already consumed for SessionPromote, this method will no-op.
        /// </para>
        ///
        /// MUST be called AFTER GONetServer is set up (after OnBecameHost promotes the dormant server).
        /// </summary>
        /// <returns>Number of despawn notifications sent</returns>
        public int SendPendingDespawnNotifications()
        {
            if (pendingDespawnNotifications.Count == 0)
            {
                return 0;
            }

            if (GONetMain.gonetServer == null)
            {
                GONetLog.Warning($"[Failover] Cannot send pending despawn notifications - GONetServer is null");
                return 0;
            }

            int sentCount = 0;
            foreach (uint gonetId in pendingDespawnNotifications)
            {
                try
                {
                    // Publish a despawn event that will be sent to all connected clients
                    var despawnEvent = new DespawnGONetParticipantEvent() { GONetId = gonetId };
                    GONetMain.EventBus.Publish(despawnEvent);
                    sentCount++;
                    GONetLog.Debug($"[Failover] Published deferred despawn event for GONetId {gonetId}");
                }
                catch (Exception ex)
                {
                    GONetLog.Error($"[Failover] Failed to send despawn notification for GONetId {gonetId}: {ex.Message}");
                }
            }

            GONetLog.Info($"[Failover] Sent {sentCount} deferred despawn notifications to connected clients");
            pendingDespawnNotifications.Clear();
            return sentCount;
        }

        /// <summary>
        /// Consumes the pending despawn list for inclusion in <see cref="SessionPromoteMessage"/>.
        /// This avoids losing despawn notifications during the post-failover ReliableNetcode reset,
        /// which temporarily suppresses reliable traffic on the promoted connection.
        /// </summary>
        /// <returns>Array of GONetIds to despawn, or null if none.</returns>
        internal uint[] ConsumePendingDespawnNotificationsForSessionPromote()
        {
            if (pendingDespawnNotifications == null || pendingDespawnNotifications.Count == 0)
            {
                return null;
            }

            int count = pendingDespawnNotifications.Count;
            uint[] ids = new uint[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = pendingDespawnNotifications[i];
            }

            pendingDespawnNotifications.Clear();
            return ids;
        }

        #region Post-Failover Reconciliation

        /// <summary>
        /// Schedules a reconciliation snapshot to be sent after the configured delay.
        /// Called automatically after failover completes.
        /// </summary>
        /// <param name="failoverEpoch">The epoch of the failover that triggered this reconciliation</param>
        public void ScheduleReconciliationSnapshot(uint failoverEpoch)
        {
            if (failoverEpoch <= lastReconciliationEpoch)
            {
                GONetLog.Debug($"[Reconciliation] Skipping snapshot for epoch {failoverEpoch} - already sent for epoch {lastReconciliationEpoch}");
                return;
            }

            long delayTicks = (long)(RECONCILIATION_SNAPSHOT_DELAY_SECONDS * TimeSpan.TicksPerSecond);
            pendingReconciliationSnapshotRawTicks = DateTime.UtcNow.Ticks + delayTicks;
            GONetLog.Info($"[Reconciliation] Scheduled snapshot for epoch {failoverEpoch} in {RECONCILIATION_SNAPSHOT_DELAY_SECONDS}s");
        }

        /// <summary>
        /// Checks if a pending reconciliation snapshot should be sent, and sends it if ready.
        /// Called from Update loop.
        /// </summary>
        public void UpdateReconciliation()
        {
            long nowTicks = DateTime.UtcNow.Ticks;

            // Check for scheduled post-failover reconciliation
            if (pendingReconciliationSnapshotRawTicks > 0 && nowTicks >= pendingReconciliationSnapshotRawTicks)
            {
                pendingReconciliationSnapshotRawTicks = 0;
                SendReconciliationSnapshot();
            }

            // Check for periodic reconciliation (if enabled)
            if (PeriodicReconciliationIntervalSeconds > 0 && GONetMain.IsServer)
            {
                long intervalTicks = (long)(PeriodicReconciliationIntervalSeconds * TimeSpan.TicksPerSecond);
                if (nowTicks - lastPeriodicReconciliationRawTicks >= intervalTicks)
                {
                    lastPeriodicReconciliationRawTicks = nowTicks;
                    SendReconciliationSnapshot();
                }
            }
        }

        /// <summary>
        /// Immediately sends a reconciliation snapshot to all connected clients.
        /// Contains the authoritative list of all alive GONetIds.
        /// </summary>
        /// <returns>Number of GONetIds in the snapshot</returns>
        public int SendReconciliationSnapshot()
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[Reconciliation] Cannot send snapshot - not the server");
                return 0;
            }

            if (GONetMain.gonetServer == null)
            {
                GONetLog.Warning("[Reconciliation] Cannot send snapshot - GONetServer is null");
                return 0;
            }

            // Collect all alive runtime-spawned GONetIds
            reconciliationAliveSet.Clear();
            int sceneObjectCount = 0;

            foreach (var gnp in GONetMain.gonetParticipantByGONetIdMap.Values)
            {
                if (gnp == null || gnp.GONetId == GONetParticipant.GONetId_Unset)
                    continue;

                // Only include runtime-spawned objects (SpawnerPersistentId != 0)
                // Scene objects (SpawnerPersistentId == 0) are handled separately and never reconciled
                if (gnp.SpawnerPersistentId != 0)
                {
                    reconciliationAliveSet.Add(gnp.GONetId);
                }
                else
                {
                    sceneObjectCount++;
                }
            }

            uint[] aliveGONetIds = new uint[reconciliationAliveSet.Count];
            reconciliationAliveSet.CopyTo(aliveGONetIds);

            // === DIAGNOSTIC: Server snapshot content analysis ===
            ushort ownerMaskServer = (ushort)((1 << GONetParticipant.OWNER_AUTHORITY_ID_BIT_COUNT_USED) - 1);
            int serverSnapshotOwner1023 = 0;
            int serverSnapshotOwnerOther = 0;
            Dictionary<ushort, int> serverOwnerDistribution = new Dictionary<ushort, int>();
            foreach (uint id in aliveGONetIds)
            {
                ushort owner = (ushort)(id & ownerMaskServer);
                if (owner == GONetMain.OwnerAuthorityId_Server)
                    serverSnapshotOwner1023++;
                else
                    serverSnapshotOwnerOther++;

                if (!serverOwnerDistribution.ContainsKey(owner))
                    serverOwnerDistribution[owner] = 0;
                serverOwnerDistribution[owner]++;
            }
            string serverDistStr = string.Join(", ", serverOwnerDistribution.Select(kv => $"owner={kv.Key}:{kv.Value}"));
            GONetLog.Warning($"[Reconciliation-SERVER-DIAG] BUILDING SNAPSHOT: total={aliveGONetIds.Length}, owner1023={serverSnapshotOwner1023}, ownerOther={serverSnapshotOwnerOther}");
            GONetLog.Warning($"[Reconciliation-SERVER-DIAG] SNAPSHOT distribution: {serverDistStr}");

            // Also log OwnerAuthorityId distribution (logical ownership vs GONetId ownership)
            int logicalOwner1023 = 0;
            int logicalOwnerOther = 0;
            foreach (var gnp in GONetMain.gonetParticipantByGONetIdMap.Values)
            {
                if (gnp != null && gnp.GONetId != GONetParticipant.GONetId_Unset && gnp.SpawnerPersistentId != 0)
                {
                    if (gnp.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Server)
                        logicalOwner1023++;
                    else
                        logicalOwnerOther++;
                }
            }
            GONetLog.Warning($"[Reconciliation-SERVER-DIAG] LOGICAL ownership: OwnerAuth1023={logicalOwner1023}, OwnerAuthOther={logicalOwnerOther}");

            // Get current failover epoch from hot standby manager
            uint epoch = GONetMain.HostEpoch;
            lastReconciliationEpoch = epoch;

            int clientCount = GONetMain.gonetServer?.remoteClients?.Count ?? 0;

            var snapshotEvent = new PostFailoverReconciliationSnapshotEvent(
                failoverEpoch: epoch,
                aliveGONetIds: aliveGONetIds,
                serverElapsedSeconds: GONetMain.Time.ElapsedSeconds,
                connectedClientCount: clientCount
            );
            snapshotEvent.OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks;

            GONetMain.EventBus.Publish(snapshotEvent);

            // Cache for late joiners
            cachedReconciliationSnapshot = snapshotEvent;

            GONetLog.Info($"[Reconciliation] Sent snapshot: epoch={epoch}, aliveIds={aliveGONetIds.Length}, sceneObjects={sceneObjectCount}, clients={clientCount}");

            return aliveGONetIds.Length;
        }

        /// <summary>
        /// Processes a reconciliation snapshot received from the server.
        /// Compares local GONetParticipants against the server's list and destroys ghosts.
        /// </summary>
        /// <param name="snapshot">The snapshot event from the server</param>
        /// <returns>Number of ghost objects destroyed</returns>
        public int ProcessReconciliationSnapshot(PostFailoverReconciliationSnapshotEvent snapshot)
        {
            if (GONetMain.IsServer)
            {
                GONetLog.Debug("[Reconciliation] Server ignoring its own snapshot");
                return 0;
            }

            if (snapshot.AliveGONetIds == null)
            {
                GONetLog.Warning("[Reconciliation] Received snapshot with null AliveGONetIds");
                return 0;
            }

            // === DIAGNOSTIC: Snapshot content analysis ===
            ushort ownerMaskDiag = (ushort)((1 << GONetParticipant.OWNER_AUTHORITY_ID_BIT_COUNT_USED) - 1);
            int snapshotOwner1023Count = 0;
            int snapshotOwnerOtherCount = 0;
            Dictionary<ushort, int> snapshotOwnerDistribution = new Dictionary<ushort, int>();
            foreach (uint aliveId in snapshot.AliveGONetIds)
            {
                ushort owner = (ushort)(aliveId & ownerMaskDiag);
                if (owner == GONetMain.OwnerAuthorityId_Server)
                    snapshotOwner1023Count++;
                else
                    snapshotOwnerOtherCount++;

                if (!snapshotOwnerDistribution.ContainsKey(owner))
                    snapshotOwnerDistribution[owner] = 0;
                snapshotOwnerDistribution[owner]++;
            }
            string snapshotDistStr = string.Join(", ", snapshotOwnerDistribution.Select(kv => $"owner={kv.Key}:{kv.Value}"));
            GONetLog.Warning($"[Reconciliation-DIAG] SNAPSHOT content: total={snapshot.AliveGONetIds.Length}, owner1023={snapshotOwner1023Count}, ownerOther={snapshotOwnerOtherCount}");
            GONetLog.Warning($"[Reconciliation-DIAG] SNAPSHOT distribution: {snapshotDistStr}");

            // Log first 10 aliveIds for debugging
            int sampleCount = Math.Min(10, snapshot.AliveGONetIds.Length);
            for (int i = 0; i < sampleCount; i++)
            {
                uint id = snapshot.AliveGONetIds[i];
                ushort owner = (ushort)(id & ownerMaskDiag);
                uint raw = id >> GONetParticipant.GONET_ID_BIT_COUNT_UNUSED;
                GONetLog.Info($"[Reconciliation-DIAG] SNAPSHOT sample[{i}]: GONetId={id}, owner={owner}, raw={raw}");
            }

            // === DIAGNOSTIC: Local object analysis ===
            int localRuntimeCount = 0;
            int localOwner1023Count = 0;
            int localOwnerOtherCount = 0;
            Dictionary<ushort, int> localOwnerDistribution = new Dictionary<ushort, int>();
            foreach (var gnp in GONetMain.gonetParticipantByGONetIdMap.Values)
            {
                if (gnp != null && gnp.GONetId != GONetParticipant.GONetId_Unset && gnp.SpawnerPersistentId != 0)
                {
                    localRuntimeCount++;
                    if (gnp.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Server)
                        localOwner1023Count++;
                    else
                        localOwnerOtherCount++;

                    if (!localOwnerDistribution.ContainsKey(gnp.OwnerAuthorityId))
                        localOwnerDistribution[gnp.OwnerAuthorityId] = 0;
                    localOwnerDistribution[gnp.OwnerAuthorityId]++;
                }
            }
            string localDistStr = string.Join(", ", localOwnerDistribution.Select(kv => $"owner={kv.Key}:{kv.Value}"));
            GONetLog.Warning($"[Reconciliation-DIAG] LOCAL content: total={localRuntimeCount}, owner1023={localOwner1023Count}, ownerOther={localOwnerOtherCount}");
            GONetLog.Warning($"[Reconciliation-DIAG] LOCAL distribution: {localDistStr}");
            GONetLog.Warning($"[Reconciliation-DIAG] MyAuthorityId={GONetMain.MyAuthorityId}, voluntaryDemotionPersistentId={voluntaryDemotionPersistentId:X}, handoffTargetAuth={voluntaryHandoffTargetAuthorityId}");

            GONetLog.Info($"[Reconciliation] Processing snapshot: serverAlive={snapshot.AliveGONetIds.Length}, localRuntime={localRuntimeCount}, epoch={snapshot.FailoverEpoch}");

            // Build HashSet for O(1) lookup
            reconciliationAliveSet.Clear();
            foreach (uint id in snapshot.AliveGONetIds)
            {
                reconciliationAliveSet.Add(id);
            }

            bool needsSoARepair = false;

            if (voluntaryDemotionPersistentId != 0 && voluntaryHandoffTargetAuthorityId != 0)
            {
                GONetLog.Info($"[Reconciliation] Rekey scan: persistentId={voluntaryDemotionPersistentId:X16}, targetAuth={voluntaryHandoffTargetAuthorityId}, aliveCount={snapshot.AliveGONetIds.Length}");
                ushort previousOwnerAuthorityId = voluntaryHandoffTargetAuthorityId;
                ushort currentOwnerAuthorityId = GONetMain.OwnerAuthorityId_Server;
                ushort ownerMask = (ushort)((1 << GONetParticipant.OWNER_AUTHORITY_ID_BIT_COUNT_USED) - 1);
                List<(GONetParticipant gnp, uint newGONetId)> pendingRekeys = null;

                int unmappedDestroyed = 0;
                int unmappedRekeyed = 0;
                int unmappedRegistered = 0;
                int unmappedDuplicateDestroyed = 0;
                HashSet<int> mappedInstanceIds = new HashSet<int>();

                foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
                {
                    if (kvp.Value != null)
                    {
                        mappedInstanceIds.Add(kvp.Value.GetInstanceID());
                    }
                }

                GONetParticipant[] allParticipants = UnityEngine.Object.FindObjectsOfType<GONetParticipant>(includeInactive: true);
                foreach (var gnp in allParticipants)
                {
                    if (gnp == null || gnp.GONetId == GONetParticipant.GONetId_Unset || gnp.SpawnerPersistentId == 0)
                    {
                        continue;
                    }

                    if (gnp.GetComponent<GONetLocal>() != null)
                    {
                        continue;
                    }

                    int instanceId = gnp.GetInstanceID();
                    if (mappedInstanceIds.Contains(instanceId))
                    {
                        continue;
                    }

                    uint gnpId = gnp.GONetId;
                    ushort owner = (ushort)(gnpId & ownerMask);
                    bool inAliveSet = reconciliationAliveSet.Contains(gnpId);

                    if (owner == previousOwnerAuthorityId)
                    {
                        uint raw = gnpId >> GONetParticipant.GONET_ID_BIT_COUNT_UNUSED;
                        uint serverId = unchecked((uint)(raw << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED)) | currentOwnerAuthorityId;
                        if (reconciliationAliveSet.Contains(serverId))
                        {
                            if (GONetMain.gonetParticipantByGONetIdMap.TryGetValue(serverId, out GONetParticipant mapped) && mapped != null)
                            {
                                DestroyGNP_LocalOnly(gnp);
                                unmappedDestroyed++;
                                continue;
                            }

                            gnp.GONetId = serverId;
                            unmappedRekeyed++;
                            mappedInstanceIds.Add(instanceId);
                            continue;
                        }
                    }

                    if (!inAliveSet)
                    {
                        DestroyGNP_LocalOnly(gnp);
                        unmappedDestroyed++;
                        continue;
                    }

                    if (GONetMain.gonetParticipantByGONetIdMap.TryGetValue(gnpId, out GONetParticipant mappedById) &&
                        mappedById != null &&
                        mappedById != gnp)
                    {
                        DestroyGNP_LocalOnly(gnp);
                        unmappedDuplicateDestroyed++;
                        continue;
                    }

                    // Ensure unmapped-but-alive participants are tracked so sync can reach them.
                    GONetMain.gonetParticipantByGONetIdMap[gnpId] = gnp;
                    if (gnp.GONetIdAtInstantiation != GONetParticipant.GONetId_Unset &&
                        !GONetMain.gonetParticipantByGONetIdAtInstantiationMap.ContainsKey(gnp.GONetIdAtInstantiation))
                    {
                        GONetMain.gonetParticipantByGONetIdAtInstantiationMap[gnp.GONetIdAtInstantiation] = gnp;
                    }
                    unmappedRegistered++;
                    mappedInstanceIds.Add(instanceId);
                }

                if (unmappedDestroyed + unmappedRekeyed + unmappedRegistered + unmappedDuplicateDestroyed > 0)
                {
                    if (unmappedRekeyed > 0 || unmappedRegistered > 0 || unmappedDuplicateDestroyed > 0)
                    {
                        needsSoARepair = true;
                    }
                    GONetLog.Warning($"[Reconciliation] Cleaned unmapped participants after demotion: destroyed={unmappedDestroyed}, rekeyed={unmappedRekeyed}, registered={unmappedRegistered}, dupDestroyed={unmappedDuplicateDestroyed}");
                }

                int instantiationMapRepaired = 0;
                foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
                {
                    GONetParticipant gnp = kvp.Value;
                    if (gnp == null || gnp.GONetIdAtInstantiation == GONetParticipant.GONetId_Unset)
                    {
                        continue;
                    }

                    if (!GONetMain.gonetParticipantByGONetIdAtInstantiationMap.TryGetValue(gnp.GONetIdAtInstantiation, out GONetParticipant existing) ||
                        existing == null)
                    {
                        GONetMain.gonetParticipantByGONetIdAtInstantiationMap[gnp.GONetIdAtInstantiation] = gnp;
                        instantiationMapRepaired++;
                    }
                }

                if (instantiationMapRepaired > 0)
                {
                    needsSoARepair = true;
                    GONetLog.Warning($"[Reconciliation] Repaired instantiation map after demotion: added={instantiationMapRepaired}");
                }

                // === DIAGNOSTIC: Enhanced rekey analysis ===
                // The rekey logic was designed for when the server re-keys GONetIds from owner=previousAuth to owner=1023.
                // But GONet v2 fix prevents re-keying, so objects should match directly.
                // This diagnostic checks BOTH direct matches AND the legacy derivation.
                int serverOwnedCount = 0;
                int directMatchCount = 0;         // Object exists locally with SAME GONetId
                int directMatchOwner1023 = 0;     // Direct match with local owner=1023
                int directMatchOwnerOther = 0;    // Direct match with local owner!=1023
                int derivedNullCount = 0;
                int wrongOwnerCount = 0;
                int alreadyRekeyedCount = 0;

                foreach (uint aliveId in snapshot.AliveGONetIds)
                {
                    ushort aliveOwnerAuthorityId = (ushort)(aliveId & ownerMask);

                    // DIAGNOSTIC: Check for DIRECT MATCH first (same GONetId exists locally)
                    if (GONetMain.gonetParticipantByGONetIdMap.TryGetValue(aliveId, out GONetParticipant directMatch))
                    {
                        directMatchCount++;
                        if (directMatch.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Server)
                            directMatchOwner1023++;
                        else
                            directMatchOwnerOther++;
                    }

                    // Legacy rekey logic: only process server-owned objects in snapshot
                    if (aliveOwnerAuthorityId != currentOwnerAuthorityId)
                    {
                        continue;
                    }
                    serverOwnedCount++;

                    uint presumedLocalId = (aliveId ^ currentOwnerAuthorityId) | previousOwnerAuthorityId;
                    GONetParticipant candidate = GONetMain.DeriveGNPFromCurrentAndPreviousValues(
                        aliveId,
                        previousOwnerAuthorityId,
                        currentOwnerAuthorityId);

                    if (candidate == null)
                    {
                        derivedNullCount++;
                        // DIAGNOSTIC: Check if direct match exists (same GONetId) instead of derived match
                        bool hasDirectMatch = GONetMain.gonetParticipantByGONetIdMap.ContainsKey(aliveId);
                        if (derivedNullCount <= 5)
                            GONetLog.Warning($"[Rekey-DIAG] aliveId={aliveId} (owner={aliveOwnerAuthorityId}): derivation NOT FOUND. DirectMatch={hasDirectMatch}, presumedLocalId={presumedLocalId}");
                        continue;
                    }
                    if (candidate.OwnerAuthorityId != previousOwnerAuthorityId)
                    {
                        wrongOwnerCount++;
                        if (wrongOwnerCount <= 5)
                            GONetLog.Info($"[Rekey] aliveId={aliveId} -> localId={candidate.GONetId}, WRONG OWNER: {candidate.OwnerAuthorityId} != expected {previousOwnerAuthorityId}");
                        continue;
                    }
                    if (candidate.GONetId == aliveId)
                    {
                        alreadyRekeyedCount++;
                        continue;
                    }

                    pendingRekeys ??= new List<(GONetParticipant gnp, uint newGONetId)>();
                    pendingRekeys.Add((candidate, aliveId));
                }

                GONetLog.Warning($"[Rekey-DIAG] DIRECT MATCHES: {directMatchCount}/{snapshot.AliveGONetIds.Length} snapshot objects exist locally (owner1023={directMatchOwner1023}, ownerOther={directMatchOwnerOther})");
                GONetLog.Warning($"[Rekey-DIAG] LEGACY REKEY: serverOwned={serverOwnedCount}, derivedNull={derivedNullCount}, wrongOwner={wrongOwnerCount}, alreadyRekeyed={alreadyRekeyedCount}, toRekey={pendingRekeys?.Count ?? 0}");

                if (pendingRekeys != null)
                {
                    int rekeyedCount = 0;
                    foreach (var rekey in pendingRekeys)
                    {
                        rekey.gnp.GONetId = rekey.newGONetId;
                        rekeyedCount++;
                    }

                    if (rekeyedCount > 0)
                    {
                        needsSoARepair = true;
                        GONetLog.Warning($"[Reconciliation] Rekeyed {rekeyedCount} participant(s) from authority {previousOwnerAuthorityId} to server based on snapshot");
                    }
                }
            }

            int ghostsDestroyed = 0;
            int objectsMatched = 0;
            List<GONetParticipant> toDestroy = null; // Lazy allocation

            // === DIAGNOSTIC: Reconciliation decision tracking ===
            int decisionPreservedSelfOwned = 0;
            int decisionFallThroughNotOwned = 0;
            int decisionMatchedAlive = 0;
            int decisionNotInAlive = 0;
            int decisionMatchedAliveOwner1023 = 0;
            int decisionMatchedAliveOwnerOther = 0;
            int decisionNotInAliveOwner1023 = 0;
            int decisionNotInAliveOwnerOther = 0;
            const int MAX_DECISION_LOG = 10;
            int decisionLogCount = 0;

            foreach (var gnp in GONetMain.gonetParticipantByGONetIdMap.Values)
            {
                if (gnp == null || gnp.GONetId == GONetParticipant.GONetId_Unset)
                    continue;

                // Skip scene objects - they are not reconciled
                if (gnp.SpawnerPersistentId == 0)
                    continue;

                // VOLUNTARY HANDOFF FIX (Dec 2025): If we recently voluntarily demoted (we were the server)
                // and this object was spawned by us, it's valid - don't destroy it.
                // These objects may have been spawned after the handoff snapshot was captured but before
                // handoff completed, so the new server doesn't know about them yet.
                //
                // CRITICAL FIX (Dec 2025): Server-owned transient objects (OwnerAuthorityId == 1023) should
                // NOT be preserved on demoted hosts. These are objects like projectiles, pickups, AI-spawned
                // entities that are "owned" by the server role, not by this specific node. When we demote,
                // we're no longer the server, so we're no longer responsible for these objects - the new
                // server is. Without this check, self-spawned server-owned objects get stuck on the demoted
                // host with IsLocallyResponsible=false and no one to clean them up.
                //
                // CRITICAL INSIGHT (Dec 2025): After voluntary demotion, we don't own ANY pre-existing
                // objects. Our old server authority (1023) is gone. Objects we spawned with other
                // OwnerAuthorityIds (e.g., OwnerAuth=2 for vice host) aren't ours either.
                // The ONLY objects we own after demotion are new ones with OwnerAuth=MyAuthorityId
                // (our new client authority), which don't exist at handoff time.
                //
                // So the correct logic is: only preserve self-spawned objects if we STILL OWN them.
                if (voluntaryDemotionPersistentId != 0)
                {
                    if (GONetGossipManager.Instance.TryGetNodePersistentId(GONetMain.MyAuthorityId, out ulong myPersistentId) &&
                        gnp.SpawnerPersistentId == myPersistentId)
                    {
                        // We spawned this object. But do we still own it after demotion?
                        bool weStillOwnIt = gnp.OwnerAuthorityId == GONetMain.MyAuthorityId;
                        if (weStillOwnIt)
                        {
                            // We spawned it AND we own it - preserve it
                            GONetLog.Debug($"[Reconciliation] Preserving self-owned object during voluntary demotion: GONetId={gnp.GONetId}, name={gnp.gameObject.name}, owner={gnp.OwnerAuthorityId}");
                            objectsMatched++;
                            decisionPreservedSelfOwned++;
                            continue;
                        }
                        else
                        {
                            // We spawned it but we DON'T own it anymore (OwnerAuth=1023 or OwnerAuth=other client)
                            // Let it fall through to the alive check - if server doesn't have it, destroy it.
                            decisionFallThroughNotOwned++;
                            if (decisionLogCount < MAX_DECISION_LOG)
                            {
                                GONetLog.Warning($"[Reconciliation-DIAG] FALL-THROUGH: GONetId={gnp.GONetId}, name={gnp.gameObject.name}, owner={gnp.OwnerAuthorityId}, myAuth={GONetMain.MyAuthorityId}, inAliveSet={reconciliationAliveSet.Contains(gnp.GONetId)}");
                                decisionLogCount++;
                            }
                        }
                    }
                }

                if (reconciliationAliveSet.Contains(gnp.GONetId))
                {
                    objectsMatched++;
                    decisionMatchedAlive++;
                    if (gnp.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Server)
                        decisionMatchedAliveOwner1023++;
                    else
                        decisionMatchedAliveOwnerOther++;
                }
                else
                {
                    // This is a ghost - server does not have it
                    decisionNotInAlive++;
                    if (gnp.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Server)
                        decisionNotInAliveOwner1023++;
                    else
                        decisionNotInAliveOwnerOther++;

                    if (decisionLogCount < MAX_DECISION_LOG)
                    {
                        GONetLog.Warning($"[Reconciliation-DIAG] NOT-IN-ALIVE (will destroy): GONetId={gnp.GONetId}, name={gnp.gameObject.name}, owner={gnp.OwnerAuthorityId}, IsMine={gnp.IsMine}");
                        decisionLogCount++;
                    }

                    if (toDestroy == null)
                        toDestroy = new List<GONetParticipant>();
                    toDestroy.Add(gnp);
                }
            }

            // === DIAGNOSTIC: Reconciliation decision summary ===
            GONetLog.Warning($"[Reconciliation-DIAG] DECISION summary: preservedSelfOwned={decisionPreservedSelfOwned}, fallThrough={decisionFallThroughNotOwned}, matchedAlive={decisionMatchedAlive}, notInAlive={decisionNotInAlive}");
            GONetLog.Warning($"[Reconciliation-DIAG] MATCHED breakdown: owner1023={decisionMatchedAliveOwner1023}, ownerOther={decisionMatchedAliveOwnerOther}");
            GONetLog.Warning($"[Reconciliation-DIAG] NOT-IN-ALIVE breakdown: owner1023={decisionNotInAliveOwner1023}, ownerOther={decisionNotInAliveOwnerOther}");

            // Destroy ghosts (deferred to avoid modifying collection during iteration)
            if (toDestroy != null)
            {
                foreach (var ghost in toDestroy)
                {
                    GONetLog.Warning($"[Reconciliation] Destroying ghost: GONetId={ghost.GONetId}, name={ghost.gameObject.name}, owner={ghost.OwnerAuthorityId}, spawner={ghost.SpawnerPersistentId:X}");
                    try
                    {
                        UnityEngine.Object.Destroy(ghost.gameObject);
                        ghostsDestroyed++;
                    }
                    catch (Exception ex)
                    {
                        GONetLog.Error($"[Reconciliation] Failed to destroy ghost GONetId={ghost.GONetId}: {ex.Message}");
                    }
                }
            }

            GONetLog.Info($"[Reconciliation] Processed snapshot: epoch={snapshot.FailoverEpoch}, matched={objectsMatched}, ghostsDestroyed={ghostsDestroyed}, serverAliveCount={snapshot.AliveGONetIds.Length}");

            if (needsSoARepair)
            {
                GONetLog.Warning("[Reconciliation] Running post-demotion SoA repair after reconciliation cleanup");
                GONetMain.RegisterNonMineObjectsInSoAAfterDemotion();
            }

            // === DIAGNOSTIC: SoA registration status for surviving objects ===
            // Objects that survive reconciliation but aren't registered in SoA won't receive sync.
            int soaRegisteredCount = 0;
            int soaNotRegisteredCount = 0;
            int soaNotRegisteredOwner1023 = 0;
            int soaNotRegisteredOwnerOther = 0;
            int soaDiagLogCount = 0;
            const int MAX_SOA_DIAG_LOG = 10;

            foreach (var gnp in GONetMain.gonetParticipantByGONetIdMap.Values)
            {
                if (gnp == null || gnp.GONetId == GONetParticipant.GONetId_Unset || gnp.SpawnerPersistentId == 0)
                    continue;

                // Skip objects that were just destroyed (may still be in map briefly)
                if (toDestroy != null && toDestroy.Contains(gnp))
                    continue;

                if (gnp.v2_isRegisteredInSoA)
                {
                    soaRegisteredCount++;
                }
                else
                {
                    soaNotRegisteredCount++;
                    if (gnp.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Server)
                        soaNotRegisteredOwner1023++;
                    else
                        soaNotRegisteredOwnerOther++;

                    if (soaDiagLogCount < MAX_SOA_DIAG_LOG)
                    {
                        GONetLog.Warning($"[SoA-DIAG] NOT REGISTERED: GONetId={gnp.GONetId}, name={gnp.gameObject.name}, owner={gnp.OwnerAuthorityId}, IsMine={gnp.IsMine}, IsLocallyResponsible={gnp.IsLocallyResponsible}");
                        soaDiagLogCount++;
                    }
                }
            }

            GONetLog.Warning($"[SoA-DIAG] POST-RECONCILIATION: registered={soaRegisteredCount}, notRegistered={soaNotRegisteredCount} (owner1023={soaNotRegisteredOwner1023}, ownerOther={soaNotRegisteredOwnerOther})");

            // Send acknowledgment back to server
            var ackEvent = new PostFailoverReconciliationAckEvent(
                failoverEpoch: snapshot.FailoverEpoch,
                ghostsDestroyed: ghostsDestroyed,
                objectsMatched: objectsMatched
            );
            ackEvent.OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks;
            GONetMain.EventBus.Publish(ackEvent);

            return ghostsDestroyed;
        }

        #endregion


        /// <summary>
        /// Processes spawner death by looking up persistent ID from authority ID.
        /// Convenience overload for when you only have the session authority ID.
        /// </summary>
        /// <param name="deadSpawnerAuthorityId">Session authority ID of the machine that died</param>
        /// <returns>Count of objects destroyed, count of objects that survived; (-1, -1) if lookup fails</returns>
        public (int destroyedCount, int survivedCount) ProcessSpawnerDeathByAuthorityId(
            ushort deadSpawnerAuthorityId,
            List<uint> destroyedGONetIds = null)
        {
            if (!GONetGossipManager.Instance.TryGetNodePersistentId(deadSpawnerAuthorityId, out ulong persistentId))
            {
                GONetLog.Warning($"[Failover] Cannot find persistent ID for authority {deadSpawnerAuthorityId} - spawner death processing skipped");
                return (-1, -1);
            }

            return ProcessSpawnerDeath(persistentId, 0, destroyedGONetIds);
        }

        /// <summary>
        /// Cleans up transient objects spawned by the previous host after a graceful handoff.
        /// Uses the captured persistent ID (pre-promotion) to avoid authority reuse ambiguity.
        /// </summary>
        public void HandleGracefulHandoffCleanup(ulong previousHostPersistentId, ushort previousHostAuthorityId)
        {
            if (previousHostPersistentId == GONetParticipant.SpawnerPersistentId_NoSpawner)
            {
                GONetLog.Warning("[Failover] Graceful handoff cleanup skipped - previous host persistent ID is unset");
                return;
            }

            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[Failover] Graceful handoff cleanup executing pre-promotion (not yet server)");
            }

            List<uint> destroyedGONetIds = new List<uint>();
            (int destroyedCount, int survivedCount) = ProcessSpawnerDeath(previousHostPersistentId, 0, destroyedGONetIds);

            if (destroyedCount > 0 || survivedCount > 0)
            {
                GONetLog.Info($"[Failover] Graceful handoff cleanup for previous host {previousHostAuthorityId} (persistentId: {previousHostPersistentId:X16}): destroyed={destroyedCount}, survived={survivedCount}");
            }

            if (destroyedGONetIds.Count > 0)
            {
                pendingDespawnNotifications.Clear();
                pendingDespawnNotifications.AddRange(destroyedGONetIds);
                GONetLog.Info($"[Failover] Queued {destroyedGONetIds.Count} GONetIds for deferred despawn notification (graceful handoff cleanup)");
            }

            CancelSynthesizedSpawns_ForDestroyedGONetIds(destroyedGONetIds, "graceful-handoff-cleanup");
        }

        /// <summary>
        /// Local-only cleanup after voluntary demotion to remove transient objects spawned by the old host.
        /// This does not publish despawns (client-side cleanup only).
        /// </summary>
        public void CleanupLocalTransientsAfterVoluntaryDemotion()
        {
            if (!didVoluntarilyDemote)
            {
                GONetLog.Warning("[Failover] Voluntary demotion cleanup skipped - didVoluntarilyDemote is false");
                return;
            }

            if (voluntaryDemotionPersistentId == GONetParticipant.SpawnerPersistentId_NoSpawner)
            {
                GONetLog.Warning("[Failover] Voluntary demotion cleanup missing persistent ID - running safety-net transient cleanup");
                int safetyDestroyedCount = CleanupServerOwnedTransientObjects();
                if (safetyDestroyedCount > 0)
                {
                    GONetLog.Warning($"[Failover] Voluntary demotion safety-net destroyed {safetyDestroyedCount} transient server-owned objects");
                }
                return;
            }

            ushort preservedAuthorityId = selfPromotedFromAuthorityId;
            (int destroyedCount, int survivedCount) = ProcessSpawnerDeath(voluntaryDemotionPersistentId, preservedAuthorityId);
            if (destroyedCount > 0 || survivedCount > 0)
            {
                GONetLog.Info($"[Failover] Voluntary demotion cleanup for former host (persistentId: {voluntaryDemotionPersistentId:X16}): destroyed={destroyedCount}, survived={survivedCount}");
            }

            int safetyNetDestroyed = CleanupServerOwnedTransientObjects();
            if (safetyNetDestroyed > 0)
            {
                GONetLog.Warning($"[Failover] Voluntary demotion safety-net destroyed {safetyNetDestroyed} additional transient objects");
            }
        }

        #endregion
    }

    #region Message Types

    /// <summary>
    /// Emergency host promotion announcement.
    /// </summary>
    [MemoryPackable]
    public partial class EmergencyHostPromotionMessage : ITransientEvent
    {
        /// <summary>
        /// Authority ID of the new host (typically 1023 after promotion).
        /// </summary>
        public ushort NewHostAuthorityId { get; set; }

        /// <summary>
        /// New host epoch.
        /// </summary>
        public uint NewHostEpoch { get; set; }

        /// <summary>
        /// Authority ID of the previous (failed) host.
        /// </summary>
        public ushort PreviousHostAuthorityId { get; set; }

        /// <summary>
        /// The ORIGINAL authority ID of the peer that is promoting (before they became 1023).
        /// This is critical for hot standby lookup - receivers have standby connections keyed
        /// by the peer's original authority ID, not their post-promotion server authority ID.
        /// </summary>
        public ushort PromotingPeerOriginalAuthorityId { get; set; }

        /// <summary>
        /// Reason for the failover.
        /// </summary>
        public string FailoverReason { get; set; }

        /// <summary>
        /// Timestamp when message was created.
        /// </summary>
        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    #endregion
}
