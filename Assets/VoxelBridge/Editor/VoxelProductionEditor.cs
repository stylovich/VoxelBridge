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
            try
            {
                var link = VoxelProductionLink.Load(path);
                if (!string.IsNullOrEmpty(link.manifestGuid))
                {
                    DrawFamily(AssetDatabase.GUIDToAssetPath(link.manifestGuid));
                    return;
                }
            }
            catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); return; }
            EditorGUILayout.HelpBox("Editar el .vox fuente y guardar en MagicaVoxel. Rebuild actualiza la misma malla y todas sus instancias, sin cambiar sus transforms. La reconstrucción es manual.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open in MagicaVoxel")) Open(path);
                if (GUILayout.Button("Select Source VOX")) SelectSource(path);
            }
            if (GUILayout.Button("Rebuild")) Rebuild(path);
            if (GUILayout.Button("Semantic Bindings")) Run(() =>
                VoxelSemanticBindingWindow.OpenForSource(VoxelProductionLink.Load(path).SourcePath));
            if (GUILayout.Button("Create LOD Family...")) Run(() =>
                VoxToUnityWindow.OpenProductionFamily(VoxelProductionLink.Load(path).SourcePath));
        }

        private static void DrawFamily(string manifestPath)
        {
            var manifest = VoxelProductionFamily.Load(manifestPath);
            EditorGUILayout.HelpBox("Rebuild actualiza sólo las mallas desde los .vox guardados. Duplicate y Reduce crean el nivel siguiente. Regenerate reemplaza explícitamente un .vox desde su padre; los niveles posteriores nunca se sobrescriben automáticamente. Los avisos de origen cambiado se propagan a los descendientes.", MessageType.None);
            foreach (var entry in manifest.lods)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField($"LOD {entry.lodIndex} · {manifest.baseVoxelSize * entry.multiplier:0.###} m", EditorStyles.boldLabel);
                    string source = VoxelProductionFamily.SourcePath(entry);
                    bool stale = VoxelProductionFamily.IsDerivedStale(manifest, entry.lodIndex);
                    bool meshChanged = entry.builtSourceHash != VoxelProductionFamily.SourceHash(entry);
                    if (stale) EditorGUILayout.HelpBox("El origen de este nivel o de un antecesor cambió. Revisar el trabajo artístico antes de regenerar.", MessageType.Warning);
                    if (meshChanged) EditorGUILayout.HelpBox("El .vox o sus bindings cambiaron. Rebuild actualiza la malla sin regenerar el archivo fuente.", MessageType.Info);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Open in MagicaVoxel")) Run(() => MagicaVoxelLauncher.OpenPath(VoxelLodPipeline.AssetPathToAbsolute(source)));
                        if (GUILayout.Button("Select Source")) { Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(source); EditorGUIUtility.PingObject(Selection.activeObject); }
                        if (GUILayout.Button("Rebuild")) Run(() => VoxelProductionFamily.RebuildLevel(manifestPath, entry.lodIndex, Progress));
                    }
                    if (GUILayout.Button("Semantic Bindings")) Run(() => VoxelSemanticBindingWindow.OpenForSource(source));
                    if (entry.lodIndex > 0)
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("Regenerate: Duplicate")) Regenerate(manifestPath, entry.lodIndex, VoxelLodGenerationMode.DuplicateParent);
                            if (GUILayout.Button("Regenerate: Reduce")) Regenerate(manifestPath, entry.lodIndex, VoxelLodGenerationMode.ReduceParent);
                        }
                }
            }
            var profile = VoxelProductionFamily.Profile(manifest);
            int next = manifest.lods.Length;
            if (next < profile.LodCount)
            {
                EditorGUILayout.LabelField($"Create LOD {next} from LOD {next - 1}", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Duplicate Previous")) Run(() => VoxelProductionFamily.DeriveLevel(manifestPath, next, VoxelLodGenerationMode.DuplicateParent, progress: Progress));
                    if (GUILayout.Button("Reduce Previous")) Run(() => VoxelProductionFamily.DeriveLevel(manifestPath, next, VoxelLodGenerationMode.ReduceParent, progress: Progress));
                }
                if (GUILayout.Button("Generate Remaining LODs by Reduction")) Run(() =>
                {
                    for (int i = next; i < profile.LodCount; i++)
                        VoxelProductionFamily.DeriveLevel(manifestPath, i, VoxelLodGenerationMode.ReduceParent, progress: Progress);
                });
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild All Meshes")) Run(() => VoxelProductionFamily.RebuildAll(manifestPath, Progress));
                if (GUILayout.Button("Select Manifest")) Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(manifestPath);
            }
        }

        private static void Regenerate(string path, int level, VoxelLodGenerationMode mode)
        {
            if (EditorUtility.DisplayDialog("Regenerate LOD", $"Se reemplazará el .vox de LOD{level} desde LOD{level - 1}. Sus ediciones manuales se perderán. Los niveles posteriores se conservan y pueden quedar pendientes de revisión. Esta operación no admite Undo.", "Replace", "Cancel"))
                Run(() => VoxelProductionFamily.DeriveLevel(path, level, mode, true, Progress));
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
