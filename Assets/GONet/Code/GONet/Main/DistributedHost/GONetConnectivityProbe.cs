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
using System.Net;
using System.Net.Sockets;
using System.Threading;
using GONet.Utils;
using MemoryPack;

namespace GONet.DistributedHost
{
    /// <summary>
    /// Proactively verifies connectivity to other nodes' dormant servers.
    /// This ensures we know ahead of time which nodes can actually accept connections
    /// and serve as host during failover.
    ///
    /// Each node periodically probes random peers to verify:
    /// - Their dormant server is listening
    /// - Network path is clear (no firewall/NAT blocking)
    /// - They can actually accept incoming connections
    ///
    /// Results are shared via gossip so all nodes know who is reachable.
    /// </summary>
    public class GONetConnectivityProbeManager
    {
        #region Constants

        /// <summary>
        /// How often to probe a peer (seconds between probes to any single peer).
        /// </summary>
        public const float PROBE_INTERVAL_SECONDS = 1.0f;

        /// <summary>
        /// Timeout for a single probe attempt (milliseconds).
        /// </summary>
        public const int PROBE_TIMEOUT_MS = 500;

        /// <summary>
        /// How many failed probes before marking a node as unreachable.
        /// </summary>
        public const int FAILURE_THRESHOLD = 3;

        /// <summary>
        /// How many successful probes needed to mark a node as reachable again.
        /// </summary>
        public const int SUCCESS_THRESHOLD = 2;

        /// <summary>
        /// Maximum number of ports to try when finding an available dormant server port.
        /// </summary>
        public const int MAX_PORT_ATTEMPTS = 100;

        /// <summary>
        /// Magic bytes for probe request (4 bytes: "GNCP" = GONet Connectivity Probe).
        /// </summary>
        private static readonly byte[] PROBE_MAGIC = { 0x47, 0x4E, 0x43, 0x50 };

        /// <summary>
        /// Magic bytes for probe response.
        /// </summary>
        private static readonly byte[] PROBE_ACK_MAGIC = { 0x47, 0x4E, 0x43, 0x41 }; // "GNCA"

        #endregion

        #region Singleton

        private static GONetConnectivityProbeManager instance;
        public static GONetConnectivityProbeManager Instance => instance ??= new GONetConnectivityProbeManager();

        private GONetConnectivityProbeManager() { }

        #endregion

        #region State

        private bool isInitialized;
        private float lastProbeTime;
        private int currentProbeIndex;
        private List<ushort> probeTargets = new List<ushort>();

        /// <summary>
        /// Tracks consecutive failures per node.
        /// </summary>
        private readonly Dictionary<ushort, int> failureCount = new Dictionary<ushort, int>();

        /// <summary>
        /// Tracks consecutive successes per node (for recovery).
        /// </summary>
        private readonly Dictionary<ushort, int> successCount = new Dictionary<ushort, int>();

        /// <summary>
        /// Last known reachability status per node.
        /// </summary>
        private readonly Dictionary<ushort, bool> reachabilityStatus = new Dictionary<ushort, bool>();

        /// <summary>
        /// Last probe time per node.
        /// </summary>
        private readonly Dictionary<ushort, float> lastProbeTimePerNode = new Dictionary<ushort, float>();

        /// <summary>
        /// The local dormant server listener (if running).
        /// </summary>
        private TcpListener dormantListener;
        private Thread listenerThread;
        private volatile bool listenerRunning;
        private ushort dormantServerPort;

        /// <summary>
        /// Queue for callbacks that must run on main thread.
        /// </summary>
        private readonly Queue<Action> mainThreadCallbacks = new Queue<Action>();
        private readonly object mainThreadCallbackLock = new object();

        #endregion

        #region Events

        /// <summary>
        /// Fired when a node's reachability status changes.
        /// Parameters: (authorityId, isReachable)
        /// </summary>
        public event Action<ushort, bool> OnReachabilityChanged;

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the connectivity probe system.
        /// </summary>
        /// <param name="startingPort">Starting port to try for the dormant server (will increment if occupied)</param>
        public void Initialize(ushort startingPort)
        {
            if (isInitialized) return;

            lastProbeTime = (float)GONetMain.Time.ElapsedSeconds;

            // Start the dormant server listener (finds available port starting from startingPort)
            if (!StartDormantServer(startingPort))
            {
                GONetLog.Error($"[ConnectivityProbe] Failed to start dormant server after {MAX_PORT_ATTEMPTS} attempts starting from port {startingPort}");
                // Still mark as initialized but without a working dormant server
                // The node can still probe others, just can't be probed itself
                isInitialized = true;
                return;
            }

            // Set our local endpoint in gossip (dual-stack: both IPv4 and IPv6)
            var endpoint = NetworkUtils.CreateLocalDualStackEndpoint(dormantServerPort);
            GONetGossipManager.Instance.SetLocalEndpoint(endpoint);

            isInitialized = true;
            GONetLog.Info($"[ConnectivityProbe] Initialized with dormant server on port {dormantServerPort} - {endpoint}");
        }

