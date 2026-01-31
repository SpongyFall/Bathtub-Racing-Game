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
using UnityEngine;

namespace GONet.DistributedHost
{
    /// <summary>
    /// Debug utilities for manual host migration testing.
    /// Use this to trigger migrations manually before implementing automatic election.
    ///
    /// Hotkeys (when enabled in GONetGlobal):
    /// - Left Alt + H: Display current host status
    /// - Left Alt + M: Trigger manual migration to vice host
    /// - Left Alt + V: Designate next-best client as vice host
    /// </summary>
    public static class GONetHostMigrationDebug
    {
        #region State

        private static bool isInitialized;
        private static float lastStatusDisplayTime;
        private const float STATUS_DISPLAY_COOLDOWN = 1.0f;

        /// <summary>
        /// When true, debug hotkeys are active.
        /// </summary>
        public static bool EnableDebugHotkeys { get; set; } = true;

        /// <summary>
        /// Event fired when a manual migration is triggered.
        /// Subscribers can use this for testing/logging.
        /// </summary>
        public static event Action<ushort> OnManualMigrationTriggered;

        /// <summary>
        /// Event fired when a vice host is manually designated.
        /// </summary>
        public static event Action<ushort> OnViceHostDesignated;

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the debug system. Called from GONetMain.
        /// </summary>
        public static void Initialize()
        {
            if (isInitialized) return;

            isInitialized = true;
            GONetLog.Info("[HostMigrationDebug] Initialized - Hotkeys: Alt+H (status), Alt+M (migrate), Alt+V (designate vice)");
        }

        /// <summary>
        /// Shuts down the debug system.
        /// </summary>
        public static void Shutdown()
        {
            isInitialized = false;
        }

        #endregion

        #region Update Loop

        /// <summary>
        /// Called from GONetMain.Update() to process debug hotkeys.
        /// </summary>
        public static void Update()
        {
            if (!isInitialized || !EnableDebugHotkeys) return;
            if (!GONetGlobal.Instance.enableDistributedHostAuthority) return;

            // Require Left Alt modifier
            if (!Input.GetKey(KeyCode.LeftAlt)) return;

            // Alt+H: Display status
            if (Input.GetKeyDown(KeyCode.H))
            {
                DisplayHostStatus();
            }
            // Alt+M: Trigger migration
            else if (Input.GetKeyDown(KeyCode.M))
            {
                TriggerManualMigration();
            }
            // Alt+V: Designate vice host
            else if (Input.GetKeyDown(KeyCode.V))
            {
                DesignateViceHost();
            }
        }

        #endregion

        #region Debug Commands

        /// <summary>
        /// Displays current host status to the console.
        /// </summary>
        public static void DisplayHostStatus()
        {
            float currentTime = (float)GONetMain.Time.ElapsedSeconds;
            if (currentTime - lastStatusDisplayTime < STATUS_DISPLAY_COOLDOWN) return;
            lastStatusDisplayTime = currentTime;

            var hostIdentity = GONetMain.CurrentHostIdentity;
            var gossipManager = GONetGossipManager.Instance;

            GONetLog.Info("=== Host Migration Status ===");
            GONetLog.Info($"  My Authority ID: {GONetMain.MyAuthorityId}");
            GONetLog.Info($"  Am I Host: {GONetMain.IsServer}");
            GONetLog.Info($"  Host Epoch: {GONetMain.HostEpoch}");
            GONetLog.Info($"  Current Host: Authority {hostIdentity.HostAuthorityId}");
            GONetLog.Info($"  Vice Host: Authority {hostIdentity.ViceHostAuthorityId}");
            GONetLog.Info($"  Gossip Topology: {gossipManager.CurrentTopology}");
            GONetLog.Info($"  Remote Nodes: {gossipManager.RemoteNodeCount}");

            // Display metrics for all known nodes
            GONetLog.Info("--- Node Metrics ---");
            GONetLog.Info($"  [Local] Authority {gossipManager.LocalIdentity.SessionAuthorityId}: " +
                         $"RTT={gossipManager.LocalMetrics.RTT_Average_Ms}ms, " +
                         $"CPU={gossipManager.LocalMetrics.CPU_Headroom_Percent}%, " +
                         $"Uptime={gossipManager.LocalMetrics.Uptime_Minutes}min");

            foreach (var (authorityId, metrics) in gossipManager.GetAllNodeMetrics())
            {
                GONetLog.Info($"  [Remote] Authority {authorityId}: " +
                             $"RTT={metrics.RTT_Average_Ms}ms, " +
                             $"CPU={metrics.CPU_Headroom_Percent}%, " +
                             $"Uptime={metrics.Uptime_Minutes}min");
            }
            GONetLog.Info("=============================");
        }

