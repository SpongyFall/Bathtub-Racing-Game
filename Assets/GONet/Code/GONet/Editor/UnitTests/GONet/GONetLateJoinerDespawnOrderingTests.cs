using NUnit.Framework;
using System.Collections;
using System.Reflection;

namespace GONet.Tests
{
    [TestFixture]
    [Category("LateJoiner")]
    [Category("SpawnDespawn")]
    [Category("Ordering")]
    public class GONetLateJoinerDespawnOrderingTests
    {
        private FieldInfo deferredSpawnEventsField;
        private FieldInfo deferredDespawnEventsField;
        private FieldInfo despawnTombstoneByGONetIdField;
        private FieldInfo gonetParticipantByGONetIdMapField;

        private MethodInfo onDespawnRemoteMethod;
        private MethodInfo onSpawnRemoteMethod;

        [SetUp]
        public void Setup()
        {
            var gonetType = typeof(GONetMain);

            deferredSpawnEventsField = gonetType.GetField("deferredSpawnEvents", BindingFlags.NonPublic | BindingFlags.Static);
            deferredDespawnEventsField = gonetType.GetField("deferredDespawnEvents", BindingFlags.NonPublic | BindingFlags.Static);
            despawnTombstoneByGONetIdField = gonetType.GetField("despawnTombstoneByGONetId", BindingFlags.NonPublic | BindingFlags.Static);
            gonetParticipantByGONetIdMapField = gonetType.GetField("gonetParticipantByGONetIdMap", BindingFlags.NonPublic | BindingFlags.Static);

            onDespawnRemoteMethod = gonetType.GetMethod("OnDespawnGNPEvent_Remote", BindingFlags.NonPublic | BindingFlags.Static);
            onSpawnRemoteMethod = gonetType.GetMethod("OnInstantiationEvent_Remote", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(deferredSpawnEventsField, "deferredSpawnEvents not found");
            Assert.IsNotNull(deferredDespawnEventsField, "deferredDespawnEvents not found");
            Assert.IsNotNull(despawnTombstoneByGONetIdField, "despawnTombstoneByGONetId not found (verify despawn-before-spawn fix is present)");
            Assert.IsNotNull(gonetParticipantByGONetIdMapField, "gonetParticipantByGONetIdMap not found");
            Assert.IsNotNull(onDespawnRemoteMethod, "OnDespawnGNPEvent_Remote not found");
            Assert.IsNotNull(onSpawnRemoteMethod, "OnInstantiationEvent_Remote not found");

            ClearState();
        }

        [TearDown]
        public void Teardown()
        {
            ClearState();
        }

        [Test]
        public void DespawnBeforeSpawn_TombstonesAndSuppressesSpawn()
        {
            uint gonetId = GetUnusedGONetId();

            InvokeDespawnRemote(gonetId);

            var tombstones = GetDespawnTombstones();
            Assert.IsTrue(tombstones.Contains(gonetId), "Expected despawn tombstone for unknown participant");

            InvokeSpawnRemote(gonetId, requiredSceneName: "SceneNotLoaded_UnitTest");

            Assert.AreEqual(0, GetDeferredSpawnEvents().Count, "Spawn should be suppressed when a prior despawn tombstone exists");
            Assert.IsTrue(GetDespawnTombstones().Contains(gonetId), "Spawn suppression should keep the tombstone briefly to drop late-arriving bundles");
        }

        [Test]
        public void DespawnAfterDeferredSpawn_CancelsSpawnAndTombstones()
        {
            // CANCEL-ON-DESPAWN OPTIMIZATION (Dec 2025):
            // When despawn arrives for a deferred spawn, we now CANCEL the spawn (remove from deferredSpawnEvents)
            // and add a tombstone (for any late-arriving AllValues bundles). This is more efficient than
            // spawning then immediately despawning.
            uint gonetId = GetUnusedGONetId();

            InvokeSpawnRemote(gonetId, requiredSceneName: "SceneNotLoaded_UnitTest");
            Assert.AreEqual(1, GetDeferredSpawnEvents().Count, "Spawn should have been deferred for unloaded scene");

            InvokeDespawnRemote(gonetId);

            // Spawn should be REMOVED (canceled), not kept
            Assert.AreEqual(0, GetDeferredSpawnEvents().Count, "Deferred spawn should be canceled (removed) when despawn arrives");
            // Tombstone should be CREATED for any late-arriving AllValues bundles
            Assert.IsTrue(GetDespawnTombstones().Contains(gonetId), "Tombstone expected to handle late-arriving AllValues bundles");
            // DeferredDespawnEvents should NOT be used (we cancel instead of defer)
            Assert.AreEqual(0, GetDeferredDespawnEvents().Count, "DeferredDespawnEvents should not be used with cancel-on-despawn");
        }

        [Test]
        public void DespawnBeforeSpawn_WhenSpawnDeferred_SpawnIsSuppressed()
        {
            // This tests that the existing test DespawnBeforeSpawn_TombstonesAndSuppressesSpawn
            // covers the case where despawn arrives before spawn. The tombstone check in
            // OnInstantiationEvent_Remote suppresses the spawn before it can be deferred.
            uint gonetId = GetUnusedGONetId();

            // Despawn arrives first - creates tombstone
            InvokeDespawnRemote(gonetId);
            Assert.IsTrue(GetDespawnTombstones().Contains(gonetId), "Despawn should create tombstone");

            // Spawn arrives - tombstone check happens FIRST in OnInstantiationEvent_Remote
            // So spawn should be suppressed immediately, not deferred
            InvokeSpawnRemote(gonetId, requiredSceneName: "SceneNotLoaded_UnitTest");

            // Spawn should be suppressed by tombstone, NOT deferred
            Assert.AreEqual(0, GetDeferredSpawnEvents().Count, "Spawn should be suppressed by tombstone, not deferred");
            // Tombstone should remain briefly (TTL) to drop late-arriving bundles
            Assert.IsTrue(GetDespawnTombstones().Contains(gonetId), "Tombstone should remain briefly when spawn is suppressed");
        }

        private void ClearState()
        {
            GetDeferredSpawnEvents().Clear();
            GetDeferredDespawnEvents().Clear();
            GetDespawnTombstones().Clear();
        }

        private uint GetUnusedGONetId()
        {
            var map = (IDictionary)gonetParticipantByGONetIdMapField.GetValue(null);
            uint candidate = 0xE0000001u;
            while (map.Contains(candidate))
            {
                candidate++;
            }
            return candidate;
        }

        private IList GetDeferredSpawnEvents() => (IList)deferredSpawnEventsField.GetValue(null);

        private IList GetDeferredDespawnEvents() => (IList)deferredDespawnEventsField.GetValue(null);

        private IDictionary GetDespawnTombstones() => (IDictionary)despawnTombstoneByGONetIdField.GetValue(null);

        private void InvokeDespawnRemote(uint gonetId)
        {
            var envelope = new GONetEventEnvelope<DespawnGONetParticipantEvent>
            {
                SourceAuthorityId = 1,
                Event = new DespawnGONetParticipantEvent { GONetId = gonetId }
            };

            onDespawnRemoteMethod.Invoke(null, new object[] { envelope });
        }

        private void InvokeSpawnRemote(uint gonetId, string requiredSceneName)
        {
            var envelope = new GONetEventEnvelope<InstantiateGONetParticipantEvent>
            {
                SourceAuthorityId = 1,
                Event = new InstantiateGONetParticipantEvent
                {
                    GONetId = gonetId,
                    SceneIdentifier = requiredSceneName
                }
            };

            onSpawnRemoteMethod.Invoke(null, new object[] { envelope });
        }
    }
}

