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

namespace GONet
{
    /// <summary>
    /// Central configuration class for GONet runtime behavior.
    /// These settings can be modified at runtime before GONet initialization or during gameplay.
    /// </summary>
    public static class GONetConfig
    {
        #region RPC Lifecycle Configuration

        /// <summary>
        /// Timeout in seconds for deferred RPCs waiting for participant registration.
        /// RPCs targeting unknown participants will be queued and retried until this timeout expires.
        /// Default: 5.0 seconds
        /// </summary>
        public static float RpcDeferralTimeoutSeconds = 5.0f;

        /// <summary>
        /// Maximum RPCs to queue per unknown participant.
        /// Prevents memory issues if a participant never appears.
        /// When this limit is reached, oldest RPCs are dropped with a warning.
        /// Default: 100
        /// </summary>
        public static int MaxDeferredRpcsPerParticipant = 100;

        /// <summary>
        /// If true, invalid RPC calls (GONetId=0, participant not in lookup, etc.) throw exceptions.
        /// If false, they log errors and return silently without sending the RPC.
        /// Recommended: true during development to catch issues early, false in production.
        /// Default: false (errors only)
        /// </summary>
        public static bool ThrowOnInvalidRpc = false;

        /// <summary>
        /// If true, enables pre-send RPC validation that checks:
        /// - GONetParticipant is not null
        /// - GONetId is assigned (non-zero)
        /// - Participant is registered in the lookup map
        /// This prevents sending RPCs with invalid identifiers.
        /// Default: true
        /// </summary>
        public static bool EnableRpcPreSendValidation = true;

        /// <summary>
        /// If true, RPC handlers will defer RPCs for unknown participants (participant not in lookup map).
        /// If false, RPCs for unknown participants are logged and dropped immediately.
        /// Default: true
        /// </summary>
        public static bool EnableRpcDeferralForUnknownParticipants = true;

        #endregion

        #region OnGONetReady Lifecycle Configuration

        /// <summary>
        /// If true, OnGONetReady precondition violations (GONetId=0, not in lookup, etc.) throw exceptions.
        /// Only active in Editor and Development builds (uses Conditional attribute).
        /// Default: false (warnings only)
        /// </summary>
        public static bool ThrowOnGONetReadyViolations = false;

        /// <summary>
        /// If true, enables runtime validation of OnGONetReady preconditions:
        /// - GONetParticipant is not null
        /// - GONetId is assigned (non-zero)
        /// - Participant is registered in GONetMain lookup
        /// This helps catch lifecycle issues during development.
        /// Default: true in Editor/Development builds, false in release builds
        /// </summary>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public static bool EnableOnGONetReadyValidation = true;
#else
        public static bool EnableOnGONetReadyValidation = false;
#endif

        #endregion

        #region Reparenting Configuration

        /// <summary>
        /// Timeout in seconds for pending reparent events waiting for participant or parent spawn.
        /// Events that exceed this timeout are evicted with a warning.
        /// Default: 30.0 seconds
        /// </summary>
        public static float PendingReparentTimeoutSeconds = 30.0f;

        /// <summary>
        /// Maximum reparent rate per second per authority.
        /// Prevents malicious or buggy clients from spamming reparent events.
        /// Set to 0 to disable rate limiting.
        /// Default: 10 reparents per second per authority
        /// </summary>
        public static int MaxReparentsPerSecondPerAuthority = 10;

        /// <summary>
        /// If true, enables automatic transform sync suspension for nested GONetParticipants.
        /// When a GNP is parented under another GNP that has IsPositionSyncd/IsRotationSyncd enabled,
        /// the child's transform sync is suspended to prevent hierarchy ordering desync.
        /// Default: true
        /// </summary>
        public static bool EnableTransformSyncSuspensionForNestedGNPs = true;

        /// <summary>
        /// If true, automatically set Rigidbody to kinematic on non-authority when transform sync is suspended.
        /// This prevents physics simulation from fighting with the suspended sync state.
        /// The original kinematic state is restored when sync resumes.
        /// Default: true
        /// </summary>
        public static bool AutoKinematicOnTransformSyncSuspension = true;

