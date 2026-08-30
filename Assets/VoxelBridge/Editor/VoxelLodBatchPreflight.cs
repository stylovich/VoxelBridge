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
        public readonly Object ConversionSource;
        public readonly string SourceName;
        public readonly VoxelGridPlan[] LodPlans;
        public readonly long EstimatedPeakBytes;
        public readonly long TotalDenseCells;
        public readonly long MemoryBudgetBytes;
        public readonly string Error;

        public bool IsValid => string.IsNullOrEmpty(Error);
        public bool IsOverBudget => IsValid && MemoryBudgetBytes > 0 &&
                                    EstimatedPeakBytes > MemoryBudgetBytes;
        public VoxelLodBatchMemoryRisk Risk => !IsValid
            ? VoxelLodBatchMemoryRisk.Invalid
            : IsOverBudget
                ? VoxelLodBatchMemoryRisk.OverBudget
                : EstimatedPeakBytes >= VoxelLodBatchAnalyzer.ElevatedMemoryBytes
                    ? VoxelLodBatchMemoryRisk.Elevated
                    : VoxelLodBatchMemoryRisk.Normal;

        public VoxelLodBatchSourceEstimate(
            Object reuseKey, Object conversionSource, string sourceName,
            IEnumerable<VoxelGridPlan> lodPlans, long estimatedPeakBytes,
            long totalDenseCells, long memoryBudgetBytes, string error)
        {
            ReuseKey = reuseKey;
            ConversionSource = conversionSource;
            SourceName = sourceName;
            LodPlans = lodPlans?.ToArray() ?? Array.Empty<VoxelGridPlan>();
            EstimatedPeakBytes = estimatedPeakBytes;
            TotalDenseCells = totalDenseCells;
            MemoryBudgetBytes = memoryBudgetBytes;
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
        public int ElevatedCount => Sources.Count(source =>
            source.Risk == VoxelLodBatchMemoryRisk.Elevated);
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
                        $"Analizando {index + 1} de {uniquePlans.Length}: {plan.Source.name}"))
                    throw new OperationCanceledException("Análisis del lote cancelado.");

                estimates.Add(Estimate(plan, profile, lodOptions, batchOptions));
            }

            cancelProgress?.Invoke(1f, "Análisis de memoria terminado");
            return new VoxelLodBatchPreflight(
                CreateSignature(plans, profile, lodOptions, batchOptions), estimates,
                plans.Length, plans.Count(plan => plan.Ignored));
        }

        public static string CreateSignature(
            VoxelLodBatchSourcePlan[] plans, VoxelStyleProfile profile,
            VoxelLodBuildOptions lodOptions, VoxelLodBatchOptions batchOptions)
        {
            plans ??= Array.Empty<VoxelLodBatchSourcePlan>();
            batchOptions ??= new VoxelLodBatchOptions();
            string sources = string.Join("|", plans.Select(plan =>
                $"{plan.Source.GetInstanceID()}:{plan.ConversionSource?.GetInstanceID() ?? 0}:" +
                $"{plan.ReuseKey?.GetInstanceID() ?? 0}:{plan.Ignored}"));
            string profileValues = profile == null
                ? "none"
                : $"{profile.GetInstanceID()}:{profile.BaseVoxelSize:R}:{profile.Padding}:" +
                  $"{profile.ChunkCellSize}:{profile.FillInterior}:" +
                  string.Join(",", Enumerable.Range(0, profile.LodCount)
                      .Select(profile.GetLodMultiplier));
            string options = lodOptions == null
                ? "none"
                : $"{lodOptions.ColorMode}:{lodOptions.AlphaCutoff:R}:{lodOptions.ExportFolder}";
            return Hash128.Compute(
                $"{sources}#{profileValues}#{options}#{batchOptions.MaximumEstimatedMemoryBytes}")
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
            try
            {
                Bounds bounds = MeshVoxelizer.GetSourceBounds(plan.ConversionSource);
                var lodPlans = new List<VoxelGridPlan>(profile.LodCount);
                long peakBytes = 0;
                long totalCells = 0;
                long sourceOverhead = EstimateSourceOverhead(plan.ConversionSource, lodOptions);
                int bytesPerCell = profile.FillInterior
                    ? DenseBytesPerCellWithFill
                    : DenseBytesPerCellWithoutFill;
                for (int lodIndex = 0; lodIndex < profile.LodCount; lodIndex++)
                {
                    float voxelSize = profile.BaseVoxelSize * profile.GetLodMultiplier(lodIndex);
                    VoxelGridPlan gridPlan = VoxelGridPlanner.Create(
                        bounds, voxelSize, profile.Padding, profile.ChunkCellSize);
                    lodPlans.Add(gridPlan);
                    totalCells += gridPlan.CellCount;
                    long lodBytes = checked(gridPlan.CellCount * bytesPerCell +
                                            sourceOverhead + FixedWorkingSetBytes);
                    peakBytes = Math.Max(peakBytes, lodBytes);
                }

                return new VoxelLodBatchSourceEstimate(
                    plan.ReuseKey, plan.ConversionSource, plan.Source.name, lodPlans,
                    peakBytes, totalCells, batchOptions.MaximumEstimatedMemoryBytes, null);
            }
            catch (Exception exception)
            {
                return new VoxelLodBatchSourceEstimate(
                    plan.ReuseKey, plan.ConversionSource, plan.Source.name,
                    Array.Empty<VoxelGridPlan>(), 0, 0,
                    batchOptions.MaximumEstimatedMemoryBytes, exception.Message);
            }
        }

        private static long EstimateSourceOverhead(Object source, VoxelLodBuildOptions options)
        {
            var meshes = new HashSet<Mesh>();
            var materials = new HashSet<Material>();
            if (source is Mesh mesh)
            {
                meshes.Add(mesh);
            }
            else if (source is GameObject root)
            {
                foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh != null) meshes.Add(filter.sharedMesh);
                    MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer == null) continue;
                    foreach (Material material in renderer.sharedMaterials)
                        if (material != null) materials.Add(material);
                }
                foreach (SkinnedMeshRenderer renderer in
                         root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh != null) meshes.Add(renderer.sharedMesh);
                    foreach (Material material in renderer.sharedMaterials)
                        if (material != null) materials.Add(material);
                }
            }

            long bytes = 0;
            foreach (Mesh sourceMesh in meshes)
            {
                bytes += sourceMesh.vertexCount * 32L;
                bytes += (long)sourceMesh.GetIndexCount(0) * sizeof(int);
                for (int submesh = 1; submesh < sourceMesh.subMeshCount; submesh++)
                    bytes += (long)sourceMesh.GetIndexCount(submesh) * sizeof(int);
            }

            if (options.ColorMode != VoxelColorMode.MaterialAndTexture) return bytes;
            foreach (Material material in materials)
            {
                Texture texture = material.mainTexture;
                if (texture == null) continue;
                int width = Mathf.Min(512, Mathf.Max(1, texture.width));
                int height = Mathf.Min(512, Mathf.Max(1, texture.height));
                bytes += (long)width * height * 12L;
            }
            return bytes;
        }
    }
}
