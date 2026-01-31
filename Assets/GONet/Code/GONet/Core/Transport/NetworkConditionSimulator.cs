/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 *
 * Authorized use is explicitly limited to the following:
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on original sources in binary form only (compiled code)
 * -The ability to modify source code for local use only
 * -The ability to distribute products based on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on modified sources in binary form only (compiled code)
 */

using System;
using System.Collections.Generic;

namespace GONet.Transport
{
    /// <summary>
    /// Configuration for network condition simulation.
    /// All values only take effect when <see cref="IsEnabled"/> is true.
    /// </summary>
    [Serializable]
    public class NetworkConditionConfig
    {
        /// <summary>
        /// Enable/disable network condition simulation.
        /// When disabled, all packets pass through immediately with no modification.
        /// </summary>
        public bool IsEnabled = false;

        /// <summary>
        /// One-way latency in milliseconds (applied to both send and receive).
        /// Total round-trip latency will be approximately 2x this value.
        /// Range: 0-2000ms. Default: 0.
        /// </summary>
        public int LatencyMs = 0;

        /// <summary>
        /// Random latency variance in milliseconds (±).
        /// Actual latency per packet = LatencyMs + Random(-JitterMs, +JitterMs).
        /// Range: 0-500ms. Default: 0.
        /// </summary>
        public int JitterMs = 0;

        /// <summary>
        /// Packet loss percentage (0-100).
        /// Each packet has this % chance of being dropped entirely.
        /// Range: 0-100%. Default: 0.
        /// </summary>
        public float PacketLossPercent = 0f;

        /// <summary>
        /// Packet duplication percentage (0-100).
        /// Each packet has this % chance of being sent twice.
        /// Useful for testing idempotent message handling.
        /// Range: 0-100%. Default: 0.
        /// </summary>
        public float DuplicatePercent = 0f;

        /// <summary>
        /// Validate and clamp all values to safe ranges.
        /// </summary>
        public void Validate()
        {
            LatencyMs = Math.Max(0, Math.Min(2000, LatencyMs));
            JitterMs = Math.Max(0, Math.Min(500, JitterMs));
            PacketLossPercent = Math.Max(0f, Math.Min(100f, PacketLossPercent));
            DuplicatePercent = Math.Max(0f, Math.Min(100f, DuplicatePercent));
        }

        /// <summary>
        /// Create a config preset for LAN conditions (minimal latency).
        /// </summary>
        public static NetworkConditionConfig LAN => new NetworkConditionConfig
        {
            IsEnabled = true,
            LatencyMs = 2,
            JitterMs = 1,
            PacketLossPercent = 0f,
            DuplicatePercent = 0f
        };

        /// <summary>
        /// Create a config preset for good WiFi conditions.
        /// </summary>
        public static NetworkConditionConfig GoodWiFi => new NetworkConditionConfig
        {
            IsEnabled = true,
            LatencyMs = 15,
            JitterMs = 5,
            PacketLossPercent = 0.5f,
            DuplicatePercent = 0f
        };

        /// <summary>
        /// Create a config preset for typical internet conditions.
        /// </summary>
        public static NetworkConditionConfig Internet => new NetworkConditionConfig
        {
            IsEnabled = true,
            LatencyMs = 50,
            JitterMs = 15,
            PacketLossPercent = 1f,
            DuplicatePercent = 0f
        };

        /// <summary>
        /// Create a config preset for poor WiFi/mobile conditions.
        /// </summary>
        public static NetworkConditionConfig PoorWiFi => new NetworkConditionConfig
        {
            IsEnabled = true,
            LatencyMs = 100,
            JitterMs = 50,
            PacketLossPercent = 5f,
            DuplicatePercent = 0.5f
        };

        /// <summary>
        /// Create a config preset for bad/congested network conditions.
        /// </summary>
        public static NetworkConditionConfig BadNetwork => new NetworkConditionConfig
        {
            IsEnabled = true,
            LatencyMs = 200,
            JitterMs = 100,
            PacketLossPercent = 10f,
            DuplicatePercent = 1f
        };
    }

