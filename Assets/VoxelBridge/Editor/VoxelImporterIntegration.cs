using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal readonly struct VoxelImporterTransform
    {
        public readonly Vector3 ImportScale;
        public readonly Vector3 ImportOffset;

        public VoxelImporterTransform(Vector3 importScale, Vector3 importOffset)
        {
            ImportScale = importScale;
            ImportOffset = importOffset;
        }
    }

    /// <summary>
    /// Optional integration with AloneSoft Voxel Importer. Reflection keeps Voxel Bridge usable
    /// if the third-party asset is later removed from the project.
    /// </summary>
    internal static class VoxelImporterIntegration
    {
        private const string ImporterTypeName = "VoxelImporter.VoxelScriptedImporter";
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        public static bool IsInstalled
        {
            get
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetType(ImporterTypeName, false) != null) return true;
                return false;
            }
        }

        public static bool IsVoxAsset(UnityEngine.Object asset)
        {
            if (asset == null) return false;
            string path = AssetDatabase.GetAssetPath(asset);
            return path.EndsWith(".vox", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetMetadataAssetPath(string voxAssetPath)
        {
            return Path.ChangeExtension(voxAssetPath.Replace('\\', '/'), ".voxelbridge.json").Replace('\\', '/');
        }

        public static bool TryLoadMetadata(string voxAssetPath, out VoxelBridgeMetadata metadata, out string error)
        {
            metadata = null;
            error = null;
            if (string.IsNullOrWhiteSpace(voxAssetPath) ||
                !voxAssetPath.EndsWith(".vox", StringComparison.OrdinalIgnoreCase))
            {
                error = "El asset seleccionado no es un archivo .vox.";
                return false;
            }

            string metadataAssetPath = GetMetadataAssetPath(voxAssetPath);
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                error = "No se encontró la raíz del proyecto.";
                return false;
            }
            string absolutePath = Path.GetFullPath(Path.Combine(projectRoot, metadataAssetPath));
            if (!File.Exists(absolutePath))
            {
                error = $"Falta el sidecar '{metadataAssetPath}'.";
                return false;
            }

            try
            {
                metadata = JsonUtility.FromJson<VoxelBridgeMetadata>(File.ReadAllText(absolutePath));
            }
            catch (Exception exception)
            {
                error = "No se pudieron leer los metadatos: " + exception.Message;
                return false;
            }

            if (metadata == null || metadata.formatVersion < 1 || metadata.formatVersion > 4 ||
                metadata.voxelSize <= 0f || float.IsNaN(metadata.voxelSize) || float.IsInfinity(metadata.voxelSize) ||
                metadata.unityGridSize.x <= 0 || metadata.unityGridSize.y <= 0 || metadata.unityGridSize.z <= 0)
            {
                error = "El sidecar de Voxel Bridge no es válido o usa una versión no compatible.";
                metadata = null;
                return false;
            }
            if (metadata.formatVersion >= 4 &&
                (metadata.semantic == null || metadata.semantic.formatVersion != 1 ||
                 metadata.semantic.slots == null || metadata.semantic.slots.Length == 0))
            {
                error = "El sidecar semántico no contiene una tabla ColorID + SurfaceID compatible.";
                metadata = null;
                return false;
            }
            return true;
        }

        internal static VoxelImporterTransform CalculateTransform(VoxelBridgeMetadata metadata)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            float voxelSize = metadata.voxelSize;
            bool usesSceneGraph = UsesSceneGraph(metadata);
            Vector3Int size = metadata.formatVersion >= 3 && metadata.importGridSize.x > 0 &&
                              metadata.importGridSize.y > 0 && metadata.importGridSize.z > 0
                ? metadata.importGridSize
                : metadata.unityGridSize;
            Vector3 origin = metadata.formatVersion >= 3 && metadata.importGridSize.x > 0 &&
                             metadata.importGridSize.y > 0 && metadata.importGridSize.z > 0
                ? metadata.importGridOrigin
                : metadata.gridOrigin;
            if (voxelSize <= 0f || size.x <= 0 || size.y <= 0 || size.z <= 0)
                throw new ArgumentException("Los metadatos no contienen una rejilla válida.", nameof(metadata));

            // Voxel Importer converts VOX coordinates to Unity by swapping Y/Z, inverting X/Z,
            // and centering those two axes for a single model. Scene graphs instead derive
            // localOffset from their node translations, so applying the model centering again
            // displaces a chunked asset by half of its occupied extent.
            Vector3 importScale = new Vector3(-voxelSize, voxelSize, -voxelSize);
            Vector3 importOffset = usesSceneGraph
                ? new Vector3(
                    -origin.x / voxelSize,
                    metadata.gridOrigin.y / voxelSize,
                    -origin.z / voxelSize)
                : new Vector3(
                    -size.x * 0.5f - origin.x / voxelSize,
                    origin.y / voxelSize,
                    -size.z * 0.5f - origin.z / voxelSize);
            return new VoxelImporterTransform(importScale, importOffset);
        }

        internal static bool UsesSceneGraph(VoxelBridgeMetadata metadata)
        {
            if (metadata == null || metadata.formatVersion < 3) return false;
            if (metadata.unityGridSize.x > 256 || metadata.unityGridSize.y > 256 ||
                metadata.unityGridSize.z > 256)
                return true;
            VoxelChunkMetadata[] chunks = metadata.chunks;
            if (chunks == null || chunks.Length == 0) return false;
            return chunks.Length > 1 || chunks[0].gridOffset != Vector3Int.zero;
        }

        internal static bool ShouldIgnoreCavity(VoxelBridgeMetadata metadata)
        {
            // Version 1 did not expose this option; preserve the desired Voxel Bridge behavior.
            return metadata.formatVersion < 2 || metadata.hideInternalCavities;
        }

        public static bool ApplyAndReimport(string voxAssetPath, out string message, bool forceReimport = false)
        {
            message = null;
            AssetImporter importer = AssetImporter.GetAtPath(voxAssetPath);
            if (importer == null)
            {
                message = "Unity todavía no ha importado el archivo .vox.";
                return false;
            }
            if (!TryLoadMetadata(voxAssetPath, out VoxelBridgeMetadata metadata, out message)) return false;
            if (!IsSupportedImporter(importer))
            {
                message = "El .vox no está siendo manejado por AloneSoft Voxel Importer.";
                return false;
            }

            if (!Application.isBatchMode)
                Undo.RecordObject(importer, "Aplicar metadatos de Voxel Bridge");
            bool changed = Apply(importer, metadata);
            if (changed || forceReimport)
            {
                EditorUtility.SetDirty(importer);
                if (changed)
                    AssetDatabase.WriteImportSettingsIfDirty(voxAssetPath);
                importer.SaveAndReimport();
            }
            message = changed
                ? "Escala, pivote, orientación y optimización aplicados al .vox."
                : forceReimport
                    ? "El .vox fue reimportado con la compatibilidad actual de Voxel Bridge."
                    : "El .vox ya usa los metadatos correctos de Voxel Bridge.";
            return true;
        }

        internal static bool TryApplyDuringImport(AssetImporter importer, string voxAssetPath)
        {
            if (!IsSupportedImporter(importer)) return false;
            if (!TryLoadMetadata(voxAssetPath, out VoxelBridgeMetadata metadata, out _)) return false;
            return Apply(importer, metadata);
        }

        private static bool IsSupportedImporter(AssetImporter importer)
        {
            return importer != null && importer.GetType().FullName == ImporterTypeName;
        }

        private static bool Apply(AssetImporter importer, VoxelBridgeMetadata metadata)
        {
            VoxelImporterTransform transform = CalculateTransform(metadata);
            Type type = importer.GetType();
            bool changed = false;
            changed |= SetField(type, importer, "importScale", transform.ImportScale);
            changed |= SetField(type, importer, "importOffset", transform.ImportOffset);
            changed |= SetField(type, importer, "combineFaces", true);
            changed |= SetField(type, importer, "shareSameFace", true);
            changed |= SetField(type, importer, "ignoreCavity", ShouldIgnoreCavity(metadata));
            if (metadata.formatVersion >= 3)
                changed |= SetEnumField(type, importer, "meshMode",
                    metadata.chunks != null && metadata.chunks.Length > 1 ? 1 : 0);
            return changed;
        }

        private static bool SetEnumField(Type type, object target, string name, int value)
        {
            FieldInfo field = type.GetField(name, PublicInstance);
            if (field == null || !field.FieldType.IsEnum || !Enum.IsDefined(field.FieldType, value)) return false;
            object desired = Enum.ToObject(field.FieldType, value);
            object current = field.GetValue(target);
            if (Equals(current, desired)) return false;
            field.SetValue(target, desired);
            return true;
        }

        private static bool SetField<T>(Type type, object target, string name, T value)
        {
            FieldInfo field = type.GetField(name, PublicInstance);
            if (field == null || field.FieldType != typeof(T)) return false;
            T current = (T)field.GetValue(target);
            if (Equals(current, value)) return false;
            field.SetValue(target, value);
            return true;
        }
    }

    public static class VoxelBridgeBatch
    {
        [MenuItem("Tools/Voxel Bridge/Sincronizar todos los .vox")]
        public static void SyncAllGeneratedVoxAssets()
        {
            SyncAllGeneratedVoxAssets(true);
        }

        internal static void SyncAllGeneratedVoxAssets(bool showDialog)
        {
            string[] files = Directory.GetFiles(Application.dataPath, "*.vox", SearchOption.AllDirectories);
            int synchronized = 0;
            int skipped = 0;
            foreach (string file in files)
            {
                string assetPath = "Assets" + file.Substring(Application.dataPath.Length).Replace('\\', '/');
                if (!File.Exists(Path.ChangeExtension(file, ".voxelbridge.json")))
                {
                    skipped++;
                    continue;
                }

                if (VoxelImporterIntegration.ApplyAndReimport(
                        assetPath, out string message, forceReimport: true))
                    synchronized++;
                else
                    Debug.LogWarning($"Voxel Bridge no pudo sincronizar '{assetPath}': {message}");
            }

            string result = $"Voxel Bridge sincronizó {synchronized} archivo(s) .vox; omitió {skipped} sin sidecar.";
            Debug.Log(result);
            if (showDialog && !Application.isBatchMode)
                EditorUtility.DisplayDialog("Voxel Bridge", result, "Aceptar");
        }
    }

    internal sealed class VoxelBridgeAssetPostprocessor : AssetPostprocessor
    {
        private void OnPreprocessAsset()
        {
            if (!assetPath.EndsWith(".vox", StringComparison.OrdinalIgnoreCase)) return;
            if (VoxelImporterIntegration.TryApplyDuringImport(assetImporter, assetPath))
                Debug.Log($"Voxel Bridge sincronizó escala y optimización de '{assetPath}'.");
        }
    }
}
