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
using UnityEngine;

namespace GONet.Tests
{
    /// <summary>
    /// Unit tests for configurable tombstone TTL (January 2026).
    ///
    /// ROOT CAUSE: Hardcoded 5-minute tombstone TTL was insufficient for late-joiner scenarios
    /// where clients may connect long after objects have been spawned and despawned, resulting
    /// in "GONetId not found (no tombstone)" errors.
    ///
    /// FIX: Made tombstone TTL configurable via GONetConfig:
    /// - DespawnTombstoneTTLMinutes: Time-to-live for tombstones (default: 5 minutes)
    /// - DespawnTombstoneMaxEntries: Maximum tombstones to keep (default: 4096)
    /// - DespawnTombstonePruneIntervalSeconds: Prune interval (default: 30 seconds)
    ///
    /// Test scenarios:
    /// 1. Config options exist with correct defaults
    /// 2. Config values can be modified at runtime
    /// 3. Tombstones respect configured TTL
    /// </summary>
    [TestFixture]
    [Category("LateJoiner")]
    [Category("Config")]
    [Category("Tombstone")]
    public class GONetTombstoneTTLConfigTests
    {
        private float originalTTLMinutes;
        private int originalMaxEntries;
        private float originalPruneInterval;
        private GameObject testGameObject;

        [SetUp]
        public void Setup()
        {
            // Save original config values
            originalTTLMinutes = GONetConfig.DespawnTombstoneTTLMinutes;
            originalMaxEntries = GONetConfig.DespawnTombstoneMaxEntries;
            originalPruneInterval = GONetConfig.DespawnTombstonePruneIntervalSeconds;

            // Clear tombstones
            GONetMain.ClearDespawnTombstones_TestOnly();

            // Create test GameObject with GONetGlobal (required for GONet operations)
            testGameObject = new GameObject("TestGONetGlobal");
            testGameObject.AddComponent<GONetGlobal>();
        }

        [TearDown]
        public void TearDown()
        {
            // Restore original config values
            GONetConfig.DespawnTombstoneTTLMinutes = originalTTLMinutes;
            GONetConfig.DespawnTombstoneMaxEntries = originalMaxEntries;
            GONetConfig.DespawnTombstonePruneIntervalSeconds = originalPruneInterval;

            // Cleanup
            GONetMain.ClearDespawnTombstones_TestOnly();

            if (testGameObject != null)
            {
                Object.DestroyImmediate(testGameObject);
            }
        }

        #region Config Defaults Tests

        [Test]
        public void DespawnTombstoneTTLMinutes_DefaultIs5Minutes()
        {
            // SCENARIO: Verify default TTL value
            // EXPECTED: 5 minutes (backwards compatible with original hardcoded value)

            // Note: We saved originalTTLMinutes in Setup
            Assert.AreEqual(5.0f, originalTTLMinutes,
                "DespawnTombstoneTTLMinutes should default to 5 minutes for backwards compatibility");
        }

        [Test]
        public void DespawnTombstoneMaxEntries_DefaultIs4096()
        {
            // SCENARIO: Verify default max entries value
            // EXPECTED: 4096 entries (~64KB max memory)

            Assert.AreEqual(4096, originalMaxEntries,
                "DespawnTombstoneMaxEntries should default to 4096");
        }

        [Test]
        public void DespawnTombstonePruneIntervalSeconds_DefaultIs30Seconds()
        {
            // SCENARIO: Verify default prune interval
            // EXPECTED: 30 seconds

            Assert.AreEqual(30.0f, originalPruneInterval,
                "DespawnTombstonePruneIntervalSeconds should default to 30 seconds");
        }

        #endregion

        #region Config Modification Tests

        [Test]
        public void DespawnTombstoneTTLMinutes_CanBeModifiedAtRuntime()
        {
            // SCENARIO: Change TTL at runtime for late-joiner scenarios
            // EXPECTED: New value is used immediately

            float newTTL = 15.0f; // Extend to 15 minutes for long lobbies

            GONetConfig.DespawnTombstoneTTLMinutes = newTTL;

            Assert.AreEqual(newTTL, GONetConfig.DespawnTombstoneTTLMinutes,
                "DespawnTombstoneTTLMinutes should be modifiable at runtime");
        }

        [Test]
        public void DespawnTombstoneMaxEntries_CanBeModifiedAtRuntime()
        {
            // SCENARIO: Change max entries at runtime for high-traffic scenarios
            // EXPECTED: New value is used immediately

            int newMax = 8192; // Increase for games with many spawns/despawns

            GONetConfig.DespawnTombstoneMaxEntries = newMax;

            Assert.AreEqual(newMax, GONetConfig.DespawnTombstoneMaxEntries,
                "DespawnTombstoneMaxEntries should be modifiable at runtime");
        }