        /// <summary>
        /// Triggers a manual migration from current host to vice host.
        /// Only works if called on the current host.
        /// </summary>
        public static void TriggerManualMigration()
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[HostMigrationDebug] Cannot trigger migration - not the current host");
                return;
            }

            var hostIdentity = GONetMain.CurrentHostIdentity;
            if (!hostIdentity.HasViceHost)
            {
                GONetLog.Warning("[HostMigrationDebug] Cannot trigger migration - no vice host designated. Use Alt+V first.");
                return;
            }

            ushort viceHostId = hostIdentity.ViceHostAuthorityId;
            GONetLog.Info($"[HostMigrationDebug] MANUAL MIGRATION TRIGGERED: Handing off to authority {viceHostId}");

            OnManualMigrationTriggered?.Invoke(viceHostId);

            // Initiate graceful handoff
            if (!GONetHostHandoffManager.Instance.InitiateGracefulHandoff(viceHostId))
            {
                GONetLog.Error("[HostMigrationDebug] Failed to initiate handoff");
            }
        }

        /// <summary>
        /// Designates the best available client as vice host.
        /// Uses GONetHostScoring for comprehensive evaluation (network, hardware, stability, NAT).
        /// </summary>
        public static void DesignateViceHost()
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[HostMigrationDebug] Cannot designate vice host - not the current host");
                return;
            }

            var gossipManager = GONetGossipManager.Instance;
            if (gossipManager.RemoteNodeCount == 0)
            {
                GONetLog.Warning("[HostMigrationDebug] Cannot designate vice host - no remote nodes");
                return;
            }

            // Build candidate list for scoring
            var candidates = new List<(ushort authorityId, GONetNodeMetrics metrics, GONetNodeCapabilities capabilities, float avgRttMs, bool isStale)>();
            float now = (float)GONetMain.Time.ElapsedSeconds;
            const float STALE_THRESHOLD_SECONDS = 6.0f;

            foreach (var (authorityId, metrics) in gossipManager.GetAllNodeMetrics())
            {
                // Skip self
                if (authorityId == GONetMain.MyAuthorityId)
                    continue;

                // Get identity for capabilities
                if (!gossipManager.TryGetNodeIdentity(authorityId, out var identity))
                    continue;

                // Calculate average RTT for this candidate
                float avgRttMs = gossipManager.GetAverageRTTForCandidate(authorityId, metrics.RTT_Average_Ms);

                // Check staleness
                bool isStale = gossipManager.IsMetricsStale(authorityId, now, STALE_THRESHOLD_SECONDS);

                candidates.Add((authorityId, metrics, identity.Capabilities, avgRttMs, isStale));
            }

            if (candidates.Count == 0)
            {
                GONetLog.Warning("[HostMigrationDebug] No eligible candidates for vice host");
                return;
            }

            // Evaluate using smart scoring system
            ushort bestCandidateId = GONetHostScoring.EvaluateBestViceHost(
                candidates,
                0, // No current vice host
                0, // No current score
                out var bestEvaluation);

            if (bestCandidateId == 0 || !bestEvaluation.IsEligible)
            {
                GONetLog.Warning("[HostMigrationDebug] No eligible candidates for vice host (all disqualified)");
                return;
            }

            GONetLog.Info($"[HostMigrationDebug] Designating authority {bestCandidateId} as vice host\n" +
                         $"  {GONetHostScoring.GetScoreBreakdown(bestEvaluation)}");

            // Update vice host designation without advancing the epoch
            GONetViceHostManager.Instance.SetViceHost(bestCandidateId);

            OnViceHostDesignated?.Invoke(bestCandidateId);
        }

        /// <summary>
        /// Manually designates a specific authority as vice host.
        /// </summary>
        /// <param name="viceHostAuthorityId">Authority ID of the new vice host</param>
        public static void DesignateViceHost(ushort viceHostAuthorityId)
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[HostMigrationDebug] Cannot designate vice host - not the current host");
                return;
            }

            var gossipManager = GONetGossipManager.Instance;
            if (!gossipManager.TryGetNodeMetrics(viceHostAuthorityId, out var metrics))
            {
                GONetLog.Warning($"[HostMigrationDebug] Cannot designate authority {viceHostAuthorityId} - not found in gossip table");
                return;
            }

            if (metrics.Uptime_Seconds < GONetNodeMetrics.MIN_UPTIME_FOR_HOST_SECONDS)
            {
                GONetLog.Warning($"[HostMigrationDebug] Authority {viceHostAuthorityId} is too new (uptime: {metrics.Uptime_Seconds}s, minimum: {GONetNodeMetrics.MIN_UPTIME_FOR_HOST_SECONDS}s)");
                return;
            }

            GONetLog.Info($"[HostMigrationDebug] Designating authority {viceHostAuthorityId} as vice host");

            // Update vice host designation without advancing the epoch
            GONetViceHostManager.Instance.SetViceHost(viceHostAuthorityId);

            OnViceHostDesignated?.Invoke(viceHostAuthorityId);
        }

        #endregion

        #region Testing Utilities

        /// <summary>
        /// Simulates host disconnect for emergency failover testing.
        /// Only works if called on the current host.
        /// WARNING: This will actually disconnect the host - use for testing only!
        /// </summary>
        public static void SimulateHostCrash()
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[HostMigrationDebug] Cannot simulate crash - not the current host");
                return;
            }

            GONetLog.Warning("[HostMigrationDebug] SIMULATING HOST CRASH - stopping server in 100ms");

            // Give a brief window for the warning to be sent
            GONetMain.Global.StartCoroutine(DelayedCrashSimulation());
        }

        private static System.Collections.IEnumerator DelayedCrashSimulation()
        {
            yield return new WaitForSeconds(0.1f);

            // Stop the server abruptly (no graceful handoff)
            if (GONetMain.gonetServer != null)
            {
                GONetMain.gonetServer.Stop();
                GONetLog.Error("[HostMigrationDebug] Server stopped - simulating crash");
            }
        }

        /// <summary>
        /// Gets a summary of the current distributed host state for debugging.
        /// </summary>
        public static DistributedHostDebugInfo GetDebugInfo()
        {
            var gossipManager = GONetGossipManager.Instance;
            var hostIdentity = GONetMain.CurrentHostIdentity;

            var nodeInfos = new List<NodeDebugInfo>();

            // Add local node
            nodeInfos.Add(new NodeDebugInfo
            {
                AuthorityId = gossipManager.LocalIdentity.SessionAuthorityId,
                PersistentId = gossipManager.LocalIdentity.PersistentId,
                IsHost = GONetMain.IsServer,
                IsViceHost = hostIdentity.IsViceHost(gossipManager.LocalIdentity.SessionAuthorityId),
                Metrics = gossipManager.LocalMetrics
            });

            // Add remote nodes
            foreach (var identity in gossipManager.GetAllNodeIdentities())
            {
                if (identity.SessionAuthorityId == gossipManager.LocalIdentity.SessionAuthorityId)
                    continue;

                if (gossipManager.TryGetNodeMetrics(identity.SessionAuthorityId, out var metrics))
                {
                    nodeInfos.Add(new NodeDebugInfo
                    {
                        AuthorityId = identity.SessionAuthorityId,
                        PersistentId = identity.PersistentId,
                        IsHost = hostIdentity.IsHost(identity.SessionAuthorityId),
                        IsViceHost = hostIdentity.IsViceHost(identity.SessionAuthorityId),
                        Metrics = metrics
                    });
                }
            }

            return new DistributedHostDebugInfo
            {
                HostEpoch = GONetMain.HostEpoch,
                HostAuthorityId = hostIdentity.HostAuthorityId,
                ViceHostAuthorityId = hostIdentity.ViceHostAuthorityId,
                Topology = gossipManager.CurrentTopology,
                Nodes = nodeInfos
            };
        }

        #endregion
    }

    #region Debug Info Structures

    /// <summary>
    /// Complete debug info about the distributed host state.
    /// </summary>
    public struct DistributedHostDebugInfo
    {
        public uint HostEpoch;
        public ushort HostAuthorityId;
        public ushort ViceHostAuthorityId;
        public GossipTopology Topology;
        public List<NodeDebugInfo> Nodes;
    }

    /// <summary>
    /// Debug info about a single node.
    /// </summary>
    public struct NodeDebugInfo
    {
        public ushort AuthorityId;
        public ulong PersistentId;
        public bool IsHost;
        public bool IsViceHost;
        public GONetNodeMetrics Metrics;
    }

    #endregion
}