    /// <summary>
    /// Simulates network conditions (latency, jitter, packet loss) for testing.
    ///
    /// <para>
    /// USAGE: Wrap any IGONetTransport with this simulator to add artificial network conditions.
    /// The simulator intercepts Send() calls and delays/drops packets according to configuration.
    /// </para>
    ///
    /// <para>
    /// THREADING: This class is designed to be called from the main thread only.
    /// The delay queue is processed during Update() calls.
    /// </para>
    ///
    /// <para>
    /// DESIGN: Unlike external tools like Clumsy that buffer packets at the driver level,
    /// this simulator processes each packet independently with its own delay, providing
    /// accurate per-packet latency simulation that works correctly with high-frequency traffic.
    /// </para>
    /// </summary>
    public class NetworkConditionSimulator : IGONetTransport
    {
        #region Fields

        private readonly IGONetTransport innerTransport;
        private NetworkConditionConfig config;
        private readonly Random random = new Random();

        // Outgoing packet delay queue (send-side latency simulation)
        private readonly List<DelayedPacket> sendQueue = new List<DelayedPacket>();

        // Incoming packet delay queue (receive-side latency simulation)
        private readonly List<DelayedPacket> receiveQueue = new List<DelayedPacket>();

        // Reusable list for processing (avoids allocation during Update)
        private readonly List<DelayedPacket> readyPackets = new List<DelayedPacket>();

        // High-resolution timing
        private readonly System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        #endregion

        #region Nested Types

        private struct DelayedPacket
        {
            public byte[] Data;
            public int Length;
            public GONetTransportQoS QoS;
            public IGONetTransportConnection Target;
            public byte Channel;
            public long DeliveryTimeTicks; // Stopwatch ticks when packet should be delivered
            public IGONetTransportConnection Source; // For received packets
            public bool IsReceive; // True if this is a receive-side packet
        }

        #endregion

        #region Constructor

        /// <summary>
        /// Create a network condition simulator wrapping an existing transport.
        /// </summary>
        /// <param name="innerTransport">The actual transport to wrap</param>
        /// <param name="config">Initial configuration (can be changed at runtime)</param>
        public NetworkConditionSimulator(IGONetTransport innerTransport, NetworkConditionConfig config = null)
        {
            this.innerTransport = innerTransport ?? throw new ArgumentNullException(nameof(innerTransport));
            this.config = config ?? new NetworkConditionConfig();
            this.config.Validate();

            // Subscribe to inner transport's receive event to intercept incoming packets
            this.innerTransport.OnMessageReceived += OnInnerMessageReceived;
        }

        #endregion

        #region Configuration

        /// <summary>
        /// Get or set the current network condition configuration.
        /// Changes take effect immediately for new packets.
        /// </summary>
        public NetworkConditionConfig Config
        {
            get => config;
            set
            {
                config = value ?? new NetworkConditionConfig();
                config.Validate();
            }
        }

        /// <summary>
        /// Number of packets currently in the send delay queue.
        /// </summary>
        public int SendQueueCount => sendQueue.Count;

        /// <summary>
        /// Number of packets currently in the receive delay queue.
        /// </summary>
        public int ReceiveQueueCount => receiveQueue.Count;

        #endregion

        #region IGONetTransport Implementation - Passthrough

        // Events - forward from inner transport (except OnMessageReceived which we intercept)
        public event Func<IGONetTransportConnectionRequest, bool> OnServerConnectionRequested
        {
            add => innerTransport.OnServerConnectionRequested += value;
            remove => innerTransport.OnServerConnectionRequested -= value;
        }

        public event Action<IGONetTransportConnection> OnServerClientConnected
        {
            add => innerTransport.OnServerClientConnected += value;
            remove => innerTransport.OnServerClientConnected -= value;
        }

        public event Action<IGONetTransportConnection, GONetTransportDisconnectReason> OnServerClientDisconnected
        {
            add => innerTransport.OnServerClientDisconnected += value;
            remove => innerTransport.OnServerClientDisconnected -= value;
        }

        public event Action OnClientConnected
        {
            add => innerTransport.OnClientConnected += value;
            remove => innerTransport.OnClientConnected -= value;
        }

        public event Action<GONetTransportDisconnectReason> OnClientDisconnected
        {
            add => innerTransport.OnClientDisconnected += value;
            remove => innerTransport.OnClientDisconnected -= value;
        }

        public event Action<GONetTransportClientState> OnClientStateChanged
        {
            add => innerTransport.OnClientStateChanged += value;
            remove => innerTransport.OnClientStateChanged -= value;
        }

        // This event is NOT forwarded - we intercept and delay messages
        public event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte> OnMessageReceived;
        public event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte, long> OnMessageReceivedWithTimestamp;