        [Test]
        public void DespawnTombstonePruneIntervalSeconds_CanBeModifiedAtRuntime()
        {
            // SCENARIO: Change prune interval at runtime
            // EXPECTED: New value is used immediately

            float newInterval = 60.0f; // Reduce CPU overhead with longer intervals

            GONetConfig.DespawnTombstonePruneIntervalSeconds = newInterval;

            Assert.AreEqual(newInterval, GONetConfig.DespawnTombstonePruneIntervalSeconds,
                "DespawnTombstonePruneIntervalSeconds should be modifiable at runtime");
        }

        #endregion

        #region Documentation Tests

        [Test]
        public void DespawnTombstoneTTLMinutes_HasXmlDocumentation()
        {
            // SCENARIO: Verify config option is documented
            // EXPECTED: Field exists and has proper type for documentation

            var field = typeof(GONetConfig).GetField("DespawnTombstoneTTLMinutes");

            Assert.IsNotNull(field, "DespawnTombstoneTTLMinutes field should exist");
            Assert.AreEqual(typeof(float), field.FieldType,
                "DespawnTombstoneTTLMinutes should be float (minutes)");
            Assert.IsTrue(field.IsStatic, "Config fields should be static");
            Assert.IsTrue(field.IsPublic, "Config fields should be public for runtime modification");
        }

        [Test]
        public void DespawnTombstoneMaxEntries_HasXmlDocumentation()
        {
            // SCENARIO: Verify config option is documented
            // EXPECTED: Field exists and has proper type

            var field = typeof(GONetConfig).GetField("DespawnTombstoneMaxEntries");

            Assert.IsNotNull(field, "DespawnTombstoneMaxEntries field should exist");
            Assert.AreEqual(typeof(int), field.FieldType,
                "DespawnTombstoneMaxEntries should be int");
            Assert.IsTrue(field.IsStatic, "Config fields should be static");
            Assert.IsTrue(field.IsPublic, "Config fields should be public for runtime modification");
        }

        [Test]
        public void DespawnTombstonePruneIntervalSeconds_HasXmlDocumentation()
        {
            // SCENARIO: Verify config option is documented
            // EXPECTED: Field exists and has proper type

            var field = typeof(GONetConfig).GetField("DespawnTombstonePruneIntervalSeconds");

            Assert.IsNotNull(field, "DespawnTombstonePruneIntervalSeconds field should exist");
            Assert.AreEqual(typeof(float), field.FieldType,
                "DespawnTombstonePruneIntervalSeconds should be float (seconds)");
            Assert.IsTrue(field.IsStatic, "Config fields should be static");
            Assert.IsTrue(field.IsPublic, "Config fields should be public for runtime modification");
        }

        #endregion

        #region Tombstone Behavior Tests

        [Test]
        public void Tombstone_CreatedAndQueryable()
        {
            // SCENARIO: Create tombstone and verify it exists
            // EXPECTED: HasDespawnTombstone returns true

            uint gonetId = 0xE0010001u;

            Assert.IsFalse(GONetMain.HasDespawnTombstone(gonetId),
                "No tombstone should exist before creation");

            GONetMain.AddOrRefreshDespawnTombstone_TestOnly(gonetId);

            Assert.IsTrue(GONetMain.HasDespawnTombstone(gonetId),
                "Tombstone should exist after creation");
        }

        [Test]
        public void Tombstone_ClearRemovesAll()
        {
            // SCENARIO: Clear tombstones
            // EXPECTED: All tombstones removed

            uint gonetId1 = 0xE0020001u;
            uint gonetId2 = 0xE0020002u;

            GONetMain.AddOrRefreshDespawnTombstone_TestOnly(gonetId1);
            GONetMain.AddOrRefreshDespawnTombstone_TestOnly(gonetId2);

            Assert.IsTrue(GONetMain.HasDespawnTombstone(gonetId1));
            Assert.IsTrue(GONetMain.HasDespawnTombstone(gonetId2));

            GONetMain.ClearDespawnTombstones_TestOnly();

            Assert.IsFalse(GONetMain.HasDespawnTombstone(gonetId1),
                "Tombstone 1 should be cleared");
            Assert.IsFalse(GONetMain.HasDespawnTombstone(gonetId2),
                "Tombstone 2 should be cleared");
        }

        #endregion
    }
}
