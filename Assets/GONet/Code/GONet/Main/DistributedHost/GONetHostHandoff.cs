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
using System.Runtime.CompilerServices;
using MemoryPack;
using GONet.Utils;
using UnityEngine;

namespace GONet.DistributedHost
{
    /// <summary>
    /// Current state of a host migration handoff.
    /// </summary>
    public enum HandoffState
    {
        /// <summary>
        /// No handoff in progress.
        /// </summary>
        None,

        /// <summary>
        /// Handoff initiated, waiting for vice host to prepare.
        /// </summary>
        Preparing,

        /// <summary>
        /// Sending delta state to vice host.
        /// </summary>
        TransferringDelta,

        /// <summary>
        /// Commit sent, waiting for new host to confirm.
        /// </summary>
        Committing,

        /// <summary>
        /// Handoff complete, new host is operational.
        /// </summary>
        Complete,

        /// <summary>
        /// Handoff was aborted.
        /// </summary>
        Aborted
    }

    /// <summary>
    /// Manages graceful host migration handoff.
    ///
    /// Message sequence:
    /// 1. HostHandoffPrepare - Host announces intent, includes snapshot tick
    /// 2. ViceHostPrepareAck - Vice host confirms ready
    /// 3. HostHandoffDelta - Delta state since last acknowledged vice host sync
    /// 4. HostHandoffCommit - Point of no return, all clients redirect
    /// 5. NewHostComplete - New host confirms operational
    ///
    /// Client behavior during handoff:
    /// - Buffer outgoing events (50-100ms window)
    /// - Accept new host identity on Commit
    /// - Flush buffered events to new host on Complete
    /// - Continue client-side prediction (don't freeze screen)
    /// </summary>
    public class GONetHostHandoffManager
    {
        #region Constants

        /// <summary>
        /// Maximum time to wait for vice host prepare acknowledgement.
        /// </summary>
        public const float PREPARE_TIMEOUT_SECONDS = 2.0f;

        /// <summary>
        /// Maximum time to wait for delta transfer completion.
        /// </summary>
        public const float DELTA_TRANSFER_TIMEOUT_SECONDS = 5.0f;

        /// <summary>
        /// Maximum time to wait for new host confirmation.
        /// </summary>
        public const float COMMIT_TIMEOUT_SECONDS = 3.0f;

        /// <summary>
        /// Delay before falling back to cleanup if the outgoing host never reconnects after a voluntary handoff.
        /// </summary>
        public const float VOLUNTARY_LOSSLESS_CLEANUP_TIMEOUT_SECONDS = 30.0f;

        /// <summary>
        /// How long clients should buffer events during handoff.
        /// </summary>
        public const float CLIENT_BUFFER_WINDOW_SECONDS = 0.1f; // 100ms

        #endregion

        #region State

        private HandoffState currentState = HandoffState.None;

        /// <summary>
        /// CRITICAL: Use raw ticks for state timing, not synchronized time.
        /// GONetMain.Time.ElapsedSeconds can have discontinuities during failover (time reset),
        /// which could cause premature timeouts or infinite waits.
        /// Raw ticks are monotonic and never reset during the session.
        /// </summary>
        private long stateStartTicks;

        private ushort targetViceHostId;
        private long handoffSnapshotTick;
        private uint newHostEpoch;
        private ushort sourceHostAuthorityId;
        private ulong sourceHostPersistentId;
        private ushort outgoingHostNewAuthorityId;

        private bool pendingLosslessCleanup;

        /// <summary>
        /// CRITICAL: Use raw ticks for lossless cleanup deadline, not synchronized time.
        /// This prevents the cleanup from triggering prematurely or never during time sync jumps.
        /// </summary>
        private long pendingLosslessCleanupDeadlineTicks;

        private ushort pendingLosslessCleanupSourceAuthorityId;
        private ulong pendingLosslessCleanupSourcePersistentId;
        private ushort pendingLosslessCleanupOutgoingAuthorityId;

        private const int POST_HANDOFF_MIGRATION_RETRY_COUNT = 2;
        private const float POST_HANDOFF_MIGRATION_RETRY_DELAY_SECONDS = 4f;
        private const float POST_HANDOFF_READY_RECHECK_DELAY_SECONDS = 0.75f;
        private ushort postHandoffPromotingAuthorityId;
        private int postHandoffMigrationRetriesRemaining;
        private long postHandoffMigrationNextRawTicks;
        private bool didLogPostHandoffIsReady;
        private bool postHandoffReadyRecheckPending;
        private long postHandoffReadyRecheckDueRawTicks;

        /// <summary>
        /// Atomic guard to prevent concurrent handoff initiation race condition.
        /// </summary>
        private volatile int handoffInitInProgress = 0;

        /// <summary>
        /// Buffered events during handoff (client-side).
        /// </summary>
        private readonly List<BufferedEvent> bufferedEvents = new List<BufferedEvent>(64);

        /// <summary>
        /// Whether this node is the outgoing host.
        /// </summary>
        private bool isOutgoingHost;

        /// <summary>
        /// Whether this node is the incoming host (vice host being promoted).
        /// </summary>
        private bool isIncomingHost;

        /// <summary>
        /// Whether the manager is initialized.
        /// </summary>
        private bool isInitialized;

        #endregion

        #region Events

        /// <summary>
        /// Fired when a handoff starts.
        /// </summary>
        public event Action<ushort> OnHandoffStarted;

        /// <summary>
        /// Fired when a handoff commits (point of no return).
        /// </summary>
        public event Action<ushort, uint> OnHandoffCommitted;

        /// <summary>
        /// Fired when a handoff completes successfully.
        /// </summary>
        public event Action<ushort> OnHandoffCompleted;

        /// <summary>
        /// Fired when a handoff is aborted.
        /// </summary>
        public event Action<string> OnHandoffAborted;

        /// <summary>
        /// Fired when this node should start buffering events (client-side).
        /// </summary>
        public event Action OnStartBuffering;

        /// <summary>
        /// Fired when this node should stop buffering and flush to new host.
        /// </summary>
        public event Action<ushort> OnFlushBuffer;

        #endregion

        #region Singleton

        private static GONetHostHandoffManager instance;
        public static GONetHostHandoffManager Instance => instance ??= new GONetHostHandoffManager();

        private GONetHostHandoffManager() { }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the current handoff state.
        /// </summary>
        public HandoffState CurrentState => currentState;

        /// <summary>
        /// Gets whether a handoff is in progress.
        /// </summary>
        public bool IsHandoffInProgress => currentState != HandoffState.None &&
                                           currentState != HandoffState.Complete &&
                                           currentState != HandoffState.Aborted;

        /// <summary>
        /// Gets whether this node is the outgoing host in a voluntary handoff.
        /// Used by the failover system to defer demotion to the handoff manager.
        /// </summary>
        public bool IsOutgoingHost => isOutgoingHost;

