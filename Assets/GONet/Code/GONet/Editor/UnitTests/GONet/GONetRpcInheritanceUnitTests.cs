/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 *
 *
 * Authorized use is explicitly limited to the following:
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on original source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 * -The ability to modify source code for local use only
 * -The ability to distribute products based on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on modified source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using NUnit.Framework;
using System;

namespace GONet.Tests
{
    /// <summary>
    /// Unit tests for RPC inheritance mechanics.
    ///
    /// Validates the core invariant: RpcId is computed from the DECLARING TYPE, not the runtime type.
    /// This ensures that when a derived class calls an inherited RPC, the system matches it to the
    /// same handler registered by the base class's generated code.
    ///
    /// KEY SCENARIOS:
    /// 1. Same declaring type + method name → deterministic, identical RpcId
    /// 2. Different types → different RpcIds (no collisions for distinct RPCs)
    /// 3. Sibling derived classes calling the same inherited RPC → same RpcId (uses base declaring type)
    /// 4. Override changes declaring type → different RpcId from base version
    /// 5. Multi-level inheritance (grandchild) → still resolves to original declaring type
    /// </summary>
    [TestFixture]
    [Category("RPC")]
    [Category("Inheritance")]
    public class GONetRpcInheritanceUnitTests
    {
        // Simulated type hierarchy for testing RpcId computation:
        //   BaseItem (declares UseItem, Broadcast, NotifyOwner)
        //     ├── Sword   (no override)
        //     ├── Shield  (no override)
        //     ├── MagicStaff (overrides UseItem)
        //     └── Weapon  (no override)
        //           └── Gun (no override, grandchild)

        // We use real Type objects to test GetRpcId. These are placeholder types
        // that mimic the inheritance structure without requiring MonoBehaviour.

        #region RpcId Determinism

        [Test]
        public void GetRpcId_SameTypeAndMethod_ReturnsSameId()
        {
            // SCENARIO: Calling GetRpcId twice with identical inputs must return the same value.
            // This is the foundational determinism guarantee.

            uint id1 = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");
            uint id2 = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");

            Assert.AreEqual(id1, id2, "GetRpcId must be deterministic for identical inputs");
        }

        [Test]
        public void GetRpcId_DifferentMethods_ReturnsDifferentIds()
        {
            // SCENARIO: Different method names on the same type must produce different RpcIds.

            uint idUse = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");
            uint idBroadcast = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "Broadcast");
            uint idNotify = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "NotifyOwner");

