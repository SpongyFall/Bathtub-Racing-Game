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

using UnityEngine;

namespace GONet
{
    /// <summary>
    /// CLIENT ONLY: Event arguments for when a spawn enters limbo state due to GONetId batch exhaustion.
    /// IMPORTANT: Limbo is RARE - only occurs during extreme rapid spawning (100+ spawns/sec).
    /// </summary>
    public class Client_SpawnLimboEventArgs
    {
        /// <summary>
        /// The GONetParticipant that entered limbo state (no GONetId assigned yet).
        /// </summary>
        public GONetParticipant Participant { get; internal set; }

        /// <summary>
        /// The prefab that was instantiated.
        /// </summary>
        public GONetParticipant Prefab { get; internal set; }

        /// <summary>
        /// The limbo mode that was applied to this spawn.
        /// </summary>
        public Client_GONetIdBatchLimboMode LimboMode { get; internal set; }

        /// <summary>
        /// Number of IDs remaining across all batches (should be 0 if entering limbo).
        /// </summary>
        public uint RemainingIds { get; internal set; }

        /// <summary>
        /// Position where the object was spawned.
        /// </summary>
        public Vector3 Position { get; internal set; }

        /// <summary>
        /// Rotation where the object was spawned.
        /// </summary>
        public Quaternion Rotation { get; internal set; }
    }
}