        /// <summary>
        /// Gets the outgoing host's reassigned authority during a lossless handoff (if pending).
        /// </summary>
        public bool TryGetPendingOutgoingHostAuthorityId(out ushort authorityId)
        {
            authorityId = pendingLosslessCleanupOutgoingAuthorityId;
            return pendingLosslessCleanup && authorityId != 0;
        }

        /// <summary>
        /// Gets the target vice host for the current handoff.
        /// </summary>
        public ushort TargetViceHostId => targetViceHostId;

        /// <summary>
        /// Gets the snapshot tick for the handoff freeze point.
        /// </summary>
        public long HandoffSnapshotTick => handoffSnapshotTick;

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the handoff manager.
        /// </summary>
        public void Initialize()
        {
            if (isInitialized) return;

            currentState = HandoffState.None;
            bufferedEvents.Clear();
            postHandoffPromotingAuthorityId = 0;
            postHandoffMigrationRetriesRemaining = 0;
            postHandoffMigrationNextRawTicks = 0;
            postHandoffReadyRecheckPending = false;
            postHandoffReadyRecheckDueRawTicks = 0;
            isInitialized = true;

            GONetLog.Info("[Handoff] Initialized");
        }

        /// <summary>
        /// Shuts down the handoff manager.
        /// </summary>
        public void Shutdown()
        {
            if (!isInitialized) return;

            if (IsHandoffInProgress)
            {
                AbortHandoff("Manager shutdown");
            }

            bufferedEvents.Clear();
            postHandoffPromotingAuthorityId = 0;
            postHandoffMigrationRetriesRemaining = 0;
            postHandoffMigrationNextRawTicks = 0;
            postHandoffReadyRecheckPending = false;
            postHandoffReadyRecheckDueRawTicks = 0;
            isInitialized = false;

            GONetLog.Info("[Handoff] Shut down");
        }

        #endregion

        #region Update Loop

        /// <summary>
        /// Called from GONetMain.Update() to process handoff timeouts.
        /// </summary>
        public void Update(float elapsedSeconds)
        {
            if (!isInitialized) return;

            if (IsHandoffInProgress)
            {
                // CRITICAL: Use raw ticks for timeout calculation to avoid time sync discontinuities
                long nowTicks = GONetMain.Time.RawElapsedTicks;
                float elapsedSeconds_raw = (float)(nowTicks - stateStartTicks) / TimeSpan.TicksPerSecond;

                switch (currentState)
                {
                    case HandoffState.Preparing:
                        if (elapsedSeconds_raw > PREPARE_TIMEOUT_SECONDS)
                        {
                            AbortHandoff("Prepare timeout - vice host did not respond");
                        }
                        break;

                    case HandoffState.TransferringDelta:
                        if (elapsedSeconds_raw > DELTA_TRANSFER_TIMEOUT_SECONDS)
                        {
                            AbortHandoff("Delta transfer timeout");
                        }
                        break;

                    case HandoffState.Committing:
                        if (elapsedSeconds_raw > COMMIT_TIMEOUT_SECONDS)
                        {
                            if (isOutgoingHost)
                            {
                                CompleteOutgoingHostHandoff("New host confirmation timeout", wasTimeout: true);
                            }
                            else
                            {
                                // After commit, we can't really abort - just log error
                                GONetLog.Error("[Handoff] New host confirmation timeout - handoff may be incomplete");
                                TransitionTo(HandoffState.Complete);
                            }
                        }
                        break;
                }
            }

            if (postHandoffReadyRecheckPending &&
                GONetMain.Time.RawElapsedTicks >= postHandoffReadyRecheckDueRawTicks)
            {
                postHandoffReadyRecheckPending = false;
                ExecutePostHandoffReadyRecheck();
            }

            // CRITICAL: Use raw ticks for lossless cleanup deadline
            if (pendingLosslessCleanup && GONetMain.Time.RawElapsedTicks >= pendingLosslessCleanupDeadlineTicks)
            {
                pendingLosslessCleanup = false;

                if (pendingLosslessCleanupSourcePersistentId != 0)
                {
                    GONetLog.Warning($"[Handoff] Lossless cleanup timeout - outgoing host {pendingLosslessCleanupOutgoingAuthorityId} did not reconnect, applying cleanup");
                    GONetHostFailoverManager.Instance.HandleGracefulHandoffCleanup(
                        pendingLosslessCleanupSourcePersistentId,
                        pendingLosslessCleanupSourceAuthorityId);
                    int sentCount = GONetHostFailoverManager.Instance.SendPendingDespawnNotifications();
                    if (sentCount > 0)
                    {
                        GONetLog.Info($"[Handoff] Sent {sentCount} despawn notifications after lossless cleanup fallback");
                    }
                }
                else
                {
                    GONetLog.Warning("[Handoff] Lossless cleanup timeout but source host persistent ID missing - skipping cleanup");
                }
            }

            if (postHandoffMigrationRetriesRemaining > 0 &&
                postHandoffPromotingAuthorityId != 0 &&
                GONetMain.IsServer &&
                GONetMain.Time.RawElapsedTicks >= postHandoffMigrationNextRawTicks)
            {
                int migratedCount = GONetHostFailoverManager.Instance.MigratePromotingClientOwnedObjectsToServer(
                    postHandoffPromotingAuthorityId,
                    "voluntary-handoff-retry");

                postHandoffMigrationRetriesRemaining--;
                if (postHandoffMigrationRetriesRemaining > 0)
                {
                    postHandoffMigrationNextRawTicks = GONetMain.Time.RawElapsedTicks +
                        (long)(POST_HANDOFF_MIGRATION_RETRY_DELAY_SECONDS * TimeSpan.TicksPerSecond);
                }
                else
                {
                    postHandoffPromotingAuthorityId = 0;
                }

                GONetLog.Info($"[Handoff] Post-handoff ownership migration retry: migrated={migratedCount}, remaining={postHandoffMigrationRetriesRemaining}");
            }
        }

        /// <summary>
        /// Diagnostic hook for standby peer disconnects during handoff.
        /// </summary>
        public void NotifyStandbyPeerDisconnected(ushort peerAuthorityId)
        {
            if (!isInitialized) return;
            if (!IsHandoffInProgress) return;

            bool isCriticalState = currentState == HandoffState.Preparing ||
                                   currentState == HandoffState.TransferringDelta ||
                                   currentState == HandoffState.Committing;

            if (!isCriticalState)
            {
                return;
            }

            if (isOutgoingHost && peerAuthorityId == targetViceHostId)
            {
                GONetLog.Error($"[Handoff] CRITICAL: New host {peerAuthorityId} disconnected during handoff (state={currentState})");
                return;
            }

            if (isIncomingHost && peerAuthorityId == sourceHostAuthorityId)
            {
                GONetLog.Error($"[Handoff] CRITICAL: Source host {peerAuthorityId} disconnected during handoff (state={currentState})");
            }
        }

