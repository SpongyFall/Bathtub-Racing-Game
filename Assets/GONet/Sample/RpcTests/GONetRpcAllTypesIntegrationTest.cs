using GONet;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace GONet.Sample.RpcTests
{
    /// <summary>
    /// Comprehensive integration test for all RPC types (ServerRpc, ClientRpc, TargetRpc).
    /// Tests real-world patterns: request-response, async/await, exception handling, nested calls.
    ///
    /// Invoked via Shift+A keyboard shortcut.
    /// </summary>
    [RequireComponent(typeof(GONetParticipant))]
    public class GONetRpcAllTypesIntegrationTest : GONetParticipantCompanionBehaviour
    {
        #region RPC Execution Tracker

        private static readonly ConcurrentBag<string> rpcExecutionLog = new ConcurrentBag<string>();
        private static int currentTestId = -1;

        private static void LogRpcExecution(string rpcVariant, string messageWithTestId = null)
        {
            int testId = ExtractTestIdFromMessage(messageWithTestId);

            if (testId == -1)
            {
                testId = currentTestId;
            }

            if (testId == -1) return;

            if (currentTestId == -1)
            {
                currentTestId = testId;
            }

            string machine = GetMachineLabel();
            rpcExecutionLog.Add($"{testId}-{rpcVariant}|{machine}");
        }

        private static int ExtractTestIdFromMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return -1;

            int dashIndex = message.IndexOf('-');
            if (dashIndex == -1 || dashIndex == 0) return -1;

            string testIdStr = message.Substring(0, dashIndex);
            if (int.TryParse(testIdStr, out int testId))
            {
                return testId;
            }

            return -1;
        }

        /// <summary>
        /// Returns a human-readable label for the current machine role: "Host", "Server", or "Client:N".
        /// </summary>
        private static string GetMachineLabel()
        {
            if (GONetMain.IsHost) return "Host";
            if (GONetMain.IsServer) return "Server";
            return $"Client:{GONetMain.MyAuthorityId}";
        }

        private void DumpRpcExecutionSummary()
        {
            if (rpcExecutionLog.Count == 0)
            {
                // No executions recorded - don't create log file
                return;
            }

            EnsureLoggingProfileRegistered();

            var grouped = rpcExecutionLog.GroupBy(entry => entry.Split('|')[0])
                                          .OrderBy(g => g.Key);

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("========== ALL RPC TYPES INTEGRATION TEST SUMMARY ==========");

            foreach (var group in grouped)
            {
                string variant = group.Key;
                var machines = group.Select(entry => entry.Split('|')[1]).Distinct().OrderBy(m => m).ToList();
                int count = group.Count();
                sb.AppendLine($"{variant}: {count} executions on [{string.Join(", ", machines)}]");
            }

            sb.AppendLine($"Total executions: {rpcExecutionLog.Count}");
            sb.AppendLine("=============================================================");

            GONetLog.Info(sb.ToString(), myRpcLogTelemetryProfile);
        }

        string myRpcLogTelemetryProfile;
        private bool isLoggingProfileRegistered = false;

        /// <summary>
        /// Lazy initialization: only register logging profile when we actually need to log something.
        /// This prevents empty log files from being created when tests are not run.
        /// </summary>
        private void EnsureLoggingProfileRegistered()
        {
            if (!isLoggingProfileRegistered)
            {
                // Use file-safe label (no colon — Windows prohibits colons in file names)
                string fileLabel = GONetMain.IsHost ? "Host" : (GONetMain.IsServer ? "Server" : $"Client{GONetMain.MyAuthorityId}");
                myRpcLogTelemetryProfile = $"RpcAllTypes-{fileLabel}";
                GONetLog.RegisterLoggingProfile(new GONetLog.LoggingProfile(myRpcLogTelemetryProfile, outputToSeparateFile: true));
                isLoggingProfileRegistered = true;
            }
        }

        #endregion

        #region Unity Lifecycle

        protected override void Start()
        {
            base.Start();
            // Logging profile registration deferred until first actual log write
        }

        private void OnApplicationQuit()
        {
            DumpRpcExecutionSummary();
        }

        internal override void UpdateAfterGONetReady()
        {
            base.UpdateAfterGONetReady();

            bool shiftPressed = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            // Shift+K: Dump execution summary
            if (shiftPressed && Input.GetKeyDown(KeyCode.K))
            {
                DumpRpcExecutionSummary();
            }

            // Shift+A: Run ALL RPC integration tests (with bidirectional connectivity pre-check)
            if (shiftPressed && Input.GetKeyDown(KeyCode.A) && !_testRunInProgress)
            {
                EnsureLoggingProfileRegistered();
                GONetLog.Info("[GONetRpcAllTypesIntegrationTest] Running ALL RPC integration tests (Shift+A)...", myRpcLogTelemetryProfile);
                StartCoroutine(RunAllTestsWithConnectivityCheck());
            }
        }

        #endregion

        #region Connectivity Pre-Check

        private static volatile bool _connectivityPongReceived = false;
        private static volatile int _connectivityExpectedNonce = 0;
        private bool _testRunInProgress = false;

        [ServerRpc]
        internal void ServerRpc_ConnectivityPing(int nonce)
        {
            GONetLog.Debug($"[RpcAllTypes] Connectivity ping received on server, nonce={nonce}. Sending pong broadcast...", myRpcLogTelemetryProfile);
            CallRpc(nameof(ClientRpc_ConnectivityPong), nonce);
        }

        [ClientRpc]
        internal void ClientRpc_ConnectivityPong(int nonce)
        {
            GONetLog.Debug($"[RpcAllTypes] Connectivity pong received on client, nonce={nonce}, expected={_connectivityExpectedNonce}", myRpcLogTelemetryProfile);
            if (nonce == _connectivityExpectedNonce)
            {
                _connectivityPongReceived = true;
            }
        }

        /// <summary>
        /// Coroutine that verifies bidirectional connectivity (client->server->client round-trip)
        /// before running the test suite. This catches transport-level half-open connections where
        /// a client can send but not receive, which would cause all inbound RPC tests to fail.
        /// </summary>
        private IEnumerator RunAllTestsWithConnectivityCheck()
        {
            if (_testRunInProgress)
                yield break;

            _testRunInProgress = true;

            // Dedicated server (not a client): cannot initiate tests
            if (GONetMain.IsServer && !GONetMain.IsClient)
            {
                GONetLog.Info("[RpcAllTypes] Dedicated server ready for tests. Press Shift+A from CLIENT or HOST to initiate.", myRpcLogTelemetryProfile);
                _testRunInProgress = false;
                yield break;
            }

            // Client Host: skip connectivity check (host IS both client and server — no network round-trip needed)
            if (GONetMain.IsHost)
            {
                GONetLog.Info("[RpcAllTypes] Client Host detected — skipping connectivity check (host is both client and server).", myRpcLogTelemetryProfile);
                RunAllTests();
                GONetLog.Info("[GONetRpcAllTypesIntegrationTest] Completed ALL tests from Host. Press Shift+K to dump summary.", myRpcLogTelemetryProfile);
                _testRunInProgress = false;
                yield break;
            }

            // Pure client: full bidirectional connectivity check
            GONetLog.Info("[RpcAllTypes] Verifying bidirectional connectivity before running tests...", myRpcLogTelemetryProfile);

            const int maxRetries = 3;
            const float timeoutSeconds = 0.5f;
            bool connectivityConfirmed = false;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                int nonce = UnityEngine.Random.Range(10000, 99999);
                _connectivityExpectedNonce = nonce;
                _connectivityPongReceived = false;

                GONetLog.Info($"[RpcAllTypes] Connectivity check attempt {attempt}/{maxRetries} (nonce={nonce})...", myRpcLogTelemetryProfile);
                CallRpc(nameof(ServerRpc_ConnectivityPing), nonce);

                float elapsed = 0f;
                while (elapsed < timeoutSeconds && !_connectivityPongReceived)
                {
                    yield return null;
                    elapsed += Time.deltaTime;
                }

                if (_connectivityPongReceived)
                {
                    connectivityConfirmed = true;
                    GONetLog.Info($"[RpcAllTypes] Bidirectional connectivity confirmed on attempt {attempt} (nonce={nonce}).", myRpcLogTelemetryProfile);
                    break;
                }
                else
                {
                    GONetLog.Warning($"[RpcAllTypes] No pong received after {timeoutSeconds}s on attempt {attempt}/{maxRetries}.", myRpcLogTelemetryProfile);
                }
            }

            if (!connectivityConfirmed)
            {
                GONetLog.Error("[RpcAllTypes] CONNECTIVITY CHECK FAILED: This client can SEND to the server but is NOT RECEIVING responses.", myRpcLogTelemetryProfile);
                GONetLog.Error("[RpcAllTypes] This is a transport-level half-open connection. The client's outbound path works (ServerRpc delivered) but the inbound path is broken.", myRpcLogTelemetryProfile);
                GONetLog.Error("[RpcAllTypes] ABORTING test suite - results would be unreliable. Try restarting the client application.", myRpcLogTelemetryProfile);
                _testRunInProgress = false;
                yield break;
            }

            // Bidirectional connectivity confirmed - proceed with all tests
            RunAllTests();
            GONetLog.Info("[GONetRpcAllTypesIntegrationTest] Completed ALL tests. Press Shift+K to dump summary.", myRpcLogTelemetryProfile);
            _testRunInProgress = false;
        }

        #endregion

        #region Test Orchestration

        private void RunAllTests()
        {
            EnsureLoggingProfileRegistered();

            if (GONetMain.IsClient)
            {
                if (GONetMain.IsHost)
                {
                    GONetLog.Info("[RpcAllTypes] === HOST-INITIATED TEST SUITE ===", myRpcLogTelemetryProfile);
                    GONetLog.Info("[RpcAllTypes] The host wears both server and client hats.", myRpcLogTelemetryProfile);
                    GONetLog.Info("[RpcAllTypes] ServerRpc = local execution, ClientRpc = local + broadcast, TargetRpc = route-based", myRpcLogTelemetryProfile);
                }

                // Tests 1-10: Core RPC types (work from both pure client and host)
                InvokeTest_ServerRpc_Basic();
                InvokeTest_ServerRpc_Async();
                InvokeTest_ServerRpc_CallsClientRpc();
                InvokeTest_ClientRpc_BroadcastToAll();
                InvokeTest_TargetRpc_ToOwner();
                InvokeTest_TargetRpc_ToSpecificClient();
                InvokeTest_NestedRpcCalls();
                InvokeTest_MixedReliableUnreliable();
                InvokeTest_ExceptionHandling();
                InvokeTest_ServerRpcToTargetRpcResponse(); // Test 10: Critical fix verification

                // Host-specific verification tests (only when running as client host)
                if (GONetMain.IsHost)
                {
                    GONetLog.Info("[RpcAllTypes] Now running HOST-SPECIFIC VERIFICATION tests (H1-H5)...", myRpcLogTelemetryProfile);
                    InvokeTest_Host_ClientRpcSelfDelivery();     // H1: Host receives its own ClientRpc
                    InvokeTest_Host_TargetRpcToSelf();           // H2: Host can target itself
                    InvokeTest_Host_TargetRpcToRemoteOnly();     // H3: Host does NOT execute TargetRpc meant for remote
                    InvokeTest_Host_ServerRpcNoDoubleExec();     // H4: No double-execution of ServerRpc
                    InvokeTest_Host_ClientRpcNoDoubleExec();     // H5: No double-execution of ClientRpc
                }

                // Run RPC Inheritance Tests (Tests 11-16)
                GONetLog.Info("[RpcAllTypes] Now running RPC INHERITANCE tests (11-16)...", myRpcLogTelemetryProfile);
                RunAllInheritanceTests();

                // Schedule overall host verification after all async tests settle
                if (GONetMain.IsHost)
                {
                    ScheduleHostVerification();
                }
            }
            else if (GONetMain.IsServer)
            {
                GONetLog.Info("[RpcAllTypes] Dedicated server ready for tests. Press Shift+A from CLIENT to initiate.", myRpcLogTelemetryProfile);
            }
        }

        #endregion

        #region Test 1: Basic ServerRpc (Client → Server)

        [ServerRpc]
        internal void ServerRpc_Basic(string message)
        {
            LogRpcExecution("ServerRpc_Basic", message);
            GONetLog.Debug($"[RpcAllTypes] ServerRpc_Basic executed: {message}", myRpcLogTelemetryProfile);
        }

        public void InvokeTest_ServerRpc_Basic()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            string msg = $"{correlationId}-ServerRpc_Basic from {GetMachineLabel()}";
            CallRpc(nameof(ServerRpc_Basic), msg);
        }

        #endregion

        #region Test 2: ServerRpc with Async Return Value

        [ServerRpc]
        internal async Task<int> ServerRpc_AsyncResponse(string request, int value)
        {
            LogRpcExecution("ServerRpc_AsyncResponse", request);
            GONetLog.Debug($"[RpcAllTypes] ServerRpc_AsyncResponse executing: {request}, value={value}", myRpcLogTelemetryProfile);

            // Simulate async work
            await Task.Delay(10);

            int result = value * 2;
            GONetLog.Debug($"[RpcAllTypes] ServerRpc_AsyncResponse returning: {result}", myRpcLogTelemetryProfile);
            return result;
        }

        public async void InvokeTest_ServerRpc_Async()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            string msg = $"{correlationId}-ServerRpc_Async from {GetMachineLabel()}";

            int result = await CallRpcAsync<int, string, int>(nameof(ServerRpc_AsyncResponse), msg, 42);
            GONetLog.Info($"[RpcAllTypes] ServerRpc_Async returned: {result} (expected 84)", myRpcLogTelemetryProfile);

            if (result == 84)
            {
                GONetLog.Info("✅ PASS: ServerRpc async return value correct", myRpcLogTelemetryProfile);
            }
            else
            {
                GONetLog.Error($"❌ FAIL: ServerRpc async return value incorrect. Expected 84, got {result}", myRpcLogTelemetryProfile);
            }
        }

        #endregion

        #region Test 3: ServerRpc Calls ClientRpc (Request-Response Broadcast Pattern)

        [ServerRpc]
        internal void ServerRpc_ThenBroadcast(string request)
        {
            LogRpcExecution("ServerRpc_ThenBroadcast", request);
            GONetLog.Debug($"[RpcAllTypes] ServerRpc_ThenBroadcast received: {request}", myRpcLogTelemetryProfile);

            // Server processes request, then broadcasts result to all clients
            string response = $"Server processed: {request}";
            CallRpc(nameof(ClientRpc_BroadcastResponse), response);
        }

        [ClientRpc]
        internal void ClientRpc_BroadcastResponse(string response)
        {
            LogRpcExecution("ClientRpc_BroadcastResponse", response);
            GONetLog.Debug($"[RpcAllTypes] ClientRpc_BroadcastResponse executed: {response}", myRpcLogTelemetryProfile);
        }

        public void InvokeTest_ServerRpc_CallsClientRpc()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            string msg = $"{correlationId}-ServerRpc_CallsClientRpc from {GetMachineLabel()}";
            CallRpc(nameof(ServerRpc_ThenBroadcast), msg);
        }

        #endregion

        #region Test 4: ClientRpc Direct Broadcast (Server → All Clients)

        [ClientRpc]
        internal void ClientRpc_DirectBroadcast(string message, int data)
        {
            LogRpcExecution("ClientRpc_DirectBroadcast", message);
            GONetLog.Debug($"[RpcAllTypes] ClientRpc_DirectBroadcast executed: {message}, data={data}", myRpcLogTelemetryProfile);
        }

        public void InvokeTest_ClientRpc_BroadcastToAll()
        {
            if (GONetMain.IsServer)
            {
                int correlationId = UnityEngine.Random.Range(100, 999);
                currentTestId = correlationId;
                string msg = $"{correlationId}-ClientRpc_DirectBroadcast from {GetMachineLabel()}";
                CallRpc(nameof(ClientRpc_DirectBroadcast), msg, 999);
            }
            else
            {
                // Client triggers server to broadcast via ServerRpc
                CallRpc(nameof(ServerRpc_TriggerClientBroadcast));
            }
        }

        [ServerRpc(IsMineRequired = false)] // Allow any client to call
        internal void ServerRpc_TriggerClientBroadcast()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            string msg = $"{correlationId}-ClientRpc_DirectBroadcast triggered by client";
            CallRpc(nameof(ClientRpc_DirectBroadcast), msg, 888);
        }

        #endregion

        #region Test 5: TargetRpc to Owner

        [TargetRpc(RpcTarget.Owner)]
        internal void TargetRpc_ToOwner(string message)
        {
            LogRpcExecution("TargetRpc_ToOwner", message);
            GONetLog.Debug($"[RpcAllTypes] TargetRpc_ToOwner executed: {message}", myRpcLogTelemetryProfile);
        }

        public void InvokeTest_TargetRpc_ToOwner()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            string msg = $"{correlationId}-TargetRpc_ToOwner from {GetMachineLabel()}";
            CallRpc(nameof(TargetRpc_ToOwner), msg);
        }

        #endregion

        #region Test 6: TargetRpc to Specific Client

        [TargetRpc(RpcTarget.SpecificAuthority)]
        internal void TargetRpc_ToSpecificClient(ushort targetClientId, string message)
        {
            LogRpcExecution("TargetRpc_ToSpecificClient", message);
            GONetLog.Debug($"[RpcAllTypes] TargetRpc_ToSpecificClient executed: {message} (target was {targetClientId})", myRpcLogTelemetryProfile);
        }

        public void InvokeTest_TargetRpc_ToSpecificClient()
        {
            // Send message to Client:1 specifically
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            string msg = $"{correlationId}-TargetRpc_ToSpecificClient from {GetMachineLabel()} targeting Client:1";
            CallRpc(nameof(TargetRpc_ToSpecificClient), (ushort)1, msg);
        }

        #endregion

        #region Test 7: Nested RPC Calls (ServerRpc → ClientRpc → TargetRpc chain)

        [ServerRpc]
        internal void ServerRpc_StartChain(string message)
        {
            LogRpcExecution("ServerRpc_StartChain", message);
            GONetLog.Debug($"[RpcAllTypes] ServerRpc_StartChain executing: {message}", myRpcLogTelemetryProfile);

            // Call ClientRpc from ServerRpc
            CallRpc(nameof(ClientRpc_MiddleChain), $"{message} -> ClientRpc");
        }

        [ClientRpc]
        internal void ClientRpc_MiddleChain(string message)
        {
            LogRpcExecution("ClientRpc_MiddleChain", message);
            GONetLog.Debug($"[RpcAllTypes] ClientRpc_MiddleChain executing: {message}", myRpcLogTelemetryProfile);

            // Call TargetRpc from ClientRpc (client-side continuation of chain, including host-as-client)
            if (GONetMain.IsClient)
            {
                CallRpc(nameof(TargetRpc_EndChain), $"{message} -> TargetRpc");
            }
        }

        [TargetRpc(RpcTarget.Owner)]
        internal void TargetRpc_EndChain(string message)
        {
            LogRpcExecution("TargetRpc_EndChain", message);
            GONetLog.Debug($"[RpcAllTypes] TargetRpc_EndChain executing: {message}", myRpcLogTelemetryProfile);
        }

        public void InvokeTest_NestedRpcCalls()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            string msg = $"{correlationId}-NestedRpcCalls from {GetMachineLabel()}";
            CallRpc(nameof(ServerRpc_StartChain), msg);
        }

        #endregion

        #region Test 8: Mixed Reliable/Unreliable RPCs

        [ServerRpc(IsReliable = true)]
        internal void ServerRpc_Reliable(string message)
        {
            LogRpcExecution("ServerRpc_Reliable", message);
            GONetLog.Debug($"[RpcAllTypes] ServerRpc_Reliable executed (reliable): {message}", myRpcLogTelemetryProfile);
        }

        [ServerRpc(IsReliable = false)]
        internal void ServerRpc_Unreliable(string message)
        {
            LogRpcExecution("ServerRpc_Unreliable", message);
            GONetLog.Debug($"[RpcAllTypes] ServerRpc_Unreliable executed (unreliable): {message}", myRpcLogTelemetryProfile);
        }

        [ClientRpc(IsReliable = false)]
        internal void ClientRpc_Unreliable(string message)
        {
            LogRpcExecution("ClientRpc_Unreliable", message);
            GONetLog.Debug($"[RpcAllTypes] ClientRpc_Unreliable executed (unreliable): {message}", myRpcLogTelemetryProfile);
        }

        public void InvokeTest_MixedReliableUnreliable()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;

            // Test reliable ServerRpc
            CallRpc(nameof(ServerRpc_Reliable), $"{correlationId}-Reliable from {GetMachineLabel()}");

            // Test unreliable ServerRpc
            CallRpc(nameof(ServerRpc_Unreliable), $"{correlationId}-Unreliable from {GetMachineLabel()}");

            // Trigger unreliable ClientRpc via ServerRpc
            CallRpc(nameof(ServerRpc_TriggerUnreliableClient));
        }

        [ServerRpc(IsMineRequired = false)]
        internal void ServerRpc_TriggerUnreliableClient()
        {
            int correlationId = currentTestId;
            CallRpc(nameof(ClientRpc_Unreliable), $"{correlationId}-UnreliableClient from Server");
        }

        #endregion

        #region Test 9: Exception Handling in RPC Methods

        [ServerRpc]
        internal void ServerRpc_ThrowsException(string message)
        {
            LogRpcExecution("ServerRpc_ThrowsException_BEFORE", message);
            GONetLog.Debug($"[RpcAllTypes] ServerRpc_ThrowsException executing: {message}", myRpcLogTelemetryProfile);

            // Intentionally throw exception
            throw new System.InvalidOperationException($"Test exception in ServerRpc: {message}");
        }

        [ClientRpc]
        internal void ClientRpc_ThrowsException(string message)
        {
            LogRpcExecution("ClientRpc_ThrowsException_BEFORE", message);
            GONetLog.Debug($"[RpcAllTypes] ClientRpc_ThrowsException executing: {message}", myRpcLogTelemetryProfile);

            // Intentionally throw exception
            throw new System.InvalidOperationException($"Test exception in ClientRpc: {message}");
        }

        public void InvokeTest_ExceptionHandling()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;

            GONetLog.Info($"[RpcAllTypes] Testing exception handling - expect error logs below (this is intentional)", myRpcLogTelemetryProfile);

            // Test ServerRpc exception
            try
            {
                CallRpc(nameof(ServerRpc_ThrowsException), $"{correlationId}-ExceptionTest from {GetMachineLabel()}");
            }
            catch (System.Exception ex)
            {
                GONetLog.Info($"✅ PASS: Caught exception from ServerRpc: {ex.Message}", myRpcLogTelemetryProfile);
            }

            // Trigger ClientRpc exception via ServerRpc
            CallRpc(nameof(ServerRpc_TriggerExceptionClient));
        }

        [ServerRpc(IsMineRequired = false)]
        internal void ServerRpc_TriggerExceptionClient()
        {
            int correlationId = currentTestId;
            try
            {
                CallRpc(nameof(ClientRpc_ThrowsException), $"{correlationId}-ClientExceptionTest from Server");
            }
            catch (System.Exception ex)
            {
                GONetLog.Info($"✅ PASS: Caught exception from ClientRpc: {ex.Message}", myRpcLogTelemetryProfile);
            }
        }

        #endregion

        #region Test 10: ServerRpc → TargetRpc Response Pattern (Request-Response to Same Client)

        /// <summary>
        /// This test verifies the critical fix for ServerRpc→TargetRpc response patterns.
        /// Previously, when a client called a ServerRpc and the server responded with a TargetRpc
        /// targeting the SAME client, the response was incorrectly skipped because GONet assumed
        /// the client "already executed locally" (which was only true for the original ServerRpc,
        /// NOT for the response TargetRpc).
        ///
        /// This test mimics the exact pattern used in ItemNetCode.ServerRequestItemSync/ClientSyncItem.
        /// </summary>
        private static bool test10ResponseReceived = false;
        private static string test10ResponseData = null;

        [ServerRpc(IsMineRequired = false)]
        internal void ServerRpc_RequestSyncData(ushort requestorAuthority, string requestData)
        {
            LogRpcExecution("ServerRpc_RequestSyncData", requestData);
            GONetLog.Debug($"[RpcAllTypes] ServerRpc_RequestSyncData received from authority {requestorAuthority}: {requestData}", myRpcLogTelemetryProfile);

            // Simulate server processing the request and preparing a response
            string responseData = $"ServerResponse:{requestData}:ProcessedAt:{GONetMain.Time.ElapsedTicks}";

            // CRITICAL: This is the exact pattern that was broken - server responds to the requesting client
            // with a TargetRpc. Previously this was incorrectly skipped.
            GONetLog.Debug($"[RpcAllTypes] Server sending TargetRpc response back to authority {requestorAuthority}", myRpcLogTelemetryProfile);
            CallRpc(nameof(TargetRpc_SyncDataResponse), requestorAuthority, responseData);
        }

        [TargetRpc(RpcTarget.SpecificAuthority)]
        internal void TargetRpc_SyncDataResponse(ushort targetAuthority, string responseData)
        {
            LogRpcExecution("TargetRpc_SyncDataResponse", responseData);
            GONetLog.Debug($"[RpcAllTypes] TargetRpc_SyncDataResponse received! targetAuthority={targetAuthority}, MyAuthorityId={GONetMain.MyAuthorityId}", myRpcLogTelemetryProfile);
            GONetLog.Debug($"[RpcAllTypes] Response data: {responseData}", myRpcLogTelemetryProfile);

            // Track that we received the response
            test10ResponseReceived = true;
            test10ResponseData = responseData;

            // Verify this is the correct recipient
            if (targetAuthority == GONetMain.MyAuthorityId)
            {
                GONetLog.Info($"✅ PASS: ServerRpc→TargetRpc response pattern works! Client {GONetMain.MyAuthorityId} received response: {responseData}", myRpcLogTelemetryProfile);
            }
            else
            {
                GONetLog.Error($"❌ FAIL: Received response meant for authority {targetAuthority} but we are {GONetMain.MyAuthorityId}", myRpcLogTelemetryProfile);
            }
        }

        public void InvokeTest_ServerRpcToTargetRpcResponse()
        {
            test10ResponseReceived = false;
            test10ResponseData = null;

            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            string msg = $"{correlationId}-RequestSyncData from {GetMachineLabel()}";

            GONetLog.Info($"[RpcAllTypes] Test 10: Testing ServerRpc→TargetRpc response pattern (the ItemNetCode fix)", myRpcLogTelemetryProfile);
            GONetLog.Info($"[RpcAllTypes] {GetMachineLabel()} sending request to server...", myRpcLogTelemetryProfile);

            // Client sends request to server, server should respond with TargetRpc to THIS client
            CallRpc(nameof(ServerRpc_RequestSyncData), GONetMain.MyAuthorityId, msg);

            // Schedule a delayed check to verify response was received
            // (In a real test framework this would be an async assertion)
            DelayedResponseCheck(correlationId);
        }

        private async void DelayedResponseCheck(int expectedCorrelationId)
        {
            // Wait a bit for the response to arrive
            await System.Threading.Tasks.Task.Delay(500);

            if (test10ResponseReceived)
            {
                if (test10ResponseData != null && test10ResponseData.Contains(expectedCorrelationId.ToString()))
                {
                    GONetLog.Info($"✅ PASS: Test 10 complete - ServerRpc→TargetRpc response pattern verified!", myRpcLogTelemetryProfile);
                }
                else
                {
                    GONetLog.Warning($"⚠️ WARNING: Response received but correlation ID mismatch. Expected {expectedCorrelationId}, got: {test10ResponseData}", myRpcLogTelemetryProfile);
                }
            }
            else
            {
                GONetLog.Error($"❌ FAIL: Test 10 - No response received after 500ms! The ServerRpc→TargetRpc response was likely skipped.", myRpcLogTelemetryProfile);
                GONetLog.Error($"❌ This indicates the bug where server responses to the requesting client are incorrectly dropped.", myRpcLogTelemetryProfile);
            }
        }

        #endregion

        #region Test 11-16: RPC INHERITANCE TESTS

        /*
         * ====================================================================
         * RPC INHERITANCE TEST PLAN
         * ====================================================================
         *
         * These tests verify that RPC methods defined in a base class can be
         * called from derived class instances WITHOUT requiring the derived
         * class to override and re-attribute the methods.
         *
         * SCENARIO: Common game development pattern
         *   Item (base) -> Weapon -> Gun
         *   Item defines TryUse() as [ServerRpc]
         *   Gun doesn't override TryUse() but needs to call it
         *
         * TEST MATRIX:
         * +---------+----------------+------------------+-------------------+
         * | Test    | RPC Type       | Inheritance      | Expected Result   |
         * +---------+----------------+------------------+-------------------+
         * | 11      | ServerRpc      | Not overridden   | Executes on server|
         * | 12      | ClientRpc      | Not overridden   | Executes on client|
         * | 13      | TargetRpc      | Not overridden   | Executes on target|
         * | 14      | ServerRpc      | Multi-level      | A->B->C works     |
         * | 15      | Mixed          | Some overridden  | Correct dispatch  |
         * | 16      | Async ServerRpc| Not overridden   | Returns correctly |
         * +---------+----------------+------------------+-------------------+
         *
         * SUCCESS CRITERIA:
         * - No "No RPC metadata found" warnings
         * - Each RPC executes on correct machine(s)
         * - Return values work correctly for async RPCs
         */

        // Track inheritance test results
        private static readonly ConcurrentDictionary<string, bool> inheritanceTestResults = new ConcurrentDictionary<string, bool>();

        /// <summary>
        /// Test 11: ServerRpc inherited from base class (not overridden)
        /// </summary>
        public void InvokeTest_Inheritance_ServerRpc_NotOverridden()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            inheritanceTestResults[$"Test11-{correlationId}"] = false;

            GONetLog.Info($"[RpcInheritance] Test 11: Calling inherited ServerRpc from derived class instance", myRpcLogTelemetryProfile);
            GONetLog.Info($"[RpcInheritance] Calling BaseClass_ServerRpc_UseItem from RpcInheritanceTestDerived", myRpcLogTelemetryProfile);

            // The derived instance calls a ServerRpc defined ONLY in the base class
            // This should work WITHOUT the derived class having to override it
            inheritedRpcTestInstance?.TestInheritedServerRpc(correlationId);
        }

        /// <summary>
        /// Test 12: ClientRpc inherited from base class (not overridden)
        /// </summary>
        public void InvokeTest_Inheritance_ClientRpc_NotOverridden()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            inheritanceTestResults[$"Test12-{correlationId}"] = false;

            GONetLog.Info($"[RpcInheritance] Test 12: Calling inherited ClientRpc from derived class instance", myRpcLogTelemetryProfile);

            // Trigger server to call ClientRpc via the derived instance (host can also initiate)
            if (GONetMain.IsClient)
            {
                inheritedRpcTestInstance?.TestInheritedClientRpc(correlationId);
            }
        }

        /// <summary>
        /// Test 13: TargetRpc inherited from base class (not overridden)
        /// </summary>
        public void InvokeTest_Inheritance_TargetRpc_NotOverridden()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            inheritanceTestResults[$"Test13-{correlationId}"] = false;

            GONetLog.Info($"[RpcInheritance] Test 13: Calling inherited TargetRpc from derived class instance", myRpcLogTelemetryProfile);

            inheritedRpcTestInstance?.TestInheritedTargetRpc(correlationId);
        }

        /// <summary>
        /// Test 14: Multi-level inheritance (A -> B -> C, calling A's RPC from C)
        /// </summary>
        public void InvokeTest_Inheritance_MultiLevel()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            inheritanceTestResults[$"Test14-{correlationId}"] = false;

            GONetLog.Info($"[RpcInheritance] Test 14: Multi-level inheritance (Grandchild calling Grandparent's RPC)", myRpcLogTelemetryProfile);

            multiLevelInheritanceTestInstance?.TestMultiLevelInheritedServerRpc(correlationId);
        }

        /// <summary>
        /// Test 15: Mix of overridden and non-overridden RPCs on same class
        /// </summary>
        public void InvokeTest_Inheritance_MixedOverridePattern()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            inheritanceTestResults[$"Test15a-{correlationId}"] = false;
            inheritanceTestResults[$"Test15b-{correlationId}"] = false;

            GONetLog.Info($"[RpcInheritance] Test 15: Mix of overridden and non-overridden RPCs", myRpcLogTelemetryProfile);

            mixedOverrideTestInstance?.TestMixedOverridePattern(correlationId);
        }

        /// <summary>
        /// Test 16: Async ServerRpc with return value, inherited
        /// </summary>
        public async void InvokeTest_Inheritance_AsyncServerRpc_NotOverridden()
        {
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;
            inheritanceTestResults[$"Test16-{correlationId}"] = false;

            GONetLog.Info($"[RpcInheritance] Test 16: Async ServerRpc inherited (with return value)", myRpcLogTelemetryProfile);

            if (inheritedRpcTestInstance != null)
            {
                int result = await inheritedRpcTestInstance.TestInheritedAsyncServerRpc(correlationId);

                if (result == correlationId * 2)
                {
                    inheritanceTestResults[$"Test16-{correlationId}"] = true;
                    GONetLog.Info($"✅ PASS Test 16: Inherited async ServerRpc returned correct value: {result} (expected {correlationId * 2})", myRpcLogTelemetryProfile);
                }
                else
                {
                    GONetLog.Error($"❌ FAIL Test 16: Inherited async ServerRpc returned wrong value: {result} (expected {correlationId * 2})", myRpcLogTelemetryProfile);
                }
            }
        }

        /// <summary>
        /// Run all inheritance tests
        /// </summary>
        public void RunAllInheritanceTests()
        {
            EnsureLoggingProfileRegistered();

            GONetLog.Info("=======================================================", myRpcLogTelemetryProfile);
            GONetLog.Info("[RpcInheritance] STARTING RPC INHERITANCE TEST SUITE", myRpcLogTelemetryProfile);
            GONetLog.Info("=======================================================", myRpcLogTelemetryProfile);

            if (!ValidateTestInstances())
            {
                GONetLog.Error("[RpcInheritance] ❌ Test instances not found! Make sure RpcInheritanceTestDerived and RpcInheritanceTestGrandchild components are attached.", myRpcLogTelemetryProfile);
                return;
            }

            // Run all inheritance tests
            InvokeTest_Inheritance_ServerRpc_NotOverridden();
            InvokeTest_Inheritance_ClientRpc_NotOverridden();
            InvokeTest_Inheritance_TargetRpc_NotOverridden();
            InvokeTest_Inheritance_MultiLevel();
            InvokeTest_Inheritance_MixedOverridePattern();
            InvokeTest_Inheritance_AsyncServerRpc_NotOverridden();

            // Schedule summary after tests complete
            ScheduleInheritanceTestSummary();
        }

        private async void ScheduleInheritanceTestSummary()
        {
            await Task.Delay(2000); // Wait for all tests to complete
            DumpInheritanceTestSummary();
        }

        private void DumpInheritanceTestSummary()
        {
            EnsureLoggingProfileRegistered();

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("=======================================================");
            sb.AppendLine("      RPC INHERITANCE TEST SUMMARY");
            sb.AppendLine("=======================================================");

            int passed = 0;
            int failed = 0;

            foreach (var kvp in inheritanceTestResults.OrderBy(x => x.Key))
            {
                string status = kvp.Value ? "✅ PASS" : "❌ FAIL";
                sb.AppendLine($"  {status}: {kvp.Key}");
                if (kvp.Value) passed++; else failed++;
            }

            sb.AppendLine("-------------------------------------------------------");
            sb.AppendLine($"  TOTAL: {passed} passed, {failed} failed, {passed + failed} total");
            sb.AppendLine("=======================================================");

            if (failed == 0 && passed > 0)
            {
                sb.AppendLine("  🎉 ALL RPC INHERITANCE TESTS PASSED! 🎉");
            }
            else if (passed == 0)
            {
                sb.AppendLine("  ⚠️ NO TESTS EXECUTED - Check test instances");
            }
            else
            {
                sb.AppendLine("  ⚠️ SOME TESTS FAILED - See log for details");
            }

            sb.AppendLine("=======================================================");

            GONetLog.Info(sb.ToString(), myRpcLogTelemetryProfile);
        }

        // Test instance references - set these in inspector or find dynamically
        private RpcInheritanceTestDerived inheritedRpcTestInstance;
        private RpcInheritanceTestGrandchild multiLevelInheritanceTestInstance;
        private RpcInheritanceTestMixedOverride mixedOverrideTestInstance;

        private bool ValidateTestInstances()
        {
            // Try to find test instances if not set
            if (inheritedRpcTestInstance == null)
                inheritedRpcTestInstance = FindObjectOfType<RpcInheritanceTestDerived>();

            if (multiLevelInheritanceTestInstance == null)
                multiLevelInheritanceTestInstance = FindObjectOfType<RpcInheritanceTestGrandchild>();

            if (mixedOverrideTestInstance == null)
                mixedOverrideTestInstance = FindObjectOfType<RpcInheritanceTestMixedOverride>();

            bool valid = inheritedRpcTestInstance != null &&
                         multiLevelInheritanceTestInstance != null &&
                         mixedOverrideTestInstance != null;

            if (!valid)
            {
                GONetLog.Warning($"[RpcInheritance] Missing test instances: " +
                    $"Derived={inheritedRpcTestInstance != null}, " +
                    $"Grandchild={multiLevelInheritanceTestInstance != null}, " +
                    $"MixedOverride={mixedOverrideTestInstance != null}", myRpcLogTelemetryProfile);
            }

            return valid;
        }

        /// <summary>
        /// Called by test classes to report success
        /// </summary>
        public static void ReportTestSuccess(string testKey)
        {
            inheritanceTestResults[testKey] = true;
        }

        /// <summary>
        /// Check if a test was reported as successful
        /// </summary>
        public static bool IsTestSuccessful(string testKey)
        {
            return inheritanceTestResults.TryGetValue(testKey, out bool result) && result;
        }

        #endregion

        #region Host-Specific Verification Tests (H1-H5) and Post-Test Integrity Check

        /*
         * ====================================================================
         * HOST-SPECIFIC VERIFICATION TESTS
         * ====================================================================
         *
         * These tests verify correct RPC behavior when running as a Client Host
         * (listen-server where IsServer && IsClient are both true).
         *
         * The host wears two hats (server + client), so we must verify:
         * - RPCs execute the correct number of times (no double-execution)
         * - TargetRpcs only reach intended targets (host doesn't receive what's meant for remote clients)
         * - ClientRpcs reach the host's client side (self-delivery)
         * - TargetRpcs can target the host itself
         *
         * These tests ONLY run when initiated from the Client Host (IsHost == true).
         */

        #region H1: Host ClientRpc Self-Delivery

        private static volatile bool _hostClientRpcSelfDelivered = false;

        [ClientRpc]
        internal void ClientRpc_HostSelfDeliveryCheck(string message)
        {
            LogRpcExecution("ClientRpc_HostSelfDeliveryCheck", message);
            if (GONetMain.IsHost)
            {
                _hostClientRpcSelfDelivered = true;
            }
        }

        public void InvokeTest_Host_ClientRpcSelfDelivery()
        {
            _hostClientRpcSelfDelivered = false;
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;

            GONetLog.Info($"[RpcAllTypes] Test H1: Verifying host receives its own ClientRpc broadcast", myRpcLogTelemetryProfile);
            CallRpc(nameof(ClientRpc_HostSelfDeliveryCheck), $"{correlationId}-HostSelfDelivery");

            // ClientRpc on host: HandleClientRpc executes locally (synchronous) before broadcast
            if (_hostClientRpcSelfDelivered)
            {
                GONetLog.Info("✅ PASS: Test H1 — ClientRpc self-delivery confirmed (synchronous local execution)", myRpcLogTelemetryProfile);
            }
            else
            {
                // In case local execution is deferred, schedule a delayed check
                DelayedHostSelfDeliveryCheck();
            }
        }

        private async void DelayedHostSelfDeliveryCheck()
        {
            await Task.Delay(500);
            if (_hostClientRpcSelfDelivered)
            {
                GONetLog.Info("✅ PASS: Test H1 — ClientRpc self-delivery confirmed (delayed)", myRpcLogTelemetryProfile);
            }
            else
            {
                GONetLog.Error("❌ FAIL: Test H1 — Host did NOT receive its own ClientRpc! The host's client hat was not reached.", myRpcLogTelemetryProfile);
            }
        }

        #endregion

        #region H2: Host TargetRpc to Self

        private static volatile bool _hostTargetRpcSelfReceived = false;

        [TargetRpc(RpcTarget.SpecificAuthority)]
        internal void TargetRpc_HostSelfTarget(ushort targetAuthority, string message)
        {
            LogRpcExecution("TargetRpc_HostSelfTarget", message);
            if (GONetMain.IsHost && targetAuthority == GONetMain.MyAuthorityId)
            {
                _hostTargetRpcSelfReceived = true;
            }
        }

        public void InvokeTest_Host_TargetRpcToSelf()
        {
            _hostTargetRpcSelfReceived = false;
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;

            GONetLog.Info($"[RpcAllTypes] Test H2: Verifying host can target itself with TargetRpc (authority={GONetMain.MyAuthorityId})", myRpcLogTelemetryProfile);
            CallRpc(nameof(TargetRpc_HostSelfTarget), GONetMain.MyAuthorityId, $"{correlationId}-HostSelfTarget");

            // TargetRpc on host targeting self: HandleTargetRpc detects server IS the target, executes locally
            if (_hostTargetRpcSelfReceived)
            {
                GONetLog.Info("✅ PASS: Test H2 — TargetRpc self-targeting confirmed (synchronous local execution)", myRpcLogTelemetryProfile);
            }
            else
            {
                DelayedHostSelfTargetCheck();
            }
        }

        private async void DelayedHostSelfTargetCheck()
        {
            await Task.Delay(500);
            if (_hostTargetRpcSelfReceived)
            {
                GONetLog.Info("✅ PASS: Test H2 — TargetRpc self-targeting confirmed (delayed)", myRpcLogTelemetryProfile);
            }
            else
            {
                GONetLog.Error("❌ FAIL: Test H2 — Host did NOT receive TargetRpc targeting itself!", myRpcLogTelemetryProfile);
            }
        }

        #endregion

        #region H3: Host TargetRpc to Remote Only (Negative Test)

        private static volatile bool _hostTargetRpcRemoteOnlyUnexpectedExec = false;

        [TargetRpc(RpcTarget.SpecificAuthority)]
        internal void TargetRpc_RemoteOnlyCheck(ushort targetAuthority, string message)
        {
            LogRpcExecution("TargetRpc_RemoteOnlyCheck", message);
            if (GONetMain.IsHost && targetAuthority != GONetMain.MyAuthorityId)
            {
                _hostTargetRpcRemoteOnlyUnexpectedExec = true;
                GONetLog.Error($"❌ FAIL: Test H3 — Host INCORRECTLY executed TargetRpc meant for authority {targetAuthority}! Host authority is {GONetMain.MyAuthorityId}", myRpcLogTelemetryProfile);
            }
        }

        public void InvokeTest_Host_TargetRpcToRemoteOnly()
        {
            _hostTargetRpcRemoteOnlyUnexpectedExec = false;
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;

            // Pick a target authority that is NOT the host
            ushort remoteTarget = 1;
            if (GONetMain.MyAuthorityId == remoteTarget)
            {
                remoteTarget = 2;
            }

            GONetLog.Info($"[RpcAllTypes] Test H3: Verifying host does NOT execute TargetRpc meant for authority {remoteTarget} (host is {GONetMain.MyAuthorityId})", myRpcLogTelemetryProfile);
            CallRpc(nameof(TargetRpc_RemoteOnlyCheck), remoteTarget, $"{correlationId}-RemoteOnlyCheck targeting {remoteTarget}");

            // The TargetRpc should route to the remote client, NOT execute locally on host.
            // Schedule a delayed verify to give time for any erroneous execution to surface.
            DelayedHostRemoteOnlyCheck();
        }

        private async void DelayedHostRemoteOnlyCheck()
        {
            await Task.Delay(500);
            if (!_hostTargetRpcRemoteOnlyUnexpectedExec)
            {
                GONetLog.Info("✅ PASS: Test H3 — Host correctly did NOT execute TargetRpc meant for a remote client", myRpcLogTelemetryProfile);
            }
            // Failure already logged in the RPC method itself
        }

        #endregion

        #region H4: ServerRpc No Double Execution

        private static int _hostServerRpcExecCount = 0;

        [ServerRpc]
        internal void ServerRpc_HostDoubleExecCheck(string message)
        {
            LogRpcExecution("ServerRpc_HostDoubleExecCheck", message);
            System.Threading.Interlocked.Increment(ref _hostServerRpcExecCount);
        }

        public void InvokeTest_Host_ServerRpcNoDoubleExec()
        {
            _hostServerRpcExecCount = 0;
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;

            GONetLog.Info("[RpcAllTypes] Test H4: Verifying ServerRpc executes exactly once on host (no double-execution)", myRpcLogTelemetryProfile);
            CallRpc(nameof(ServerRpc_HostDoubleExecCheck), $"{correlationId}-HostServerRpcDoubleExecCheck");

            // Void ServerRpc on host: HandleServerRpc checks IsServer → true → ExecuteRpcLocally (synchronous)
            if (_hostServerRpcExecCount == 1)
            {
                GONetLog.Info("✅ PASS: Test H4 — ServerRpc executed exactly once on host", myRpcLogTelemetryProfile);
            }
            else if (_hostServerRpcExecCount == 0)
            {
                GONetLog.Error("❌ FAIL: Test H4 — ServerRpc did NOT execute on host (count=0)", myRpcLogTelemetryProfile);
            }
            else
            {
                GONetLog.Error($"❌ FAIL: Test H4 — ServerRpc DOUBLE EXECUTED on host! Count={_hostServerRpcExecCount}", myRpcLogTelemetryProfile);
            }
        }

        #endregion

        #region H5: ClientRpc No Double Execution

        private static int _hostClientRpcExecCount = 0;

        [ClientRpc]
        internal void ClientRpc_HostDoubleExecCheck(string message)
        {
            LogRpcExecution("ClientRpc_HostDoubleExecCheck", message);
            if (GONetMain.IsHost)
            {
                System.Threading.Interlocked.Increment(ref _hostClientRpcExecCount);
            }
        }

        public void InvokeTest_Host_ClientRpcNoDoubleExec()
        {
            _hostClientRpcExecCount = 0;
            int correlationId = UnityEngine.Random.Range(100, 999);
            currentTestId = correlationId;

            GONetLog.Info("[RpcAllTypes] Test H5: Verifying ClientRpc executes exactly once on host (no double-execution)", myRpcLogTelemetryProfile);
            CallRpc(nameof(ClientRpc_HostDoubleExecCheck), $"{correlationId}-HostClientRpcDoubleExecCheck");

            // ClientRpc on host: HandleClientRpc checks IsServer → IsClient → ExecuteRpcLocally (synchronous), then broadcasts.
            // The host should execute locally exactly once (client hat), NOT also on receive-side.
            if (_hostClientRpcExecCount == 1)
            {
                GONetLog.Info("✅ PASS: Test H5 — ClientRpc executed exactly once on host (synchronous)", myRpcLogTelemetryProfile);
            }
            else
            {
                DelayedHostClientRpcDoubleExecCheck();
            }
        }

        private async void DelayedHostClientRpcDoubleExecCheck()
        {
            await Task.Delay(500);
            if (_hostClientRpcExecCount == 1)
            {
                GONetLog.Info("✅ PASS: Test H5 — ClientRpc executed exactly once on host", myRpcLogTelemetryProfile);
            }
            else if (_hostClientRpcExecCount == 0)
            {
                GONetLog.Error("❌ FAIL: Test H5 — ClientRpc did NOT execute on host! (count=0)", myRpcLogTelemetryProfile);
            }
            else
            {
                GONetLog.Error($"❌ FAIL: Test H5 — ClientRpc DOUBLE EXECUTED on host! Count={_hostClientRpcExecCount}", myRpcLogTelemetryProfile);
            }
        }

        #endregion

        #region Host Verification Summary

        /// <summary>
        /// Schedules a post-test integrity check that scans the execution log for anomalies
        /// specific to Client Host mode (double-executions, unexpected receptions, etc.).
        /// </summary>
        private async void ScheduleHostVerification()
        {
            await Task.Delay(3000); // Wait for all tests including async responses to settle
            VerifyHostExecutionIntegrity();
        }

        private void VerifyHostExecutionIntegrity()
        {
            EnsureLoggingProfileRegistered();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== HOST MODE EXECUTION INTEGRITY CHECK ==========");

            // Collect all host-side executions
            var hostEntries = rpcExecutionLog.Where(e => e.Contains("|Host")).ToList();

            // Check for double-executions: same testId-variant appearing more than once on Host
            var grouped = hostEntries.GroupBy(e => e.Split('|')[0]).OrderBy(g => g.Key);

            int doubleExecIssues = 0;
            foreach (var group in grouped)
            {
                if (group.Count() > 1)
                {
                    sb.AppendLine($"  ⚠️ DOUBLE EXECUTION: {group.Key} executed {group.Count()} times on Host");
                    doubleExecIssues++;
                }
            }

            if (doubleExecIssues == 0)
            {
                sb.AppendLine("  ✅ No double-executions detected on Host");
            }
            else
            {
                sb.AppendLine($"  ❌ {doubleExecIssues} double-execution(s) detected!");
            }

            // Check for TargetRpc entries that should NOT have executed on host
            var targetRpcHostEntries = hostEntries.Where(e => e.Contains("TargetRpc_RemoteOnlyCheck")).ToList();
            if (targetRpcHostEntries.Count > 0)
            {
                sb.AppendLine($"  ❌ UNEXPECTED: Host executed {targetRpcHostEntries.Count} TargetRpc(s) meant for remote clients");
            }
            else
            {
                sb.AppendLine("  ✅ No unexpected TargetRpc executions on Host");
            }

            sb.AppendLine($"  Total Host-side executions: {hostEntries.Count}");
            sb.AppendLine("==========================================================");

            GONetLog.Info(sb.ToString(), myRpcLogTelemetryProfile);
        }

        #endregion

        #endregion
    }

    // NOTE: RPC Inheritance Test Classes are now in separate files:
    // - RpcInheritanceTestBase.cs
    // - RpcInheritanceTestDerived.cs
    // - RpcInheritanceTestMiddle.cs
    // - RpcInheritanceTestGrandchild.cs
    // - RpcInheritanceTestMixedOverride.cs
}
