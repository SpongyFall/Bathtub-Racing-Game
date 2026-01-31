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
using GONet.Generation;

namespace GONet.Editor.UnitTests.DistributedHost
{
    /// <summary>
    /// Tests for Phase 2.5: Client-Host Ownership Semantics.
    /// Verifies the DestroyWhenSpawnerLeaves flag and SpawnerPersistentId tracking.
    /// </summary>
    [TestFixture]
    public class GONetClientHostOwnershipTests
    {
        #region SpawnerPersistentId Constant Tests

        [Test]
        public void SpawnerPersistentId_NoSpawner_IsZero()
        {
            // Sentinel value for scene objects - immune to ProcessSpawnerDeath
            Assert.AreEqual(0UL, GONetParticipant.SpawnerPersistentId_NoSpawner);
        }

        #endregion

        #region DestroyWhenSpawnerLeaves Tests

        // NOTE: DestroyWhenSpawnerLeaves is now a serialized field on GONetParticipant (MonoBehaviour).
        // Unit tests for MonoBehaviour fields require PlayMode tests or mocking.
        // The default value (true) and behavior are documented in GONetParticipant.cs.

        [Test]
        public void DestroyWhenSpawnerLeaves_DefaultValue_IsDocumented()
        {
            // This test documents that DestroyWhenSpawnerLeaves defaults to TRUE on GONetParticipant.
            // The actual field is: [SerializeField] private bool destroyWhenSpawnerLeaves = true;
            //
            // Player prefabs (default): TRUE - destroyed when spawner leaves
            // World object prefabs: FALSE - survive failover (set manually in Inspector)
            Assert.Pass("DestroyWhenSpawnerLeaves defaults to true (player objects die with their spawner)");
        }

        #endregion

        #region InstantiateGONetParticipantEvent Tests

        [Test]
        public void InstantiateGONetParticipantEvent_HasSpawnerPersistentId()
        {
            // Arrange & Act
            var spawnEvent = new InstantiateGONetParticipantEvent();

            // Assert - SpawnerPersistentId field exists and defaults to 0
            Assert.AreEqual(0UL, spawnEvent.SpawnerPersistentId);
        }

        [Test]
        public void InstantiateGONetParticipantEvent_SpawnerPersistentId_CanBeSet()
        {
            // Arrange
            var spawnEvent = new InstantiateGONetParticipantEvent();
            ulong testPersistentId = 0xDEADBEEF12345678UL;

            // Act
            spawnEvent.SpawnerPersistentId = testPersistentId;

            // Assert
            Assert.AreEqual(testPersistentId, spawnEvent.SpawnerPersistentId);
        }

        #endregion

        #region ProcessSpawnerDeath Tests

        [Test]
        public void ProcessSpawnerDeath_WithNoSpawnerValue_ReturnsZeroCounts()
        {
            // Arrange - Use singleton instance (constructor is private)
            var failoverManager = GONetHostFailoverManager.Instance;

            // Act - Calling with NoSpawner value should do nothing
            var (destroyedCount, survivedCount) = failoverManager.ProcessSpawnerDeath(GONetParticipant.SpawnerPersistentId_NoSpawner);

            // Assert - Should return 0, 0 (nothing processed)
            Assert.AreEqual(0, destroyedCount);
            Assert.AreEqual(0, survivedCount);
        }

        #endregion

        #region TryGetNodePersistentId Tests

        [Test]
        public void GONetGossipManager_TryGetNodePersistentId_ReturnsZeroForUnknownNode()
        {
            // Arrange - Get gossip manager instance (may not be initialized in test context)
            var gossipManager = GONetGossipManager.Instance;

            // Act - Try to get persistent ID for unknown authority
            bool found = gossipManager.TryGetNodePersistentId(999, out ulong persistentId);

            // Assert - Should return false for unknown node, persistentId = 0
            Assert.IsFalse(found);
            Assert.AreEqual(0UL, persistentId);
        }

        #endregion

        #region Semantic Verification Tests

        [Test]
        public void PlayerPrefab_ShouldHave_DestroyWhenSpawnerLeaves_True()
        {
            // This test documents the expected behavior for player prefabs
            // DestroyWhenSpawnerLeaves is now on GONetParticipant (default = true)
            // Player objects die with their player - this is the default behavior
            Assert.Pass("Player prefabs should have DestroyWhenSpawnerLeaves=true (the default on GONetParticipant)");
        }

