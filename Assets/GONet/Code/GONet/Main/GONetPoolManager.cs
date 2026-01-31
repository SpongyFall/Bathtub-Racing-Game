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

using System;
using System.Collections.Generic;
using MemoryPack;
using UnityEngine;
using UnityEngine.SceneManagement;
using GONet.Utils;
using GONet.Generation;

namespace GONet
{
    internal static class GONetPoolManager
    {
        private struct PoolKey : IEquatable<PoolKey>
        {
            public ushort DesignTimeLocationIndex;
            public string SceneIdentifier;

            public bool Equals(PoolKey other)
            {
                return DesignTimeLocationIndex == other.DesignTimeLocationIndex &&
                       string.Equals(SceneIdentifier, other.SceneIdentifier, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is PoolKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = (hash * 31) + DesignTimeLocationIndex.GetHashCode();
                    hash = (hash * 31) + (SceneIdentifier != null ? SceneIdentifier.GetHashCode() : 0);
                    return hash;
                }
            }

            public override string ToString()
            {
                return $"Index={DesignTimeLocationIndex}, Scene='{SceneIdentifier}'";
            }
        }

        private struct RigidbodyDefaults
        {
            public bool IsKinematic;
            public bool UseGravity;
            public RigidbodyConstraints Constraints;
            public float Mass;
            public float Drag;
            public float AngularDrag;
            public RigidbodyInterpolation Interpolation;
            public CollisionDetectionMode CollisionDetectionMode;
        }

        private sealed class PoolState
        {
            public PoolKey Key;
            public GONetParticipant Prefab;
            public Transform Root;
            public bool PersistAcrossScenes;
            public int GrowByCount;
            public int MaxPoolSize;
            public bool IsSceneLoaded;

            public readonly Dictionary<uint, GONetParticipant> Entries = new Dictionary<uint, GONetParticipant>(128);
            public readonly Queue<uint> AvailableQueue = new Queue<uint>(128);
            public readonly HashSet<uint> AvailableSet = new HashSet<uint>();
            public readonly List<PoolIdRangeEntry> Ranges = new List<PoolIdRangeEntry>(4);
            public readonly HashSet<uint> DestroyedIds = new HashSet<uint>();
            public readonly Queue<PendingBorrowRequest> PendingBorrowRequests = new Queue<PendingBorrowRequest>();

            public readonly HashSet<uint> UniqueBorrowedIds = new HashSet<uint>();
            public int TotalBorrowEvents;
            public int ReusedBorrowEvents;
            public int PeakBorrowed;

            public readonly Dictionary<uint, RigidbodyDefaults> RigidbodyDefaultsById = new Dictionary<uint, RigidbodyDefaults>(128);
        }

        private static readonly Dictionary<PoolKey, PoolState> poolsByKey = new Dictionary<PoolKey, PoolState>(64);
        private static readonly Dictionary<uint, PoolState> poolByGONetId = new Dictionary<uint, PoolState>(1024);

        private static readonly List<PoolIdRangeEntry> deferredInitRanges = new List<PoolIdRangeEntry>(16);
        private static readonly List<PoolIdRangeEntry> deferredGrowthRanges = new List<PoolIdRangeEntry>(16);
        private static readonly Dictionary<uint, PoolObjectBorrowEvent> deferredBorrowEvents = new Dictionary<uint, PoolObjectBorrowEvent>(64);

        private struct PoolablePrefabInfo
        {
            public GONetParticipant Prefab;
            public GONetPooledObject Config;
            public ushort DesignTimeLocationIndex;
        }

        private struct PendingBorrowRequest
        {
            public PoolBorrowRequest Request;
            public ushort RequesterAuthorityId;
        }

        private struct PendingBorrowCallback
        {
            public Action<GONetParticipant> Callback;
            public string SceneIdentifier;
            public ushort DesignTimeLocationIndex;
        }

        private static readonly List<PoolablePrefabInfo> pooledPrefabs = new List<PoolablePrefabInfo>(32);
        private static bool pooledPrefabsCached;

        private static readonly HashSet<string> serverInitializedScenes = new HashSet<string>();
        private static readonly HashSet<string> pendingServerSceneInits = new HashSet<string>();
        private static readonly HashSet<string> scenesBeingUnloaded = new HashSet<string>();

        private static readonly Dictionary<uint, PendingBorrowCallback> pendingBorrowCallbacks = new Dictionary<uint, PendingBorrowCallback>(64);
        private static uint nextRequestId = 0;

        private static bool serverPoolsInitInProgress;
        private static bool poolSummaryLogged;

        private static long totalBorrowRequests;
        private static long totalBorrowRequestsGranted;
        private static long totalBorrowRequestsPending;
        private static long totalBorrowRequestsDenied;
        private static long totalReturnRequests;
        private static long totalBorrowEventsPublished;
        private static long totalReturnEventsPublished;
        private static long totalDestroyedEventsPublished;
        private static long totalInitEventsPublished;
        private static long totalGrowthEventsPublished;

        internal static void Update()
        {
            if (!GONetMain.IsClientVsServerStatusKnown)
            {
                return;
            }

            if (GONetMain.IsServer)
            {
                TryInitializeServerPools();
            }

            ProcessDeferredPoolEvents();
            ProcessPendingBorrowRequests();
        }

        internal static void ResetForNewSession()
        {
            poolsByKey.Clear();
            poolByGONetId.Clear();
            deferredInitRanges.Clear();
            deferredGrowthRanges.Clear();
            deferredBorrowEvents.Clear();
            pendingBorrowCallbacks.Clear();
            pooledPrefabs.Clear();
            pooledPrefabsCached = false;
            serverInitializedScenes.Clear();
            pendingServerSceneInits.Clear();
            scenesBeingUnloaded.Clear();
            nextRequestId = 0;
            serverPoolsInitInProgress = false;
            poolSummaryLogged = false;

            totalBorrowRequests = 0;
            totalBorrowRequestsGranted = 0;
            totalBorrowRequestsPending = 0;
            totalBorrowRequestsDenied = 0;
            totalReturnRequests = 0;
            totalBorrowEventsPublished = 0;
            totalReturnEventsPublished = 0;
            totalDestroyedEventsPublished = 0;
            totalInitEventsPublished = 0;
            totalGrowthEventsPublished = 0;
        }

        internal static void OnSceneLoadCompleted(string sceneName, LoadSceneMode mode)
        {
            scenesBeingUnloaded.Remove(sceneName);

            if (!GONetMain.IsClientVsServerStatusKnown)
            {
                return;
            }

            if (GONetMain.IsServer)
            {
                InitializeServerPoolsForScene(sceneName);
            }
        }

        internal static void OnSceneUnloadStarted(string sceneName, LoadSceneMode mode)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return;
            }