        #endregion

        #region Host-Side: Initiating Handoff

        /// <summary>
        /// Initiates a graceful handoff to the designated vice host.
        /// Only the current host can call this.
        /// </summary>
        /// <param name="viceHostAuthorityId">Authority ID of the vice host to promote</param>
        /// <returns>True if handoff was initiated</returns>
        public bool InitiateGracefulHandoff(ushort viceHostAuthorityId)
        {
            // CRITICAL: Atomic guard to prevent concurrent handoff initiation race condition.
            // Between the IsHandoffInProgress check and state assignment, a network message
            // could arrive and cause both initiations to partially execute, corrupting state.
            if (System.Threading.Interlocked.CompareExchange(ref handoffInitInProgress, 1, 0) != 0)
            {
                GONetLog.Warning("[Handoff] Cannot initiate handoff - initialization already in progress");
                return false;
            }

            try
            {
                if (!GONetMain.IsServer)
                {
                    GONetLog.Warning("[Handoff] Cannot initiate handoff - not the current host");
                    return false;
                }

                if (IsHandoffInProgress)
                {
                    GONetLog.Warning("[Handoff] Cannot initiate handoff - already in progress");
                    return false;
                }

                if (viceHostAuthorityId == 0)
                {
                    GONetLog.Warning("[Handoff] Cannot initiate handoff - no vice host specified");
                    return false;
                }

                // Verify vice host is known in gossip
                if (!GONetGossipManager.Instance.TryGetNodeMetrics(viceHostAuthorityId, out _))
                {
                    GONetLog.Warning($"[Handoff] Cannot initiate handoff - vice host {viceHostAuthorityId} not found in gossip");
                    return false;
                }

                // ROBUSTNESS: Verify vice host is reachable in the mesh, not just known in gossip.
                // A node could be in gossip but unreachable due to network partition.
                var hotStandby = GONetHotStandbyManager.Instance;
                if (hotStandby != null && !hotStandby.IsConnectedInMesh(viceHostAuthorityId))
                {
                    GONetLog.Warning($"[Handoff] Cannot initiate handoff - vice host {viceHostAuthorityId} not reachable in mesh");
                    return false;
                }

                targetViceHostId = viceHostAuthorityId;
                handoffSnapshotTick = GONetMain.Time.ElapsedTicks;
                newHostEpoch = GONetMain.HostEpoch + 1;
                isOutgoingHost = true;

                TransitionTo(HandoffState.Preparing);

                outgoingHostNewAuthorityId = ResolveOutgoingHostNewAuthorityId();
                if (outgoingHostNewAuthorityId == 0)
                {
                    GONetLog.Warning("[Handoff] Could not reserve outgoing host client authority - lossless handoff may be limited");
                }

                // Send prepare message to vice host
                var prepareMsg = new HostHandoffPrepareMessage
                {
                    SourceHostAuthorityId = GONetMain.MyAuthorityId,
                    TargetViceHostAuthorityId = viceHostAuthorityId,
                    NewHostEpoch = newHostEpoch,
                    SnapshotTick = handoffSnapshotTick,
                    OutgoingHostNewAuthorityId = outgoingHostNewAuthorityId,
                    OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
                };

                SendHandoffMessage(prepareMsg);

                GONetLog.Info($"[Handoff] Initiated graceful handoff to authority {viceHostAuthorityId} at tick {handoffSnapshotTick}");

                OnHandoffStarted?.Invoke(viceHostAuthorityId);
                return true;
            }
            finally
            {
                // Release the atomic guard - handoff is now in progress (or failed)
                System.Threading.Interlocked.Exchange(ref handoffInitInProgress, 0);
            }
        }

        /// <summary>
        /// Aborts the current handoff (before commit only).
        /// </summary>
        public void AbortHandoff(string reason)
        {
            if (!IsHandoffInProgress)
            {
                return;
            }

            if (currentState == HandoffState.Committing || currentState == HandoffState.Complete)
            {
                GONetLog.Warning($"[Handoff] Cannot abort after commit - reason: {reason}");
                return;
            }

            GONetLog.Warning($"[Handoff] Aborting handoff: {reason}");

            // Send abort message if we're the host
            if (isOutgoingHost)
            {
                var abortMsg = new HostHandoffAbortMessage
                {
                    Reason = reason,
                    OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
                };
                SendHandoffMessage(abortMsg);
            }

            TransitionTo(HandoffState.Aborted);

            // Clear buffered events (client-side)
            bufferedEvents.Clear();

            OnHandoffAborted?.Invoke(reason);

            // Reset state
            ResetHandoffState();
        }

        #endregion

        #region Host-Side: Processing Handoff

        /// <summary>
        /// Called when the vice host acknowledges prepare.
        /// </summary>
        public void OnViceHostPrepareAck(ViceHostPrepareAckMessage message)
        {
            if (!isOutgoingHost) return;
            if (currentState != HandoffState.Preparing) return;

            if (message.ViceHostAuthorityId != targetViceHostId)
            {
                GONetLog.Warning($"[Handoff] Received ack from wrong vice host: {message.ViceHostAuthorityId}");
                return;
            }

            if (!message.IsReady)
            {
                AbortHandoff($"Vice host not ready: {message.RejectionReason}");
                return;
            }

            GONetLog.Info($"[Handoff] Vice host {targetViceHostId} is ready, sending delta");

            TransitionTo(HandoffState.TransferringDelta);

            // Send delta state
            SendDeltaState(message.LastSyncSequence);
        }

        /// <summary>
        /// Sends delta state to vice host.
        /// </summary>
        private void SendDeltaState(ulong viceHostLastSyncSequence)
        {
            // Try to capture delta
            var delta = GONetStateSnapshotManager.CaptureDeltaSnapshot(viceHostLastSyncSequence);

            byte[] deltaData;
            if (delta != null)
            {
                // Delta available
                var bytes = SerializationUtils.SerializeToBytes(delta, out int bytesUsed, out bool needsReturn);
                deltaData = new byte[bytesUsed];
                Array.Copy(bytes, deltaData, bytesUsed);
                if (needsReturn) SerializationUtils.ReturnByteArray(bytes);

                GONetLog.Info($"[Handoff] Sending delta snapshot ({bytesUsed} bytes)");
            }
            else
            {
                // Fall back to full snapshot
                var snapshot = GONetStateSnapshotManager.CaptureFullSnapshot();
                if (snapshot == null)
                {
                    AbortHandoff("Failed to capture state snapshot");
                    return;
                }

                deltaData = GONetStateSnapshotManager.SerializeSnapshot(snapshot);
                GONetLog.Info($"[Handoff] Sending full snapshot ({deltaData.Length} bytes)");
            }

            var deltaMsg = new HostHandoffDeltaMessage
            {
                DeltaData = deltaData,
                IsDelta = delta != null,
                SnapshotTick = handoffSnapshotTick,
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };

            SendHandoffMessage(deltaMsg);

            // Immediately send commit (for now - could wait for delta ack in future)
            SendCommit();
        }

