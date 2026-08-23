using DynamicGI.Debugging;
using DynamicGI.Radiance;
using UnityEditor;
using UnityEngine;

namespace DynamicGI.Editor
{
    [CustomEditor(typeof(WorldRadianceField))]
    public sealed class WorldRadianceFieldEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            WorldRadianceField field = (WorldRadianceField)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Phase 4 stores six directional RGB irradiance values per local probe. It injects sky and direct sun only; " +
                "probe propagation, cascades, and temporal accumulation are later phases.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild All"))
                    field.RebuildAll();
                if (GUILayout.Button("Process Dirty Now"))
                    field.ProcessAllDirtyNow();
            }

            if (field.GetComponent<RadianceFieldDebug>() == null && GUILayout.Button("Add Radiance Debug"))
                Undo.AddComponent<RadianceFieldDebug>(field.gameObject);

            RadianceFieldStats stats = field.Stats;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime statistics", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Resolution", $"{stats.Resolution.x} x {stats.Resolution.y} x {stats.Resolution.z}");
            EditorGUILayout.LabelField("Active probes", stats.ProbeCount.ToString());
            EditorGUILayout.LabelField("Dirty / total tiles", $"{stats.DirtyTiles} / {stats.TotalTiles}");
            EditorGUILayout.LabelField("Updated tiles / probes", $"{stats.UpdatedTilesThisFrame} / {stats.UpdatedProbesThisFrame}");
            EditorGUILayout.LabelField("Compute dispatches", stats.ComputeDispatchesThisFrame.ToString());
            EditorGUILayout.LabelField("Estimated GPU memory", EditorUtility.FormatBytes(stats.EstimatedGpuBytes));
            EditorGUILayout.LabelField("Scheduling CPU time", $"{stats.UpdateCpuMilliseconds:0.###} ms");
            if (Application.isPlaying)
                Repaint();
        }

        [MenuItem("GameObject/Dynamic GI/Local Radiance Field", false, 12)]
        private static void AddRadianceField(MenuCommand command)
        {
            GameObject targetObject = command.context as GameObject ?? Selection.activeGameObject;
            if (targetObject == null || targetObject.GetComponent<DynamicGI.Geometry.WorldGeometryField>() == null)
            {
                EditorUtility.DisplayDialog("Local Radiance Field", "Select a GameObject containing WorldGeometryField first.", "OK");
                return;
            }
            if (targetObject.GetComponent<WorldRadianceField>() == null)
                Undo.AddComponent<WorldRadianceField>(targetObject);
            if (targetObject.GetComponent<RadianceFieldDebug>() == null)
                Undo.AddComponent<RadianceFieldDebug>(targetObject);
            Selection.activeGameObject = targetObject;
        }
    }
}
