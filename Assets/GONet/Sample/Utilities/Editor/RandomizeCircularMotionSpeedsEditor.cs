using UnityEditor;
using UnityEngine;

namespace GONet.Sample.Utilities.Editor
{
    [CustomEditor(typeof(RandomizeCircularMotionSpeeds))]
    public class RandomizeCircularMotionSpeedsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "This utility finds all CircularMotion components under this GameObject's children " +
                "and replaces any 'Slow' movement or rotation presets with randomly selected Medium or Fast.",
                MessageType.Info);

            EditorGUILayout.Space(5);

            RandomizeCircularMotionSpeeds script = (RandomizeCircularMotionSpeeds)target;

            // Big friendly button
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f); // Green
            if (GUILayout.Button("Randomize Slow Speeds", GUILayout.Height(40)))
            {
                Undo.RecordObject(script, "Randomize CircularMotion Slow Speeds");
                script.RandomizeSlowSpeeds();
                EditorUtility.SetDirty(script);

                // Force scene to save changes
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(5);

            // Show scan results
            SerializedProperty scanCount = serializedObject.FindProperty("lastScanCount");
            SerializedProperty randomizedCount = serializedObject.FindProperty("lastRandomizedCount");

            if (scanCount.intValue > 0)
            {
                EditorGUILayout.LabelField("Last Scan Results:", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"  • Components Found: {scanCount.intValue}");
                EditorGUILayout.LabelField($"  • Slow Presets Randomized: {randomizedCount.intValue}");
            }
        }
    }
}