        /// <summary>
        /// Number of frames to wait before auto-publishing a pending reparent event.
        /// Allows gameplay code to call FinalizeReparentOffset() before auto-publish.
        /// Default: 1 (end of current frame via WaitForEndOfFrame)
        /// </summary>
        public static int ReparentAutoPublishDelayFrames = 1;

        /// <summary>
        /// If true, enables the reparent position guard that automatically corrects local position/rotation drift
        /// for reparented children on non-authority machines. This provides a safety net against any sync/blending
        /// paths that might overwrite the intended local offset.
        ///
        /// IMPORTANT: Set this to false if you need to manually sync or animate the child's local position/rotation
        /// while parented. In that case, you are responsible for syncing the child's local transform yourself using
        /// a custom [GONetAutoMagicalSync] attribute on your own local position/rotation property.
        ///
        /// Per-instance opt-out: Call GONetParticipant.DisableReparentPositionGuard() on specific objects
        /// that need manual local transform control while remaining parented.
        ///
        /// Default: true
        /// </summary>
        public static bool EnableReparentPositionGuard = true;

        #endregion

        #region Logging Configuration

        /// <summary>
        /// If true, logs detailed diagnostic information about RPC deferral operations.
        /// Useful for debugging RPC timing issues.
        /// Default: false
        /// </summary>
        public static bool LogRpcDeferralDiagnostics = false;

        /// <summary>
        /// If true, logs detailed diagnostic information about reparenting operations.
        /// Useful for debugging reparenting issues.
        /// Default: false
        /// </summary>
        public static bool LogReparentDiagnostics = false;

        /// <summary>
        /// If true, logs detailed diagnostic information about spawn/instantiation operations.
        /// Useful for debugging spawn timing and GONetId assignment issues.
        /// Default: false
        /// </summary>
        public static bool LogSpawnDiagnostics = false;

        /// <summary>
        /// If true, logs diagnostic information about participant lookup recovery and map removal.
        /// Useful for diagnosing missing GONetId lookup entries and re-enable recovery.
        /// Default: false
        /// </summary>
        public static bool LogParticipantMapDiagnostics = false;

        /// <summary>
        /// If true, logs detailed diagnostic information about sync transmission operations.
        /// Useful for debugging network sync issues on the server.
        /// Default: false
        /// </summary>
        public static bool LogSyncDiagnostics = false;

        /// <summary>
        /// If true, logs diagnostic information about client connection state transitions.
        /// Useful for debugging client connection issues.
        /// Default: false
        /// </summary>
        public static bool LogClientConnectionDiagnostics = false;

        /// <summary>
        /// If true, logs detailed diagnostic information about animator trigger sync operations.
        /// Useful for debugging trigger sync issues between authority and non-authority machines.
        /// Default: false
        /// </summary>
        public static bool LogAnimatorTriggerDiagnostics = false;

        #endregion

        #region Participant Lookup Recovery Configuration

        /// <summary>
        /// If true, attempts to recover missing participants on lookup by scanning scene objects.
        /// This is a safety net for cases where a participant was removed from lookup due to
        /// temporary disable/re-enable or hierarchy changes.
        ///
        /// <para><b>When Recovery Triggers:</b></para>
        /// <para>
        /// Recovery typically indicates a lifecycle issue - GONetParticipants should remain in
        /// active hierarchies throughout their networked lifetime. If you see [LOOKUP-RECOVERY]
        /// messages frequently, check if any GameObjects containing GONetParticipants are being
        /// disabled. The correct pattern is to disable components (Camera, Renderer, etc.) rather
        /// than entire GameObjects. See <see cref="GONetParticipant"/> documentation for details.
        /// </para>
        ///
        /// <para><b>Performance:</b></para>
        /// <para>
        /// Recovery uses Resources.FindObjectsOfTypeAll which is expensive, but is rate-limited
        /// to 1 attempt per GONetId per second to prevent performance degradation.
        /// </para>
        ///
        /// Default: true
        /// </summary>
        public static bool EnableParticipantLookupRecovery = true;

        #endregion

        #region Despawn Tombstone Configuration

