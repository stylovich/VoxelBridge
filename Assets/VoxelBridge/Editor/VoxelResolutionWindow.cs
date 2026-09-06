using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelResolutionWindow : EditorWindow
    {
        private Object source;
        private int resolution = 64;
        private int padding = 1;
        private bool fillInterior = true;
        private bool hideInternalCavities = true;
        private VoxelColorMode colorMode = VoxelColorMode.MaterialAndTexture;
        private Color singleColor = new Color32(180, 180, 180, 255);
        private float alphaCutoff = 0.1f;
        private string exportFolder = "Assets/VoxelBridgeExports";
        private Object generatedVox;
        private string generatedVoxPath;
        private string status;

        [MenuItem("Tools/Voxel Bridge/Resolution-Based Conversion", false, 110)]
        private static void OpenWindow()
        {
            var window = GetWindow<VoxelResolutionWindow>();
            window.titleContent = new GUIContent("Resolution-Based VOX");
            window.minSize = new Vector2(440, 430);
            if (VoxelBridgeSourceSelection.IsSupported(Selection.activeObject))
                window.source = Selection.activeObject;
            window.Show();
        }

        [MenuItem("Assets/Voxel Bridge/Convert to VOX by Resolution", false, 2101)]
        private static void OpenFromSelection() => OpenWindow();

        [MenuItem("Assets/Voxel Bridge/Convert to VOX by Resolution", true)]
        private static bool ValidateOpenFromSelection() =>
            VoxelBridgeSourceSelection.IsSupported(Selection.activeObject);

        private void OnEnable()
        {
            if (source == null && VoxelBridgeSourceSelection.IsSupported(Selection.activeObject))
                source = Selection.activeObject;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Single Resolution-Based Conversion", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Genera un único .vox indicando cuántas celdas tendrá el eje más largo. No crea perfiles, LODs ni manifiestos.",
                MessageType.Info);
            source = EditorGUILayout.ObjectField(new GUIContent("Source Model", "GameObject, prefab, FBX/OBJ o Mesh"),
                source, typeof(Object), true);
            resolution = EditorGUILayout.IntSlider("Longest Axis Resolution", resolution, 8, 256);
            padding = EditorGUILayout.IntSlider("Padding", padding, 0, 8);
            fillInterior = EditorGUILayout.Toggle("Fill Interior", fillInterior);
            hideInternalCavities = EditorGUILayout.Toggle("Hide Enclosed Cavities", hideInternalCavities);
            if (!fillInterior)
                EditorGUILayout.HelpBox(
                    "Sin relleno se genera una carcasa hueca. Ignore Cavity solo oculta cavidades sin conexión al exterior.",
                    MessageType.None);
            colorMode = (VoxelColorMode)EditorGUILayout.Popup("Color Source", (int)colorMode,
                new[] { "Material + Texture", "Material Only", "Single Color" });
            if (colorMode == VoxelColorMode.SingleColor)
                singleColor = EditorGUILayout.ColorField("Color", singleColor);
            if (colorMode == VoxelColorMode.MaterialAndTexture)
                alphaCutoff = EditorGUILayout.Slider("Alpha Cutoff", alphaCutoff, 0f, 1f);
            VoxelBridgeFolderPicker.Draw("Output Folder", ref exportFolder);

            bool canConvert = source != null && VoxelBridgeSourceSelection.IsSupported(source) &&
                              VoxelLodPipeline.IsAssetFolder(exportFolder) && resolution > padding * 2;
            using (new EditorGUI.DisabledScope(!canConvert))
            {
                if (GUILayout.Button("Convert to a .vox File", GUILayout.Height(36))) Convert();
            }

            if (!string.IsNullOrEmpty(generatedVoxPath))
            {
                EditorGUILayout.Space(10);
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField(".vox Result", generatedVox, typeof(Object), false);
                EditorGUILayout.SelectableLabel(generatedVoxPath, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Reveal File"))
                    EditorUtility.RevealInFinder(VoxelLodPipeline.AssetPathToAbsolute(generatedVoxPath));
                if (GUILayout.Button("Open in MagicaVoxel")) MagicaVoxelLauncher.OpenAsset(generatedVox);
                EditorGUILayout.EndHorizontal();
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
        }

        private void Convert()
        {
            try
            {
                if (source is GameObject root)
                    foreach (var marker in root.GetComponentsInChildren<VoxelConversionRule>(true))
                        if (marker.enabled && (marker.Action == VoxelConversionAction.KeepOriginal || marker.SurfaceId >= 0))
                            throw new InvalidOperationException("Keep Original and SurfaceID rules require Physical Models and LODs with a conversion profile.");
                VoxelLodPipeline.EnsureAssetFolder(exportFolder);
                var settings = new VoxelizationSettings
                {
                    Resolution = resolution,
                    Padding = padding,
                    FillInterior = fillInterior,
                    ColorMode = colorMode,
                    SingleColor = singleColor,
                    AlphaCutoff = alphaCutoff
                };
                VoxelizationResult result = MeshVoxelizer.Voxelize(source, settings,
                    (progress, message) => EditorUtility.DisplayCancelableProgressBar(
                        "Voxel Bridge · Resolution", message, progress * 0.9f));
                EditorUtility.DisplayProgressBar("Voxel Bridge · Resolution",
                    "Reducing palette to 255 colors", 0.93f);
                QuantizedVoxels quantized = VoxelColorQuantizer.Quantize(result.Grid);

                string safeName = VoxelLodPipeline.MakeSafeFileName(source.name);
                generatedVoxPath = AssetDatabase.GenerateUniqueAssetPath($"{exportFolder}/{safeName}.vox");
                string absolutePath = VoxelLodPipeline.AssetPathToAbsolute(generatedVoxPath);
                VoxWriter.Write(absolutePath, result.Grid, quantized);
                var metadata = new VoxelBridgeMetadata
                {
                    sourceName = source.name,
                    sourceAssetPath = AssetDatabase.GetAssetPath(source),
                    resolution = resolution,
                    padding = padding,
                    fillInterior = fillInterior,
                    hideInternalCavities = hideInternalCavities,
                    voxelSize = result.Grid.VoxelSize,
                    gridOrigin = result.Grid.Origin,
                    unityGridSize = result.Grid.Size,
                    voxGridSize = new Vector3Int(result.Grid.Size.x, result.Grid.Size.z, result.Grid.Size.y),
                    voxelCount = result.Grid.CountOccupied(),
                    paletteColorCount = quantized.Palette.Length
                };
                File.WriteAllText(Path.ChangeExtension(absolutePath, ".voxelbridge.json"),
                    JsonUtility.ToJson(metadata, true));
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                generatedVox = AssetDatabase.LoadMainAssetAtPath(generatedVoxPath);
                Selection.activeObject = generatedVox;
                EditorGUIUtility.PingObject(generatedVox);
                status = $"{metadata.voxelCount:N0} voxels · {metadata.paletteColorCount} colors · " +
                         $"grid {metadata.voxGridSize.x}×{metadata.voxGridSize.y}×{metadata.voxGridSize.z}.";
            }
            catch (OperationCanceledException)
            {
                status = "Conversion cancelled.";
            }
            catch (Exception exception)
            {
                status = "Error: " + exception.Message;
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Voxel Bridge", status, "Close");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
