using GONet;
using UnityEngine;

namespace GONet.Sample.RpcTests
{
    /// <summary>
    /// MIDDLE CLASS: For testing multi-level inheritance
    ///
    /// This simulates: public class Weapon : Item { }
    /// </summary>
    public class RpcInheritanceTestMiddle : RpcInheritanceTestBase
    {
        // No overrides - just passes through
    }
}