        /// <summary>
        /// Shuts down the connectivity probe system.
        /// </summary>
        public void Shutdown()
        {
            if (!isInitialized) return;

            StopDormantServer();

            failureCount.Clear();
            successCount.Clear();
            reachabilityStatus.Clear();
            lastProbeTimePerNode.Clear();
            probeTargets.Clear();

            isInitialized = false;
            GONetLog.Info("[ConnectivityProbe] Shutdown");
        }

        #endregion

        #region Dormant Server

        /// <summary>
        /// Starts the dormant server listener.
        /// This listens for connectivity probes from other nodes.
        /// Uses dual-stack (IPv6Any) to accept both IPv4 and IPv6 connections.
        /// Tries successive ports starting from startingPort until one is available.
        /// </summary>
        /// <param name="startingPort">First port to try</param>
        /// <returns>True if successfully started on some port, false if all attempts failed</returns>
        private bool StartDormantServer(ushort startingPort)
        {
            for (int attempt = 0; attempt < MAX_PORT_ATTEMPTS; attempt++)
            {
                ushort portToTry = (ushort)(startingPort + attempt);

                // Skip if port would overflow
                if (portToTry < startingPort && attempt > 0)
                {
                    GONetLog.Warning($"[ConnectivityProbe] Port overflow, stopping search at port {portToTry}");
                    break;
                }

                if (TryStartOnPort(portToTry))
                {
                    dormantServerPort = portToTry;
                    return true;
                }
            }

            MarkEndpointCannotAccept();
            return false;
        }

        /// <summary>
        /// Attempts to start the dormant server on a specific port.
        /// </summary>
        /// <param name="port">Port to try</param>
        /// <returns>True if successful, false if port is unavailable</returns>
        private bool TryStartOnPort(ushort port)
        {
            // Try dual-stack (IPv6Any with DualMode) first
            try
            {
                dormantListener = new TcpListener(IPAddress.IPv6Any, port);
                dormantListener.Server.DualMode = true;
                dormantListener.Start();

                listenerRunning = true;
                listenerThread = new Thread(DormantServerLoop)
                {
                    IsBackground = true,
                    Name = "GONet_DormantServer"
                };
                listenerThread.Start();

                GONetLog.Info($"[ConnectivityProbe] Dormant server started on port {port} (dual-stack IPv4/IPv6)");
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // Port in use, will try next port
                return false;
            }
            catch (SocketException)
            {
                // Dual-stack not supported, fall back to IPv4 only
            }
            catch (Exception)
            {
                // Other error with dual-stack, try IPv4
            }

            // Try IPv4 only
            try
            {
                dormantListener = new TcpListener(IPAddress.Any, port);
                dormantListener.Start();

                listenerRunning = true;
                listenerThread = new Thread(DormantServerLoop)
                {
                    IsBackground = true,
                    Name = "GONet_DormantServer"
                };
                listenerThread.Start();

                GONetLog.Info($"[ConnectivityProbe] Dormant server started on port {port} (IPv4 only)");
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // Port in use, will try next port
                return false;
            }
            catch (Exception ex)
            {
                GONetLog.Debug($"[ConnectivityProbe] Failed to bind port {port}: {ex.Message}");
                return false;
            }
        }

        private void MarkEndpointCannotAccept()
        {
            // Update our endpoint to indicate we can't accept connections
            var endpoint = NetworkUtils.CreateLocalDualStackEndpoint(dormantServerPort);
            endpoint.Flags &= ~ConnectionEndpointFlags.CanAcceptConnections;
            GONetGossipManager.Instance.SetLocalEndpoint(endpoint);
        }

        /// <summary>
        /// Stops the dormant server listener.
        /// </summary>
        private void StopDormantServer()
        {
            listenerRunning = false;

            try
            {
                dormantListener?.Stop();
            }
            catch { }

            try
            {
                listenerThread?.Join(1000);
            }
            catch { }

            dormantListener = null;
            listenerThread = null;
        }

