using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelBridgeWindow : EditorWindow
    {
        private const string DefaultExportFolder = "Assets/VoxelBridgeExports";
        private const string MagicaVoxelPathKey = "LocalModels.VoxelBridge.MagicaVoxelPath";

        private UnityEngine.Object source;
        private VoxelStyleProfile styleProfile;
        private int resolution = 64;
        private int padding = 1;
        private bool fillInterior = true;
        private bool hideInternalCavities = true;
        private VoxelColorMode colorMode = VoxelColorMode.MaterialAndTexture;
        private Color singleColor = new Color32(180, 180, 180, 255);
        private float alphaCutoff = 0.1f;
        private string exportFolder = DefaultExportFolder;
        private string lastVoxPath;
        private string status;
        private Vector2 scroll;

        private UnityEngine.Object directVoxAsset;
        private GameObject returnedObj;
        private TextAsset metadataAsset;
        private UnityEngine.Object manualParentVox;
        private int manualTargetLod = 1;
        private VoxelLodGenerationMode manualGenerationMode = VoxelLodGenerationMode.ReduceParent;
        private TextAsset lodSetManifest;
        private ReturnAxis returnAxis = ReturnAxis.MagicaVoxelDefault;
        private string prefabFolder = "Assets/VoxelBridgeImports";

        private enum ReturnAxis
        {
            MagicaVoxelDefault,
            AlreadyUnityAligned
        }

        [MenuItem("Tools/Voxel Bridge/Mesh a MagicaVoxel")]
        private static void OpenWindow()
        {
            var window = GetWindow<VoxelBridgeWindow>();
            window.titleContent = new GUIContent("Voxel Bridge");
            window.minSize = new Vector2(440, 570);
            if (IsSupported(Selection.activeObject)) window.source = Selection.activeObject;
            window.Show();
        }

        [MenuItem("Assets/Voxel Bridge/Convertir a MagicaVoxel", false, 2100)]
        private static void OpenFromSelection() => OpenWindow();

        [MenuItem("Assets/Voxel Bridge/Convertir a MagicaVoxel", true)]
        private static bool ValidateOpenFromSelection() => IsSupported(Selection.activeObject);

        [MenuItem("Assets/Voxel Bridge/Aplicar escala al .vox", false, 2101)]
        private static void ApplyScaleFromSelection()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            bool success = VoxelImporterIntegration.ApplyAndReimport(path, out string message);
            if (success) Debug.Log($"Voxel Bridge: {message} ({path})", Selection.activeObject);
            else EditorUtility.DisplayDialog("Voxel Bridge", message, "Cerrar");
        }

        [MenuItem("Assets/Voxel Bridge/Aplicar escala al .vox", true)]
        private static bool ValidateApplyScaleFromSelection() => VoxelImporterIntegration.IsVoxAsset(Selection.activeObject);

        [MenuItem("Assets/Voxel Bridge/Abrir en MagicaVoxel", false, 2102)]
        private static void OpenSelectedVoxInMagicaVoxel()
        {
            OpenVoxAssetInMagicaVoxel(Selection.activeObject);
        }

        [MenuItem("Assets/Voxel Bridge/Abrir en MagicaVoxel", true)]
        private static bool ValidateOpenSelectedVoxInMagicaVoxel() =>
            VoxelImporterIntegration.IsVoxAsset(Selection.activeObject);

        private void OnEnable()
        {
            if (source == null && IsSupported(Selection.activeObject)) source = Selection.activeObject;
            if (directVoxAsset == null && VoxelImporterIntegration.IsVoxAsset(Selection.activeObject))
                directVoxAsset = Selection.activeObject;
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawHeader();
            EditorGUILayout.Space(8);
            DrawExportSection();
            EditorGUILayout.Space(18);
            DrawLodSection();
            EditorGUILayout.Space(18);
            DrawDirectVoxSection();
            EditorGUILayout.Space(18);
            DrawReturnSection();
            EditorGUILayout.EndScrollView();
        }

        private static void DrawHeader()
        {
            EditorGUILayout.LabelField("Voxel Bridge", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Convierte mallas a una escala voxel física consistente, genera LODs editables en MagicaVoxel y prepara un prefab LOD para Unity.",
                MessageType.Info);
        }

        private void DrawExportSection()
        {
            EditorGUILayout.LabelField("1. Unity → MagicaVoxel", EditorStyles.boldLabel);
            source = EditorGUILayout.ObjectField(new GUIContent("Modelo", "GameObject, prefab, FBX/OBJ o Mesh"),
                source, typeof(UnityEngine.Object), true);
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
                string levels = string.Join(", ", System.Linq.Enumerable.Range(0, styleProfile.LodCount)
                    .Select(i => $"LOD{i}=x{styleProfile.GetLodMultiplier(i)} " +
                                 $"({styleProfile.BaseVoxelSize * styleProfile.GetLodMultiplier(i):0.###} m)"));
                EditorGUILayout.HelpBox(
                    $"Unidad base: {styleProfile.BaseVoxelSize:0.###} m · Chunk: {styleProfile.ChunkCellSize} celdas\n{levels}",
                    MessageType.Info);
            }

            bool canGenerateLods = source != null && IsSupported(source) && styleProfile != null &&
                                   styleProfile.TryValidate(out _) && IsAssetFolder(exportFolder) &&
                                   IsAssetFolder(prefabFolder);
            using (new EditorGUI.DisabledScope(!canGenerateLods))
            {
                if (GUILayout.Button("Generar LODs automáticos + prefab", GUILayout.Height(36)))
                    GenerateAutomaticLods();
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Conversión individual/legada", EditorStyles.miniBoldLabel);
            resolution = EditorGUILayout.IntSlider(new GUIContent("Resolución", "Vóxeles en el eje más largo"),
                resolution, 8, 256);
            padding = EditorGUILayout.IntSlider(new GUIContent("Margen", "Vóxeles vacíos alrededor del modelo"),
                padding, 0, 8);
            fillInterior = EditorGUILayout.Toggle(new GUIContent("Rellenar interior", "Recomendado para modelos cerrados"), fillInterior);
            hideInternalCavities = EditorGUILayout.Toggle(new GUIContent("Ocultar cavidades cerradas",
                "Activa Ignore Cavity de Voxel Importer; no afecta huecos conectados con el exterior"), hideInternalCavities);
            if (!fillInterior)
                EditorGUILayout.HelpBox(
                    "Sin relleno, el .vox es una carcasa hueca y sus caras interiores existen. Voxel Importer puede ocultar cavidades cerradas, pero las aberturas dejan entrar al exterior.",
                    MessageType.None);
            colorMode = (VoxelColorMode)EditorGUILayout.Popup("Origen del color", (int)colorMode,
                new[] { "Material + textura", "Solo material", "Color único" });
            if (colorMode == VoxelColorMode.SingleColor)
                singleColor = EditorGUILayout.ColorField("Color", singleColor);
            if (colorMode == VoxelColorMode.MaterialAndTexture)
                alphaCutoff = EditorGUILayout.Slider(new GUIContent("Corte de alpha", "Descarta texeles transparentes"), alphaCutoff, 0f, 1f);

            EditorGUILayout.BeginHorizontal();
            exportFolder = EditorGUILayout.TextField("Carpeta de salida", exportFolder);
            if (GUILayout.Button("…", GUILayout.Width(30))) PickAssetFolder(ref exportFolder);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            prefabFolder = EditorGUILayout.TextField("Carpeta de prefabs", prefabFolder);
            if (GUILayout.Button("…", GUILayout.Width(30))) PickAssetFolder(ref prefabFolder);
            EditorGUILayout.EndHorizontal();

            bool settingsValid = source != null && IsSupported(source) && IsAssetFolder(exportFolder) &&
                                 resolution > padding * 2;
            using (new EditorGUI.DisabledScope(!settingsValid))
            {
                if (GUILayout.Button("Convertir a .vox", GUILayout.Height(34))) Convert();
            }

            if (source != null && !IsSupported(source))
                EditorGUILayout.HelpBox("Selecciona un GameObject, prefab, FBX/OBJ o Mesh.", MessageType.Warning);
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(lastVoxPath) || !File.Exists(lastVoxPath)))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Mostrar archivo")) EditorUtility.RevealInFinder(lastVoxPath);
                if (GUILayout.Button("Abrir en MagicaVoxel")) OpenInMagicaVoxel(lastVoxPath);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawLodSection()
        {
            EditorGUILayout.LabelField("2. LOD manual", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Duplica el LOD anterior para una simplificación artística libre o genera directamente la rejilla física correspondiente al nuevo nivel.",
                MessageType.Info);
            manualParentVox = EditorGUILayout.ObjectField("LOD padre (.vox)", manualParentVox,
                typeof(UnityEngine.Object), false);
            int firstTargetLod = 1;
            if (VoxelImporterIntegration.IsVoxAsset(manualParentVox) &&
                VoxelImporterIntegration.TryLoadMetadata(AssetDatabase.GetAssetPath(manualParentVox),
                    out VoxelBridgeMetadata parentMetadata, out _))
                firstTargetLod = parentMetadata.lodIndex + 1;
            bool hasManualTarget = styleProfile != null && firstTargetLod < styleProfile.LodCount;
            if (hasManualTarget)
            {
                string[] labels = System.Linq.Enumerable.Range(firstTargetLod,
                        styleProfile.LodCount - firstTargetLod)
                    .Select(i => $"LOD {i} · x{styleProfile.GetLodMultiplier(i)}")
                    .ToArray();
                int selected = Mathf.Clamp(manualTargetLod - firstTargetLod, 0, labels.Length - 1);
                selected = EditorGUILayout.Popup("LOD destino", selected, labels);
                manualTargetLod = selected + firstTargetLod;
            }
            else if (VoxelImporterIntegration.IsVoxAsset(manualParentVox) && styleProfile != null)
                EditorGUILayout.HelpBox("El LOD padre ya es el último nivel definido en el perfil.", MessageType.Warning);
            manualGenerationMode = (VoxelLodGenerationMode)EditorGUILayout.Popup(
                "Punto de partida", manualGenerationMode == VoxelLodGenerationMode.DuplicateParent ? 0 : 1,
                new[] { "Duplicar anterior (libre)", "Reducir a resolución objetivo" }) == 0
                ? VoxelLodGenerationMode.DuplicateParent
                : VoxelLodGenerationMode.ReduceParent;

            bool canGenerateManual = hasManualTarget && styleProfile.TryValidate(out _) &&
                                     VoxelImporterIntegration.IsVoxAsset(manualParentVox) &&
                                     IsAssetFolder(prefabFolder);
            using (new EditorGUI.DisabledScope(!canGenerateManual))
            {
                if (GUILayout.Button("Crear LOD manual y actualizar prefab", GUILayout.Height(32)))
                    GenerateManualLod();
            }

            EditorGUILayout.Space(8);
            lodSetManifest = (TextAsset)EditorGUILayout.ObjectField("Manifiesto .voxset", lodSetManifest,
                typeof(TextAsset), false);
            using (new EditorGUI.DisabledScope(lodSetManifest == null || !IsAssetFolder(prefabFolder)))
            {
                if (GUILayout.Button("Reconstruir prefab LOD")) RebuildLodPrefab();
            }
        }

        private void DrawDirectVoxSection()
        {
            EditorGUILayout.LabelField("3. .vox directo en Unity", EditorStyles.boldLabel);
            if (!VoxelImporterIntegration.IsInstalled)
            {
                EditorGUILayout.HelpBox(
                    "AloneSoft Voxel Importer no está cargado. El .vox se seguirá generando, pero esta sincronización estará desactivada.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Voxel Bridge sincroniza automáticamente escala, pivote, orientación, Combine Voxel Faces e Ignore Cavity cada vez que cambia el .vox.",
                    MessageType.Info);
            }
            directVoxAsset = EditorGUILayout.ObjectField("Asset .vox", directVoxAsset, typeof(UnityEngine.Object), false);
            if (directVoxAsset != null && VoxelImporterIntegration.IsVoxAsset(directVoxAsset))
                EditorGUILayout.LabelField("Sidecar", VoxelImporterIntegration.GetMetadataAssetPath(AssetDatabase.GetAssetPath(directVoxAsset)), EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(!VoxelImporterIntegration.IsInstalled ||
                                               !VoxelImporterIntegration.IsVoxAsset(directVoxAsset)))
            {
                if (GUILayout.Button("Aplicar metadatos y reimportar", GUILayout.Height(30)))
                {
                    string path = AssetDatabase.GetAssetPath(directVoxAsset);
                    bool success = VoxelImporterIntegration.ApplyAndReimport(path, out string message);
                    status = message;
                    if (!success) EditorUtility.DisplayDialog("Voxel Bridge", message, "Cerrar");
                }
            }
            using (new EditorGUI.DisabledScope(!VoxelImporterIntegration.IsVoxAsset(directVoxAsset)))
            {
                if (GUILayout.Button("Abrir en MagicaVoxel", GUILayout.Height(26)))
                    OpenVoxAssetInMagicaVoxel(directVoxAsset);
            }
        }

        private void DrawReturnSection()
        {
            EditorGUILayout.LabelField("4. MagicaVoxel OBJ → Unity (alternativa)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "En MagicaVoxel usa Export → obj y guarda el OBJ dentro de Assets. Selecciona ese modelo y el archivo .voxelbridge.json generado en el paso anterior.",
                MessageType.Info);
            returnedObj = (GameObject)EditorGUILayout.ObjectField("OBJ exportado", returnedObj, typeof(GameObject), false);
            metadataAsset = (TextAsset)EditorGUILayout.ObjectField("Metadatos", metadataAsset, typeof(TextAsset), false);
            returnAxis = (ReturnAxis)EditorGUILayout.Popup(new GUIContent("Ejes del OBJ",
                    "El valor predeterminado compensa la conversión Z-up de MagicaVoxel al OBJ Y-up"),
                (int)returnAxis, new[] { "MagicaVoxel predeterminado", "Ya alineado con Unity" });
            EditorGUILayout.LabelField("Carpeta de prefabs", prefabFolder, EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(returnedObj == null || metadataAsset == null || !IsAssetFolder(prefabFolder)))
            {
                if (GUILayout.Button("Crear prefab alineado", GUILayout.Height(30))) CreateRoundTripPrefab();
            }
        }

        private void CreateDefaultProfile()
        {
            const string settingsFolder = "Assets/VoxelBridgeSettings";
            const string profilePath = settingsFolder + "/VoxelStyleProfile.asset";
            EnsureAssetFolder(settingsFolder);
            styleProfile = AssetDatabase.LoadAssetAtPath<VoxelStyleProfile>(profilePath);
            if (styleProfile == null)
            {
                styleProfile = CreateInstance<VoxelStyleProfile>();
                AssetDatabase.CreateAsset(styleProfile, profilePath);
                AssetDatabase.SaveAssets();
            }
            Selection.activeObject = styleProfile;
            EditorGUIUtility.PingObject(styleProfile);
        }

        private VoxelLodBuildOptions CreateLodOptions()
        {
            return new VoxelLodBuildOptions
            {
                ColorMode = colorMode,
                SingleColor = singleColor,
                AlphaCutoff = alphaCutoff,
                ExportFolder = exportFolder,
                PrefabFolder = prefabFolder
            };
        }

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
                    directVoxAsset = AssetDatabase.LoadMainAssetAtPath(result.VoxAssetPaths[0]);
                    manualParentVox = directVoxAsset;
                    manualTargetLod = Mathf.Min(1, styleProfile.LodCount - 1);
                    lastVoxPath = AssetPathToAbsolute(result.VoxAssetPaths[0]);
                }
                UnityEngine.Object prefab = AssetDatabase.LoadMainAssetAtPath(result.PrefabAssetPath);
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
                status = $"Familia LOD creada: {result.VoxAssetPaths.Length} niveles · {result.PrefabAssetPath}";
            }
            catch (OperationCanceledException)
            {
                status = "Generación LOD cancelada.";
            }
            catch (Exception exception)
            {
                status = "Error: " + exception.Message;
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Voxel Bridge", status, "Cerrar");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void GenerateManualLod()
        {
            try
            {
                string parentPath = AssetDatabase.GetAssetPath(manualParentVox);
                VoxelLodBuildResult result = VoxelLodPipeline.GenerateManual(
                    parentPath, styleProfile, manualTargetLod, manualGenerationMode, CreateLodOptions());
                lodSetManifest = AssetDatabase.LoadAssetAtPath<TextAsset>(result.ManifestAssetPath);
                directVoxAsset = AssetDatabase.LoadMainAssetAtPath(result.VoxAssetPaths[0]);
                manualParentVox = directVoxAsset;
                manualTargetLod = Mathf.Min(manualTargetLod + 1, styleProfile.LodCount - 1);
                lastVoxPath = AssetPathToAbsolute(result.VoxAssetPaths[0]);
                UnityEngine.Object prefab = AssetDatabase.LoadMainAssetAtPath(result.PrefabAssetPath);
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
                status = $"LOD {manualTargetLod} creado y prefab actualizado: {result.PrefabAssetPath}";
            }
            catch (Exception exception)
            {
                status = "Error: " + exception.Message;
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Voxel Bridge", status, "Cerrar");
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
                string manifestPath = AssetDatabase.GetAssetPath(lodSetManifest);
                string prefabPath = VoxelLodPipeline.RebuildPrefab(manifestPath, prefabFolder);
                UnityEngine.Object prefab = AssetDatabase.LoadMainAssetAtPath(prefabPath);
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
                status = $"Prefab LOD reconstruido: {prefabPath}";
            }
            catch (Exception exception)
            {
                status = "Error: " + exception.Message;
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Voxel Bridge", status, "Cerrar");
            }
        }

        private void Convert()
        {
            try
            {
                EnsureAssetFolder(exportFolder);
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
                    (progress, message) => EditorUtility.DisplayCancelableProgressBar("Voxel Bridge", message, progress * 0.9f));
                EditorUtility.DisplayProgressBar("Voxel Bridge", "Reduciendo la paleta a 255 colores", 0.93f);
                QuantizedVoxels quantized = VoxelColorQuantizer.Quantize(result.Grid);

                string safeName = MakeSafeFileName(source.name);
                string voxAssetPath = AssetDatabase.GenerateUniqueAssetPath($"{exportFolder}/{safeName}.vox");
                string absoluteVoxPath = AssetPathToAbsolute(voxAssetPath);
                VoxWriter.Write(absoluteVoxPath, result.Grid, quantized);

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
                string metadataPath = Path.ChangeExtension(absoluteVoxPath, ".voxelbridge.json");
                File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata, true));
                AssetDatabase.Refresh();
                directVoxAsset = AssetDatabase.LoadMainAssetAtPath(voxAssetPath);
                lastVoxPath = absoluteVoxPath;
                status = $"Listo: {metadata.voxelCount:N0} vóxeles, {metadata.paletteColorCount} colores, " +
                         $"rejilla {metadata.voxGridSize.x}×{metadata.voxGridSize.y}×{metadata.voxGridSize.z}.";
                Debug.Log($"Voxel Bridge exportó '{voxAssetPath}' ({status})", source);
            }
            catch (OperationCanceledException)
            {
                status = "Conversión cancelada.";
            }
            catch (Exception exception)
            {
                status = "Error: " + exception.Message;
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Voxel Bridge", status, "Cerrar");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void CreateRoundTripPrefab()
        {
            try
            {
                VoxelBridgeMetadata metadata = JsonUtility.FromJson<VoxelBridgeMetadata>(metadataAsset.text);
                if (metadata == null || metadata.formatVersion < 1 || metadata.formatVersion > 2 || metadata.voxelSize <= 0f)
                    throw new InvalidDataException("El archivo de metadatos no es válido o no corresponde a Voxel Bridge.");
                EnsureAssetFolder(prefabFolder);

                var root = new GameObject(metadata.sourceName + "_Voxel");
                GameObject child = Instantiate(returnedObj);
                child.name = returnedObj.name;
                child.transform.SetParent(root.transform, false);
                child.transform.localPosition = metadata.gridOrigin;
                child.transform.localRotation = returnAxis == ReturnAxis.MagicaVoxelDefault
                    ? Quaternion.Euler(90f, 0f, 0f)
                    : Quaternion.identity;
                child.transform.localScale = Vector3.one * metadata.voxelSize;

                string path = AssetDatabase.GenerateUniqueAssetPath($"{prefabFolder}/{MakeSafeFileName(root.name)}.prefab");
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                DestroyImmediate(root);
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
                status = $"Prefab creado: {path}";
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Voxel Bridge", "No se pudo crear el prefab: " + exception.Message, "Cerrar");
            }
        }

        private static void OpenInMagicaVoxel(string voxPath)
        {
            string executable = EditorPrefs.GetString(MagicaVoxelPathKey, string.Empty);
            if (!File.Exists(executable))
            {
                executable = EditorUtility.OpenFilePanel("Selecciona MagicaVoxel.exe", string.Empty, "exe");
                if (string.IsNullOrEmpty(executable)) return;
                EditorPrefs.SetString(MagicaVoxelPathKey, executable);
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = $"\"{voxPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Voxel Bridge", "No se pudo abrir MagicaVoxel: " + exception.Message, "Cerrar");
            }
        }

        private static void OpenVoxAssetInMagicaVoxel(UnityEngine.Object asset)
        {
            if (!VoxelImporterIntegration.IsVoxAsset(asset)) return;
            string assetPath = AssetDatabase.GetAssetPath(asset);
            string absolutePath = AssetPathToAbsolute(assetPath);
            if (!File.Exists(absolutePath))
            {
                EditorUtility.DisplayDialog("Voxel Bridge", "No se encontró el archivo .vox seleccionado.", "Cerrar");
                return;
            }
            OpenInMagicaVoxel(absolutePath);
        }

        private static bool IsSupported(UnityEngine.Object value) => value is GameObject || value is Mesh;

        private static bool IsAssetFolder(string path)
        {
            path = NormalizeAssetPath(path);
            return path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal);
        }

        private static void PickAssetFolder(ref string path)
        {
            string selected = EditorUtility.OpenFolderPanel("Carpeta dentro de Assets", Application.dataPath, string.Empty);
            if (string.IsNullOrEmpty(selected)) return;
            selected = selected.Replace('\\', '/');
            string dataPath = Application.dataPath.Replace('\\', '/');
            if (!selected.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Voxel Bridge", "La carpeta debe estar dentro de Assets.", "Cerrar");
                return;
            }
            path = "Assets" + selected.Substring(dataPath.Length);
        }

        private static void EnsureAssetFolder(string path)
        {
            path = NormalizeAssetPath(path);
            if (!IsAssetFolder(path)) throw new ArgumentException("La carpeta debe estar dentro de Assets.");
            if (AssetDatabase.IsValidFolder(path)) return;
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string AssetPathToAbsolute(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                 ?? throw new InvalidOperationException("No se encontró la raíz del proyecto.");
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        private static string NormalizeAssetPath(string path) => (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');

        private static string MakeSafeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(value) ? "VoxelModel" : value;
        }
    }
}
