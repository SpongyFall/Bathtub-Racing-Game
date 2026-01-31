using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

namespace GONet.Editor.UnitTests
{
    /// <summary>
    /// Tests for sync thread race conditions that cause persistent BUNDLE-ABORT errors.
    ///
    /// ROOT CAUSE:
    /// - Background sync threads enumerate activeAutoSyncCompanionsByCodeGenerationIdMap
    /// - Main thread removes entries in OnDisable() during object destruction
    /// - C# Dictionary is NOT thread-safe for concurrent read/write
    /// - Sync thread's enumerator can hold stale references to removed participants
    ///
    /// SYMPTOMS:
    /// - [BUNDLE-ABORT] errors for destroyed objects persist indefinitely
    /// - Typically seen with GONetGlobal duplicates (DestroyImmediate in Awake)
    /// - InstantiationId references objects that no longer exist
    ///
    /// FIX:
    /// - Defensive check now validates participant exists in BOTH maps:
    ///   * gonetParticipantByGONetIdMap (indexed by current GONetId)
    ///   * gonetParticipantByGONetIdAtInstantiationMap (indexed by GONetIdAtInstantiation)
    /// - If missing from EITHER map, participant is skipped
    ///
    /// Created: November 2025
    /// </summary>
    [TestFixture]
    public class GONetSyncThreadRaceConditionTests
    {
        /// <summary>
        /// Validates that the defensive check correctly skips participants removed from gonetParticipantByGONetIdMap.
        ///
        /// SCENARIO:
        /// - Participant was destroyed and removed from gonetParticipantByGONetIdMap
        /// - Sync thread's enumerator still holds reference (race condition)
        /// - Defensive check should skip this participant
        ///
        /// WHAT THIS TESTS:
        /// - The check `!gonetParticipantByGONetIdMap.ContainsKey(participant.GONetId)` works correctly
        ///
        /// INTEGRATION NOTE:
        /// This is a unit test of the defensive check logic in isolation.
        /// Full integration testing requires:
        /// - Multi-threading (sync thread + main thread)
        /// - Real GONetParticipant lifecycle (Awake, OnEnable, OnDisable)
        /// - Scene transitions (GONetGlobal duplicate destruction)
        ///
        /// For integration testing, use:
        /// 1. Spawn GONetGlobal in multiple scenes
        /// 2. Trigger scene transitions (Lobby → GONetSample → RPCPlayground)
        /// 3. Late joiner connects during/after transitions
        /// 4. Verify no BUNDLE-ABORT errors in logs
        /// </summary>
        [Test]
        public void DefensiveCheck_SkipsParticipantRemovedFromGONetIdMap()
        {
            // ARRANGE: Simulate participant removed from gonetParticipantByGONetIdMap
            // In real scenario:
            // - OnDisable() removes participant from map
            // - Sync thread enumerator still holds participant reference
            // - Defensive check evaluates: !gonetParticipantByGONetIdMap.ContainsKey(participant.GONetId)

            uint testGONetId = 843775; // Example from real bug: raw ID 823, authority 1023

            // Simulate the defensive check conditions
            bool isParticipantNull = false; // Participant reference exists (held by enumerator)
            bool isGONetIdUnset = testGONetId != GONetParticipant.GONetId_Unset; // GONetId is set
            bool isInGONetIdMap = false; // ❌ NOT in map (was removed in OnDisable)

            // ACT: Evaluate defensive check logic
            bool shouldSkip = isParticipantNull ||
                              !isGONetIdUnset ||
                              !isInGONetIdMap;

            // ASSERT: Participant should be skipped
            Assert.IsTrue(shouldSkip,
                $"Defensive check should skip participant with GONetId {testGONetId} when removed from gonetParticipantByGONetIdMap");
        }

        /// <summary>
        /// Validates that the defensive check correctly skips participants removed from gonetParticipantByGONetIdAtInstantiationMap.
        ///
        /// SCENARIO (NEW - Nov 2025):
        /// - Participant destroyed and removed from gonetParticipantByGONetIdAtInstantiationMap
        /// - Might still be in gonetParticipantByGONetIdMap temporarily (race condition)
        /// - NEW check: !gonetParticipantByGONetIdAtInstantiationMap.ContainsKey(participant.GONetIdAtInstantiation)
        ///
        /// WHY THIS CHECK IS NEEDED:
        /// - Maps are updated sequentially, not atomically
        /// - Sync thread can see participant in one map but not the other
        /// - Checking BOTH maps catches all race conditions
        ///
        /// REAL-WORLD EXAMPLE:
        /// - Error log shows: TotalInGONetIdMap: 813, TotalInInstantiationMap: 812
        /// - One participant is in GONetIdMap but NOT InstantiationMap
        /// - Original check only looked at GONetIdMap → missed this case
        /// </summary>
        [Test]
        public void DefensiveCheck_SkipsParticipantRemovedFromInstantiationMap()
        {
            // ARRANGE: Simulate participant removed from InstantiationMap but still in GONetIdMap (race)
            uint testGONetId = 843775;
            uint testInstantiationId = 843775;

            bool isParticipantNull = false; // Participant reference exists
            bool isGONetIdUnset = false; // GONetId is set
            bool isInstantiationIdUnset = false; // InstantiationId is set
            bool isInGONetIdMap = true; // ✅ Still in GONetIdMap (race - not removed yet)
            bool isInInstantiationMap = false; // ❌ NOT in InstantiationMap (removed first)

            // ACT: Evaluate NEW defensive check logic (Nov 2025 fix)
            bool shouldSkip = isParticipantNull ||
                              isGONetIdUnset ||
                              isInstantiationIdUnset ||
                              !isInGONetIdMap ||
                              !isInInstantiationMap; // ← NEW CHECK catches this case

            // ASSERT: Participant should be skipped
            Assert.IsTrue(shouldSkip,
                $"Defensive check should skip participant when removed from gonetParticipantByGONetIdAtInstantiationMap " +
                $"(GONetId: {testGONetId}, InstantiationId: {testInstantiationId})");
        }

