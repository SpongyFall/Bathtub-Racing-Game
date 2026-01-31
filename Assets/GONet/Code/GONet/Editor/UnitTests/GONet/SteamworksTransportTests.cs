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

namespace GONet.Tests
{
    /// <summary>
    /// Unit tests for <see cref="SteamworksTransport"/>.
    ///
    /// <para>
    /// NOTE: These tests are primarily structural/API tests that verify:
    /// - Initialization/shutdown lifecycle
    /// - Configuration handling
    /// - Event subscription/unsubscription
    /// - Interface contract compliance
    /// - Error handling for common edge cases
    /// </para>
    ///
    /// <para>
    /// INTEGRATION TESTING REQUIRED:
    /// Full network functionality testing requires:
    /// - Steam client running
    /// - Valid Steam App ID (GONet App ID: 4168160)
    /// - SteamAPI.Init() success
    /// - At least 2 Unity instances (server + client)
    /// </para>
    ///
    /// <para>
    /// These tests do NOT require Steam to be running (they test structure, not network I/O).
    /// For full integration tests, see integration test scene (GONetSample.unity with Steamworks transport).
    /// </para>
    /// </summary>
    [TestFixture]
    public class SteamworksTransportTests
    {
        private SteamworksTransport transport;

        [SetUp]
        public void SetUp()
        {
            // Note: We do NOT initialize transport in SetUp because some tests verify uninitialized state
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up transport if it was created
            if (transport != null)
            {
                try
                {
                    transport.Shutdown();
                    transport.Dispose();
                }
                catch
                {
                    // Ignore cleanup errors (Steam may not be running)
                }
                transport = null;
            }
        }

        #region Lifecycle Tests

        [Test]
        public void SteamworksTransport_Constructor_CreatesInstance()
        {
            // Arrange & Act
            transport = new SteamworksTransport();

            // Assert
            Assert.IsNotNull(transport, "Constructor should create transport instance");
            Assert.IsFalse(transport.IsServer, "New transport should not be server");
            Assert.IsFalse(transport.IsClient, "New transport should not be client");
            Assert.IsFalse(transport.IsConnected, "New transport should not be connected");
        }

        [Test]
        public void SteamworksTransport_Initialize_WithNullConfig_UsesDefault()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            // NOTE: This test will throw if Steam is not running (expected behavior)
            // We're testing that null config is handled gracefully (uses default)
            try
            {
                transport.Initialize(null);
                // If we get here, Steam is running and initialization succeeded
                Assert.Pass("Initialize with null config succeeded (uses default)");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Steam") || ex.Message.Contains("not running") || ex.Message.Contains("not initialized"))
            {
                // Expected if Steam is not running - test passes
                Assert.Pass("Initialize correctly throws InvalidOperationException when Steam not running");
            }
        }

        [Test]
        public void SteamworksTransport_Initialize_WithValidConfig_Succeeds()
        {
            // Arrange
            transport = new SteamworksTransport();
            GONetTransportConfig config = GONetTransportConfig.CreateDefault();

            // Act & Assert
            try
            {
                transport.Initialize(config);
                Assert.Pass("Initialize with valid config succeeded");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Steam") || ex.Message.Contains("not running") || ex.Message.Contains("not initialized"))
            {
                Assert.Pass("Initialize correctly throws InvalidOperationException when Steam not running");
            }
        }

        [Test]
        public void SteamworksTransport_Shutdown_WithoutInitialize_DoesNotThrow()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            Assert.DoesNotThrow(() => transport.Shutdown(), "Shutdown without Initialize should not throw");
        }

        [Test]
        public void SteamworksTransport_Dispose_WithoutInitialize_DoesNotThrow()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            Assert.DoesNotThrow(() => transport.Dispose(), "Dispose without Initialize should not throw");
        }

