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
    /// Runtime component added to pool root GameObjects for monitoring pool statistics.
    /// This component is automatically added when pools are created and provides
    /// real-time statistics viewable in the Unity Inspector.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GONetPoolMonitor : MonoBehaviour
    {
        [HideInInspector]
        public ushort DesignTimeLocationIndex;

        [HideInInspector]
        public string SceneIdentifier;

        [HideInInspector]
        public string PrefabName;

        [HideInInspector]
        public bool PersistAcrossScenes;

        internal void Initialize(ushort designTimeLocationIndex, string sceneIdentifier, string prefabName, bool persistAcrossScenes)
        {
            DesignTimeLocationIndex = designTimeLocationIndex;
            SceneIdentifier = sceneIdentifier;
            PrefabName = prefabName;
            PersistAcrossScenes = persistAcrossScenes;
        }
    }
}
