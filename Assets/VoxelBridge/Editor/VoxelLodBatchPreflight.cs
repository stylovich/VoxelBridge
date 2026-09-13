using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal enum VoxelLodBatchMemoryRisk
    {
        Normal,
        Elevated,
        OverBudget,
        Invalid
    }

    internal sealed class VoxelLodBatchSourceEstimate
    {
        public readonly Object ReuseKey;
        public readonly string SourceName;
        public readonly VoxelGridPlan[] LodPlans;
        public readonly long EstimatedPeakBytes;
        public readonly long TotalDenseCells;
        public readonly long MemoryBudgetBytes;
        public readonly int InitialLodIndex;
        public readonly int InitialVoxelMultiplier;
        public readonly string Error;

        public bool IsValid => string.IsNullOrEmpty(Error);
        public bool IsOverBudget => IsValid && MemoryBudgetBytes > 0 &&
                                    EstimatedPeakBytes > MemoryBudgetBytes;
        public bool WasAdapted => InitialVoxelMultiplier > 1;
        public VoxelLodBatchMemoryRisk Risk => !IsValid
            ? VoxelLodBatchMemoryRisk.Invalid
            : IsOverBudget
                ? VoxelLodBatchMemoryRisk.OverBudget
                : EstimatedPeakBytes >= VoxelLodBatchAnalyzer.ElevatedMemoryBytes
                    ? VoxelLodBatchMemoryRisk.Elevated
                    : VoxelLodBatchMemoryRisk.Normal;

        public VoxelLodBatchSourceEstimate(
            Object reuseKey, string sourceName,
            IEnumerable<VoxelGridPlan> lodPlans, long estimatedPeakBytes,
            long totalDenseCells, long memoryBudgetBytes, int initialLodIndex,
            int initialVoxelMultiplier, string error)
        {
            ReuseKey = reuseKey;
            SourceName = sourceName;
            LodPlans = lodPlans?.ToArray() ?? Array.Empty<VoxelGridPlan>();
            EstimatedPeakBytes = estimatedPeakBytes;
            TotalDenseCells = totalDenseCells;
            MemoryBudgetBytes = memoryBudgetBytes;
            InitialLodIndex = Mathf.Max(0, initialLodIndex);
            InitialVoxelMultiplier = Mathf.Max(1, initialVoxelMultiplier);
            Error = error;
        }
    }

    internal sealed class VoxelLodBatchPreflight
    {
        public readonly string Signature;
        public readonly VoxelLodBatchSourceEstimate[] Sources;
        public readonly int CandidateCount;
        public readonly int IgnoredCount;

        public int UniqueConversionCount => Sources.Length;
        public int ReusedCount => Mathf.Max(0, CandidateCount - IgnoredCount - Sources.Length);
        public int InvalidCount => Sources.Count(source => !source.IsValid);
        public int OverBudgetCount => Sources.Count(source => source.IsOverBudget);
        public int AdaptedCount => Sources.Count(source => source.IsValid && source.WasAdapted);
        public long EstimatedPeakBytes => Sources.Length == 0
            ? 0
            : Sources.Max(source => source.EstimatedPeakBytes);
        public long TotalDenseCells => Sources.Sum(source => source.TotalDenseCells);

        public VoxelLodBatchPreflight(
            string signature, IEnumerable<VoxelLodBatchSourceEstimate> sources,
            int candidateCount, int ignoredCount)
        {
            Signature = signature;
            Sources = sources.ToArray();
            CandidateCount = candidateCount;
            IgnoredCount = ignoredCount;
        }

        public VoxelLodBatchSourceEstimate Find(Object reuseKey) =>
            Sources.FirstOrDefault(source => source.ReuseKey == reuseKey);
    }

    internal static class VoxelLodBatchAnalyzer
    {
        internal const long Mebibyte = 1024L * 1024L;
        internal const long ElevatedMemoryBytes = 512L * Mebibyte;
        private const long FixedWorkingSetBytes = 64L * Mebibyte;
        private const int DenseBytesPerCellWithoutFill = 11;
        private const int DenseBytesPerCellWithFill = 15;

        public static VoxelLodBatchPreflight Analyze(
            VoxelLodBatchSourcePlan[] plans, VoxelStyleProfile profile,
            VoxelLodBuildOptions lodOptions, VoxelLodBatchOptions batchOptions,
            Func<float, string, bool> cancelProgress = null)
        {
            if (plans == null) throw new ArgumentNullException(nameof(plans));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (lodOptions == null) throw new ArgumentNullException(nameof(lodOptions));
            batchOptions ??= new VoxelLodBatchOptions();

            VoxelLodBatchSourcePlan[] uniquePlans = plans
                .Where(plan => !plan.Ignored)
                .GroupBy(plan => plan.ReuseKey)
                .Select(group => group.First())
                .ToArray();
            var estimates = new List<VoxelLodBatchSourceEstimate>(uniquePlans.Length);
            for (int index = 0; index < uniquePlans.Length; index++)
            {
                VoxelLodBatchSourcePlan plan = uniquePlans[index];
                if (cancelProgress != null && cancelProgress(
                        uniquePlans.Length == 0 ? 1f : (float)index / uniquePlans.Length,
                        $"Analyzing {index + 1} of {uniquePlans.Length}: {plan.Source.name}"))
                    throw new OperationCanceledException("Batch analysis cancelled.");

                estimates.Add(Estimate(plan, profile, lodOptions, batchOptions));
            }

            cancelProgress?.Invoke(1f, "Memory analysis complete");
            return new VoxelLodBatchPreflight(
                CreateSignature(plans, profile, lodOptions, batchOptions), estimates,
                plans.Length, plans.Count(plan => plan.Ignored));
        }

        public static VoxelLodBatchSourceEstimate AnalyzeSingle(
            Object source, VoxelStyleProfile profile, VoxelLodBuildOptions lodOptions,
            VoxelLodBatchOptions batchOptions)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (lodOptions == null) throw new ArgumentNullException(nameof(lodOptions));
            batchOptions ??= new VoxelLodBatchOptions();
            return Estimate(source, source, source.name, profile, lodOptions, batchOptions);
        }

        public static string CreateSignature(
            VoxelLodBatchSourcePlan[] plans, VoxelStyleProfile profile,
            VoxelLodBuildOptions lodOptions, VoxelLodBatchOptions batchOptions)
        {
            plans ??= Array.Empty<VoxelLodBatchSourcePlan>();
            batchOptions ??= new VoxelLodBatchOptions();
            string sources = string.Join("|", plans.Select(plan =>
                $"{plan.Source.GetInstanceID()}:{plan.ConversionSource?.GetInstanceID() ?? 0}:" +
                $"{plan.ReuseKey?.GetInstanceID() ?? 0}:{plan.Ignored}:{VoxelConversionProfile.RuleFingerprint(plan.ConversionSource)}:" +
                (lodOptions?.NormalizeScale == true ? plan.Source.transform.localToWorldMatrix.ToString("R") : "")));
            string profileValues = profile == null
                ? "none"
                : $"{profile.GetInstanceID()}:{profile.BaseVoxelSize:R}:{profile.Padding}:" +
                  $"{profile.ChunkCellSize}:{profile.FillInterior}:" +
                  string.Join(",", Enumerable.Range(0, profile.LodCount)
                      .Select(profile.GetLodMultiplier));
            bool includeInactiveObjects = !batchOptions.IgnoreInactiveObjects;
            string options = lodOptions == null
                ? "none"
                : $"{lodOptions.ColorMode}:{lodOptions.AlphaCutoff:R}:{lodOptions.ExportFolder}:" +
                  $"{includeInactiveObjects}:{lodOptions.NormalizeScale}:{lodOptions.GenerateLod0Only}:{VoxelConversionProfile.Fingerprint(lodOptions.ConversionProfile)}";
            return Hash128.Compute(
                $"{sources}#{profileValues}#{options}#{batchOptions.MaximumEstimatedMemoryBytes}:" +
                $"{batchOptions.SkipSourcesOverMemoryBudget}:{batchOptions.AdaptInitialVoxelSize}:" +
                $"{batchOptions.MaximumInitialLodIndex}:{batchOptions.MaximumImportedVoxelCount}:" +
                $"{batchOptions.IgnoreInactiveObjects}")
                .ToString();
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024L) return $"{bytes} B";
            if (bytes < Mebibyte) return $"{bytes / 1024d:0.0} KiB";
            long gibibyte = 1024L * Mebibyte;
            return bytes < gibibyte
                ? $"{bytes / (double)Mebibyte:0.0} MiB"
                : $"{bytes / (double)gibibyte:0.00} GiB";
        }

        private static VoxelLodBatchSourceEstimate Estimate(
            VoxelLodBatchSourcePlan plan, VoxelStyleProfile profile,
            VoxelLodBuildOptions lodOptions, VoxelLodBatchOptions batchOptions)
        {
            return Estimate(
                plan.ReuseKey, plan.ConversionSource, plan.Source.name,
                profile, lodOptions.ForScaleSource(plan.Source), batchOptions);
        }

        private static VoxelLodBatchSourceEstimate Estimate(
            Object reuseKey, Object conversionSource, string sourceName,
            VoxelStyleProfile profile, VoxelLodBuildOptions lodOptions,
            VoxelLodBatchOptions batchOptions)
        {
            Bounds bounds;
            long sourceOverhead;
            try
            {
                bool includeInactiveObjects = !batchOptions.IgnoreInactiveObjects;
                bounds = MeshVoxelizer.GetSourceBounds(
                    conversionSource, includeInactiveObjects, lodOptions.ConversionProfile, lodOptions.ResolveScale(conversionSource));
                sourceOverhead = EstimateSourceOverhead(
                    conversionSource, lodOptions.ColorMode, includeInactiveObjects, lodOptions.ConversionProfile);
            }
            catch (Exception exception)
            {
                return new VoxelLodBatchSourceEstimate(
                    reuseKey, sourceName,
                    Array.Empty<VoxelGridPlan>(), 0, 0,
                    batchOptions.MaximumEstimatedMemoryBytes, 0, 1, exception.Message);
            }

            int maximumInitialLod = batchOptions.AdaptInitialVoxelSize
                ? Mathf.Clamp(batchOptions.MaximumInitialLodIndex, 0, profile.LodCount - 1)
                : 0;
            VoxelLodBatchSourceEstimate lastValid = null;
            string lastError = null;
            int lastInitialLod = 0;
            int lastInitialMultiplier = 1;
            for (int initialLodIndex = 0; initialLodIndex <= maximumInitialLod; initialLodIndex++)
            {
                int initialMultiplier = profile.GetLodMultiplier(initialLodIndex);
                lastInitialLod = initialLodIndex;
                lastInitialMultiplier = initialMultiplier;
                try
                {
                    VoxelLodBatchSourceEstimate estimate = EstimateAtMultiplier(
                        reuseKey, sourceName, profile,
                        batchOptions, bounds, sourceOverhead,
                        initialLodIndex, initialMultiplier, lodOptions.GenerateLod0Only, lodOptions.ConversionProfile != null,
                        lodOptions.ConversionProfile?.assignSurfacesFromPbr == true);
                    lastValid = estimate;
                    if (!estimate.IsOverBudget) return estimate;
                }
                catch (Exception exception)
                {
                    lastError = exception.Message;
                }
            }

            if (lastValid != null) return lastValid;
            string suffix = batchOptions.AdaptInitialVoxelSize && maximumInitialLod > 0
                ? $" No valid grid was found up to LOD{maximumInitialLod} " +
                  $"(×{lastInitialMultiplier})."
                : string.Empty;
            return new VoxelLodBatchSourceEstimate(
                reuseKey, sourceName,
                Array.Empty<VoxelGridPlan>(), 0, 0,
                batchOptions.MaximumEstimatedMemoryBytes, lastInitialLod,
                lastInitialMultiplier, (lastError ?? "The source could not be analyzed.") + suffix);
        }

        private static VoxelLodBatchSourceEstimate EstimateAtMultiplier(
            Object reuseKey, string sourceName,
            VoxelStyleProfile profile,
            VoxelLodBatchOptions batchOptions,
            Bounds bounds, long sourceOverhead, int initialLodIndex,
            int initialMultiplier, bool lod0Only, bool productionMeshes, bool pbrMapping)
        {
            var lodPlans = new List<VoxelGridPlan>(profile.LodCount);
            long peakBytes = 0;
            long totalCells = 0;
            int bytesPerCell = profile.FillInterior
                ? DenseBytesPerCellWithFill
                : DenseBytesPerCellWithoutFill;
            if (pbrMapping) { bytesPerCell++; sourceOverhead += 4L * 1024 * 1024; } // Decision buffer and bounded PBR match cache.
            for (int lodIndex = 0; lodIndex < (lod0Only ? 1 : profile.LodCount); lodIndex++)
            {
                int effectiveMultiplier = checked(
                    initialMultiplier * profile.GetLodMultiplier(lodIndex));
                float voxelSize = profile.BaseVoxelSize * effectiveMultiplier;
                VoxelGridPlan gridPlan = VoxelGridPlanner.Create(
                    bounds, voxelSize, profile.Padding, profile.ChunkCellSize);
                if (productionMeshes) VoxelSemanticMesher.ValidateGrid(gridPlan.Size, gridPlan.Origin, gridPlan.VoxelSize);
                lodPlans.Add(gridPlan);
                totalCells += gridPlan.CellCount;
                long lodBytes = checked(gridPlan.CellCount * bytesPerCell +
                                        sourceOverhead + FixedWorkingSetBytes);
                peakBytes = Math.Max(peakBytes, lodBytes);
            }

            return new VoxelLodBatchSourceEstimate(
                reuseKey, sourceName, lodPlans,
                peakBytes, totalCells, batchOptions.MaximumEstimatedMemoryBytes,
                initialLodIndex, initialMultiplier, null);
        }

        private static long EstimateSourceOverhead(
            Object source, VoxelColorMode colorMode, bool includeInactiveObjects, VoxelConversionProfile profile)
        {
            var meshes = new HashSet<Mesh>();
            var materials = new HashSet<Material>();
            long retainedBytes = 0;
            if (source is Mesh mesh)
            {
                meshes.Add(mesh);
            }
            else if (source is GameObject root)
            {
                foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null ||
                        (!includeInactiveObjects &&
                         !MeshVoxelizer.IsActiveWithinRoot(root.transform, filter.transform)))
                        continue;
                    MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                    AddSource(root.transform, filter.transform, filter.sharedMesh, renderer != null ? renderer.sharedMaterials : null);
                }
                foreach (SkinnedMeshRenderer renderer in
                         root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh == null ||
                        (!includeInactiveObjects &&
                         !MeshVoxelizer.IsActiveWithinRoot(root.transform, renderer.transform)))
                        continue;
                    AddSource(root.transform, renderer.transform, renderer.sharedMesh, renderer.sharedMaterials);
                }
            }

            void AddSource(Transform root, Transform current, Mesh sourceMesh, Material[] sourceMaterials)
            {
                for (int i = 0; i < sourceMesh.subMeshCount; i++)
                {
                    Material material = sourceMaterials != null && i < sourceMaterials.Length ? sourceMaterials[i] : null;
                    var rule = VoxelResolvedRule.Resolve(root, current, material, profile);
                    if (rule.Action == VoxelConversionAction.Ignore) continue;
                    meshes.Add(sourceMesh);
                    if (rule.Action == VoxelConversionAction.KeepOriginal)
                        retainedBytes = checked(retainedBytes + sourceMesh.vertexCount * 64L + (long)sourceMesh.GetIndexCount(i) * 8L);
                    if (rule.Action == VoxelConversionAction.Voxelize && material != null) materials.Add(material);
                }
            }

            long bytes = retainedBytes;
            foreach (Mesh sourceMesh in meshes)
            {
                bytes += sourceMesh.vertexCount * 32L;
                bytes += (long)sourceMesh.GetIndexCount(0) * sizeof(int);
                for (int submesh = 1; submesh < sourceMesh.subMeshCount; submesh++)
                    bytes += (long)sourceMesh.GetIndexCount(submesh) * sizeof(int);
            }

            foreach (Material material in materials)
            {
                if (colorMode == VoxelColorMode.MaterialAndTexture) AddTexture(material.mainTexture);
                if (profile != null && profile.detectEmission && (colorMode != VoxelColorMode.SingleColor || profile.assignSurfacesFromPbr))
                {
                    if (material.HasProperty("_EmissiveColorMap")) AddTexture(material.GetTexture("_EmissiveColorMap"));
                    else if (material.HasProperty("_EmissionMap")) AddTexture(material.GetTexture("_EmissionMap"));
                }
                if (profile?.assignSurfacesFromPbr == true && material.HasProperty("_MaskMap")) AddTexture(material.GetTexture("_MaskMap"));
            }
            void AddTexture(Texture texture)
            {
                if (texture != null) bytes += (long)Mathf.Min(512, Mathf.Max(1, texture.width)) * Mathf.Min(512, Mathf.Max(1, texture.height)) * 12L;
            }
            return bytes;
        }
    }
}
