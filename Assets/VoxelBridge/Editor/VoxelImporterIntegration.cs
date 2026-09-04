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
                error = "The selected asset is not a .vox file.";
                return false;
            }

            string metadataAssetPath = GetMetadataAssetPath(voxAssetPath);
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                error = "Could not find the project root.";
                return false;
            }
            string absolutePath = Path.GetFullPath(Path.Combine(projectRoot, metadataAssetPath));
            if (!File.Exists(absolutePath))
            {
                error = $"Missing sidecar '{metadataAssetPath}'.";
                return false;
            }

            try
            {
                metadata = JsonUtility.FromJson<VoxelBridgeMetadata>(File.ReadAllText(absolutePath));
            }
            catch (Exception exception)
            {
                error = "Could not read metadata: " + exception.Message;
                return false;
            }

            if (metadata == null || (metadata.formatVersion != 3 && metadata.formatVersion != 4) ||
                metadata.voxelSize <= 0f || float.IsNaN(metadata.voxelSize) || float.IsInfinity(metadata.voxelSize) ||
                metadata.unityGridSize.x <= 0 || metadata.unityGridSize.y <= 0 || metadata.unityGridSize.z <= 0)
            {
                error = "The Voxel Bridge sidecar is invalid or unsupported. Expected RGB v3 or semantic v4; regenerate the VOX export.";
                metadata = null;
                return false;
            }
            if (metadata.formatVersion == 4 &&
                (metadata.semantic == null || metadata.semantic.formatVersion != 1 ||
                 metadata.semantic.slots == null || metadata.semantic.slots.Length == 0))
            {
                error = "The semantic sidecar does not contain a supported ColorID + SurfaceID table.";
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
            Vector3Int size = metadata.importGridSize.x > 0 &&
                              metadata.importGridSize.y > 0 && metadata.importGridSize.z > 0
                ? metadata.importGridSize
                : metadata.unityGridSize;
            Vector3 origin = metadata.importGridSize.x > 0 &&
                             metadata.importGridSize.y > 0 && metadata.importGridSize.z > 0
                ? metadata.importGridOrigin
                : metadata.gridOrigin;
            if (voxelSize <= 0f || size.x <= 0 || size.y <= 0 || size.z <= 0)
                throw new ArgumentException("The metadata does not contain a valid grid.", nameof(metadata));

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
            if (metadata == null) return false;
            if (metadata.unityGridSize.x > 256 || metadata.unityGridSize.y > 256 ||
                metadata.unityGridSize.z > 256)
                return true;
            VoxelChunkMetadata[] chunks = metadata.chunks;
            if (chunks == null || chunks.Length == 0) return false;
            return chunks.Length > 1 || chunks[0].gridOffset != Vector3Int.zero;
        }

        public static bool ApplyAndReimport(string voxAssetPath, out string message, bool forceReimport = false)
        {
            message = null;
            AssetImporter importer = AssetImporter.GetAtPath(voxAssetPath);
            if (importer == null)
            {
                message = "Unity has not imported the .vox file yet.";
                return false;
            }
            if (!TryLoadMetadata(voxAssetPath, out VoxelBridgeMetadata metadata, out message)) return false;
            if (!IsSupportedImporter(importer))
            {
                message = "The .vox asset is not handled by AloneSoft Voxel Importer.";
                return false;
            }
            if (!TryValidateImporterApi(importer.GetType(), out message)) return false;

            if (!Application.isBatchMode)
                Undo.RecordObject(importer, "Apply Voxel Bridge Metadata");
            bool changed = Apply(importer, metadata);
            if (changed || forceReimport)
            {
                EditorUtility.SetDirty(importer);
                if (changed)
                    AssetDatabase.WriteImportSettingsIfDirty(voxAssetPath);
                importer.SaveAndReimport();
            }
            message = changed
                ? "Applied scale, pivot, orientation, and optimization to the .vox asset."
                : forceReimport
                    ? "Reimported the .vox asset with the current Voxel Bridge settings."
                    : "The .vox asset already uses the correct Voxel Bridge metadata.";
            return true;
        }

        internal static bool TryApplyDuringImport(AssetImporter importer, string voxAssetPath)
        {
            if (!IsSupportedImporter(importer)) return false;
            if (!TryLoadMetadata(voxAssetPath, out VoxelBridgeMetadata metadata, out string metadataError))
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (!string.IsNullOrEmpty(projectRoot) &&
                    File.Exists(Path.Combine(projectRoot, GetMetadataAssetPath(voxAssetPath))))
                    Debug.LogWarning($"Voxel Bridge could not configure '{voxAssetPath}': {metadataError}");
                return false;
            }
            if (!TryValidateImporterApi(importer.GetType(), out string error))
            {
                Debug.LogWarning($"Voxel Bridge could not configure '{voxAssetPath}': {error}");
                return false;
            }
            return Apply(importer, metadata);
        }

        private static bool IsSupportedImporter(AssetImporter importer)
        {
            return importer != null && importer.GetType().FullName == ImporterTypeName;
        }

        private static bool TryValidateImporterApi(Type type, out string error)
        {
            FieldInfo meshMode = type.GetField("meshMode", PublicInstance);
            bool valid = type.GetField("importScale", PublicInstance)?.FieldType == typeof(Vector3) &&
                         type.GetField("importOffset", PublicInstance)?.FieldType == typeof(Vector3) &&
                         type.GetField("combineFaces", PublicInstance)?.FieldType == typeof(bool) &&
                         type.GetField("shareSameFace", PublicInstance)?.FieldType == typeof(bool) &&
                         type.GetField("ignoreCavity", PublicInstance)?.FieldType == typeof(bool) &&
                         meshMode?.FieldType.IsEnum == true &&
                         Enum.IsDefined(meshMode.FieldType, 0) && Enum.IsDefined(meshMode.FieldType, 1);
            error = valid ? null : "The installed Voxel Importer API does not match the expected import settings. " +
                                  "No settings were changed; update the Voxel Bridge adapter before importing.";
            return valid;
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
            changed |= SetField(type, importer, "ignoreCavity", metadata.hideInternalCavities);
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
        [MenuItem("Tools/Voxel Bridge/Sync All Generated VOX Assets")]
        public static void SyncAllGeneratedVoxAssets()
        {
            SyncAllGeneratedVoxAssets(true);
        }

        internal static void SyncAllGeneratedVoxAssets(bool showDialog)
        {
            string[] files = Directory.GetFiles(Application.dataPath, "*.vox", SearchOption.AllDirectories);
            int synchronized = 0;
            int skipped = 0;
            int failed = 0;
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
                {
                    failed++;
                    Debug.LogWarning($"Voxel Bridge could not synchronize '{assetPath}': {message}");
                }
            }

            string result = $"Voxel Bridge synchronized {synchronized} .vox file(s); failed {failed}; " +
                            $"skipped {skipped} without a sidecar.";
            Debug.Log(result);
            if (showDialog && !Application.isBatchMode)
                EditorUtility.DisplayDialog("Voxel Bridge", result, "OK");
        }
    }

    internal sealed class VoxelBridgeAssetPostprocessor : AssetPostprocessor
    {
        private void OnPreprocessAsset()
        {
            if (!assetPath.EndsWith(".vox", StringComparison.OrdinalIgnoreCase)) return;
            VoxelImporterIntegration.TryApplyDuringImport(assetImporter, assetPath);
        }
    }
}
