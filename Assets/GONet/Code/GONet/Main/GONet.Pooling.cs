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
using UnityEngine;

namespace GONet
{
    /// <summary>
    /// Pool statistics for monitoring and debugging pool performance.
    /// </summary>
    public readonly struct PoolStats
    {
        public readonly bool IsValid;
        public readonly string PrefabName;
        public readonly string SceneIdentifier;
        public readonly ushort DesignTimeLocationIndex;
        public readonly bool PersistAcrossScenes;
        public readonly bool IsSceneLoaded;
        public readonly int TotalInstances;
        public readonly int AvailableCount;
        public readonly int BorrowedCount;
        public readonly int DestroyedCount;
        public readonly int PendingBorrowRequests;
        public readonly int TotalBorrowEvents;
        public readonly int ReusedBorrowEvents;
        public readonly int UniqueBorrowedCount;
        public readonly int PeakBorrowed;
        public readonly int RangeCount;
        public readonly int GrowByCount;
        public readonly int MaxPoolSize;

        internal PoolStats(
            bool isValid,
            string prefabName,
            string sceneIdentifier,
            ushort designTimeLocationIndex,
            bool persistAcrossScenes,
            bool isSceneLoaded,
            int totalInstances,
            int availableCount,
            int borrowedCount,
            int destroyedCount,
            int pendingBorrowRequests,
            int totalBorrowEvents,
            int reusedBorrowEvents,
            int uniqueBorrowedCount,
            int peakBorrowed,
            int rangeCount,
            int growByCount,
            int maxPoolSize)
        {
            IsValid = isValid;
            PrefabName = prefabName;
            SceneIdentifier = sceneIdentifier;
            DesignTimeLocationIndex = designTimeLocationIndex;
            PersistAcrossScenes = persistAcrossScenes;
            IsSceneLoaded = isSceneLoaded;
            TotalInstances = totalInstances;
            AvailableCount = availableCount;
            BorrowedCount = borrowedCount;
            DestroyedCount = destroyedCount;
            PendingBorrowRequests = pendingBorrowRequests;
            TotalBorrowEvents = totalBorrowEvents;
            ReusedBorrowEvents = reusedBorrowEvents;
            UniqueBorrowedCount = uniqueBorrowedCount;
            PeakBorrowed = peakBorrowed;
            RangeCount = rangeCount;
            GrowByCount = growByCount;
            MaxPoolSize = maxPoolSize;
        }
    }

    public static partial class GONetMain
    {
        /// <summary>
        /// SERVER ONLY: Borrow a pooled instance immediately and broadcast a borrow event.
        /// </summary>
        public static GONetParticipant InstantiateFromPool(GONetParticipant prefab, Vector3 position, Quaternion rotation)
        {
            return GONetPoolManager.Server_BorrowFromPool(prefab, position, rotation);
        }

        /// <summary>
        /// SERVER ONLY: Return a pooled instance immediately and broadcast a return event.
        /// </summary>
        public static void ReturnToPool(GONetParticipant participant)
        {
            GONetPoolManager.Server_ReturnToPool(participant);
        }

        /// <summary>
        /// CLIENT ONLY: Request to borrow a pooled instance from the server.
        /// Callback is invoked when the matching borrow event arrives, or with null if denied.
        /// </summary>
        public static uint RequestBorrowFromPool(GONetParticipant prefab, Vector3 position, Quaternion rotation, Action<GONetParticipant> callback)
        {
            return GONetPoolManager.RequestBorrowFromPool(prefab, position, rotation, callback);
        }

        /// <summary>
        /// CLIENT ONLY: Request to return a pooled instance to the server.
        /// </summary>
        public static void RequestReturnToPool(GONetParticipant participant)
        {
            GONetPoolManager.RequestReturnToPool(participant);
        }

        /// <summary>
        /// Gets pool statistics for a specific GONetPooledObject instance.
        /// Useful for runtime monitoring and debugging pool performance.
        /// </summary>
        public static PoolStats GetPoolStats(GONetPooledObject pooledObject)
        {
            return GONetPoolManager.GetPoolStats(pooledObject);
        }

        /// <summary>
        /// Gets pool statistics for all pools associated with a prefab (across different scenes).
        /// </summary>
        public static System.Collections.Generic.List<PoolStats> GetAllPoolStatsForPrefab(GONetPooledObject pooledObject)
        {
            return GONetPoolManager.GetAllPoolStatsForPrefab(pooledObject);
        }

        /// <summary>
        /// Gets global pool manager statistics including total borrow/return counts.
        /// </summary>
        public static (int TotalPools, long TotalBorrowRequests, long TotalReturnRequests, long TotalBorrowEvents, long TotalReturnEvents) GetPoolManagerGlobalStats()
        {
            return GONetPoolManager.GetGlobalStats();
        }

        /// <summary>
        /// Gets pool statistics by pool key identifiers.
        /// Used by GONetPoolMonitor for inspector display.
        /// </summary>
        public static PoolStats GetPoolStatsByKey(ushort designTimeLocationIndex, string sceneIdentifier)
        {
            return GONetPoolManager.GetPoolStatsByKey(designTimeLocationIndex, sceneIdentifier);
        }
    }
}