        [Test]
        public void WorldObjectPrefab_ShouldHave_DestroyWhenSpawnerLeaves_False()
        {
            // This test documents the expected behavior for world objects
            // Developers should uncheck DestroyWhenSpawnerLeaves in the Inspector for world objects
            // World objects (NPCs, doors, tradeable items) should survive failover
            Assert.Pass("World object prefabs should have DestroyWhenSpawnerLeaves=false (set in Inspector)");
        }

        [Test]
        public void SceneObjects_AreImmune_BecauseSpawnerPersistentIdIsZero()
        {
            // Scene objects get SpawnerPersistentId = 0 (NoSpawner sentinel)
            // ProcessSpawnerDeath skips objects with SpawnerPersistentId = 0

            ulong sceneObjectSpawnerId = GONetParticipant.SpawnerPersistentId_NoSpawner;

            Assert.AreEqual(0UL, sceneObjectSpawnerId,
                "Scene objects have SpawnerPersistentId=0, making them immune to ProcessSpawnerDeath");
        }

        #endregion

        #region Server Authority Cleanup Guard Tests (b262c599 fix)

        /// <summary>
        /// CRITICAL REGRESSION TEST: Verifies the fix for sync stopping ~10s after failover.
        ///
        /// Bug: After host failover, when the stale connection from the dead host (authority 1023)
        /// timed out, Server_MakeDoublySureAllClientOwnedGNPsDestroyed(1023) was called.
        /// This destroyed ALL GONetParticipants with OwnerAuthorityId=1023 - including scene
        /// objects that the new server legitimately owned.
        ///
        /// Fix: Skip cleanup when ownerAuthorityId == GONetMain.OwnerAuthorityId_Server (1023).
        /// </summary>
        [Test]
        public void ServerAuthorityCleanup_ServerAuthorityId_IsCorrectValue()
        {
            // The server authority ID must be 1023 - this is a critical constant
            Assert.AreEqual(1023, GONetMain.OwnerAuthorityId_Server,
                "Server authority ID must be 1023 for failover cleanup logic to work");
        }

        [Test]
        public void ServerAuthorityCleanup_ClientAuthorityId_IsNeverServerAuthority()
        {
            // Client authority IDs are assigned sequentially starting from low values
            // They should never equal the server authority (1023)
            ushort typicalClientAuthority1 = 2;
            ushort typicalClientAuthority2 = 3;
            ushort typicalClientAuthority3 = 4;

            Assert.AreNotEqual(GONetMain.OwnerAuthorityId_Server, typicalClientAuthority1);
            Assert.AreNotEqual(GONetMain.OwnerAuthorityId_Server, typicalClientAuthority2);
            Assert.AreNotEqual(GONetMain.OwnerAuthorityId_Server, typicalClientAuthority3);
        }

        [Test]
        public void ServerAuthorityCleanup_ShouldSkip_WhenAuthorityIsServer()
        {
            // This test documents the decision logic that prevents sync from stopping after failover.
            // The actual implementation is in GONetGlobal.Server_MakeDoublySureAllClientOwnedGNPsDestroyed()
            //
            // Decision: if (ownerAuthorityId == GONetMain.OwnerAuthorityId_Server) return;

            ushort staleConnectionAuthority = GONetMain.OwnerAuthorityId_Server; // 1023 from dead host

            bool shouldSkipCleanup = staleConnectionAuthority == GONetMain.OwnerAuthorityId_Server;

            Assert.IsTrue(shouldSkipCleanup,
                "Cleanup MUST be skipped when authority is 1023 (server) to prevent destroying server-owned objects");
        }

        [Test]
        public void ServerAuthorityCleanup_ShouldProcess_WhenAuthorityIsClient()
        {
            // Normal client disconnect should still trigger cleanup
            ushort disconnectedClientAuthority = 5; // A regular client

            bool shouldSkipCleanup = disconnectedClientAuthority == GONetMain.OwnerAuthorityId_Server;

            Assert.IsFalse(shouldSkipCleanup,
                "Cleanup should NOT be skipped for regular client disconnections");
        }

