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

using UnityEngine;

namespace GONet
{
    /// <summary>
    /// Marks a prefab as eligible for GONet's server-authoritative pooling system.
    /// Pool sizing hints are ONLY respected by the server; clients follow pool events.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GONetParticipant))]
    public sealed class GONetPooledObject : MonoBehaviour
    {
        [Header("Pool Sizing (Server Authoritative)")]
        [Tooltip("Initial number of pooled instances to pre-create on the server.")]
        [Min(0)]
        public int suggestedInitialSize = 8;

        [Tooltip("How many instances to add when the pool grows.")]
        [Min(0)]
        public int growByCount = 4;

        [Tooltip("Maximum pooled instances allowed. 0 = unlimited.")]
        [Min(0)]
        public int maxPoolSize = 0;

        [Tooltip("If true, the pool persists across scene loads (DontDestroyOnLoad).")]
        public bool persistAcrossScenes = false;

        [Header("Deferred Initialization")]
        [Tooltip("If populated, the pool will only be initialized when one of these scenes is loaded. " +
                 "If empty, the pool initializes as soon as any scene loads (default behavior).")]
        public string[] initializeOnlyForScenes = new string[0];
    }
}
