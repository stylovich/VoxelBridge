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
        private string status;
        private Vector2 scroll;

        [MenuItem("Tools/Voxel Bridge/VOX a Unity", false, 120)]
        private static void OpenWindow()
        {
            var window = GetWindow<VoxToUnityWindow>();
            window.titleContent = new GUIContent("VOX a Unity");
            window.minSize = new Vector2(440, 430);
            if (VoxelImporterIntegration.IsVoxAsset(Selection.activeObject))
                window.voxAsset = Selection.activeObject;
            window.Show();
        }

        [MenuItem("Assets/Voxel Bridge/Abrir conversor VOX a Unity", false, 2110)]
        private static void OpenFromSelection() => OpenWindow();

        [MenuItem("Assets/Voxel Bridge/Abrir conversor VOX a Unity", true)]
        private static bool ValidateOpenFromSelection() =>
            VoxelImporterIntegration.IsVoxAsset(Selection.activeObject);

        [MenuItem("Assets/Voxel Bridge/Aplicar metadatos al .vox", false, 2111)]
        private static void ApplyFromSelection()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            bool success = VoxelImporterIntegration.ApplyAndReimport(path, out string message);
            if (success) Debug.Log($"Voxel Bridge: {message} ({path})", Selection.activeObject);
            else EditorUtility.DisplayDialog("Voxel Bridge", message, "Cerrar");
        }

        [MenuItem("Assets/Voxel Bridge/Aplicar metadatos al .vox", true)]
        private static bool ValidateApplyFromSelection() =>
            VoxelImporterIntegration.IsVoxAsset(Selection.activeObject);

        [MenuItem("Assets/Voxel Bridge/Abrir en MagicaVoxel", false, 2112)]
        private static void OpenSelectedInMagicaVoxel() =>
            MagicaVoxelLauncher.OpenAsset(Selection.activeObject);

        [MenuItem("Assets/Voxel Bridge/Abrir en MagicaVoxel", true)]
        private static bool ValidateOpenInMagicaVoxel() =>
            VoxelImporterIntegration.IsVoxAsset(Selection.activeObject);

        private void OnEnable()
        {
            if (voxAsset == null && VoxelImporterIntegration.IsVoxAsset(Selection.activeObject))
                voxAsset = Selection.activeObject;
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("VOX a Unity", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Voxel Importer convierte el archivo .vox en mallas de Unity durante la importación. El asset sigue siendo físicamente un .vox aunque Project lo muestre como GameObject.",
                MessageType.Info);
            DrawDirectImport();
            EditorGUILayout.Space(18);
            DrawObjAlternative();
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        private void DrawDirectImport()
        {
            EditorGUILayout.LabelField("1. Importación directa .vox", EditorStyles.boldLabel);
            if (!VoxelImporterIntegration.IsInstalled)
                EditorGUILayout.HelpBox("AloneSoft Voxel Importer no está cargado.", MessageType.Warning);
            voxAsset = EditorGUILayout.ObjectField("Archivo .vox", voxAsset, typeof(Object), false);
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
                if (GUILayout.Button("Aplicar escala, pivote y reimportar", GUILayout.Height(30)))
                {
                    bool success = VoxelImporterIntegration.ApplyAndReimport(
                        AssetDatabase.GetAssetPath(voxAsset), out status);
                    if (!success) EditorUtility.DisplayDialog("Voxel Bridge", status, "Cerrar");
                }
            }
            using (new EditorGUI.DisabledScope(!VoxelImporterIntegration.IsVoxAsset(voxAsset)))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Seleccionar modelo importado"))
                {
                    Selection.activeObject = voxAsset;
                    EditorGUIUtility.PingObject(voxAsset);
                }
                if (GUILayout.Button("Mostrar archivo .vox"))
                    EditorUtility.RevealInFinder(VoxelLodPipeline.AssetPathToAbsolute(
                        AssetDatabase.GetAssetPath(voxAsset)));
                if (GUILayout.Button("Abrir en MagicaVoxel")) MagicaVoxelLauncher.OpenAsset(voxAsset);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawObjAlternative()
        {
            EditorGUILayout.LabelField("2. OBJ exportado por MagicaVoxel (alternativa)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Solo hace falta si no quieres conservar el .vox directo. Exporta OBJ desde MagicaVoxel y usa el sidecar original para recuperar escala y pivote.",
                MessageType.None);
            returnedObj = (GameObject)EditorGUILayout.ObjectField("OBJ exportado", returnedObj,
                typeof(GameObject), false);
            metadataAsset = (TextAsset)EditorGUILayout.ObjectField("Metadatos", metadataAsset,
                typeof(TextAsset), false);
            returnAxis = (ReturnAxis)EditorGUILayout.Popup(new GUIContent("Ejes del OBJ",
                    "El valor predeterminado compensa la conversión Z-up de MagicaVoxel al OBJ Y-up"),
                (int)returnAxis, new[] { "MagicaVoxel predeterminado", "Ya alineado con Unity" });
            VoxelBridgeFolderPicker.Draw("Carpeta de prefabs", ref prefabFolder);
            using (new EditorGUI.DisabledScope(returnedObj == null || metadataAsset == null ||
                                               !VoxelLodPipeline.IsAssetFolder(prefabFolder)))
            {
                if (GUILayout.Button("Crear prefab desde OBJ", GUILayout.Height(30))) CreateRoundTripPrefab();
            }
        }

        private void CreateRoundTripPrefab()
        {
            GameObject root = null;
            try
            {
                VoxelBridgeMetadata metadata = JsonUtility.FromJson<VoxelBridgeMetadata>(metadataAsset.text);
                if (metadata == null || metadata.formatVersion < 1 || metadata.formatVersion > 3 ||
                    metadata.voxelSize <= 0f)
                    throw new InvalidDataException(
                        "El archivo de metadatos no es válido o no corresponde a Voxel Bridge.");
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
                status = $"Prefab creado: {path}";
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                status = "No se pudo crear el prefab: " + exception.Message;
                EditorUtility.DisplayDialog("Voxel Bridge", status, "Cerrar");
            }
            finally
            {
                if (root != null) DestroyImmediate(root);
            }
        }
    }
}
