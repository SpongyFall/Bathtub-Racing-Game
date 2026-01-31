using GONet;
using System.Threading.Tasks;
using UnityEngine;

namespace GONet.Sample.RpcTests
{
    /// <summary>
    /// BASE CLASS: Defines RPCs that should be callable from derived classes
    /// without requiring overrides.
    ///
    /// This simulates: public class Item : GONetParticipantCompanionBehaviour
    /// </summary>
    [RequireComponent(typeof(GONetParticipant))]
    public class RpcInheritanceTestBase : GONetParticipantCompanionBehaviour
    {
        protected string logProfile;

        protected override void Start()
        {
            base.Start();
            logProfile = $"RpcInherit-{(GONetMain.IsServer ? "Server" : $"Client{GONetMain.MyAuthorityId}")}";
            GONetLog.RegisterLoggingProfile(new GONetLog.LoggingProfile(logProfile, outputToSeparateFile: true));
        }

        #region Test Success Reporting (Server -> Clients)

        /// <summary>
        /// ClientRpc to broadcast test success from server to all clients.
        /// This solves the problem where ServerRpc executes on server but clients need to know the result.
        /// </summary>
        [ClientRpc]
        public void ClientRpc_ReportTestSuccess(string testKey)
        {
            GONetLog.Info($"[RpcInheritance] ClientRpc_ReportTestSuccess received: {testKey}", logProfile);
            GONetRpcAllTypesIntegrationTest.ReportTestSuccess(testKey);
        }

        /// <summary>
        /// Helper to report test success - broadcasts to all clients if on server
        /// </summary>
        protected void ReportSuccessToClients(string testKey)
        {
            if (GONetMain.IsServer)
            {
                // Broadcast success to all clients
                CallRpc(nameof(ClientRpc_ReportTestSuccess), testKey);
            }
            // Also report locally (for server-side tracking)
            GONetRpcAllTypesIntegrationTest.ReportTestSuccess(testKey);
        }

        #endregion

        #region Base Class RPCs

        /// <summary>
        /// ServerRpc defined in base class - should be callable from derived classes
        /// </summary>
        [ServerRpc]
        public virtual void BaseClass_ServerRpc_UseItem(int testId, string itemName)
        {
            GONetLog.Info($"[RpcInheritance] BASE BaseClass_ServerRpc_UseItem EXECUTED! testId={testId}, itemName={itemName}, IsServer={GONetMain.IsServer}", logProfile);

            // Determine which test this is based on itemName pattern
            if (itemName.StartsWith("InheritedItem-"))
            {
                ReportSuccessToClients($"Test11-{testId}");
            }
            else if (itemName.StartsWith("GrandchildItem-"))
            {
                ReportSuccessToClients($"Test14-{testId}");
            }
        }

        /// <summary>
        /// ClientRpc defined in base class - should be callable from derived classes
        /// </summary>
        [ClientRpc]
        public virtual void BaseClass_ClientRpc_UpdateVisuals(int testId, string visualData)
        {
            GONetLog.Info($"[RpcInheritance] BASE BaseClass_ClientRpc_UpdateVisuals EXECUTED! testId={testId}, visualData={visualData}, IsClient={GONetMain.IsClient}", logProfile);
            // ClientRpc executes on clients, so direct reporting works
            GONetRpcAllTypesIntegrationTest.ReportTestSuccess($"Test12-{testId}");
        }

        /// <summary>
        /// TargetRpc defined in base class - should be callable from derived classes
        /// </summary>
        [TargetRpc(RpcTarget.Owner)]
        public virtual void BaseClass_TargetRpc_NotifyOwner(int testId, string notification)
        {
            GONetLog.Info($"[RpcInheritance] BASE BaseClass_TargetRpc_NotifyOwner EXECUTED! testId={testId}, notification={notification}, IsServer={GONetMain.IsServer}", logProfile);

            // TargetRpc executes on the target (owner). If we're the owner client, report directly.
            // If server is owner, broadcast to clients.
            if (GONetMain.IsServer)
            {
                ReportSuccessToClients($"Test13-{testId}");
            }
            else
            {
                GONetRpcAllTypesIntegrationTest.ReportTestSuccess($"Test13-{testId}");
            }
        }

        /// <summary>
        /// Async ServerRpc defined in base class - should be callable from derived classes
        /// </summary>
        [ServerRpc]
        public virtual async Task<int> BaseClass_ServerRpc_AsyncProcess(int testId, int inputValue)
        {
            GONetLog.Info($"[RpcInheritance] BASE BaseClass_ServerRpc_AsyncProcess EXECUTING! testId={testId}, inputValue={inputValue}", logProfile);
            await Task.Delay(10); // Simulate async work
            int result = inputValue * 2;
            GONetLog.Info($"[RpcInheritance] BaseClass_ServerRpc_AsyncProcess returning {result}", logProfile);
            // Note: Test 16 success is reported by the client when it receives the correct return value
            return result;
        }

        /// <summary>
        /// Helper to trigger ClientRpc from server (used by tests)
        /// </summary>
        [ServerRpc(IsMineRequired = false)]
        public void TriggerClientRpcFromServer(int testId)
        {
            GONetLog.Info($"[RpcInheritance] TriggerClientRpcFromServer called, now calling inherited ClientRpc", logProfile);
            CallRpc(nameof(BaseClass_ClientRpc_UpdateVisuals), testId, $"VisualUpdate-{testId}");
        }

        #endregion
    }
}
