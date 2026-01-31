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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GONet
{
    /// <summary>
    /// Represents the current host's identity in a distributed host topology.
    /// Combines session GUID, epoch (migration counter), and authority IDs.
    ///
    /// Used for:
    /// - Split-brain prevention: Messages with stale epochs are rejected
    /// - Host validation: Ensures messages come from the legitimate host
    /// - Vice host designation: "Monarch Selects Heir" pattern
    ///
    /// Size: 16 bytes (unmanaged struct for efficient serialization)
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HostIdentity : IEquatable<HostIdentity>
    {
        /// <summary>
        /// Session GUID - immutable for the lifetime of a session.
        /// All peers in the same session share this value.
        /// </summary>
        public long SessionGUID;

        /// <summary>
        /// Host epoch - monotonically increasing counter that increments on each host migration.
        /// Epoch 0 = initial host. Higher epoch always wins in conflict resolution.
        /// </summary>
        public uint HostEpoch;

        /// <summary>
        /// Authority ID of the current host.
        /// When distributed host is disabled, this is always OwnerAuthorityId_Server (0).
        /// </summary>
        public ushort HostAuthorityId;

        /// <summary>
        /// Authority ID of the designated vice host (heir apparent).
        /// Set by the current host ("Monarch Selects Heir" pattern).
        /// 0 if no vice host is designated.
        /// On emergency failover, only this designated node can self-promote.
        /// </summary>
        public ushort ViceHostAuthorityId;

        /// <summary>
        /// Creates a new host identity.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public HostIdentity(long sessionGUID, uint hostEpoch, ushort hostAuthorityId, ushort viceHostAuthorityId)
        {
            SessionGUID = sessionGUID;
            HostEpoch = hostEpoch;
            HostAuthorityId = hostAuthorityId;
            ViceHostAuthorityId = viceHostAuthorityId;
        }

        /// <summary>
        /// Returns true if this identity represents a valid host (non-default values).
        /// </summary>
        public bool IsValid => SessionGUID != 0 || HostAuthorityId != 0;

        /// <summary>
        /// Returns true if a vice host has been designated.
        /// </summary>
        public bool HasViceHost => ViceHostAuthorityId != 0;

        /// <summary>
        /// Checks if the given authority ID matches the current host.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsHost(ushort authorityId) => HostAuthorityId == authorityId;

        /// <summary>
        /// Checks if the given authority ID matches the designated vice host.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsViceHost(ushort authorityId) => ViceHostAuthorityId == authorityId && HasViceHost;

        /// <summary>
        /// Compares epochs: returns true if this identity is newer (higher epoch).
        /// Used for conflict resolution during split-brain scenarios.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsNewerThan(in HostIdentity other)
        {
            return SessionGUID == other.SessionGUID && HostEpoch > other.HostEpoch;
        }

        /// <summary>
        /// Compares epochs: returns true if this identity is same or newer epoch.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSameOrNewerThan(in HostIdentity other)
        {
            return SessionGUID == other.SessionGUID && HostEpoch >= other.HostEpoch;
        }

        public bool Equals(HostIdentity other)
        {
            return SessionGUID == other.SessionGUID &&
                   HostEpoch == other.HostEpoch &&
                   HostAuthorityId == other.HostAuthorityId &&
                   ViceHostAuthorityId == other.ViceHostAuthorityId;
        }

        public override bool Equals(object obj)
        {
            return obj is HostIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(SessionGUID, HostEpoch, HostAuthorityId, ViceHostAuthorityId);
        }

        public static bool operator ==(HostIdentity left, HostIdentity right) => left.Equals(right);
        public static bool operator !=(HostIdentity left, HostIdentity right) => !left.Equals(right);

        public override string ToString()
        {
            return $"HostIdentity(Session:{SessionGUID:X8}, Epoch:{HostEpoch}, Host:{HostAuthorityId}, Vice:{ViceHostAuthorityId})";
        }

        /// <summary>
        /// Default/invalid host identity.
        /// </summary>
        public static readonly HostIdentity Invalid = default;
    }
}
