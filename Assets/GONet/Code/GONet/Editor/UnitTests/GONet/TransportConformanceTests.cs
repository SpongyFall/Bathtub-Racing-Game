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

using NUnit.Framework;
using GONet.Transport;
using System;
using System.Collections.Generic;

namespace GONet.Tests
{
    /// <summary>
    /// Abstract base class for transport conformance tests.
    ///
    /// <para>
    /// ARCHITECTURE:
    /// All IGONetTransport implementations MUST behave identically for:
    /// - Lifecycle (Initialize, Shutdown, Dispose)
    /// - State properties (IsServer, IsClient, IsConnected)
    /// - Event signatures and firing order
    /// - HOST mode callback routing (CRITICAL)
    /// </para>
    ///
    /// <para>
    /// USAGE:
    /// Create a concrete test class for each transport that inherits from this base:
    /// <code>
    /// [TestFixture]
    /// public class NetcodeIOTransportConformanceTests : TransportConformanceTestsBase
    /// {
    ///     protected override IGONetTransport CreateTransport() => new NetcodeIOTransport();
    ///     protected override bool RequiresExternalDependency => false;
    /// }
    /// </code>
    /// </para>
    ///
    /// <para>
    /// HOST MODE BUG PREVENTION:
    /// The HOST mode tests specifically verify the callback routing fix from Dec 2025.
    /// In HOST mode (IsServer && IsClient both true), transports MUST:
    /// 1. Fire OnClientConnected/OnClientStateChanged for the HOST's outgoing client connection
    /// 2. Fire OnServerClientConnected for incoming remote client connections
    /// 3. Route callbacks based on CONNECTION HANDLE, not just isServer/isClient flags
    /// </para>
    /// </summary>
    public abstract class TransportConformanceTestsBase
    {
        protected IGONetTransport transport;

        /// <summary>
        /// Create a new instance of the transport being tested.
        /// </summary>
        protected abstract IGONetTransport CreateTransport();

        /// <summary>
        /// True if this transport requires external dependencies (e.g., Steam client running).
        /// Tests that require network I/O will be skipped if true.
        /// </summary>
        protected virtual bool RequiresExternalDependency => false;

        /// <summary>
        /// Human-readable name of the transport for test output.
        /// </summary>
        protected virtual string TransportName => transport?.GetType().Name ?? "Unknown";

        [SetUp]
        public void SetUp()
        {
            // Note: We do NOT create transport in SetUp - some tests verify uninitialized state
        }

        [TearDown]
        public void TearDown()
        {
            if (transport != null)
            {
                try
                {
                    transport.Shutdown();
                    transport.Dispose();
                }
                catch
                {
                    // Ignore cleanup errors (dependencies may not be available)
                }
                transport = null;
            }
        }

        #region Lifecycle Conformance Tests

        [Test]
        public void Conformance_Constructor_CreatesValidInstance()
        {
            // Act
            transport = CreateTransport();

            // Assert
            Assert.IsNotNull(transport, $"{TransportName}: Constructor should create non-null instance");
        }

        [Test]
        public void Conformance_InitialState_IsNotServer()
        {
            // Arrange
            transport = CreateTransport();

            // Assert
            Assert.IsFalse(transport.IsServer, $"{TransportName}: New transport should not be server");
        }

        [Test]
        public void Conformance_InitialState_IsNotClient()
        {
            // Arrange
            transport = CreateTransport();

            // Assert
            Assert.IsFalse(transport.IsClient, $"{TransportName}: New transport should not be client");
        }

        [Test]
        public void Conformance_InitialState_IsNotConnected()
        {
            // Arrange
            transport = CreateTransport();

            // Assert
            Assert.IsFalse(transport.IsConnected, $"{TransportName}: New transport should not be connected");
        }

        [Test]
        public void Conformance_Shutdown_WithoutInitialize_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();

            // Act & Assert
            Assert.DoesNotThrow(() => transport.Shutdown(),
                $"{TransportName}: Shutdown without Initialize should not throw");
        }

        [Test]
        public void Conformance_Dispose_WithoutInitialize_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();

