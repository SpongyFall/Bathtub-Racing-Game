using GONet;
using System.Threading.Tasks;
using UnityEngine;

namespace GONet.Sample.RpcTests
{
    /// <summary>
    /// CLASS WITH MIXED OVERRIDE PATTERN: Some RPCs overridden, some not
    ///
    /// Tests that overridden RPCs use the derived implementation while
    /// non-overridden RPCs correctly call the base implementation.
    /// </summary>
    public class RpcInheritanceTestMixedOverride : RpcInheritanceTestBase
    {
        /// <summary>
        /// OVERRIDDEN: This should use the derived class implementation
        /// </summary>
        [ServerRpc]
        public override void BaseClass_ServerRpc_UseItem(int testId, string itemName)
        {
            GONetLog.Info($"[RpcInheritance] MIXEDOVERRIDE BaseClass_ServerRpc_UseItem EXECUTED (override)! testId={testId}", logProfile);

            // Report success to all clients (this executes on server)
            ReportSuccessToClients($"Test15a-{testId}");

            // Don't call base - we want to verify the override is being used
            // base.BaseClass_ServerRpc_UseItem(testId, itemName);
        }

        // NOTE: BaseClass_ClientRpc_UpdateVisuals is NOT overridden here
        // It should use the base class implementation

        /// <summary>
        /// Test the mixed override pattern
        /// </summary>
        public void TestMixedOverridePattern(int testId)
        {
            GONetLog.Info($"[RpcInheritance] Testing mixed override pattern...", logProfile);

            // Call the overridden RPC - should use derived implementation
            GONetLog.Info($"[RpcInheritance] Calling OVERRIDDEN ServerRpc...", logProfile);
            CallRpc(nameof(BaseClass_ServerRpc_UseItem), testId, $"MixedOverride-Overridden-{testId}");

            // Call the non-overridden RPC via server trigger - should use base implementation
            GONetLog.Info($"[RpcInheritance] Calling NON-OVERRIDDEN ClientRpc...", logProfile);
            CallRpc(nameof(TriggerClientRpcFromServer), testId);

            // Schedule verification for Test15b
            VerifyMixedOverrideResults(testId);
        }

        private async void VerifyMixedOverrideResults(int testId)
        {
            await Task.Delay(500);

            // For Test15b, check if the base class ClientRpc was called (reported as Test12-{testId})
            // This verifies that non-overridden RPCs correctly use the base class implementation
            if (GONetMain.IsClient)
            {
                // The base class ClientRpc reports as Test12, so if that's set for our testId,
                // it means the non-overridden RPC was correctly dispatched
                if (GONetRpcAllTypesIntegrationTest.IsTestSuccessful($"Test12-{testId}"))
                {
                    GONetRpcAllTypesIntegrationTest.ReportTestSuccess($"Test15b-{testId}");
                    GONetLog.Info($"[RpcInheritance] PASS Test15b: Non-overridden RPC used base class implementation", logProfile);
                }
                else
                {
                    GONetLog.Warning($"[RpcInheritance] Test15b: Waiting for base class ClientRpc execution...", logProfile);
                    // Try again after more time
                    await Task.Delay(500);
                    if (GONetRpcAllTypesIntegrationTest.IsTestSuccessful($"Test12-{testId}"))
                    {
                        GONetRpcAllTypesIntegrationTest.ReportTestSuccess($"Test15b-{testId}");
                        GONetLog.Info($"[RpcInheritance] PASS Test15b: Non-overridden RPC used base class implementation", logProfile);
                    }
                }
            }
        }
    }
}
