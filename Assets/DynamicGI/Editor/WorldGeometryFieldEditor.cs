using DynamicGI.Debugging;
using DynamicGI.Geometry;
using UnityEditor;
using UnityEngine;

namespace DynamicGI.Editor
{
    [CustomEditor(typeof(WorldGeometryField))]
    public sealed class WorldGeometryFieldEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            WorldGeometryField field = (WorldGeometryField)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Phase 1 stores surface occupancy only. Base and dynamic overlays are separate; " +
                "APV and HTrace remain untouched. Dynamic objects should use GIGeometryContributor.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild All"))
                    field.RebuildAll();

                if (GUILayout.Button("Process Dirty Now"))
                    field.ProcessAllDirtyNow();
            }

            GeometryFieldStats stats = field.Stats;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime statistics", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Active / dirty bricks", $"{stats.ActiveBricks} / {stats.DirtyBricks}");
            EditorGUILayout.LabelField("Updated bricks", stats.UpdatedBricksThisFrame.ToString());
            EditorGUILayout.LabelField("Compute dispatches", stats.ComputeDispatchesThisFrame.ToString());
            EditorGUILayout.LabelField("Estimated GPU memory", EditorUtility.FormatBytes(stats.EstimatedGpuBytes));
            EditorGUILayout.LabelField("Scheduling CPU time", $"{stats.UpdateCpuMilliseconds:0.###} ms");

            if (Application.isPlaying)
                Repaint();
        }

        [MenuItem("GameObject/Dynamic GI/World Geometry Field", false, 10)]
        private static void CreateGeometryField(MenuCommand command)
        {
            GameObject fieldObject = new("World Geometry Field");
            Undo.RegisterCreatedObjectUndo(fieldObject, "Create World Geometry Field");
            GameObjectUtility.SetParentAndAlign(fieldObject, command.context as GameObject);
            WorldGeometryField field = Undo.AddComponent<WorldGeometryField>(fieldObject);
            Undo.AddComponent<GeometryFieldDebug>(fieldObject);

            SerializedObject serializedField = new(field);
            SerializedProperty computeProperty = serializedField.FindProperty("voxelizationShader");
            if (computeProperty != null && computeProperty.objectReferenceValue == null)
            {
                computeProperty.objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/DynamicGI/Shaders/GeometryVoxelize.compute");
                serializedField.ApplyModifiedPropertiesWithoutUndo();
            }

            Selection.activeGameObject = fieldObject;
            EditorGUIUtility.PingObject(fieldObject);
        }
    }
}
