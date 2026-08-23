using DynamicGI.Debugging;
using DynamicGI.Radiance;
using UnityEditor;
using UnityEngine;

namespace DynamicGI.Editor
{
    [CustomEditor(typeof(WorldRadianceClipmap))]
    public sealed class WorldRadianceClipmapEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            WorldRadianceClipmap clipmap = (WorldRadianceClipmap)target;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Origins snap to whole update tiles. Probe textures are toroidal: moving the target rotates a ring offset and only exposes new slabs. " +
                "Large teleports invalidate a full cascade.",
                MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild All")) clipmap.RebuildAll();
                if (GUILayout.Button("Process Dirty Now")) clipmap.ProcessAllDirtyNow();
            }
            if (clipmap.GetComponent<RadianceClipmapDebug>() == null && GUILayout.Button("Add Clipmap Debug"))
                Undo.AddComponent<RadianceClipmapDebug>(clipmap.gameObject);

            RadianceClipmapStats stats = clipmap.Stats;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime statistics", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Cascades / probes", $"{stats.CascadeCount} / {stats.ActiveProbes}");
            EditorGUILayout.LabelField("Dirty tiles", stats.DirtyTiles.ToString());
            EditorGUILayout.LabelField("Updated tiles / probes", $"{stats.UpdatedTilesThisFrame} / {stats.UpdatedProbesThisFrame}");
            EditorGUILayout.LabelField("Exposed / recycled", $"{stats.ExposedProbesThisFrame} / {stats.RecycledProbesThisFrame}");
            EditorGUILayout.LabelField("Origin moves", stats.OriginMovesThisFrame.ToString());
            EditorGUILayout.LabelField("Compute dispatches", stats.ComputeDispatchesThisFrame.ToString());
            EditorGUILayout.LabelField("Estimated GPU memory", EditorUtility.FormatBytes(stats.EstimatedGpuBytes));
            EditorGUILayout.LabelField("Scheduling CPU time", $"{stats.UpdateCpuMilliseconds:0.###} ms");
            if (Application.isPlaying) Repaint();
        }

        [MenuItem("GameObject/Dynamic GI/Radiance Clipmap", false, 13)]
        private static void AddClipmap(MenuCommand command)
        {
            GameObject targetObject = command.context as GameObject ?? Selection.activeGameObject;
            if (targetObject == null || targetObject.GetComponent<DynamicGI.Geometry.WorldGeometryField>() == null)
            {
                EditorUtility.DisplayDialog("Radiance Clipmap", "Select a GameObject containing WorldGeometryField first.", "OK");
                return;
            }
            if (targetObject.GetComponent<WorldRadianceClipmap>() == null)
                Undo.AddComponent<WorldRadianceClipmap>(targetObject);
            if (targetObject.GetComponent<RadianceClipmapDebug>() == null)
                Undo.AddComponent<RadianceClipmapDebug>(targetObject);
            Selection.activeGameObject = targetObject;
        }
    }
}
