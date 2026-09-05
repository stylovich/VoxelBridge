using System;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [InitializeOnLoad]
    internal static class VoxelProductionEditor
    {
        static VoxelProductionEditor() => UnityEditor.Editor.finishedDefaultHeaderGUI += DrawHeader;

        private static void DrawHeader(UnityEditor.Editor editor)
        {
            if (editor.targets.Length != 1 || !(editor.target is GameObject)) return;
            string path = VoxelProductionLink.PrefabPath(editor.target);
            if (!VoxelProductionLink.HasLink(path)) return;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Voxel Bridge Production", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Editar el .vox fuente y guardar en MagicaVoxel. Rebuild actualiza la misma malla y todas sus instancias, sin cambiar sus transforms. La reconstrucción es manual.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open in MagicaVoxel")) Open(path);
                if (GUILayout.Button("Select Source VOX")) SelectSource(path);
            }
            if (GUILayout.Button("Rebuild")) Rebuild(path);
        }

        [MenuItem("Assets/Voxel Bridge/Production/Open in MagicaVoxel", false, 2120)]
        [MenuItem("GameObject/Voxel Bridge/Production/Open in MagicaVoxel", false, 54)]
        private static void OpenSelected() => Open(SelectedPath);
        [MenuItem("Assets/Voxel Bridge/Production/Select Source VOX", false, 2121)]
        [MenuItem("GameObject/Voxel Bridge/Production/Select Source VOX", false, 55)]
        private static void SelectSelectedSource() => SelectSource(SelectedPath);
        [MenuItem("Assets/Voxel Bridge/Production/Rebuild", false, 2122)]
        [MenuItem("GameObject/Voxel Bridge/Production/Rebuild", false, 56)]
        private static void RebuildSelected() => Rebuild(SelectedPath);

        [MenuItem("Assets/Voxel Bridge/Production/Open in MagicaVoxel", true)]
        [MenuItem("GameObject/Voxel Bridge/Production/Open in MagicaVoxel", true)]
        [MenuItem("Assets/Voxel Bridge/Production/Select Source VOX", true)]
        [MenuItem("GameObject/Voxel Bridge/Production/Select Source VOX", true)]
        [MenuItem("Assets/Voxel Bridge/Production/Rebuild", true)]
        [MenuItem("GameObject/Voxel Bridge/Production/Rebuild", true)]
        private static bool ValidateSelection() => Selection.objects.Length == 1 && VoxelProductionLink.HasLink(SelectedPath);

        private static string SelectedPath => VoxelProductionLink.PrefabPath(Selection.activeObject);
        private static void Open(string path) => Run(() => MagicaVoxelLauncher.OpenPath(
            VoxelLodPipeline.AssetPathToAbsolute(VoxelProductionLink.Load(path).SourcePath)));
        private static void SelectSource(string path) => Run(() =>
        {
            Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(VoxelProductionLink.Load(path).SourcePath);
            EditorGUIUtility.PingObject(Selection.activeObject);
        });
        private static void Rebuild(string path) => Run(() =>
        {
            VoxelProductionExporter.Rebuild(path, Progress);
            // Keep the current scene instance selected, including its transform overrides.
            EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(path));
        });

        internal static void Progress(float value)
        {
            if (EditorUtility.DisplayCancelableProgressBar("Voxel Bridge", "Rebuilding production mesh", value))
                throw new OperationCanceledException();
        }

        private static void Run(Action action)
        {
            try { action(); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { EditorUtility.DisplayDialog("Voxel Bridge", exception.Message, "Close"); }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
