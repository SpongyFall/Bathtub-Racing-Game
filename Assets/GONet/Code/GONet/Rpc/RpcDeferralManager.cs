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

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GONet
{
    /// <summary>
    /// Manages deferred RPCs that arrive before their target participant is registered.
    /// Instead of silently dropping RPCs for unknown participants, this manager queues them
    /// and executes them when the participant becomes available, with configurable timeout.
    /// </summary>
    public class RpcDeferralManager
    {
        /// <summary>
        /// Represents an RPC that has been deferred because its target participant was not found.
        /// </summary>
        public struct DeferredRpc
        {
            /// <summary>
            /// The GONetId of the target participant.
            /// </summary>
            public uint TargetGONetId;

            /// <summary>
            /// The RPC method identifier.
            /// </summary>
            public uint RpcId;

            /// <summary>
            /// The serialized RPC data (arguments).
            /// </summary>
            public byte[] SerializedData;

            /// <summary>
            /// Unity time when this RPC was enqueued.
            /// </summary>
            public float EnqueueTime;

            /// <summary>
            /// Timeout in seconds for this specific RPC.
            /// </summary>
            public float TimeoutSeconds;

            /// <summary>
            /// Callback to execute when the target participant becomes available.
            /// Takes the serialized data as parameter.
            /// </summary>
            public Action<byte[]> ExecuteCallback;

            /// <summary>
            /// Optional: The method name for logging purposes.
            /// </summary>
            public string MethodName;
        }

        private readonly Dictionary<uint, List<DeferredRpc>> _deferredByGONetId = new Dictionary<uint, List<DeferredRpc>>();
        private readonly float _defaultTimeoutSeconds;
        private readonly int _maxPerParticipant;

        // Object pool for lists to reduce allocations
        private readonly Stack<List<DeferredRpc>> _listPool = new Stack<List<DeferredRpc>>();

        /// <summary>
        /// Creates a new RpcDeferralManager with the specified configuration.
        /// </summary>
        /// <param name="defaultTimeoutSeconds">Default timeout for deferred RPCs. Uses GONetConfig if not specified.</param>
        /// <param name="maxPerParticipant">Maximum RPCs to queue per participant. Uses GONetConfig if not specified.</param>
        public RpcDeferralManager(float? defaultTimeoutSeconds = null, int? maxPerParticipant = null)
        {
            _defaultTimeoutSeconds = defaultTimeoutSeconds ?? GONetConfig.RpcDeferralTimeoutSeconds;
            _maxPerParticipant = maxPerParticipant ?? GONetConfig.MaxDeferredRpcsPerParticipant;
        }

        /// <summary>
        /// Defers an RPC for later execution when the target participant becomes available.
        /// </summary>
        /// <param name="targetGONetId">The GONetId of the target participant.</param>
        /// <param name="rpcId">The RPC method identifier.</param>
        /// <param name="data">The serialized RPC arguments.</param>
        /// <param name="executeCallback">Callback to invoke when participant is ready.</param>
        /// <param name="methodName">Optional method name for logging.</param>
        /// <param name="customTimeout">Optional custom timeout (uses default if not specified).</param>
        public void DeferRpc(uint targetGONetId, uint rpcId, byte[] data, Action<byte[]> executeCallback,
                            string methodName = null, float? customTimeout = null)
        {
            if (!GONetConfig.EnableRpcDeferralForUnknownParticipants)
            {
                GONetLog.Warning($"[RPC-DEFER] RPC deferral disabled - dropping RPC 0x{rpcId:X8} for GONetId {targetGONetId}");
                return;
            }

            if (!_deferredByGONetId.TryGetValue(targetGONetId, out var list))
            {
                list = GetListFromPool();
                _deferredByGONetId[targetGONetId] = list;
            }

            // Check if we've hit the max limit for this participant
            if (list.Count >= _maxPerParticipant)
            {
                // Remove oldest RPC to make room
                var oldest = list[0];
                list.RemoveAt(0);
                GONetLog.Warning($"[RPC-DEFER] Max deferred RPCs ({_maxPerParticipant}) reached for GONetId {targetGONetId}. " +
                               $"Dropping oldest RPC 0x{oldest.RpcId:X8} ({oldest.MethodName ?? "unknown"})");
            }

            var deferred = new DeferredRpc
            {
                TargetGONetId = targetGONetId,
                RpcId = rpcId,
                SerializedData = data,
                EnqueueTime = Time.unscaledTime,
                TimeoutSeconds = customTimeout ?? _defaultTimeoutSeconds,
                ExecuteCallback = executeCallback,
                MethodName = methodName
            };

            list.Add(deferred);

            if (GONetConfig.LogRpcDeferralDiagnostics)
            {
                GONetLog.Debug($"[RPC-DEFER] Queued RPC 0x{rpcId:X8} ({methodName ?? "unknown"}) for GONetId {targetGONetId} " +
                              $"(timeout: {deferred.TimeoutSeconds}s, queue size: {list.Count})");
            }
        }

        /// <summary>
        /// Called when a participant is registered. Processes any waiting RPCs.
        /// </summary>
        /// <param name="gonetId">The GONetId of the newly registered participant.</param>
        public void OnParticipantRegistered(uint gonetId)
        {
            if (!_deferredByGONetId.TryGetValue(gonetId, out var list))
                return;

            _deferredByGONetId.Remove(gonetId);

            if (GONetConfig.LogRpcDeferralDiagnostics)
            {
                GONetLog.Debug($"[RPC-DEFER] Processing {list.Count} deferred RPCs for newly registered GONetId {gonetId}");
            }

            int successCount = 0;
            int errorCount = 0;

            foreach (var deferred in list)
            {
                try
                {
                    deferred.ExecuteCallback?.Invoke(deferred.SerializedData);
                    successCount++;

                    if (GONetConfig.LogRpcDeferralDiagnostics)
                    {
                        float waitedSeconds = Time.unscaledTime - deferred.EnqueueTime;
                        GONetLog.Debug($"[RPC-DEFER] Executed deferred RPC 0x{deferred.RpcId:X8} ({deferred.MethodName ?? "unknown"}) " +
                                      $"for GONetId {gonetId} after {waitedSeconds:F2}s wait");
                    }
                }
                catch (Exception ex)
                {
                    errorCount++;
                    // Use Unity's Debug.LogError directly (not GONetLog) for two reasons:
                    // 1. Callback exceptions are developer errors that should ALWAYS be immediately visible
                    // 2. Synchronous logging is required for unit tests using LogAssert.Expect
                    UnityEngine.Debug.LogError($"[RPC-DEFER] Error executing deferred RPC 0x{deferred.RpcId:X8} ({deferred.MethodName ?? "unknown"}) " +
                                              $"for GONetId {gonetId}: {ex.Message}\n{ex.StackTrace}");
                }
            }

            if (errorCount > 0)
            {
                GONetLog.Warning($"[RPC-DEFER] Completed processing for GONetId {gonetId}: {successCount} succeeded, {errorCount} failed");
            }

            ReturnListToPool(list);
        }

        /// <summary>
        /// Called when a participant is removed/destroyed. Cleans up any pending RPCs.
        /// </summary>
        /// <param name="gonetId">The GONetId of the removed participant.</param>
        public void OnParticipantRemoved(uint gonetId)
        {
            if (_deferredByGONetId.TryGetValue(gonetId, out var list))
            {
                if (GONetConfig.LogRpcDeferralDiagnostics && list.Count > 0)
                {
                    GONetLog.Debug($"[RPC-DEFER] Clearing {list.Count} pending RPCs for removed participant GONetId {gonetId}");
                }

                _deferredByGONetId.Remove(gonetId);
                ReturnListToPool(list);
            }
        }

        /// <summary>
        /// Called each frame to check for timeouts and clean up expired entries.
        /// Should be called from GONetMain.Update().
        /// </summary>
        public void Update()
        {
            if (_deferredByGONetId.Count == 0)
                return;

            float now = Time.unscaledTime;
            List<uint> emptyKeys = null;

            foreach (var kvp in _deferredByGONetId)
            {
                int removedCount = kvp.Value.RemoveAll(deferred =>
                {
                    float waitedSeconds = now - deferred.EnqueueTime;
                    bool timedOut = waitedSeconds > deferred.TimeoutSeconds;

                    if (timedOut)
                    {
                        GONetLog.Warning($"[RPC-DEFER] RPC 0x{deferred.RpcId:X8} ({deferred.MethodName ?? "unknown"}) for GONetId {kvp.Key} " +
                                        $"timed out after {waitedSeconds:F2}s - participant never appeared");

                        GONetConfig.RaiseRpcDeferralTimeout(kvp.Key, deferred.RpcId, waitedSeconds);
                    }
                    return timedOut;
                });

                if (kvp.Value.Count == 0)
                {
                    emptyKeys ??= new List<uint>();
                    emptyKeys.Add(kvp.Key);
                }
            }

            if (emptyKeys != null)
            {
                foreach (var key in emptyKeys)
                {
                    if (_deferredByGONetId.TryGetValue(key, out var list))
                    {
                        _deferredByGONetId.Remove(key);
                        ReturnListToPool(list);
                    }
                }
            }
        }

        /// <summary>
        /// Gets statistics about currently deferred RPCs.
        /// </summary>
        /// <returns>Tuple of (participant count waiting, total RPC count waiting)</returns>
        public (int participantCount, int totalRpcCount) GetStats()
        {
            int total = 0;
            foreach (var list in _deferredByGONetId.Values)
            {
                total += list.Count;
            }
            return (_deferredByGONetId.Count, total);
        }

        /// <summary>
        /// Gets the number of deferred RPCs waiting for a specific participant.
        /// </summary>
        public int GetDeferredCountForParticipant(uint gonetId)
        {
            return _deferredByGONetId.TryGetValue(gonetId, out var list) ? list.Count : 0;
        }

        /// <summary>
        /// Clears all deferred RPCs. Use with caution - may cause state desync.
        /// </summary>
        public void ClearAll()
        {
            int totalCleared = 0;
            foreach (var list in _deferredByGONetId.Values)
            {
                totalCleared += list.Count;
                ReturnListToPool(list);
            }
            _deferredByGONetId.Clear();

            if (totalCleared > 0)
            {
                GONetLog.Warning($"[RPC-DEFER] Cleared {totalCleared} deferred RPCs across {_deferredByGONetId.Count} participants");
            }
        }

        #region Object Pooling

        private List<DeferredRpc> GetListFromPool()
        {
            if (_listPool.Count > 0)
            {
                var list = _listPool.Pop();
                list.Clear();
                return list;
            }
            return new List<DeferredRpc>();
        }

        private void ReturnListToPool(List<DeferredRpc> list)
        {
            if (list != null)
            {
                list.Clear();
                _listPool.Push(list);
            }
        }

        #endregion
    }
}