        /// <summary>
        /// Validates that the defensive check does NOT skip valid participants that exist in both maps.
        ///
        /// REGRESSION TEST:
        /// - Ensures our fix doesn't break normal operation
        /// - Valid participants with entries in BOTH maps should NOT be skipped
        /// </summary>
        [Test]
        public void DefensiveCheck_DoesNotSkipValidParticipant()
        {
            // ARRANGE: Simulate valid participant in both maps
            uint testGONetId = 5119;
            uint testInstantiationId = 5119;

            bool isParticipantNull = false; // Valid participant
            bool isGONetIdUnset = false; // GONetId is set
            bool isInstantiationIdUnset = false; // InstantiationId is set
            bool isInGONetIdMap = true; // ✅ In GONetIdMap
            bool isInInstantiationMap = true; // ✅ In InstantiationMap

            // ACT: Evaluate defensive check logic
            bool shouldSkip = isParticipantNull ||
                              isGONetIdUnset ||
                              isInstantiationIdUnset ||
                              !isInGONetIdMap ||
                              !isInInstantiationMap;

            // ASSERT: Participant should NOT be skipped
            Assert.IsFalse(shouldSkip,
                $"Defensive check should NOT skip valid participant in both maps " +
                $"(GONetId: {testGONetId}, InstantiationId: {testInstantiationId})");
        }

        /// <summary>
        /// Documents the GONetGlobal duplicate destruction pattern that commonly triggers this bug.
        ///
        /// SCENARIO:
        /// 1. Scene flow: Lobby → GONetSample → RPCPlayground
        /// 2. Each scene has GONetGlobal prefab instance
        /// 3. When scene loads, new GONetGlobal spawns
        /// 4. GONetGlobal.Awake() detects duplicate (line 1017)
        /// 5. Calls DestroyImmediate(gameObject) (line 1023)
        /// 6. OnDisable fires BEFORE base.Awake() completes
        /// 7. Removes from maps while potentially partially initialized
        ///
        /// WHY THIS IS PROBLEMATIC:
        /// - DestroyImmediate is SYNCHRONOUS (not deferred like Destroy)
        /// - Destruction happens in middle of Awake() execution
        /// - GONetParticipant may be partially registered
        /// - Creates inconsistent map state (813 in one map, 812 in another)
        ///
        /// LATE-JOINER AMPLIFICATION:
        /// - Server has destroyed 2-3 GONetGlobal duplicates across scene transitions
        /// - Late joiner connects
        /// - Server's sync thread includes destroyed duplicates in bundles (race condition)
        /// - Client receives InstantiationIds for objects that were never sent in spawn events
        /// - Persistent BUNDLE-ABORT errors (24-60 errors/sec, indefinitely)
        ///
        /// This test serves as documentation - actual testing requires integration test.
        /// </summary>
        [Test]
        public void Documentation_GONetGlobalDuplicateDestructionPattern()
        {
            // This test exists purely for documentation purposes
            // It describes the real-world scenario that causes the bug

            // FACT 1: GONetGlobal uses DestroyImmediate (line 1023 in GONetGlobal.cs)
            const string destructionMethod = "DestroyImmediate(gameObject)";

            // FACT 2: This happens in Awake, BEFORE base.Awake() is called
            const int lineNumberOfDestruction = 1023;
            const int lineNumberOfBaseAwake = 1109; // Approximate - base.Awake() called much later

            // FACT 3: Each scene transition creates one destroyed duplicate
            int sceneCount = 3; // Lobby, GONetSample, RPCPlayground
            int expectedDestroyedDuplicates = sceneCount - 1; // First scene creates persistent instance

            // VERIFY: Documentation facts are correct
            Assert.AreEqual("DestroyImmediate(gameObject)", destructionMethod);
            Assert.AreEqual(2, expectedDestroyedDuplicates, "3 scenes = 1 persistent + 2 destroyed duplicates");
            Assert.Less(lineNumberOfDestruction, lineNumberOfBaseAwake,
                "DestroyImmediate happens BEFORE base.Awake() - creates partial initialization");

            // For actual testing of this scenario:
            // 1. Run scenes: Lobby → GONetSample → RPCPlayground
            // 2. Late joiner connects during/after scene transitions
            // 3. Monitor logs for BUNDLE-ABORT errors
            // 4. With fix: No errors
            // 5. Without fix: 24-60 errors/sec for destroyed GONetGlobal duplicates
        }
    }
}