        /// <summary>
        /// Sends the commit message - point of no return.
        /// </summary>
        private void SendCommit()
        {
            TransitionTo(HandoffState.Committing);

            var commitMsg = new HostHandoffCommitMessage
            {
                NewHostAuthorityId = targetViceHostId,
                NewHostEpoch = newHostEpoch,
                CommitTick = GONetMain.Time.ElapsedTicks,
                OutgoingHostNewAuthorityId = outgoingHostNewAuthorityId,
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };

            // Send to ALL clients (including vice host)
            SendHandoffMessage(commitMsg, broadcast: true);

            GONetLog.Info($"[Handoff] COMMIT sent - new host: authority {targetViceHostId}, epoch {newHostEpoch}");

            OnHandoffCommitted?.Invoke(targetViceHostId, newHostEpoch);
        }

        /// <summary>
        /// Called when the new host confirms operational.
        /// </summary>
        public void OnNewHostComplete(NewHostCompleteMessage message)
        {
            if (!isOutgoingHost) return;
            if (currentState != HandoffState.Committing) return;

            bool isExpectedAuthority =
                message.NewHostAuthorityId == targetViceHostId ||
                message.NewHostAuthorityId == GONetMain.OwnerAuthorityId_Server;
            if (!isExpectedAuthority)
            {
                GONetLog.Warning($"[Handoff] Complete from wrong host: {message.NewHostAuthorityId}");
                return;
            }

            if (message.NewHostAuthorityId == GONetMain.OwnerAuthorityId_Server &&
                targetViceHostId != GONetMain.OwnerAuthorityId_Server)
            {
                GONetLog.Info($"[Handoff] New host confirmed via server authority for vice host {targetViceHostId}");
            }

            GONetLog.Info($"[Handoff] New host {targetViceHostId} confirmed operational at epoch {newHostEpoch}");

            CompleteOutgoingHostHandoff("New host confirmation received", wasTimeout: false);
        }

        #endregion

        #region Vice Host-Side: Receiving Handoff

        /// <summary>
        /// Called when this node (vice host) receives a handoff prepare.
        /// </summary>
        public void OnHandoffPrepare(HostHandoffPrepareMessage message)
        {
            didLogPostHandoffIsReady = false;

            if (message.TargetViceHostAuthorityId != GONetMain.MyAuthorityId)
            {
                // Not for us - just observe (start buffering if client)
                if (!GONetMain.IsServer)
                {
                    OnStartBuffering?.Invoke();
                }
                return;
            }

            isIncomingHost = true;
            targetViceHostId = GONetMain.MyAuthorityId;
            handoffSnapshotTick = message.SnapshotTick;
            newHostEpoch = message.NewHostEpoch;
            sourceHostAuthorityId = message.SourceHostAuthorityId;
            outgoingHostNewAuthorityId = message.OutgoingHostNewAuthorityId;
            sourceHostPersistentId = 0;

            if (!GONetGossipManager.Instance.TryGetNodePersistentId(sourceHostAuthorityId, out sourceHostPersistentId))
            {
                GONetLog.Warning($"[Handoff] Could not capture source host persistent ID (authority {sourceHostAuthorityId}) - cleanup may be incomplete");
            }
            else
            {
                GONetLog.Info($"[Handoff] Captured source host persistent ID {sourceHostPersistentId:X16} (authority {sourceHostAuthorityId})");
            }

            GONetLog.Info($"[Handoff] Received prepare from host {message.SourceHostAuthorityId} - preparing to take over");

            // Prepare for handoff
            bool isReady = GONetViceHostManager.Instance.PrepareForHandoff();
            ulong lastSync = GONetViceHostManager.Instance.LastReceivedSyncSequence;

            var ackMsg = new ViceHostPrepareAckMessage
            {
                ViceHostAuthorityId = GONetMain.MyAuthorityId,
                IsReady = isReady,
                LastSyncSequence = lastSync,
                RejectionReason = isReady ? null : "Not ready for handoff",
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
            };

            SendHandoffMessage(ackMsg);

            if (isReady)
            {
                TransitionTo(HandoffState.Preparing);
            }
        }

        /// <summary>
        /// Called when this node (vice host) receives delta state.
        /// </summary>
        public void OnHandoffDelta(HostHandoffDeltaMessage message)
        {
            if (!isIncomingHost) return;

            GONetLog.Info($"[Handoff] Received {(message.IsDelta ? "delta" : "full")} state ({message.DeltaData?.Length ?? 0} bytes)");

            TransitionTo(HandoffState.TransferringDelta);

            // Apply the state
            if (message.IsDelta)
            {
                // TODO: Apply delta
            }
            else
            {
                // Apply full snapshot
                var snapshot = GONetStateSnapshotManager.DeserializeSnapshot(message.DeltaData, message.DeltaData.Length);
                if (snapshot != null)
                {
                    GONetStateSnapshotManager.ApplySnapshot(snapshot);
                }
            }
        }

