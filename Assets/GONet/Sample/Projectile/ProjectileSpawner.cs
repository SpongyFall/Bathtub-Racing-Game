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

using GONet;
using GONet.Sample;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class ProjectileSpawner : GONetBehaviour
{
    [SerializeField] private GONetParticipant projectilPrefab_zeroSync;
    [SerializeField] private GONetParticipant projectilPrefab;

    private readonly List<Projectile> projectiles = new(100);
    private readonly List<GONetParticipant> addressableParticipants = new(100); // For Physics Cube Projectiles (no Projectile component)
    private float lastCheckTime = 0f;
    const float CHECK_INTERVAL = 1f;

    // Track last known positions to detect idle projectiles
    private readonly Dictionary<uint, Vector3> lastKnownPositions = new Dictionary<uint, Vector3>();
    private readonly Dictionary<uint, float> lastMovementTime = new Dictionary<uint, float>();

    /// <summary>
    /// PERFORMANCE OPTIMIZATION: Single centralized subscription for ALL projectile transform updates.
    /// Instead of N projectiles each subscribing individually (N handler invocations per frame),
    /// we have 1 subscription that dispatches to the correct projectile (1 handler invocation + fast lookup).
    ///
    /// Benefits:
    /// - Reduces event bus overhead (1 subscription vs N subscriptions)
    /// - Reduces handler invocation overhead (1 call vs N calls in event bus loop)
    /// - Fast O(1) lookup via GONetId → Projectile mapping
    /// - Centralized lifecycle management (no per-projectile subscribe/unsubscribe)
    /// </summary>
    private readonly Dictionary<uint, Projectile> projectilesByGONetId = new Dictionary<uint, Projectile>(100);

    public override void OnGONetReady(GONetParticipant gonetParticipant) // NOTE:  OnGONetReady is the recommended approach for v1.5+ (instead of OnGONetParticipantEnabled/Started/Etc..
    {
        base.OnGONetReady(gonetParticipant);

        if (gonetParticipant.GetComponent<Projectile>() != null)
        {
            Projectile projectile = gonetParticipant.GetComponent<Projectile>();
            projectiles.Add(projectile);

            // PERFORMANCE OPTIMIZATION: Register projectile for centralized event dispatching
            projectilesByGONetId[gonetParticipant.GONetId] = projectile;

            // DIAGNOSTIC: Log when projectile is added to tracking list
            //GONetLog.Info($"[ProjectileSpawner] OnGONetReady() called for '{projectile.name}' (GONetId: {gonetParticipant.GONetId}) - IsMine: {gonetParticipant.IsMine}, Owner: {gonetParticipant.OwnerAuthorityId}, IsServer: {GONetMain.IsServer}, projectiles.Count: {projectiles.Count}");

            /* This was replaced in v1.1.1 with use of GONetMain.Client_InstantiateToBeRemotelyControlledByMe():
            if (GONetMain.IsServer && !projectile.GONetParticipant.IsMine)
            {
                GONetMain.Server_AssumeAuthorityOver(projectile.GONetParticipant);
            }
            */
        }

        // Check for Physics Cube addressable projectiles (INDEPENDENT of Projectile component check)
        // Note: Physics Cube Projectiles don't have a Projectile component, so we track the GONetParticipant directly
        if (gonetParticipant.gameObject.name.StartsWith("Physics Cube Projectile"))
        {
            // Add reparenting collision test component if not already present (for reparenting demo)
            var reparentCollision = gonetParticipant.GetComponent<PhysicsCubeReparentOnCollision>();
            if (reparentCollision == null)
            {
                reparentCollision = gonetParticipant.gameObject.AddComponent<PhysicsCubeReparentOnCollision>();
            }

            // Server-side tracking for cleanup
            if (GONetMain.IsServer && gonetParticipant.IsMine)
            {
                // Store the GONetParticipant in a separate tracking list since Physics Cube doesn't have Projectile component
                addressableParticipants.Add(gonetParticipant);
            }
        }
    }

    public override void OnGONetParticipantDisabled(GONetParticipant gonetParticipant)
    {
        base.OnGONetParticipantDisabled(gonetParticipant);

        // Remove from projectiles list if it has a Projectile component
        Projectile projectile = gonetParticipant.GetComponent<Projectile>();
        if (projectile != null)
        {
            projectiles.Remove(projectile);

            // PERFORMANCE OPTIMIZATION: Unregister from centralized event dispatching
            projectilesByGONetId.Remove(gonetParticipant.GONetId);
        }

        // Also remove from addressableParticipants list (for Physics Cube Projectiles)
        addressableParticipants.Remove(gonetParticipant);

        // Clean up tracking dictionaries
        uint gonetId = gonetParticipant.GONetId;
        lastKnownPositions.Remove(gonetId);
        lastMovementTime.Remove(gonetId);
    }

    /// <summary>
    /// Standard Unity Update() - Used for logic that doesn't depend on GONet initialization.
    ///
    /// PATTERN CHOICE: This demonstrates using BOTH Update() and UpdateAfterGONetReady():
    /// - Update() handles input polling (no GONet dependency)
    /// - UpdateAfterGONetReady() handles projectile movement (depends on OnGONetReady)
    ///
    /// ALTERNATIVE PATTERN: See SpawnTestBeacon.cs for defensive checks in Update() instead of using UpdateAfterGONetReady.
    ///
    /// SCRIPT EXECUTION ORDER NOTE:
    /// - This Update() runs at ProjectileSpawner's script execution order (default: 0)
    /// - UpdateAfterGONetReady() runs at GONetGlobal's order (-32000, very early in frame)
    /// - If you need precise script execution order control, use defensive Update() pattern instead
    /// </summary>
    private void Update()
    {
        // REPARENT TEST: Left-click on a reparented physics cube to detach it
        // This runs on any machine with a camera (server or client in host mode)
        // NOTE: DetachChild() itself only executes on server - see its implementation
        if (Input.GetMouseButtonDown(0))
        {
            if (TryDetachClickedPhysicsCube())
            {
                return; // Don't spawn projectiles if we clicked on a physics cube
            }
        }

        // INPUT HANDLING: Doesn't depend on GONet initialization, safe to run in Update()
        if (GONetMain.IsClient && projectilPrefab_zeroSync != null)
        {
            #region check keys and touches states
            bool shouldInstantiateBasedOnInput = Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.B);
            if (!shouldInstantiateBasedOnInput)
            {
                shouldInstantiateBasedOnInput = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2);
                if (!shouldInstantiateBasedOnInput)
                {
                    foreach (Touch touch in Input.touches)
                    {
                        if (touch.phase == TouchPhase.Began)
                        {
                            shouldInstantiateBasedOnInput = true;
                            break;
                        }
                    }
                }
            }
            #endregion
            if (shouldInstantiateBasedOnInput)
            {
                // Spawn projectiles in a spread pattern (160 degree arc)
                const int PROJECTILE_COUNT = 9;
                int pooledRequests = 0;
                int clientOwnedCount = 0;
                int serverOwnedCount = 0;
                const float SPREAD_ANGLE = 160f; // Total spread in degrees
                const float ANGLE_INCREMENT = PROJECTILE_COUNT > 1 ? SPREAD_ANGLE / (PROJECTILE_COUNT - 1) : 0f;
                const float START_ANGLE = PROJECTILE_COUNT > 1 ? -SPREAD_ANGLE / 2f : 0f;
                for (int i = 0; i < PROJECTILE_COUNT; i++)
                {
                    // Calculate angle for this projectile (relative to transform.forward)
                    float angleOffset = START_ANGLE + (i * ANGLE_INCREMENT);
                    // Manually calculate direction for vertical spread
                    // Start with forward direction, then rotate up/down using transform's right axis
                    Vector3 baseForward = transform.forward;
                    Vector3 rightAxis = transform.right; // Local X axis
                    Vector3 upComponent = transform.up * Mathf.Sin(angleOffset * Mathf.Deg2Rad);
                    Vector3 forwardComponent = baseForward * Mathf.Cos(angleOffset * Mathf.Deg2Rad);
                    Vector3 spreadDirection = (forwardComponent + upComponent);
                    // Defensive check: If spread direction is too small, fallback to forward
                    if (spreadDirection.sqrMagnitude < 0.0001f)
                    {
                        spreadDirection = transform.forward;
                    }
                    spreadDirection.Normalize();
                    // Create rotation that points in the spread direction
                    Quaternion spreadRotation = Quaternion.LookRotation(spreadDirection, transform.up);
                    GONetParticipant gnp = default;
                    bool shouldClientOwn = UnityEngine.Random.Range(0f, 1f) < 0.5f;
                    // TEMP DISABLED: Zero-sync projectiles confuse testing - they don't sync position at all
                    // bool shouldBeZeroSync = UnityEngine.Random.Range(0f, 1f) < 0.5f;
                    bool shouldBeZeroSync = false; // Force all projectiles to sync for testing
                    GONetParticipant prefabToUse = shouldBeZeroSync ? projectilPrefab_zeroSync : projectilPrefab;
                    if (TryRequestPooledProjectile(prefabToUse, transform.position, spreadRotation))
                    {
                        pooledRequests++;
                        continue;
                    }

                    if (shouldClientOwn)
                    {
                        gnp = Instantiate(prefabToUse, transform.position, spreadRotation);
                        clientOwnedCount++;
                    }
                    else
                    {
                        // Spawn using batch-aware API (now delegates internally to Try version with limbo fallback)
                        gnp = GONetMain.Client_InstantiateToBeRemotelyControlledByMe(prefabToUse, transform.position, spreadRotation);
                        serverOwnedCount++;
                    }

                    if (gnp == null)
                    {
                        GONetLog.Warning($"[SPAWN-DIAG] Projectile #{i} FAILED to spawn! shouldClientOwn={shouldClientOwn}, shouldBeZeroSync={shouldBeZeroSync}");
                    }
                }
                GONetLog.Info($"[SPAWN-DIAG] Spawned {PROJECTILE_COUNT} projectiles: {pooledRequests} pooled requests, {clientOwnedCount} client-owned, {serverOwnedCount} server-owned");

#if ADDRESSABLES_AVAILABLE
                const int PHYSICS_CUBE_COUNT = 5;
                // Spawn just ONE set of PHYSICS_CUBE_COUNT addressable physics cubes (not per projectile)
                for (int i = 0; i < PHYSICS_CUBE_COUNT; i++)
                {
                    float angleOffset = START_ANGLE + (i * (SPREAD_ANGLE / 4f)); // 5 cubes spread across arc

                    Vector3 baseForward = transform.forward;
                    Vector3 upComponent = transform.up * Mathf.Sin(angleOffset * Mathf.Deg2Rad);
                    Vector3 forwardComponent = baseForward * Mathf.Cos(angleOffset * Mathf.Deg2Rad);
                    Vector3 spreadDirection = (forwardComponent + upComponent).normalized;
                    Quaternion cubeRotation = Quaternion.LookRotation(spreadDirection, transform.up);

                    InstantiateAddressablesPrefab(cubeRotation);
                }
#endif
            }
        }

        // ADDRESSABLE CLEANUP: Server-side only, doesn't depend on per-projectile GONet state
        if (GONetMain.IsServer && Time.time - lastCheckTime >= CHECK_INTERVAL)
        {
            lastCheckTime = Time.time;
            DestroyAddressableProjectilesOutOfView();
        }
    }

    // NOTE: ProjectileSpawner no longer needs UpdateAfterGONetReady for movement.
    // Movement logic has been moved to Projectile.UpdateAfterGONetReady() for better encapsulation.
    // Each projectile now handles its own movement using the UpdateAfterGONetReady pattern.
    //
    // See Projectile.UpdateAfterGONetReady() for the movement implementation.

