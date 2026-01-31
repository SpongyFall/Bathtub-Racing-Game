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
 * -The ability to commercialize products built on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using NUnit.Framework;
using System.Runtime.InteropServices;

namespace GONet.Editor.UnitTests.DistributedHost
{
    [TestFixture]
    public class GONetHostIdentityTests
    {
        #region Struct Layout Tests

        [Test]
        public void HostIdentity_Size_Is16Bytes()
        {
            // Assert - struct should be exactly 16 bytes for efficient serialization
            int size = Marshal.SizeOf<HostIdentity>();
            Assert.AreEqual(16, size, "HostIdentity should be exactly 16 bytes");
        }

        #endregion

        #region Constructor Tests

        [Test]
        public void Constructor_SetsAllFields()
        {
            // Arrange
            long sessionGUID = 0x123456789ABCDEF0;
            uint epoch = 5;
            ushort hostId = 1;
            ushort viceHostId = 2;

            // Act
            var identity = new HostIdentity(sessionGUID, epoch, hostId, viceHostId);

            // Assert
            Assert.AreEqual(sessionGUID, identity.SessionGUID);
            Assert.AreEqual(epoch, identity.HostEpoch);
            Assert.AreEqual(hostId, identity.HostAuthorityId);
            Assert.AreEqual(viceHostId, identity.ViceHostAuthorityId);
        }

        #endregion

        #region IsValid Tests

        [Test]
        public void IsValid_TrueWhenSessionGUIDNonZero()
        {
            // Arrange
            var identity = new HostIdentity(12345, 0, 0, 0);

            // Assert
            Assert.IsTrue(identity.IsValid);
        }

        [Test]
        public void IsValid_TrueWhenHostAuthorityIdNonZero()
        {
            // Arrange
            var identity = new HostIdentity(0, 0, 1, 0);

            // Assert
            Assert.IsTrue(identity.IsValid);
        }

        [Test]
        public void IsValid_FalseWhenDefault()
        {
            // Arrange
            var identity = default(HostIdentity);

            // Assert
            Assert.IsFalse(identity.IsValid);
        }

        [Test]
        public void Invalid_IsDefault()
        {
            // Assert
            Assert.AreEqual(default(HostIdentity), HostIdentity.Invalid);
            Assert.IsFalse(HostIdentity.Invalid.IsValid);
        }

        #endregion

        #region HasViceHost Tests

        [Test]
        public void HasViceHost_TrueWhenViceHostIdNonZero()
        {
            // Arrange
            var identity = new HostIdentity(12345, 0, 1, 2);

            // Assert
            Assert.IsTrue(identity.HasViceHost);
        }

        [Test]
        public void HasViceHost_FalseWhenViceHostIdZero()
        {
            // Arrange
            var identity = new HostIdentity(12345, 0, 1, 0);

            // Assert
            Assert.IsFalse(identity.HasViceHost);
        }

        #endregion

        #region IsHost / IsViceHost Tests

        [Test]
        public void IsHost_TrueWhenAuthorityIdMatchesHost()
        {
            // Arrange
            var identity = new HostIdentity(12345, 0, 1, 2);

            // Assert
            Assert.IsTrue(identity.IsHost(1));
            Assert.IsFalse(identity.IsHost(2));
            Assert.IsFalse(identity.IsHost(3));
        }

        [Test]
        public void IsViceHost_TrueWhenAuthorityIdMatchesViceHost()
        {
            // Arrange
            var identity = new HostIdentity(12345, 0, 1, 2);

            // Assert
            Assert.IsTrue(identity.IsViceHost(2));
            Assert.IsFalse(identity.IsViceHost(1));
            Assert.IsFalse(identity.IsViceHost(3));
        }

        [Test]
        public void IsViceHost_FalseWhenNoViceHostDesignated()
        {
            // Arrange
            var identity = new HostIdentity(12345, 0, 1, 0);

            // Assert - even authority ID 0 shouldn't match when no vice host
            Assert.IsFalse(identity.IsViceHost(0));
        }

        #endregion

        #region Epoch Comparison Tests

        [Test]
        public void IsNewerThan_TrueWhenHigherEpoch()
        {
            // Arrange
            var older = new HostIdentity(12345, 1, 1, 0);
            var newer = new HostIdentity(12345, 2, 2, 0);

            // Assert
            Assert.IsTrue(newer.IsNewerThan(older));
            Assert.IsFalse(older.IsNewerThan(newer));
        }

        [Test]
        public void IsNewerThan_FalseWhenSameEpoch()
        {
            // Arrange
            var id1 = new HostIdentity(12345, 1, 1, 0);
            var id2 = new HostIdentity(12345, 1, 2, 0);

            // Assert
            Assert.IsFalse(id1.IsNewerThan(id2));
            Assert.IsFalse(id2.IsNewerThan(id1));
        }

        [Test]
        public void IsNewerThan_FalseWhenDifferentSession()
        {
            // Arrange - different sessions can't be compared
            var id1 = new HostIdentity(12345, 2, 1, 0);
            var id2 = new HostIdentity(54321, 1, 2, 0);

            // Assert
            Assert.IsFalse(id1.IsNewerThan(id2));
        }

