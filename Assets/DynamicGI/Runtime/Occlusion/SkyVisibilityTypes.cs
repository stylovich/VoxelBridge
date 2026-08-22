using UnityEngine;

namespace DynamicGI.Occlusion
{
    public readonly struct SkyVisibilityResult
    {
        public readonly Vector3 WorldPosition;
        public readonly float Visibility;
        public readonly bool HasError;

        public SkyVisibilityResult(Vector3 worldPosition, float visibility, bool hasError)
        {
            WorldPosition = worldPosition;
            Visibility = visibility;
            HasError = hasError;
        }
    }

    public readonly struct SkyVisibilityStats
    {
        public readonly Vector3Int Resolution;
        public readonly int TotalTiles;
        public readonly int DirtyTiles;
        public readonly int UpdatedTilesThisFrame;
        public readonly int UpdatedSamplesThisFrame;
        public readonly int ComputeDispatchesThisFrame;
        public readonly long EstimatedGpuBytes;
        public readonly double UpdateCpuMilliseconds;

        public SkyVisibilityStats(
            Vector3Int resolution,
            int totalTiles,
            int dirtyTiles,
            int updatedTilesThisFrame,
            int updatedSamplesThisFrame,
            int computeDispatchesThisFrame,
            long estimatedGpuBytes,
            double updateCpuMilliseconds)
        {
            Resolution = resolution;
            TotalTiles = totalTiles;
            DirtyTiles = dirtyTiles;
            UpdatedTilesThisFrame = updatedTilesThisFrame;
            UpdatedSamplesThisFrame = updatedSamplesThisFrame;
            ComputeDispatchesThisFrame = computeDispatchesThisFrame;
            EstimatedGpuBytes = estimatedGpuBytes;
            UpdateCpuMilliseconds = updateCpuMilliseconds;
        }
    }
}
