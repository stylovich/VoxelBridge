using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    [Serializable]
    internal sealed class VoxelLodBatchCheckpointEntry
    {
        public string reuseKey;
        public string sourceFingerprint;
        public string sourceName;
        public string manifestAssetPath;
        public string prefabAssetPath;
        public string[] voxAssetPaths;
    }

    [Serializable]
    internal sealed class VoxelLodBatchCheckpointData
    {
        public int formatVersion = 1;
        public string signature;
        public string createdUtc;
        public string updatedUtc;
        public VoxelLodBatchCheckpointEntry[] completed = Array.Empty<VoxelLodBatchCheckpointEntry>();
    }

    [Serializable]
    internal sealed class VoxelLodIncompleteFamilyMarker
    {
        public int formatVersion = 1;
        public string sourceName;
        public string createdUtc;
    }

    internal static class VoxelLodBatchIdentity
    {
        public static string GetStableObjectKey(Object value)
        {
            if (value == null) return "null";
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    value, out string guid, out long localId) && !string.IsNullOrEmpty(guid))
                return $"asset:{guid}:{localId}";

            GlobalObjectId globalId = GlobalObjectId.GetGlobalObjectIdSlow(value);
            string serialized = globalId.ToString();
            if (!string.IsNullOrEmpty(serialized)) return "global:" + serialized;
            return $"instance:{value.GetType().FullName}:{value.GetInstanceID()}";
        }

        public static string GetSourceFingerprint(Object source)
        {
            if (source == null) return "null";
            string assetPath = AssetDatabase.GetAssetPath(source);
            if (!string.IsNullOrEmpty(assetPath))
                return Hash128.Compute(
                    $"{GetStableObjectKey(source)}:{AssetDatabase.GetAssetDependencyHash(assetPath)}")
                    .ToString();
            if (source is Mesh mesh)
                return Hash128.Compute(GetStableObjectKey(mesh)).ToString();
            if (!(source is GameObject root))
                return Hash128.Compute(GetStableObjectKey(source)).ToString();

            var builder = new StringBuilder(GetStableObjectKey(root));
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                builder.Append('|').Append(GetHierarchyPath(root.transform, transform));
                builder.Append(':').Append(transform.gameObject.activeSelf);
                AppendVector(builder, transform.localPosition);
                AppendVector(builder, transform.localEulerAngles);
                AppendVector(builder, transform.localScale);

                MeshFilter filter = transform.GetComponent<MeshFilter>();
                if (filter != null) AppendAsset(builder, filter.sharedMesh);
                SkinnedMeshRenderer skinned = transform.GetComponent<SkinnedMeshRenderer>();
                if (skinned != null) AppendAsset(builder, skinned.sharedMesh);
                Renderer renderer = transform.GetComponent<Renderer>();
                if (renderer == null) continue;
                foreach (Material material in renderer.sharedMaterials) AppendAsset(builder, material);
            }
            return Hash128.Compute(builder.ToString()).ToString();
        }

        public static string CreateBatchSignature(
            VoxelLodBatchSourcePlan[] plans, VoxelStyleProfile profile,
            VoxelLodBuildOptions lodOptions, VoxelLodBatchOptions batchOptions)
        {
            var builder = new StringBuilder("VoxelLodBatch:v2");
            builder.Append('|').Append(profile != null ? GetStableObjectKey(profile) : "profile:null");
            if (profile != null)
            {
                builder.Append('|').Append(profile.BaseVoxelSize.ToString("R"));
                builder.Append('|').Append(profile.Padding).Append('|').Append(profile.ChunkCellSize);
                builder.Append('|').Append(profile.FillInterior);
                for (int index = 0; index < profile.LodCount; index++)
                    builder.Append('|').Append(profile.GetLodMultiplier(index));
            }
            if (lodOptions != null)
            {
                builder.Append('|').Append(lodOptions.ExportFolder).Append('|').Append(lodOptions.ColorMode);
                builder.Append('|').Append(lodOptions.SingleColor.r).Append(',')
                    .Append(lodOptions.SingleColor.g).Append(',').Append(lodOptions.SingleColor.b)
                    .Append(',').Append(lodOptions.SingleColor.a);
                builder.Append('|').Append(lodOptions.AlphaCutoff.ToString("R"));
                builder.Append('|').Append(batchOptions == null
                    ? lodOptions.IncludeInactiveObjects
                    : !batchOptions.IgnoreInactiveObjects);
            }
            if (batchOptions != null)
            {
                builder.Append('|').Append(batchOptions.ReusePrefabSources);
                builder.Append('|').Append(batchOptions.ModifiedPrefabHandling);
                builder.Append('|').Append(batchOptions.MaximumEstimatedMemoryBytes);
                builder.Append('|').Append(batchOptions.SkipSourcesOverMemoryBudget);
                builder.Append('|').Append(batchOptions.AdaptInitialVoxelSize);
                builder.Append('|').Append(batchOptions.MaximumInitialLodIndex);
                builder.Append('|').Append(batchOptions.IgnoreInactiveObjects);
            }
            foreach (VoxelLodBatchSourcePlan plan in plans ?? Array.Empty<VoxelLodBatchSourcePlan>())
            {
                builder.Append('|').Append(GetStableObjectKey(plan.ReuseKey));
                builder.Append(':').Append(GetSourceFingerprint(plan.ConversionSource));
                builder.Append(':').Append(plan.Ignored);
            }
            return Hash128.Compute(builder.ToString()).ToString();
        }

        private static void AppendAsset(StringBuilder builder, Object value)
        {
            builder.Append('@').Append(GetStableObjectKey(value));
            if (value == null) return;
            string path = AssetDatabase.GetAssetPath(value);
            if (!string.IsNullOrEmpty(path))
                builder.Append(':').Append(AssetDatabase.GetAssetDependencyHash(path));
        }

        private static void AppendVector(StringBuilder builder, Vector3 value) =>
            builder.Append(':').Append(value.x.ToString("R")).Append(',')
                .Append(value.y.ToString("R")).Append(',').Append(value.z.ToString("R"));

        private static string GetHierarchyPath(Transform root, Transform current)
        {
            if (current == root) return ".";
            var names = new Stack<string>();
            while (current != null && current != root)
            {
                names.Push($"{current.name}[{current.GetSiblingIndex()}]");
                current = current.parent;
            }
            return string.Join("/", names);
        }
    }

    internal static class VoxelLodBatchCheckpointStore
    {
        private static string CheckpointRoot => Path.Combine(
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath,
            "Library", "VoxelBridge", "BatchCheckpoints");

        public static string GetCheckpointPath(string signature) =>
            Path.Combine(CheckpointRoot, signature + ".json");

        public static Dictionary<Object, VoxelLodBatchConversionOutcome> LoadOutcomes(
            string signature, VoxelLodBatchSourcePlan[] plans)
        {
            var outcomes = new Dictionary<Object, VoxelLodBatchConversionOutcome>();
            if (!TryRead(signature, out VoxelLodBatchCheckpointData data)) return outcomes;

            var entries = (data.completed ?? Array.Empty<VoxelLodBatchCheckpointEntry>())
                .GroupBy(entry => entry.reuseKey)
                .ToDictionary(group => group.Key, group => group.Last());
            bool removedInvalid = false;
            foreach (VoxelLodBatchSourcePlan plan in plans.Where(plan => !plan.Ignored)
                         .GroupBy(plan => plan.ReuseKey).Select(group => group.First()))
            {
                string key = VoxelLodBatchIdentity.GetStableObjectKey(plan.ReuseKey);
                if (!entries.TryGetValue(key, out VoxelLodBatchCheckpointEntry entry)) continue;
                if (entry.sourceFingerprint !=
                    VoxelLodBatchIdentity.GetSourceFingerprint(plan.ConversionSource) ||
                    !IsComplete(entry))
                {
                    entries.Remove(key);
                    removedInvalid = true;
                    continue;
                }
                var build = new VoxelLodBuildResult(
                    entry.manifestAssetPath, entry.prefabAssetPath,
                    entry.voxAssetPaths ?? Array.Empty<string>());
                outcomes.Add(plan.ReuseKey,
                    new VoxelLodBatchConversionOutcome(build, null, true));
            }

            if (removedInvalid)
            {
                data.completed = entries.Values.ToArray();
                Write(data);
            }
            return outcomes;
        }

        public static void Reset(string signature)
        {
            string path = GetCheckpointPath(signature);
            if (File.Exists(path)) File.Delete(path);
        }

        public static void Record(
            string signature, VoxelLodBatchSourcePlan plan, VoxelLodBuildResult result)
        {
            VoxelLodBatchCheckpointData data;
            if (!TryRead(signature, out data))
            {
                string now = DateTime.UtcNow.ToString("O");
                data = new VoxelLodBatchCheckpointData
                {
                    signature = signature,
                    createdUtc = now,
                    updatedUtc = now
                };
            }

            string key = VoxelLodBatchIdentity.GetStableObjectKey(plan.ReuseKey);
            var entries = new List<VoxelLodBatchCheckpointEntry>(
                data.completed ?? Array.Empty<VoxelLodBatchCheckpointEntry>());
            entries.RemoveAll(entry => entry.reuseKey == key);
            entries.Add(new VoxelLodBatchCheckpointEntry
            {
                reuseKey = key,
                sourceFingerprint = VoxelLodBatchIdentity.GetSourceFingerprint(plan.ConversionSource),
                sourceName = plan.Source.name,
                manifestAssetPath = result.ManifestAssetPath,
                prefabAssetPath = result.PrefabAssetPath,
                voxAssetPaths = result.VoxAssetPaths
            });
            data.completed = entries.ToArray();
            data.updatedUtc = DateTime.UtcNow.ToString("O");
            Write(data);
        }

        public static bool Exists(string signature) => File.Exists(GetCheckpointPath(signature));

        private static bool TryRead(string signature, out VoxelLodBatchCheckpointData data)
        {
            data = null;
            string path = GetCheckpointPath(signature);
            if (!File.Exists(path)) return false;
            try
            {
                data = JsonUtility.FromJson<VoxelLodBatchCheckpointData>(File.ReadAllText(path));
                return data != null && data.formatVersion == 1 && data.signature == signature;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Voxel Bridge ignoró el checkpoint dañado '{path}': {exception.Message}");
                return false;
            }
        }

        private static bool IsComplete(VoxelLodBatchCheckpointEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.manifestAssetPath) ||
                string.IsNullOrWhiteSpace(entry.prefabAssetPath) ||
                entry.voxAssetPaths == null || entry.voxAssetPaths.Length == 0)
                return false;
            if (!File.Exists(VoxelLodPipeline.AssetPathToAbsolute(entry.manifestAssetPath)) ||
                !File.Exists(VoxelLodPipeline.AssetPathToAbsolute(entry.prefabAssetPath)))
                return false;
            return entry.voxAssetPaths.All(path =>
                !string.IsNullOrWhiteSpace(path) &&
                File.Exists(VoxelLodPipeline.AssetPathToAbsolute(path)) &&
                File.Exists(VoxelLodPipeline.AssetPathToAbsolute(
                    VoxelImporterIntegration.GetMetadataAssetPath(path))));
        }

        private static void Write(VoxelLodBatchCheckpointData data)
        {
            Directory.CreateDirectory(CheckpointRoot);
            string path = GetCheckpointPath(data.signature);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(data, true));
            if (File.Exists(path))
            {
                string backup = path + ".bak";
                File.Replace(temporary, path, backup, true);
                if (File.Exists(backup)) File.Delete(backup);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
    }

    internal static class VoxelLodBatchRecovery
    {
        internal const string IncompleteMarkerFileName = ".voxelbridge.incomplete.json";

        public static void MarkFamilyIncomplete(string familyAssetFolder, string sourceName)
        {
            string markerPath = Path.Combine(
                VoxelLodPipeline.AssetPathToAbsolute(familyAssetFolder),
                IncompleteMarkerFileName);
            var marker = new VoxelLodIncompleteFamilyMarker
            {
                sourceName = sourceName,
                createdUtc = DateTime.UtcNow.ToString("O")
            };
            File.WriteAllText(markerPath, JsonUtility.ToJson(marker, true));
        }

        public static void CompleteFamily(string familyAssetFolder)
        {
            string markerPath = Path.Combine(
                VoxelLodPipeline.AssetPathToAbsolute(familyAssetFolder),
                IncompleteMarkerFileName);
            if (File.Exists(markerPath)) File.Delete(markerPath);
            if (File.Exists(markerPath + ".meta")) File.Delete(markerPath + ".meta");
        }

        public static void DeleteFamilyIfIncomplete(string familyAssetFolder)
        {
            string markerPath = Path.Combine(
                VoxelLodPipeline.AssetPathToAbsolute(familyAssetFolder),
                IncompleteMarkerFileName);
            if (!File.Exists(markerPath)) return;
            if (!AssetDatabase.DeleteAsset(familyAssetFolder))
            {
                string absolute = VoxelLodPipeline.AssetPathToAbsolute(familyAssetFolder);
                if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
            }
        }

        public static string[] FindIncompleteFamilies(string exportAssetFolder)
        {
            if (!VoxelLodPipeline.IsAssetFolder(exportAssetFolder)) return Array.Empty<string>();
            string root = VoxelLodPipeline.AssetPathToAbsolute(exportAssetFolder);
            if (!Directory.Exists(root)) return Array.Empty<string>();
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return Directory.EnumerateFiles(root, IncompleteMarkerFileName,
                    SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(path => !string.IsNullOrEmpty(path) &&
                               Path.GetFullPath(path).StartsWith(
                                   normalizedRoot, StringComparison.OrdinalIgnoreCase))
                .Select(AbsoluteToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path) && path != exportAssetFolder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static int CleanupIncompleteFamilies(string exportAssetFolder)
        {
            string[] families = FindIncompleteFamilies(exportAssetFolder);
            foreach (string family in families) DeleteFamilyIfIncomplete(family);
            if (families.Length > 0) AssetDatabase.Refresh();
            return families.Length;
        }

        private static string AbsoluteToAssetPath(string absolutePath)
        {
            string normalized = Path.GetFullPath(absolutePath).Replace('\\', '/');
            string dataPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (!normalized.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets" + normalized.Substring(dataPath.Length);
        }
    }

    internal static class VoxelLodBatchMemoryCleaner
    {
        private sealed class KeepAliveReferences : ScriptableObject
        {
            public Object[] Values;
        }

        public static void ReleaseUnusedMemory(params Object[] keepAlive)
        {
            AssetDatabase.SaveAssets();
            KeepAliveReferences holder = ScriptableObject.CreateInstance<KeepAliveReferences>();
            holder.hideFlags = HideFlags.HideAndDontSave;
            holder.Values = keepAlive?.Where(value => value != null).ToArray() ?? Array.Empty<Object>();
            try
            {
                EditorUtility.UnloadUnusedAssetsImmediate(true);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            finally
            {
                Object.DestroyImmediate(holder);
            }
        }
    }
}
