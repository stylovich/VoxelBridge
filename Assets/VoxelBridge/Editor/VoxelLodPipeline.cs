using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelLodBuildOptions
    {
        public VoxelColorMode ColorMode;
        public Color32 SingleColor;
        public float AlphaCutoff;
        public string ExportFolder;
        public bool IncludeInactiveObjects = true;
        public bool GenerateLod0Only;
        public VoxelConversionProfile ConversionProfile;
        public bool NormalizeScale;
        public Object ScaleSource;

        internal Vector3 ResolveScale(Object source) => NormalizeScale
            ? VoxelScaleNormalization.SourceScale(ScaleSource != null ? ScaleSource : source) : Vector3.one;

        internal VoxelLodBuildOptions ForScaleSource(Object source)
        {
            var copy = (VoxelLodBuildOptions)MemberwiseClone();
            copy.ScaleSource = source;
            return copy;
        }
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
        public const int DefaultMaximumImportedVoxelCount = 4_000_000;

        public bool ReusePrefabSources = true;
        public bool NormalizeScale;
        public VoxelPrefabOverrideHandling ModifiedPrefabHandling =
            VoxelPrefabOverrideHandling.UsePrefabSource;
        public long MaximumEstimatedMemoryBytes = 1024L * 1024L * 1024L;
        public bool SkipSourcesOverMemoryBudget = true;
        public bool AdaptInitialVoxelSize = true;
        public int MaximumInitialLodIndex = 2;
        public int MaximumImportedVoxelCount = DefaultMaximumImportedVoxelCount;
        public bool IgnoreInactiveObjects = true;
        public bool EnableCheckpoint = true;
        public bool ResumeInterruptedBatch = true;
        public int CleanupInterval = 1;
        public VoxelLodBatchPreflight Preflight;
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
        public readonly VoxelLodBuildResult BuildResult;
        public readonly string Error;
        public readonly bool Reused;
        public readonly bool Resumed;
        public readonly bool Ignored;

        public bool Succeeded => !Ignored && string.IsNullOrEmpty(Error);
        public bool Failed => !Ignored && !string.IsNullOrEmpty(Error);

        public VoxelLodBatchItemResult(
            VoxelLodBatchSourcePlan plan, VoxelLodBuildResult buildResult,
            bool reused = false, string error = null, bool resumed = false)
        {
            Source = plan.Source;
            BuildResult = buildResult;
            Error = error;
            Reused = reused;
            Resumed = resumed;
            Ignored = plan.Ignored;
        }
    }

    internal sealed class VoxelLodBatchBuildResult
    {
        public readonly VoxelLodBatchItemResult[] Items;
        public readonly int CandidateCount;
        public readonly int ExcludedInactiveDirectChildCount;
        public readonly bool Cancelled;
        public readonly VoxelLodBatchPreflight Preflight;

        public int SucceededCount => Items.Count(item => item.Succeeded);
        public int FailedCount => Items.Count(item => item.Failed);
        public int IgnoredCount => Items.Count(item => item.Ignored);
        public int ReusedCount => Items.Count(item => item.Succeeded && item.Reused);
        public int ResumedCount => Items.Count(item => item.Succeeded && item.Resumed);
        public int CreatedFamilyCount => Items.Count(item =>
            item.Succeeded && !item.Reused && !item.Resumed);
        public bool IsComplete => !Cancelled && Items.Length == CandidateCount &&
                                  FailedCount == 0 && IgnoredCount == 0;

        public VoxelLodBatchBuildResult(
            IEnumerable<VoxelLodBatchItemResult> items, int candidateCount, bool cancelled,
            VoxelLodBatchPreflight preflight = null, int excludedInactiveDirectChildCount = 0)
        {
            Items = items.ToArray();
            CandidateCount = candidateCount;
            ExcludedInactiveDirectChildCount = Mathf.Max(0, excludedInactiveDirectChildCount);
            Cancelled = cancelled;
            Preflight = preflight;
        }
    }

    internal sealed class VoxelLodBatchConversionOutcome
    {
        public readonly VoxelLodBuildResult BuildResult;
        public readonly string Error;
        public readonly bool FromCheckpoint;
        public bool CheckpointClaimed;

        public VoxelLodBatchConversionOutcome(
            VoxelLodBuildResult buildResult, string error, bool fromCheckpoint = false)
        {
            BuildResult = buildResult;
            Error = error;
            FromCheckpoint = fromCheckpoint;
        }
    }

    internal static class VoxelLodPipeline
    {
        internal static GameObject[] GetAutomaticBatchSources(
            GameObject parent, VoxelLodBatchOptions batchOptions = null)
        {
            if (parent == null) return Array.Empty<GameObject>();
            batchOptions ??= new VoxelLodBatchOptions();

            var sources = new List<GameObject>();
            for (int childIndex = 0; childIndex < parent.transform.childCount; childIndex++)
            {
                GameObject child = parent.transform.GetChild(childIndex).gameObject;
                if (batchOptions.IgnoreInactiveObjects && !child.activeSelf) continue;
                bool hasMesh = child.GetComponentsInChildren<MeshFilter>(true)
                    .Any(filter => filter.sharedMesh != null &&
                                   (!batchOptions.IgnoreInactiveObjects ||
                                    MeshVoxelizer.IsActiveWithinRoot(
                                        child.transform, filter.transform)));
                bool hasSkinnedMesh = child.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Any(renderer => renderer.sharedMesh != null &&
                                     (!batchOptions.IgnoreInactiveObjects ||
                                      MeshVoxelizer.IsActiveWithinRoot(
                                          child.transform, renderer.transform)));
                if (hasMesh || hasSkinnedMesh) sources.Add(child);
            }
            return sources.ToArray();
        }

        internal static VoxelLodBatchSourcePlan[] GetAutomaticBatchPlans(
            GameObject parent, VoxelLodBatchOptions batchOptions = null)
        {
            batchOptions ??= new VoxelLodBatchOptions();
            return GetAutomaticBatchSources(parent, batchOptions)
                .Select(source => CreateAutomaticBatchPlan(source, batchOptions))
                .ToArray();
        }

        public static VoxelLodBatchBuildResult GenerateAutomaticBatch(
            GameObject parent, VoxelStyleProfile profile, VoxelLodBuildOptions options,
            Func<float, string, bool> cancelProgress = null,
            VoxelLodBatchOptions batchOptions = null)
            => VoxelConversionTiming.Run("Batch: " + (parent != null ? parent.name : "Missing parent"),
                timing => GenerateAutomaticBatchCore(parent, profile, options, cancelProgress, batchOptions, timing));

        private static VoxelLodBatchBuildResult GenerateAutomaticBatchCore(
            GameObject parent, VoxelStyleProfile profile, VoxelLodBuildOptions options,
            Func<float, string, bool> cancelProgress, VoxelLodBatchOptions batchOptions, VoxelConversionTiming timing)
        {
            ValidateProfileAndOptions(profile, options);
            if (parent == null) throw new ArgumentNullException(nameof(parent));

            batchOptions ??= new VoxelLodBatchOptions();
            VoxelLodBuildOptions effectiveOptions = CreateBatchBuildOptions(options, batchOptions);
            batchOptions.NormalizeScale = effectiveOptions.NormalizeScale;
            VoxelLodBatchSourcePlan[] plans = GetAutomaticBatchPlans(parent, batchOptions);
            if (plans.Length == 0)
                throw new InvalidOperationException(
                    "The parent object has no direct children containing meshes to voxelize.");

            int excludedInactiveDirectChildren = batchOptions.IgnoreInactiveObjects
                ? CountInactiveDirectChildren(parent)
                : 0;
            VoxelLodBatchPreflight preflight = batchOptions.Preflight;
            string expectedSignature = VoxelLodBatchAnalyzer.CreateSignature(
                plans, profile, effectiveOptions, batchOptions);
            if (preflight == null || preflight.Signature != expectedSignature)
                using (timing.Measure("Preflight"))
                    preflight = VoxelLodBatchAnalyzer.Analyze(
                        plans, profile, effectiveOptions, batchOptions, cancelProgress);

            int cleanedFamilies = VoxelLodBatchRecovery.CleanupIncompleteFamilies(
                effectiveOptions.ExportFolder);
            if (cleanedFamilies > 0)
                Debug.LogWarning(
                    $"Voxel Bridge removed {cleanedFamilies} incomplete families marked " +
                    "by an interrupted run.");

            var items = new List<VoxelLodBatchItemResult>();
            string checkpointSignature = VoxelLodBatchIdentity.CreateBatchSignature(
                plans, profile, effectiveOptions, batchOptions);
            if (batchOptions.EnableCheckpoint && !batchOptions.ResumeInterruptedBatch)
                VoxelLodBatchCheckpointStore.Reset(checkpointSignature);
            Dictionary<Object, VoxelLodBatchConversionOutcome> outcomes =
                batchOptions.EnableCheckpoint && batchOptions.ResumeInterruptedBatch
                    ? VoxelLodBatchCheckpointStore.LoadOutcomes(checkpointSignature, plans)
                    : new Dictionary<Object, VoxelLodBatchConversionOutcome>();
            int processedUniqueSources = 0;
            for (int sourceIndex = 0; sourceIndex < plans.Length; sourceIndex++)
            {
                VoxelLodBatchSourcePlan plan = plans[sourceIndex];
                GameObject current = plan.Source;
                float progressBase = (float)sourceIndex / plans.Length;
                if (cancelProgress != null && cancelProgress(progressBase,
                        $"Model {sourceIndex + 1} of {plans.Length}: preparing {current.name}"))
                {
                    using (timing.Measure("Cleanup")) VoxelLodBatchMemoryCleaner.ReleaseUnusedMemory(parent, profile, effectiveOptions.ConversionProfile);
                    return new VoxelLodBatchBuildResult(
                        items, plans.Length, true, preflight, excludedInactiveDirectChildren);
                }

                if (plan.Ignored)
                {
                    items.Add(new VoxelLodBatchItemResult(plan, default));
                    continue;
                }

                if (outcomes.TryGetValue(plan.ReuseKey, out VoxelLodBatchConversionOutcome previous))
                {
                    bool resumed = previous.FromCheckpoint && !previous.CheckpointClaimed;
                    if (resumed) previous.CheckpointClaimed = true;
                    items.Add(new VoxelLodBatchItemResult(
                        plan, previous.BuildResult, !resumed, previous.Error, resumed));
                    continue;
                }

                VoxelLodBatchSourceEstimate estimate = preflight.Find(plan.ReuseKey);
                string preflightError = estimate == null
                    ? "No preflight analysis was found for this source."
                    : estimate.Error;
                if (string.IsNullOrEmpty(preflightError) &&
                    (batchOptions.AdaptInitialVoxelSize ||
                     batchOptions.SkipSourcesOverMemoryBudget) && estimate.IsOverBudget)
                    preflightError =
                        $"Estimated memory {VoxelLodBatchAnalyzer.FormatBytes(estimate.EstimatedPeakBytes)} " +
                        $"exceeds the budget of {VoxelLodBatchAnalyzer.FormatBytes(estimate.MemoryBudgetBytes)}.";
                if (!string.IsNullOrEmpty(preflightError))
                {
                    var rejected = new VoxelLodBatchConversionOutcome(default, preflightError);
                    outcomes.Add(plan.ReuseKey, rejected);
                    items.Add(new VoxelLodBatchItemResult(plan, default, false, preflightError));
                    using (timing.Measure("Cleanup")) CleanupBatchMemoryIfNeeded(
                        ++processedUniqueSources, batchOptions.CleanupInterval, parent, profile, effectiveOptions.ConversionProfile);
                    continue;
                }

                try
                {
                    VoxelLodBuildResult build = GenerateAutomaticCore(
                        plan.ConversionSource, profile, effectiveOptions.ForScaleSource(plan.Source), (progress, message) =>
                            cancelProgress != null && cancelProgress(
                                progressBase + progress / plans.Length,
                                $"Model {sourceIndex + 1} of {plans.Length} · {current.name}: {message}"),
                        estimate.InitialVoxelMultiplier,
                        batchOptions.MaximumImportedVoxelCount,
                        batchOptions.AdaptInitialVoxelSize
                            ? profile.GetLodMultiplier(Mathf.Clamp(
                                batchOptions.MaximumInitialLodIndex, 0, profile.LodCount - 1))
                            : estimate.InitialVoxelMultiplier, timing);
                    var outcome = new VoxelLodBatchConversionOutcome(build, null);
                    outcomes.Add(plan.ReuseKey, outcome);
                    items.Add(new VoxelLodBatchItemResult(plan, build));
                    if (batchOptions.EnableCheckpoint)
                        VoxelLodBatchCheckpointStore.Record(
                            checkpointSignature, plan, build);
                    using (timing.Measure("Cleanup")) CleanupBatchMemoryIfNeeded(
                        ++processedUniqueSources, batchOptions.CleanupInterval, parent, profile, effectiveOptions.ConversionProfile);
                }
                catch (OperationCanceledException)
                {
                    using (timing.Measure("Cleanup")) VoxelLodBatchMemoryCleaner.ReleaseUnusedMemory(parent, profile, effectiveOptions.ConversionProfile);
                    return new VoxelLodBatchBuildResult(
                        items, plans.Length, true, preflight, excludedInactiveDirectChildren);
                }
                catch (Exception exception)
                {
                    var outcome = new VoxelLodBatchConversionOutcome(default, exception.Message);
                    outcomes.Add(plan.ReuseKey, outcome);
                    items.Add(new VoxelLodBatchItemResult(plan, default, false, exception.Message));
                    using (timing.Measure("Cleanup")) CleanupBatchMemoryIfNeeded(
                        ++processedUniqueSources, batchOptions.CleanupInterval, parent, profile, effectiveOptions.ConversionProfile);
                }
            }

            if (batchOptions.EnableCheckpoint)
                VoxelLodBatchCheckpointStore.Reset(checkpointSignature);
            using (timing.Measure("Cleanup")) VoxelLodBatchMemoryCleaner.ReleaseUnusedMemory(parent, profile, effectiveOptions.ConversionProfile);
            return new VoxelLodBatchBuildResult(
                items, plans.Length, false, preflight, excludedInactiveDirectChildren);
        }

        private static int CountInactiveDirectChildren(GameObject parent)
        {
            if (parent == null) return 0;
            int count = 0;
            for (int index = 0; index < parent.transform.childCount; index++)
                if (!parent.transform.GetChild(index).gameObject.activeSelf)
                    count++;
            return count;
        }

        private static VoxelLodBuildOptions CreateBatchBuildOptions(
            VoxelLodBuildOptions source, VoxelLodBatchOptions batchOptions)
        {
            return new VoxelLodBuildOptions
            {
                ColorMode = source.ColorMode,
                SingleColor = source.SingleColor,
                AlphaCutoff = source.AlphaCutoff,
                ExportFolder = source.ExportFolder,
                GenerateLod0Only = source.GenerateLod0Only,
                ConversionProfile = source.ConversionProfile,
                NormalizeScale = source.NormalizeScale,
                IncludeInactiveObjects = !batchOptions.IgnoreInactiveObjects
            };
        }

        private static void CleanupBatchMemoryIfNeeded(
            int processedCount, int interval, params Object[] keepAlive)
        {
            interval = Mathf.Clamp(interval, 1, 50);
            if (processedCount % interval == 0)
                VoxelLodBatchMemoryCleaner.ReleaseUnusedMemory(keepAlive);
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
            // A scaled instance cannot reuse a unit-scale source family after baking world scale.
            // Keep unsupported transforms in their own plan so preflight can report the error.
            if (batchOptions.NormalizeScale && reuseKey == prefabSource)
            {
                try
                {
                    if (VoxelScaleNormalization.SourceScale(source) != VoxelScaleNormalization.SourceScale(prefabSource))
                        reuseKey = source;
                }
                catch (InvalidOperationException) { reuseKey = source; }
            }
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

            string nearestAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source);
            if (!string.IsNullOrEmpty(nearestAssetPath))
                prefabSource = PrefabUtility.GetCorrespondingObjectFromSourceAtPath(
                    source, nearestAssetPath);
            if (prefabSource == null) prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(source);
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

            string scopeAssetPath = AssetDatabase.GetAssetPath(prefabSource);
            if (!string.IsNullOrEmpty(scopeAssetPath))
            {
                GameObject scoped = PrefabUtility.GetCorrespondingObjectFromSourceAtPath(
                    candidateObject, scopeAssetPath);
                if (scoped != null) candidateObject = scoped;
            }
            return candidateObject == prefabSource ||
                   candidateObject.transform.IsChildOf(prefabSource.transform);
        }

        public static VoxelLodBuildResult GenerateAutomatic(
            Object source, VoxelStyleProfile profile, VoxelLodBuildOptions options,
            Func<float, string, bool> cancelProgress = null, int initialVoxelMultiplier = 1,
            int maximumImportedVoxelCount = 0, int maximumInitialVoxelMultiplier = 0)
            => VoxelConversionTiming.Run(source != null ? source.name : "Missing source",
                timing => GenerateAutomaticCore(source, profile, options, cancelProgress, initialVoxelMultiplier,
                    maximumImportedVoxelCount, maximumInitialVoxelMultiplier, timing));

        private static VoxelLodBuildResult GenerateAutomaticCore(
            Object source, VoxelStyleProfile profile, VoxelLodBuildOptions options,
            Func<float, string, bool> cancelProgress, int initialVoxelMultiplier,
            int maximumImportedVoxelCount, int maximumInitialVoxelMultiplier, VoxelConversionTiming timing)
        {
            ValidateProfileAndOptions(profile, options);
            if (initialVoxelMultiplier < 1 ||
                (initialVoxelMultiplier & (initialVoxelMultiplier - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(initialVoxelMultiplier),
                    "The initial voxel multiplier must be a power of two greater than or equal to one.");
            if (maximumImportedVoxelCount < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumImportedVoxelCount),
                    "The imported voxel limit cannot be negative.");
            if (maximumInitialVoxelMultiplier == 0)
                maximumInitialVoxelMultiplier = initialVoxelMultiplier;
            options.ConversionProfile?.ValidateForExport();
            Vector3 bakedRootScale = options.ResolveScale(source);
            if (options.ConversionProfile != null)
                VoxelProductionExporter.ValidatePalettes(options.ConversionProfile.colorMapping.ColorPalette,
                    options.ConversionProfile.surfacePalette);
            if (!IsPowerOfTwo(maximumInitialVoxelMultiplier) ||
                maximumInitialVoxelMultiplier < initialVoxelMultiplier)
                throw new ArgumentOutOfRangeException(nameof(maximumInitialVoxelMultiplier),
                    "The maximum voxel multiplier must be a power of two no smaller than the initial multiplier.");

            VoxelizationResult validatedLod0 = VoxelizeAutomaticLod(
                source, profile, options, 0, initialVoxelMultiplier, cancelProgress, timing);
            while (maximumImportedVoxelCount > 0 &&
                   validatedLod0.OccupiedVoxelCount > maximumImportedVoxelCount)
            {
                int rejectedVoxelCount = validatedLod0.OccupiedVoxelCount;
                if (!TryGetNextInitialVoxelMultiplier(
                        profile, initialVoxelMultiplier, maximumInitialVoxelMultiplier,
                        out int nextMultiplier))
                {
                    validatedLod0 = null;
                    using (timing.Measure("Cleanup")) CollectRejectedVoxelGrid();
                    throw new InvalidOperationException(
                        $"LOD0 contains {rejectedVoxelCount:N0} voxels, exceeding the import limit " +
                        $"of {maximumImportedVoxelCount:N0}. No further multiplier " +
                        $"is allowed beyond ×{initialVoxelMultiplier}.");
                }

                if (cancelProgress != null && cancelProgress(0f,
                        $"LOD0 contains {rejectedVoxelCount:N0} voxels; " +
                        $"retrying with multiplier ×{nextMultiplier}"))
                    throw new OperationCanceledException("Voxelization cancelled.");

                validatedLod0 = null;
                using (timing.Measure("Cleanup")) CollectRejectedVoxelGrid();
                initialVoxelMultiplier = nextMultiplier;
                validatedLod0 = VoxelizeAutomaticLod(
                    source, profile, options, 0, initialVoxelMultiplier, cancelProgress, timing);
            }

            string familyId = Guid.NewGuid().ToString("N");
            string safeName = MakeSafeFileName(source.name);
            EnsureAssetFolder(options.ExportFolder);
            string familyFolder = AssetDatabase.GenerateUniqueAssetPath(
                $"{NormalizeAssetPath(options.ExportFolder)}/{safeName}_VoxelLOD");
            EnsureAssetFolder(familyFolder);
            VoxelLodBatchRecovery.MarkFamilyIncomplete(familyFolder, source.name);
            try
            {
                string manifestAssetPath = $"{familyFolder}/{safeName}.voxset.json";
                string profilePath = AssetDatabase.GetAssetPath(profile);
                string retainedGuid;
                using (timing.Measure("Asset and prefab I/O"))
                    retainedGuid = MeshVoxelizer.ExportRetainedGeometry(source, options.IncludeInactiveObjects,
                        options.ConversionProfile, familyFolder, bakedRootScale);
                var entries = new List<VoxelLodEntry>();
                var voxPaths = new List<string>();

                for (int lodIndex = 0; lodIndex < (options.GenerateLod0Only ? 1 : profile.LodCount); lodIndex++)
                {
                    int multiplier = checked(
                        initialVoxelMultiplier * profile.GetLodMultiplier(lodIndex));
                    VoxelizationResult result;
                    if (lodIndex == 0)
                    {
                        result = validatedLod0;
                        validatedLod0 = null;
                    }
                    else
                    {
                        result = VoxelizeAutomaticLod(
                            source, profile, options, lodIndex,
                            initialVoxelMultiplier, cancelProgress, timing);
                    }
                    string voxPath = $"{familyFolder}/{safeName}_LOD{lodIndex}.vox";
                    using (timing.Measure("VOX and metadata write"))
                    WriteGrid(voxPath, result.Grid, result.SourceBounds, source.name,
                        AssetDatabase.GetAssetPath(source), familyId, manifestAssetPath, lodIndex, multiplier,
                        VoxelLodGenerationMode.SourceMesh, null, profile, conversionProfile: options.ConversionProfile,
                        retainedGeometryGuid: retainedGuid);
                    if (result.SurfaceReport != null)
                    {
                        string reportPath = Path.ChangeExtension(voxPath, ".surface-report.json");
                        using (timing.Measure("VOX and metadata write")) WriteJsonAsset(reportPath, result.SurfaceReport);
                        using (timing.Measure("Import")) AssetDatabase.ImportAsset(reportPath, ImportAssetOptions.ForceSynchronousImport);
                        Debug.Log($"Voxel Bridge PBR mapping for '{source.name}' LOD{lodIndex}: {result.SurfaceReport.Summary}. Review {reportPath}");
                    }
                    if (options.ConversionProfile != null && result.MaximumColorDistance > options.ConversionProfile.colorMapping.WarningDistance)
                        Debug.LogWarning($"Voxel Bridge color mapping for '{source.name}' LOD{lodIndex} reached OKLab distance {result.MaximumColorDistance:0.00}. Review the mapped colors.");
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
                    normalizedScale = options.NormalizeScale,
                    bakedRootScale = bakedRootScale,
                    sourceAxes = options.ConversionProfile?.sourceAxes ?? VoxelSourceAxes.PreserveLocalAxes,
                    familyId = familyId,
                    sourceName = source.name,
                    sourceAssetPath = AssetDatabase.GetAssetPath(source),
                    retainedGeometryGuid = retainedGuid,
                    baseVoxelSize = profile.BaseVoxelSize,
                    initialVoxelMultiplier = initialVoxelMultiplier,
                    chunkCellSize = profile.ChunkCellSize,
                    profileAssetPath = profilePath,
                    lods = entries.ToArray()
                };
                using (timing.Measure("VOX and metadata write")) WriteJsonAsset(manifestAssetPath, manifest);
                using (timing.Measure("Import")) ImportGeneratedVox(voxPaths);
                string prefabPath;
                if (options.ConversionProfile != null)
                    prefabPath = VoxelProductionFamily.BuildConvertedFamily(manifestAssetPath, profile, progress =>
                    {
                        if (cancelProgress != null && cancelProgress(progress, "Building production meshes"))
                            throw new OperationCanceledException("Production meshing cancelled.");
                    }, timing);
                else
                {
                    using (timing.Measure("Asset and prefab I/O")) prefabPath = BuildPrefab(manifest, familyFolder);
                    manifest.prefabAssetPath = prefabPath;
                    WriteJsonAsset(manifestAssetPath, manifest);
                }
                AssetDatabase.ImportAsset(manifestAssetPath, ImportAssetOptions.ForceSynchronousImport);
                VoxelLodBatchRecovery.CompleteFamily(familyFolder);
                return new VoxelLodBuildResult(manifestAssetPath, prefabPath, voxPaths.ToArray());
            }
            catch
            {
                VoxelLodBatchRecovery.DeleteFamilyIfIncomplete(familyFolder);
                throw;
            }
        }

        private static VoxelizationResult VoxelizeAutomaticLod(
            Object source, VoxelStyleProfile profile, VoxelLodBuildOptions options,
            int lodIndex, int initialVoxelMultiplier,
            Func<float, string, bool> cancelProgress, VoxelConversionTiming timing)
        {
            int multiplier = checked(
                initialVoxelMultiplier * profile.GetLodMultiplier(lodIndex));
            int count = options.GenerateLod0Only ? 1 : profile.LodCount;
            float progressBase = (float)lodIndex / count;
            var settings = new VoxelizationSettings
            {
                VoxelSize = profile.BaseVoxelSize * multiplier,
                ChunkCellSize = profile.ChunkCellSize,
                Padding = profile.Padding,
                FillInterior = profile.FillInterior,
                IncludeInactiveObjects = options.IncludeInactiveObjects,
                ColorMode = options.ColorMode,
                SingleColor = options.SingleColor,
                AlphaCutoff = options.AlphaCutoff,
                ConversionProfile = options.ConversionProfile,
                RootScale = options.ResolveScale(source),
                Timing = timing
            };
            return MeshVoxelizer.Voxelize(source, settings, (progress, message) =>
                cancelProgress != null && cancelProgress(
                    progressBase + progress / count,
                    $"LOD {lodIndex}: {message}"));
        }

        private static bool TryGetNextInitialVoxelMultiplier(
            VoxelStyleProfile profile, int currentMultiplier, int maximumMultiplier,
            out int nextMultiplier)
        {
            for (int lodIndex = 0; lodIndex < profile.LodCount; lodIndex++)
            {
                int candidate = profile.GetLodMultiplier(lodIndex);
                if (candidate <= currentMultiplier || candidate > maximumMultiplier) continue;
                nextMultiplier = candidate;
                return true;
            }
            nextMultiplier = currentMultiplier;
            return false;
        }

        private static void CollectRejectedVoxelGrid()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        public static VoxelLodBuildResult GenerateManual(
            string parentVoxAssetPath, VoxelStyleProfile profile, int targetLodIndex,
            VoxelLodGenerationMode mode, VoxelLodBuildOptions options)
        {
            ValidateProfileAndOptions(profile, options);
            if (mode == VoxelLodGenerationMode.SourceMesh)
                throw new ArgumentException("Manual mode must duplicate or reduce the previous LOD.", nameof(mode));
            if (!VoxelImporterIntegration.TryLoadMetadata(parentVoxAssetPath,
                    out VoxelBridgeMetadata parent, out string error))
                throw new InvalidDataException(error);
            if (targetLodIndex <= parent.lodIndex || targetLodIndex >= profile.LodCount)
                throw new ArgumentOutOfRangeException(nameof(targetLodIndex),
                    "The target LOD must follow the parent LOD and exist in the profile.");

            string manifestPath = parent.lodSetAssetPath;
            VoxelLodSetManifest manifest;
            if (!string.IsNullOrWhiteSpace(manifestPath))
            {
                if (!TryReadManifest(manifestPath, out manifest))
                    throw new InvalidDataException("The LOD manifest is missing or unsupported. Regenerate the family before creating a manual LOD.");
                if (manifest.familyId != parent.familyId)
                    throw new InvalidDataException("The manifest does not belong to the same LOD family.");
                if (manifest.productionMeshes)
                {
                    VoxelProductionFamily.DeriveLevel(manifestPath, targetLodIndex, mode);
                    var production = VoxelProductionFamily.Load(manifestPath);
                    return new VoxelLodBuildResult(manifestPath, production.prefabAssetPath,
                        new[] { VoxelProductionFamily.SourcePath(production.lods[targetLodIndex]) });
                }
            }
            else
            {
                string folder = Path.GetDirectoryName(parentVoxAssetPath)?.Replace('\\', '/') ?? "Assets";
                manifestPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{folder}/{MakeSafeFileName(parent.sourceName)}.voxset.json");
                manifest = new VoxelLodSetManifest
                {
                    sourceAxes = parent.sourceAxes,
                    familyId = string.IsNullOrWhiteSpace(parent.familyId) ? Guid.NewGuid().ToString("N") : parent.familyId,
                    sourceName = parent.sourceName,
                    sourceAssetPath = parent.sourceAssetPath,
                    retainedGeometryGuid = parent.retainedGeometryGuid,
                    baseVoxelSize = parent.baseVoxelSize > 0f ? parent.baseVoxelSize : profile.BaseVoxelSize,
                    initialVoxelMultiplier = ResolveInitialVoxelMultiplier(null, profile, parent),
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
            int initialVoxelMultiplier = ResolveInitialVoxelMultiplier(manifest, profile, parent);
            if (manifest.baseVoxelSize <= 0f)
                manifest.baseVoxelSize = parent.baseVoxelSize > 0f
                    ? parent.baseVoxelSize
                    : profile.BaseVoxelSize;
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
                targetMultiplier = checked(
                    initialVoxelMultiplier * profile.GetLodMultiplier(targetLodIndex));
                VoxelGrid parentGrid = VoxelVolumeReader.Read(AssetPathToAbsolute(parentVoxAssetPath), parent);
                VoxelGrid reduced = VoxelGridDownsampler.Downsample(parentGrid,
                    manifest.baseVoxelSize * targetMultiplier, profile.Padding, profile.ChunkCellSize,
                    parent.hideInternalCavities);
                Bounds sourceBounds = BoundsFromMetadataOrGrid(parent, parentGrid);
                WriteGrid(targetPath, reduced, sourceBounds, parent.sourceName, parent.sourceAssetPath,
                    manifest.familyId, manifestPath, targetLodIndex, targetMultiplier, mode,
                    parentVoxAssetPath, profile, parent);
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
            manifest.initialVoxelMultiplier = initialVoxelMultiplier;
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

        private static int ResolveInitialVoxelMultiplier(
            VoxelLodSetManifest manifest, VoxelStyleProfile profile,
            VoxelBridgeMetadata metadata)
        {
            if (manifest != null)
                return manifest.initialVoxelMultiplier;

            if (metadata != null && metadata.lodIndex >= 0 &&
                metadata.lodIndex < profile.LodCount)
            {
                int localMultiplier = profile.GetLodMultiplier(metadata.lodIndex);
                if (metadata.lodMultiplier >= localMultiplier &&
                    metadata.lodMultiplier % localMultiplier == 0)
                {
                    int derived = metadata.lodMultiplier / localMultiplier;
                    if (IsPowerOfTwo(derived)) return derived;
                }
            }
            return 1;
        }

        private static bool IsPowerOfTwo(int value) =>
            value >= 1 && (value & (value - 1)) == 0;

        public static string RebuildPrefab(string manifestAssetPath)
        {
            if (!TryReadManifest(manifestAssetPath, out VoxelLodSetManifest manifest))
                throw new InvalidDataException("The LOD manifest is invalid or unsupported. Regenerate the family.");
            if (manifest.productionMeshes)
            {
                VoxelProductionFamily.RebuildAll(manifestAssetPath);
                return manifest.prefabAssetPath;
            }
            foreach (VoxelLodEntry entry in manifest.lods ?? Array.Empty<VoxelLodEntry>())
            {
                if (!VoxelImporterIntegration.ApplyAndReimport(
                        entry.voxAssetPath, out string message, forceReimport: true))
                    Debug.LogWarning($"Voxel Bridge could not resynchronize '{entry.voxAssetPath}': {message}");
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
                if (!TryReadManifest(assetPath, out manifest)) return false;
                manifestAssetPath = assetPath;
                return true;
            }

            if (assetPath.EndsWith(".vox", StringComparison.OrdinalIgnoreCase) &&
                VoxelImporterIntegration.TryLoadMetadata(assetPath,
                    out VoxelBridgeMetadata metadata, out _) &&
                TryReadManifest(metadata.lodSetAssetPath, out manifest))
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
                    !TryReadManifest(candidate, out VoxelLodSetManifest candidateManifest))
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

        internal static void WriteGrid(string voxAssetPath, VoxelGrid grid, Bounds sourceBounds,
            string sourceName, string sourceAssetPath, string familyId, string manifestAssetPath,
            int lodIndex, int lodMultiplier, VoxelLodGenerationMode mode, string parentVoxAssetPath,
            VoxelStyleProfile profile, VoxelBridgeMetadata semanticSource = null,
            VoxelConversionProfile conversionProfile = null, string retainedGeometryGuid = null)
        {
            VoxelSemanticMetadata semantic = null;
            QuantizedVoxels quantized;
            if (grid.IsSemantic)
            {
                if (semanticSource?.semantic == null && conversionProfile == null)
                    throw new InvalidDataException(
                        "The semantic LOD references no source semantic metadata.");
                VoxelColorPalette colorPalette = conversionProfile?.colorMapping.ColorPalette;
                VoxelSurfacePalette surfacePalette = conversionProfile?.surfacePalette;
                if (conversionProfile == null && !VoxelSemanticTransport.TryLoadPalettes(semanticSource.semantic,
                        out colorPalette, out surfacePalette, out string paletteError))
                    throw new InvalidDataException(paletteError);
                quantized = VoxelSemanticQuantizer.Quantize(grid, colorPalette, surfacePalette);
                if (!VoxelSemanticTransport.TryCreateMetadata(
                        quantized.SemanticSlots, colorPalette, surfacePalette,
                        out semantic, out string semanticError))
                    throw new InvalidDataException(semanticError);
                semantic.colorMappingProfileGuid =
                    conversionProfile != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(conversionProfile.colorMapping)) : semanticSource.semantic.colorMappingProfileGuid;
                semantic.colorMappingProfileAssetPath =
                    conversionProfile != null ? AssetDatabase.GetAssetPath(conversionProfile.colorMapping) : semanticSource.semantic.colorMappingProfileAssetPath;
            }
            else
            {
                quantized = VoxelColorQuantizer.Quantize(grid);
            }
            VoxWriteResult writeResult = VoxelChunkedVoxWriter.Write(
                AssetPathToAbsolute(voxAssetPath), grid, quantized, profile.ChunkCellSize);
            CalculateOccupiedBounds(grid, out Vector3Int occupiedMin, out Vector3Int occupiedSize);
            bool normalizedBySceneGraph = writeResult.UsesSceneGraph;
            var metadata = new VoxelBridgeMetadata
            {
                sourceAxes = conversionProfile?.sourceAxes ?? semanticSource?.sourceAxes ?? VoxelSourceAxes.PreserveLocalAxes,
                formatVersion = semantic != null ? 4 : 3,
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
                baseVoxelSize = semanticSource?.baseVoxelSize > 0 ? semanticSource.baseVoxelSize : profile.BaseVoxelSize,
                chunkCellSize = profile.ChunkCellSize,
                sourceBoundsMin = sourceBounds.min,
                sourceBoundsMax = sourceBounds.max,
                importGridOrigin = normalizedBySceneGraph
                    ? grid.Origin + (Vector3)occupiedMin * grid.VoxelSize
                    : grid.Origin,
                importGridSize = normalizedBySceneGraph ? occupiedSize : grid.Size,
                chunks = writeResult.Chunks,
                semantic = semantic,
                retainedGeometryGuid = retainedGeometryGuid ?? semanticSource?.retainedGeometryGuid
            };
            WriteJsonAsset(VoxelImporterIntegration.GetMetadataAssetPath(voxAssetPath), metadata);
        }

        private static void ImportGeneratedVox(IEnumerable<string> voxPaths)
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (string path in voxPaths)
            {
                if (!VoxelImporterIntegration.ApplyAndReimport(path, out string message, forceReimport: true))
                    Debug.LogWarning($"Voxel Bridge could not configure '{path}': {message}");
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
            if (entries.Length == 0) throw new InvalidDataException("The manifest contains no LOD levels.");

            VoxelStyleProfile profile = string.IsNullOrEmpty(manifest.profileAssetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<VoxelStyleProfile>(manifest.profileAssetPath);
            if (profile == null)
                throw new InvalidDataException("The LOD style profile is missing. Assign a valid profile and regenerate the family.");
            if (!profile.TryValidate(out string profileError))
                throw new InvalidDataException(profileError);
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
                        throw new InvalidDataException($"'{entry.voxAssetPath}' did not produce an imported GameObject.");
                    GameObject child = Object.Instantiate(imported);
                    child.name = $"LOD{entry.lodIndex}_x{entry.multiplier}";
                    child.transform.SetParent(root.transform, false);
                    VoxelRetainedGeometry.Attach(child.transform, manifest.retainedGeometryGuid);
                    Renderer[] renderers = child.GetComponentsInChildren<Renderer>(true);
                    if (renderers.Length == 0)
                        throw new InvalidDataException($"LOD {entry.lodIndex} contains no renderers.");
                    float height = profile.GetLodScreenHeight(entry.lodIndex);
                    lods[i] = new LOD(height, renderers);
                }
                var group = root.AddComponent<LODGroup>();
                group.fadeMode = LODFadeMode.None;
                group.SetLODs(lods);
                group.RecalculateBounds();

                manifest.lodGroupSize = group.size;
                for (int i = 0; i < entries.Length; i++)
                {
                    float height = profile.GetLodScreenHeight(
                        entries[i].lodIndex, manifest.lodGroupSize);
                    entries[i].screenRelativeTransitionHeight = height;
                    lods[i].screenRelativeTransitionHeight = height;
                }
                group.SetLODs(lods);
                group.RecalculateBounds();

                if (manifest.impostor != null &&
                    !string.IsNullOrWhiteSpace(manifest.impostor.assetPath) &&
                    !AmplifyImpostorIntegration.TryAppendExistingImpostor(
                        root, group, manifest.impostor,
                        profile.GetLargeModelLodRangeBlend(manifest.lodGroupSize),
                        out string impostorError))
                    Debug.LogWarning($"Voxel Bridge skipped the impostor for '{modelName}': {impostorError}");

                string path = ResolvePrefabAssetPath(manifest, familyFolder, modelName);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                ApplySavedPrefabShadowPolicy(
                    path, profile, entries.Length, manifest.lodGroupSize);
                return path;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void ApplySavedPrefabShadowPolicy(
            string prefabPath, VoxelStyleProfile profile, int voxelLodCount, float modelSize)
        {
            if (profile.GetFirstShadowlessVoxelLodIndex(modelSize, voxelLodCount) < 0)
                return;

            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                LODGroup group = prefabRoot.GetComponent<LODGroup>();
                if (group == null)
                    throw new InvalidDataException("The saved prefab contains no LODGroup.");
                ApplyLodShadowPolicy(profile, group, voxelLodCount, modelSize);
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private static void ApplyLodShadowPolicy(
            VoxelStyleProfile profile, LODGroup group, int voxelLodCount, float modelSize)
        {
            int firstShadowlessLod = profile.GetFirstShadowlessVoxelLodIndex(
                modelSize, voxelLodCount);
            if (firstShadowlessLod < 0) return;

            LOD[] lods = group.GetLODs();
            for (int lodIndex = firstShadowlessLod; lodIndex < lods.Length; lodIndex++)
            {
                foreach (Renderer renderer in lods[lodIndex].renderers ?? Array.Empty<Renderer>())
                {
                    if (renderer != null)
                    {
                        renderer.shadowCastingMode = ShadowCastingMode.Off;
                        if (PrefabUtility.IsPartOfPrefabInstance(renderer))
                            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    }
                }
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
            if (min.x == int.MaxValue) throw new InvalidOperationException("The grid contains no voxels.");
            size = max - min + Vector3Int.one;
        }

        private static void ValidateProfileAndOptions(VoxelStyleProfile profile, VoxelLodBuildOptions options)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (!profile.TryValidate(out string error)) throw new InvalidOperationException(error);
            if (!AssetDatabase.Contains(profile) ||
                string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(profile)))
                throw new InvalidOperationException(
                    "Save the LOD style profile as an asset before generating a family. " +
                    "The manifest requires a persistent profile to rebuild its prefab.");
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!IsAssetFolder(options.ExportFolder))
                throw new ArgumentException("The output folder must be inside Assets.");
        }

        internal static bool TryReadManifest(string assetPath, out VoxelLodSetManifest manifest)
        {
            manifest = null;
            if (string.IsNullOrWhiteSpace(assetPath)) return false;
            string absolute = AssetPathToAbsolute(assetPath);
            if (!File.Exists(absolute)) return false;
            VoxelLodSetManifest candidate;
            try
            {
                candidate = JsonUtility.FromJson<VoxelLodSetManifest>(File.ReadAllText(absolute));
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (candidate == null || candidate.formatVersion != 4 ||
                !Enum.IsDefined(typeof(VoxelSourceAxes), candidate.sourceAxes) ||
                !IsPowerOfTwo(candidate.initialVoxelMultiplier))
                return false;
            manifest = candidate;
            return true;
        }

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
            if (!IsAssetFolder(path)) throw new ArgumentException("The folder must be inside Assets.");
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
                                 ?? throw new InvalidOperationException("The project root could not be found.");
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
                return NormalizeAssetPath(manifest.prefabAssetPath);

            return AssetDatabase.GenerateUniqueAssetPath(desiredPath);
        }

        internal static string MakeSafeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(value) ? "VoxelModel" : value;
        }
    }
}