        [Test]
        public void IsSameOrNewerThan_TrueWhenSameEpoch()
        {
            // Arrange
            var id1 = new HostIdentity(12345, 1, 1, 0);
            var id2 = new HostIdentity(12345, 1, 2, 0);

            // Assert
            Assert.IsTrue(id1.IsSameOrNewerThan(id2));
            Assert.IsTrue(id2.IsSameOrNewerThan(id1));
        }

        [Test]
        public void IsSameOrNewerThan_TrueWhenNewerEpoch()
        {
            // Arrange
            var older = new HostIdentity(12345, 1, 1, 0);
            var newer = new HostIdentity(12345, 2, 2, 0);

            // Assert
            Assert.IsTrue(newer.IsSameOrNewerThan(older));
            Assert.IsFalse(older.IsSameOrNewerThan(newer));
        }

        [Test]
        public void IsSameOrNewerThan_FalseWhenDifferentSession()
        {
            // Arrange
            var id1 = new HostIdentity(12345, 2, 1, 0);
            var id2 = new HostIdentity(54321, 1, 2, 0);

            // Assert
            Assert.IsFalse(id1.IsSameOrNewerThan(id2));
        }

        #endregion

        #region Equality Tests

        [Test]
        public void Equality_TrueWhenAllFieldsMatch()
        {
            // Arrange
            var id1 = new HostIdentity(12345, 2, 1, 3);
            var id2 = new HostIdentity(12345, 2, 1, 3);

            // Assert
            Assert.AreEqual(id1, id2);
            Assert.IsTrue(id1 == id2);
            Assert.IsFalse(id1 != id2);
        }

        [Test]
        public void Equality_FalseWhenSessionGUIDDiffers()
        {
            // Arrange
            var id1 = new HostIdentity(12345, 2, 1, 3);
            var id2 = new HostIdentity(54321, 2, 1, 3);

            // Assert
            Assert.AreNotEqual(id1, id2);
        }

        [Test]
        public void Equality_FalseWhenEpochDiffers()
        {
            // Arrange
            var id1 = new HostIdentity(12345, 2, 1, 3);
            var id2 = new HostIdentity(12345, 3, 1, 3);

            // Assert
            Assert.AreNotEqual(id1, id2);
        }

        [Test]
        public void Equality_FalseWhenHostIdDiffers()
        {
            // Arrange
            var id1 = new HostIdentity(12345, 2, 1, 3);
            var id2 = new HostIdentity(12345, 2, 2, 3);

            // Assert
            Assert.AreNotEqual(id1, id2);
        }

        [Test]
        public void Equality_FalseWhenViceHostIdDiffers()
        {
            // Arrange
            var id1 = new HostIdentity(12345, 2, 1, 3);
            var id2 = new HostIdentity(12345, 2, 1, 4);

            // Assert
            Assert.AreNotEqual(id1, id2);
        }

        [Test]
        public void GetHashCode_ConsistentForEqualObjects()
        {
            // Arrange
            var id1 = new HostIdentity(12345, 2, 1, 3);
            var id2 = new HostIdentity(12345, 2, 1, 3);

            // Assert
            Assert.AreEqual(id1.GetHashCode(), id2.GetHashCode());
        }

        #endregion

        #region ToString Tests

        [Test]
        public void ToString_ContainsAllKeyInfo()
        {
            // Arrange
            var identity = new HostIdentity(0x12345678, 5, 1, 2);

            // Act
            string str = identity.ToString();

            // Assert
            Assert.IsTrue(str.Contains("12345678"), "Should contain SessionGUID");
            Assert.IsTrue(str.Contains("5"), "Should contain Epoch");
            Assert.IsTrue(str.Contains("Host:1"), "Should contain HostId");
            Assert.IsTrue(str.Contains("Vice:2"), "Should contain ViceHostId");
        }

        #endregion

        #region Split-Brain Prevention Tests

        [Test]
        public void SplitBrainScenario_HigherEpochWins()
        {
            // Simulate split-brain: two nodes both claim to be host
            // The one with higher epoch should be considered authoritative

            // Arrange
            var groupA_Host = new HostIdentity(12345, 2, 1, 0); // epoch 2
            var groupB_Host = new HostIdentity(12345, 3, 2, 0); // epoch 3 (partition healed, this group advanced)

            // Assert - group B with higher epoch wins
            Assert.IsTrue(groupB_Host.IsNewerThan(groupA_Host));
            Assert.IsFalse(groupA_Host.IsNewerThan(groupB_Host));
        }

        [Test]
        public void SplitBrainScenario_SameEpochNeedsAdditionalTiebreaker()
        {
            // Arrange - same epoch, different hosts (shouldn't happen, but test the edge case)
            var hostA = new HostIdentity(12345, 2, 1, 0);
            var hostB = new HostIdentity(12345, 2, 2, 0);

            // Assert - neither is "newer" than the other at epoch level
            Assert.IsFalse(hostA.IsNewerThan(hostB));
            Assert.IsFalse(hostB.IsNewerThan(hostA));
            // In this case, additional tiebreaker needed (vice host designation, authority ID, etc.)
        }

        #endregion
    }
}