        // Properties - passthrough
        public float RTTMilliseconds => innerTransport.RTTMilliseconds;
        public float PacketLoss => innerTransport.PacketLoss;
        public float SentBandwidthKBPS => innerTransport.SentBandwidthKBPS;
        public float ReceivedBandwidthKBPS => innerTransport.ReceivedBandwidthKBPS;
        public bool IsServer => innerTransport.IsServer;
        public bool IsClient => innerTransport.IsClient;
        public bool IsConnected => innerTransport.IsConnected;
        public GONetTransportCapabilities Capabilities => innerTransport.Capabilities;

        // Lifecycle - passthrough
        public void Initialize(GONetTransportConfig config) => innerTransport.Initialize(config);
        public void Shutdown()
        {
            // Clear queues on shutdown
            sendQueue.Clear();
            receiveQueue.Clear();
            innerTransport.Shutdown();
        }

        public void Dispose()
        {
            innerTransport.OnMessageReceived -= OnInnerMessageReceived;
            sendQueue.Clear();
            receiveQueue.Clear();
            innerTransport.Dispose();
        }

        // Server operations - passthrough
        public void StartServer(int port, int maxConnections) => innerTransport.StartServer(port, maxConnections);
        public void StopServer() => innerTransport.StopServer();
        public void DisconnectConnection(IGONetTransportConnection connection, GONetTransportDisconnectReason reason)
            => innerTransport.DisconnectConnection(connection, reason);

        // Client operations - passthrough
        public void ConnectClient(string address, int port, int timeoutSeconds, byte[] authData = null)
            => innerTransport.ConnectClient(address, port, timeoutSeconds, authData);
        public void DisconnectClient() => innerTransport.DisconnectClient();

        // Capabilities - passthrough
        public bool IsServerRunningLocally(int port) => innerTransport.IsServerRunningLocally(port);
        public int GetMaxMessageSize(GONetTransportQoS qos) => innerTransport.GetMaxMessageSize(qos);

        #endregion

        #region IGONetTransport Implementation - Simulated

        /// <summary>
        /// Send with simulated network conditions.
        /// Packets may be delayed, dropped, or duplicated based on configuration.
        /// </summary>
        public void Send(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection target = null, byte channel = 0)
        {
            if (!config.IsEnabled)
            {
                // Simulation disabled - send immediately
                innerTransport.Send(data, length, qos, target, channel);
                return;
            }

            // Check for packet loss
            if (ShouldDropPacket())
            {
                // Packet dropped - do nothing
                return;
            }

            // Calculate delivery time with latency + jitter
            long deliveryTicks = CalculateDeliveryTime();

            // Copy data (original buffer may be reused by caller)
            byte[] dataCopy = new byte[length];
            Array.Copy(data, 0, dataCopy, 0, length);

            // Queue for delayed delivery
            sendQueue.Add(new DelayedPacket
            {
                Data = dataCopy,
                Length = length,
                QoS = qos,
                Target = target,
                Channel = channel,
                DeliveryTimeTicks = deliveryTicks,
                IsReceive = false
            });

            // Check for duplication
            if (ShouldDuplicatePacket())
            {
                // Add duplicate with slightly different delay
                byte[] duplicateCopy = new byte[length];
                Array.Copy(data, 0, duplicateCopy, 0, length);

                sendQueue.Add(new DelayedPacket
                {
                    Data = duplicateCopy,
                    Length = length,
                    QoS = qos,
                    Target = target,
                    Channel = channel,
                    DeliveryTimeTicks = CalculateDeliveryTime(), // Different jitter
                    IsReceive = false
                });
            }
        }

        /// <summary>
        /// Broadcast with simulated network conditions.
        /// </summary>
        public void Broadcast(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection excludeConnection = null, byte channel = 0)
        {
            if (!config.IsEnabled)
            {
                innerTransport.Broadcast(data, length, qos, excludeConnection, channel);
                return;
            }

            // For broadcast, we apply simulation to the entire broadcast
            // (In real networks, broadcast packets experience similar conditions)
            if (ShouldDropPacket())
            {
                return;
            }

            long deliveryTicks = CalculateDeliveryTime();

            byte[] dataCopy = new byte[length];
            Array.Copy(data, 0, dataCopy, 0, length);

            // Store as a "broadcast" packet (target = null, but we track excludeConnection separately)
            // For simplicity, we just delay and then call inner broadcast
            sendQueue.Add(new DelayedPacket
            {
                Data = dataCopy,
                Length = length,
                QoS = qos,
                Target = null, // Indicates broadcast
                Channel = channel,
                DeliveryTimeTicks = deliveryTicks,
                IsReceive = false,
                Source = excludeConnection // Reuse Source field for exclude connection
            });
        }

