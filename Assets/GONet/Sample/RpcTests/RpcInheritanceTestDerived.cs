using GONet;
using System.Threading.Tasks;
using UnityEngine;

namespace GONet.Sample.RpcTests
{
    /// <summary>
    /// DERIVED CLASS: Inherits from RpcInheritanceTestBase but does NOT override
    /// the RPC methods. This is the key test case.
    ///
    /// This simulates: public class Weapon : Item { /* no override of TryUse */ }
    /// </summary>
    public class RpcInheritanceTestDerived : RpcInheritanceTestBase
    {
        // IMPORTANT: This class does NOT override any RPC methods from the base class
        // The inherited RPCs should still work when called from this class

        /// <summary>
        /// Test calling inherited ServerRpc from derived class
        /// </summary>
        public void TestInheritedServerRpc(int testId)
        {
            GONetLog.Info($"[RpcInheritance] RpcInheritanceTestDerived calling inherited BaseClass_ServerRpc_UseItem...", logProfile);

            // This call should work even though this class doesn't override BaseClass_ServerRpc_UseItem
            CallRpc(nameof(BaseClass_ServerRpc_UseItem), testId, $"InheritedItem-{testId}");
        }

        /// <summary>
        /// Test calling inherited ClientRpc from derived class
        /// </summary>
        public void TestInheritedClientRpc(int testId)
        {
            GONetLog.Info($"[RpcInheritance] RpcInheritanceTestDerived triggering inherited ClientRpc via ServerRpc...", logProfile);

            // Call ServerRpc to trigger the inherited ClientRpc from server
            CallRpc(nameof(TriggerClientRpcFromServer), testId);
        }

        /// <summary>
        /// Test calling inherited TargetRpc from derived class
        /// </summary>
        public void TestInheritedTargetRpc(int testId)
        {
            GONetLog.Info($"[RpcInheritance] RpcInheritanceTestDerived calling inherited BaseClass_TargetRpc_NotifyOwner...", logProfile);

            // This call should work even though this class doesn't override BaseClass_TargetRpc_NotifyOwner
            CallRpc(nameof(BaseClass_TargetRpc_NotifyOwner), testId, $"InheritedNotification-{testId}");
        }

        /// <summary>
        /// Test calling inherited async ServerRpc from derived class
        /// </summary>
        public async Task<int> TestInheritedAsyncServerRpc(int testId)
        {
            GONetLog.Info($"[RpcInheritance] RpcInheritanceTestDerived calling inherited BaseClass_ServerRpc_AsyncProcess...", logProfile);

            // This call should work even though this class doesn't override BaseClass_ServerRpc_AsyncProcess
            return await CallRpcAsync<int, int, int>(nameof(BaseClass_ServerRpc_AsyncProcess), testId, testId);
        }
    }
}
