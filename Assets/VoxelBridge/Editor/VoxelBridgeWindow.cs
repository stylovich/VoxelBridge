using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    /// <summary>Primary workflow for physically consistent voxel families and LODs.</summary>
    internal sealed class VoxelBridgeWindow : EditorWindow
    {
        private const string DefaultExportFolder = "Assets/VoxelBridgeExports";
        private const string DefaultPrefabFolder = "Assets/VoxelBridgeImports";

        private Object source;
        private VoxelStyleProfile styleProfile;
        private VoxelColorMode colorMode = VoxelColorMode.MaterialAndTexture;
        private Color singleColor = new Color32(180, 180, 180, 255);
        private float alphaCutoff = 0.1f;
        private string exportFolder = DefaultExportFolder;
        private string prefabFolder = DefaultPrefabFolder;
        private Object manualParentVox;
        private int manualTargetLod = 1;
        private VoxelLodGenerationMode manualGenerationMode = VoxelLodGenerationMode.ReduceParent;
        private TextAsset lodSetManifest;
        private Object lastVoxAsset;
        private Object lastPrefabAsset;
        private string lastVoxAssetPath;
        private string status;
        private Vector2 scroll;

        [MenuItem("Tools/Voxel Bridge/Modelos físicos y LODs", false, 100)]
        private static void OpenWindow()
        {
            var window = GetWindow<VoxelBridgeWindow>();
            window.titleContent = new GUIContent("Voxel LODs");
            window.minSize = new Vector2(460, 570);
            if (VoxelBridgeSourceSelection.IsSupported(Selection.activeObject))
                window.source = Selection.activeObject;
            window.Show();
        }

        [MenuItem("Assets/Voxel Bridge/Generar modelo físico y LODs", false, 2100)]
        private static void OpenFromSelection() => OpenWindow();

        [MenuItem("Assets/Voxel Bridge/Generar modelo físico y LODs", true)]
        private static bool ValidateOpenFromSelection() =>
            VoxelBridgeSourceSelection.IsSupported(Selection.activeObject);

        private void OnEnable()
        {
            if (source == null && VoxelBridgeSourceSelection.IsSupported(Selection.activeObject))
                source = Selection.activeObject;
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Modelos físicos y LODs", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Genera una familia de archivos .vox con unidad física consistente, sus LODs y el prefab final para Unity.",
                MessageType.Info);
            EditorGUILayout.Space(8);
            DrawAutomaticSection();
            EditorGUILayout.Space(18);
            DrawManualSection();
            EditorGUILayout.Space(18);
            DrawOutputSection();
            EditorGUILayout.EndScrollView();
        }

        private void DrawAutomaticSection()
        {
            EditorGUILayout.LabelField("1. Generación automática", EditorStyles.boldLabel);
            source = EditorGUILayout.ObjectField(new GUIContent("Modelo", "GameObject, prefab, FBX/OBJ o Mesh"),
                source, typeof(Object), true);
            styleProfile = (VoxelStyleProfile)EditorGUILayout.ObjectField(
                new GUIContent("Perfil voxel", "Unidad física, LODs, chunks y transiciones compartidos"),
                styleProfile, typeof(VoxelStyleProfile), false);

            if (styleProfile == null)
            {
                EditorGUILayout.HelpBox(
                    "Crea o asigna un perfil para generar modelos con vóxeles y LODs consistentes.",
                    MessageType.Warning);
                if (GUILayout.Button("Crear perfil voxel predeterminado")) CreateDefaultProfile();
            }
            else if (!styleProfile.TryValidate(out string profileError))
            {
                EditorGUILayout.HelpBox(profileError, MessageType.Error);
            }
            else
            {
                string levels = string.Join(", ", Enumerable.Range(0, styleProfile.LodCount)
                    .Select(i => $"LOD{i}=x{styleProfile.GetLodMultiplier(i)} " +
                                 $"({styleProfile.BaseVoxelSize * styleProfile.GetLodMultiplier(i):0.###} m)"));
                EditorGUILayout.HelpBox(
                    $"Unidad base: {styleProfile.BaseVoxelSize:0.###} m · Chunk: {styleProfile.ChunkCellSize} celdas\n{levels}",
                    MessageType.Info);
            }

            DrawColorSettings();
            VoxelBridgeFolderPicker.Draw("Carpeta de familias", ref exportFolder);
            VoxelBridgeFolderPicker.Draw("Carpeta de prefabs", ref prefabFolder);
            bool canGenerate = source != null && VoxelBridgeSourceSelection.IsSupported(source) &&
                               styleProfile != null && styleProfile.TryValidate(out _) &&
                               VoxelLodPipeline.IsAssetFolder(exportFolder) &&
                               VoxelLodPipeline.IsAssetFolder(prefabFolder);
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Generar familia .vox + prefab LOD", GUILayout.Height(38)))
                    GenerateAutomaticLods();
            }
            if (source != null && !VoxelBridgeSourceSelection.IsSupported(source))
                EditorGUILayout.HelpBox("Selecciona un GameObject, prefab, FBX/OBJ o Mesh.", MessageType.Warning);
        }

        private void DrawColorSettings()
        {
            colorMode = (VoxelColorMode)EditorGUILayout.Popup("Origen del color", (int)colorMode,
                new[] { "Material + textura", "Solo material", "Color único" });
            if (colorMode == VoxelColorMode.SingleColor)
                singleColor = EditorGUILayout.ColorField("Color", singleColor);
            if (colorMode == VoxelColorMode.MaterialAndTexture)
                alphaCutoff = EditorGUILayout.Slider(
                    new GUIContent("Corte de alpha", "Descarta texeles transparentes"), alphaCutoff, 0f, 1f);
        }

        private void DrawManualSection()
        {
            EditorGUILayout.LabelField("2. LOD manual", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Parte del .vox editado anterior: duplícalo para una simplificación libre o redúcelo a la rejilla física del nuevo nivel.",
                MessageType.Info);
            manualParentVox = EditorGUILayout.ObjectField("LOD padre (.vox)", manualParentVox,
                typeof(Object), false);

            int firstTargetLod = 1;
            if (VoxelImporterIntegration.IsVoxAsset(manualParentVox) &&
                VoxelImporterIntegration.TryLoadMetadata(AssetDatabase.GetAssetPath(manualParentVox),
                    out VoxelBridgeMetadata parentMetadata, out _))
                firstTargetLod = parentMetadata.lodIndex + 1;
            bool hasManualTarget = styleProfile != null && firstTargetLod < styleProfile.LodCount;
            if (hasManualTarget)
            {
                string[] labels = Enumerable.Range(firstTargetLod, styleProfile.LodCount - firstTargetLod)
                    .Select(i => $"LOD {i} · x{styleProfile.GetLodMultiplier(i)}")
                    .ToArray();
                int selected = Mathf.Clamp(manualTargetLod - firstTargetLod, 0, labels.Length - 1);
                manualTargetLod = EditorGUILayout.Popup("LOD destino", selected, labels) + firstTargetLod;
            }
            else if (VoxelImporterIntegration.IsVoxAsset(manualParentVox) && styleProfile != null)
            {
                EditorGUILayout.HelpBox("El LOD padre ya es el último nivel del perfil.", MessageType.Warning);
            }

            manualGenerationMode = (VoxelLodGenerationMode)EditorGUILayout.Popup(
                "Punto de partida", manualGenerationMode == VoxelLodGenerationMode.DuplicateParent ? 0 : 1,
                new[] { "Duplicar anterior (libre)", "Reducir a resolución objetivo" }) == 0
                ? VoxelLodGenerationMode.DuplicateParent
                : VoxelLodGenerationMode.ReduceParent;
            bool canGenerate = hasManualTarget && styleProfile.TryValidate(out _) &&
                               VoxelImporterIntegration.IsVoxAsset(manualParentVox) &&
                               VoxelLodPipeline.IsAssetFolder(prefabFolder);
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Crear LOD manual y actualizar prefab", GUILayout.Height(32)))
                    GenerateManualLod();
            }

            EditorGUILayout.Space(8);
            lodSetManifest = (TextAsset)EditorGUILayout.ObjectField("Manifiesto .voxset", lodSetManifest,
                typeof(TextAsset), false);
            using (new EditorGUI.DisabledScope(lodSetManifest == null ||
                                               !VoxelLodPipeline.IsAssetFolder(prefabFolder)))
            {
                if (GUILayout.Button("Reconstruir prefab desde manifiesto")) RebuildLodPrefab();
            }
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("3. Resultados", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Los niveles siguen siendo archivos .vox. Voxel Importer muestra cada .vox como un GameObject importado; el prefab es un asset adicional que agrupa los LODs para usarlo en escena.",
                MessageType.Info);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("LOD generado (.vox)", lastVoxAsset, typeof(Object), false);
                EditorGUILayout.ObjectField("Prefab para escena", lastPrefabAsset, typeof(Object), false);
            }
            if (!string.IsNullOrEmpty(lastVoxAssetPath))
            {
                EditorGUILayout.LabelField("Archivo real", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(lastVoxAssetPath, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

            using (new EditorGUI.DisabledScope(lastVoxAsset == null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Seleccionar .vox")) SelectAndPing(lastVoxAsset);
                if (GUILayout.Button("Mostrar archivo .vox"))
                    EditorUtility.RevealInFinder(VoxelLodPipeline.AssetPathToAbsolute(lastVoxAssetPath));
                if (GUILayout.Button("Abrir en MagicaVoxel")) MagicaVoxelLauncher.OpenAsset(lastVoxAsset);
                EditorGUILayout.EndHorizontal();
            }
            using (new EditorGUI.DisabledScope(lastPrefabAsset == null))
            {
                if (GUILayout.Button("Seleccionar prefab para escena")) SelectAndPing(lastPrefabAsset);
            }
        }

        private void CreateDefaultProfile()
        {
            const string settingsFolder = "Assets/VoxelBridgeSettings";
            const string profilePath = settingsFolder + "/VoxelStyleProfile.asset";
            VoxelLodPipeline.EnsureAssetFolder(settingsFolder);
            styleProfile = AssetDatabase.LoadAssetAtPath<VoxelStyleProfile>(profilePath);
            if (styleProfile == null)
            {
                styleProfile = CreateInstance<VoxelStyleProfile>();
                AssetDatabase.CreateAsset(styleProfile, profilePath);
                AssetDatabase.SaveAssets();
            }
            SelectAndPing(styleProfile);
        }

        private VoxelLodBuildOptions CreateLodOptions() => new()
        {
            ColorMode = colorMode,
            SingleColor = singleColor,
            AlphaCutoff = alphaCutoff,
            ExportFolder = exportFolder,
            PrefabFolder = prefabFolder
        };

        private void GenerateAutomaticLods()
        {
            try
            {
                VoxelLodBuildResult result = VoxelLodPipeline.GenerateAutomatic(
                    source, styleProfile, CreateLodOptions(),
                    (progress, message) => EditorUtility.DisplayCancelableProgressBar(
                        "Voxel Bridge · LOD automático", message, progress));
                lodSetManifest = AssetDatabase.LoadAssetAtPath<TextAsset>(result.ManifestAssetPath);
                if (result.VoxAssetPaths.Length > 0)
                {
                    lastVoxAssetPath = result.VoxAssetPaths[0];
                    lastVoxAsset = AssetDatabase.LoadMainAssetAtPath(lastVoxAssetPath);
                    manualParentVox = lastVoxAsset;
                    manualTargetLod = Mathf.Min(1, styleProfile.LodCount - 1);
                }
                lastPrefabAsset = AssetDatabase.LoadMainAssetAtPath(result.PrefabAssetPath);
                SelectAndPing(lastVoxAsset);
                status = $"Familia creada: {result.VoxAssetPaths.Length} archivo(s) .vox. Prefab: {result.PrefabAssetPath}";
            }
            catch (OperationCanceledException)
            {
                status = "Generación LOD cancelada.";
            }
            catch (Exception exception)
            {
                ShowException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void GenerateManualLod()
        {
            int generatedLod = manualTargetLod;
            try
            {
                VoxelLodBuildResult result = VoxelLodPipeline.GenerateManual(
                    AssetDatabase.GetAssetPath(manualParentVox), styleProfile, generatedLod,
                    manualGenerationMode, CreateLodOptions());
                lodSetManifest = AssetDatabase.LoadAssetAtPath<TextAsset>(result.ManifestAssetPath);
                lastVoxAssetPath = result.VoxAssetPaths[0];
                lastVoxAsset = AssetDatabase.LoadMainAssetAtPath(lastVoxAssetPath);
                manualParentVox = lastVoxAsset;
                manualTargetLod = Mathf.Min(generatedLod + 1, styleProfile.LodCount - 1);
                lastPrefabAsset = AssetDatabase.LoadMainAssetAtPath(result.PrefabAssetPath);
                SelectAndPing(lastVoxAsset);
                status = $"LOD {generatedLod} creado como .vox y prefab actualizado: {result.PrefabAssetPath}";
            }
            catch (Exception exception)
            {
                ShowException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void RebuildLodPrefab()
        {
            try
            {
                string path = VoxelLodPipeline.RebuildPrefab(
                    AssetDatabase.GetAssetPath(lodSetManifest), prefabFolder);
                lastPrefabAsset = AssetDatabase.LoadMainAssetAtPath(path);
                SelectAndPing(lastPrefabAsset);
                status = $"Prefab LOD reconstruido: {path}";
            }
            catch (Exception exception)
            {
                ShowException(exception);
            }
        }

        private void ShowException(Exception exception)
        {
            status = "Error: " + exception.Message;
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("Voxel Bridge", status, "Cerrar");
        }

        private static void SelectAndPing(Object value)
        {
            if (value == null) return;
            Selection.activeObject = value;
            EditorGUIUtility.PingObject(value);
        }
    }
}