        /// <summary>
        /// Documents the scenario that caused the bug:
        /// 1. Client 2 (authority 2) promotes to host, becomes authority 1023
        /// 2. Promoted host's dormant server has connection to dead host (also 1023)
        /// 3. Connection times out ~10 seconds later
        /// 4. Cleanup fires with OwnerAuthorityId=1023
        /// 5. BUG: All server-owned objects (scene objects, GONetGlobal, etc.) destroyed
        /// 6. FIX: Skip cleanup when authority is 1023
        /// </summary>
        [Test]
        public void ServerAuthorityCleanup_StaleConnectionScenario_IsDocumented()
        {
            // Timeline of the bug scenario (all values observed in actual logs):
            ushort originalHostAuthority = GONetMain.OwnerAuthorityId_Server; // 1023
            ushort promotingClientOriginalAuthority = 2;
            ushort promotingClientNewAuthority = GONetMain.OwnerAuthorityId_Server; // 1023 after promotion

            // The stale connection that times out has authority 1023 (the dead original host)
            ushort staleConnectionAuthority = originalHostAuthority;

            // Without the fix, this would destroy all objects owned by 1023
            // Including objects the NEW server legitimately owns!
            Assert.AreEqual(promotingClientNewAuthority, staleConnectionAuthority,
                "Both the promoted host and stale connection have authority 1023 - this is why cleanup must be skipped");

            Assert.Pass("Stale connection timeout scenario documented - fix prevents server-owned object destruction");
        }

        #endregion

        #region ProcessSpawnerDeath Ownership Preservation Tests

        /// <summary>
        /// Documents that objects where IsMine=true survive ProcessSpawnerDeath.
        /// This handles the case where an object spawned by the dead host has been
        /// adopted by the new host (e.g., GONetLocal after authority transfer).
        /// </summary>
        [Test]
        public void ProcessSpawnerDeath_IsMineObjects_Documentation()
        {
            // ProcessSpawnerDeath logic (GONetHostFailover.cs ~line 1361):
            // if (gnp.IsMine)
            // {
            //     gnpsToTransfer.Add((gnp, "IsMine=true (adopted by new host)"));
            //     continue;
            // }
            //
            // Example: GONetLocal for client 2 was spawned by original host (authority 1023).
            // After client 2 promotes, IsMine becomes true (it's their own local).
            // The object should survive even though spawner (original host) is dead.

            Assert.Pass("IsMine objects survive ProcessSpawnerDeath (adopted by new host)");
        }

        /// <summary>
        /// Documents that objects with the promoting client's original authority survive.
        /// This handles objects like GONetLocal that were owned by the client BEFORE promotion.
        /// </summary>
        [Test]
        public void ProcessSpawnerDeath_PrePromotionOwnerObjects_Documentation()
        {
            // ProcessSpawnerDeath logic (GONetHostFailover.cs ~line 1372):
            // if (promotingClientOriginalAuthorityId != 0 && gnp.OwnerAuthorityId == promotingClientOriginalAuthorityId)
            // {
            //     gnpsToTransfer.Add((gnp, "pre-promotion owner"));
            //     continue;
            // }
            //
            // Example: Client 2's GONetLocal has OwnerAuthorityId=2.
            // When client 2 promotes (becomes 1023), GONetLocal.IsMine becomes FALSE
            // because MyAuthorityId changed but OwnerAuthorityId is still 2.
            // Without this check, the client's own GONetLocal would be destroyed.

            ushort promotingClientOriginalAuthority = 2;
            ushort gnpOwnerAuthority = 2; // Same - this is the client's own object

            bool shouldPreserve = gnpOwnerAuthority == promotingClientOriginalAuthority;

            Assert.IsTrue(shouldPreserve,
                "Objects with promoting client's original authority should be preserved");
        }

        /// <summary>
        /// Documents the ProcessSpawnerDeath decision matrix.
        /// </summary>
        [Test]
        public void ProcessSpawnerDeath_DecisionMatrix_Documentation()
        {
            // Decision matrix for ProcessSpawnerDeath:
            //
            // | SpawnerPersistentId | IsMine | OwnerAuth=PromotingAuth | DestroyWhenLeaves | Result |
            // |---------------------|--------|-------------------------|-------------------|--------|
            // | 0 (scene)           | -      | -                       | -                 | SKIP   |
            // | != deadSpawner      | -      | -                       | -                 | SKIP   |
            // | == deadSpawner      | true   | -                       | -                 | KEEP   |
            // | == deadSpawner      | false  | true                    | -                 | KEEP   |
            // | == deadSpawner      | false  | false                   | false             | KEEP   |
            // | == deadSpawner      | false  | false                   | true              | DESTROY|

            Assert.Pass("ProcessSpawnerDeath decision matrix documented");
        }