        /// <summary>
        /// Called when commit is received (all nodes).
        /// </summary>
        public void OnHandoffCommit(HostHandoffCommitMessage message)
        {
            GONetLog.Info($"[Handoff] COMMIT received - new host: authority {message.NewHostAuthorityId}, epoch {message.NewHostEpoch}");

            // Update host identity
            GONetMain.AdoptHostIdentity(message.NewHostEpoch, message.NewHostAuthorityId, 0);
            if (message.OutgoingHostNewAuthorityId != 0)
            {
                outgoingHostNewAuthorityId = message.OutgoingHostNewAuthorityId;
            }

            if (isIncomingHost && message.NewHostAuthorityId == GONetMain.MyAuthorityId)
            {
                // We are the new host!
                TransitionTo(HandoffState.Committing);

                // CRITICAL: Preserve time sync offset BEFORE becoming host.
                // This maintains time continuity for other clients during graceful handoff.
                long oneWayDelayTicks = 0;
                var connection = GONetMain.GONetClient?.connectionToServer;
                if (connection != null && connection.RTT_RecentAverage > 0)
                {
                    oneWayDelayTicks = (long)(connection.RTT_RecentAverage * TimeSpan.TicksPerSecond) >> 1;
                }
                GONetMain.PreserveTimeOffsetForFailover(message.CommitTick, oneWayDelayTicks, "handoff_commit");

                ushort promotingClientOriginalAuthorityId = GONetMain.MyAuthorityId;
                bool canAttemptLossless = outgoingHostNewAuthorityId != 0;
                GONetLocal outgoingHostLocal = null;
                if (canAttemptLossless && GONetLocal.LookupByAuthorityId != null)
                {
                    outgoingHostLocal = GONetLocal.LookupByAuthorityId[GONetMain.OwnerAuthorityId_Server];
                }

                // Promote ourselves
                GONetHostFailoverManager.Instance.OnBecameHost();
                GONetViceHostManager.Instance.OnBecameHost();
                if (outgoingHostNewAuthorityId != 0)
                {
                    GONetViceHostManager.Instance.SetDemotedHostAuthority(outgoingHostNewAuthorityId);
                }

                if (canAttemptLossless)
                {
                    ScheduleLosslessCleanup(sourceHostAuthorityId, sourceHostPersistentId, outgoingHostNewAuthorityId);
                }
                else if (sourceHostPersistentId != 0)
                {
                    GONetHostFailoverManager.Instance.HandleGracefulHandoffCleanup(sourceHostPersistentId, sourceHostAuthorityId);
                }
                else
                {
                    GONetLog.Warning("[Handoff] Graceful cleanup skipped - missing source host persistent ID");
                }

                // CRITICAL (December 2025): Promote the dormant server to active and set isServerOverride = true.
                // Without this, GONetMain.IsServer remains false after graceful handoff, causing:
                // - GONetStatusUI to show wrong role (Client instead of Server+Client)
                // - Server-only logic to not execute on the new host
                // This was already handled in emergency failover via OnSelfPromotedToHost event,
                // but graceful handoff was missing this step.
                GONetHotStandbyManager.Instance.OnBecameHost();

                // CRITICAL FIX (Jan 2026): Notify gossip integration of host status change.
                // This clears stale remoteMetrics[1023] from the old server and updates local identity.
                // Without this, the gossip aggregate contains the old server's endpoint (e.g., port 1)
                // which overwrites the correct endpoint received via StandbyHello, causing late-joiners
                // to fail connecting to the promoted host's dormant server ("3/2 peers" issue).
                // NOTE: Must be called AFTER OnBecameHost() sets isServerOverride = true.
                GONetGossipIntegration.OnHostStatusChanged(true);

                // HANDOFF FIX (January 2026): Ensure the new server's authority counter is updated
                // to avoid reusing the authority ID reserved for the demoted host.
                // Without this, late joiners could be assigned the same authority ID as the demoted host.
                // NOTE: Must be called AFTER OnBecameHost() sets isServerOverride = true.
                if (outgoingHostNewAuthorityId != 0)
                {
                    GONetMain.EnsureServerAuthorityHighWaterMark(outgoingHostNewAuthorityId);
                }

                int migratedFromClientCount = GONetHostFailoverManager.Instance.MigratePromotingClientOwnedObjectsToServer(
                    promotingClientOriginalAuthorityId,
                    "voluntary-handoff");

                if (promotingClientOriginalAuthorityId != 0)
                {
                    postHandoffPromotingAuthorityId = promotingClientOriginalAuthorityId;
                    postHandoffMigrationRetriesRemaining = POST_HANDOFF_MIGRATION_RETRY_COUNT;
                    postHandoffMigrationNextRawTicks = GONetMain.Time.RawElapsedTicks +
                        (long)(POST_HANDOFF_MIGRATION_RETRY_DELAY_SECONDS * TimeSpan.TicksPerSecond);
                    GONetLog.Info($"[Handoff] Scheduled {postHandoffMigrationRetriesRemaining} post-handoff ownership migration retries for authority {postHandoffPromotingAuthorityId}");
                }

                // CRITICAL FIX (Dec 2025): Reset blend buffers for server-owned objects.
                // These objects (OwnerAuthorityId=1023) don't change ownership during handoff, so
                // MigratePromotingClientOwnedObjectsToServer doesn't process them. However, their
                // at-rest tracking bits are stuck in NEEDS_TO_BROADCAST state from the old host,
                // causing massive log spam and 2 FPS performance.
                GONetMain.ResetBlendBuffersForAllServerOwnedObjects();

                if (canAttemptLossless)
                {
                    if (outgoingHostLocal != null)
                    {
                        ushort previousOwner = outgoingHostLocal.GONetParticipant.OwnerAuthorityId;
                        if (previousOwner == outgoingHostNewAuthorityId)
                        {
                            GONetLog.Info($"[Handoff] Outgoing host authority already set to {outgoingHostNewAuthorityId}");
                        }
                        else
                        {
                            outgoingHostLocal.GONetParticipant.OwnerAuthorityId = outgoingHostNewAuthorityId;
                            GONetLocal.UpdateAuthorityMapping(outgoingHostLocal, previousOwner, outgoingHostNewAuthorityId);
                            GONetLog.Info($"[Handoff] Reassigned outgoing host GONetLocal owner {previousOwner} -> {outgoingHostNewAuthorityId} (captured pre-promotion)");
                        }
                    }
                    else if (!GONetMain.Server_ReassignOutgoingHostAuthority(outgoingHostNewAuthorityId))
                    {
                        GONetLog.Warning($"[Handoff] Failed to reassign outgoing host authority to {outgoingHostNewAuthorityId} - lossless handoff may degrade");
                    }
                }

                // Seed persistent events for the promoted host and replay to already-connected clients.
                GONetMain.SynthesizePersistentEventsForPromotedHost();
                GONetMain.Server_SendPersistentEventsToExistingClients();
                if (outgoingHostNewAuthorityId != 0)
                {
                    GONetMain.Server_RequestFullStateSyncForClient(outgoingHostNewAuthorityId, "voluntary-handoff-promotion");
                }

                // Send complete confirmation
                var completeMsg = new NewHostCompleteMessage
                {
                    // Use pre-promotion authority so outgoing host can validate completion.
                    NewHostAuthorityId = promotingClientOriginalAuthorityId,
                    NewHostEpoch = message.NewHostEpoch,
                    OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks
                };

                SendHandoffMessage(completeMsg, broadcast: true);

                TransitionTo(HandoffState.Complete);

                GONetLog.Info("[Handoff] This node is now the host!");

                var completedEvent = new GONet.HostFailoverCompletedEvent(
                    GONetMain.Time.ElapsedTicks,
                    GONetMain.MyAuthorityId,
                    promotingClientOriginalAuthorityId,
                    isSelf: true,
                    migratedGNPCount: migratedFromClientCount);
                GONetMain.EventBus.Publish<GONet.IGONetEvent>(completedEvent);
                GONetMain.BroadcastHostFailoverCompleted(GONetMain.MyAuthorityId, promotingClientOriginalAuthorityId, isSelf: true);

                OnHandoffCompleted?.Invoke(GONetMain.MyAuthorityId);
                ResetHandoffState();
            }
            else
            {
                // We are a regular client - flush buffered events to new host
                OnFlushBuffer?.Invoke(message.NewHostAuthorityId);
                bufferedEvents.Clear();

                if (!isOutgoingHost)
                {
                    // CRITICAL FIX (Dec 2025): Third-party clients (not vice host, not demoting host) must
                    // also switch their main connection to the new host. Without this, when the old host
                    // demotes and shuts down its server, third-party clients lose connection and never reconnect.
                    // The demoted host already does this (see TryActivateStandbyConnection call in DemoteIfOutgoingHost),
                    // but regular clients were missing this step entirely.
                    if (GONetHotStandbyManager.Instance.TryActivateStandbyConnection(message.NewHostAuthorityId, message.NewHostEpoch))
                    {
                        GONetLog.Info($"[Handoff] Third-party client activated standby connection to new host (authority {message.NewHostAuthorityId}, epoch {message.NewHostEpoch})");
                    }
                    else
                    {
                        GONetLog.Warning($"[Handoff] Third-party client could not activate standby connection to new host {message.NewHostAuthorityId} - may need manual reconnect");
                    }

                    // HANDOFF FIX (Jan 2025): Reset blend buffers for third-party clients after handoff.
                    // Third-party clients have stale V1 blend data from the old host. After handoff:
                    // 1. Time sync resets and enters gap-closing phase (~15 seconds)
                    // 2. V1 sync data from new host arrives with timestamps in new host's time domain
                    // 3. With stale time offset, data is placed incorrectly in blend buffer (or rejected)
                    // 4. V1 blending extrapolates from stale pre-handoff data → frozen visuals
                    // Position/rotation (SoA) work because they use direct value application, not timestamp-based blending.
                    // Resetting blend buffers allows fresh data from new host to be written cleanly.
                    GONetMain.ResetBlendBuffersForAllServerOwnedObjects(applyCurrentValueBeforeReset: false);

                    // Start grace period to bypass STALE_DATA check during time sync gap-closing phase.
                    // After blend buffer reset, historyCount=0. The STALE_DATA check (historyCount <= 2)
                    // would reject the first sync samples from new host, causing ~24 second stuck period.
                    GONetMain.StartPostHandoffGracePeriod();

                    // Clear deserialization requirements for server-owned objects.
                    // Before handoff, these objects had requiresDeserializeInit=true waiting for initial sync.
                    // After handoff, they won't receive DeserializeInitAllCompleted (that's for new spawns only).
                    // Without clearing, IsGONetReady() returns false forever → V1 blending visuals never update.
                    GONetHostFailoverManager.Instance?.ClearDeserializeInitRequirements_ForServerOwnedObjects();

                    if (!didLogPostHandoffIsReady)
                    {
                        didLogPostHandoffIsReady = true;
                        LogServerOwnedIsReadyState("post-handoff");
                    }

                    SchedulePostHandoffReadyRecheck();

                    ushort promotedAuthorityId = GONetMain.OwnerAuthorityId_Server;
                    var completedEvent = new GONet.HostFailoverCompletedEvent(
                        GONetMain.Time.ElapsedTicks,
                        promotedAuthorityId,
                        message.NewHostAuthorityId,
                        isSelf: false,
                        migratedGNPCount: 0);
                    GONetMain.EventBus.Publish<GONet.IGONetEvent>(completedEvent);
                    GONetMain.BroadcastHostFailoverCompleted(promotedAuthorityId, message.NewHostAuthorityId, isSelf: false);
                }
            }

            OnHandoffCommitted?.Invoke(message.NewHostAuthorityId, message.NewHostEpoch);
        }

