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
using GONet.DistributedHost;
using System.Runtime.InteropServices;

namespace GONet.Editor.UnitTests.DistributedHost
{
    [TestFixture]
    public class GONetNodeIdentityTests
    {
        [SetUp]
        public void SetUp()
        {
            // Clear cached persistent ID between tests
            GONetNodeIdentityManager.ClearCache();
        }

        #region GONetNodeIdentity Struct Tests

        [Test]
        public void GONetNodeIdentity_Constructor_SetsAllFields()
        {
            // Arrange
            ulong persistentId = 0x123456789ABCDEF0;
            ushort sessionId = 42;
            long joinedAt = 1000000;
            var caps = GONetNodeCapabilities.CanHost | GONetNodeCapabilities.HasGoodUplink;

            // Act
            var identity = new GONetNodeIdentity(persistentId, sessionId, joinedAt, caps);

            // Assert
            Assert.AreEqual(persistentId, identity.PersistentId);
            Assert.AreEqual(sessionId, identity.SessionAuthorityId);
            Assert.AreEqual(joinedAt, identity.JoinedAtTicks);
            Assert.AreEqual(caps, identity.Capabilities);
        }

        [Test]
        public void GONetNodeIdentity_Size_Is24Bytes()
        {
            // Assert - struct should be exactly 24 bytes for efficient serialization
            int size = Marshal.SizeOf<GONetNodeIdentity>();
            Assert.AreEqual(24, size, "GONetNodeIdentity should be exactly 24 bytes");
        }

        [Test]
        public void GONetNodeIdentity_CanBecomeHost_TrueWhenCanHostAndNoRelay()
        {
            // Arrange
            var identity = new GONetNodeIdentity(1, 1, 100, GONetNodeCapabilities.CanHost);

            // Assert
            Assert.IsTrue(identity.CanBecomeHost);
        }

        [Test]
        public void GONetNodeIdentity_CanBecomeHost_FalseWhenRequiresRelay()
        {
            // Arrange
            var identity = new GONetNodeIdentity(1, 1, 100,
                GONetNodeCapabilities.CanHost | GONetNodeCapabilities.RequiresRelay);

            // Assert
            Assert.IsFalse(identity.CanBecomeHost);
        }

        [Test]
        public void GONetNodeIdentity_CanBecomeHost_FalseWhenNoCanHostFlag()
        {
            // Arrange
            var identity = new GONetNodeIdentity(1, 1, 100, GONetNodeCapabilities.None);

            // Assert
            Assert.IsFalse(identity.CanBecomeHost);
        }

        [Test]
        public void GONetNodeIdentity_IsDedicatedServer_TrueWhenFlagSet()
        {
            // Arrange
            var identity = new GONetNodeIdentity(1, 1, 100, GONetNodeCapabilities.DedicatedServer);

            // Assert
            Assert.IsTrue(identity.IsDedicatedServer);
        }

        [Test]
        public void GONetNodeIdentity_Equality_SameIdAndSession()
        {
            // Arrange
            var id1 = new GONetNodeIdentity(100, 5, 1000, GONetNodeCapabilities.CanHost);
            var id2 = new GONetNodeIdentity(100, 5, 2000, GONetNodeCapabilities.None); // Different tick/caps

            // Assert - equality is based on PersistentId and SessionAuthorityId only
            Assert.AreEqual(id1, id2);
            Assert.IsTrue(id1 == id2);
        }

        [Test]
        public void GONetNodeIdentity_Inequality_DifferentPersistentId()
        {
            // Arrange
            var id1 = new GONetNodeIdentity(100, 5, 1000, GONetNodeCapabilities.CanHost);
            var id2 = new GONetNodeIdentity(200, 5, 1000, GONetNodeCapabilities.CanHost);

            // Assert
            Assert.AreNotEqual(id1, id2);
            Assert.IsTrue(id1 != id2);
        }

        [Test]
        public void GONetNodeIdentity_Inequality_DifferentSessionId()
        {
            // Arrange
            var id1 = new GONetNodeIdentity(100, 5, 1000, GONetNodeCapabilities.CanHost);
            var id2 = new GONetNodeIdentity(100, 6, 1000, GONetNodeCapabilities.CanHost);

            // Assert
            Assert.AreNotEqual(id1, id2);
        }

        [Test]
        public void GONetNodeIdentity_GetHashCode_ConsistentForEqualObjects()
        {
            // Arrange
            var id1 = new GONetNodeIdentity(100, 5, 1000, GONetNodeCapabilities.CanHost);
            var id2 = new GONetNodeIdentity(100, 5, 2000, GONetNodeCapabilities.None);

            // Assert
            Assert.AreEqual(id1.GetHashCode(), id2.GetHashCode());
        }

        [Test]
        public void GONetNodeIdentity_ToString_ContainsKeyInfo()
        {
            // Arrange
            var identity = new GONetNodeIdentity(0x1234, 42, 1000, GONetNodeCapabilities.CanHost);

            // Act
            string str = identity.ToString();

            // Assert
            Assert.IsTrue(str.Contains("1234"), "Should contain PersistentId");
            Assert.IsTrue(str.Contains("42"), "Should contain SessionAuthorityId");
        }