#if ADDRESSABLES_AVAILABLE
    private async Task InstantiateAddressablesPrefab(Quaternion rotation)
    {
        const string oohLaLa_addressablesPrefabPath = "Assets/GONet/Sample/Projectile/AddressablesOohLaLa/Physics Cube Projectile.prefab";

        // PERFORMANCE: Use LoadGONetPrefabAsync_Cached() for repeated spawning
        // First call loads from Addressables (~50ms), subsequent calls return instantly from cache (~0.01ms)
        // This is called 5 times per click, so caching provides ~250ms savings per click!
        GONetParticipant addressablePrefab = await GONetAddressablesHelper.LoadGONetPrefabAsync_Cached(oohLaLa_addressablesPrefabPath);

        if (TryRequestPooledProjectile(addressablePrefab, transform.position, rotation))
        {
            return;
        }

        // LoadGONetPrefabAsync_Cached guarantees we're back on Unity main thread after await
        // Safe to call Unity APIs now
        GONetParticipant addressableInstance =
            GONetMain.Client_InstantiateToBeRemotelyControlledByMe(addressablePrefab, transform.position, rotation);
    }
#endif

    /// <summary>
    /// Server-side cleanup: Destroys addressable projectiles that have traveled too far from spawn origin.
    /// Uses distance-based check instead of frustum culling because:
    /// 1. Server typically has no camera (headless)
    /// 2. We don't know which client camera to use
    /// 3. Distance check is simpler and more predictable
    /// </summary>
    private void DestroyAddressableProjectilesOutOfView()
    {
        const float MAX_DISTANCE = 50f; // Destroy projectiles beyond this distance from spawn origin
        Vector3 spawnOrigin = transform.position; // Spawner's position

        int destroyedCount = 0;
        for (int i = addressableParticipants.Count - 1; i >= 0; --i)
        {
            GONetParticipant participant = addressableParticipants[i];
            if (participant == null || participant.gameObject == null)
            {
                // Already destroyed, remove from list
                addressableParticipants.RemoveAt(i);
                continue;
            }

            float distanceFromSpawn = Vector3.Distance(participant.transform.position, spawnOrigin);

            if (distanceFromSpawn > MAX_DISTANCE)
            {
                if (participant.IsPooled)
                {
                    GONetMain.ReturnToPool(participant);
                }
                else
                {
                    Destroy(participant.gameObject);
                }
                destroyedCount++;
            }
        }
    }

    /// <summary>
    /// Attempts to detach a reparented physics cube by raycasting from the mouse position.
    /// If the clicked object is a parent or child in a reparent relationship, detaches them.
    ///
    /// NOTE: DetachChild() only executes on the server. In host mode (server + client on same machine),
    /// this works because the machine is both server and client. In dedicated server setup, the client
    /// click will be detected but DetachChild() won't execute (would require RPC for that case).
    /// </summary>
    /// <returns>True if a detachment was triggered (or attempted), false otherwise</returns>
    private bool TryDetachClickedPhysicsCube()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            return false;
        }

        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 100f))
        {
            // Check if we hit a physics cube with reparenting component
            var reparentComponent = hit.collider.GetComponent<PhysicsCubeReparentOnCollision>();
            if (reparentComponent == null)
            {
                return false;
            }

            // Check if it's in a parent/child relationship and detach if so
            if (reparentComponent.IsParent)
            {
                reparentComponent.DetachChild();
            }
            else if (reparentComponent.IsChild)
            {
                var parentReparent = reparentComponent.transform.parent?.GetComponent<PhysicsCubeReparentOnCollision>();
                if (parentReparent != null)
                {
                    parentReparent.DetachChild();
                }
            }

            // Always return true when clicking on a physics cube to prevent spawning
            return true;
        }

        return false;
    }

    private bool TryRequestPooledProjectile(GONetParticipant prefab, Vector3 position, Quaternion rotation)
    {
        if (prefab == null)
        {
            return false;
        }

        if (prefab.GetComponent<GONetPooledObject>() == null)
        {
            return false;
        }

        GONetMain.RequestBorrowFromPool(prefab, position, rotation, borrowed =>
        {
            if (borrowed == null)
            {
                GONetLog.Warning($"[POOL] Projectile borrow request denied for prefab '{prefab.name}'.");
            }
        });

        return true;
    }
}