        /// <summary>
        /// Background thread loop for the dormant server.
        /// Accepts probe connections and responds with ack.
        /// </summary>
        private void DormantServerLoop()
        {
            byte[] buffer = new byte[16];

            while (listenerRunning)
            {
                try
                {
                    // Check for pending connections with timeout
                    if (!dormantListener.Pending())
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    using (var client = dormantListener.AcceptTcpClient())
                    {
                        client.ReceiveTimeout = PROBE_TIMEOUT_MS;
                        client.SendTimeout = PROBE_TIMEOUT_MS;

                        using (var stream = client.GetStream())
                        {
                            // Read probe magic
                            int bytesRead = stream.Read(buffer, 0, PROBE_MAGIC.Length);
                            if (bytesRead == PROBE_MAGIC.Length && IsProbeRequest(buffer))
                            {
                                // Send ack
                                stream.Write(PROBE_ACK_MAGIC, 0, PROBE_ACK_MAGIC.Length);
                            }
                        }
                    }
                }
                catch (SocketException)
                {
                    // Expected during shutdown or timeout
                }
                catch (Exception ex)
                {
                    if (listenerRunning)
                    {
                        GONetLog.Debug($"[ConnectivityProbe] Dormant server error: {ex.Message}");
                    }
                }
            }
        }

        private bool IsProbeRequest(byte[] buffer)
        {
            for (int i = 0; i < PROBE_MAGIC.Length; i++)
            {
                if (buffer[i] != PROBE_MAGIC[i]) return false;
            }
            return true;
        }

        #endregion

        #region Update Loop

        /// <summary>
        /// Called from GONetMain.Update() to perform periodic probes.
        /// </summary>
        public void Update(float elapsedSeconds)
        {
            if (!isInitialized) return;
            if (!GONetGlobal.Instance.enableDistributedHostAuthority) return;

            // Process any queued callbacks from background probe threads
            ProcessMainThreadCallbacks();

            // Time to probe?
            if (elapsedSeconds - lastProbeTime < PROBE_INTERVAL_SECONDS)
            {
                return;
            }
            lastProbeTime = elapsedSeconds;

            // Refresh target list
            RefreshProbeTargets();

            if (probeTargets.Count == 0) return;

            // Pick next target (round-robin)
            currentProbeIndex = (currentProbeIndex + 1) % probeTargets.Count;
            ushort targetId = probeTargets[currentProbeIndex];

            // Don't probe too frequently
            if (lastProbeTimePerNode.TryGetValue(targetId, out float lastTime))
            {
                if (elapsedSeconds - lastTime < PROBE_INTERVAL_SECONDS * probeTargets.Count * 0.5f)
                {
                    return; // Skip, probed recently
                }
            }

            // Probe async (don't block main thread)
            ThreadPool.QueueUserWorkItem(_ => ProbeNode(targetId));
            lastProbeTimePerNode[targetId] = elapsedSeconds;
        }