        #endregion

        #region GONetNodeCapabilities Tests

        [Test]
        public void GONetNodeCapabilities_FlagsCanBeCombined()
        {
            // Arrange
            var caps = GONetNodeCapabilities.CanHost |
                       GONetNodeCapabilities.HasGoodUplink |
                       GONetNodeCapabilities.IsHeadless;

            // Assert
            Assert.IsTrue((caps & GONetNodeCapabilities.CanHost) != 0);
            Assert.IsTrue((caps & GONetNodeCapabilities.HasGoodUplink) != 0);
            Assert.IsTrue((caps & GONetNodeCapabilities.IsHeadless) != 0);
            Assert.IsFalse((caps & GONetNodeCapabilities.RequiresRelay) != 0);
        }

        [Test]
        public void GONetNodeCapabilities_None_IsZero()
        {
            Assert.AreEqual(0, (int)GONetNodeCapabilities.None);
        }

        #endregion

        #region Wrapper Types Tests

        [Test]
        public void PersistentNodeId_PreventsMixingWithRawUlong()
        {
            // Arrange
            var id = new PersistentNodeId(12345);

            // Assert
            Assert.AreEqual(12345UL, id.Value);
            Assert.AreEqual(12345UL, (ulong)id); // Implicit conversion
        }

        [Test]
        public void SessionNodeId_PreventsMixingWithRawUshort()
        {
            // Arrange
            var id = new SessionNodeId(42);

            // Assert
            Assert.AreEqual(42, id.Value);
            Assert.AreEqual(42, (ushort)id); // Implicit conversion
        }

        [Test]
        public void PersistentNodeId_Equality()
        {
            // Arrange
            var id1 = new PersistentNodeId(100);
            var id2 = new PersistentNodeId(100);
            var id3 = new PersistentNodeId(200);

            // Assert
            Assert.AreEqual(id1, id2);
            Assert.AreNotEqual(id1, id3);
            Assert.IsTrue(id1 == id2);
            Assert.IsTrue(id1 != id3);
        }

        [Test]
        public void SessionNodeId_Equality()
        {
            // Arrange
            var id1 = new SessionNodeId(5);
            var id2 = new SessionNodeId(5);
            var id3 = new SessionNodeId(10);

            // Assert
            Assert.AreEqual(id1, id2);
            Assert.AreNotEqual(id1, id3);
        }

        #endregion

        #region MetricsValidityFlags Tests

        [Test]
        public void MetricsValidityFlags_AllValid_CoversAllFlags()
        {
            // Arrange
            var allValid = MetricsValidityFlags.AllValid;

            // Assert - AllValid should include all individual flags
            Assert.IsTrue((allValid & MetricsValidityFlags.RTTValid) != 0);
            Assert.IsTrue((allValid & MetricsValidityFlags.JitterValid) != 0);
            Assert.IsTrue((allValid & MetricsValidityFlags.PacketLossValid) != 0);
            Assert.IsTrue((allValid & MetricsValidityFlags.BandwidthValid) != 0);
            Assert.IsTrue((allValid & MetricsValidityFlags.CPUHeadroomValid) != 0);
            Assert.IsTrue((allValid & MetricsValidityFlags.BatteryValid) != 0);
            Assert.IsTrue((allValid & MetricsValidityFlags.NATTypeValid) != 0);
        }

        #endregion

        #region GONetNodeIdentityManager Tests

        [Test]
        public void GONetNodeIdentityManager_GetOrCreatePersistentId_ReturnsSameIdOnMultipleCalls()
        {
            // Act
            ulong id1 = GONetNodeIdentityManager.GetOrCreatePersistentId();
            ulong id2 = GONetNodeIdentityManager.GetOrCreatePersistentId();

            // Assert
            Assert.AreEqual(id1, id2, "Should return same ID on subsequent calls");
        }

        [Test]
        public void GONetNodeIdentityManager_GetOrCreatePersistentId_ReturnsNonZero()
        {
            // Act
            ulong id = GONetNodeIdentityManager.GetOrCreatePersistentId();

            // Assert
            Assert.AreNotEqual(0UL, id, "Persistent ID should never be zero");
        }

        [Test]
        public void GONetNodeIdentityManager_CreateLocalIdentity_SetsCorrectValues()
        {
            // Arrange
            ushort sessionId = 42;
            long joinedAt = 12345678;

            // Act
            var identity = GONetNodeIdentityManager.CreateLocalIdentity(sessionId, joinedAt);

            // Assert
            Assert.AreEqual(sessionId, identity.SessionAuthorityId);
            Assert.AreEqual(joinedAt, identity.JoinedAtTicks);
            Assert.AreNotEqual(0UL, identity.PersistentId);
            Assert.IsTrue((identity.Capabilities & GONetNodeCapabilities.CanHost) != 0,
                "Should have CanHost by default");
        }

        #endregion
    }
}
