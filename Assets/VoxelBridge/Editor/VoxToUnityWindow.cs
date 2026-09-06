using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxToUnityWindow : EditorWindow
    {
        private enum ReturnAxis
        {
            MagicaVoxelDefault,
            AlreadyUnityAligned
        }

        private Object voxAsset;
        private GameObject returnedObj;
        private TextAsset metadataAsset;
        private ReturnAxis returnAxis = ReturnAxis.MagicaVoxelDefault;
        private string prefabFolder = "Assets/VoxelBridgeImports";
        [SerializeField] private VoxelStyleProfile productionProfile;
        private string status;
        private Vector2 scroll;

        [MenuItem("Tools/Voxel Bridge/VOX to Unity", false, 120)]
        private static void OpenWindow()
        {
            var window = GetWindow<VoxToUnityWindow>();
            window.titleContent = new GUIContent("VOX to Unity");
            window.minSize = new Vector2(440, 430);
            if (VoxelImporterIntegration.IsVoxAsset(Selection.activeObject))
                window.voxAsset = Selection.activeObject;
            window.Show();
        }

        [MenuItem("Assets/Voxel Bridge/Open VOX to Unity Converter", false, 2110)]
        private static void OpenFromSelection() => OpenWindow();

        [MenuItem("Assets/Voxel Bridge/Open VOX to Unity Converter", true)]
        private static bool ValidateOpenFromSelection() =>
            VoxelImporterIntegration.IsVoxAsset(Selection.activeObject);

        [MenuItem("Assets/Voxel Bridge/Apply Metadata to .vox", false, 2111)]
        private static void ApplyFromSelection()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            bool success = VoxelImporterIntegration.ApplyAndReimport(path, out string message);
            if (success) Debug.Log($"Voxel Bridge: {message} ({path})", Selection.activeObject);
            else EditorUtility.DisplayDialog("Voxel Bridge", message, "Close");
        }

        [MenuItem("Assets/Voxel Bridge/Apply Metadata to .vox", true)]
        private static bool ValidateApplyFromSelection() =>
            VoxelImporterIntegration.IsVoxAsset(Selection.activeObject);

        [MenuItem("Assets/Voxel Bridge/Open in MagicaVoxel", false, 2112)]
        private static void OpenSelectedInMagicaVoxel() =>
            MagicaVoxelLauncher.OpenAsset(Selection.activeObject);

        [MenuItem("Assets/Voxel Bridge/Open in MagicaVoxel", true)]
        private static bool ValidateOpenInMagicaVoxel() =>
            VoxelImporterIntegration.IsVoxAsset(Selection.activeObject);

        private void OnEnable()
        {
            if (voxAsset == null && VoxelImporterIntegration.IsVoxAsset(Selection.activeObject))
                voxAsset = Selection.activeObject;
        }

        internal static void OpenProductionFamily(string sourcePath)
        {
            var window = GetWindow<VoxToUnityWindow>();
            window.titleContent = new GUIContent("VOX to Unity");
            window.voxAsset = AssetDatabase.LoadMainAssetAtPath(sourcePath);
            window.Show();
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("VOX to Unity", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Voxel Importer convierte el archivo .vox en mallas de Unity durante la importación. El asset sigue siendo físicamente un .vox aunque Project lo muestre como GameObject.",
                MessageType.Info);
            DrawDirectImport();
            EditorGUILayout.Space(18);
            DrawProductionExport();
            DrawProductionFamily();
            EditorGUILayout.Space(18);
            DrawObjAlternative();
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        private void DrawDirectImport()
        {
            EditorGUILayout.LabelField("1. Direct .vox Import", EditorStyles.boldLabel);
            if (!VoxelImporterIntegration.IsInstalled)
                EditorGUILayout.HelpBox("AloneSoft Voxel Importer no está cargado.", MessageType.Warning);
            voxAsset = EditorGUILayout.ObjectField(".vox File", voxAsset, typeof(Object), false);
            if (VoxelImporterIntegration.IsVoxAsset(voxAsset))
            {
                string path = AssetDatabase.GetAssetPath(voxAsset);
                EditorGUILayout.SelectableLabel(path, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.LabelField("Sidecar", VoxelImporterIntegration.GetMetadataAssetPath(path),
                    EditorStyles.miniLabel);
            }
            using (new EditorGUI.DisabledScope(!VoxelImporterIntegration.IsInstalled ||
                                               !VoxelImporterIntegration.IsVoxAsset(voxAsset)))
            {
                if (GUILayout.Button("Apply Scale and Pivot, then Reimport", GUILayout.Height(30)))
                {
                    bool success = VoxelImporterIntegration.ApplyAndReimport(
                        AssetDatabase.GetAssetPath(voxAsset), out status);
                    if (!success) EditorUtility.DisplayDialog("Voxel Bridge", status, "Close");
                }
            }
            using (new EditorGUI.DisabledScope(!VoxelImporterIntegration.IsVoxAsset(voxAsset)))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Select Imported Model"))
                {
                    Selection.activeObject = voxAsset;
                    EditorGUIUtility.PingObject(voxAsset);
                }
                if (GUILayout.Button("Reveal .vox File"))
                    EditorUtility.RevealInFinder(VoxelLodPipeline.AssetPathToAbsolute(
                        AssetDatabase.GetAssetPath(voxAsset)));
                if (GUILayout.Button("Open in MagicaVoxel")) MagicaVoxelLauncher.OpenAsset(voxAsset);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawProductionFamily()
        {
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Semantic Production LOD Family", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Crea una familia de producción desde el LOD0 semántico editado. El .vox existente sigue siendo la fuente; los siguientes niveles se crean explícitamente desde el prefab. Cada nivel conserva chunks, IDs y un material compartido. Máximo 8 millones de celdas y 500.000 quads por nivel. La unidad efectiva de LOD0 determina la escala de toda la familia.", MessageType.Info);
            productionProfile = (VoxelStyleProfile)EditorGUILayout.ObjectField("LOD Profile", productionProfile, typeof(VoxelStyleProfile), false);
            using (new EditorGUI.DisabledScope(productionProfile == null || !VoxelImporterIntegration.IsVoxAsset(voxAsset)))
            {
                if (GUILayout.Button("Create or Select Production LOD Family", GUILayout.Height(30)))
                {
                    try
                    {
                        string manifestPath = VoxelProductionFamily.Create(AssetDatabase.GetAssetPath(voxAsset), productionProfile, prefabFolder, VoxelProductionEditor.Progress);
                        var manifest = VoxelProductionFamily.Load(manifestPath);
                        Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(manifest.prefabAssetPath);
                        EditorGUIUtility.PingObject(Selection.activeObject);
                        status = "Production family: " + manifest.prefabAssetPath;
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception exception) { EditorUtility.DisplayDialog("Voxel Bridge", exception.Message, "Close"); }
                    finally { EditorUtility.ClearProgressBar(); }
                }
            }
        }

        private void DrawProductionExport()
        {
            EditorGUILayout.LabelField("2. Semantic LOD0 Production Mesh", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Genera un prefab independiente desde el .vox seleccionado y sus IDs guardados. " +
                "Comparte un material HDRP por pareja de paletas, sin modificar la previsualización " +
                "de Voxel Importer ni la familia LOD. Admite superficies opacas y una sola malla: " +
                "máximo 8 millones de celdas y 500.000 quads. Si existe un prefab vinculado en la carpeta " +
                "de salida, actualiza su malla. El prefab ofrece Open in MagicaVoxel, Select Source VOX y Rebuild.",
                MessageType.Info);
            VoxelBridgeFolderPicker.Draw("Production Folder", ref prefabFolder);
            using (new EditorGUI.DisabledScope(!VoxelImporterIntegration.IsVoxAsset(voxAsset) ||
                                               !VoxelLodPipeline.IsAssetFolder(prefabFolder)))
            {
                if (!GUILayout.Button("Create or Rebuild Production LOD0", GUILayout.Height(30))) return;
                try
                {
                    GameObject prefab = VoxelProductionExporter.Export(AssetDatabase.GetAssetPath(voxAsset),
                        prefabFolder, value =>
                        {
                            if (EditorUtility.DisplayCancelableProgressBar("Voxel Bridge", "Building semantic LOD0", value))
                                throw new OperationCanceledException();
                        });
                    Selection.activeObject = prefab;
                    EditorGUIUtility.PingObject(prefab);
                    status = "Production prefab ready: " + AssetDatabase.GetAssetPath(prefab);
                }
                catch (OperationCanceledException) { status = "Production export cancelled. No output was saved."; }
                catch (Exception exception)
                {
                    status = "Production export failed: " + exception.Message;
                    EditorUtility.DisplayDialog("Voxel Bridge", status, "Close");
                }
                finally { EditorUtility.ClearProgressBar(); }
            }
        }

        private void DrawObjAlternative()
        {
            EditorGUILayout.LabelField("3. MagicaVoxel OBJ Export (Alternative)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Flujo alternativo para importar un OBJ exportado desde MagicaVoxel. El sidecar original recupera la escala y el pivote.",
                MessageType.None);
            EditorGUILayout.HelpBox(
                "Este flujo admite sidecars RGB v3. Los OBJ exportados no conservan el contrato " +
                "ColorID + SurfaceID de los archivos semánticos v4; conservar el .vox y su sidecar " +
                "para editar y generar sus LOD sin perder esa información.",
                MessageType.None);
            returnedObj = (GameObject)EditorGUILayout.ObjectField("Exported OBJ", returnedObj,
                typeof(GameObject), false);
            metadataAsset = (TextAsset)EditorGUILayout.ObjectField("Metadata", metadataAsset,
                typeof(TextAsset), false);
            returnAxis = (ReturnAxis)EditorGUILayout.Popup(new GUIContent("OBJ Axes",
                    "El valor predeterminado compensa la conversión Z-up de MagicaVoxel al OBJ Y-up"),
                (int)returnAxis, new[] { "MagicaVoxel Default", "Already Unity-Aligned" });
            VoxelBridgeFolderPicker.Draw("Prefab Folder", ref prefabFolder);
            using (new EditorGUI.DisabledScope(returnedObj == null || metadataAsset == null ||
                                               !VoxelLodPipeline.IsAssetFolder(prefabFolder)))
            {
                if (GUILayout.Button("Create Prefab from OBJ", GUILayout.Height(30))) CreateRoundTripPrefab();
            }
        }

        private void CreateRoundTripPrefab()
        {
            GameObject root = null;
            try
            {
                VoxelBridgeMetadata metadata = JsonUtility.FromJson<VoxelBridgeMetadata>(metadataAsset.text);
                if (metadata == null || metadata.formatVersion != 3 ||
                    !float.IsFinite(metadata.voxelSize) || metadata.voxelSize <= 0f)
                    throw new InvalidDataException(
                        "OBJ conversion requires valid Voxel Bridge RGB metadata (version 3). " +
                        "Semantic metadata cannot be restored from an exported OBJ.");
                VoxelLodPipeline.EnsureAssetFolder(prefabFolder);
                root = new GameObject(metadata.sourceName + "_Voxel");
                GameObject child = Instantiate(returnedObj);
                child.name = returnedObj.name;
                child.transform.SetParent(root.transform, false);
                child.transform.localPosition = metadata.gridOrigin;
                child.transform.localRotation = returnAxis == ReturnAxis.MagicaVoxelDefault
                    ? Quaternion.Euler(90f, 0f, 0f)
                    : Quaternion.identity;
                child.transform.localScale = Vector3.one * metadata.voxelSize;

                string path = AssetDatabase.GenerateUniqueAssetPath(
                    $"{prefabFolder}/{VoxelLodPipeline.MakeSafeFileName(root.name)}.prefab");
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
                status = $"Prefab created: {path}";
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                status = "Could not create prefab: " + exception.Message;
                EditorUtility.DisplayDialog("Voxel Bridge", status, "Close");
            }
            finally
            {
                if (root != null) DestroyImmediate(root);
            }
        }
    }
}
