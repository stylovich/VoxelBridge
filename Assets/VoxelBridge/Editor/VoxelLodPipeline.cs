using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelLodBuildOptions
    {
        public VoxelColorMode ColorMode;
        public Color32 SingleColor;
        public float AlphaCutoff;
        public string ExportFolder;
    }

    internal readonly struct VoxelLodBuildResult
    {
        public readonly string ManifestAssetPath;
        public readonly string PrefabAssetPath;
        public readonly string[] VoxAssetPaths;

        public VoxelLodBuildResult(string manifestAssetPath, string prefabAssetPath, string[] voxAssetPaths)
        {
            ManifestAssetPath = manifestAssetPath;
            PrefabAssetPath = prefabAssetPath;
            VoxAssetPaths = voxAssetPaths;
        }
    }

    internal enum VoxelPrefabOverrideHandling
    {
        UsePrefabSource,
        ConvertInstanceSeparately,
        IgnoreInstance
    }

    internal sealed class VoxelLodBatchOptions
    {
        public bool ReusePrefabSources = true;
        public VoxelPrefabOverrideHandling ModifiedPrefabHandling =
            VoxelPrefabOverrideHandling.UsePrefabSource;
    }

    internal sealed class VoxelLodBatchSourcePlan
    {
        public readonly GameObject Source;
        public readonly Object ConversionSource;
        public readonly Object ReuseKey;
        public readonly bool HasPrefabOverrides;
        public readonly bool Ignored;

        public bool UsesPrefabSource => ConversionSource != null && ConversionSource != Source;

        public VoxelLodBatchSourcePlan(
            GameObject source, Object conversionSource, Object reuseKey,
            bool hasPrefabOverrides, bool ignored)
        {
            Source = source;
            ConversionSource = conversionSource;
            ReuseKey = reuseKey;
            HasPrefabOverrides = hasPrefabOverrides;
            Ignored = ignored;
        }
    }

    internal sealed class VoxelLodBatchItemResult
    {
        public readonly GameObject Source;
        public readonly Object ConversionSource;
        public readonly VoxelLodBuildResult BuildResult;
        public readonly string Error;
        public readonly bool Reused;
        public readonly bool Ignored;

        public bool Succeeded => !Ignored && string.IsNullOrEmpty(Error);
        public bool Failed => !Ignored && !string.IsNullOrEmpty(Error);

        public VoxelLodBatchItemResult(
            VoxelLodBatchSourcePlan plan, VoxelLodBuildResult buildResult,
            bool reused = false, string error = null)
        {
            Source = plan.Source;
            ConversionSource = plan.ConversionSource;
            BuildResult = buildResult;
            Error = error;
            Reused = reused;
            Ignored = plan.Ignored;
        }
    }

    internal sealed class VoxelLodBatchBuildResult
    {
        public readonly VoxelLodBatchItemResult[] Items;
        public readonly int CandidateCount;
        public readonly bool Cancelled;

        public int SucceededCount => Items.Count(item => item.Succeeded);
        public int FailedCount => Items.Count(item => item.Failed);
        public int IgnoredCount => Items.Count(item => item.Ignored);
        public int ReusedCount => Items.Count(item => item.Succeeded && item.Reused);
        public int CreatedFamilyCount => Items.Count(item => item.Succeeded && !item.Reused);
        public bool IsComplete => !Cancelled && Items.Length == CandidateCount &&
                                  FailedCount == 0 && IgnoredCount == 0;

        public VoxelLodBatchBuildResult(
            IEnumerable<VoxelLodBatchItemResult> items, int candidateCount, bool cancelled)
        {
            Items = items.ToArray();
            CandidateCount = candidateCount;
            Cancelled = cancelled;
        }
    }

    internal sealed class VoxelLodBatchConversionOutcome
    {
        public readonly VoxelLodBuildResult BuildResult;
        public readonly string Error;

        public VoxelLodBatchConversionOutcome(VoxelLodBuildResult buildResult, string error)
        {
            BuildResult = buildResult;
            Error = error;
        }
    }

    internal static class VoxelLodPipeline
    {
        internal static GameObject[] GetAutomaticBatchSources(GameObject parent)
        {
            if (parent == null) return Array.Empty<GameObject>();

            var sources = new List<GameObject>();
            for (int childIndex = 0; childIndex < parent.transform.childCount; childIndex++)
            {
                GameObject child = parent.transform.GetChild(childIndex).gameObject;
                bool hasMesh = child.GetComponentsInChildren<MeshFilter>(true)
                    .Any(filter => filter.sharedMesh != null);
                bool hasSkinnedMesh = child.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Any(renderer => renderer.sharedMesh != null);
                if (hasMesh || hasSkinnedMesh) sources.Add(child);
            }
            return sources.ToArray();
        }

        internal static VoxelLodBatchSourcePlan[] GetAutomaticBatchPlans(
            GameObject parent, VoxelLodBatchOptions batchOptions = null)
        {
            batchOptions ??= new VoxelLodBatchOptions();
            return GetAutomaticBatchSources(parent)
                .Select(source => CreateAutomaticBatchPlan(source, batchOptions))
                .ToArray();
        }

        public static VoxelLodBatchBuildResult GenerateAutomaticBatch(
            GameObject parent, VoxelStyleProfile profile, VoxelLodBuildOptions options,
            Func<float, string, bool> cancelProgress = null,
            VoxelLodBatchOptions batchOptions = null)
        {
            ValidateProfileAndOptions(profile, options);
            if (parent == null) throw new ArgumentNullException(nameof(parent));

            VoxelLodBatchSourcePlan[] plans = GetAutomaticBatchPlans(parent, batchOptions);
            if (plans.Length == 0)
                throw new InvalidOperationException(
                    "El objeto padre no contiene hijos directos con mallas para voxelizar.");

            var items = new List<VoxelLodBatchItemResult>();
            var outcomes = new Dictionary<Object, VoxelLodBatchConversionOutcome>();
            for (int sourceIndex = 0; sourceIndex < plans.Length; sourceIndex++)
            {
                VoxelLodBatchSourcePlan plan = plans[sourceIndex];
                GameObject current = plan.Source;
                float progressBase = (float)sourceIndex / plans.Length;
                if (cancelProgress != null && cancelProgress(progressBase,
                        $"Modelo {sourceIndex + 1} de {plans.Length}: preparando {current.name}"))
                    return new VoxelLodBatchBuildResult(items, plans.Length, true);

                if (plan.Ignored)
                {
                    items.Add(new VoxelLodBatchItemResult(plan, default));
                    continue;
                }

                if (outcomes.TryGetValue(plan.ReuseKey, out VoxelLodBatchConversionOutcome previous))
                {
                    items.Add(new VoxelLodBatchItemResult(
                        plan, previous.BuildResult, true, previous.Error));
                    continue;
                }

                try
                {
                    VoxelLodBuildResult build = GenerateAutomatic(
                        plan.ConversionSource, profile, options, (progress, message) =>
                            cancelProgress != null && cancelProgress(
                                progressBase + progress / plans.Length,
                                $"Modelo {sourceIndex + 1} de {plans.Length} · {current.name}: {message}"));
                    var outcome = new VoxelLodBatchConversionOutcome(build, null);
                    outcomes.Add(plan.ReuseKey, outcome);
                    items.Add(new VoxelLodBatchItemResult(plan, build));
                }
                catch (OperationCanceledException)
                {
                    return new VoxelLodBatchBuildResult(items, plans.Length, true);
                }
                catch (Exception exception)
                {
                    var outcome = new VoxelLodBatchConversionOutcome(default, exception.Message);
                    outcomes.Add(plan.ReuseKey, outcome);
                    items.Add(new VoxelLodBatchItemResult(plan, default, false, exception.Message));
                }
            }

            return new VoxelLodBatchBuildResult(items, plans.Length, false);
        }

        private static VoxelLodBatchSourcePlan CreateAutomaticBatchPlan(
            GameObject source, VoxelLodBatchOptions batchOptions)
        {
            if (!TryGetPrefabSource(source, out GameObject prefabSource,
                    out GameObject instanceRoot))
                return new VoxelLodBatchSourcePlan(source, source, source, false, false);

            bool hasOverrides = HasPrefabOverridesInScope(
                source, prefabSource, instanceRoot);
            if (hasOverrides && batchOptions.ModifiedPrefabHandling ==
                VoxelPrefabOverrideHandling.IgnoreInstance)
                return new VoxelLodBatchSourcePlan(source, null, source, true, true);

            bool convertSeparately = hasOverrides && batchOptions.ModifiedPrefabHandling ==
                VoxelPrefabOverrideHandling.ConvertInstanceSeparately;
            Object conversionSource = convertSeparately ||
                                      (!batchOptions.ReusePrefabSources && !hasOverrides)
                ? source
                : prefabSource;
            Object reuseKey = batchOptions.ReusePrefabSources && conversionSource == prefabSource
                ? prefabSource
                : source;
            return new VoxelLodBatchSourcePlan(
                source, conversionSource, reuseKey, hasOverrides, false);
        }

        private static bool TryGetPrefabSource(
            GameObject source, out GameObject prefabSource, out GameObject instanceRoot)
        {
            prefabSource = null;
            instanceRoot = null;
            if (source == null) return false;
            instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(source);
            if (instanceRoot == null) return false;

            prefabSource = PrefabUtility.GetCorrespondingObjectFromOriginalSource(source);
            if (prefabSource == null)
                prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(source);
            return prefabSource != null && AssetDatabase.Contains(prefabSource);
        }

        private static bool HasPrefabOverridesInScope(
            GameObject source, GameObject prefabSource, GameObject instanceRoot)
        {
            if (source == instanceRoot)
                return PrefabUtility.HasPrefabInstanceAnyOverrides(instanceRoot, false);
            if (!PrefabUtility.HasPrefabInstanceAnyOverrides(instanceRoot, false))
                return false;

            PropertyModification[] modifications =
                PrefabUtility.GetPropertyModifications(instanceRoot);
            if (modifications != null && modifications.Any(modification =>
                    IsAssetObjectInScope(modification.target, prefabSource)))
                return true;

            if (PrefabUtility.GetAddedGameObjects(instanceRoot).Any(added =>
                    IsInstanceObjectInScope(added.instanceGameObject, source)))
                return true;
            if (PrefabUtility.GetAddedComponents(instanceRoot).Any(added =>
                    IsInstanceObjectInScope(added.instanceComponent, source)))
                return true;
            if (PrefabUtility.GetRemovedGameObjects(instanceRoot).Any(removed =>
                    IsAssetObjectInScope(removed.assetGameObject, prefabSource)))
                return true;
            return PrefabUtility.GetRemovedComponents(instanceRoot).Any(removed =>
                IsAssetObjectInScope(removed.assetComponent, prefabSource));
        }

        private static bool IsInstanceObjectInScope(Object candidate, GameObject source)
        {
            GameObject candidateObject = candidate as GameObject;
            if (candidate is Component component) candidateObject = component.gameObject;
            return candidateObject != null &&
                   (candidateObject == source || candidateObject.transform.IsChildOf(source.transform));
        }

        private static bool IsAssetObjectInScope(Object candidate, GameObject prefabSource)
        {
            GameObject candidateObject = candidate as GameObject;
            if (candidate is Component component) candidateObject = component.gameObject;
            if (candidateObject == null) return false;

            GameObject original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(
                candidateObject);
            if (original != null) candidateObject = original;
            return candidateObject == prefabSource ||
                   candidateObject.transform.IsChildOf(prefabSource.transform);
        }

        public static VoxelLodBuildResult GenerateAutomatic(
            Object source, VoxelStyleProfile profile, VoxelLodBuildOptions options,
            Func<float, string, bool> cancelProgress = null)
        {
            ValidateProfileAndOptions(profile, options);
            string familyId = Guid.NewGuid().ToString("N");
            string safeName = MakeSafeFileName(source.name);
            EnsureAssetFolder(options.ExportFolder);
            string familyFolder = AssetDatabase.GenerateUniqueAssetPath(
                $"{NormalizeAssetPath(options.ExportFolder)}/{safeName}_VoxelLOD");
            EnsureAssetFolder(familyFolder);
            string manifestAssetPath = $"{familyFolder}/{safeName}.voxset.json";
            string profilePath = AssetDatabase.GetAssetPath(profile);
            var entries = new List<VoxelLodEntry>();
            var voxPaths = new List<string>();

            for (int lodIndex = 0; lodIndex < profile.LodCount; lodIndex++)
            {
                int multiplier = profile.GetLodMultiplier(lodIndex);
                float progressBase = (float)lodIndex / profile.LodCount;
                var settings = new VoxelizationSettings
                {
                    VoxelSize = profile.BaseVoxelSize * multiplier,
                    ChunkCellSize = profile.ChunkCellSize,
                    Padding = profile.Padding,
                    FillInterior = profile.FillInterior,
                    ColorMode = options.ColorMode,
                    SingleColor = options.SingleColor,
                    AlphaCutoff = options.AlphaCutoff
                };
                VoxelizationResult result = MeshVoxelizer.Voxelize(source, settings, (p, message) =>
                    cancelProgress != null && cancelProgress(
                        progressBase + p / profile.LodCount,
                        $"LOD {lodIndex}: {message}"));
                string voxPath = $"{familyFolder}/{safeName}_LOD{lodIndex}.vox";
                WriteGrid(voxPath, result.Grid, result.SourceBounds, source.name,
                    AssetDatabase.GetAssetPath(source), familyId, manifestAssetPath, lodIndex, multiplier,
                    VoxelLodGenerationMode.SourceMesh, null, profile);
                entries.Add(new VoxelLodEntry
                {
                    lodIndex = lodIndex,
                    multiplier = multiplier,
                    generationMode = VoxelLodGenerationMode.SourceMesh,
                    voxAssetPath = voxPath
                });
                voxPaths.Add(voxPath);
            }

            var manifest = new VoxelLodSetManifest
            {
                familyId = familyId,
                sourceName = source.name,
                sourceAssetPath = AssetDatabase.GetAssetPath(source),
                baseVoxelSize = profile.BaseVoxelSize,
                chunkCellSize = profile.ChunkCellSize,
                profileAssetPath = profilePath,
                lods = entries.ToArray()
            };
            WriteJsonAsset(manifestAssetPath, manifest);
            ImportGeneratedVox(voxPaths);
            string prefabPath = BuildPrefab(manifest, familyFolder);
            manifest.prefabAssetPath = prefabPath;
            WriteJsonAsset(manifestAssetPath, manifest);
            AssetDatabase.ImportAsset(manifestAssetPath, ImportAssetOptions.ForceSynchronousImport);
            return new VoxelLodBuildResult(manifestAssetPath, prefabPath, voxPaths.ToArray());
        }

        public static VoxelLodBuildResult GenerateManual(
            string parentVoxAssetPath, VoxelStyleProfile profile, int targetLodIndex,
            VoxelLodGenerationMode mode, VoxelLodBuildOptions options)
        {
            ValidateProfileAndOptions(profile, options);
            if (mode == VoxelLodGenerationMode.SourceMesh)
                throw new ArgumentException("El modo manual debe duplicar o reducir el LOD anterior.", nameof(mode));
            if (!VoxelImporterIntegration.TryLoadMetadata(parentVoxAssetPath,
                    out VoxelBridgeMetadata parent, out string error))
                throw new InvalidDataException(error);
            if (targetLodIndex <= parent.lodIndex || targetLodIndex >= profile.LodCount)
                throw new ArgumentOutOfRangeException(nameof(targetLodIndex),
                    "El LOD de destino debe ser posterior al LOD padre y existir en el perfil.");

            string manifestPath = parent.lodSetAssetPath;
            VoxelLodSetManifest manifest;
            if (!string.IsNullOrWhiteSpace(manifestPath) && TryReadJsonAsset(manifestPath, out manifest))
            {
                if (manifest.familyId != parent.familyId)
                    throw new InvalidDataException("El manifiesto no pertenece a la misma familia LOD.");
            }
            else
            {
                string folder = Path.GetDirectoryName(parentVoxAssetPath)?.Replace('\\', '/') ?? "Assets";
                manifestPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{folder}/{MakeSafeFileName(parent.sourceName)}.voxset.json");
                manifest = new VoxelLodSetManifest
                {
                    familyId = string.IsNullOrWhiteSpace(parent.familyId) ? Guid.NewGuid().ToString("N") : parent.familyId,
                    sourceName = parent.sourceName,
                    sourceAssetPath = parent.sourceAssetPath,
                    baseVoxelSize = parent.baseVoxelSize > 0f ? parent.baseVoxelSize : profile.BaseVoxelSize,
                    chunkCellSize = parent.chunkCellSize > 0 ? parent.chunkCellSize : profile.ChunkCellSize,
                    profileAssetPath = AssetDatabase.GetAssetPath(profile),
                    lods = new[]
                    {
                        new VoxelLodEntry
                        {
                            lodIndex = parent.lodIndex,
                            multiplier = Mathf.Max(1, parent.lodMultiplier),
                            generationMode = parent.lodGenerationMode,
                            voxAssetPath = parentVoxAssetPath
                        }
                    }
                };
            }

            string folderPath = Path.GetDirectoryName(parentVoxAssetPath)?.Replace('\\', '/') ?? "Assets";
            string targetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{folderPath}/{MakeSafeFileName(parent.sourceName)}_LOD{targetLodIndex}.vox");
            int targetMultiplier;
            if (mode == VoxelLodGenerationMode.DuplicateParent)
            {
                File.Copy(AssetPathToAbsolute(parentVoxAssetPath), AssetPathToAbsolute(targetPath));
                VoxelBridgeMetadata copy = JsonUtility.FromJson<VoxelBridgeMetadata>(JsonUtility.ToJson(parent));
                copy.familyId = manifest.familyId;
                copy.lodSetAssetPath = manifestPath;
                copy.lodIndex = targetLodIndex;
                copy.lodGenerationMode = mode;
                copy.parentVoxAssetPath = parentVoxAssetPath;
                WriteJsonAsset(VoxelImporterIntegration.GetMetadataAssetPath(targetPath), copy);
                targetMultiplier = Mathf.Max(1, parent.lodMultiplier);
            }
            else
            {
                targetMultiplier = profile.GetLodMultiplier(targetLodIndex);
                VoxelGrid parentGrid = VoxelVolumeReader.Read(AssetPathToAbsolute(parentVoxAssetPath), parent);
                VoxelGrid reduced = VoxelGridDownsampler.Downsample(parentGrid,
                    profile.BaseVoxelSize * targetMultiplier, profile.Padding, profile.ChunkCellSize);
                Bounds sourceBounds = BoundsFromMetadataOrGrid(parent, parentGrid);
                WriteGrid(targetPath, reduced, sourceBounds, parent.sourceName, parent.sourceAssetPath,
                    manifest.familyId, manifestPath, targetLodIndex, targetMultiplier, mode,
                    parentVoxAssetPath, profile);
            }

            var entries = new List<VoxelLodEntry>(manifest.lods ?? Array.Empty<VoxelLodEntry>());
            entries.RemoveAll(entry => entry.lodIndex == targetLodIndex);
            entries.Add(new VoxelLodEntry
            {
                lodIndex = targetLodIndex,
                multiplier = targetMultiplier,
                generationMode = mode,
                voxAssetPath = targetPath
            });
            entries.Sort((a, b) => a.lodIndex.CompareTo(b.lodIndex));
            manifest.lods = entries.ToArray();
            manifest.profileAssetPath = AssetDatabase.GetAssetPath(profile);
            WriteJsonAsset(manifestPath, manifest);
            ImportGeneratedVox(new[] { targetPath });
            string familyFolder = Path.GetDirectoryName(manifestPath)?.Replace('\\', '/') ?? "Assets";
            string prefabPath = BuildPrefab(manifest, familyFolder);
            manifest.prefabAssetPath = prefabPath;
            WriteJsonAsset(manifestPath, manifest);
            AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceSynchronousImport);
            return new VoxelLodBuildResult(manifestPath, prefabPath, new[] { targetPath });
        }

        public static string RebuildPrefab(string manifestAssetPath)
        {
            if (!TryReadJsonAsset(manifestAssetPath, out VoxelLodSetManifest manifest))
                throw new InvalidDataException("El manifiesto LOD no es válido.");
            foreach (VoxelLodEntry entry in manifest.lods ?? Array.Empty<VoxelLodEntry>())
            {
                if (!VoxelImporterIntegration.ApplyAndReimport(
                        entry.voxAssetPath, out string message, forceReimport: true))
                    Debug.LogWarning($"Voxel Bridge no pudo resincronizar '{entry.voxAssetPath}': {message}");
            }
            string familyFolder = Path.GetDirectoryName(manifestAssetPath)?.Replace('\\', '/') ?? "Assets";
            string prefabPath = BuildPrefab(manifest, familyFolder);
            manifest.prefabAssetPath = prefabPath;
            WriteJsonAsset(manifestAssetPath, manifest);
            AssetDatabase.ImportAsset(manifestAssetPath, ImportAssetOptions.ForceSynchronousImport);
            return prefabPath;
        }

        internal static bool TryFindManifestForAsset(
            string assetPath, out string manifestAssetPath, out VoxelLodSetManifest manifest)
        {
            manifestAssetPath = null;
            manifest = null;
            assetPath = NormalizeAssetPath(assetPath);
            if (string.IsNullOrEmpty(assetPath)) return false;

            if (assetPath.EndsWith(".voxset.json", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadJsonAsset(assetPath, out manifest)) return false;
                manifestAssetPath = assetPath;
                return true;
            }

            if (assetPath.EndsWith(".vox", StringComparison.OrdinalIgnoreCase) &&
                VoxelImporterIntegration.TryLoadMetadata(assetPath,
                    out VoxelBridgeMetadata metadata, out _) &&
                TryReadJsonAsset(metadata.lodSetAssetPath, out manifest))
            {
                manifestAssetPath = NormalizeAssetPath(metadata.lodSetAssetPath);
                return true;
            }

            string folder = NormalizeAssetPath(Path.GetDirectoryName(assetPath));
            if (!IsAssetFolder(folder) || !AssetDatabase.IsValidFolder(folder)) return false;
            string parentFolder = NormalizeAssetPath(Path.GetDirectoryName(folder));
            string[] searchFolders = IsAssetFolder(parentFolder) && AssetDatabase.IsValidFolder(parentFolder)
                ? new[] { folder, parentFolder }
                : new[] { folder };
            foreach (string guid in AssetDatabase.FindAssets("t:TextAsset", searchFolders))
            {
                string candidate = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(guid));
                if (!candidate.EndsWith(".voxset.json", StringComparison.OrdinalIgnoreCase) ||
                    !TryReadJsonAsset(candidate, out VoxelLodSetManifest candidateManifest))
                    continue;

                bool isPrefab = NormalizeAssetPath(candidateManifest.prefabAssetPath)
                    .Equals(assetPath, StringComparison.Ordinal);
                bool isLod = (candidateManifest.lods ?? Array.Empty<VoxelLodEntry>())
                    .Any(entry => NormalizeAssetPath(entry.voxAssetPath)
                        .Equals(assetPath, StringComparison.Ordinal));
                bool isImpostor = NormalizeAssetPath(candidateManifest.impostor?.assetPath)
                    .Equals(assetPath, StringComparison.Ordinal);
                if (!isPrefab && !isLod && !isImpostor) continue;
                manifestAssetPath = candidate;
                manifest = candidateManifest;
                return true;
            }

            return false;
        }

        private static void WriteGrid(string voxAssetPath, VoxelGrid grid, Bounds sourceBounds,
            string sourceName, string sourceAssetPath, string familyId, string manifestAssetPath,
            int lodIndex, int lodMultiplier, VoxelLodGenerationMode mode, string parentVoxAssetPath,
            VoxelStyleProfile profile)
        {
            QuantizedVoxels quantized = VoxelColorQuantizer.Quantize(grid);
            VoxWriteResult writeResult = VoxelChunkedVoxWriter.Write(
                AssetPathToAbsolute(voxAssetPath), grid, quantized, profile.ChunkCellSize);
            CalculateOccupiedBounds(grid, out Vector3Int occupiedMin, out Vector3Int occupiedSize);
            bool normalizedBySceneGraph = writeResult.UsesSceneGraph;
            var metadata = new VoxelBridgeMetadata
            {
                sourceName = sourceName,
                sourceAssetPath = sourceAssetPath,
                resolution = Mathf.Max(grid.Size.x, Mathf.Max(grid.Size.y, grid.Size.z)),
                padding = profile.Padding,
                fillInterior = profile.FillInterior,
                hideInternalCavities = profile.HideInternalCavities,
                voxelSize = grid.VoxelSize,
                gridOrigin = grid.Origin,
                unityGridSize = grid.Size,
                voxGridSize = new Vector3Int(grid.Size.x, grid.Size.z, grid.Size.y),
                voxelCount = grid.CountOccupied(),
                paletteColorCount = quantized.Palette.Length,
                familyId = familyId,
                lodSetAssetPath = manifestAssetPath,
                lodIndex = lodIndex,
                lodMultiplier = lodMultiplier,
                lodGenerationMode = mode,
                parentVoxAssetPath = parentVoxAssetPath,
                baseVoxelSize = profile.BaseVoxelSize,
                chunkCellSize = profile.ChunkCellSize,
                sourceBoundsMin = sourceBounds.min,
                sourceBoundsMax = sourceBounds.max,
                importGridOrigin = normalizedBySceneGraph
                    ? grid.Origin + (Vector3)occupiedMin * grid.VoxelSize
                    : grid.Origin,
                importGridSize = normalizedBySceneGraph ? occupiedSize : grid.Size,
                chunks = writeResult.Chunks
            };
            WriteJsonAsset(VoxelImporterIntegration.GetMetadataAssetPath(voxAssetPath), metadata);
        }

        private static void ImportGeneratedVox(IEnumerable<string> voxPaths)
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (string path in voxPaths)
            {
                if (!VoxelImporterIntegration.ApplyAndReimport(path, out string message, forceReimport: true))
                    Debug.LogWarning($"Voxel Bridge no pudo configurar '{path}': {message}");
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private static string BuildPrefab(VoxelLodSetManifest manifest, string familyFolder)
        {
            EnsureAssetFolder(familyFolder);
            VoxelLodEntry[] entries = (manifest.lods ?? Array.Empty<VoxelLodEntry>())
                .Where(entry => !string.IsNullOrWhiteSpace(entry.voxAssetPath))
                .OrderBy(entry => entry.lodIndex)
                .ToArray();
            if (entries.Length == 0) throw new InvalidDataException("El manifiesto no contiene niveles LOD.");

            VoxelStyleProfile profile = string.IsNullOrEmpty(manifest.profileAssetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<VoxelStyleProfile>(manifest.profileAssetPath);
            string modelName = MakeSafeFileName(manifest.sourceName);
            var root = new GameObject(modelName);
            try
            {
                var lods = new LOD[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    VoxelLodEntry entry = entries[i];
                    GameObject imported = AssetDatabase.LoadAssetAtPath<GameObject>(entry.voxAssetPath);
                    if (imported == null)
                        throw new InvalidDataException($"'{entry.voxAssetPath}' no produjo un GameObject importado.");
                    GameObject child = Object.Instantiate(imported);
                    child.name = $"LOD{entry.lodIndex}_x{entry.multiplier}";
                    child.transform.SetParent(root.transform, false);
                    Renderer[] renderers = child.GetComponentsInChildren<Renderer>(true);
                    if (renderers.Length == 0)
                        throw new InvalidDataException($"El LOD {entry.lodIndex} no contiene renderers.");
                    float height = profile != null
                        ? profile.GetLodScreenHeight(entry.lodIndex)
                        : Mathf.Max(0.01f, 0.6f * Mathf.Pow(0.5f, entry.lodIndex));
                    lods[i] = new LOD(height, renderers);
                }
                var group = root.AddComponent<LODGroup>();
                group.fadeMode = LODFadeMode.None;
                group.SetLODs(lods);
                group.RecalculateBounds();

                if (manifest.impostor != null &&
                    !string.IsNullOrWhiteSpace(manifest.impostor.assetPath) &&
                    !AmplifyImpostorIntegration.TryAppendExistingImpostor(
                        root, group, manifest.impostor, out string impostorError))
                    Debug.LogWarning($"Voxel Bridge omitió el impostor de '{modelName}': {impostorError}");

                string path = ResolvePrefabAssetPath(manifest, familyFolder, modelName);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                return path;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static Bounds BoundsFromMetadataOrGrid(VoxelBridgeMetadata metadata, VoxelGrid grid)
        {
            if ((metadata.sourceBoundsMax - metadata.sourceBoundsMin).sqrMagnitude > 1e-12f)
                return new Bounds((metadata.sourceBoundsMin + metadata.sourceBoundsMax) * 0.5f,
                    metadata.sourceBoundsMax - metadata.sourceBoundsMin);
            return new Bounds(grid.Origin + (Vector3)grid.Size * grid.VoxelSize * 0.5f,
                (Vector3)grid.Size * grid.VoxelSize);
        }

        private static void CalculateOccupiedBounds(VoxelGrid grid, out Vector3Int min, out Vector3Int size)
        {
            min = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
            Vector3Int max = new(int.MinValue, int.MinValue, int.MinValue);
            for (int i = 0; i < grid.Occupied.Length; i++)
            {
                if (!grid.Occupied[i]) continue;
                grid.Coordinates(i, out int x, out int y, out int z);
                var position = new Vector3Int(x, y, z);
                min = Vector3Int.Min(min, position);
                max = Vector3Int.Max(max, position);
            }
            if (min.x == int.MaxValue) throw new InvalidOperationException("La rejilla no contiene vóxeles.");
            size = max - min + Vector3Int.one;
        }

        private static void ValidateProfileAndOptions(VoxelStyleProfile profile, VoxelLodBuildOptions options)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (!profile.TryValidate(out string error)) throw new InvalidOperationException(error);
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!IsAssetFolder(options.ExportFolder))
                throw new ArgumentException("La carpeta de salida debe estar dentro de Assets.");
        }

        private static bool TryReadJsonAsset<T>(string assetPath, out T value) where T : class
        {
            value = null;
            if (string.IsNullOrWhiteSpace(assetPath)) return false;
            string absolute = AssetPathToAbsolute(assetPath);
            if (!File.Exists(absolute)) return false;
            value = JsonUtility.FromJson<T>(File.ReadAllText(absolute));
            return value != null;
        }

        internal static bool TryReadManifest(string assetPath, out VoxelLodSetManifest manifest) =>
            TryReadJsonAsset(assetPath, out manifest);

        internal static void SaveManifest(string assetPath, VoxelLodSetManifest manifest)
        {
            WriteJsonAsset(assetPath, manifest);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void WriteJsonAsset<T>(string assetPath, T value)
        {
            string absolute = AssetPathToAbsolute(assetPath);
            string directory = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(absolute, JsonUtility.ToJson(value, true));
        }

        internal static void EnsureAssetFolder(string path)
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

        internal static string AssetPathToAbsolute(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                 ?? throw new InvalidOperationException("No se encontró la raíz del proyecto.");
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        internal static bool IsAssetFolder(string path)
        {
            path = NormalizeAssetPath(path);
            return path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal);
        }

        internal static string NormalizeAssetPath(string path) =>
            (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');

        private static bool IsReusablePrefabPath(string path)
        {
            path = NormalizeAssetPath(path);
            return path.StartsWith("Assets/", StringComparison.Ordinal) &&
                   path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolvePrefabAssetPath(
            VoxelLodSetManifest manifest, string familyFolder, string prefabName)
        {
            familyFolder = NormalizeAssetPath(familyFolder);
            prefabName = MakeSafeFileName(prefabName);
            string desiredPath = $"{familyFolder}/{prefabName}.prefab";
            if (IsReusablePrefabPath(manifest.prefabAssetPath) &&
                AssetDatabase.LoadMainAssetAtPath(manifest.prefabAssetPath) != null)
            {
                string existingPath = NormalizeAssetPath(manifest.prefabAssetPath);
                if (existingPath.Equals(desiredPath, StringComparison.Ordinal)) return existingPath;

                if (AssetDatabase.LoadMainAssetAtPath(desiredPath) != null)
                    desiredPath = AssetDatabase.GenerateUniqueAssetPath(desiredPath);
                string moveError = AssetDatabase.MoveAsset(existingPath, desiredPath);
                if (!string.IsNullOrEmpty(moveError))
                    throw new IOException(
                        $"No se pudo mover el prefab a la carpeta de su familia: {moveError}");
                return desiredPath;
            }

            return AssetDatabase.GenerateUniqueAssetPath(desiredPath);
        }

        internal static string MakeSafeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(value) ? "VoxelModel" : value;
        }
    }
}