        /// <summary>
        /// Time-to-live in minutes for despawn tombstones. Tombstones prevent late-arriving sync bundles
        /// and spawn events for objects that have already been despawned.
        ///
        /// For late-joiner scenarios where clients may connect long after objects have been spawned and
        /// despawned, consider increasing this value to prevent "GONetId not found" errors.
        ///
        /// Performance note: Higher values use more memory but provide better protection against
        /// cross-channel message ordering issues. Each tombstone uses ~16 bytes.
        ///
        /// Default: 5 minutes (sufficient for most real-time games)
        /// Recommended for games with long lobbies or frequent late-joiners: 10-15 minutes
        /// </summary>
        public static float DespawnTombstoneTTLMinutes = 5.0f;

        /// <summary>
        /// Maximum number of despawn tombstones to keep in memory. When this limit is reached,
        /// oldest tombstones are pruned even if they haven't expired.
        ///
        /// Each tombstone uses ~16 bytes, so 4096 entries = ~64KB max memory.
        ///
        /// Default: 4096
        /// </summary>
        public static int DespawnTombstoneMaxEntries = 4096;

        /// <summary>
        /// Interval in seconds for pruning expired tombstones. Lower values reduce peak memory
        /// usage but increase CPU overhead.
        ///
        /// Default: 30 seconds
        /// </summary>
        public static float DespawnTombstonePruneIntervalSeconds = 30.0f;

        #endregion

        #region Log File Cleanup Configuration

        /// <summary>
        /// Maximum age in days for log files before automatic cleanup.
        /// Applies to all GONet log files (.log) in the logs directory.
        /// Set to 0 to disable automatic log cleanup (not recommended for long-running games).
        /// Default: 5 days
        /// </summary>
        public static int MaxLogFileAgeDays = 5;

        /// <summary>
        /// Maximum age in days for event history export files (.txt) before automatic cleanup.
        /// These are the gonet-events-*.txt files created on application quit.
        /// Set to 0 to disable automatic cleanup (not recommended).
        /// Default: 5 days
        /// </summary>
        public static int MaxEventHistoryFileAgeDays = 5;

        /// <summary>
        /// Maximum number of event history files to keep, regardless of age.
        /// Older files are deleted first when this limit is exceeded.
        /// Set to 0 to disable count-based cleanup (only age-based cleanup applies).
        /// Default: 20 files
        /// </summary>
        public static int MaxEventHistoryFileCount = 20;

        /// <summary>
        /// Maximum total size in megabytes for all log files before cleanup.
        /// When total log size exceeds this, oldest files are deleted until under limit.
        /// Set to 0 to disable size-based cleanup (only age-based cleanup applies).
        /// Default: 100 MB
        /// </summary>
        public static int MaxLogDirectorySizeMB = 100;

        /// <summary>
        /// If true, logs cleanup operations (files deleted, space reclaimed).
        /// Useful for debugging cleanup behavior.
        /// Default: false
        /// </summary>
        public static bool LogCleanupOperations = false;

        #endregion

        #region Events

        /// <summary>
        /// Event fired when an RPC times out waiting for its target participant.
        /// Parameters: (uint gonetId, uint rpcId, float waitedSeconds)
        /// </summary>
        public static event Action<uint, uint, float> OnRpcDeferralTimeout;

        /// <summary>
        /// Event fired when a pending reparent times out waiting for object or parent.
        /// Parameters: (uint objectGONetId, uint parentGONetId, float waitedSeconds)
        /// </summary>
        public static event Action<uint, uint, float> OnReparentTimeout;

        /// <summary>
        /// Event fired when an RPC fails pre-send validation.
        /// Parameters: (string methodName, uint gonetId, string reason)
        /// </summary>
        public static event Action<string, uint, string> OnRpcValidationFailed;

        internal static void RaiseRpcDeferralTimeout(uint gonetId, uint rpcId, float waitedSeconds)
        {
            OnRpcDeferralTimeout?.Invoke(gonetId, rpcId, waitedSeconds);
        }

        internal static void RaiseReparentTimeout(uint objectGONetId, uint parentGONetId, float waitedSeconds)
        {
            OnReparentTimeout?.Invoke(objectGONetId, parentGONetId, waitedSeconds);
        }

        internal static void RaiseRpcValidationFailed(string methodName, uint gonetId, string reason)
        {
            OnRpcValidationFailed?.Invoke(methodName, gonetId, reason);
        }

        #endregion
    }
}
