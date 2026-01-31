using UnityEngine;

namespace GONet.Sample.Utilities
{
    /// <summary>
    /// Editor utility to randomize CircularMotion speed presets.
    /// Finds all CircularMotion components under this GameObject's children
    /// and replaces any "Slow" presets with randomly selected Medium or Fast.
    ///
    /// Usage: Attach to a parent GameObject, click "Randomize Slow Speeds" in Inspector.
    /// </summary>
    public class RandomizeCircularMotionSpeeds : MonoBehaviour
    {
        [Tooltip("How many components were found in the last scan")]
        [SerializeField] private int lastScanCount = 0;

        [Tooltip("How many Slow presets were randomized in the last operation")]
        [SerializeField] private int lastRandomizedCount = 0;

        /// <summary>
        /// Find all CircularMotion components in children and randomize Slow presets.
        /// Called from custom editor button.
        /// </summary>
        public void RandomizeSlowSpeeds()
        {
            CircularMotion[] motionComponents = GetComponentsInChildren<CircularMotion>(true);
            lastScanCount = motionComponents.Length;
            lastRandomizedCount = 0;

            foreach (var motion in motionComponents)
            {
                bool changed = false;

                // Randomize movement speed if set to Slow
                if (motion.movementSpeedPreset == CircularMotion.MovementSpeedPreset.Slow)
                {
                    motion.movementSpeedPreset = Random.value > 0.5f
                        ? CircularMotion.MovementSpeedPreset.Medium
                        : CircularMotion.MovementSpeedPreset.Fast;
                    changed = true;
                    Debug.Log($"[RandomizeCircularMotion] {motion.gameObject.name}: Movement Slow → {motion.movementSpeedPreset}");
                }

                // Randomize rotation speed if set to Slow
                if (motion.rotationSpeedPreset == CircularMotion.RotationSpeedPreset.Slow)
                {
                    motion.rotationSpeedPreset = Random.value > 0.5f
                        ? CircularMotion.RotationSpeedPreset.Medium
                        : CircularMotion.RotationSpeedPreset.Fast;
                    changed = true;
                    Debug.Log($"[RandomizeCircularMotion] {motion.gameObject.name}: Rotation Slow → {motion.rotationSpeedPreset}");
                }

                if (changed)
                {
                    lastRandomizedCount++;
#if UNITY_EDITOR
                    UnityEditor.EditorUtility.SetDirty(motion);
#endif
                }
            }

            Debug.Log($"[RandomizeCircularMotion] Scanned {lastScanCount} components, randomized {lastRandomizedCount} Slow presets");
        }
    }
}
