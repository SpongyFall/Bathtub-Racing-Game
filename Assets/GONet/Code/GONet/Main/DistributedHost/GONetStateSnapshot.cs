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
using System.Runtime.CompilerServices;
using MemoryPack;
using GONet.Utils;
using UnityEngine;

namespace GONet.DistributedHost
{
    /// <summary>
    /// Creates and manages state snapshots for host migration handoff.
    ///
    /// Key design principles:
    /// - Delta-only transfer when vice host is synchronized
    /// - Worker thread serialization to avoid main thread stalls
    /// - Compression for large scenes
    /// - Strict versioning for build compatibility
    /// </summary>
    public static class GONetStateSnapshotManager
    {
        #region Constants

        /// <summary>
        /// Current snapshot format version.
        /// Incompatible builds will reject snapshots with different versions.
        /// </summary>
        public const ushort SNAPSHOT_VERSION = 1;

        /// <summary>
        /// Magic number for snapshot validation.
        /// </summary>
        public const uint SNAPSHOT_MAGIC = 0x474F4E53; // "GONS" in ASCII

        /// <summary>
        /// Compression threshold - snapshots larger than this are compressed.
        /// </summary>
        public const int COMPRESSION_THRESHOLD_BYTES = 4096; // 4 KB

        #endregion

        #region State

        private static bool isCapturing;
        private static long captureStartTicks;
        private static GONetStateSnapshot currentSnapshot;

        /// <summary>
        /// Event fired when a snapshot is captured.
        /// </summary>
        public static event Action<GONetStateSnapshot> OnSnapshotCaptured;

        /// <summary>
        /// Event fired when a snapshot capture fails.
        /// </summary>
        public static event Action<string> OnSnapshotCaptureFailed;

        #endregion

        #region Properties

        /// <summary>
        /// Gets whether a capture is in progress.
        /// </summary>
        public static bool IsCapturing => isCapturing;

        /// <summary>
        /// Gets the most recent captured snapshot.
        /// </summary>
        public static GONetStateSnapshot CurrentSnapshot => currentSnapshot;

        #endregion

        #region Capture

        /// <summary>
        /// Captures a full state snapshot for handoff.
        /// This should be called on the host when initiating migration.
        /// </summary>
        /// <returns>The captured snapshot, or null if capture failed</returns>
        public static GONetStateSnapshot CaptureFullSnapshot()
        {
            if (isCapturing)
            {
                GONetLog.Warning("[StateSnapshot] Capture already in progress");
                return null;
            }

            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[StateSnapshot] Cannot capture snapshot - not the host");
                return null;
            }

            isCapturing = true;
            captureStartTicks = GONetMain.Time.ElapsedTicks;