            HandleSceneUnload(sceneName);
        }

        internal static uint RequestBorrowFromPool(GONetParticipant prefab, Vector3 position, Quaternion rotation, Action<GONetParticipant> callback)
        {
            if (!GONetMain.IsClient)
            {
                GONetLog.Warning("[POOL] RequestBorrowFromPool called on non-client.");
                return 0;
            }

            if (prefab == null)
            {
                GONetLog.Warning("[POOL] RequestBorrowFromPool called with null prefab.");
                return 0;
            }

            var pooledConfig = prefab.GetComponent<GONetPooledObject>();
            if (pooledConfig == null)
            {
                GONetLog.Warning($"[POOL] Prefab '{prefab.name}' does not have GONetPooledObject component.");
                return 0;
            }

            ushort designTimeIndex = GONetSpawnSupport_Runtime.GetDesignTimeLocationIndex(prefab.DesignTimeLocation);
            string sceneIdentifier = ResolveSceneIdentifier(pooledConfig);

            uint requestId = GetNextRequestId();
            if (callback != null)
            {
                pendingBorrowCallbacks[requestId] = new PendingBorrowCallback
                {
                    Callback = callback,
                    SceneIdentifier = sceneIdentifier,
                    DesignTimeLocationIndex = designTimeIndex
                };
            }

            var request = new PoolBorrowRequest
            {
                DesignTimeLocationIndex = designTimeIndex,
                SceneIdentifier = sceneIdentifier,
                Position = position,
                Rotation = rotation,
                RequestId = requestId
            };

            if (GONetGlobal.Instance == null)
            {
                GONetLog.Error("[POOL] Cannot send borrow request - GONetGlobal.Instance is null.");
                return requestId;
            }

            if (GONetMain.IsServer && GONetMain.MyAuthorityId != GONetMain.OwnerAuthorityId_Unset)
            {
                Server_HandleBorrowRequest(request, GONetMain.MyAuthorityId);
                return requestId;
            }

            GONetGlobal.Instance.SendPoolBorrowRequest(request);
            return requestId;
        }

        internal static void RequestReturnToPool(GONetParticipant participant)
        {
            if (!GONetMain.IsClient)
            {
                GONetLog.Warning("[POOL] RequestReturnToPool called on non-client.");
                return;
            }

            if (participant == null)
            {
                GONetLog.Warning("[POOL] RequestReturnToPool called with null participant.");
                return;
            }

            if (GONetGlobal.Instance == null)
            {
                GONetLog.Error("[POOL] Cannot send return request - GONetGlobal.Instance is null.");
                return;
            }

            if (GONetMain.IsServer && GONetMain.MyAuthorityId != GONetMain.OwnerAuthorityId_Unset)
            {
                Server_HandleReturnRequest(participant.GONetId, GONetMain.MyAuthorityId);
                return;
            }

            GONetGlobal.Instance.SendPoolReturnRequest(participant.GONetId);
        }

        internal static GONetParticipant Server_BorrowFromPool(GONetParticipant prefab, Vector3 position, Quaternion rotation)
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[POOL] Server_BorrowFromPool called on non-server.");
                return null;
            }

            if (prefab == null)
            {
                GONetLog.Warning("[POOL] Server_BorrowFromPool called with null prefab.");
                return null;
            }

            if (!TryGetPoolForPrefab(prefab, out PoolState pool, out GONetPooledObject config))
            {
                GONetLog.Warning($"[POOL] No pool found for prefab '{prefab.name}'.");
                return null;
            }

            if (!TryTakeAvailable(pool, out GONetParticipant instance))
            {
                if (TryGrowPool_Server(pool, config?.growByCount ?? 0))
                {
                    if (!TryTakeAvailable(pool, out instance))
                    {
                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }

            PublishBorrowEvent(instance, position, rotation, GONetMain.OwnerAuthorityId_Server, requestId: 0);
            return instance;
        }

        internal static void Server_ReturnToPool(GONetParticipant participant)
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[POOL] Server_ReturnToPool called on non-server.");
                return;
            }

            if (participant == null)
            {
                GONetLog.Warning("[POOL] Server_ReturnToPool called with null participant.");
                return;
            }

            PublishReturnEvent(participant);
        }

        internal static void Server_HandleBorrowRequest(PoolBorrowRequest request, ushort requesterAuthorityId)
        {
            if (!GONetMain.IsServer)
            {
                return;
            }

            totalBorrowRequests++;

            if (!TryGetPoolForRequest(request, out PoolState pool, out GONetPooledObject config))
            {
                GONetLog.Warning($"[POOL] Borrow request rejected - pool not found. RequestId={request.RequestId}, Index={request.DesignTimeLocationIndex}, Scene='{request.SceneIdentifier}'");
                SendBorrowResponse(requesterAuthorityId, request.RequestId, PoolBorrowResponseStatus.Denied, 0);
                totalBorrowRequestsDenied++;
                return;
            }

            if (!TryTakeAvailable(pool, out GONetParticipant instance))
            {
                if (TryGrowPool_Server(pool, config?.growByCount ?? 0))
                {
                    if (!TryTakeAvailable(pool, out instance))
                    {
                        EnqueuePendingBorrowRequest(pool, request, requesterAuthorityId);
                        return;
                    }
                }
                else
                {
                    if (!EnqueuePendingBorrowRequest(pool, request, requesterAuthorityId))
                    {
                        SendBorrowResponse(requesterAuthorityId, request.RequestId, PoolBorrowResponseStatus.Denied, 0);
                        totalBorrowRequestsDenied++;
                    }
                    return;
                }
            }

            PublishBorrowEvent(instance, request.Position, request.Rotation, requesterAuthorityId, request.RequestId);
            SendBorrowResponse(requesterAuthorityId, request.RequestId, PoolBorrowResponseStatus.Granted, instance.GONetId);
            totalBorrowRequestsGranted++;
        }

        internal static void Server_HandleReturnRequest(uint gonetId, ushort requesterAuthorityId)
        {
            if (!GONetMain.IsServer)
            {
                return;
            }

            totalReturnRequests++;

            if (!poolByGONetId.TryGetValue(gonetId, out PoolState pool))
            {
                GONetLog.Warning($"[POOL] Return request ignored - pool not found for GONetId {gonetId}.");
                return;
            }

            if (!pool.Entries.TryGetValue(gonetId, out GONetParticipant instance) || instance == null)
            {
                GONetLog.Warning($"[POOL] Return request ignored - instance not found for GONetId {gonetId}.");
                return;
            }

            if (instance.RemotelyControlledByAuthorityId != requesterAuthorityId)
            {
                GONetLog.Warning($"[POOL] Return request denied - authority mismatch for GONetId {gonetId}. " +
                                 $"Requester={requesterAuthorityId}, Borrower={instance.RemotelyControlledByAuthorityId}");
                return;
            }

            PublishReturnEvent(instance);
        }

        internal static void Server_ReturnAllBorrowedByAuthority(ushort authorityId)
        {
            if (!GONetMain.IsServer)
            {
                return;
            }

            foreach (var kvp in poolByGONetId)
            {
                if (kvp.Value.Entries.TryGetValue(kvp.Key, out GONetParticipant instance) &&
                    instance != null && instance.RemotelyControlledByAuthorityId == authorityId)
                {
                    PublishReturnEvent(instance);
                }
            }
        }

        internal static void OnPoolInitializationEvent(GONetEventEnvelope<PoolInitializationEvent> envelope)
        {
            if (envelope?.Event == null)
            {
                return;
            }

            foreach (var range in envelope.Event.Ranges)
            {
                if (!IsSceneLoaded(range.SceneIdentifier))
                {
                    deferredInitRanges.Add(range);
                    continue;
                }

                ApplyPoolRange(range, isGrowth: false);
            }
        }

        internal static void OnPoolGrowthEvent(GONetEventEnvelope<PoolGrowthEvent> envelope)
        {
            if (envelope?.Event == null)
            {
                return;
            }

            foreach (var range in envelope.Event.Ranges)
            {
                if (!IsSceneLoaded(range.SceneIdentifier))
                {
                    deferredGrowthRanges.Add(range);
                    continue;
                }

                ApplyPoolRange(range, isGrowth: true);
            }
        }

        internal static void OnPoolBorrowEvent(GONetEventEnvelope<PoolObjectBorrowEvent> envelope)
        {
            if (envelope?.Event == null)
            {
                return;
            }

            PoolObjectBorrowEvent evt = envelope.Event;
            if (!poolByGONetId.TryGetValue(evt.GONetId, out PoolState pool) ||
                !pool.Entries.TryGetValue(evt.GONetId, out GONetParticipant instance) ||
                instance == null)
            {
                deferredBorrowEvents[evt.GONetId] = evt;
                return;
            }

            if (pool.DestroyedIds.Contains(evt.GONetId))
            {
                return;
            }

            ApplyBorrowEvent(instance, evt);
        }

        internal static void OnPoolReturnEvent(GONetEventEnvelope<PoolObjectReturnEvent> envelope)
        {
            if (envelope?.Event == null)
            {
                return;
            }

            uint gonetId = envelope.Event.GONetId;
            deferredBorrowEvents.Remove(gonetId);

            if (!poolByGONetId.TryGetValue(gonetId, out PoolState pool) ||
                !pool.Entries.TryGetValue(gonetId, out GONetParticipant instance) ||
                instance == null)
            {
                return;
            }

            ApplyReturnEvent(instance);

            if (GONetMain.IsServer)
            {
                TryFulfillPendingRequests(pool);
            }
        }

        internal static void OnPoolDestroyedEvent(GONetEventEnvelope<PoolObjectDestroyedEvent> envelope)
        {
            if (envelope?.Event == null)
            {
                return;
            }

            uint gonetId = envelope.Event.GONetId;
            if (!poolByGONetId.TryGetValue(gonetId, out PoolState pool))
            {
                return;
            }

            if (!pool.Entries.TryGetValue(gonetId, out GONetParticipant instance) || instance == null)
            {
                pool.DestroyedIds.Add(gonetId);
                poolByGONetId.Remove(gonetId);
                pool.AvailableSet.Remove(gonetId);
                pool.RigidbodyDefaultsById.Remove(gonetId);
                deferredBorrowEvents.Remove(gonetId);
                return;
            }

            pool.Entries.Remove(gonetId);
            poolByGONetId.Remove(gonetId);
            pool.AvailableSet.Remove(gonetId);
            pool.DestroyedIds.Add(gonetId);
            pool.RigidbodyDefaultsById.Remove(gonetId);
            deferredBorrowEvents.Remove(gonetId);

            if (!instance.isPoolDestructionInProgress)
            {
                instance.isPoolDestructionInProgress = true;
                UnityEngine.Object.Destroy(instance.gameObject);
            }
        }

        internal static void OnBorrowResponseReceived(uint requestId, PoolBorrowResponseStatus status, uint gonetId)
        {
            if (!pendingBorrowCallbacks.TryGetValue(requestId, out PendingBorrowCallback callbackInfo))
            {
                return;
            }

            if (status == PoolBorrowResponseStatus.Denied)
            {
                pendingBorrowCallbacks.Remove(requestId);
                callbackInfo.Callback?.Invoke(null);
                return;
            }

            if (status == PoolBorrowResponseStatus.Granted && gonetId != 0)
            {
                if (GONetMain.GetGONetParticipantById(gonetId) is GONetParticipant participant)
                {
                    pendingBorrowCallbacks.Remove(requestId);
                    callbackInfo.Callback?.Invoke(participant);
                }
            }
        }

        internal static void Server_HandlePooledObjectDestroyed(GONetParticipant participant, PoolObjectDestroyedReason reason)
        {
            if (!GONetMain.IsServer || participant == null)
            {
                return;
            }

            if (!participant.isPooled)
            {
                return;
            }

            participant.isPoolDestructionInProgress = true;

            var evt = new PoolObjectDestroyedEvent
            {
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks,
                GONetId = participant.GONetId,
                ReasonCode = reason
            };

            GONetMain.EventBus.Publish(evt);
            totalDestroyedEventsPublished++;
        }

        private static void TryInitializeServerPools()
        {
            if (serverPoolsInitInProgress)
            {
                return;
            }

            if (!GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached)
            {
                return;
            }

            serverPoolsInitInProgress = true;

            EnsurePooledPrefabsCached();
            InitializeServerPoolsForLoadedScenes();

            serverPoolsInitInProgress = false;
        }

        private static void InitializeServerPoolsForLoadedScenes()
        {
            GONetSceneManager sceneManager = GONetMain.SceneManager;
            int sceneCount = sceneManager.LoadedSceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                Scene scene = sceneManager.GetLoadedSceneAt(i);
                if (scene.isLoaded)
                {
                    InitializeServerPoolsForScene(scene.name);
                }
            }
        }

        private static void InitializeServerPoolsForScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return;
            }

            if (scenesBeingUnloaded.Contains(sceneName))
            {
                return;
            }

            if (serverInitializedScenes.Contains(sceneName) && !SceneNeedsPoolInitialization(sceneName))
            {
                return;
            }

            if (!pendingServerSceneInits.Add(sceneName))
            {
                return;
            }

            var initRanges = new List<PoolIdRangeEntry>(8);

            if (!EnsurePooledPrefabsCached())
            {
                pendingServerSceneInits.Remove(sceneName);
                return;
            }

            for (int i = 0; i < pooledPrefabs.Count; i++)
            {
                PoolablePrefabInfo info = pooledPrefabs[i];
                if (info.Prefab == null || info.Config == null)
                {
                    continue;
                }

                if (!IsPrefabAllowedForScene(info.Config, sceneName))
                {
                    continue;
                }

                string sceneIdentifier = info.Config.persistAcrossScenes
                    ? HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE
                    : sceneName;

                PoolKey key = new PoolKey
                {
                    DesignTimeLocationIndex = info.DesignTimeLocationIndex,
                    SceneIdentifier = sceneIdentifier
                };

                bool poolExists = poolsByKey.TryGetValue(key, out PoolState pool);
                if (!poolExists)
                {
                    pool = CreatePoolState(info.Prefab, info.Config, key);
                }

                if (pool == null)
                {
                    continue;
                }

                pool.IsSceneLoaded = true;
                EnsurePoolRoot(pool);

                if (poolExists && pool.Entries.Count > 0)
                {
                    continue;
                }

                if (pool.Ranges.Count > 0 && pool.Entries.Count == 0)
                {
                    for (int rangeIndex = 0; rangeIndex < pool.Ranges.Count; rangeIndex++)
                    {
                        PoolIdRangeEntry range = pool.Ranges[rangeIndex];
                        initRanges.Add(range);
                        InstantiatePoolRange(pool, range);
                    }

                    continue;
                }

                int initialSize = Mathf.Max(0, info.Config.suggestedInitialSize);
                if (initialSize > 0)
                {
                    AllocateAndInstantiate(pool, initialSize, initRanges);
                }
            }

            if (initRanges.Count > 0)
            {
                var initEvent = new PoolInitializationEvent
                {
                    OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks,
                    Ranges = initRanges
                };
                GONetMain.EventBus.Publish(initEvent);
                totalInitEventsPublished++;
                GONetLog.Info($"[POOL] Published PoolInitializationEvent with {initRanges.Count} range(s) for scene '{sceneName}'.");
            }

            serverInitializedScenes.Add(sceneName);
            pendingServerSceneInits.Remove(sceneName);
        }

        private static bool SceneNeedsPoolInitialization(string sceneName)
        {
            if (!EnsurePooledPrefabsCached())
            {
                return false;
            }

            for (int i = 0; i < pooledPrefabs.Count; i++)
            {
                PoolablePrefabInfo info = pooledPrefabs[i];
                if (info.Config == null)
                {
                    continue;
                }

                if (!IsPrefabAllowedForScene(info.Config, sceneName))
                {
                    continue;
                }

                string sceneIdentifier = info.Config.persistAcrossScenes
                    ? HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE
                    : sceneName;

                PoolKey key = new PoolKey
                {
                    DesignTimeLocationIndex = info.DesignTimeLocationIndex,
                    SceneIdentifier = sceneIdentifier
                };

                if (!poolsByKey.TryGetValue(key, out PoolState pool))
                {
                    return true;
                }

                if (!pool.PersistAcrossScenes && !pool.IsSceneLoaded)
                {
                    return true;
                }

                if (!pool.PersistAcrossScenes && pool.Entries.Count == 0 && pool.Ranges.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool EnsurePooledPrefabsCached()
        {
            if (pooledPrefabsCached)
            {
                return true;
            }

            if (!GONetSpawnSupport_Runtime.IsDesignTimeMetadataCached)
            {
                return false;
            }

            pooledPrefabsCached = true;
            pooledPrefabs.Clear();

            HashSet<ushort> seenIndexes = new HashSet<ushort>();
            foreach (DesignTimeMetadata metadata in GONetSpawnSupport_Runtime.GetAllDesignTimeMetadata())
            {
                if (metadata == null || string.IsNullOrWhiteSpace(metadata.Location))
                {
                    continue;
                }

                if (metadata.Location.StartsWith(GONetSpawnSupport_Runtime.SCENE_HIERARCHY_PREFIX, StringComparison.Ordinal))
                {
                    continue;
                }

                ushort designTimeIndex = GONetSpawnSupport_Runtime.GetDesignTimeLocationIndex(metadata.Location);
                if (!seenIndexes.Add(designTimeIndex))
                {
                    continue;
                }

                GONetParticipant prefab = GONetSpawnSupport_Runtime.LookupTemplateFromDesignTimeMetadata(metadata);
                if (prefab == null)
                {
                    continue;
                }

                GONetPooledObject pooledConfig = prefab.GetComponent<GONetPooledObject>();
                if (pooledConfig == null)
                {
                    continue;
                }

                pooledPrefabs.Add(new PoolablePrefabInfo
                {
                    Prefab = prefab,
                    Config = pooledConfig,
                    DesignTimeLocationIndex = designTimeIndex
                });
            }

            return true;
        }

        private static bool IsPrefabAllowedForScene(GONetPooledObject config, string sceneName)
        {
            if (config.initializeOnlyForScenes == null || config.initializeOnlyForScenes.Length == 0)
            {
                return true;
            }

            for (int i = 0; i < config.initializeOnlyForScenes.Length; i++)
            {
                if (string.Equals(config.initializeOnlyForScenes[i], sceneName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetPooledPrefabInfo(ushort designTimeIndex, out PoolablePrefabInfo info)
        {
            if (!EnsurePooledPrefabsCached())
            {
                info = default;
                return false;
            }

            for (int i = 0; i < pooledPrefabs.Count; i++)
            {
                PoolablePrefabInfo candidate = pooledPrefabs[i];
                if (candidate.DesignTimeLocationIndex == designTimeIndex)
                {
                    info = candidate;
                    return true;
                }
            }

            info = default;
            return false;
        }

        private static void ProcessDeferredPoolEvents()
        {
            if (deferredInitRanges.Count > 0)
            {
                for (int i = deferredInitRanges.Count - 1; i >= 0; --i)
                {
                    PoolIdRangeEntry range = deferredInitRanges[i];
                    if (IsSceneLoaded(range.SceneIdentifier))
                    {
                        deferredInitRanges.RemoveAt(i);
                        ApplyPoolRange(range, isGrowth: false);
                    }
                }
            }

            if (deferredGrowthRanges.Count > 0)
            {
                for (int i = deferredGrowthRanges.Count - 1; i >= 0; --i)
                {
                    PoolIdRangeEntry range = deferredGrowthRanges[i];
                    if (IsSceneLoaded(range.SceneIdentifier))
                    {
                        deferredGrowthRanges.RemoveAt(i);
                        ApplyPoolRange(range, isGrowth: true);
                    }
                }
            }

            if (deferredBorrowEvents.Count > 0)
            {
                var keys = new List<uint>(deferredBorrowEvents.Keys);
                foreach (uint gonetId in keys)
                {
                    if (poolByGONetId.TryGetValue(gonetId, out PoolState pool) &&
                        pool.Entries.TryGetValue(gonetId, out GONetParticipant instance) &&
                        instance != null)
                    {
                        if (pool.DestroyedIds.Contains(gonetId))
                        {
                            deferredBorrowEvents.Remove(gonetId);
                            continue;
                        }

                        PoolObjectBorrowEvent evt = deferredBorrowEvents[gonetId];
                        deferredBorrowEvents.Remove(gonetId);
                        ApplyBorrowEvent(instance, evt);
                    }
                }
            }
        }

        private static void ProcessPendingBorrowRequests()
        {
            if (!GONetMain.IsServer)
            {
                return;
            }

            foreach (var kvp in poolsByKey)
            {
                PoolState pool = kvp.Value;
                if (pool == null || !pool.IsSceneLoaded || pool.PendingBorrowRequests.Count == 0)
                {
                    continue;
                }

                if (pool.AvailableQueue.Count > 0)
                {
                    TryFulfillPendingRequests(pool);
                }
            }
        }

        private static void HandleSceneUnload(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return;
            }

            scenesBeingUnloaded.Add(sceneName);
            serverInitializedScenes.Remove(sceneName);

            RemoveDeferredRangesForScene(sceneName);

            foreach (var kvp in poolsByKey)
            {
                PoolState pool = kvp.Value;
                if (pool == null || pool.PersistAcrossScenes)
                {
                    continue;
                }

                if (!string.Equals(pool.Key.SceneIdentifier, sceneName, StringComparison.Ordinal))
                {
                    continue;
                }

                ClearPoolForSceneUnload(pool);
            }

            RemoveDeferredBorrowEventsForScene(sceneName);

            if (GONetMain.IsClient)
            {
                CancelPendingBorrowCallbacksForScene(sceneName);
            }
        }

        private static void RemoveDeferredRangesForScene(string sceneName)
        {
            for (int i = deferredInitRanges.Count - 1; i >= 0; --i)
            {
                if (string.Equals(deferredInitRanges[i].SceneIdentifier, sceneName, StringComparison.Ordinal))
                {
                    deferredInitRanges.RemoveAt(i);
                }
            }

            for (int i = deferredGrowthRanges.Count - 1; i >= 0; --i)
            {
                if (string.Equals(deferredGrowthRanges[i].SceneIdentifier, sceneName, StringComparison.Ordinal))
                {
                    deferredGrowthRanges.RemoveAt(i);
                }
            }
        }

        private static void RemoveDeferredBorrowEventsForScene(string sceneName)
        {
            if (deferredBorrowEvents.Count == 0)
            {
                return;
            }

            List<uint> toRemove = new List<uint>();
            foreach (var kvp in deferredBorrowEvents)
            {
                uint gonetId = kvp.Key;
                if (IsGONetIdInScenePools(sceneName, gonetId))
                {
                    toRemove.Add(gonetId);
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                deferredBorrowEvents.Remove(toRemove[i]);
            }
        }

        private static bool IsGONetIdInScenePools(string sceneName, uint gonetId)
        {
            foreach (var kvp in poolsByKey)
            {
                PoolState pool = kvp.Value;
                if (pool == null || !string.Equals(pool.Key.SceneIdentifier, sceneName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsGONetIdInPoolRanges(pool, gonetId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsGONetIdInPoolRanges(PoolState pool, uint gonetId)
        {
            if (pool == null || pool.Ranges.Count == 0)
            {
                return false;
            }

            uint rawId = gonetId >> GONetParticipant.GONET_ID_BIT_COUNT_UNUSED;
            for (int i = 0; i < pool.Ranges.Count; i++)
            {
                PoolIdRangeEntry range = pool.Ranges[i];
                uint start = range.GONetIdRawStart;
                uint end = start + range.Count;
                if (rawId >= start && rawId < end)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ClearPoolForSceneUnload(PoolState pool)
        {
            if (pool == null)
            {
                return;
            }

            pool.IsSceneLoaded = false;
            pool.Root = null;

            CancelPendingBorrowRequestsForPool(pool);

            List<uint> idsToClear = new List<uint>(pool.Entries.Count);
            foreach (var kvp in pool.Entries)
            {
                idsToClear.Add(kvp.Key);
            }

            for (int i = 0; i < idsToClear.Count; i++)
            {
                uint id = idsToClear[i];
                if (pool.Entries.TryGetValue(id, out GONetParticipant instance) && instance != null)
                {
                    instance.isPoolDestructionInProgress = true;

                    if (GONetMain.IsServer && instance.RemotelyControlledByAuthorityId != GONetMain.OwnerAuthorityId_Unset)
                    {
                        PublishReturnEvent(instance);
                    }

                    instance.IsOKToStartAutoMagicalProcessing = false;
                    instance.isPooledInactive = true;
                }

                poolByGONetId.Remove(id);
                deferredBorrowEvents.Remove(id);
            }

            pool.Entries.Clear();
            pool.AvailableQueue.Clear();
            pool.AvailableSet.Clear();
            pool.RigidbodyDefaultsById.Clear();
            pool.Ranges.Clear();
            pool.DestroyedIds.Clear();
        }

        private static void CancelPendingBorrowRequestsForPool(PoolState pool)
        {
            if (!GONetMain.IsServer || pool == null || pool.PendingBorrowRequests.Count == 0)
            {
                return;
            }

            while (pool.PendingBorrowRequests.Count > 0)
            {
                PendingBorrowRequest pending = pool.PendingBorrowRequests.Dequeue();
                if (pending.Request.RequestId != 0)
                {
                    SendBorrowResponse(pending.RequesterAuthorityId, pending.Request.RequestId, PoolBorrowResponseStatus.Denied, 0);
                }
            }
        }

        private static void CancelPendingBorrowCallbacksForScene(string sceneIdentifier)
        {
            if (pendingBorrowCallbacks.Count == 0)
            {
                return;
            }

            List<uint> toRemove = new List<uint>();
            foreach (var kvp in pendingBorrowCallbacks)
            {
                PendingBorrowCallback callback = kvp.Value;
                if (string.Equals(callback.SceneIdentifier, sceneIdentifier, StringComparison.Ordinal))
                {
                    toRemove.Add(kvp.Key);
                    callback.Callback?.Invoke(null);
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                pendingBorrowCallbacks.Remove(toRemove[i]);
            }
        }

        private static void ApplyPoolRange(PoolIdRangeEntry range, bool isGrowth)
        {
            PoolState pool = GetOrCreatePoolState(range);
            if (pool == null)
            {
                return;
            }

            InstantiatePoolRange(pool, range);

            if (isGrowth)
            {
                GONetLog.Debug($"[POOL] Applied growth range: {range.DesignTimeLocationIndex} start {range.GONetIdRawStart} count {range.Count} scene '{range.SceneIdentifier}'.");
            }

            if (GONetMain.IsServer)
            {
                TryFulfillPendingRequests(pool);
            }
        }

        private static void InstantiatePoolRange(PoolState pool, PoolIdRangeEntry range)
        {
            if (pool == null || pool.Prefab == null)
            {
                return;
            }

            pool.IsSceneLoaded = true;
            EnsurePoolRoot(pool);
            AddRangeIfMissing(pool, range);

            uint rawStart = range.GONetIdRawStart;
            uint rawEnd = rawStart + range.Count;

            for (uint rawId = rawStart; rawId < rawEnd; ++rawId)
            {
                uint gonetId = unchecked((uint)(rawId << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED)) | GONetMain.OwnerAuthorityId_Server;

                if (pool.DestroyedIds.Contains(gonetId))
                {
                    continue;
                }

                if (pool.Entries.ContainsKey(gonetId))
                {
                    continue;
                }

                GONetParticipant instance = UnityEngine.Object.Instantiate(pool.Prefab);
                if (instance == null)
                {
                    continue;
                }

                ConfigurePooledInstance(instance, pool, gonetId);
                pool.Entries[gonetId] = instance;
                poolByGONetId[gonetId] = pool;

                if (pool.AvailableSet.Add(gonetId))
                {
                    pool.AvailableQueue.Enqueue(gonetId);
                }
            }
        }

        private static void AddRangeIfMissing(PoolState pool, PoolIdRangeEntry range)
        {
            if (pool == null || range == null)
            {
                return;
            }

            for (int i = 0; i < pool.Ranges.Count; i++)
            {
                PoolIdRangeEntry existing = pool.Ranges[i];
                if (existing.DesignTimeLocationIndex == range.DesignTimeLocationIndex &&
                    existing.GONetIdRawStart == range.GONetIdRawStart &&
                    existing.Count == range.Count &&
                    string.Equals(existing.SceneIdentifier, range.SceneIdentifier, StringComparison.Ordinal))
                {
                    return;
                }
            }

            pool.Ranges.Add(new PoolIdRangeEntry
            {
                DesignTimeLocationIndex = range.DesignTimeLocationIndex,
                GONetIdRawStart = range.GONetIdRawStart,
                Count = range.Count,
                SceneIdentifier = range.SceneIdentifier,
                PersistAcrossScenes = range.PersistAcrossScenes
            });
        }

        private static void EnsurePoolRoot(PoolState pool)
        {
            if (pool == null)
            {
                return;
            }

            if (pool.Root == null)
            {
                pool.Root = CreatePoolRoot(pool.Prefab, pool.Key.DesignTimeLocationIndex, pool.Key.SceneIdentifier, pool.PersistAcrossScenes);
            }
        }

        private static void ConfigurePooledInstance(GONetParticipant instance, PoolState pool, uint gonetId)
        {
            instance.isPooled = true;
            instance.isPooledInactive = true;
            instance.RemotelyControlledByAuthorityId = GONetMain.OwnerAuthorityId_Unset;
            instance.OwnerAuthorityId = GONetMain.OwnerAuthorityId_Server;
            instance.SpawnerPersistentId = GONetParticipant.SpawnerPersistentId_NoSpawner;

            if (pool.Root != null)
            {
                instance.transform.SetParent(pool.Root, false);
            }

            GONetMain.AssignGONetIdRaw_Direct(instance, gonetId);
            instance.IsOKToStartAutoMagicalProcessing = false;
            CacheRigidbodyDefaultsIfNeeded(instance, pool);

            instance.isPoolReturnInProgress = true;
            instance.gameObject.SetActive(false);
            instance.isPoolReturnInProgress = false;

            if (pool.PersistAcrossScenes && pool.Root == null)
            {
                UnityEngine.Object.DontDestroyOnLoad(instance.gameObject);
            }
        }

        private static void CacheRigidbodyDefaultsIfNeeded(GONetParticipant instance, PoolState pool)
        {
            if (instance == null || pool == null)
            {
                return;
            }

            uint gonetId = instance.GONetId;
            if (gonetId == 0 || pool.RigidbodyDefaultsById.ContainsKey(gonetId))
            {
                return;
            }

            Rigidbody rb = instance.GetComponent<Rigidbody>();
            if (rb == null)
            {
                return;
            }

            pool.RigidbodyDefaultsById[gonetId] = new RigidbodyDefaults
            {
                IsKinematic = rb.isKinematic,
                UseGravity = rb.useGravity,
                Constraints = rb.constraints,
                Mass = rb.mass,
                Drag = rb.drag,
                AngularDrag = rb.angularDrag,
                Interpolation = rb.interpolation,
                CollisionDetectionMode = rb.collisionDetectionMode
            };
        }

        private static void RestoreRigidbodyDefaultsIfAuthority(GONetParticipant instance, PoolState pool)
        {
            if (instance == null || pool == null)
            {
                return;
            }

            if (!GONetMain.IsServer || !instance.IsMine)
            {
                return;
            }

            uint gonetId = instance.GONetId;
            if (gonetId == 0)
            {
                return;
            }

            if (!pool.RigidbodyDefaultsById.TryGetValue(gonetId, out RigidbodyDefaults defaults))
            {
                CacheRigidbodyDefaultsIfNeeded(instance, pool);
                if (!pool.RigidbodyDefaultsById.TryGetValue(gonetId, out defaults))
                {
                    return;
                }
            }

            Rigidbody rb = instance.GetComponent<Rigidbody>();
            if (rb == null)
            {
                return;
            }

            rb.isKinematic = defaults.IsKinematic;
            rb.useGravity = defaults.UseGravity;
            rb.constraints = defaults.Constraints;
            rb.mass = defaults.Mass;
            rb.drag = defaults.Drag;
            rb.angularDrag = defaults.AngularDrag;
            rb.interpolation = defaults.Interpolation;
            rb.collisionDetectionMode = defaults.CollisionDetectionMode;
        }

        private static void ApplyBorrowEvent(GONetParticipant instance, PoolObjectBorrowEvent evt)
        {
            if (instance == null)
            {
                return;
            }

            instance.isPoolBorrowInProgress = true;
            instance.isPooledInactive = false;
            instance.RemotelyControlledByAuthorityId = evt.BorrowerAuthorityId;
            instance.SetRigidBodySettingsConsideringOwner();
            instance.transform.SetPositionAndRotation(evt.Position, evt.Rotation);

            poolByGONetId.TryGetValue(evt.GONetId, out PoolState pool);
            if (pool != null)
            {
                RestoreRigidbodyDefaultsIfAuthority(instance, pool);
            }

            if (!instance.gameObject.activeSelf)
            {
                instance.gameObject.SetActive(true);
            }

            instance.IsOKToStartAutoMagicalProcessing = true;

            if (!instance.IsMine && !instance.v2_isRegisteredInSoA)
            {
                GONetMain.RegisterObjectInSoA(instance);
            }

            InvokePoolResetHooks(instance, isBorrow: true);

            instance.isPoolBorrowInProgress = false;

            GONetMain.CheckAndPublishOnGONetReady_IfAllConditionsMet(instance);

            if (pool != null)
            {
                pool.AvailableSet.Remove(evt.GONetId);

                pool.TotalBorrowEvents++;
                if (!pool.UniqueBorrowedIds.Add(evt.GONetId))
                {
                    pool.ReusedBorrowEvents++;
                }

                int borrowedNow = pool.Entries.Count - pool.AvailableSet.Count;
                if (borrowedNow > pool.PeakBorrowed)
                {
                    pool.PeakBorrowed = borrowedNow;
                }
            }

            if (evt.BorrowerAuthorityId == GONetMain.MyAuthorityId && evt.RequestId != 0 &&
                pendingBorrowCallbacks.TryGetValue(evt.RequestId, out PendingBorrowCallback callbackInfo))
            {
                pendingBorrowCallbacks.Remove(evt.RequestId);
                callbackInfo.Callback?.Invoke(instance);
            }
        }

        private static void ApplyReturnEvent(GONetParticipant instance)
        {
            if (instance == null)
            {
                return;
            }

            instance.RemotelyControlledByAuthorityId = GONetMain.OwnerAuthorityId_Unset;
            InvokePoolResetHooks(instance, isBorrow: false);
            instance.IsOKToStartAutoMagicalProcessing = false;

            instance.isPoolReturnInProgress = true;
            instance.gameObject.SetActive(false);
            instance.isPoolReturnInProgress = false;

            instance.isPooledInactive = true;

            if (poolByGONetId.TryGetValue(instance.GONetId, out PoolState pool))
            {
                if (pool.Root != null)
                {
                    bool suppressReparentEvent = instance.suppressReparentEvent;
                    instance.suppressReparentEvent = true;
                    instance.transform.SetParent(pool.Root, false);
                    instance.suppressReparentEvent = suppressReparentEvent;
                }

                if (pool.AvailableSet.Add(instance.GONetId))
                {
                    pool.AvailableQueue.Enqueue(instance.GONetId);
                }
            }

            GONetMain.OnReparentSelfCancel(instance);
            GONetMain.ClearPendingReparentsForTarget(instance.GONetId);
            instance.ResetReparentingStateForPooling();
        }

        private static void InvokePoolResetHooks(GONetParticipant instance, bool isBorrow)
        {
            MonoBehaviour[] behaviours = instance.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour is IGONetPoolResettable resettable)
                {
                    try
                    {
                        if (isBorrow)
                        {
                            resettable.ResetForPoolBorrow();
                        }
                        else
                        {
                            resettable.ResetForPoolReturn();
                        }
                    }
                    catch (Exception ex)
                    {
                        GONetLog.Warning($"[POOL] Pool reset hook threw exception on '{instance.name}': {ex.Message}");
                    }
                }
            }
        }

        private static bool TryGetPoolForRequest(PoolBorrowRequest request, out PoolState pool, out GONetPooledObject config)
        {
            PoolKey key = new PoolKey
            {
                DesignTimeLocationIndex = request.DesignTimeLocationIndex,
                SceneIdentifier = request.SceneIdentifier
            };

            if (poolsByKey.TryGetValue(key, out pool))
            {
                config = pool.Prefab != null ? pool.Prefab.GetComponent<GONetPooledObject>() : null;
                return true;
            }

            if (!GONetMain.IsServer)
            {
                config = null;
                return false;
            }

            if (!IsSceneLoaded(request.SceneIdentifier))
            {
                config = null;
                return false;
            }

            if (!TryGetPooledPrefabInfo(request.DesignTimeLocationIndex, out PoolablePrefabInfo info))
            {
                config = null;
                return false;
            }

            pool = CreatePoolState(info.Prefab, info.Config, key);
            if (pool == null)
            {
                config = null;
                return false;
            }

            pool.IsSceneLoaded = true;
            config = info.Config;

            int initialSize = config != null ? Mathf.Max(0, config.suggestedInitialSize) : 0;
            if (initialSize > 0)
            {
                var initRanges = new List<PoolIdRangeEntry>(4);
                AllocateAndInstantiate(pool, initialSize, initRanges);

                if (initRanges.Count > 0)
                {
                    var initEvent = new PoolInitializationEvent
                    {
                        OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks,
                        Ranges = initRanges
                    };
                    GONetMain.EventBus.Publish(initEvent);
                    totalInitEventsPublished++;
                    GONetLog.Warning($"[POOL] On-demand initialization for pool Index={key.DesignTimeLocationIndex}, Scene='{key.SceneIdentifier}'. " +
                                     "Consider adding this scene to initializeOnlyForScenes on the prefab's GONetPooledObject component.");
                }
            }

            return true;
        }

        private static bool TryGetPoolForPrefab(GONetParticipant prefab, out PoolState pool, out GONetPooledObject config)
        {
            config = prefab.GetComponent<GONetPooledObject>();
            if (config == null)
            {
                pool = null;
                return false;
            }

            string sceneIdentifier = ResolveSceneIdentifier(config);
            ushort designTimeIndex = GONetSpawnSupport_Runtime.GetDesignTimeLocationIndex(prefab.DesignTimeLocation);
            PoolKey key = new PoolKey { DesignTimeLocationIndex = designTimeIndex, SceneIdentifier = sceneIdentifier };

            if (poolsByKey.TryGetValue(key, out pool))
            {
                return true;
            }

            if (GONetMain.IsServer && IsSceneLoaded(sceneIdentifier))
            {
                pool = CreatePoolState(prefab, config, key);
                if (pool != null)
                {
                    pool.IsSceneLoaded = true;
                    return true;
                }
            }

            return false;
        }

        private static PoolState CreatePoolState(GONetParticipant prefab, GONetPooledObject config, PoolKey key)
        {
            PoolState pool = new PoolState
            {
                Key = key,
                Prefab = prefab,
                PersistAcrossScenes = config != null && config.persistAcrossScenes,
                GrowByCount = config != null ? Mathf.Max(0, config.growByCount) : 0,
                MaxPoolSize = config != null ? config.maxPoolSize : 0
            };

            pool.IsSceneLoaded = IsSceneLoaded(key.SceneIdentifier);
            pool.Root = CreatePoolRoot(prefab, key.DesignTimeLocationIndex, key.SceneIdentifier, pool.PersistAcrossScenes);
            poolsByKey[key] = pool;
            return pool;
        }

        private static PoolState GetOrCreatePoolState(PoolIdRangeEntry range)
        {
            PoolKey key = new PoolKey
            {
                DesignTimeLocationIndex = range.DesignTimeLocationIndex,
                SceneIdentifier = range.SceneIdentifier
            };

            if (poolsByKey.TryGetValue(key, out PoolState existing))
            {
                existing.IsSceneLoaded = IsSceneLoaded(range.SceneIdentifier);
                return existing;
            }

            string designTimeLocation = GONetSpawnSupport_Runtime.GetDesignTimeLocationFromIndex(range.DesignTimeLocationIndex);
            if (string.IsNullOrWhiteSpace(designTimeLocation))
            {
                GONetLog.Warning($"[POOL] Invalid design time index {range.DesignTimeLocationIndex}.");
                return null;
            }

            DesignTimeMetadata metadata = GONetSpawnSupport_Runtime.GetDesignTimeMetadata(designTimeLocation);
            if (metadata == null)
            {
                GONetLog.Warning($"[POOL] Missing metadata for index {range.DesignTimeLocationIndex}.");
                return null;
            }

            GONetParticipant prefab = GONetSpawnSupport_Runtime.LookupTemplateFromDesignTimeMetadata(metadata);
            if (prefab == null)
            {
                GONetLog.Warning($"[POOL] Could not resolve prefab for index {range.DesignTimeLocationIndex}.");
                return null;
            }

            var config = prefab.GetComponent<GONetPooledObject>();
            PoolState pool = new PoolState
            {
                Key = key,
                Prefab = prefab,
                PersistAcrossScenes = range.PersistAcrossScenes,
                GrowByCount = config != null ? Mathf.Max(0, config.growByCount) : 0,
                MaxPoolSize = config != null ? config.maxPoolSize : 0
            };

            pool.IsSceneLoaded = IsSceneLoaded(range.SceneIdentifier);
            pool.Root = CreatePoolRoot(prefab, key.DesignTimeLocationIndex, key.SceneIdentifier, pool.PersistAcrossScenes);
            poolsByKey[key] = pool;
            return pool;
        }

        private static Transform CreatePoolRoot(GONetParticipant prefab, ushort designTimeLocationIndex, string sceneIdentifier, bool persistAcrossScenes)
        {
            string prefabName = prefab != null ? prefab.name : "UnknownPrefab";
            string rootName = $"[GONetPool] {prefabName} ({sceneIdentifier})";
            var rootGo = new GameObject(rootName);

            // Add the pool monitor component for inspector visibility
            GONetPoolMonitor monitor = rootGo.AddComponent<GONetPoolMonitor>();
            monitor.Initialize(designTimeLocationIndex, sceneIdentifier, prefabName, persistAcrossScenes);

            if (persistAcrossScenes)
            {
                UnityEngine.Object.DontDestroyOnLoad(rootGo);
            }
            else if (!string.IsNullOrEmpty(sceneIdentifier))
            {
                GONetSceneManager sceneManager = GONetMain.SceneManager;
                Scene scene = sceneManager.GetSceneByName(sceneIdentifier);
                if (scene.IsValid())
                {
                    sceneManager.MoveGameObjectToScene(rootGo, scene);
                }
            }

            return rootGo.transform;
        }

        private static void AllocateAndInstantiate(PoolState pool, int count, List<PoolIdRangeEntry> rangesOut)
        {
            if (count <= 0)
            {
                return;
            }

            EnsurePoolRoot(pool);
            pool.IsSceneLoaded = true;

            List<uint> rawIds = new List<uint>(count);
            for (int i = 0; i < count; i++)
            {
                uint rawId = GONetMain.AllocateNextServerGONetIdRaw();
                rawIds.Add(rawId);
            }

            rawIds.Sort();
            int rangeStartIndex = 0;

            for (int i = 1; i <= rawIds.Count; i++)
            {
                bool isEnd = i == rawIds.Count || rawIds[i] != rawIds[i - 1] + 1;
                if (isEnd)
                {
                    uint startRaw = rawIds[rangeStartIndex];
                    ushort rangeCount = (ushort)(rawIds[i - 1] - startRaw + 1);
                    rangesOut.Add(new PoolIdRangeEntry
                    {
                        DesignTimeLocationIndex = pool.Key.DesignTimeLocationIndex,
                        SceneIdentifier = pool.Key.SceneIdentifier,
                        PersistAcrossScenes = pool.PersistAcrossScenes,
                        GONetIdRawStart = startRaw,
                        Count = rangeCount
                    });
                    AddRangeIfMissing(pool, rangesOut[rangesOut.Count - 1]);

                    rangeStartIndex = i;
                }
            }

            for (int i = 0; i < rawIds.Count; i++)
            {
                uint rawId = rawIds[i];
                uint gonetId = unchecked((uint)(rawId << GONetParticipant.GONET_ID_BIT_COUNT_UNUSED)) | GONetMain.OwnerAuthorityId_Server;

                GONetParticipant instance = UnityEngine.Object.Instantiate(pool.Prefab);
                if (instance == null)
                {
                    continue;
                }

                ConfigurePooledInstance(instance, pool, gonetId);
                pool.Entries[gonetId] = instance;
                poolByGONetId[gonetId] = pool;
                pool.AvailableSet.Add(gonetId);
                pool.AvailableQueue.Enqueue(gonetId);
            }

            DistributedHost.GONetViceHostManager.Instance?.UpdateGONetIdWatermark(rawIds[rawIds.Count - 1]);
        }

        private static bool TryTakeAvailable(PoolState pool, out GONetParticipant instance)
        {
            while (pool.AvailableQueue.Count > 0)
            {
                uint id = pool.AvailableQueue.Dequeue();
                if (pool.AvailableSet.Remove(id))
                {
                    if (pool.Entries.TryGetValue(id, out instance) && instance != null)
                    {
                        return true;
                    }
                }
            }

            instance = null;
            return false;
        }

        private static bool TryGrowPool_Server(PoolState pool, int growByCount)
        {
            if (growByCount <= 0)
            {
                return false;
            }

            int currentCount = pool.Entries.Count;
            if (pool.MaxPoolSize > 0 && currentCount >= pool.MaxPoolSize)
            {
                return false;
            }

            int actualGrow = pool.MaxPoolSize > 0 ? Mathf.Min(growByCount, pool.MaxPoolSize - currentCount) : growByCount;
            if (actualGrow <= 0)
            {
                return false;
            }

            var growthRanges = new List<PoolIdRangeEntry>(4);
            AllocateAndInstantiate(pool, actualGrow, growthRanges);
            if (growthRanges.Count == 0)
            {
                return false;
            }

            var growthEvent = new PoolGrowthEvent
            {
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks,
                Ranges = growthRanges
            };
            GONetMain.EventBus.Publish(growthEvent);
            totalGrowthEventsPublished++;
            GONetLog.Info($"[POOL] Published PoolGrowthEvent with {growthRanges.Count} range(s).");
            return true;
        }

        private static bool EnqueuePendingBorrowRequest(PoolState pool, PoolBorrowRequest request, ushort requesterAuthorityId)
        {
            if (pool == null || request.RequestId == 0)
            {
                return false;
            }

            pool.PendingBorrowRequests.Enqueue(new PendingBorrowRequest
            {
                Request = request,
                RequesterAuthorityId = requesterAuthorityId
            });

            SendBorrowResponse(requesterAuthorityId, request.RequestId, PoolBorrowResponseStatus.Pending, 0);
            totalBorrowRequestsPending++;
            return true;
        }

        private static void TryFulfillPendingRequests(PoolState pool)
        {
            if (pool == null || pool.PendingBorrowRequests.Count == 0)
            {
                return;
            }

            while (pool.PendingBorrowRequests.Count > 0 && TryTakeAvailable(pool, out GONetParticipant instance))
            {
                PendingBorrowRequest pending = pool.PendingBorrowRequests.Dequeue();
                PublishBorrowEvent(instance, pending.Request.Position, pending.Request.Rotation, pending.RequesterAuthorityId, pending.Request.RequestId);
                SendBorrowResponse(pending.RequesterAuthorityId, pending.Request.RequestId, PoolBorrowResponseStatus.Granted, instance.GONetId);
                totalBorrowRequestsGranted++;
            }
        }

        private static void PublishBorrowEvent(GONetParticipant instance, Vector3 position, Quaternion rotation, ushort borrowerAuthorityId, uint requestId)
        {
            var evt = new PoolObjectBorrowEvent
            {
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks,
                GONetId = instance.GONetId,
                BorrowerAuthorityId = borrowerAuthorityId,
                Position = position,
                Rotation = rotation,
                RequestId = requestId
            };
            GONetMain.EventBus.Publish(evt);
            totalBorrowEventsPublished++;
        }

        private static void PublishReturnEvent(GONetParticipant instance)
        {
            var evt = new PoolObjectReturnEvent
            {
                OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks,
                GONetId = instance.GONetId
            };
            GONetMain.EventBus.Publish(evt);
            totalReturnEventsPublished++;
        }

        private static void SendBorrowResponse(ushort targetAuthorityId, uint requestId, PoolBorrowResponseStatus status, uint gonetId)
        {
            if (GONetGlobal.Instance == null || requestId == 0)
            {
                return;
            }

            GONetGlobal.Instance.SendPoolBorrowResponse(targetAuthorityId, requestId, status, gonetId);
        }

        internal static void SynthesizePersistentEventsForPromotedHost()
        {
            if (!GONetMain.IsServer)
            {
                return;
            }

            var initRanges = new List<PoolIdRangeEntry>(16);
            foreach (var kvp in poolsByKey)
            {
                PoolState pool = kvp.Value;
                if (pool == null || !pool.IsSceneLoaded)
                {
                    continue;
                }

                AppendRangesFromEntries(pool, initRanges);
            }

            if (initRanges.Count > 0)
            {
                var initEvent = new PoolInitializationEvent
                {
                    OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks,
                    Ranges = initRanges
                };
                GONetMain.EventBus.Publish(initEvent);
                totalInitEventsPublished++;
            }

            foreach (var kvp in poolsByKey)
            {
                PoolState pool = kvp.Value;
                if (pool == null || !pool.IsSceneLoaded)
                {
                    continue;
                }

                foreach (var entry in pool.Entries)
                {
                    GONetParticipant instance = entry.Value;
                    if (instance == null || instance.IsPooledInactive)
                    {
                        continue;
                    }

                    if (instance.RemotelyControlledByAuthorityId == GONetMain.OwnerAuthorityId_Unset)
                    {
                        continue;
                    }

                    var borrowEvent = new PoolObjectBorrowEvent
                    {
                        OccurredAtElapsedTicks = GONetMain.Time.ElapsedTicks,
                        GONetId = instance.GONetId,
                        BorrowerAuthorityId = instance.RemotelyControlledByAuthorityId,
                        Position = instance.transform.position,
                        Rotation = instance.transform.rotation,
                        RequestId = 0
                    };
                    GONetMain.EventBus.Publish(borrowEvent);
                    totalBorrowEventsPublished++;
                }
            }
        }

        private static void AppendRangesFromEntries(PoolState pool, List<PoolIdRangeEntry> rangesOut)
        {
            if (pool == null || rangesOut == null || pool.Entries.Count == 0)
            {
                return;
            }

            List<uint> rawIds = new List<uint>(pool.Entries.Count);
            foreach (uint gonetId in pool.Entries.Keys)
            {
                uint rawId = gonetId >> GONetParticipant.GONET_ID_BIT_COUNT_UNUSED;
                rawIds.Add(rawId);
            }

            rawIds.Sort();
            int rangeStartIndex = 0;

            for (int i = 1; i <= rawIds.Count; i++)
            {
                bool isEnd = i == rawIds.Count || rawIds[i] != rawIds[i - 1] + 1;
                if (!isEnd)
                {
                    continue;
                }

                uint startRaw = rawIds[rangeStartIndex];
                ushort rangeCount = (ushort)(rawIds[i - 1] - startRaw + 1);
                rangesOut.Add(new PoolIdRangeEntry
                {
                    DesignTimeLocationIndex = pool.Key.DesignTimeLocationIndex,
                    SceneIdentifier = pool.Key.SceneIdentifier,
                    PersistAcrossScenes = pool.PersistAcrossScenes,
                    GONetIdRawStart = startRaw,
                    Count = rangeCount
                });

                rangeStartIndex = i;
            }
        }

        private static uint GetNextRequestId()
        {
            nextRequestId++;
            if (nextRequestId == 0)
            {
                nextRequestId++;
            }
            return nextRequestId;
        }

        private static bool IsSceneLoaded(string sceneIdentifier)
        {
            if (string.IsNullOrEmpty(sceneIdentifier))
            {
                return true;
            }

            if (sceneIdentifier == HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE)
            {
                return true;
            }

            return GONetMain.SceneManager.IsSceneLoaded(sceneIdentifier);
        }

        private static string ResolveSceneIdentifier(GONetPooledObject pooledConfig)
        {
            if (pooledConfig != null && pooledConfig.persistAcrossScenes)
            {
                return HierarchyUtils.DONT_DESTROY_ON_LOAD_SCENE;
            }

            Scene activeScene = GONetMain.SceneManager.ActiveScene;
            return activeScene.IsValid() ? activeScene.name : string.Empty;
        }

        internal static void LogPoolSummary(string reason)
        {
            if (poolSummaryLogged)
            {
                return;
            }

            poolSummaryLogged = true;

            long totalEntries = 0;
            long totalAvailable = 0;
            long totalDestroyed = 0;
            long totalRanges = 0;
            long totalPendingBorrowRequests = 0;
            long totalUniqueBorrowed = 0;
            long totalReuseBorrowEvents = 0;
            int maxPeakBorrowed = 0;

            foreach (var kvp in poolsByKey)
            {
                PoolState pool = kvp.Value;
                if (pool == null)
                {
                    continue;
                }

                totalEntries += pool.Entries.Count;
                totalAvailable += pool.AvailableSet.Count;
                totalDestroyed += pool.DestroyedIds.Count;
                totalRanges += pool.Ranges.Count;
                totalPendingBorrowRequests += pool.PendingBorrowRequests.Count;
                totalUniqueBorrowed += pool.UniqueBorrowedIds.Count;
                totalReuseBorrowEvents += pool.ReusedBorrowEvents;
                if (pool.PeakBorrowed > maxPeakBorrowed)
                {
                    maxPeakBorrowed = pool.PeakBorrowed;
                }
            }

            long totalBorrowed = totalEntries - totalAvailable;
            string reasonLabel = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;

            GONetLog.Info($"[POOL] Summary ({reasonLabel}) Pools={poolsByKey.Count}, Entries={totalEntries}, Available={totalAvailable}, Borrowed={totalBorrowed}, Destroyed={totalDestroyed}, Ranges={totalRanges}, PendingRequests={totalPendingBorrowRequests}.");
            GONetLog.Info($"[POOL] Summary Counters: BorrowRequests={totalBorrowRequests}, Granted={totalBorrowRequestsGranted}, Pending={totalBorrowRequestsPending}, Denied={totalBorrowRequestsDenied}, ReturnRequests={totalReturnRequests}, BorrowEvents={totalBorrowEventsPublished}, ReturnEvents={totalReturnEventsPublished}, DestroyedEvents={totalDestroyedEventsPublished}, InitEvents={totalInitEventsPublished}, GrowthEvents={totalGrowthEventsPublished}, UniqueBorrowed={totalUniqueBorrowed}, ReusedBorrows={totalReuseBorrowEvents}, PeakBorrowed={maxPeakBorrowed}.");
            GONetLog.Info($"[POOL] Summary Deferred: InitRanges={deferredInitRanges.Count}, GrowthRanges={deferredGrowthRanges.Count}, BorrowEvents={deferredBorrowEvents.Count}, PendingBorrowCallbacks={pendingBorrowCallbacks.Count}.");

            if (poolsByKey.Count > 0)
            {
                var pools = new List<PoolState>(poolsByKey.Values);
                pools.Sort((a, b) =>
                {
                    string aName = a?.Prefab != null ? a.Prefab.name : string.Empty;
                    string bName = b?.Prefab != null ? b.Prefab.name : string.Empty;
                    int nameCompare = string.CompareOrdinal(aName, bName);
                    if (nameCompare != 0)
                    {
                        return nameCompare;
                    }

                    string aScene = a?.Key.SceneIdentifier ?? string.Empty;
                    string bScene = b?.Key.SceneIdentifier ?? string.Empty;
                    return string.CompareOrdinal(aScene, bScene);
                });

                for (int i = 0; i < pools.Count; i++)
                {
                    PoolState pool = pools[i];
                    if (pool == null)
                    {
                        continue;
                    }

                    int borrowedNow = pool.Entries.Count - pool.AvailableSet.Count;
                    string prefabName = pool.Prefab != null ? pool.Prefab.name : "UnknownPrefab";
                    string rootName = pool.Root != null ? pool.Root.name : "NoRoot";
                    string sceneLabel = string.IsNullOrWhiteSpace(pool.Key.SceneIdentifier) ? "Global" : pool.Key.SceneIdentifier;
                    bool hasReuseEvidence = pool.ReusedBorrowEvents > 0;

                    GONetLog.Info($"[POOL] Summary Pool '{rootName}' Prefab='{prefabName}' Scene='{sceneLabel}' Index={pool.Key.DesignTimeLocationIndex} Entries={pool.Entries.Count}, Available={pool.AvailableSet.Count}, Borrowed={borrowedNow}, UniqueBorrowed={pool.UniqueBorrowedIds.Count}, ReusedBorrows={pool.ReusedBorrowEvents}, PeakBorrowed={pool.PeakBorrowed}, BorrowEvents={pool.TotalBorrowEvents}, ReuseEvidence={(hasReuseEvidence ? "YES" : "NO")}.");
                }
            }
        }

        internal static bool TryGetPoolSceneIdentifier(uint gonetId, out string sceneIdentifier)
        {
            if (poolByGONetId.TryGetValue(gonetId, out PoolState pool))
            {
                sceneIdentifier = pool.Key.SceneIdentifier;
                return true;
            }

            foreach (var kvp in poolsByKey)
            {
                PoolState candidate = kvp.Value;
                if (candidate != null && candidate.DestroyedIds.Contains(gonetId))
                {
                    sceneIdentifier = candidate.Key.SceneIdentifier;
                    return true;
                }
            }

            sceneIdentifier = null;
            return false;
        }

        /// <summary>
        /// Gets pool statistics for a specific GONetPooledObject instance.
        /// Returns stats for the pool associated with this prefab in the current scene context.
        /// </summary>
        internal static PoolStats GetPoolStats(GONetPooledObject pooledObject)
        {
            if (pooledObject == null)
            {
                return default;
            }

            GONetParticipant participant = pooledObject.GetComponent<GONetParticipant>();
            if (participant == null)
            {
                return default;
            }

            // Try to find pool by GONetId first (for runtime instances)
            if (participant.GONetId != 0 && poolByGONetId.TryGetValue(participant.GONetId, out PoolState poolById))
            {
                return BuildStats(poolById);
            }

            // Fall back to finding pool by design time location and scene
            string designTimeLocation = participant.DesignTimeLocation;
            if (string.IsNullOrWhiteSpace(designTimeLocation))
            {
                return default;
            }

            ushort designTimeIndex = GONetSpawnSupport_Runtime.GetDesignTimeLocationIndex(designTimeLocation);
            if (designTimeIndex == 0)
            {
                return default;
            }

            string sceneIdentifier = ResolveSceneIdentifier(pooledObject);

            PoolKey key = new PoolKey
            {
                DesignTimeLocationIndex = designTimeIndex,
                SceneIdentifier = sceneIdentifier
            };

            if (poolsByKey.TryGetValue(key, out PoolState pool))
            {
                return BuildStats(pool);
            }

            // Try to find any pool with this design time index (different scene identifiers)
            foreach (var kvp in poolsByKey)
            {
                if (kvp.Key.DesignTimeLocationIndex == designTimeIndex)
                {
                    return BuildStats(kvp.Value);
                }
            }

            return default;
        }

        /// <summary>
        /// Gets pool statistics for all pools associated with a prefab (across different scenes).
        /// </summary>
        internal static List<PoolStats> GetAllPoolStatsForPrefab(GONetPooledObject pooledObject)
        {
            var result = new List<PoolStats>();

            if (pooledObject == null)
            {
                return result;
            }

            GONetParticipant participant = pooledObject.GetComponent<GONetParticipant>();
            if (participant == null)
            {
                return result;
            }

            string designTimeLocation = participant.DesignTimeLocation;
            if (string.IsNullOrWhiteSpace(designTimeLocation))
            {
                return result;
            }

            ushort designTimeIndex = GONetSpawnSupport_Runtime.GetDesignTimeLocationIndex(designTimeLocation);
            if (designTimeIndex == 0)
            {
                return result;
            }

            foreach (var kvp in poolsByKey)
            {
                if (kvp.Key.DesignTimeLocationIndex == designTimeIndex)
                {
                    result.Add(BuildStats(kvp.Value));
                }
            }

            return result;
        }

        /// <summary>
        /// Gets pool statistics by pool key identifiers (used by GONetPoolMonitor).
        /// </summary>
        internal static PoolStats GetPoolStatsByKey(ushort designTimeLocationIndex, string sceneIdentifier)
        {
            PoolKey key = new PoolKey
            {
                DesignTimeLocationIndex = designTimeLocationIndex,
                SceneIdentifier = sceneIdentifier
            };

            if (poolsByKey.TryGetValue(key, out PoolState pool))
            {
                return BuildStats(pool);
            }

            return default;
        }

        /// <summary>
        /// Gets global pool manager statistics.
        /// </summary>
        internal static (int TotalPools, long TotalBorrowRequests, long TotalReturnRequests, long TotalBorrowEvents, long TotalReturnEvents) GetGlobalStats()
        {
            return (poolsByKey.Count, totalBorrowRequests, totalReturnRequests, totalBorrowEventsPublished, totalReturnEventsPublished);
        }

        private static PoolStats BuildStats(PoolState pool)
        {
            if (pool == null)
            {
                return default;
            }

            int availableCount = pool.AvailableSet.Count;
            int totalInstances = pool.Entries.Count;
            int borrowedCount = totalInstances - availableCount;

            return new PoolStats(
                isValid: true,
                prefabName: pool.Prefab != null ? pool.Prefab.name : "Unknown",
                sceneIdentifier: pool.Key.SceneIdentifier ?? string.Empty,
                designTimeLocationIndex: pool.Key.DesignTimeLocationIndex,
                persistAcrossScenes: pool.PersistAcrossScenes,
                isSceneLoaded: pool.IsSceneLoaded,
                totalInstances: totalInstances,
                availableCount: availableCount,
                borrowedCount: borrowedCount,
                destroyedCount: pool.DestroyedIds.Count,
                pendingBorrowRequests: pool.PendingBorrowRequests.Count,
                totalBorrowEvents: pool.TotalBorrowEvents,
                reusedBorrowEvents: pool.ReusedBorrowEvents,
                uniqueBorrowedCount: pool.UniqueBorrowedIds.Count,
                peakBorrowed: pool.PeakBorrowed,
                rangeCount: pool.Ranges.Count,
                growByCount: pool.GrowByCount,
                maxPoolSize: pool.MaxPoolSize);
        }
    }

    [MemoryPackable]
    public partial class PoolBorrowRequest
    {
        public ushort DesignTimeLocationIndex;
        public string SceneIdentifier;
        public Vector3 Position;
        public Quaternion Rotation;
        public uint RequestId;
    }

    internal enum PoolBorrowResponseStatus : byte
    {
        Granted = 0,
        Pending = 1,
        Denied = 2
    }
}