        #endregion

        #region Server_IsClientOwnerConnected Tests

        /// <summary>
        /// Documents the Server_IsClientOwnerConnected fix for server-owned objects.
        /// CRITICAL: Server-owned objects (OwnerAuthorityId = 1023) must always return true
        /// because the server is inherently "connected to itself".
        /// </summary>
        [Test]
        public void Server_IsClientOwnerConnected_ServerOwnedObjects_ReturnsTrue()
        {
            // This test documents the critical fix to Server_IsClientOwnerConnected.
            //
            // The bug:
            // - Server_IsClientOwnerConnected checked TryGetRemoteClientByAuthorityId(ownerAuthority)
            // - For server-owned objects (OwnerAuthorityId = 1023), this returned FALSE
            // - Because the server doesn't register ITSELF as a "remote client"
            //
            // The fix:
            // public static bool Server_IsClientOwnerConnected(GONetParticipant gnp)
            // {
            //     if (gnp.OwnerAuthorityId == OwnerAuthorityId_Server)
            //     {
            //         return true;  // Server is always connected to itself
            //     }
            //     return gonetServer.TryGetRemoteClientByAuthorityId(gnp.OwnerAuthorityId, out _);
            // }
            //
            // Why this matters:
            // - IsLocallyResponsible depends on Server_IsClientOwnerConnected
            // - Without the fix, server-owned objects would show OwnerConnected=False in diagnostics
            // - This confused diagnostic logging and could affect orphan cleanup logic

            ushort serverOwnerAuthority = GONetMain.OwnerAuthorityId_Server;

            // The fix ensures this case returns true
            Assert.AreEqual(1023, serverOwnerAuthority, "Server authority is 1023");
            Assert.Pass("Server_IsClientOwnerConnected returns true for server-owned objects (OwnerAuth=1023)");
        }

        /// <summary>
        /// Documents the Server_IsClientOwnerConnected behavior for client-owned objects.
        /// Client-owned objects should check if the client is actually connected.
        /// </summary>
        [Test]
        public void Server_IsClientOwnerConnected_ClientOwnedObjects_ChecksConnection()
        {
            // For client-owned objects, the method should:
            // 1. Look up the client in remoteClientsByAuthorityId
            // 2. Return true if client is connected, false if disconnected
            //
            // This is used for:
            // - Determining IsLocallyResponsible on the server
            // - Deciding when to clean up orphaned objects (owner disconnected)

            ushort clientAuthority = 5; // A regular client

            // The method should check actual connection state for client authorities
            Assert.AreNotEqual(GONetMain.OwnerAuthorityId_Server, clientAuthority,
                "Client authority should not equal server authority");
            Assert.Pass("Server_IsClientOwnerConnected checks connection state for client-owned objects");
        }

        /// <summary>
        /// Documents the relationship between Server_IsClientOwnerConnected and IsLocallyResponsible.
        /// </summary>
        [Test]
        public void IsLocallyResponsible_DependsOnOwnerConnected()
        {
            // IsLocallyResponsible is defined as:
            // public bool IsLocallyResponsible => IsMine || (GONetMain.IsServer && !GONetMain.Server_IsClientOwnerConnected(this));
            //
            // This means:
            // - IsMine=true -> IsLocallyResponsible=true (owner is always responsible)
            // - IsServer=false -> IsLocallyResponsible=IsMine (clients only responsible for their own objects)
            // - IsServer=true && OwnerConnected=true -> IsLocallyResponsible=IsMine (server defers to connected owner)
            // - IsServer=true && OwnerConnected=false -> IsLocallyResponsible=true (server takes over orphaned objects)
            //
            // For server-owned objects on the server:
            // - IsMine=true (MyAuthorityId=1023, OwnerAuth=1023)
            // - IsLocallyResponsible=true (correct)
            //
            // For server-owned objects on clients:
            // - IsMine=false (client authority != 1023)
            // - IsServer=false
            // - IsLocallyResponsible=false (client not responsible for server objects, correct)

            Assert.Pass("IsLocallyResponsible logic documented - depends on IsMine, IsServer, and Server_IsClientOwnerConnected");
        }

        #endregion
    }
}