        /// <summary>
        /// Called when abort is received.
        /// </summary>
        public void OnHandoffAbort(HostHandoffAbortMessage message)
        {
            if (!IsHandoffInProgress) return;

            GONetLog.Warning($"[Handoff] Received abort: {message.Reason}");

            TransitionTo(HandoffState.Aborted);
            bufferedEvents.Clear();

            OnHandoffAborted?.Invoke(message.Reason);
            ResetHandoffState();
        }

        #endregion

        #region Client-Side: Event Buffering

        /// <summary>
        /// Buffers an event during handoff (client-side).
        /// </summary>
        public void BufferEvent(IGONetEvent evt, long occurredAtTicks)
        {
            if (!IsHandoffInProgress) return;

            bufferedEvents.Add(new BufferedEvent
            {
                Event = evt,
                OccurredAtTicks = occurredAtTicks
            });
        }

        /// <summary>
        /// Gets buffered events for flushing to new host.
        /// </summary>
        public IReadOnlyList<BufferedEvent> GetBufferedEvents()
        {
            return bufferedEvents;
        }

        /// <summary>
        /// Clears the buffered events.
        /// </summary>
        public void ClearBufferedEvents()
        {
            bufferedEvents.Clear();
        }

        #endregion

        #region Internal

        private void TransitionTo(HandoffState newState)
        {
            if (currentState != newState)
            {
                GONetLog.Debug($"[Handoff] State: {currentState} -> {newState}");
                currentState = newState;
                // CRITICAL: Use raw ticks for state timing, not synchronized time
                stateStartTicks = GONetMain.Time.RawElapsedTicks;
            }
        }

