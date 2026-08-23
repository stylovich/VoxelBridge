using UnityEngine;

namespace DynamicGI.Radiance
{
    public readonly struct RadianceClipmapProbeResult
    {
        public readonly int CascadeIndex;
        public readonly RadianceProbeResult Probe;

        public RadianceClipmapProbeResult(int cascadeIndex, RadianceProbeResult probe)
        {
            CascadeIndex = cascadeIndex;
            Probe = probe;
        }
    }

    public readonly struct RadianceClipmapStats
    {
        public readonly int CascadeCount;
        public readonly int ActiveProbes;
        public readonly int DirtyTiles;
        public readonly int UpdatedTilesThisFrame;
        public readonly int UpdatedProbesThisFrame;
        public readonly int ExposedProbesThisFrame;
        public readonly int RecycledProbesThisFrame;
        public readonly int OriginMovesThisFrame;
        public readonly int ComputeDispatchesThisFrame;
        public readonly long EstimatedGpuBytes;
        public readonly double UpdateCpuMilliseconds;

        public RadianceClipmapStats(
            int cascadeCount,
            int activeProbes,
            int dirtyTiles,
            int updatedTilesThisFrame,
            int updatedProbesThisFrame,
            int exposedProbesThisFrame,
            int recycledProbesThisFrame,
            int originMovesThisFrame,
            int computeDispatchesThisFrame,
            long estimatedGpuBytes,
            double updateCpuMilliseconds)
        {
            CascadeCount = cascadeCount;
            ActiveProbes = activeProbes;
            DirtyTiles = dirtyTiles;
            UpdatedTilesThisFrame = updatedTilesThisFrame;
            UpdatedProbesThisFrame = updatedProbesThisFrame;
            ExposedProbesThisFrame = exposedProbesThisFrame;
            RecycledProbesThisFrame = recycledProbesThisFrame;
            OriginMovesThisFrame = originMovesThisFrame;
            ComputeDispatchesThisFrame = computeDispatchesThisFrame;
            EstimatedGpuBytes = estimatedGpuBytes;
            UpdateCpuMilliseconds = updateCpuMilliseconds;
        }
    }
}