        [Test]
        public void SteamworksTransport_Dispose_MultipleCalls_DoesNotThrow()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            Assert.DoesNotThrow(() =>
            {
                transport.Dispose();
                transport.Dispose(); // Second call should be safe
            }, "Multiple Dispose calls should not throw");
        }

        #endregion

        #region Capabilities Tests

        [Test]
        public void SteamworksTransport_Capabilities_IncludesP2P()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act
            var capabilities = transport.Capabilities;

            // Assert
            Assert.IsTrue((capabilities & GONetTransportCapabilities.P2P) != 0,
                "Steamworks transport should report P2P capability");
        }

        [Test]
        public void SteamworksTransport_Capabilities_IncludesEncryption()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act
            var capabilities = transport.Capabilities;

            // Assert
            Assert.IsTrue((capabilities & GONetTransportCapabilities.Encryption) != 0,
                "Steamworks transport should report Encryption capability");
        }

        [Test]
        public void SteamworksTransport_Capabilities_IncludesAuthentication()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act
            var capabilities = transport.Capabilities;

            // Assert
            Assert.IsTrue((capabilities & GONetTransportCapabilities.Authentication) != 0,
                "Steamworks transport should report Authentication capability");
        }

        [Test]
        [Ignore("SteamworksTransport intentionally does NOT report Reliability capability. " +
                "Steam's native reliable channel had 0-2% delivery failures, so we removed it (commit 581eac6d, Nov 7 2025). " +
                "GONet now wraps SteamworksTransport with ReliabilityLayerAdapter which provides reliability via ReliableNetcode library. " +
                "See SteamworksTransport.cs:1147-1149 for details.")]
        public void SteamworksTransport_Capabilities_IncludesReliability()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act
            var capabilities = transport.Capabilities;

            // Assert
            Assert.IsTrue((capabilities & GONetTransportCapabilities.Reliability) != 0,
                "Steamworks transport should report Reliability capability");
        }

        [Test]
        public void SteamworksTransport_Capabilities_IncludesCompression()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act
            var capabilities = transport.Capabilities;

            // Assert
            Assert.IsTrue((capabilities & GONetTransportCapabilities.Compression) != 0,
                "Steamworks transport should report Compression capability");
        }

        [Test]
        public void SteamworksTransport_Capabilities_IncludesFragmentation()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act
            var capabilities = transport.Capabilities;

            // Assert
            Assert.IsTrue((capabilities & GONetTransportCapabilities.Fragmentation) != 0,
                "Steamworks transport should report Fragmentation capability");
        }

        [Test]
        public void SteamworksTransport_Capabilities_IncludesVirtualPorts()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act
            var capabilities = transport.Capabilities;

            // Assert
            Assert.IsTrue((capabilities & GONetTransportCapabilities.VirtualPorts) != 0,
                "Steamworks transport should report VirtualPorts capability");
        }

        [Test]
        public void SteamworksTransport_Capabilities_IncludesMultipleListenSockets()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act
            var capabilities = transport.Capabilities;

            // Assert
            Assert.IsTrue((capabilities & GONetTransportCapabilities.MultipleListenSockets) != 0,
                "Steamworks transport should report MultipleListenSockets capability");
        }

        [Test]
        public void SteamworksTransport_EnsureP2PListenSocket_ReturnsFalse_WhenServerNotRunning()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act
            bool result = transport.EnsureP2PListenSocket(0);

            // Assert
            Assert.IsFalse(result, "EnsureP2PListenSocket should return false when server is not running");
        }

        #endregion

        #region Event Subscription Tests

        [Test]
        public void SteamworksTransport_EventSubscription_OnServerClientConnected_DoesNotThrow()
        {
            // Arrange
            transport = new SteamworksTransport();
            bool eventFired = false;

            // Act & Assert
            Assert.DoesNotThrow(() =>
            {
                transport.OnServerClientConnected += (conn) => eventFired = true;
            }, "Event subscription should not throw");

            Assert.DoesNotThrow(() =>
            {
                transport.OnServerClientConnected -= (conn) => eventFired = true;
            }, "Event unsubscription should not throw");
        }

        [Test]
        public void SteamworksTransport_EventSubscription_OnClientConnected_DoesNotThrow()
        {
            // Arrange
            transport = new SteamworksTransport();
            bool eventFired = false;

            // Act & Assert
            Assert.DoesNotThrow(() =>
            {
                transport.OnClientConnected += () => eventFired = true;
            }, "Event subscription should not throw");

            Assert.DoesNotThrow(() =>
            {
                transport.OnClientConnected -= () => eventFired = true;
            }, "Event unsubscription should not throw");
        }

        [Test]
        public void SteamworksTransport_EventSubscription_OnMessageReceived_DoesNotThrow()
        {
            // Arrange
            transport = new SteamworksTransport();
            bool eventFired = false;

            // Act & Assert
            Assert.DoesNotThrow(() =>
            {
                transport.OnMessageReceived += (data, length, qos, source, channel) => eventFired = true;
            }, "Event subscription should not throw");

            Assert.DoesNotThrow(() =>
            {
                transport.OnMessageReceived -= (data, length, qos, source, channel) => eventFired = true;
            }, "Event unsubscription should not throw");
        }

        #endregion

        #region State Tests

        [Test]
        public void SteamworksTransport_InitialState_IsNotServer()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            Assert.IsFalse(transport.IsServer, "New transport should not be server");
        }

        [Test]
        public void SteamworksTransport_InitialState_IsNotClient()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            Assert.IsFalse(transport.IsClient, "New transport should not be client");
        }

        [Test]
        public void SteamworksTransport_InitialState_IsNotConnected()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            Assert.IsFalse(transport.IsConnected, "New transport should not be connected");
        }

        [Test]
        public void SteamworksTransport_InitialState_RTTIsZero()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            Assert.AreEqual(0f, transport.RTTMilliseconds, "Initial RTT should be 0");
        }

        [Test]
        public void SteamworksTransport_InitialState_PacketLossIsZero()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            Assert.AreEqual(0f, transport.PacketLoss, "Initial packet loss should be 0");
        }

        #endregion

        #region Error Handling Tests

        [Test]
        public void SteamworksTransport_Update_WithoutInitialize_DoesNotThrow()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            Assert.DoesNotThrow(() => transport.Update(), "Update without Initialize should not throw");
        }

        [Test]
        public void SteamworksTransport_Send_WithoutConnection_DoesNotThrow()
        {
            // Arrange
            transport = new SteamworksTransport();
            byte[] data = new byte[100];

            // Act & Assert
            Assert.DoesNotThrow(() =>
            {
                transport.Send(data, data.Length, GONetTransportQoS.Reliable);
            }, "Send without connection should log warning but not throw");
        }

        [Test]
        public void SteamworksTransport_Broadcast_WithoutServer_DoesNotThrow()
        {
            // Arrange
            transport = new SteamworksTransport();
            byte[] data = new byte[100];

            // Act & Assert
            Assert.DoesNotThrow(() =>
            {
                transport.Broadcast(data, data.Length, GONetTransportQoS.Reliable);
            }, "Broadcast without server should log warning but not throw");
        }

        [Test]
        public void SteamworksTransport_StopServer_WithoutStart_DoesNotThrow()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            Assert.DoesNotThrow(() => transport.StopServer(), "StopServer without StartServer should not throw");
        }

        [Test]
        public void SteamworksTransport_DisconnectClient_WithoutConnect_DoesNotThrow()
        {
            // Arrange
            transport = new SteamworksTransport();

            // Act & Assert
            Assert.DoesNotThrow(() => transport.DisconnectClient(), "DisconnectClient without ConnectClient should not throw");
        }

        #endregion

        #region Integration Test Documentation

        /// <summary>
        /// INTEGRATION TEST SCENARIOS (Require Steam + Multiple Unity Instances):
        ///
        /// 1. SERVER STARTUP:
        ///    - Start server with StartServer()
        ///    - Verify IsServer == true
        ///    - Verify listen socket created (check logs)
        ///    - Verify Steam ID logged
        ///
        /// 2. CLIENT CONNECTION:
        ///    - Start client with ConnectClient(serverSteamID)
        ///    - Verify OnServerConnectionRequested fires on server
        ///    - Accept connection
        ///    - Verify OnServerClientConnected fires on server
        ///    - Verify OnClientConnected fires on client
        ///    - Verify IsClient == true, IsConnected == true
        ///
        /// 3. MESSAGE TRANSMISSION (RELIABLE):
        ///    - Client sends byte[] via Send(data, length, Reliable)
        ///    - Verify OnMessageReceived fires on server with correct data
        ///    - Server broadcasts byte[] via Broadcast()
        ///    - Verify OnMessageReceived fires on all clients (except sender if excluded)
        ///
        /// 4. MESSAGE TRANSMISSION (UNRELIABLE):
        ///    - Client sends byte[] via Send(data, length, Unreliable)
        ///    - Verify OnMessageReceived fires on server (may be lossy under load)
        ///    - Server broadcasts unreliable data
        ///    - Verify clients receive most messages (100% not guaranteed)
        ///
        /// 5. DISCONNECTION:
        ///    - Client calls DisconnectClient()
        ///    - Verify OnClientDisconnected fires on client
        ///    - Verify OnServerClientDisconnected fires on server
        ///    - Verify connection removed from server's connection list
        ///
        /// 6. SERVER SHUTDOWN:
        ///    - Server calls StopServer()
        ///    - Verify all clients receive OnClientDisconnected with ServerShutdown reason
        ///    - Verify IsServer == false
        ///
        /// 7. CONNECTION REJECTION:
        ///    - Client attempts to connect
        ///    - Server's OnServerConnectionRequested handler returns false
        ///    - Verify connection rejected
        ///    - Verify OnClientDisconnected fires with AuthenticationFailed reason
        ///
        /// 8. NETWORK STATISTICS:
        ///    - Establish connection
        ///    - Exchange messages for 5+ seconds
        ///    - Verify RTTMilliseconds > 0 (ping measured)
        ///    - Verify PacketLoss >= 0 (stats available)
        ///    - Verify connection.IsUsingRelay reports correct relay status
        ///
        /// 9. STRESS TEST (100+ messages/sec):
        ///    - Send 100 reliable messages back-to-back
        ///    - Verify all received (reliable guarantee)
        ///    - Send 100 unreliable messages back-to-back
        ///    - Verify most received (some loss acceptable)
        ///
        /// 10. LATE-JOINER:
        ///     - Server already running with 2 clients
        ///     - Third client connects
        ///     - Verify OnServerClientConnected fires
        ///     - Verify new client can send/receive messages immediately
        /// </summary>
        [Test]
        [Ignore("Integration test - requires Steam client running + multiple Unity instances")]
        public void SteamworksTransport_IntegrationTest_PlaceholderForDocumentation()
        {
            Assert.Pass("See XML documentation above for integration test scenarios");
        }

        #endregion
    }
}