        private void CompleteOutgoingHostHandoff(string reason, bool wasTimeout)
        {
            if (!isOutgoingHost)
            {
                return;
            }

            if (wasTimeout)
            {
                GONetLog.Error($"[Handoff] {reason} - forcing outgoing host demotion");
            }
            else
            {
                GONetLog.Info($"[Handoff] {reason} - outgoing host demoting");
            }

            TransitionTo(HandoffState.Complete);
            OnHandoffCompleted?.Invoke(targetViceHostId);

            ushort previousHostAuthorityId = GONetMain.MyAuthorityId;
            ushort previousHostOriginalAuthorityId = GONetHostFailoverManager.Instance.SelfPromotedFromAuthorityId;
            ushort newHostAuthorityId = GONetMain.OwnerAuthorityId_Server;
            ushort newHostOriginalAuthorityId = targetViceHostId;
            uint newHostEpochSnapshot = newHostEpoch;

            // Old host demotes itself
            GONetGossipManager.Instance.OnHostStatusChanged(false);
            GONetHostFailoverManager.Instance.OnStoppedBeingHost(targetViceHostId, newHostEpoch);
            GONetViceHostManager.Instance.OnStoppedBeingHost();

            // CRITICAL (December 2025): Full demotion for graceful handoff.
            // The hot standby manager needs to restart its dormant server in DormantMesh mode.
            GONetHotStandbyManager.Instance.OnDemotedFromHost();

            if (GONetMain.gonetServer != null)
            {
                try { GONetMain.gonetServer.Stop(); } catch { }
            }

            DemoteOutgoingHostAuthority();

            // CRITICAL FIX (Dec 2025): Reset blend buffers on demoted host.
            // The demoted host was sending sync data as the authority. After demotion, it needs to
            // receive sync data from the new host. Reset all blend buffers so it can properly
            // receive and apply incoming value changes. This also resets physics to kinematic.
            GONetMain.ResetBlendBuffersForDemotedHost();

            // HANDOFF FIX (Jan 2025): Clear deserialization requirements on demoted host.
            // Same issue as third-party clients: server-owned objects had requiresDeserializeInit=true
            // waiting for initial sync that completed before handoff. Without clearing, IsGONetReady()
            // returns false forever → V1 blending visuals never update for Vector2/Vector4/scalar types.
            GONetHostFailoverManager.Instance?.ClearDeserializeInitRequirements_ForServerOwnedObjects();

            // CRITICAL FIX (Dec 2025): Start post-handoff grace period.
            // After reset, historyCount=0 for all objects. The STALE_DATA check (historyCount <= 2)
            // would reject the first sync samples from the new host, causing objects to stay stuck.
            // The grace period bypasses this check so sync is applied immediately.
            GONetMain.StartPostHandoffGracePeriod();

            // CRITICAL FIX (Dec 2025): Reset time sync state for demotion.
            // The demoted host was the time authority (EffectiveOffset=0). After demotion, it needs
            // to sync to the new host's time. Without this reset, time jumps to incorrect values
            // (e.g., 533s instead of 130s) because the new host sends time with its preserved offset.
            GONetMain.ResetTimeSyncForDemotion();

            ushort demotedHostNewAuthorityId = GONetMain.MyAuthorityId;
            var demotedEvent = new GONet.HostDemotedEvent(
                GONetMain.Time.ElapsedTicks,
                previousHostAuthorityId,
                previousHostOriginalAuthorityId,
                demotedHostNewAuthorityId,
                newHostAuthorityId,
                newHostOriginalAuthorityId,
                newHostEpochSnapshot,
                wasVoluntary: true);
            GONetMain.EventBus.Publish<GONet.IGONetEvent>(demotedEvent);
            GONetMain.BroadcastHostDemoted(
                previousHostAuthorityId,
                previousHostOriginalAuthorityId,
                demotedHostNewAuthorityId,
                newHostAuthorityId,
                newHostOriginalAuthorityId,
                newHostEpochSnapshot,
                wasVoluntary: true);

            var completedEvent = new GONet.HostFailoverCompletedEvent(
                GONetMain.Time.ElapsedTicks,
                newHostAuthorityId,
                newHostOriginalAuthorityId,
                isSelf: false,
                migratedGNPCount: 0);
            GONetMain.EventBus.Publish<GONet.IGONetEvent>(completedEvent);
            GONetMain.BroadcastHostFailoverCompleted(newHostAuthorityId, newHostOriginalAuthorityId, isSelf: false);

            // Switch to using the standby connection to the new host.
            // The new host's authority is in message.NewHostAuthorityId but they've promoted to 1023.
            // We use the original vice host authority ID (targetViceHostId) to look up the standby connection.
            if (targetViceHostId != 0)
            {
                if (GONetMain.MyAuthorityId != 0)
                {
                    GONetHotStandbyManager.Instance.RequestStandbyAuthorityRefresh(targetViceHostId, "voluntary-demotion");
                }
                if (GONetHotStandbyManager.Instance.TryActivateStandbyConnection(targetViceHostId, newHostEpoch))
                {
                    GONetLog.Info($"[Handoff] Activated standby connection to new host (original authority {targetViceHostId})");
                }
                else
                {
                    GONetLog.Warning($"[Handoff] Could not activate standby connection to new host {targetViceHostId} - may need to reconnect");
                }
            }

            if (outgoingHostNewAuthorityId == 0)
            {
                GONetHostFailoverManager.Instance.CleanupLocalTransientsAfterVoluntaryDemotion();
            }
            else
            {
                GONetLog.Info("[Handoff] Skipping local cleanup for lossless voluntary handoff (will fallback on timeout if needed)");
            }
            ResetHandoffState();
        }

        private void DemoteOutgoingHostAuthority()
        {
            ushort restoredAuthorityId = GONetHostFailoverManager.Instance.SelfPromotedFromAuthorityId;
            if (restoredAuthorityId != 0)
            {
                GONetMain.DemoteFromServerAuthority(restoredAuthorityId);
                GONetGossipManager.Instance.UpdateLocalAuthorityId(GONetMain.MyAuthorityId);
            }
            else
            {
                // For an original server, we don't have a "previous" client authority to restore to.
                // Set isServerOverride = false AND clear MyAuthorityId so IsServer returns false.
                // The new host should send us a proper client authority during state sync.
                if (outgoingHostNewAuthorityId != 0)
                {
                    GONetMain.DemoteOriginalServerAfterHandoff(outgoingHostNewAuthorityId);
                    GONetGossipManager.Instance.UpdateLocalAuthorityId(GONetMain.MyAuthorityId);
                }
                else
                {
                    GONetMain.DemoteOriginalServerAfterHandoff();
                }
            }
        }

        private void ResetHandoffState()
        {
            currentState = HandoffState.None;
            targetViceHostId = 0;
            handoffSnapshotTick = 0;
            newHostEpoch = 0;
            sourceHostAuthorityId = 0;
            sourceHostPersistentId = 0;
            outgoingHostNewAuthorityId = 0;
            isOutgoingHost = false;
            isIncomingHost = false;
            didLogPostHandoffIsReady = false;
            postHandoffReadyRecheckPending = false;
            postHandoffReadyRecheckDueRawTicks = 0;
        }

        private void SchedulePostHandoffReadyRecheck()
        {
            postHandoffReadyRecheckPending = true;
            postHandoffReadyRecheckDueRawTicks = GONetMain.Time.RawElapsedTicks +
                (long)(POST_HANDOFF_READY_RECHECK_DELAY_SECONDS * TimeSpan.TicksPerSecond);
        }

        private void ExecutePostHandoffReadyRecheck()
        {
            GONetLog.Info($"[Handoff] Post-handoff readiness recheck ({POST_HANDOFF_READY_RECHECK_DELAY_SECONDS:0.##}s delay)");
            LogServerOwnedIsReadyState("post-handoff-recheck");

            ushort hostAuthorityId = GONetMain.CurrentHostIdentity.HostAuthorityId;
            if (hostAuthorityId != GONetMain.OwnerAuthorityId_Unset)
            {
                GONetMain.TryMapServerAuthorityToHostLocal(hostAuthorityId);
            }

            GONetHostFailoverManager.Instance?.ClearDeserializeInitRequirements_ForServerOwnedObjects();
        }

