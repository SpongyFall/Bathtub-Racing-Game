/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using UnityEngine;

namespace GONet
{
    /// <summary>
    /// Ensures GONetStatusUI is initialized and persists across scenes.
    /// Add this component to any scene to guarantee status UI is visible.
    /// Uses DontDestroyOnLoad singleton pattern to ensure only one status UI exists.
    ///
    /// NOTE: Only functional in Editor and Development builds. In release builds,
    /// this component does nothing to avoid including debug UI in production.
    /// </summary>
    public class GONetStatusUIInitializer : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static GONetStatusUIInitializer instance;
        private static GameObject statusUIObject;

        private void Awake()
        {
            // Singleton pattern - only allow one initializer
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;

            // CRITICAL: Only call DontDestroyOnLoad during play mode (not during editor build process)
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }

            // Check if GONetStatusUI already exists
            if (statusUIObject == null)
            {
                statusUIObject = FindExistingStatusUI();
            }

            // If not found, create it
            if (statusUIObject == null)
            {
                CreateStatusUI();
            }
        }

        private GameObject FindExistingStatusUI()
        {
            // Check if GONetStatusUI component exists anywhere
            GONet.Sample.GONetStatusUI existingUI = FindObjectOfType<GONet.Sample.GONetStatusUI>();
            if (existingUI != null)
            {
                GONetLog.Debug("[GONetStatusUIInitializer] Found existing GONetStatusUI");
                return existingUI.gameObject;
            }

            return null;
        }

        private void CreateStatusUI()
        {
            // Create a persistent GameObject for status UI
            statusUIObject = new GameObject("GONetStatusUI");
            statusUIObject.AddComponent<GONet.Sample.GONetStatusUI>();

            // CRITICAL: Only call DontDestroyOnLoad during play mode (not during editor build process)
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(statusUIObject);
            }

            GONetLog.Info("[GONetStatusUIInitializer] Created GONetStatusUI");
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }
#endif
    }
}