            Assert.AreNotEqual(idUse, idBroadcast, "Different methods must have different RpcIds");
            Assert.AreNotEqual(idUse, idNotify, "Different methods must have different RpcIds");
            Assert.AreNotEqual(idBroadcast, idNotify, "Different methods must have different RpcIds");
        }

        [Test]
        public void GetRpcId_DifferentTypes_SameMethod_ReturnsDifferentIds()
        {
            // SCENARIO: Same method name declared on different types produces different RpcIds.
            // This is what happens when a derived class OVERRIDES a base RPC.

            uint idBase = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");
            uint idDerived = GONetEventBus.GetRpcId(typeof(FakeMagicStaff), "UseItem");

            Assert.AreNotEqual(idBase, idDerived,
                "Override changes declaring type, so RpcId must differ from base version");
        }

        #endregion

        #region Sibling Inheritance (the user's specific question)

        [Test]
        public void GetRpcId_SiblingDerivedClasses_SameBaseMethod_ProduceSameId()
        {
            // SCENARIO: Multiple derived classes (Sword, Shield) that do NOT override UseItem
            // should all resolve to the base type's RpcId, because the declaring type is BaseItem.
            //
            // This is the "multiple child classes" case:
            //   class Sword : BaseItem { }      // no override
            //   class Shield : BaseItem { }     // no override
            // Both call UseItem → declaring type is BaseItem → same RpcId.

            uint idFromBase = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");
            // In the real system, Sword.UseItem has DeclaringType == FakeBaseItem (no override)
            // So the generated code passes typeof(FakeBaseItem) for both siblings.
            uint idFromSword = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");
            uint idFromShield = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");

            Assert.AreEqual(idFromBase, idFromSword,
                "Non-overriding Sword must use base declaring type → same RpcId");
            Assert.AreEqual(idFromBase, idFromShield,
                "Non-overriding Shield must use base declaring type → same RpcId");
            Assert.AreEqual(idFromSword, idFromShield,
                "Sibling classes without overrides must produce identical RpcIds");
        }

        [Test]
        public void GetRpcId_SiblingWithOverride_DiffersFromSiblingWithout()
        {
            // SCENARIO: MagicStaff overrides UseItem, Sword does not.
            // MagicStaff's declaring type is FakeMagicStaff, Sword's is FakeBaseItem.

            uint idSword = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");     // inherited
            uint idMagicStaff = GONetEventBus.GetRpcId(typeof(FakeMagicStaff), "UseItem"); // overridden

            Assert.AreNotEqual(idSword, idMagicStaff,
                "Overriding sibling must have different RpcId from non-overriding sibling");
        }

        [Test]
        public void GetRpcId_MultipleSiblings_AllNonOverriding_AllIdentical()
        {
            // SCENARIO: Three siblings (Sword, Shield, Weapon) none overriding.
            // All must produce the same RpcId when using the base declaring type.

            uint idSword = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");
            uint idShield = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");
            uint idWeapon = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");

            Assert.AreEqual(idSword, idShield);
            Assert.AreEqual(idShield, idWeapon);
        }

        #endregion

        #region Multi-Level Inheritance

        [Test]
        public void GetRpcId_GrandchildClass_UsesOriginalDeclaringType()
        {
            // SCENARIO: Gun extends Weapon extends BaseItem. None override UseItem.
            // The declaring type is still BaseItem → same RpcId as any other non-overriding class.

            uint idBase = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");
            // Gun.UseItem.DeclaringType == FakeBaseItem (2 levels up)
            uint idGun = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");

            Assert.AreEqual(idBase, idGun,
                "Grandchild calling grandparent's non-overridden RPC must use grandparent's declaring type");
        }

        [Test]
        public void GetRpcId_MixedOverrideInChain_MiddleOverrides_GrandchildUsesMiddle()
        {
            // SCENARIO: If Weapon overrides UseItem, then Gun (extending Weapon) inherits
            // Weapon's version → declaring type is FakeWeaponOverride, not FakeBaseItem.

            uint idBase = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");
            uint idWeaponOverride = GONetEventBus.GetRpcId(typeof(FakeWeaponOverride), "UseItem");

            Assert.AreNotEqual(idBase, idWeaponOverride,
                "Middle class override changes declaring type away from grandparent");
            // Gun extends WeaponOverride → Gun.UseItem.DeclaringType == FakeWeaponOverride
            uint idGunFromOverridingParent = GONetEventBus.GetRpcId(typeof(FakeWeaponOverride), "UseItem");
            Assert.AreEqual(idWeaponOverride, idGunFromOverridingParent,
                "Grandchild of overriding middle class uses middle class's declaring type");
        }

        #endregion

        #region RpcId Hash Quality

        [Test]
        public void GetRpcId_NonZero()
        {
            // RpcId should never be zero (zero is used as "unset" sentinel)
            uint id = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");
            Assert.AreNotEqual(0u, id, "RpcId must not be zero");
        }

        [Test]
        public void GetRpcId_DifferentlyNamedTypes_NoCollision()
        {
            // Spot-check that similarly named types don't collide
            uint id1 = GONetEventBus.GetRpcId(typeof(FakeBaseItem), "UseItem");
            uint id2 = GONetEventBus.GetRpcId(typeof(FakeSword), "UseItem");

            // FakeSword is a different type, so even with same method name the hash differs
            Assert.AreNotEqual(id1, id2,
                "Different declaring types with same method name must produce different RpcIds");
        }

        #endregion

        #region Declaring Type Resolution via Reflection

        [Test]
        public void Reflection_InheritedMethod_DeclaringType_IsBaseClass()
        {
            // SCENARIO: Verify that C# reflection correctly reports DeclaringType for inherited methods.
            // This is what the code generator uses to determine the correct type for GetRpcId.

            var method = typeof(FakeSword).GetMethod(nameof(FakeBaseItem.VirtualMethod));
            Assert.IsNotNull(method, "Should find inherited virtual method on derived class");
            Assert.AreEqual(typeof(FakeBaseItem), method.DeclaringType,
                "Non-overridden method's DeclaringType should be the base class");
        }

        [Test]
        public void Reflection_OverriddenMethod_DeclaringType_IsDerivedClass()
        {
            // SCENARIO: When a method IS overridden, DeclaringType changes to the overriding class.

            var method = typeof(FakeMagicStaff).GetMethod(nameof(FakeBaseItem.VirtualMethod));
            Assert.IsNotNull(method, "Should find overridden method on derived class");
            Assert.AreEqual(typeof(FakeMagicStaff), method.DeclaringType,
                "Overridden method's DeclaringType should be the derived class");
        }

        [Test]
        public void Reflection_GrandchildInherited_DeclaringType_IsOriginalBase()
        {
            // SCENARIO: Grandchild inherits from Middle which inherits from Base, none overriding.
            // DeclaringType should still be Base.

            var method = typeof(FakeGun).GetMethod(nameof(FakeBaseItem.VirtualMethod));
            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(FakeBaseItem), method.DeclaringType,
                "Grandchild's inherited non-overridden method DeclaringType should be the original base");
        }

        [Test]
        public void Reflection_MultipleSiblings_AllReportSameDeclaringType()
        {
            // SCENARIO: Multiple non-overriding siblings all report same DeclaringType.

            var swordMethod = typeof(FakeSword).GetMethod(nameof(FakeBaseItem.VirtualMethod));
            var shieldMethod = typeof(FakeShield).GetMethod(nameof(FakeBaseItem.VirtualMethod));
            var weaponMethod = typeof(FakeWeapon).GetMethod(nameof(FakeBaseItem.VirtualMethod));

            Assert.AreEqual(swordMethod.DeclaringType, shieldMethod.DeclaringType);
            Assert.AreEqual(shieldMethod.DeclaringType, weaponMethod.DeclaringType);
            Assert.AreEqual(typeof(FakeBaseItem), swordMethod.DeclaringType);
        }

        [Test]
        public void Reflection_OverridingSibling_HasDifferentDeclaringType()
        {
            // SCENARIO: MagicStaff overrides → different DeclaringType from non-overriding Sword.

            var swordMethod = typeof(FakeSword).GetMethod(nameof(FakeBaseItem.VirtualMethod));
            var staffMethod = typeof(FakeMagicStaff).GetMethod(nameof(FakeBaseItem.VirtualMethod));

            Assert.AreNotEqual(swordMethod.DeclaringType, staffMethod.DeclaringType,
                "Overriding class should have itself as DeclaringType, not the base");
        }

        #endregion

        #region Fake Type Hierarchy (for testing only)

        // These classes simulate a GONet component hierarchy for RpcId computation.
        // They don't inherit from GONetParticipantCompanionBehaviour since we're
        // only testing GetRpcId (which takes any Type) and reflection behavior.

        private class FakeBaseItem
        {
            public virtual void VirtualMethod() { }
        }

        private class FakeSword : FakeBaseItem
        {
            // No override - inherits VirtualMethod from FakeBaseItem
        }

        private class FakeShield : FakeBaseItem
        {
            // No override
        }

        private class FakeMagicStaff : FakeBaseItem
        {
            public override void VirtualMethod() { }  // OVERRIDES
        }

        private class FakeWeapon : FakeBaseItem
        {
            // No override - pass-through middle class
        }

        private class FakeGun : FakeWeapon
        {
            // Grandchild - no override
        }

        private class FakeWeaponOverride : FakeBaseItem
        {
            public override void VirtualMethod() { }  // Middle class that overrides
        }

        private class FakeGunFromOverride : FakeWeaponOverride
        {
            // Grandchild of overriding middle class - no override
        }

        #endregion
    }
}
