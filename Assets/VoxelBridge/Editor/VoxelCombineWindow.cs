using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelCombineWindow : EditorWindow
    {
        [SerializeField] private GameObject[] selection = Array.Empty<GameObject>();
        [SerializeField] private VoxelStyleProfile profile;
        [SerializeField] private string familyName = "CombinedVoxels";
        [SerializeField] private string outputFolder = "Assets/VoxelBridgeExports";
        [SerializeField] private bool ignoreInactive = true;
        [SerializeField] private bool generateLods = true;
        [SerializeField] private bool placeResult;
        [SerializeField] private bool disableSources = true;
        private string report;
        private MessageType reportType;
        private Vector2 scroll;

        [MenuItem("Tools/Voxel Bridge/Combine Voxel Models", false, 103)]
        [MenuItem("GameObject/Voxel Bridge/Combine Voxel Models", false, 53)]
        private static void Open()
        {
            var window = GetWindow<VoxelCombineWindow>("Combine Voxel Models");
            window.minSize = new Vector2(480, 410);
            window.selection = Selection.gameObjects;
            window.Show();
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox("Une los LOD0 guardados de instancias de producción semántica. Conserva ColorID, SurfaceID y las piezas Keep Original. Las fuentes no se sobrescriben; la salida es una familia independiente, editable en MagicaVoxel.", MessageType.Info);
            EditorGUILayout.HelpBox("La unión exacta requiere celdas del tamaño base del perfil, posiciones alineadas a la rejilla mundial y rotaciones ortogonales. No remuestrea, no rellena nuevos huecos y no copia scripts, luces ni colliders. Las modificaciones internas de las instancias deben aplicarse en el VOX.", MessageType.Info);
            if (GUILayout.Button("Use Scene Selection")) { selection = Selection.gameObjects; report = null; }
            using (new EditorGUI.DisabledScope(true))
                foreach (var item in selection) EditorGUILayout.ObjectField(item, typeof(GameObject), true);
            EditorGUI.BeginChangeCheck();
            profile = (VoxelStyleProfile)EditorGUILayout.ObjectField("Voxel Profile", profile, typeof(VoxelStyleProfile), false);
            familyName = EditorGUILayout.TextField("Family Name", familyName);
            outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
            ignoreInactive = EditorGUILayout.Toggle(new GUIContent("Ignore Inactive Objects", "Excluye instancias desactivadas, incluidas las que tienen un padre desactivado."), ignoreInactive);
            generateLods = EditorGUILayout.Toggle(new GUIContent("Generate Derived LODs", "Reduce sucesivamente el volumen combinado según los multiplicadores del perfil. Desactivar para editar primero el LOD0 y derivar los niveles desde el prefab."), generateLods);
            placeResult = EditorGUILayout.Toggle("Place Result in Scene", placeResult);
            using (new EditorGUI.DisabledScope(!placeResult)) disableSources = EditorGUILayout.Toggle("Disable Source Instances", disableSources);
            if (EditorGUI.EndChangeCheck()) report = null;
            EditorGUILayout.HelpBox("Mantener grupos compactos. Un único LODGroup aplica las transiciones al conjunto; una agrupación extensa limita la eficacia del culling. No se permite cruzar escenas. Los impostores semánticos requieren adaptar su horneado.", MessageType.Info);
            using (new EditorGUI.DisabledScope(profile == null || selection.Length == 0 || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Analyze Combination")) Run(false);
                if (GUILayout.Button("Create Combined Family")) Run(true);
            }
            if (!string.IsNullOrEmpty(report)) EditorGUILayout.HelpBox(report, reportType);
            EditorGUILayout.EndScrollView();
        }

        private void Run(bool build)
        {
            try
            {
                // Re-read sources on every action; a preview never authorizes stale transforms or VOX data.
                var analysis = VoxelFamilyCombiner.Analyze(selection, profile, ignoreInactive, p => Progress(p, "Analyzing sources"));
                var grid = analysis.Grid;
                report = $"Sources: {analysis.Sources.Length} | Grid: {grid.Size} | Cells: {grid.Occupied.Length:N0}\n" +
                    $"Occupied: {grid.CountOccupied():N0} | Pairs: {analysis.PairCount}/255 | Overlaps: {analysis.Overlaps:N0} | Conflicts: {analysis.Conflicts:N0}\n" +
                    $"Size: {(Vector3)grid.Size * grid.VoxelSize} m | Pivot: {analysis.Pivot}\n" +
                    "Priority (first source wins):\n" + string.Join("\n", analysis.Sources.Select((s, i) => $"{i + 1}. {s.Instance.name}"));
                if (analysis.ConflictExamples.Count > 0) report += "\n" + string.Join("\n", analysis.ConflictExamples);
                reportType = analysis.Conflicts > 0 ? MessageType.Warning : MessageType.Info;
                EditorUtility.ClearProgressBar();
                if (!build) return;
                if (analysis.Conflicts > 0 && !EditorUtility.DisplayDialog("Voxel Overlap Conflicts",
                    $"Hay {analysis.Conflicts:N0} solapamientos con IDs diferentes. Se conservará la primera fuente según el orden de la jerarquía. Revisar el informe antes de continuar.\n\n" + string.Join("\n", analysis.ConflictExamples), "Keep First Source", "Cancel")) return;
                var result = VoxelFamilyCombiner.Build(analysis, profile, outputFolder, familyName, generateLods,
                    acceptConflicts: true, progress: p => Progress(p, "Building combined family"));
                report += "\nPrefab: " + result.PrefabAssetPath;
                // Output assets survive a placement failure and remain available in the report.
                if (placeResult) Selection.activeGameObject = VoxelFamilyCombiner.Place(analysis, result, disableSources);
                else Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabAssetPath);
            }
            catch (OperationCanceledException) { report = "Combination cancelled. Source assets are unchanged."; reportType = MessageType.Info; }
            catch (Exception ex) { report = (report == null ? "" : report + "\n\n") + ex.Message; reportType = MessageType.Error; }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private static void Progress(float value, string stage)
        {
            if (EditorUtility.DisplayCancelableProgressBar("Combine Voxel Models", stage, value)) throw new OperationCanceledException();
        }
    }
}