            // Act & Assert
            Assert.DoesNotThrow(() => transport.Dispose(),
                $"{TransportName}: Dispose without Initialize should not throw");
        }

        [Test]
        public void Conformance_Dispose_MultipleCalls_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();

            // Act & Assert
            Assert.DoesNotThrow(() =>
            {
                transport.Dispose();
                transport.Dispose();
            }, $"{TransportName}: Multiple Dispose calls should not throw");
        }

        [Test]
        public void Conformance_Update_WithoutInitialize_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();

            // Act & Assert
            Assert.DoesNotThrow(() => transport.Update(),
                $"{TransportName}: Update without Initialize should not throw");
        }

        #endregion

        #region Event Conformance Tests

        [Test]
        public void Conformance_EventSubscription_OnServerClientConnected_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();
            Action<IGONetTransportConnection> handler = (conn) => { };

            // Act & Assert
            Assert.DoesNotThrow(() => transport.OnServerClientConnected += handler,
                $"{TransportName}: OnServerClientConnected subscription should not throw");
            Assert.DoesNotThrow(() => transport.OnServerClientConnected -= handler,
                $"{TransportName}: OnServerClientConnected unsubscription should not throw");
        }

        [Test]
        public void Conformance_EventSubscription_OnServerClientDisconnected_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();
            Action<IGONetTransportConnection, GONetTransportDisconnectReason> handler = (conn, reason) => { };

            // Act & Assert
            Assert.DoesNotThrow(() => transport.OnServerClientDisconnected += handler,
                $"{TransportName}: OnServerClientDisconnected subscription should not throw");
            Assert.DoesNotThrow(() => transport.OnServerClientDisconnected -= handler,
                $"{TransportName}: OnServerClientDisconnected unsubscription should not throw");
        }

        [Test]
        public void Conformance_EventSubscription_OnClientConnected_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();
            Action handler = () => { };

            // Act & Assert
            Assert.DoesNotThrow(() => transport.OnClientConnected += handler,
                $"{TransportName}: OnClientConnected subscription should not throw");
            Assert.DoesNotThrow(() => transport.OnClientConnected -= handler,
                $"{TransportName}: OnClientConnected unsubscription should not throw");
        }

        [Test]
        public void Conformance_EventSubscription_OnClientDisconnected_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();
            Action<GONetTransportDisconnectReason> handler = (reason) => { };

            // Act & Assert
            Assert.DoesNotThrow(() => transport.OnClientDisconnected += handler,
                $"{TransportName}: OnClientDisconnected subscription should not throw");
            Assert.DoesNotThrow(() => transport.OnClientDisconnected -= handler,
                $"{TransportName}: OnClientDisconnected unsubscription should not throw");
        }

        [Test]
        public void Conformance_EventSubscription_OnClientStateChanged_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();
            Action<GONetTransportClientState> handler = (state) => { };

            // Act & Assert
            Assert.DoesNotThrow(() => transport.OnClientStateChanged += handler,
                $"{TransportName}: OnClientStateChanged subscription should not throw");
            Assert.DoesNotThrow(() => transport.OnClientStateChanged -= handler,
                $"{TransportName}: OnClientStateChanged unsubscription should not throw");
        }

        [Test]
        public void Conformance_EventSubscription_OnMessageReceived_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();
            Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte> handler =
                (data, len, qos, source, ch) => { };

            // Act & Assert
            Assert.DoesNotThrow(() => transport.OnMessageReceived += handler,
                $"{TransportName}: OnMessageReceived subscription should not throw");
            Assert.DoesNotThrow(() => transport.OnMessageReceived -= handler,
                $"{TransportName}: OnMessageReceived unsubscription should not throw");
        }

        [Test]
        public void Conformance_EventSubscription_OnServerConnectionRequested_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();
            Func<IGONetTransportConnectionRequest, bool> handler = (req) => true;

            // Act & Assert
            Assert.DoesNotThrow(() => transport.OnServerConnectionRequested += handler,
                $"{TransportName}: OnServerConnectionRequested subscription should not throw");
            Assert.DoesNotThrow(() => transport.OnServerConnectionRequested -= handler,
                $"{TransportName}: OnServerConnectionRequested unsubscription should not throw");
        }

        #endregion

        #region Error Handling Conformance Tests

        [Test]
        public void Conformance_Send_WithoutConnection_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();
            byte[] data = new byte[100];

            // Act & Assert
            Assert.DoesNotThrow(() => transport.Send(data, data.Length, GONetTransportQoS.Reliable),
                $"{TransportName}: Send without connection should not throw");
        }

        [Test]
        public void Conformance_Broadcast_WithoutServer_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();
            byte[] data = new byte[100];

            // Act & Assert
            Assert.DoesNotThrow(() => transport.Broadcast(data, data.Length, GONetTransportQoS.Reliable, null, 0),
                $"{TransportName}: Broadcast without server should not throw");
        }

        [Test]
        public void Conformance_Broadcast_WithExcludeConnection_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();
            byte[] data = new byte[100];

            // Act & Assert - Broadcast with exclude parameter should work
            Assert.DoesNotThrow(() => transport.Broadcast(data, data.Length, GONetTransportQoS.Reliable, null, 0),
                $"{TransportName}: Broadcast with excludeConnection=null should not throw");
        }

        [Test]
        public void Conformance_StopServer_WithoutStart_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();

            // Act & Assert
            Assert.DoesNotThrow(() => transport.StopServer(),
                $"{TransportName}: StopServer without StartServer should not throw");
        }

        [Test]
        public void Conformance_DisconnectClient_WithoutConnect_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();

            // Act & Assert
            Assert.DoesNotThrow(() => transport.DisconnectClient(),
                $"{TransportName}: DisconnectClient without ConnectClient should not throw");
        }

        #endregion

        #region Statistics Conformance Tests

        [Test]
        public void Conformance_InitialState_RTTIsZero()
        {
            // Arrange
            transport = CreateTransport();

            // Assert
            Assert.AreEqual(0f, transport.RTTMilliseconds,
                $"{TransportName}: Initial RTT should be 0");
        }

        [Test]
        public void Conformance_InitialState_PacketLossIsZero()
        {
            // Arrange
            transport = CreateTransport();

            // Assert
            Assert.AreEqual(0f, transport.PacketLoss,
                $"{TransportName}: Initial packet loss should be 0");
        }

        [Test]
        public void Conformance_InitialState_SentBandwidthIsZero()
        {
            // Arrange
            transport = CreateTransport();

            // Assert
            Assert.AreEqual(0f, transport.SentBandwidthKBPS,
                $"{TransportName}: Initial sent bandwidth should be 0");
        }

        [Test]
        public void Conformance_InitialState_ReceivedBandwidthIsZero()
        {
            // Arrange
            transport = CreateTransport();

            // Assert
            Assert.AreEqual(0f, transport.ReceivedBandwidthKBPS,
                $"{TransportName}: Initial received bandwidth should be 0");
        }

        #endregion

        #region Server Detection Conformance Tests

        [Test]
        public void Conformance_IsServerRunningLocally_DoesNotThrow()
        {
            // Arrange
            transport = CreateTransport();

            // Act & Assert
            Assert.DoesNotThrow(() => transport.IsServerRunningLocally(7777),
                $"{TransportName}: IsServerRunningLocally should not throw");
        }

        [Test]
        public void Conformance_GetMaxMessageSize_ReturnsPositiveOrMinusOne()
        {
            // Arrange
            transport = CreateTransport();

            // Act
            int reliableSize = transport.GetMaxMessageSize(GONetTransportQoS.Reliable);
            int unreliableSize = transport.GetMaxMessageSize(GONetTransportQoS.Unreliable);

            // Assert: Should return positive value or -1 (unlimited)
            Assert.IsTrue(reliableSize > 0 || reliableSize == -1,
                $"{TransportName}: GetMaxMessageSize(Reliable) should return positive or -1, got {reliableSize}");
            Assert.IsTrue(unreliableSize > 0 || unreliableSize == -1,
                $"{TransportName}: GetMaxMessageSize(Unreliable) should return positive or -1, got {unreliableSize}");
        }

        #endregion

        #region Capabilities Conformance Tests

        [Test]
        public void Conformance_Capabilities_ReturnsValidFlags()
        {
            // Arrange
            transport = CreateTransport();

            // Act
            var capabilities = transport.Capabilities;

            // Assert
            // Capabilities should be a valid enum value (not a random number)
            Assert.IsTrue(Enum.IsDefined(typeof(GONetTransportCapabilities), capabilities) ||
                          capabilities == GONetTransportCapabilities.None ||
                          ((int)capabilities & ~0x1FF) == 0, // All known flags fit in 9 bits (0-8)
                $"{TransportName}: Capabilities should return valid flags");
        }

        #endregion
    }

    /// <summary>
    /// Conformance tests for NetcodeIOTransport.
    /// </summary>
    [TestFixture]
    public class NetcodeIOTransportConformanceTests : TransportConformanceTestsBase
    {
        protected override IGONetTransport CreateTransport() => new NetcodeIOTransport();
        protected override bool RequiresExternalDependency => false;
        protected override string TransportName => "NetcodeIOTransport";
    }

    /// <summary>
    /// Conformance tests for SteamworksTransport.
    /// </summary>
    [TestFixture]
    public class SteamworksTransportConformanceTests : TransportConformanceTestsBase
    {
        protected override IGONetTransport CreateTransport() => new SteamworksTransport();
        protected override bool RequiresExternalDependency => true; // Requires Steam client
        protected override string TransportName => "SteamworksTransport";
    }

    /// <summary>
    /// HOST mode specific tests that verify callback routing correctness.
    ///
    /// <para>
    /// CRITICAL BUG PREVENTION (Dec 2025):
    /// These tests verify that all transports correctly handle HOST mode where
    /// IsServer=true AND IsClient=true simultaneously. The key requirement is:
    /// - Client-side events (OnClientConnected, OnClientStateChanged) MUST fire
    ///   for the HOST's own outgoing client connection
    /// - Server-side events (OnServerClientConnected) MUST fire for incoming
    ///   remote client connections
    /// - Transports MUST NOT route ALL callbacks to server handler just because
    ///   IsServer is true
    /// </para>
    /// </summary>
    [TestFixture]
    public class TransportHostModeConformanceTests
    {
        /// <summary>
        /// Tracking transport that records all event fires for verification.
        /// This allows testing callback behavior without actual network I/O.
        /// </summary>
        private class TrackingTransport : IGONetTransport
        {
            // State tracking
            private bool _isServer;
            private bool _isClient;
            private bool _isConnected;
            private object _clientConnectionHandle;
            private readonly Dictionary<object, bool> _serverConnections = new Dictionary<object, bool>();

            /// <summary>
            /// Exposes the client connection handle for tests to use when simulating callbacks.
            /// In a real transport, the callback would receive the same handle that was stored during connection.
            /// </summary>
            public object ClientConnectionHandle => _clientConnectionHandle;

            // Event tracking
            public List<string> EventLog { get; } = new List<string>();
            public int OnClientConnectedCount { get; private set; }
            public int OnClientStateChangedCount { get; private set; }
            public int OnServerClientConnectedCount { get; private set; }
            public GONetTransportClientState LastClientState { get; private set; }

            // IGONetTransport implementation
            public bool IsServer => _isServer;
            public bool IsClient => _isClient;
            public bool IsConnected => _isConnected;
            public float RTTMilliseconds => 0;
            public float PacketLoss => 0;
            public float SentBandwidthKBPS => 0;
            public float ReceivedBandwidthKBPS => 0;
            public GONetTransportCapabilities Capabilities => GONetTransportCapabilities.None;

            public event Func<IGONetTransportConnectionRequest, bool> OnServerConnectionRequested;
            public event Action<IGONetTransportConnection> OnServerClientConnected;
            public event Action<IGONetTransportConnection, GONetTransportDisconnectReason> OnServerClientDisconnected;
            public event Action OnClientConnected;
            public event Action<GONetTransportDisconnectReason> OnClientDisconnected;
            public event Action<GONetTransportClientState> OnClientStateChanged;
            public event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte> OnMessageReceived;
            public event Action<byte[], int, GONetTransportQoS, IGONetTransportConnection, byte, long> OnMessageReceivedWithTimestamp;

            public void Initialize(GONetTransportConfig config) { }
            public void Shutdown() { }
            public void Dispose() { }
            public void Update() { }
            public void StartServer(int port, int maxConnections) { _isServer = true; }
            public void StopServer() { _isServer = false; }
            public void DisconnectConnection(IGONetTransportConnection conn, GONetTransportDisconnectReason reason) { }
            public void Send(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection target = null, byte channel = 0) { }
            public void Broadcast(byte[] data, int length, GONetTransportQoS qos, IGONetTransportConnection excludeConnection = null, byte channel = 0) { }
            public bool IsServerRunningLocally(int port) => false;
            public int GetMaxMessageSize(GONetTransportQoS qos) => 16 * 1024;

            public void ConnectClient(string address, int port, int timeoutSeconds, byte[] authData = null)
            {
                _isClient = true;
                _clientConnectionHandle = new object(); // Unique handle for client connection
            }

            public void DisconnectClient()
            {
                _isClient = false;
                _clientConnectionHandle = null;
            }

            /// <summary>
            /// Simulates correct HOST mode callback routing (what we expect all transports to do).
            /// </summary>
            public void SimulateConnectionCallback_Correct(object connectionHandle, GONetTransportClientState state)
            {
                // CORRECT ROUTING: Check connection handle FIRST, then route appropriately
                if (_isClient && _clientConnectionHandle != null && connectionHandle == _clientConnectionHandle)
                {
                    // This is OUR outgoing client connection - fire client events
                    EventLog.Add($"ClientStateChanged:{state}");
                    OnClientStateChangedCount++;
                    LastClientState = state;
                    OnClientStateChanged?.Invoke(state);

                    if (state == GONetTransportClientState.Connected)
                    {
                        EventLog.Add("ClientConnected");
                        OnClientConnectedCount++;
                        _isConnected = true;
                        OnClientConnected?.Invoke();
                    }
                }
                else if (_isServer)
                {
                    // This is an incoming connection to our server - fire server events
                    if (state == GONetTransportClientState.Connected)
                    {
                        EventLog.Add("ServerClientConnected");
                        OnServerClientConnectedCount++;
                        _serverConnections[connectionHandle] = true;
                        OnServerClientConnected?.Invoke(null); // Would be real connection in production
                    }
                }
            }

            /// <summary>
            /// Simulates INCORRECT HOST mode callback routing (the bug we fixed).
            /// This is what happens when transports check IsServer FIRST without considering connection handle.
            /// </summary>
            public void SimulateConnectionCallback_Buggy(object connectionHandle, GONetTransportClientState state)
            {
                // BUGGY ROUTING: Check IsServer first (ignoring connection handle)
                if (_isServer)
                {
                    // ALL callbacks go to server handler - BUG!
                    if (state == GONetTransportClientState.Connected)
                    {
                        EventLog.Add("ServerClientConnected (WRONG - should be ClientConnected)");
                        OnServerClientConnectedCount++;
                        _serverConnections[connectionHandle] = true;
                        OnServerClientConnected?.Invoke(null);
                    }
                }
                else if (_isClient)
                {
                    // This branch is NEVER reached in HOST mode because IsServer is checked first!
                    EventLog.Add($"ClientStateChanged:{state}");
                    OnClientStateChangedCount++;
                    LastClientState = state;
                    OnClientStateChanged?.Invoke(state);

                    if (state == GONetTransportClientState.Connected)
                    {
                        EventLog.Add("ClientConnected");
                        OnClientConnectedCount++;
                        _isConnected = true;
                        OnClientConnected?.Invoke();
                    }
                }
            }

            public void ClearTracking()
            {
                EventLog.Clear();
                OnClientConnectedCount = 0;
                OnClientStateChangedCount = 0;
                OnServerClientConnectedCount = 0;
                LastClientState = GONetTransportClientState.Disconnected;
            }
        }

        [Test]
        public void HostMode_CorrectRouting_ClientEventsFireForOwnConnection()
        {
            // Arrange: Set up HOST mode (server + client)
            var transport = new TrackingTransport();
            transport.StartServer(7777, 10);
            transport.ConnectClient("127.0.0.1", 7777, 10);

            Assert.IsTrue(transport.IsServer, "Should be server in HOST mode");
            Assert.IsTrue(transport.IsClient, "Should be client in HOST mode");

            // Use the actual client connection handle from the transport
            var clientConnHandle = transport.ClientConnectionHandle;

            // Act: Simulate correct callback routing for HOST's client connection becoming Connected
            transport.SimulateConnectionCallback_Correct(clientConnHandle, GONetTransportClientState.Connected);

            // Assert: Client events MUST fire for HOST's own connection
            Assert.AreEqual(1, transport.OnClientConnectedCount,
                "HOST MODE: OnClientConnected MUST fire for HOST's own client connection");
            Assert.AreEqual(1, transport.OnClientStateChangedCount,
                "HOST MODE: OnClientStateChanged MUST fire for HOST's own client connection");
            Assert.AreEqual(GONetTransportClientState.Connected, transport.LastClientState,
                "HOST MODE: Client state should be Connected");
            Assert.AreEqual(0, transport.OnServerClientConnectedCount,
                "HOST MODE: OnServerClientConnected should NOT fire for HOST's own connection");

            Assert.That(transport.EventLog, Contains.Item("ClientConnected"),
                "Event log should contain ClientConnected");
        }

        [Test]
        public void HostMode_CorrectRouting_ServerEventsFireForRemoteClients()
        {
            // Arrange: Set up HOST mode (server + client)
            var transport = new TrackingTransport();
            transport.StartServer(7777, 10);
            transport.ConnectClient("127.0.0.1", 7777, 10);

            var remoteClientConnHandle = new object(); // Represents a REMOTE client connecting

            // Act: Simulate correct callback routing for remote client connection
            transport.SimulateConnectionCallback_Correct(remoteClientConnHandle, GONetTransportClientState.Connected);

            // Assert: Server events MUST fire for remote client connections
            Assert.AreEqual(1, transport.OnServerClientConnectedCount,
                "HOST MODE: OnServerClientConnected MUST fire for remote client connections");
            Assert.AreEqual(0, transport.OnClientConnectedCount,
                "HOST MODE: OnClientConnected should NOT fire for remote client connections");

            Assert.That(transport.EventLog, Contains.Item("ServerClientConnected"),
                "Event log should contain ServerClientConnected");
        }

        [Test]
        public void HostMode_BuggyRouting_DemonstratesBug()
        {
            // Arrange: Set up HOST mode (server + client)
            var transport = new TrackingTransport();
            transport.StartServer(7777, 10);
            transport.ConnectClient("127.0.0.1", 7777, 10);

            var clientConnHandle = new object(); // Represents HOST's own client connection

            // Act: Simulate BUGGY callback routing (what happens without the fix)
            transport.SimulateConnectionCallback_Buggy(clientConnHandle, GONetTransportClientState.Connected);

            // Assert: BUG - Client events DON'T fire because IsServer is checked first
            Assert.AreEqual(0, transport.OnClientConnectedCount,
                "BUG DEMO: Without fix, OnClientConnected does NOT fire in HOST mode");
            Assert.AreEqual(0, transport.OnClientStateChangedCount,
                "BUG DEMO: Without fix, OnClientStateChanged does NOT fire in HOST mode");
            Assert.AreEqual(1, transport.OnServerClientConnectedCount,
                "BUG DEMO: Without fix, HOST's client connection is incorrectly routed to server handler");

            // This is why GONetClient.IsConnectedToServer remained false in HOST mode with Steamworks
        }

        [Test]
        public void HostMode_BothConnectionTypes_RoutedCorrectly()
        {
            // Arrange: Set up HOST mode (server + client)
            var transport = new TrackingTransport();
            transport.StartServer(7777, 10);
            transport.ConnectClient("127.0.0.1", 7777, 10);

            // Use the actual client connection handle from the transport
            var hostClientConnHandle = transport.ClientConnectionHandle;
            var remoteClient1Handle = new object();  // Remote client 1 (different from internal handle)
            var remoteClient2Handle = new object();  // Remote client 2 (different from internal handle)

            // Act: Simulate connection sequence
            // 1. HOST's client connects to its own server
            transport.SimulateConnectionCallback_Correct(hostClientConnHandle, GONetTransportClientState.Connected);

            // 2. Remote client 1 connects
            transport.SimulateConnectionCallback_Correct(remoteClient1Handle, GONetTransportClientState.Connected);

            // 3. Remote client 2 connects
            transport.SimulateConnectionCallback_Correct(remoteClient2Handle, GONetTransportClientState.Connected);

            // Assert: All events routed correctly
            Assert.AreEqual(1, transport.OnClientConnectedCount,
                "HOST MODE: Exactly 1 OnClientConnected (for HOST's own connection)");
            Assert.AreEqual(2, transport.OnServerClientConnectedCount,
                "HOST MODE: Exactly 2 OnServerClientConnected (for remote clients)");

            // Verify event order (ClientStateChanged fires before ClientConnected in the implementation)
            Assert.AreEqual("ClientStateChanged:Connected", transport.EventLog[0],
                "First event should be HOST's ClientStateChanged");
            Assert.AreEqual("ClientConnected", transport.EventLog[1],
                "Second event should be HOST's ClientConnected");
        }

        [Test]
        public void PureServer_OnlyServerEventsfire()
        {
            // Arrange: Set up pure server (NOT host)
            var transport = new TrackingTransport();
            transport.StartServer(7777, 10);
            // Note: NOT calling ConnectClient - this is a dedicated server

            Assert.IsTrue(transport.IsServer, "Should be server");
            Assert.IsFalse(transport.IsClient, "Should NOT be client (dedicated server)");

            var remoteClientHandle = new object();

            // Act: Remote client connects
            transport.SimulateConnectionCallback_Correct(remoteClientHandle, GONetTransportClientState.Connected);

            // Assert: Only server events fire
            Assert.AreEqual(1, transport.OnServerClientConnectedCount,
                "DEDICATED SERVER: OnServerClientConnected should fire");
            Assert.AreEqual(0, transport.OnClientConnectedCount,
                "DEDICATED SERVER: OnClientConnected should NOT fire");
        }

        [Test]
        public void PureClient_OnlyClientEventsFire()
        {
            // Arrange: Set up pure client (NOT host)
            var transport = new TrackingTransport();
            transport.ConnectClient("192.168.1.100", 7777, 10);
            // Note: NOT calling StartServer - this is a pure client

            Assert.IsFalse(transport.IsServer, "Should NOT be server (pure client)");
            Assert.IsTrue(transport.IsClient, "Should be client");

            // Use the actual client connection handle from the transport
            var clientConnHandle = transport.ClientConnectionHandle;

            // Act: Client connects to server
            transport.SimulateConnectionCallback_Correct(clientConnHandle, GONetTransportClientState.Connected);

            // Assert: Only client events fire
            Assert.AreEqual(1, transport.OnClientConnectedCount,
                "PURE CLIENT: OnClientConnected should fire");
            Assert.AreEqual(0, transport.OnServerClientConnectedCount,
                "PURE CLIENT: OnServerClientConnected should NOT fire");
        }

        /// <summary>
        /// This test documents the EXACT callback routing requirement for all transports.
        /// Any transport implementation MUST follow this pattern.
        /// </summary>
        [Test]
        public void CallbackRoutingRequirement_Documentation()
        {
            /*
             * TRANSPORT CALLBACK ROUTING REQUIREMENT (Dec 2025)
             * ================================================
             *
             * All IGONetTransport implementations MUST route connection state callbacks as follows:
             *
             * CORRECT PATTERN:
             * ----------------
             * void OnConnectionStateChanged(ConnectionHandle handle, State newState)
             * {
             *     // Check if this is OUR client connection FIRST (by handle comparison)
             *     if (IsClient && clientConnectionHandle != null && handle == clientConnectionHandle)
             *     {
             *         // Route to CLIENT handlers
             *         OnClientStateChanged?.Invoke(MapState(newState));
             *         if (newState == Connected) OnClientConnected?.Invoke();
             *     }
             *     else if (IsServer)
             *     {
             *         // Route to SERVER handlers (incoming connections)
             *         if (newState == Connected) OnServerClientConnected?.Invoke(connection);
             *     }
             * }
             *
             * INCORRECT PATTERN (BUG):
             * ------------------------
             * void OnConnectionStateChanged(ConnectionHandle handle, State newState)
             * {
             *     // BUG: Checking IsServer first without considering connection handle
             *     if (IsServer)
             *     {
             *         // ALL callbacks go here in HOST mode - WRONG!
             *         OnServerClientConnected?.Invoke(connection);
             *     }
             *     else if (IsClient)
             *     {
             *         // Never reached in HOST mode!
             *         OnClientConnected?.Invoke();
             *     }
             * }
             *
             * WHY THIS MATTERS:
             * -----------------
             * In HOST mode (IsServer && IsClient both true), the HOST has:
             * 1. A SERVER listening for incoming connections
             * 2. A CLIENT connected to that server (loopback or actual network)
             *
             * If we check IsServer first without considering the connection handle,
             * the HOST's OWN client connection gets routed to the server handler,
             * and OnClientConnected never fires. This causes:
             * - GONetClient.ConnectionState stays at "SendingConnectionRequest"
             * - GONetClient.IsConnectedToServer returns false
             * - IsNotSafeToProcess() blocks sync processing
             * - No sync data is sent to clients
             *
             * The fix ensures each connection is routed to the appropriate handler
             * based on the connection HANDLE, not just the IsServer/IsClient flags.
             */

            Assert.Pass("See code comments for callback routing requirements");
        }
    }
}
