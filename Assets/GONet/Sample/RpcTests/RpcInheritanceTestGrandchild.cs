using GONet;
using UnityEngine;

namespace GONet.Sample.RpcTests
{
    /// <summary>
    /// GRANDCHILD CLASS: For testing multi-level inheritance (A -> B -> C)
    ///
    /// This simulates: public class Gun : Weapon { } (where Weapon : Item)
    /// Gun should be able to call Item's RPCs even though Weapon didn't override them.
    /// </summary>
    public class RpcInheritanceTestGrandchild : RpcInheritanceTestMiddle
    {
        // IMPORTANT: This class is 2 levels removed from the RPC definitions
        // The inherited RPCs should still work

        /// <summary>
        /// Test calling RPC defined 2 levels up in the hierarchy
        /// </summary>
        public void TestMultiLevelInheritedServerRpc(int testId)
        {
            GONetLog.Info($"[RpcInheritance] RpcInheritanceTestGrandchild (2 levels deep) calling BaseClass_ServerRpc_UseItem...", logProfile);

            // This call should work - grandchild calling grandparent's RPC
            CallRpc(nameof(BaseClass_ServerRpc_UseItem), testId, $"GrandchildItem-{testId}");
        }
    }
}
