using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GONet.Transport;
using ReliableNetcode;
using UnityEngine;

namespace GONet
{
    /// <summary>
    /// Runtime stress test harness for testing reliable transport under IL2CPP.
    ///
    /// Drop this component on any GameObject in your scene.
    /// Press the configured hotkey (default: F9) to start/stop stress tests.
    ///
    /// Tests run entirely in-memory using ReliableEndpoint pairs - no network required.
    /// Results are logged to the console and GONet log file.
    /// </summary>
    public class GONetRuntimeStressTest : MonoBehaviour
    {
        [Header("Hotkey")]
        [Tooltip("Key to start/stop stress test")]
        public KeyCode triggerKey = KeyCode.F9;

        [Header("Test Configuration")]
        [Tooltip("Test mode: ReliableEndpoint (raw) or GONetConnection (full stack)")]
        public TestMode testMode = TestMode.ReliableEndpoint;

        [Tooltip("Number of simulated client connections")]
        public int clientCount = 10;

        [Tooltip("Number of sender threads per client")]
        public int threadsPerClient = 10;

        [Tooltip("Messages per thread")]
        public int messagesPerThread = 50;

        [Tooltip("Simulated packet loss rate (0-1)")]
        [Range(0f, 0.5f)]
        public float packetLossRate = 0.05f;

        [Tooltip("Simulated latency in milliseconds")]
        public float latencyMs = 30f;

        [Tooltip("Include HOST client in GONetConnection mode (tests the HOST fix)")]
        public bool includeHostClient = true;

        public enum TestMode
        {
            /// <summary>Uses raw ReliableEndpoint pairs (original test)</summary>
            ReliableEndpoint,
            /// <summary>Uses actual GONetConnection classes with mock transport (tests HOST fix)</summary>
            GONetConnection
        }

        [Header("Status")]
        [SerializeField] private bool _isRunning;
        [SerializeField] private string _status = "Idle";
        [SerializeField] private int _messagesSent;
        [SerializeField] private int _messagesReceived;
        [SerializeField] private int _errors;

        private CancellationTokenSource _cts;
        private Task _testTask;

        private void Update()
        {
            if (Input.GetKeyDown(triggerKey))
            {
                if (_isRunning)
                {
                    StopTest();
                }
                else
                {
                    StartTest();
                }
            }
        }

        public void StartTest()
        {
            if (_isRunning) return;

            _isRunning = true;
            _status = "Starting...";
            _messagesSent = 0;
            _messagesReceived = 0;
            _errors = 0;

            _cts = new CancellationTokenSource();
            _testTask = Task.Run(() => RunStressTest(_cts.Token));

            string modeStr = testMode == TestMode.GONetConnection ? "GONetConnection (HOST fix test)" : "ReliableEndpoint (raw)";
            GONetLog.Info($"[StressTest] Started: {clientCount} clients, {threadsPerClient} threads/client, {messagesPerThread} msgs/thread, Mode: {modeStr}");
        }

        public void StopTest()
        {
            if (!_isRunning) return;

            _status = "Stopping...";
            _cts?.Cancel();

            GONetLog.Info("[StressTest] Stop requested");
        }

        private void RunStressTest(CancellationToken ct)
        {
            try
            {
                _status = $"Running stress test ({testMode})...";

                TestResult result;
                if (testMode == TestMode.GONetConnection)
                {
                    result = RunGONetConnectionTest(ct);
                }
                else
                {
                    result = RunConcurrencyTest(ct);
                }

                _status = result.Success
                    ? $"PASSED: {result.MessagesSent} sent, {result.MessagesReceived} received"
                    : $"FAILED: {result.ErrorMessage}";

                GONetLog.Info($"[StressTest] {_status}");
            }
            catch (OperationCanceledException)
            {
                _status = "Cancelled";
                GONetLog.Info("[StressTest] Cancelled by user");
            }
            catch (Exception ex)
            {
                _status = $"ERROR: {ex.Message}";
                GONetLog.Error($"[StressTest] Exception: {ex}");
                _errors++;
            }
            finally
            {
                _isRunning = false;
            }
        }

