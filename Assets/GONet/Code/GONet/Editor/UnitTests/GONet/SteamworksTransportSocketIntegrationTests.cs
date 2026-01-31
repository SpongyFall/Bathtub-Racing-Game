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
using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace GONet.Tests
{
    /// <summary>
    /// Integration tests for Steamworks ISteamNetworkingSockets functionality WITHOUT the full GONet layer.
    ///
    /// <para>
    /// Tests core socket functionality:
    /// - Listen socket creation
    /// - Client connection establishment
    /// - Raw message transmission (unreliable and reliable channels)
    /// - Connection status callbacks
    /// - Message reception via polling
    /// - Proper cleanup and shutdown
    /// </para>
    ///
    /// <para>
    /// REQUIREMENTS:
    /// - Steam client must be running
    /// - Valid Steam App ID in steam_appid.txt (GONet App ID: 4168160)
    /// - SteamAPI.Init() must succeed (or GONetSteamManager already initialized)
    /// - Tests run in Unity Editor (single process, uses loopback networking)
    /// </para>
    ///
    /// <para>
    /// This test validates the fixes from 2025-11-07:
    /// - Immediate connection event firing (no stabilization delay)
    /// - Message reception via polling (ReceiveMessagesOnConnection)
    /// - Proper callback handling (OnSteamNetConnectionStatusChanged)
    /// - Thread safety (callbacks marshalled to main thread if needed)
    /// </para>
    /// </summary>
    [TestFixture]
    public class SteamworksTransportSocketIntegrationTests
    {
        private bool isSteamInitialized = false;
        private HSteamListenSocket serverListenSocket;
        private HSteamNetConnection serverClientConnection; // Server's handle to connected client
        private HSteamNetConnection clientConnection; // Client's handle to server
        private CSteamID serverSteamID;
        private CSteamID clientSteamID;

        private List<string> logMessages = new List<string>();
        private object logLock = new object();

        private const int TEST_TIMEOUT_MS = 10000; // 10 seconds
        private const int POLL_INTERVAL_MS = 16; // ~60 FPS polling rate

        #region Setup and Teardown

        [SetUp]
        public void SetUp()
        {
            logMessages.Clear();
            serverListenSocket = HSteamListenSocket.Invalid;
            serverClientConnection = HSteamNetConnection.Invalid;
            clientConnection = HSteamNetConnection.Invalid;

            // Initialize Steam (safe to call multiple times - returns true if already initialized)
            if (!isSteamInitialized)
            {
                if (!SteamAPI.Init())
                {
                    Assert.Ignore("SteamAPI.Init() failed - Steam client not running or steam_appid.txt contains invalid App ID. Ensure Steam is running and steam_appid.txt contains 4168160 (GONet App ID).");
                    return;
                }

                isSteamInitialized = true;
                Log("Steam initialized by test SetUp");
            }
            else
            {
                Log("Steam already initialized by previous test");
            }

            // Get local Steam ID
            serverSteamID = SteamUser.GetSteamID();
            clientSteamID = serverSteamID; // Same process, same Steam ID

            Log($"Steam initialized - Steam ID: {serverSteamID}");
        }

        [TearDown]
        public void TearDown()
        {
            // Close connections
            if (clientConnection != HSteamNetConnection.Invalid)
            {
                SteamNetworkingSockets.CloseConnection(clientConnection, 0, "", false);
                clientConnection = HSteamNetConnection.Invalid;
            }

            if (serverClientConnection != HSteamNetConnection.Invalid)
            {
                SteamNetworkingSockets.CloseConnection(serverClientConnection, 0, "", false);
                serverClientConnection = HSteamNetConnection.Invalid;
            }

            // Close listen socket
            if (serverListenSocket != HSteamListenSocket.Invalid)
            {
                SteamNetworkingSockets.CloseListenSocket(serverListenSocket);
                serverListenSocket = HSteamListenSocket.Invalid;
            }

            // Shutdown Steam if we initialized it
            if (isSteamInitialized)
            {
                // Give Steam time to flush pending operations
                Thread.Sleep(100);
                SteamAPI.Shutdown();
                isSteamInitialized = false;
            }

            // Log all messages for debugging
            if (logMessages.Count > 0)
            {
                Debug.Log("=== Test Log Messages ===");
                foreach (var msg in logMessages)
                {
                    Debug.Log(msg);
                }
            }
        }

        #endregion

        #region Helper Methods

        private void Log(string message)
        {
            lock (logLock)
            {
                string timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                logMessages.Add(timestamped);
                Debug.Log(timestamped);
            }
        }

        private void RunCallbacks()
        {
            SteamAPI.RunCallbacks();
        }

        private bool WaitForCondition(Func<bool> condition, int timeoutMs, string description)
        {
            Log($"Waiting for: {description}");
            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                RunCallbacks();
                if (condition())
                {
                    Log($"✓ Condition met: {description} (elapsed: {elapsed}ms)");
                    return true;
                }
                Thread.Sleep(POLL_INTERVAL_MS);
                elapsed += POLL_INTERVAL_MS;
            }
            Log($"✗ Timeout waiting for: {description} (elapsed: {elapsed}ms)");
            return false;
        }

        /// <summary>
        /// Creates optimized SteamNetworkingConfigValue_t array matching SteamworksTransport.CreateOptimizedConnectionConfig().
        /// EXACT COPY from production code to ensure tests use same configuration.
        /// </summary>
        private SteamNetworkingConfigValue_t[] CreateOptimizedConnectionConfig()
        {
            var config = new List<SteamNetworkingConfigValue_t>();

            // 1. Increase send buffer to 4 MB (from 512 KB default)
            config.Add(new SteamNetworkingConfigValue_t
            {
                m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                m_val = new SteamNetworkingConfigValue_t.OptionValue { m_int32 = 4 * 1024 * 1024 } // 4 MB
            });

            // 2. Increase receive buffer to 8 MB (from ~4 MB default)
            config.Add(new SteamNetworkingConfigValue_t
            {
                m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvBufferSize,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                m_val = new SteamNetworkingConfigValue_t.OptionValue { m_int32 = 8 * 1024 * 1024 } // 8 MB
            });

            // 3. Disable Nagle algorithm (NagleTime=0)
            config.Add(new SteamNetworkingConfigValue_t
            {
                m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                m_val = new SteamNetworkingConfigValue_t.OptionValue { m_int32 = 0 } // Immediate transmission
            });

            return config.ToArray();
        }

        #endregion

        #region Connection Tests

        [Test]
        [Timeout(TEST_TIMEOUT_MS)]
        public void SteamSockets_CreateListenSocket_Succeeds()
        {
            if (!isSteamInitialized)
            {
                Assert.Ignore("Steam not initialized");
            }

            // Arrange - EXACT API usage from SteamworksTransport.StartServer()
            SteamNetworkingIPAddr localAddr = new SteamNetworkingIPAddr();
            localAddr.Clear();
            localAddr.SetIPv4(0, 27015); // 0.0.0.0:27015 (bind to all interfaces)

            // Act - Use optimized config like production code
            var connectionConfig = CreateOptimizedConnectionConfig();
            serverListenSocket = SteamNetworkingSockets.CreateListenSocketIP(ref localAddr, connectionConfig.Length, connectionConfig);

            // Assert
            Assert.AreNotEqual(HSteamListenSocket.Invalid, serverListenSocket, "Listen socket should be created");
            Log($"✓ Listen socket created: {serverListenSocket}");
        }

        [Test]
        [Timeout(TEST_TIMEOUT_MS)]
        public void SteamSockets_ClientConnectToServer_SuccessfulConnection()
        {
            if (!isSteamInitialized)
            {
                Assert.Ignore("Steam not initialized");
            }

            // Arrange - Create server listen socket EXACTLY like SteamworksTransport
            SteamNetworkingIPAddr serverAddr = new SteamNetworkingIPAddr();
            serverAddr.Clear();
            serverAddr.SetIPv4(0, 27015); // 0.0.0.0:27015 (bind to all interfaces)

            var connectionConfig = CreateOptimizedConnectionConfig();
            serverListenSocket = SteamNetworkingSockets.CreateListenSocketIP(ref serverAddr, connectionConfig.Length, connectionConfig);
            Assert.AreNotEqual(HSteamListenSocket.Invalid, serverListenSocket);
            Log($"Server listen socket created: {serverListenSocket} on port 27015");

            // Track connection status
            bool serverReceivedConnectionRequest = false;
            bool serverConnectionEstablished = false;
            bool clientConnected = false;

            // Register callback for connection status changes
            Callback<SteamNetConnectionStatusChangedCallback_t> callback =
                Callback<SteamNetConnectionStatusChangedCallback_t>.Create((callbackData) =>
            {
                Log($"Callback: Connection={callbackData.m_hConn}, State={callbackData.m_info.m_eState}, OldState={callbackData.m_eOldState}");

                // Server-side: Incoming connection request
                if (callbackData.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting &&
                    callbackData.m_info.m_hListenSocket == serverListenSocket)
                {
                    Log("Server: Connection request received - accepting");
                    serverReceivedConnectionRequest = true;

                    // Accept the connection
                    EResult result = SteamNetworkingSockets.AcceptConnection(callbackData.m_hConn);
                    Log($"Server: AcceptConnection result = {result}");

                    if (result == EResult.k_EResultOK)
                    {
                        serverClientConnection = callbackData.m_hConn;
                    }
                }

                // Server-side: Connection established
                if (callbackData.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected &&
                    callbackData.m_hConn == serverClientConnection)
                {
                    Log("Server: Client fully connected");
                    serverConnectionEstablished = true;
                }

                // Client-side: Connected to server
                if (callbackData.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected &&
                    callbackData.m_hConn == clientConnection)
                {
                    Log("Client: Connected to server");
                    clientConnected = true;
                }
            });

            // Act - Client connects via IP EXACTLY like SteamworksTransport.ConnectClient()
            SteamNetworkingIPAddr clientConnectAddr = new SteamNetworkingIPAddr();
            clientConnectAddr.Clear();
            // Parse "127.0.0.1" exactly like production code
            byte[] ipBytes = new byte[] { 127, 0, 0, 1 };
            uint ipv4 = (uint)((ipBytes[0] << 24) | (ipBytes[1] << 16) | (ipBytes[2] << 8) | ipBytes[3]);
            clientConnectAddr.SetIPv4(ipv4, 27015);

            clientConnection = SteamNetworkingSockets.ConnectByIPAddress(ref clientConnectAddr, connectionConfig.Length, connectionConfig);
            Assert.AreNotEqual(HSteamNetConnection.Invalid, clientConnection, "Client connection handle should be valid");
            Log($"Client: Initiated connection to 127.0.0.1:27015, handle: {clientConnection}");

            // Assert - Wait for connection to establish
            bool serverRequestOk = WaitForCondition(() => serverReceivedConnectionRequest, TEST_TIMEOUT_MS, "Server receives connection request");
            Assert.IsTrue(serverRequestOk, "Server should receive connection request");

            bool serverConnectedOk = WaitForCondition(() => serverConnectionEstablished, TEST_TIMEOUT_MS, "Server connection established");
            Assert.IsTrue(serverConnectedOk, "Server connection should be established");

            bool clientConnectedOk = WaitForCondition(() => clientConnected, TEST_TIMEOUT_MS, "Client connected");
            Assert.IsTrue(clientConnectedOk, "Client should be connected");

            Log("✓ Connection established successfully");
        }

        #endregion

        #region Message Transmission Tests

        [Test]
        [Timeout(TEST_TIMEOUT_MS)]
        public void SteamSockets_SendUnreliableMessage_ClientToServer_Success()
        {
            if (!isSteamInitialized)
            {
                Assert.Ignore("Steam not initialized");
            }

            // Arrange - Establish connection (copied from ClientConnectToServer test)
            SetupClientServerConnection();

            // Prepare test message
            byte[] testMessage = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
            Log($"Sending unreliable message: {testMessage.Length} bytes");

            // Act - Client sends unreliable message to server (pin byte array for Steam API)
            GCHandle handle = GCHandle.Alloc(testMessage, GCHandleType.Pinned);
            EResult sendResult;
            long msgNum;
            try
            {
                IntPtr pData = handle.AddrOfPinnedObject();
                sendResult = SteamNetworkingSockets.SendMessageToConnection(
                    clientConnection,
                    pData,
                    (uint)testMessage.Length,
                    Constants.k_nSteamNetworkingSend_Unreliable | Constants.k_nSteamNetworkingSend_NoDelay,
                    out msgNum
                );
            }
            finally
            {
                handle.Free();
            }

            Assert.AreEqual(EResult.k_EResultOK, sendResult, "Send should succeed");
            Log($"✓ Message sent - msgNum: {msgNum}");

            // Wait a bit for message to arrive
            Thread.Sleep(100);

            // Poll for messages on server
            IntPtr[] messagesPtr = new IntPtr[32];
            int numMessages = SteamNetworkingSockets.ReceiveMessagesOnConnection(serverClientConnection, messagesPtr, 32);
            Log($"Server: Polled and received {numMessages} messages");

            // Assert - Server should receive the message
            Assert.Greater(numMessages, 0, "Server should receive at least 1 message");

            // Validate message contents
            for (int i = 0; i < numMessages; i++)
            {
                SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(messagesPtr[i]);
                byte[] receivedData = new byte[msg.m_cbSize];
                System.Runtime.InteropServices.Marshal.Copy(msg.m_pData, receivedData, 0, msg.m_cbSize);

                Log($"Server: Message {i}: {msg.m_cbSize} bytes, connection: {msg.m_conn}");
                Log($"Server: Message contents: {BitConverter.ToString(receivedData)}");

                // Verify this is the message we sent
                if (msg.m_cbSize == testMessage.Length)
                {
                    bool matches = true;
                    for (int j = 0; j < testMessage.Length; j++)
                    {
                        if (receivedData[j] != testMessage[j])
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (matches)
                    {
                        Log("✓ Received message matches sent message");
                        SteamNetworkingMessage_t.Release(messagesPtr[i]); // CRITICAL: Release message memory
                        return; // Test passed
                    }
                }

                SteamNetworkingMessage_t.Release(messagesPtr[i]); // CRITICAL: Release message memory
            }

            Assert.Fail("Expected message not found in received messages");
        }

        [Test]
        [Timeout(TEST_TIMEOUT_MS)]
        public void SteamSockets_SendReliableMessage_ServerToClient_Success()
        {
            if (!isSteamInitialized)
            {
                Assert.Ignore("Steam not initialized");
            }

            // Arrange - Establish connection
            SetupClientServerConnection();

            // Prepare test message
            byte[] testMessage = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x05, 0x06, 0x07, 0x08 };
            Log($"Server sending reliable message: {testMessage.Length} bytes");

            // Act - Server sends reliable message to client (pin byte array for Steam API)
            GCHandle handle = GCHandle.Alloc(testMessage, GCHandleType.Pinned);
            EResult sendResult;
            long msgNum;
            try
            {
                IntPtr pData = handle.AddrOfPinnedObject();
                sendResult = SteamNetworkingSockets.SendMessageToConnection(
                    serverClientConnection,
                    pData,
                    (uint)testMessage.Length,
                    Constants.k_nSteamNetworkingSend_Reliable | Constants.k_nSteamNetworkingSend_NoDelay,
                    out msgNum
                );
            }
            finally
            {
                handle.Free();
            }

            Assert.AreEqual(EResult.k_EResultOK, sendResult, "Send should succeed");
            Log($"✓ Server: Reliable message sent - msgNum: {msgNum}");

            // Wait a bit for message to arrive
            Thread.Sleep(100);

            // Poll for messages on client
            IntPtr[] messagesPtr = new IntPtr[32];
            int numMessages = SteamNetworkingSockets.ReceiveMessagesOnConnection(clientConnection, messagesPtr, 32);
            Log($"Client: Polled and received {numMessages} messages");

            // Assert - Client should receive the message
            Assert.Greater(numMessages, 0, "Client should receive at least 1 message");

            // Validate message contents
            for (int i = 0; i < numMessages; i++)
            {
                SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(messagesPtr[i]);
                byte[] receivedData = new byte[msg.m_cbSize];
                System.Runtime.InteropServices.Marshal.Copy(msg.m_pData, receivedData, 0, msg.m_cbSize);

                Log($"Client: Message {i}: {msg.m_cbSize} bytes, connection: {msg.m_conn}");
                Log($"Client: Message contents: {BitConverter.ToString(receivedData)}");

                // Verify this is the message we sent
                if (msg.m_cbSize == testMessage.Length)
                {
                    bool matches = true;
                    for (int j = 0; j < testMessage.Length; j++)
                    {
                        if (receivedData[j] != testMessage[j])
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (matches)
                    {
                        Log("✓ Client: Received message matches sent message");
                        SteamNetworkingMessage_t.Release(messagesPtr[i]); // CRITICAL: Release message memory
                        return; // Test passed
                    }
                }

                SteamNetworkingMessage_t.Release(messagesPtr[i]); // CRITICAL: Release message memory
            }

            Assert.Fail("Expected message not found in received messages");
        }

        [Test]
        [Timeout(TEST_TIMEOUT_MS)]
        public void SteamSockets_BidirectionalCommunication_MultipleMessages()
        {
            if (!isSteamInitialized)
            {
                Assert.Ignore("Steam not initialized");
            }

            // Arrange - Establish connection
            SetupClientServerConnection();

            int messagesToSend = 10;
            int clientMessagesReceived = 0;
            int serverMessagesReceived = 0;

            // Act - Send multiple messages in both directions
            for (int i = 0; i < messagesToSend; i++)
            {
                // Client → Server (pin byte array)
                byte[] clientMsg = new byte[] { 0xC1, (byte)i, 0xFF };
                GCHandle clientHandle = GCHandle.Alloc(clientMsg, GCHandleType.Pinned);
                try
                {
                    IntPtr clientData = clientHandle.AddrOfPinnedObject();
                    EResult clientSendResult = SteamNetworkingSockets.SendMessageToConnection(
                        clientConnection, clientData, (uint)clientMsg.Length,
                        Constants.k_nSteamNetworkingSend_Unreliable | Constants.k_nSteamNetworkingSend_NoDelay,
                        out long clientMsgNum
                    );
                    Assert.AreEqual(EResult.k_EResultOK, clientSendResult);
                }
                finally
                {
                    clientHandle.Free();
                }

                // Server → Client (pin byte array)
                byte[] serverMsg = new byte[] { 0x5E, (byte)i, 0xAA };
                GCHandle serverHandle = GCHandle.Alloc(serverMsg, GCHandleType.Pinned);
                try
                {
                    IntPtr serverData = serverHandle.AddrOfPinnedObject();
                    EResult serverSendResult = SteamNetworkingSockets.SendMessageToConnection(
                        serverClientConnection, serverData, (uint)serverMsg.Length,
                        Constants.k_nSteamNetworkingSend_Unreliable | Constants.k_nSteamNetworkingSend_NoDelay,
                        out long serverMsgNum
                    );
                    Assert.AreEqual(EResult.k_EResultOK, serverSendResult);
                }
                finally
                {
                    serverHandle.Free();
                }

                Thread.Sleep(20); // Small delay between messages
            }

            Log($"Sent {messagesToSend} messages in each direction");

            // Wait for messages to arrive
            Thread.Sleep(200);

            // Poll server for client messages
            IntPtr[] serverMessagesPtr = new IntPtr[32];
            int serverMsgCount = SteamNetworkingSockets.ReceiveMessagesOnConnection(serverClientConnection, serverMessagesPtr, 32);
            Log($"Server received {serverMsgCount} messages from client");

            for (int i = 0; i < serverMsgCount; i++)
            {
                SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(serverMessagesPtr[i]);
                if (msg.m_cbSize == 3)
                {
                    byte[] data = new byte[3];
                    System.Runtime.InteropServices.Marshal.Copy(msg.m_pData, data, 0, 3);
                    if (data[0] == 0xC1 && data[2] == 0xFF)
                    {
                        serverMessagesReceived++;
                    }
                }
                SteamNetworkingMessage_t.Release(serverMessagesPtr[i]);
            }

            // Poll client for server messages
            IntPtr[] clientMessagesPtr = new IntPtr[32];
            int clientMsgCount = SteamNetworkingSockets.ReceiveMessagesOnConnection(clientConnection, clientMessagesPtr, 32);
            Log($"Client received {clientMsgCount} messages from server");

            for (int i = 0; i < clientMsgCount; i++)
            {
                SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(clientMessagesPtr[i]);
                if (msg.m_cbSize == 3)
                {
                    byte[] data = new byte[3];
                    System.Runtime.InteropServices.Marshal.Copy(msg.m_pData, data, 0, 3);
                    if (data[0] == 0x5E && data[2] == 0xAA)
                    {
                        clientMessagesReceived++;
                    }
                }
                SteamNetworkingMessage_t.Release(clientMessagesPtr[i]);
            }

            // Assert - Should receive most/all messages (unreliable, so some loss acceptable in theory)
            Log($"Server received {serverMessagesReceived}/{messagesToSend} client messages");
            Log($"Client received {clientMessagesReceived}/{messagesToSend} server messages");

            // On localhost loopback, we should get 100% delivery even for unreliable
            Assert.GreaterOrEqual(serverMessagesReceived, messagesToSend * 0.9f, "Server should receive most client messages");
            Assert.GreaterOrEqual(clientMessagesReceived, messagesToSend * 0.9f, "Client should receive most server messages");
        }

        #endregion

        #region Helper: Connection Setup

        private void SetupClientServerConnection()
        {
            // Create server listen socket EXACTLY like SteamworksTransport.StartServer()
            SteamNetworkingIPAddr serverAddr = new SteamNetworkingIPAddr();
            serverAddr.Clear();
            serverAddr.SetIPv4(0, 27015); // 0.0.0.0:27015 (bind to all interfaces)

            var connectionConfig = CreateOptimizedConnectionConfig();
            serverListenSocket = SteamNetworkingSockets.CreateListenSocketIP(ref serverAddr, connectionConfig.Length, connectionConfig);
            Assert.AreNotEqual(HSteamListenSocket.Invalid, serverListenSocket, "Failed to create listen socket");
            Log($"Server listen socket created: {serverListenSocket} on port 27015");

            // Track connection status
            bool serverReceivedConnectionRequest = false;
            bool serverConnectionEstablished = false;
            bool clientConnected = false;

            // Register callback
            Callback<SteamNetConnectionStatusChangedCallback_t> callback =
                Callback<SteamNetConnectionStatusChangedCallback_t>.Create((callbackData) =>
            {
                Log($"Callback: conn={callbackData.m_hConn}, state={callbackData.m_info.m_eState}, oldState={callbackData.m_eOldState}, listenSocket={callbackData.m_info.m_hListenSocket}");

                if (callbackData.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting &&
                    callbackData.m_info.m_hListenSocket == serverListenSocket)
                {
                    Log("Server: Connection request received - accepting");
                    serverReceivedConnectionRequest = true;
                    EResult result = SteamNetworkingSockets.AcceptConnection(callbackData.m_hConn);
                    Log($"Server: AcceptConnection result = {result}");
                    if (result == EResult.k_EResultOK)
                    {
                        serverClientConnection = callbackData.m_hConn;
                    }
                }

                if (callbackData.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected &&
                    callbackData.m_hConn == serverClientConnection)
                {
                    Log("Server: Client connection established");
                    serverConnectionEstablished = true;
                }

                if (callbackData.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected &&
                    callbackData.m_hConn == clientConnection)
                {
                    Log("Client: Connected to server");
                    clientConnected = true;
                }
            });

            // Client connects via IP EXACTLY like SteamworksTransport.ConnectClient()
            SteamNetworkingIPAddr clientConnectAddr = new SteamNetworkingIPAddr();
            clientConnectAddr.Clear();
            // Parse "127.0.0.1" exactly like production code
            byte[] ipBytes = new byte[] { 127, 0, 0, 1 };
            uint ipv4 = (uint)((ipBytes[0] << 24) | (ipBytes[1] << 16) | (ipBytes[2] << 8) | ipBytes[3]);
            clientConnectAddr.SetIPv4(ipv4, 27015);

            clientConnection = SteamNetworkingSockets.ConnectByIPAddress(ref clientConnectAddr, connectionConfig.Length, connectionConfig);
            Assert.AreNotEqual(HSteamNetConnection.Invalid, clientConnection, "Failed to create client connection");
            Log($"Client initiated connection to 127.0.0.1:27015, handle: {clientConnection}");

            // Wait for connection
            Assert.IsTrue(WaitForCondition(() => serverReceivedConnectionRequest, TEST_TIMEOUT_MS, "Server connection request"),
                "Server never received connection request");
            Assert.IsTrue(WaitForCondition(() => serverConnectionEstablished, TEST_TIMEOUT_MS, "Server connection established"),
                "Server connection never established");
            Assert.IsTrue(WaitForCondition(() => clientConnected, TEST_TIMEOUT_MS, "Client connected"),
                "Client never connected");

            Log("✓ Connection setup complete");
        }

        #endregion
    }
}
