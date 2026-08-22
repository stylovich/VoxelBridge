using DynamicGI.Debugging;
using DynamicGI.Occlusion;
using UnityEditor;
using UnityEngine;

namespace DynamicGI.Editor
{
    [CustomEditor(typeof(WorldSkyVisibilityField))]
    public sealed class WorldSkyVisibilityFieldEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            WorldSkyVisibilityField field = (WorldSkyVisibilityField)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Sky Visibility measures upper-hemisphere accessibility. It is intended for sky/ambient accessibility and structural darkening; " +
                "do not multiply the complete APV indirect result by this scalar.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild All"))
                    field.RebuildAll();
                if (GUILayout.Button("Process Dirty Now"))
                    field.ProcessAllDirtyNow();
            }

            if (field.GetComponent<SkyVisibilityDebug>() == null && GUILayout.Button("Add Sky Visibility Debug"))
                Undo.AddComponent<SkyVisibilityDebug>(field.gameObject);

            SkyVisibilityStats stats = field.Stats;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime statistics", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Resolution", $"{stats.Resolution.x} x {stats.Resolution.y} x {stats.Resolution.z}");
            EditorGUILayout.LabelField("Dirty / total tiles", $"{stats.DirtyTiles} / {stats.TotalTiles}");
            EditorGUILayout.LabelField("Updated tiles / samples", $"{stats.UpdatedTilesThisFrame} / {stats.UpdatedSamplesThisFrame}");
            EditorGUILayout.LabelField("Compute dispatches", stats.ComputeDispatchesThisFrame.ToString());
            EditorGUILayout.LabelField("Estimated GPU memory", EditorUtility.FormatBytes(stats.EstimatedGpuBytes));
            EditorGUILayout.LabelField("Scheduling CPU time", $"{stats.UpdateCpuMilliseconds:0.###} ms");

            if (Application.isPlaying)
                Repaint();
        }

        [MenuItem("GameObject/Dynamic GI/Sky Visibility Field", false, 11)]
        private static void AddSkyVisibility(MenuCommand command)
        {
            GameObject targetObject = command.context as GameObject ?? Selection.activeGameObject;
            if (targetObject == null || targetObject.GetComponent<DynamicGI.Geometry.WorldGeometryField>() == null)
            {
                EditorUtility.DisplayDialog(
                    "Sky Visibility Field",
                    "Select a GameObject containing WorldGeometryField first.",
                    "OK");
                return;
            }

            if (targetObject.GetComponent<WorldSkyVisibilityField>() == null)
                Undo.AddComponent<WorldSkyVisibilityField>(targetObject);
            if (targetObject.GetComponent<SkyVisibilityDebug>() == null)
                Undo.AddComponent<SkyVisibilityDebug>(targetObject);
            Selection.activeGameObject = targetObject;
        }
    }
}