        private class TestResult
        {
            public bool Success;
            public int MessagesSent;
            public int MessagesReceived;
            public int Errors;
            public string ErrorMessage;
        }

        private class ClientPair
        {
            public int ClientId;
            public ReliableEndpoint Client;
            public ReliableEndpoint Server;
            public ConcurrentQueue<(byte[], int, double)> ToServer = new();
            public ConcurrentQueue<(byte[], int, double)> ToClient = new();
            public readonly object ClientLock = new object();
            public readonly object ServerLock = new object();
            public int Sent;
            public int ServerReceived;
        }

        private TestResult RunConcurrencyTest(CancellationToken ct)
        {
            var result = new TestResult();
            var pairs = new List<ClientPair>();
            var rng = new System.Random();
            double currentTime = 0;
            var timeLock = new object();
            bool running = true;

            // Create client pairs
            for (int i = 0; i < clientCount; i++)
            {
                var pair = new ClientPair
                {
                    ClientId = i + 1,
                    Client = new ReliableEndpoint(),
                    Server = new ReliableEndpoint()
                };

                // Server receive callback
                pair.Server.ReceiveCallback = (buffer, length, receiveTimestamp) =>
                {
                    Interlocked.Increment(ref pair.ServerReceived);
                    Interlocked.Increment(ref _messagesReceived);
                };

                // Client transmit -> queue with latency
                pair.Client.TransmitCallback = (buffer, length) =>
                {
                    if (packetLossRate > 0 && rng.NextDouble() < packetLossRate)
                        return; // Drop

                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    double deliveryTime;
                    lock (timeLock)
                    {
                        deliveryTime = currentTime + latencyMs / 1000.0;
                    }
                    pair.ToServer.Enqueue((copy, length, deliveryTime));
                };

                // Server transmit -> queue with latency
                pair.Server.TransmitCallback = (buffer, length) =>
                {
                    if (packetLossRate > 0 && rng.NextDouble() < packetLossRate)
                        return;

                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(buffer, 0, copy, 0, length);
                    double deliveryTime;
                    lock (timeLock)
                    {
                        deliveryTime = currentTime + latencyMs / 1000.0;
                    }
                    pair.ToClient.Enqueue((copy, length, deliveryTime));
                };

                pairs.Add(pair);
            }

            // Update thread
            var updateTask = Task.Run(() =>
            {
                while (running && !ct.IsCancellationRequested)
                {
                    lock (timeLock)
                    {
                        currentTime += 0.016;
                    }

                    foreach (var pair in pairs)
                    {
                        double t;
                        lock (timeLock) t = currentTime;

                        // Process queues
                        while (pair.ToServer.TryPeek(out var p) && p.Item3 <= t)
                        {
                            if (pair.ToServer.TryDequeue(out p))
                            {
                                lock (pair.ServerLock)
                                {
                                    pair.Server.ReceivePacket(p.Item1, p.Item2);
                                }
                            }
                        }

                        while (pair.ToClient.TryPeek(out var p2) && p2.Item3 <= t)
                        {
                            if (pair.ToClient.TryDequeue(out p2))
                            {
                                lock (pair.ClientLock)
                                {
                                    pair.Client.ReceivePacket(p2.Item1, p2.Item2);
                                }
                            }
                        }

                        lock (pair.ClientLock)
                        {
                            pair.Client.Update(t);
                            pair.Client.ProcessSendBuffer_IfAppropriate();
                        }
                        lock (pair.ServerLock)
                        {
                            pair.Server.Update(t);
                            pair.Server.ProcessSendBuffer_IfAppropriate();
                        }
                    }

                    Thread.Sleep(1);
                }
            });

            // Sender tasks
            var senderTasks = new List<Task>();
            foreach (var pair in pairs)
            {
                var p = pair;
                for (int t = 0; t < threadsPerClient; t++)
                {
                    int threadId = t;
                    senderTasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            for (int i = 0; i < messagesPerThread && running && !ct.IsCancellationRequested; i++)
                            {
                                byte[] msg = new byte[80];
                                Buffer.BlockCopy(BitConverter.GetBytes(p.ClientId), 0, msg, 0, 4);
                                Buffer.BlockCopy(BitConverter.GetBytes(threadId * 1000 + i), 0, msg, 4, 4);

                                lock (p.ClientLock)
                                {
                                    p.Client.SendMessage(msg, msg.Length, QosType.Reliable);
                                    Interlocked.Increment(ref p.Sent);
                                    Interlocked.Increment(ref _messagesSent);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _errors);
                            GONetLog.Error($"[StressTest] Sender error: {ex.Message}");
                        }
                    }));
                }
            }

            // Wait for senders
            Task.WaitAll(senderTasks.ToArray(), 30000);

            // Flush
            Thread.Sleep(3000);
            running = false;
            updateTask.Wait(5000);

            // Final flush
            lock (timeLock)
            {
                for (int i = 0; i < 300; i++)
                {
                    currentTime += 0.016;
                    foreach (var pair in pairs)
                    {
                        lock (pair.ClientLock)
                        {
                            pair.Client.Update(currentTime);
                            pair.Client.ProcessSendBuffer_IfAppropriate();
                        }
                        lock (pair.ServerLock)
                        {
                            pair.Server.Update(currentTime);
                            pair.Server.ProcessSendBuffer_IfAppropriate();
                        }
                    }
                }
            }

            // Collect results
            result.MessagesSent = pairs.Sum(p => p.Sent);
            result.MessagesReceived = pairs.Sum(p => p.ServerReceived);
            result.Errors = _errors;

            // Log per-client stats
            foreach (var pair in pairs)
            {
                GONetLog.Info($"[StressTest] Client {pair.ClientId}: sent={pair.Sent}, serverRcv={pair.ServerReceived}");
            }

            result.Success = result.Errors == 0 && result.MessagesReceived > 0;
            if (!result.Success)
            {
                result.ErrorMessage = result.Errors > 0
                    ? $"{result.Errors} errors occurred"
                    : "No messages received";
            }

            return result;
        }

        private void OnGUI()
        {
            if (!_isRunning && string.IsNullOrEmpty(_status)) return;

            GUILayout.BeginArea(new Rect(10, 10, 450, 180));
            GUILayout.BeginVertical("box");

            string modeStr = testMode == TestMode.GONetConnection ? "GONetConnection (HOST fix)" : "ReliableEndpoint";
            GUILayout.Label($"<b>GONet Stress Test</b> [{modeStr}] (Press {triggerKey})", new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Label($"Status: {_status}");
            GUILayout.Label($"Sent: {_messagesSent:N0}  |  Received: {_messagesReceived:N0}  |  Errors: {_errors}");
            if (testMode == TestMode.GONetConnection && includeHostClient)
            {
                GUILayout.Label("<color=yellow>HOST client included (tests cross-delivery fix)</color>", new GUIStyle(GUI.skin.label) { richText = true });
            }

            if (_isRunning)
            {
                if (GUILayout.Button("Stop Test"))
                {
                    StopTest();
                }
            }
            else
            {
                if (GUILayout.Button("Start Test"))
                {
                    StartTest();
                }
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
        }

        #region GONetConnection Test Mode

        /// <summary>
        /// Mock transport for testing GONetConnection classes without actual network.
        /// Routes messages through in-memory queues with simulated latency/loss.
        /// </summary>
        private class MockTransport : IGONetTransport
        {
            public GONetTransportCapabilities Capabilities => GONetTransportCapabilities.None; // No built-in reliability
            public float RTTMilliseconds => 0;
            public float PacketLoss => 0;
            public float SentBandwidthKBPS => 0;
            public float ReceivedBandwidthKBPS => 0;
            public bool IsServer => false;
            public bool IsClient => false;
            public bool IsConnected => false;

            public event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte> OnMessageReceived;
            public event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte, long> OnMessageReceivedWithTimestamp;
            public event Func<IGONetTransportConnectionRequest, bool> OnServerConnectionRequested;
            public event Action<IGONetTransportConnection> OnServerClientConnected;
            public event Action<IGONetTransportConnection, GONetTransportDisconnectReason> OnServerClientDisconnected;
            public event Action OnClientConnected;
            public event Action<GONetTransportDisconnectReason> OnClientDisconnected;
            public event Action<GONetTransportClientState> OnClientStateChanged;

            private readonly ConcurrentQueue<(byte[] data, int length, GONetTransportQoS qos, MockConnection source, byte channel, double deliverAt)> _pendingMessages = new();
            private double _currentTime;
            private readonly float _packetLossRate;
            private readonly float _latencyMs;
            private readonly System.Random _rng = new();
            private readonly object _lock = new();

            public MockTransport(float packetLossRate, float latencyMs)
            {
                _packetLossRate = packetLossRate;
                _latencyMs = latencyMs;
            }

            public void Initialize(GONetTransportConfig config) { }
            public void Shutdown() { }
            public void StartServer(int port, int maxConnections) { }
            public void StopServer() { }
            public void DisconnectConnection(IGONetTransportConnection connection, GONetTransportDisconnectReason reason) { }
            public void ConnectClient(string address, int port, int timeoutSeconds, byte[] authData = null) { }
            public void DisconnectClient() { }
            public bool IsServerRunningLocally(int port) => false;
            public int GetMaxMessageSize(GONetTransportQoS qos) => 16 * 1024;
            public void Dispose() { }

            public void Send(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection target = null, byte channel = 0)
            {
                // Simulate packet loss
                if (_packetLossRate > 0 && _rng.NextDouble() < _packetLossRate)
                    return;

                // Copy data and queue with latency
                byte[] copy = new byte[length];
                Buffer.BlockCopy(data, 0, copy, 0, length);

                double deliverAt;
                lock (_lock)
                {
                    deliverAt = _currentTime + _latencyMs / 1000.0;
                }

                _pendingMessages.Enqueue((copy, length, qos, target as MockConnection, channel, deliverAt));
            }

            public void Broadcast(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection excludeConnection = null, byte channel = 0)
            {
                Send(data, length, qos, null, channel);
            }

            public void Update()
            {
                // Deliver messages that have reached their delivery time
                double t;
                lock (_lock) t = _currentTime;

                while (_pendingMessages.TryPeek(out var msg) && msg.deliverAt <= t)
                {
                    if (_pendingMessages.TryDequeue(out msg))
                    {
                        OnMessageReceived?.Invoke(msg.data, msg.length, msg.qos, msg.source, msg.channel);
                    }
                }
            }

            public void AdvanceTime(double deltaSeconds)
            {
                lock (_lock)
                {
                    _currentTime += deltaSeconds;
                }
            }

            public double GetCurrentTime()
            {
                lock (_lock)
                {
                    return _currentTime;
                }
            }
        }

        /// <summary>
        /// Mock connection identifier for the mock transport.
        /// </summary>
        private class MockConnection : IGONetTransportConnection
        {
            public ulong ConnectionUID { get; }
            public ushort AuthorityId { get; set; }
            public bool IsConnected => true;
            public string RemoteAddress => "mock://localhost";
            public float RTTMilliseconds => 0;
            public float PacketLoss => 0;
            public uint BytesQueuedForSend => 0;
            public bool IsUsingRelay => false;

            public MockConnection(ulong uid)
            {
                ConnectionUID = uid;
            }

            public T GetNativeConnection<T>() where T : class => null;
        }

        /// <summary>
        /// Represents a client-server pair using actual GONetConnection classes.
        /// </summary>
        private class GONetConnectionPair
        {
            public int ClientId;
            public GONetConnection_ClientToServer ClientConn;
            public GONetConnection_ServerToClient ServerConn;
            public MockTransport ClientTransport;
            public MockTransport ServerTransport;
            public MockConnection ClientMockConn;
            public readonly object ClientLock = new();
            public readonly object ServerLock = new();
            public int Sent;
            public int ServerReceived;
            public bool IsHostClient;
        }

        /// <summary>
        /// Tests using actual GONetConnection classes to verify the HOST fix.
        /// Creates real GONetConnection_ClientToServer and GONetConnection_ServerToClient instances.
        /// </summary>
        private TestResult RunGONetConnectionTest(CancellationToken ct)
        {
            var result = new TestResult();
            var pairs = new List<GONetConnectionPair>();
            bool running = true;

            // Save original GONetMain state
            bool originalIsServerOverride = GONetMain.isServerOverride;

            try
            {
                // Create client pairs
                for (int i = 0; i < clientCount; i++)
                {
                    bool isHostClient = includeHostClient && i == 0;
                    var pair = CreateGONetConnectionPair(i + 1, isHostClient);
                    pairs.Add(pair);

                    GONetLog.Info($"[StressTest] Created {(isHostClient ? "HOST" : "remote")} client {pair.ClientId}");
                }

                // Update thread - processes transports and reliable endpoints
                var updateTask = Task.Run(() =>
                {
                    while (running && !ct.IsCancellationRequested)
                    {
                        foreach (var pair in pairs)
                        {
                            // Advance transport time
                            pair.ClientTransport.AdvanceTime(0.016);
                            pair.ServerTransport.AdvanceTime(0.016);
                            double t = pair.ClientTransport.GetCurrentTime();

                            // Process transport queues
                            lock (pair.ClientLock)
                            {
                                pair.ClientTransport.Update();
                                pair.ClientConn.Update(t);
                                pair.ClientConn.ProcessSendBuffer_IfAppropriate();
                            }
                            lock (pair.ServerLock)
                            {
                                pair.ServerTransport.Update();
                                pair.ServerConn.Update(t);
                                pair.ServerConn.ProcessSendBuffer_IfAppropriate();
                            }
                        }

                        Thread.Sleep(1);
                    }
                });

                // Sender tasks
                var senderTasks = new List<Task>();
                foreach (var pair in pairs)
                {
                    var p = pair;
                    for (int t = 0; t < threadsPerClient; t++)
                    {
                        int threadId = t;
                        senderTasks.Add(Task.Run(() =>
                        {
                            try
                            {
                                for (int i = 0; i < messagesPerThread && running && !ct.IsCancellationRequested; i++)
                                {
                                    byte[] msg = new byte[80];
                                    Buffer.BlockCopy(BitConverter.GetBytes(p.ClientId), 0, msg, 0, 4);
                                    Buffer.BlockCopy(BitConverter.GetBytes(threadId * 1000 + i), 0, msg, 4, 4);

                                    lock (p.ClientLock)
                                    {
                                        // Cast to ReliableEndpoint to call SendMessage directly
                                        // (GONetConnection hides it with [Obsolete] since normal usage requires channels)
                                        // For this test, we bypass channel setup and go straight to reliability layer
                                        ((ReliableEndpoint)p.ClientConn).SendMessage(msg, msg.Length, QosType.Reliable);
                                        Interlocked.Increment(ref p.Sent);
                                        Interlocked.Increment(ref _messagesSent);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref _errors);
                                GONetLog.Error($"[StressTest] Sender error (GONetConnection): {ex.Message}");
                            }
                        }));
                    }
                }

                // Wait for senders
                Task.WaitAll(senderTasks.ToArray(), 30000);

                // Flush
                Thread.Sleep(3000);
                running = false;
                updateTask.Wait(5000);

                // Final flush
                for (int i = 0; i < 300; i++)
                {
                    foreach (var pair in pairs)
                    {
                        double t = pair.ClientTransport.GetCurrentTime() + 0.016;
                        pair.ClientTransport.AdvanceTime(0.016);
                        pair.ServerTransport.AdvanceTime(0.016);

                        lock (pair.ClientLock)
                        {
                            pair.ClientTransport.Update();
                            pair.ClientConn.Update(t);
                            pair.ClientConn.ProcessSendBuffer_IfAppropriate();
                        }
                        lock (pair.ServerLock)
                        {
                            pair.ServerTransport.Update();
                            pair.ServerConn.Update(t);
                            pair.ServerConn.ProcessSendBuffer_IfAppropriate();
                        }
                    }
                }

                // Collect results
                result.MessagesSent = pairs.Sum(p => p.Sent);
                result.MessagesReceived = pairs.Sum(p => p.ServerReceived);
                result.Errors = _errors;

                // Log per-client stats
                foreach (var pair in pairs)
                {
                    string clientType = pair.IsHostClient ? "HOST" : "remote";
                    GONetLog.Info($"[StressTest] Client {pair.ClientId} ({clientType}): sent={pair.Sent}, serverRcv={pair.ServerReceived}");
                }

                result.Success = result.Errors == 0 && result.MessagesReceived > 0;
                if (!result.Success)
                {
                    result.ErrorMessage = result.Errors > 0
                        ? $"{result.Errors} errors occurred"
                        : "No messages received";
                }
            }
            finally
            {
                // Restore GONetMain state
                GONetMain.isServerOverride = originalIsServerOverride;
            }

            return result;
        }

        /// <summary>
        /// Creates a GONetConnection pair with mock transports.
        /// </summary>
        private GONetConnectionPair CreateGONetConnectionPair(int clientId, bool isHostClient)
        {
            var pair = new GONetConnectionPair
            {
                ClientId = clientId,
                IsHostClient = isHostClient,
                ClientTransport = new MockTransport(packetLossRate, latencyMs),
                ServerTransport = new MockTransport(packetLossRate, latencyMs)
            };

            // For HOST client scenario, set GONetMain.IsServer = true
            // This is the key condition for the HOST fix at GONetConnections.cs:281
            if (isHostClient)
            {
                GONetMain.isServerOverride = true;
            }
            else
            {
                GONetMain.isServerOverride = false;
            }

            // Create mock connection for server-side (null for HOST client)
            pair.ClientMockConn = isHostClient ? null : new MockConnection((ulong)clientId);

            // Create actual GONetConnection instances
            // HOST client: connection=null + IsServer=true → should NOT subscribe to transport
            // Remote client: connection!=null → should subscribe
            pair.ClientConn = new GONetConnection_ClientToServer(pair.ClientTransport);
            pair.ServerConn = new GONetConnection_ServerToClient(pair.ServerTransport, pair.ClientMockConn);

            // Wire up transport routing: client sends → server receives
            pair.ClientTransport.OnMessageReceived += (data, length, qos, source, channel) =>
            {
                // This is from server → client (ACKs)
                lock (pair.ClientLock)
                {
                    pair.ClientConn.ReceivePacket(data, length);
                }
            };

            // Server receives messages from client
            pair.ServerConn.ReceiveCallback = (data, length, receiveTimestamp) =>
            {
                Interlocked.Increment(ref pair.ServerReceived);
                Interlocked.Increment(ref _messagesReceived);
            };

            // Wire up client transmit → server transport
            pair.ClientConn.TransmitCallback = (data, length) =>
            {
                // Route to server transport which will deliver to server connection
                pair.ServerTransport.Send(data, length, GONetTransportQoS.Unreliable, pair.ClientMockConn, 0);
            };

            // Wire up server transmit → client transport (for ACKs)
            pair.ServerConn.TransmitCallback = (data, length) =>
            {
                pair.ClientTransport.Send(data, length, GONetTransportQoS.Unreliable, null, 0);
            };

            // Server transport delivers to server connection's reliability layer
            pair.ServerTransport.OnMessageReceived += (data, length, qos, source, channel) =>
            {
                lock (pair.ServerLock)
                {
                    pair.ServerConn.ReceivePacket(data, length);
                }
            };

            return pair;
        }

        #endregion
    }
}
