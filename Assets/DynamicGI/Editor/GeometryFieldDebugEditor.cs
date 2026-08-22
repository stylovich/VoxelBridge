using DynamicGI.Debugging;
using DynamicGI.Geometry;
using UnityEditor;
using UnityEngine;

namespace DynamicGI.Editor
{
    [CustomEditor(typeof(GeometryFieldDebug))]
    public sealed class GeometryFieldDebugEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Visualization presets", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Occupied"))
                    ApplyVoxelPreset(true, false);
                if (GUILayout.Button("Empty"))
                    ApplyVoxelPreset(false, true);
                if (GUILayout.Button("Both"))
                    ApplyVoxelPreset(true, true);
                if (GUILayout.Button("Voxels Off"))
                    ApplyVoxelPreset(false, false);
            }

            GeometryFieldDebug fieldDebug = (GeometryFieldDebug)target;
            WorldGeometryField field = fieldDebug.GeometryField;
            if (field == null)
            {
                EditorGUILayout.HelpBox("Assign a WorldGeometryField before enabling voxel visualization.", MessageType.Warning);
            }
            else
            {
                Vector3Int resolution = field.FieldVoxelResolution;
                GeometryFieldStats stats = field.Stats;
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Live debug readout", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Voxel resolution", $"{resolution.x} x {resolution.y} x {resolution.z}");
                EditorGUILayout.LabelField("Debug bricks", field.LastDebugBrickCount.ToString());
                EditorGUILayout.LabelField("Samples / stride", $"{field.LastDebugSampleCount} / {field.LastDebugSampleStride}");
                EditorGUILayout.LabelField("Active / dirty bricks", $"{stats.ActiveBricks} / {stats.DirtyBricks}");
                EditorGUILayout.LabelField("Estimated GPU memory", EditorUtility.FormatBytes(stats.EstimatedGpuBytes));
            }

            EditorGUILayout.HelpBox(
                "Occupied/empty voxel instances are generated on the GPU inside Camera Radius. " +
                "If Samples / stride is greater than 1, increase Maximum Voxel Instances or reduce the radius.",
                MessageType.Info);

            serializedObject.ApplyModifiedProperties();
            if (Application.isPlaying)
                Repaint();
        }

        private void ApplyVoxelPreset(bool occupied, bool empty)
        {
            serializedObject.FindProperty("showOccupiedVoxels").boolValue = occupied;
            serializedObject.FindProperty("showEmptyVoxels").boolValue = empty;
            serializedObject.ApplyModifiedProperties();
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
}
