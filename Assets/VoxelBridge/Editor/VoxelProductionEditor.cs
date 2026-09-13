using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [InitializeOnLoad]
    internal static class VoxelProductionEditor
    {
        static VoxelProductionEditor() => UnityEditor.Editor.finishedDefaultHeaderGUI += DrawHeader;

        private static readonly GUIContent EditContent = new("Edit", "Editar colores y superficies de este LOD en Surface Painter.");
        private static readonly GUIContent VoxContent = new("VOX", "Abrir el VOX de este LOD en MagicaVoxel.");
        private static readonly GUIContent RebuildContent = new("Rebuild", "Reconstruir sólo la malla de este LOD desde su VOX guardado. No regenera la fuente.");
        private static readonly GUIContent MoreContent = new("⋯", "Más acciones de este LOD: fuente, bindings, informe y regeneración.");
        private static readonly GUIContent FamilyContent = new("Family ▾", "Duplicar la familia, reconstruir todas las mallas o seleccionar el manifiesto.");
        private static readonly GUIContent HelpContent = new("?", "Mostrar u ocultar la ayuda del flujo de producción.");
        private const string FamilyHelp = "Edit abre Surface Painter; VOX abre MagicaVoxel. Rebuild lee el VOX guardado sin regenerarlo. Create LOD crea niveles nuevos; Regenerate reemplaza un nivel y descarta sus retoques, con confirmación. Los descendientes existentes no se sobrescriben automáticamente.";

        private static void DrawHeader(UnityEditor.Editor editor)
        {
            if (editor.targets.Length != 1 || !(editor.target is GameObject)) return;
            string path = VoxelProductionLink.PrefabPath(editor.target);
            if (!VoxelProductionLink.HasLink(path)) return;
            EditorGUILayout.Space(4);
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
            EditorGUILayout.LabelField("Voxel Bridge Production", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Editar el .vox fuente y guardar en MagicaVoxel. Rebuild actualiza la misma malla y todas sus instancias, sin cambiar sus transforms. La reconstrucción es manual.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open in MagicaVoxel")) Open(path);
                if (GUILayout.Button("Select Source VOX")) SelectSource(path);
            }
            if (GUILayout.Button("Rebuild")) Rebuild(path);
            if (GUILayout.Button("Edit Surfaces")) Run(() => VoxelSurfacePainterWindow.OpenForSource(VoxelProductionLink.Load(path).SourcePath, path));
            DrawSurfaceReport(AssetDatabase.GUIDToAssetPath(VoxelProductionLink.Load(path).sourceGuid));
            if (GUILayout.Button("Semantic Bindings")) Run(() =>
                VoxelSemanticBindingWindow.OpenForSource(VoxelProductionLink.Load(path).SourcePath));
            if (GUILayout.Button("Create LOD Family...")) Run(() =>
                VoxToUnityWindow.OpenProductionFamily(VoxelProductionLink.Load(path).SourcePath));
        }

        private static void DrawSurfaceReport(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return;
            var report = AssetDatabase.LoadAssetAtPath<TextAsset>(System.IO.Path.ChangeExtension(sourcePath, ".surface-report.json"));
            if (report != null && GUILayout.Button(new GUIContent("Surface Assignment Report", "Informe de la conversión original. Los retoques posteriores del VOX no modifican este informe.")))
                AssetDatabase.OpenAsset(report);
        }

        private static void DrawFamily(string manifestPath)
        {
            var manifest = VoxelProductionFamily.Load(manifestPath);
            string stateKey = "VoxelBridge.ProductionInspector." + AssetDatabase.AssetPathToGUID(manifestPath);
            bool expanded = SessionState.GetBool(stateKey, true);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(12);
                bool nextExpanded = EditorGUILayout.Foldout(expanded, $"Voxel Bridge · {manifest.lods.Length} LODs", true);
                if (nextExpanded != expanded) SessionState.SetBool(stateKey, expanded = nextExpanded);
                Rect helpRect = GUILayoutUtility.GetRect(HelpContent, EditorStyles.miniButton, GUILayout.Width(22));
                if (GUI.Button(helpRect, HelpContent, EditorStyles.miniButton))
                    SessionState.SetBool(stateKey + ".Help", !SessionState.GetBool(stateKey + ".Help", false));
                Rect menuRect = GUILayoutUtility.GetRect(FamilyContent, EditorStyles.miniButton, GUILayout.Width(65));
                if (GUI.Button(menuRect, FamilyContent, EditorStyles.miniButton))
                    CreateFamilyMenu(manifestPath, manifest.prefabAssetPath).DropDown(menuRect);
            }
            if (expanded && SessionState.GetBool(stateKey + ".Help", false))
                EditorGUILayout.HelpBox(FamilyHelp, MessageType.None);

            var staleLevels = new List<int>();
            var changedLevels = new List<int>();
            foreach (var entry in manifest.lods)
            {
                int level = entry.lodIndex;
                string source = VoxelProductionFamily.SourcePath(entry);
                if (VoxelProductionFamily.IsDerivedStale(manifest, level)) staleLevels.Add(level);
                if (entry.builtSourceHash != VoxelProductionFamily.SourceHash(entry)) changedLevels.Add(level);
                if (!expanded) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = $"LOD {level} · {manifest.baseVoxelSize * entry.multiplier:0.#####} m";
                    GUILayout.Label(new GUIContent(label, label + "\n" + source), EditorStyles.miniLabel, GUILayout.MinWidth(75));
                    if (GUILayout.Button(EditContent, EditorStyles.miniButton, GUILayout.Width(38)))
                        Run(() => VoxelSurfacePainterWindow.OpenForSource(source, manifest.prefabAssetPath, level));
                    if (GUILayout.Button(VoxContent, EditorStyles.miniButton, GUILayout.Width(35)))
                        Run(() => MagicaVoxelLauncher.OpenPath(VoxelLodPipeline.AssetPathToAbsolute(source)));
                    if (GUILayout.Button(RebuildContent, EditorStyles.miniButton, GUILayout.Width(56)))
                        Run(() => VoxelProductionFamily.RebuildLevel(manifestPath, level, Progress));
                    Rect menuRect = GUILayoutUtility.GetRect(MoreContent, EditorStyles.miniButton, GUILayout.Width(22));
                    if (GUI.Button(menuRect, MoreContent, EditorStyles.miniButton))
                        CreateLevelMenu(manifestPath, level, source).DropDown(menuRect);
                }
            }
            // Pending-source warnings remain visible even when the compact panel is collapsed.
            string status = FormatFamilyStatus(staleLevels, changedLevels);
            if (status.Length > 0) EditorGUILayout.HelpBox(status, staleLevels.Count > 0 ? MessageType.Warning : MessageType.Info);
            if (!expanded) return;
            var profile = VoxelProductionFamily.Profile(manifest);
            int next = manifest.lods.Length;
            if (next < profile.LodCount)
            {
                var content = new GUIContent($"Create LOD {next} ▾", $"Crear niveles nuevos desde LOD {next - 1}. No sobrescribe niveles existentes.");
                Rect rect = GUILayoutUtility.GetRect(content, EditorStyles.miniButton);
                if (GUI.Button(rect, content, EditorStyles.miniButton)) CreateNextLevelMenu(manifestPath, next, profile.LodCount).DropDown(rect);
            }
        }

        internal static string FormatFamilyStatus(IReadOnlyList<int> staleLevels, IReadOnlyList<int> changedLevels)
        {
            string stale = staleLevels.Count == 0 ? "" : $"Origen cambiado: LOD {string.Join(", ", staleLevels)}. Revisar retoques antes de regenerar.";
            string changed = changedLevels.Count == 0 ? "" : $"Rebuild pendiente: LOD {string.Join(", ", changedLevels)}. El VOX o sus bindings cambiaron; reconstruir no regenera la fuente.";
            return stale + (stale.Length > 0 && changed.Length > 0 ? "\n" : "") + changed;
        }

        internal static GenericMenu CreateLevelMenu(string manifestPath, int level, string source)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Select Source VOX"), false, () => Run(() =>
            {
                Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(source);
                EditorGUIUtility.PingObject(Selection.activeObject);
            }));
            menu.AddItem(new GUIContent("Semantic Bindings"), false, () => Run(() => VoxelSemanticBindingWindow.OpenForSource(source)));
            var report = string.IsNullOrEmpty(source) ? null : AssetDatabase.LoadAssetAtPath<TextAsset>(System.IO.Path.ChangeExtension(source, ".surface-report.json"));
            var reportLabel = new GUIContent("Surface Assignment Report", "Informe de la conversión original; no incluye retoques posteriores del VOX.");
            if (report != null) menu.AddItem(reportLabel, false, () => Run(() => AssetDatabase.OpenAsset(report)));
            else menu.AddDisabledItem(reportLabel);
            if (level > 0)
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Regenerate/Duplicate Previous…"), false, () => Regenerate(manifestPath, level, VoxelLodGenerationMode.DuplicateParent));
                menu.AddItem(new GUIContent("Regenerate/Reduce Previous…"), false, () => Regenerate(manifestPath, level, VoxelLodGenerationMode.ReduceParent));
            }
            return menu;
        }

        internal static GenericMenu CreateFamilyMenu(string manifestPath, string prefabPath)
        {
            var menu = new GenericMenu();
            var duplicate = new GUIContent("Duplicate Editable Family…", "Copia independiente de los recursos guardados. Comparte paletas, perfiles y materiales; no copia borradores ni overrides de escena.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) menu.AddDisabledItem(duplicate);
            else menu.AddItem(duplicate, false, () => Duplicate(prefabPath));
            menu.AddItem(new GUIContent("Rebuild All Meshes"), false, () => Run(() => VoxelProductionFamily.RebuildAll(manifestPath, Progress)));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Select Manifest"), false, () => Run(() => Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(manifestPath)));
            return menu;
        }

        internal static GenericMenu CreateNextLevelMenu(string manifestPath, int next, int lodCount)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Duplicate Previous"), false, () => Run(() => VoxelProductionFamily.DeriveLevel(manifestPath, next, VoxelLodGenerationMode.DuplicateParent, progress: Progress)));
            menu.AddItem(new GUIContent("Reduce Previous"), false, () => Run(() => VoxelProductionFamily.DeriveLevel(manifestPath, next, VoxelLodGenerationMode.ReduceParent, progress: Progress)));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Generate Remaining LODs by Reduction"), false, () => Run(() =>
            {
                for (int i = next; i < lodCount; i++)
                    VoxelProductionFamily.DeriveLevel(manifestPath, i, VoxelLodGenerationMode.ReduceParent, progress: Progress);
            }));
            return menu;
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

        [MenuItem("Assets/Voxel Bridge/Production/Duplicate Editable Family", false, 2123)]
        [MenuItem("GameObject/Voxel Bridge/Production/Duplicate Editable Family", false, 57)]
        private static void DuplicateSelected() => Duplicate(SelectedPath);

        [MenuItem("Assets/Voxel Bridge/Production/Duplicate Editable Family", true)]
        [MenuItem("GameObject/Voxel Bridge/Production/Duplicate Editable Family", true)]
        private static bool ValidateDuplicate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !ValidateSelection()) return false;
            try { return !string.IsNullOrEmpty(VoxelProductionLink.Load(SelectedPath).manifestGuid); }
            catch { return false; }
        }

        private static void Duplicate(string path) => Run(() =>
        {
            string manifest = AssetDatabase.GUIDToAssetPath(VoxelProductionLink.Load(path).manifestGuid);
            string parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(manifest));
            parent = string.IsNullOrEmpty(parent) ? "Assets" : parent.Replace('\\', '/');
            string copy = VoxelProductionFamilyCopy.Duplicate(path, parent, value =>
            {
                if (EditorUtility.DisplayCancelableProgressBar("Voxel Bridge", "Duplicating editable family", value))
                    throw new OperationCanceledException();
            });
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(copy);
            EditorGUIUtility.PingObject(Selection.activeObject);
        });

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