        private void LogServerOwnedIsReadyState(string context, int maxDetails = 5)
        {
            int readyCount = 0;
            int notReadyCount = 0;
            int loggedDetails = 0;

            foreach (var kvp in GONetMain.gonetParticipantByGONetIdMap)
            {
                GONetParticipant gnp = kvp.Value;
                if (gnp == null) continue;
                if (gnp.OwnerAuthorityId != GONetMain.OwnerAuthorityId_Server) continue;

                if (GONetMain.IsGONetReady(gnp))
                {
                    readyCount++;
                }
                else
                {
                    notReadyCount++;
                    if (loggedDetails < maxDetails)
                    {
                        string reason = GONetMain.GetIsGONetReadyBlockingReason(gnp);
                        GONetLog.Warning($"[Handoff] {context}: GNP NOT READY '{gnp.name}' (GONetId={gnp.GONetId}, OwnerAuth={gnp.OwnerAuthorityId}) reason={reason}");
                        loggedDetails++;
                    }
                }
            }

            if (readyCount + notReadyCount == 0)
            {
                GONetLog.Warning($"[Handoff] {context}: No server-owned GONetParticipants found for readiness check");
                return;
            }

            if (notReadyCount > 0)
            {
                GONetLog.Warning($"[Handoff] {context}: server-owned ready={readyCount}, notReady={notReadyCount}");
            }
        }

        private void ScheduleLosslessCleanup(ushort sourceAuthorityId, ulong sourcePersistentId, ushort outgoingAuthorityId)
        {
            pendingLosslessCleanup = true;
            pendingLosslessCleanupSourceAuthorityId = sourceAuthorityId;
            pendingLosslessCleanupSourcePersistentId = sourcePersistentId;
            pendingLosslessCleanupOutgoingAuthorityId = outgoingAuthorityId;
            // CRITICAL: Use raw ticks for lossless cleanup deadline to avoid time sync discontinuities
            pendingLosslessCleanupDeadlineTicks = GONetMain.Time.RawElapsedTicks +
                (long)(VOLUNTARY_LOSSLESS_CLEANUP_TIMEOUT_SECONDS * TimeSpan.TicksPerSecond);

            GONetLog.Info($"[Handoff] Lossless handoff active - deferring cleanup for {VOLUNTARY_LOSSLESS_CLEANUP_TIMEOUT_SECONDS:0.##}s " +
                          $"(outgoingAuthority={outgoingAuthorityId}, sourceAuthority={sourceAuthorityId})");
        }

        private ushort ResolveOutgoingHostNewAuthorityId()
        {
            ushort restoredAuthorityId = GONetHostFailoverManager.Instance.SelfPromotedFromAuthorityId;
            if (restoredAuthorityId != 0)
            {
                return restoredAuthorityId;
            }

            return GONetMain.ReserveClientAuthorityIdForHandoff();
        }

        public void NotifyOutgoingHostReconnected(ushort authorityId)
        {
            if (!pendingLosslessCleanup) return;
            if (authorityId == 0 || authorityId != pendingLosslessCleanupOutgoingAuthorityId) return;

            pendingLosslessCleanup = false;
            GONetLog.Info($"[Handoff] Outgoing host reconnected (authority {authorityId}) - lossless cleanup canceled");
        }

        private void SendHandoffMessage<T>(T message, bool broadcast = false) where T : class
        {
            GONetLog.Debug($"[Handoff] Sending {typeof(T).Name} (broadcast: {broadcast})");

            // Route to appropriate send method based on message type
            switch (message)
            {
                case HostHandoffPrepareMessage prepareMsg:
                    GONetGossipIntegration.SendHandoffPrepare(prepareMsg, targetViceHostId);
                    break;
                case ViceHostPrepareAckMessage ackMsg:
                    GONetGossipIntegration.SendHandoffPrepareAck(ackMsg);
                    break;
                case HostHandoffDeltaMessage deltaMsg:
                    GONetGossipIntegration.SendHandoffDelta(deltaMsg, targetViceHostId);
                    break;
                case HostHandoffCommitMessage commitMsg:
                    GONetGossipIntegration.SendHandoffCommit(commitMsg);
                    break;
                case NewHostCompleteMessage completeMsg:
                    GONetGossipIntegration.SendNewHostComplete(completeMsg);
                    break;
                case HostHandoffAbortMessage abortMsg:
                    GONetGossipIntegration.SendHandoffAbort(abortMsg);
                    break;
                default:
                    GONetLog.Warning($"[Handoff] Unknown message type: {typeof(T).Name}");
                    break;
            }
        }

        #endregion
    }

    #region Message Types

    /// <summary>
    /// Host announces intent to hand off.
    /// </summary>
    [MemoryPackable]
    public partial class HostHandoffPrepareMessage : ITransientEvent
    {
        public ushort SourceHostAuthorityId { get; set; }
        public ushort TargetViceHostAuthorityId { get; set; }
        public uint NewHostEpoch { get; set; }
        public long SnapshotTick { get; set; }
        public ushort OutgoingHostNewAuthorityId { get; set; }

        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    /// <summary>
    /// Vice host acknowledges prepare.
    /// </summary>
    [MemoryPackable]
    public partial class ViceHostPrepareAckMessage : ITransientEvent
    {
        public ushort ViceHostAuthorityId { get; set; }
        public bool IsReady { get; set; }
        public ulong LastSyncSequence { get; set; }
        public string RejectionReason { get; set; }

        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    /// <summary>
    /// Delta state transfer from host to vice host.
    /// </summary>
    [MemoryPackable]
    public partial class HostHandoffDeltaMessage : ITransientEvent
    {
        public byte[] DeltaData { get; set; }
        public bool IsDelta { get; set; }
        public long SnapshotTick { get; set; }

        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    /// <summary>
    /// Commit message - point of no return.
    /// </summary>
    [MemoryPackable]
    public partial class HostHandoffCommitMessage : ITransientEvent
    {
        public ushort NewHostAuthorityId { get; set; }
        public uint NewHostEpoch { get; set; }
        public long CommitTick { get; set; }
        public ushort OutgoingHostNewAuthorityId { get; set; }

        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    /// <summary>
    /// New host confirms operational.
    /// </summary>
    [MemoryPackable]
    public partial class NewHostCompleteMessage : ITransientEvent
    {
        public ushort NewHostAuthorityId { get; set; }
        public uint NewHostEpoch { get; set; }

        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    /// <summary>
    /// Handoff abort message.
    /// </summary>
    [MemoryPackable]
    public partial class HostHandoffAbortMessage : ITransientEvent
    {
        public string Reason { get; set; }

        [MemoryPackIgnore]
        public long OccurredAtElapsedTicks { get; set; }
    }

    #endregion

    #region Buffered Event

    /// <summary>
    /// An event buffered during handoff.
    /// </summary>
    public struct BufferedEvent
    {
        public IGONetEvent Event;
        public long OccurredAtTicks;
    }

    #endregion
}