        /// <summary>
        /// Update must be called each frame to process delayed packets.
        /// </summary>
        public void Update()
        {
            // Update inner transport first
            innerTransport.Update();

            if (!config.IsEnabled)
            {
                return;
            }

            long nowTicks = stopwatch.ElapsedTicks;

            // Process send queue - deliver packets whose time has come
            ProcessDelayQueue(sendQueue, nowTicks, isReceive: false);

            // Process receive queue - deliver packets whose time has come
            ProcessDelayQueue(receiveQueue, nowTicks, isReceive: true);
        }

        #endregion

        #region Private Methods

        private void OnInnerMessageReceived(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection source, byte channel)
        {
            if (!config.IsEnabled)
            {
                // Simulation disabled - forward immediately
                OnMessageReceived?.Invoke(data, length, qos, source, channel);
                return;
            }

            // Apply receive-side simulation (latency on incoming packets)
            if (ShouldDropPacket())
            {
                return;
            }

            long deliveryTicks = CalculateDeliveryTime();

            // Copy data (inner transport's buffer may be reused)
            byte[] dataCopy = new byte[length];
            Array.Copy(data, 0, dataCopy, 0, length);

            receiveQueue.Add(new DelayedPacket
            {
                Data = dataCopy,
                Length = length,
                QoS = qos,
                Source = source,
                Channel = channel,
                DeliveryTimeTicks = deliveryTicks,
                IsReceive = true
            });

            // Handle duplication
            if (ShouldDuplicatePacket())
            {
                byte[] duplicateCopy = new byte[length];
                Array.Copy(data, 0, duplicateCopy, 0, length);

                receiveQueue.Add(new DelayedPacket
                {
                    Data = duplicateCopy,
                    Length = length,
                    QoS = qos,
                    Source = source,
                    Channel = channel,
                    DeliveryTimeTicks = CalculateDeliveryTime(),
                    IsReceive = true
                });
            }
        }

        private void ProcessDelayQueue(List<DelayedPacket> queue, long nowTicks, bool isReceive)
        {
            if (queue.Count == 0) return;

            readyPackets.Clear();

            // Find all packets ready for delivery
            for (int i = queue.Count - 1; i >= 0; i--)
            {
                if (queue[i].DeliveryTimeTicks <= nowTicks)
                {
                    readyPackets.Add(queue[i]);
                    queue.RemoveAt(i);
                }
            }

            // Deliver ready packets
            foreach (var packet in readyPackets)
            {
                if (isReceive)
                {
                    // Deliver to subscribers
                    OnMessageReceived?.Invoke(packet.Data, packet.Length, packet.QoS, packet.Source, packet.Channel);
                }
                else
                {
                    // Actually send via inner transport
                    if (packet.Target == null && packet.Source != null)
                    {
                        // This was a broadcast (Source holds excludeConnection)
                        innerTransport.Broadcast(packet.Data, packet.Length, packet.QoS, packet.Source, packet.Channel);
                    }
                    else
                    {
                        innerTransport.Send(packet.Data, packet.Length, packet.QoS, packet.Target, packet.Channel);
                    }
                }
            }
        }

        private bool ShouldDropPacket()
        {
            if (config.PacketLossPercent <= 0f) return false;
            return random.NextDouble() * 100.0 < config.PacketLossPercent;
        }

        private bool ShouldDuplicatePacket()
        {
            if (config.DuplicatePercent <= 0f) return false;
            return random.NextDouble() * 100.0 < config.DuplicatePercent;
        }

        private long CalculateDeliveryTime()
        {
            int latency = config.LatencyMs;

            // Add jitter
            if (config.JitterMs > 0)
            {
                int jitter = random.Next(-config.JitterMs, config.JitterMs + 1);
                latency = Math.Max(0, latency + jitter);
            }

            // Convert ms to stopwatch ticks
            long delayTicks = (long)(latency * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            return stopwatch.ElapsedTicks + delayTicks;
        }

        #endregion
    }
}