        /// <summary>
        /// Processes callbacks queued from background probe threads.
        /// </summary>
        private void ProcessMainThreadCallbacks()
        {
            while (true)
            {
                Action callback = null;
                lock (mainThreadCallbackLock)
                {
                    if (mainThreadCallbacks.Count > 0)
                    {
                        callback = mainThreadCallbacks.Dequeue();
                    }
                }

                if (callback == null) break;

                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    GONetLog.Error($"[ConnectivityProbe] Exception in main thread callback: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Refreshes the list of nodes to probe.
        /// </summary>
        private void RefreshProbeTargets()
        {
            probeTargets.Clear();

            foreach (var (authorityId, endpoint) in GONetGossipManager.Instance.GetAllNodeEndpoints())
            {
                // Skip self
                if (authorityId == GONetGossipManager.Instance.LocalIdentity.SessionAuthorityId)
                    continue;

                // Only probe nodes that claim to have endpoints
                if (endpoint.HasIPv4 || endpoint.HasTransportId)
                {
                    probeTargets.Add(authorityId);
                }
            }
        }

        #endregion

        #region Probing

        /// <summary>
        /// Probes a specific node's dormant server.
        /// Called on background thread.
        /// </summary>
        private void ProbeNode(ushort targetAuthorityId)
        {
            if (!GONetGossipManager.Instance.TryGetNodeEndpoint(targetAuthorityId, out var endpoint))
            {
                return;
            }

            bool success = false;

            try
            {
                // Try IPv6 first (if available), then IPv4
                if (endpoint.HasIPv6)
                {
                    success = ProbeIPv6(endpoint.GetIPv6Address(), endpoint.Port);
                }

                // If IPv6 failed or not available, try IPv4
                if (!success && endpoint.HasIPv4)
                {
                    success = ProbeIPv4(endpoint.IPv4Address, endpoint.Port);
                }

                // TODO: Add transport-specific probing (Steam, etc.)
            }
            catch (Exception ex)
            {
                GONetLog.Debug($"[ConnectivityProbe] Probe to {targetAuthorityId} failed: {ex.Message}");
            }

            // Update status on main thread - queue for processing in GONetMain.Update
            lock (mainThreadCallbackLock)
            {
                mainThreadCallbacks.Enqueue(() => UpdateProbeResult(targetAuthorityId, success));
            }
        }

        /// <summary>
        /// Attempts to connect to an IPv4 endpoint and exchange probe magic.
        /// </summary>
        private bool ProbeIPv4(uint ipv4, ushort port)
        {
            string ipString = $"{(ipv4 >> 24) & 0xFF}.{(ipv4 >> 16) & 0xFF}.{(ipv4 >> 8) & 0xFF}.{ipv4 & 0xFF}";
            return ProbeAddress(ipString, port);
        }

        /// <summary>
        /// Attempts to connect to an IPv6 endpoint and exchange probe magic.
        /// </summary>
        private bool ProbeIPv6(IPAddress ipv6, ushort port)
        {
            return ProbeAddress(ipv6.ToString(), port);
        }

        /// <summary>
        /// Core probe logic - connects to address and exchanges magic bytes.
        /// </summary>
        private bool ProbeAddress(string address, ushort port)
        {
            using (var client = new TcpClient())
            {
                // Connect with timeout
                var connectTask = client.ConnectAsync(address, port);
                if (!connectTask.Wait(PROBE_TIMEOUT_MS))
                {
                    return false; // Timeout
                }

                if (!client.Connected)
                {
                    return false;
                }

                client.ReceiveTimeout = PROBE_TIMEOUT_MS;
                client.SendTimeout = PROBE_TIMEOUT_MS;

                using (var stream = client.GetStream())
                {
                    // Send probe
                    stream.Write(PROBE_MAGIC, 0, PROBE_MAGIC.Length);

                    // Read ack
                    byte[] buffer = new byte[PROBE_ACK_MAGIC.Length];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);

                    if (bytesRead != PROBE_ACK_MAGIC.Length)
                        return false;

                    for (int i = 0; i < PROBE_ACK_MAGIC.Length; i++)
                    {
                        if (buffer[i] != PROBE_ACK_MAGIC[i])
                            return false;
                    }

                    return true; // Success!
                }
            }
        }

        /// <summary>
        /// Updates probe result and triggers events if status changed.
        /// Must be called on main thread.
        /// </summary>
        private void UpdateProbeResult(ushort authorityId, bool success)
        {
            bool wasReachable = reachabilityStatus.TryGetValue(authorityId, out bool prev) && prev;

            if (success)
            {
                // Reset failure count, increment success
                failureCount[authorityId] = 0;
                successCount.TryGetValue(authorityId, out int sc);
                successCount[authorityId] = sc + 1;

                // Mark reachable after threshold successes (or immediately if first time)
                if (!wasReachable && (successCount[authorityId] >= SUCCESS_THRESHOLD || !reachabilityStatus.ContainsKey(authorityId)))
                {
                    reachabilityStatus[authorityId] = true;
                    GONetLog.Info($"[ConnectivityProbe] Node {authorityId} is now REACHABLE");
                    OnReachabilityChanged?.Invoke(authorityId, true);
                }
            }
            else
            {
                // Reset success count, increment failure
                successCount[authorityId] = 0;
                failureCount.TryGetValue(authorityId, out int fc);
                failureCount[authorityId] = fc + 1;

                // Mark unreachable after threshold failures
                if (wasReachable && failureCount[authorityId] >= FAILURE_THRESHOLD)
                {
                    reachabilityStatus[authorityId] = false;
                    GONetLog.Warning($"[ConnectivityProbe] Node {authorityId} is now UNREACHABLE");
                    OnReachabilityChanged?.Invoke(authorityId, false);
                }
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Returns true if the specified node has been verified as reachable.
        /// </summary>
        public bool IsNodeReachable(ushort authorityId)
        {
            return reachabilityStatus.TryGetValue(authorityId, out bool reachable) && reachable;
        }

        /// <summary>
        /// Gets all nodes that have been verified as reachable.
        /// </summary>
        public IEnumerable<ushort> GetReachableNodes()
        {
            foreach (var kvp in reachabilityStatus)
            {
                if (kvp.Value)
                    yield return kvp.Key;
            }
        }

        /// <summary>
        /// Gets the dormant server port for this node.
        /// </summary>
        public ushort DormantServerPort => dormantServerPort;

        /// <summary>
        /// Returns true if the dormant server is running.
        /// </summary>
        public bool IsDormantServerRunning => listenerRunning;

        /// <summary>
        /// Promotes the dormant server to active server mode.
        /// Called when this node becomes the host.
        /// </summary>
        public void PromoteDormantServer()
        {
            if (!listenerRunning)
            {
                GONetLog.Error("[ConnectivityProbe] Cannot promote - dormant server not running");
                return;
            }

            GONetLog.Info($"[ConnectivityProbe] Promoting dormant server on port {dormantServerPort} to active");

            // The actual server promotion happens in GONetMain/transport layer
            // This just signals that we're ready
            // TODO: Wire to actual server promotion
        }

        #endregion
    }
}