            try
            {
                var snapshot = new GONetStateSnapshot
                {
                    Version = SNAPSHOT_VERSION,
                    Magic = SNAPSHOT_MAGIC,
                    CapturedAtTicks = captureStartTicks,
                    CapturedAtElapsedSeconds = GONetMain.Time.ElapsedSeconds,
                    HostEpoch = GONetMain.HostEpoch,
                    HostAuthorityId = GONetMain.MyAuthorityId,
                    SessionGUID = GONetMain.SessionGUID
                };

                // Capture persistent events
                CapturePeristentEvents(snapshot);

                // Capture GONetId allocation state
                CaptureGONetIdState(snapshot);

                // Capture time sync state
                CaptureTimeSyncState(snapshot);

                // Calculate checksum
                snapshot.Checksum = CalculateChecksum(snapshot);

                currentSnapshot = snapshot;
                isCapturing = false;

                float captureTimeMs = (GONetMain.Time.ElapsedTicks - captureStartTicks) / 10000f;
                GONetLog.Info($"[StateSnapshot] Captured full snapshot: {snapshot.PersistentEventCount} events, " +
                             $"capture time: {captureTimeMs:F2}ms");

                OnSnapshotCaptured?.Invoke(snapshot);
                return snapshot;
            }
            catch (Exception ex)
            {
                isCapturing = false;
                GONetLog.Error($"[StateSnapshot] Capture failed: {ex.Message}");
                OnSnapshotCaptureFailed?.Invoke(ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Captures a delta snapshot relative to a base sequence.
        /// Used when vice host has recent sync data.
        /// </summary>
        /// <param name="baseSyncSequence">The vice host's last acknowledged sync sequence</param>
        /// <returns>Delta snapshot, or null if delta not possible</returns>
        public static GONetStateSnapshotDelta CaptureDeltaSnapshot(ulong baseSyncSequence)
        {
            if (!GONetMain.IsServer)
            {
                GONetLog.Warning("[StateSnapshot] Cannot capture delta - not the host");
                return null;
            }

            // For now, return null to force full sync
            // TODO: Implement delta capture based on persistent events since baseSyncSequence
            GONetLog.Debug($"[StateSnapshot] Delta capture not yet implemented (base: {baseSyncSequence})");
            return null;
        }

        private static void CapturePeristentEvents(GONetStateSnapshot snapshot)
        {
            // Access persistent events from GONetMain
            var persistentEvents = GONetMain.PersistentEventsArchive_CompleteHistory;

            var eventRecords = new List<SnapshotEventRecord>();
            int index = 0;

            foreach (var evt in persistentEvents)
            {
                var record = new SnapshotEventRecord
                {
                    Index = index++,
                    EventTypeName = evt.GetType().AssemblyQualifiedName,
                    OccurredAtElapsedTicks = evt.OccurredAtElapsedTicks
                };

                // Serialize the event
                try
                {
                    record.SerializedData = SerializationUtils.SerializeToBytes(evt, out int bytesUsed, out bool needsReturn);
                    if (needsReturn)
                    {
                        // Make a copy since the buffer will be returned
                        var copy = new byte[bytesUsed];
                        Array.Copy(record.SerializedData, copy, bytesUsed);
                        SerializationUtils.ReturnByteArray(record.SerializedData);
                        record.SerializedData = copy;
                    }
                    else
                    {
                        // Trim to actual size
                        if (record.SerializedData.Length != bytesUsed)
                        {
                            var trimmed = new byte[bytesUsed];
                            Array.Copy(record.SerializedData, trimmed, bytesUsed);
                            record.SerializedData = trimmed;
                        }
                    }
                }
                catch (Exception ex)
                {
                    GONetLog.Warning($"[StateSnapshot] Failed to serialize event {evt.GetType().Name}: {ex.Message}");
                    record.SerializedData = Array.Empty<byte>();
                }

                eventRecords.Add(record);
            }

            snapshot.PersistentEvents = eventRecords;
            snapshot.PersistentEventCount = eventRecords.Count;
        }

        private static void CaptureGONetIdState(GONetStateSnapshot snapshot)
        {
            // TODO: Capture from GONetIdBatchManager
            // For now, set placeholder values
            snapshot.GONetIdWatermark = 0;
            snapshot.AllocatedBatches = new List<GONetIdBatchRecord>();
        }

        private static void CaptureTimeSyncState(GONetStateSnapshot snapshot)
        {
            snapshot.TimeElapsedSeconds = GONetMain.Time.ElapsedSeconds;
            snapshot.TimeElapsedTicks = GONetMain.Time.ElapsedTicks;
        }

        private static uint CalculateChecksum(GONetStateSnapshot snapshot)
        {
            // Simple checksum based on key values
            unchecked
            {
                uint hash = 17;
                hash = hash * 31 + snapshot.Version;
                hash = hash * 31 + (uint)snapshot.PersistentEventCount;
                hash = hash * 31 + snapshot.HostEpoch;
                hash = hash * 31 + snapshot.HostAuthorityId;
                hash = hash * 31 + (uint)(snapshot.CapturedAtTicks & 0xFFFFFFFF);
                return hash;
            }
        }

        #endregion

        #region Validation

        /// <summary>
        /// Validates a received snapshot before applying.
        /// </summary>
        /// <param name="snapshot">The snapshot to validate</param>
        /// <returns>True if valid, false otherwise</returns>
        public static bool ValidateSnapshot(GONetStateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                GONetLog.Error("[StateSnapshot] Validation failed: null snapshot");
                return false;
            }

            // Check magic number
            if (snapshot.Magic != SNAPSHOT_MAGIC)
            {
                GONetLog.Error($"[StateSnapshot] Validation failed: invalid magic (expected {SNAPSHOT_MAGIC:X8}, got {snapshot.Magic:X8})");
                return false;
            }

            // Check version compatibility
            if (snapshot.Version != SNAPSHOT_VERSION)
            {
                GONetLog.Error($"[StateSnapshot] Validation failed: version mismatch (expected {SNAPSHOT_VERSION}, got {snapshot.Version})");
                return false;
            }

            // Verify checksum
            uint expectedChecksum = CalculateChecksum(snapshot);
            if (snapshot.Checksum != expectedChecksum)
            {
                GONetLog.Error($"[StateSnapshot] Validation failed: checksum mismatch (expected {expectedChecksum:X8}, got {snapshot.Checksum:X8})");
                return false;
            }

            // Check session GUID matches
            if (snapshot.SessionGUID != GONetMain.SessionGUID)
            {
                GONetLog.Error("[StateSnapshot] Validation failed: session GUID mismatch");
                return false;
            }

            return true;
        }

        #endregion

        #region Application

        /// <summary>
        /// Applies a snapshot to restore host state.
        /// Called by the new host after handoff completes.
        /// </summary>
        /// <param name="snapshot">The snapshot to apply</param>
        /// <returns>True if applied successfully</returns>
        public static bool ApplySnapshot(GONetStateSnapshot snapshot)
        {
            if (!ValidateSnapshot(snapshot))
            {
                return false;
            }

            GONetLog.Info($"[StateSnapshot] Applying snapshot: epoch {snapshot.HostEpoch}, " +
                         $"{snapshot.PersistentEventCount} events");

            // TODO: Apply persistent events
            // TODO: Restore GONetId allocation state
            // TODO: Sync time

            return true;
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes a snapshot to bytes for network transmission.
        /// Applies compression if larger than threshold.
        /// </summary>
        public static byte[] SerializeSnapshot(GONetStateSnapshot snapshot)
        {
            var bytes = SerializationUtils.SerializeToBytes(snapshot, out int bytesUsed, out bool needsReturn);

            byte[] result;
            if (bytesUsed >= COMPRESSION_THRESHOLD_BYTES && bytesUsed <= ushort.MaxValue && GONetMain.AutoCompressEverything != null)
            {
                // Compress using the out-parameter API
                GONetMain.AutoCompressEverything.Compress(bytes, (ushort)bytesUsed, out byte[] compressed, out ushort compressedSize);
                result = new byte[compressedSize + 1];
                result[0] = 1; // Compression flag
                Array.Copy(compressed, 0, result, 1, compressedSize);
                GONetLog.Debug($"[StateSnapshot] Compressed {bytesUsed} -> {compressedSize} bytes ({100 * compressedSize / bytesUsed}%)");
            }
            else
            {
                result = new byte[bytesUsed + 1];
                result[0] = 0; // No compression
                Array.Copy(bytes, 0, result, 1, bytesUsed);
            }

            if (needsReturn)
            {
                SerializationUtils.ReturnByteArray(bytes);
            }

            return result;
        }

        /// <summary>
        /// Deserializes a snapshot from bytes.
        /// </summary>
        public static GONetStateSnapshot DeserializeSnapshot(byte[] data, int length)
        {
            if (length < 2)
            {
                GONetLog.Error("[StateSnapshot] Data too short to deserialize");
                return null;
            }

            bool isCompressed = data[0] == 1;
            ReadOnlySpan<byte> payload;

            if (isCompressed && GONetMain.AutoCompressEverything != null && (length - 1) <= ushort.MaxValue)
            {
                // Need to copy to a separate buffer for the API (it expects the compressed data to start at index 0)
                byte[] compressedData = new byte[length - 1];
                Array.Copy(data, 1, compressedData, 0, length - 1);
                GONetMain.AutoCompressEverything.Uncompress(compressedData, (ushort)(length - 1), out byte[] decompressed, out ushort decompressedSize);
                payload = new ReadOnlySpan<byte>(decompressed, 0, decompressedSize);
            }
            else
            {
                payload = new ReadOnlySpan<byte>(data, 1, length - 1);
            }

            return SerializationUtils.DeserializeFromBytes<GONetStateSnapshot>(payload);
        }

        #endregion
    }

    #region Snapshot Types

    /// <summary>
    /// Complete state snapshot for host migration.
    /// </summary>
    [MemoryPackable]
    public partial class GONetStateSnapshot
    {
        #region Header

        /// <summary>
        /// Magic number for validation.
        /// </summary>
        public uint Magic { get; set; }

        /// <summary>
        /// Snapshot format version.
        /// </summary>
        public ushort Version { get; set; }

        /// <summary>
        /// Checksum for integrity validation.
        /// </summary>
        public uint Checksum { get; set; }

        /// <summary>
        /// Tick when snapshot was captured.
        /// </summary>
        public long CapturedAtTicks { get; set; }

        /// <summary>
        /// Elapsed seconds when captured.
        /// </summary>
        public double CapturedAtElapsedSeconds { get; set; }

        #endregion

        #region Host Identity

        /// <summary>
        /// Session GUID for validation.
        /// </summary>
        public long SessionGUID { get; set; }

        /// <summary>
        /// Host epoch when captured.
        /// </summary>
        public uint HostEpoch { get; set; }

        /// <summary>
        /// Authority ID of the capturing host.
        /// </summary>
        public ushort HostAuthorityId { get; set; }

        #endregion

        #region Persistent Events

        /// <summary>
        /// Number of persistent events.
        /// </summary>
        public int PersistentEventCount { get; set; }

        /// <summary>
        /// Serialized persistent events.
        /// </summary>
        public List<SnapshotEventRecord> PersistentEvents { get; set; }

        #endregion

        #region GONetId Allocation

        /// <summary>
        /// Highest allocated GONetId raw value.
        /// </summary>
        public uint GONetIdWatermark { get; set; }

        /// <summary>
        /// Allocated batch records.
        /// </summary>
        public List<GONetIdBatchRecord> AllocatedBatches { get; set; }

        #endregion

        #region Time Sync

        /// <summary>
        /// Elapsed seconds from time authority.
        /// </summary>
        public double TimeElapsedSeconds { get; set; }

        /// <summary>
        /// Elapsed ticks from time authority.
        /// </summary>
        public long TimeElapsedTicks { get; set; }

        #endregion
    }

    /// <summary>
    /// Delta snapshot for efficient handoff when vice host is synchronized.
    /// </summary>
    [MemoryPackable]
    public partial class GONetStateSnapshotDelta
    {
        /// <summary>
        /// Base sync sequence this delta is relative to.
        /// </summary>
        public ulong BaseSyncSequence { get; set; }

        /// <summary>
        /// Tick when delta was captured.
        /// </summary>
        public long CapturedAtTicks { get; set; }

        /// <summary>
        /// New persistent events since base.
        /// </summary>
        public List<SnapshotEventRecord> NewEvents { get; set; }

        /// <summary>
        /// Cancelled event indices (despawns, etc).
        /// </summary>
        public List<int> CancelledEventIndices { get; set; }

        /// <summary>
        /// Updated GONetId watermark.
        /// </summary>
        public uint GONetIdWatermarkDelta { get; set; }
    }

    /// <summary>
    /// Record of a persistent event in a snapshot.
    /// </summary>
    [MemoryPackable]
    public partial class SnapshotEventRecord
    {
        /// <summary>
        /// Index in the persistent events list.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Fully qualified event type name for deserialization.
        /// </summary>
        public string EventTypeName { get; set; }

        /// <summary>
        /// Tick when event occurred.
        /// </summary>
        public long OccurredAtElapsedTicks { get; set; }

        /// <summary>
        /// Serialized event data.
        /// </summary>
        public byte[] SerializedData { get; set; }
    }

    /// <summary>
    /// Record of an allocated GONetId batch.
    /// </summary>
    [MemoryPackable]
    public partial class GONetIdBatchRecord
    {
        /// <summary>
        /// Authority ID that owns this batch.
        /// </summary>
        public ushort OwnerAuthorityId { get; set; }

        /// <summary>
        /// Starting raw ID of the batch.
        /// </summary>
        public uint StartId { get; set; }

        /// <summary>
        /// Size of the batch.
        /// </summary>
        public uint BatchSize { get; set; }

        /// <summary>
        /// Next ID to allocate from this batch.
        /// </summary>
        public uint NextId { get; set; }
    }

    #endregion
}
