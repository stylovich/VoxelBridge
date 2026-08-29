using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelBridgeSourceSelection
    {
        public static bool IsSupported(Object value) => value is GameObject || value is Mesh;
    }

    internal static class VoxelBridgeFolderPicker
    {
        public static void Draw(string label, ref string assetPath)
        {
            EditorGUILayout.BeginHorizontal();
            assetPath = EditorGUILayout.TextField(label, assetPath);
            if (GUILayout.Button("…", GUILayout.Width(30))) Pick(ref assetPath);
            EditorGUILayout.EndHorizontal();
        }

        private static void Pick(ref string assetPath)
        {
            string selected = EditorUtility.OpenFolderPanel(
                "Carpeta dentro de Assets", Application.dataPath, string.Empty);
            if (string.IsNullOrEmpty(selected)) return;
            selected = selected.Replace('\\', '/');
            string dataPath = Application.dataPath.Replace('\\', '/');
            bool isAssetsRoot = selected.Equals(dataPath, StringComparison.OrdinalIgnoreCase);
            bool isInsideAssets = selected.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase);
            if (!isAssetsRoot && !isInsideAssets)
            {
                EditorUtility.DisplayDialog("Voxel Bridge",
                    "La carpeta debe estar dentro de Assets.", "Cerrar");
                return;
            }
            assetPath = "Assets" + selected.Substring(dataPath.Length);
        }
    }

    internal static class MagicaVoxelLauncher
    {
        private const string ExecutablePathKey = "LocalModels.VoxelBridge.MagicaVoxelPath";

        public static void OpenAsset(Object asset)
        {
            if (!VoxelImporterIntegration.IsVoxAsset(asset)) return;
            string path = VoxelLodPipeline.AssetPathToAbsolute(AssetDatabase.GetAssetPath(asset));
            if (!File.Exists(path))
            {
                EditorUtility.DisplayDialog("Voxel Bridge",
                    "No se encontró el archivo .vox seleccionado.", "Cerrar");
                return;
            }
            OpenPath(path);
        }

        public static void OpenPath(string voxPath)
        {
            string executable = EditorPrefs.GetString(ExecutablePathKey, string.Empty);
            if (!File.Exists(executable))
            {
                executable = EditorUtility.OpenFilePanel(
                    "Selecciona MagicaVoxel.exe", string.Empty, "exe");
                if (string.IsNullOrEmpty(executable)) return;
                EditorPrefs.SetString(ExecutablePathKey, executable);
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
                EditorUtility.DisplayDialog("Voxel Bridge",
                    "No se pudo abrir MagicaVoxel: " + exception.Message, "Cerrar");
            }
        }
    }
}
